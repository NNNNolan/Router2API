using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class ResourceSelectionPerformanceTests
{
    private static readonly string[] SkipCooldownIds = ["active", "cooling", "disabled-until"];

    [TestMethod]
    public async Task AccountCandidateQueryFiltersAvailabilityInSqlWithoutManagementCount()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-data", $"selection-{Guid.NewGuid():N}.db");
        var sql = new SqlCaptureLogger();
        var database = new SqlSugarDatabase(Options.Create(new DatabaseOptions { Path = path }), sql);
        LogDatabaseTestHelper.Initializer(database).Initialize();
        var accounts = new AccountService(database, Mock.Of<ISharedKeyValueStore>());
        var now = DateTimeOffset.UtcNow;
        await accounts.SaveAsync(Account("active"));
        await accounts.SaveAsync(Account("cooling", new ResourceStatus { State = ResourceState.Active, CooldownUntil = now.AddMinutes(5) }));
        await accounts.SaveAsync(Account("disabled-until", new ResourceStatus { State = ResourceState.Active, DisabledUntil = now.AddMinutes(5) }));
        await accounts.SaveAsync(Account("disabled", new ResourceStatus { State = ResourceState.Disabled }));

        var before = sql.Statements.Count;
        var available = await accounts.QueryCandidatesAsync("plugin", "platform", false, now, 1, 100, CancellationToken.None);
        var query = string.Join('\n', sql.Statements.Skip(before));

        available.Select(account => account.Id).Should().Equal("active");
        query.Should().Contain("CooldownUntilUtc");
        query.Should().Contain("DisabledUntilUtc");
        query.Should().Contain("State");
        query.Should().NotContain("COUNT(", because: "runtime selection must not compute management page totals");

        var skipCooldown = await accounts.QueryCandidatesAsync("plugin", "platform", true, now, null, 0, CancellationToken.None);
        skipCooldown.Select(account => account.Id).Should().BeEquivalentTo(SkipCooldownIds);
    }

    [TestMethod]
    public async Task RedisCooldownBatchesUsePrefixedKeysAndValidateEveryPolicyHash()
    {
        await using var server = new RedisPipeServer();
        using var redis = server.CreateStore();

        var cooldownKeys = Enumerable.Range(0, 65)
            .Select(index => $"account:cooldown:plugin:{(index == 0 ? "cooling" : "free")}-{index}")
            .ToArray();
        var expiries = await redis.GetExpiryBatchAsync(
            cooldownKeys, CancellationToken.None);
        var store = new RedisProxyPolicyStore(redis);
        var proxies = Enumerable.Range(0, 65)
            .Select(index => new ProxyEndpoint { Id = $"proxy-{index}", ConfigurationVersion = index })
            .ToArray();
        var states = await store.GetManyAsync("plugin", proxies, CancellationToken.None);

        expiries[0].Should().NotBeNull();
        expiries.Skip(1).Should().OnlyContain(expiry => expiry == null);
        states.Should().OnlyContain(state => state != null && state.State == ProxyPolicyState.Cooldown);
        server.Commands.Count(command => command[0] == "PTTL").Should().Be(65);
        server.Commands.Count(command => command[0] == "HGETALL").Should().Be(65);
        server.Keys.Should().Contain(key => key.StartsWith("batch-test:account:cooldown:", StringComparison.Ordinal));
        server.Keys.Should().Contain(key => key.StartsWith("batch-test:proxy-policy:plugin:", StringComparison.Ordinal));

        Func<Task> invalidHash = async () => await store.GetManyAsync("plugin",
            [new ProxyEndpoint { Id = "invalid-hash", ConfigurationVersion = 1 }], CancellationToken.None);
        await invalidHash.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task PreCancelledRedisBatchReadsCancelBeforeRequiringRedis()
    {
        using var redis = new RedisKeyValueStore(Options.Create(new RedisOptions { ConnectionString = "disabled" }));
        var store = new RedisProxyPolicyStore(redis);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> accountRead = async () => await redis.GetExpiryBatchAsync([], cancellation.Token);
        Func<Task> proxyRead = async () => await store.GetManyAsync("plugin", [], cancellation.Token);

        await accountRead.Should().ThrowAsync<OperationCanceledException>();
        await proxyRead.Should().ThrowAsync<OperationCanceledException>();
    }

    private static Account Account(string id, ResourceStatus? status = null) => new()
    {
        Id = id,
        PluginKey = "plugin",
        Platform = "platform",
        Credential = new ApiKeyCredential("secret"),
        Status = status ?? new ResourceStatus()
    };

    private sealed class RedisPipeServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        private readonly Task accept;
        private readonly ConcurrentQueue<string[]> commands = new();
        private readonly ConcurrentQueue<string> keys = new();
        private readonly List<Task> connections = [];

        public ConcurrentQueue<string[]> Commands => commands;
        public ConcurrentQueue<string> Keys => keys;

        public RedisPipeServer()
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            accept = AcceptAsync();
        }

        private int Port { get; }

        public RedisKeyValueStore CreateStore() => new(Options.Create(new RedisOptions
        {
            ConnectionString = $"127.0.0.1:{Port},poolsize=1,preheat=0,connectTimeout=1000,syncTimeout=1000",
            InstanceName = "batch-test:"
        }));

        private async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                    connections.Add(ServeAsync(await listener.AcceptTcpClientAsync(stop.Token)));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                await using var stream = client.GetStream();
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var countLine = await ReadLineAsync(stream, stop.Token);
                        if (countLine is null) return;
                        var count = int.Parse(countLine.AsSpan(1), CultureInfo.InvariantCulture);
                        var command = new string[count];
                        for (var index = 0; index < count; index++)
                        {
                            var lengthLine = await ReadLineAsync(stream, stop.Token) ?? throw new EndOfStreamException();
                            var length = int.Parse(lengthLine.AsSpan(1), CultureInfo.InvariantCulture);
                            var bytes = new byte[length + 2];
                            await stream.ReadExactlyAsync(bytes, stop.Token);
                            command[index] = Encoding.UTF8.GetString(bytes, 0, length);
                        }
                        command[0] = command[0].ToUpperInvariant();
                        commands.Enqueue(command);
                        if (command.Length > 1) keys.Enqueue(command[1]);
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(Reply(command)), stop.Token);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }

        private static string Reply(string[] command) => command[0] switch
        {
            "PING" => "+PONG\r\n",
            "SELECT" or "READONLY" => "+OK\r\n",
            "CLUSTER" => "-ERR This instance has cluster support disabled\r\n",
            "PTTL" => command[1].Contains(":cooling-", StringComparison.Ordinal) ? ":5000\r\n" : ":-2\r\n",
            "HGETALL" => HashReply(command[1]),
            _ => throw new InvalidOperationException($"Unexpected Redis command {command[0]}.")
        };

        private static string HashReply(string key)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var values = new Dictionary<string, string>
            {
                ["state"] = "Cooldown",
                ["cooldownUntil"] = (now + 60_000).ToString(CultureInfo.InvariantCulture),
                ["failureCount"] = "1",
                ["lastFailureAt"] = now.ToString(CultureInfo.InvariantCulture),
                ["reason"] = "test",
                ["lastStatusCode"] = "429"
            };
            if (key.Contains("invalid-hash", StringComparison.Ordinal))
                values.Remove("failureCount");
            var response = new StringBuilder($"*{values.Count * 2}\r\n");
            foreach (var (field, value) in values)
            {
                AppendBulk(response, field);
                AppendBulk(response, value);
            }
            return response.ToString();
        }

        private static void AppendBulk(StringBuilder response, string value)
            => response.Append('$').Append(Encoding.UTF8.GetByteCount(value)).Append("\r\n")
                .Append(value).Append("\r\n");

        private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken token)
        {
            var line = new StringBuilder();
            var buffer = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(buffer, token) == 0) return null;
                if (buffer[0] == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)buffer[0]);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            await accept;
            await Task.WhenAll(connections);
            stop.Dispose();
        }
    }
}
