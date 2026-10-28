using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class SqlSugarRedisCacheTests
{
    private const string CacheKey = "subscriptions";
    private const string PhysicalKey = "cache-test:sqlsugar:v1:subscriptions";
    private static readonly string[] CachedValues = ["cached"];
    private static readonly string[] DatabaseValues = ["database"];

    [TestMethod]
    public async Task CachedCollectionIsReturnedWithoutRebuilding()
    {
        await using var server = new RedisServer("[\"cached\"]");
        using var redis = server.CreateStore();
        var cache = CreateCache(redis);

        var result = cache.GetOrCreate<string[]>(CacheKey,
            () => throw new InvalidOperationException("cache hit must not query the database"), 60);

        CollectionAssert.AreEqual(CachedValues, result);
        Assert.AreEqual(1, server.Count("GET"));
        Assert.AreEqual(0, server.Count("SET"));
    }

    [TestMethod]
    public async Task CachedEmptyCollectionIsAValidHit()
    {
        await using var server = new RedisServer("[]");
        using var redis = server.CreateStore();
        var cache = CreateCache(redis);

        var result = cache.GetOrCreate<string[]>(CacheKey,
            () => throw new InvalidOperationException("cached empty collection is valid"), 60);

        Assert.IsNotNull(result);
        Assert.IsEmpty(result);
        Assert.AreEqual(1, server.Count("GET"));
        Assert.AreEqual(0, server.Count("SET"));
    }

    [TestMethod]
    public async Task EntryThatDisappearsAfterExistenceCheckIsRebuilt()
    {
        // EXISTS 与实际读取之间过期或被失效：EXISTS 命中，随后 GET 返回 nil。
        await using var server = new RedisServer(null);
        using var redis = server.CreateStore();
        var cache = CreateCache(redis);
        Assert.IsTrue(cache.ContainsKey<string[]>(CacheKey));
        var calls = 0;

        var result = cache.GetOrCreate(CacheKey, () =>
        {
            calls++;
            return DatabaseValues;
        }, 60);

        // 代理订阅查询接下来会调用 Select；缓存缺失必须返回数据库结果，不能返回 null。
        CollectionAssert.AreEqual(DatabaseValues, result.Select(value => value).ToArray());
        Assert.AreEqual(1, calls);
        Assert.AreEqual("[\"database\"]", server.Value);
        Assert.AreEqual(1, server.Count("SET"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("null")]
    [DataRow("{broken")]
    [DataRow("{}")]
    public async Task EmptyNullOrInvalidCachedCollectionsAreRebuilt(string raw)
    {
        await using var server = new RedisServer(raw);
        using var redis = server.CreateStore();
        var cache = CreateCache(redis);
        var calls = 0;

        var result = cache.GetOrCreate(CacheKey, () =>
        {
            calls++;
            return DatabaseValues;
        }, 60);

        CollectionAssert.AreEqual(DatabaseValues, result);
        Assert.AreEqual(1, calls);
        Assert.AreEqual("[\"database\"]", server.Value);
    }

    [TestMethod]
    public async Task RedisReadFailureFallsBackToTheFactory()
    {
        await using var server = new RedisServer("[\"stale\"]") { FailReads = true };
        using var redis = server.CreateStore();
        var cache = CreateCache(redis);
        var calls = 0;

        var result = cache.GetOrCreate(CacheKey, () =>
        {
            calls++;
            return DatabaseValues;
        }, 60);

        CollectionAssert.AreEqual(DatabaseValues, result);
        Assert.AreEqual(1, calls);
        Assert.IsGreaterThanOrEqualTo(1, server.Count("GET"));
    }

    [TestMethod]
    public async Task ValueTypeDefaultsAndRawStringsRemainCacheHits()
    {
        await using var server = new RedisServer("0");
        using var redis = server.CreateStore();
        var cache = CreateCache(redis);

        Assert.AreEqual(0, cache.GetOrCreate<int>(CacheKey,
            () => throw new InvalidOperationException("cached zero is valid"), 60));
        server.Value = "false";
        Assert.IsFalse(cache.GetOrCreate<bool>(CacheKey,
            () => throw new InvalidOperationException("cached false is valid"), 60));
        server.Value = "raw string that is not JSON";
        Assert.AreEqual(server.Value, cache.GetOrCreate<string>(CacheKey,
            () => throw new InvalidOperationException("cached string is stored without JSON encoding"), 60));
        Assert.AreEqual(0, server.Count("SET"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("null")]
    [DataRow("invalid")]
    public async Task MissingOrInvalidValueTypesAreNotMistakenForCachedZero(string? raw)
    {
        await using var server = new RedisServer(raw);
        using var redis = server.CreateStore();
        var cache = CreateCache(redis);
        var calls = 0;

        Assert.AreEqual(42, cache.GetOrCreate(CacheKey, () => { calls++; return 42; }, 60));
        Assert.AreEqual(1, calls);
        Assert.AreEqual("42", server.Value);
    }

    [TestMethod]
    public async Task FactoryFailureIsPropagatedWithoutWritingAFakeResult()
    {
        await using var server = new RedisServer(null);
        using var redis = server.CreateStore();
        var cache = CreateCache(redis);
        var failure = new InvalidOperationException("database query failed");

        var actual = Assert.ThrowsExactly<InvalidOperationException>(() =>
            cache.GetOrCreate<string[]>(CacheKey, () => throw failure, 60));

        Assert.AreSame(failure, actual);
        Assert.AreEqual(0, server.Count("SET"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProxySubscriptionQueryRebuildsAnExpiredFactCache(bool hasSubscription)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-data", $"cache-subscriptions-{Guid.NewGuid():N}.db");
        var options = Options.Create(new DatabaseOptions { Path = path });
        var uncached = new SqlSugarDatabase(options, NullLogger<SqlSugarDatabase>.Instance);
        try
        {
            using (var db = uncached.CreateClient())
            {
                db.CodeFirst.InitTables<ProxySubscriptionEntity>();
                if (hasSubscription)
                    db.Insertable(new ProxySubscriptionEntity
                    {
                        Id = "persisted-subscription",
                        Name = "persisted subscription",
                        Url = "https://feed.invalid/proxies"
                    }).ExecuteCommand();
            }

            await using var server = new RedisServer(null) { ExpectedKey = null };
            using var redis = server.CreateStore();
            var cached = new SqlSugarDatabase(options, NullLogger<SqlSugarDatabase>.Instance, redis);
            using var service = new ProxySubscriptionService(cached,
                Mock.Of<IProxyStore>(), Mock.Of<IHttpClientFactory>(), Mock.Of<IProxyProbeService>());
            Assert.IsTrue(cached.FactCacheEnabled);

            var first = await service.ListAsync();
            Assert.HasCount(hasSubscription ? 1 : 0, first);
            if (hasSubscription) Assert.AreEqual("persisted-subscription", first[0].Id);
            Assert.AreEqual(1, server.Count("SET"));

            var hit = await service.ListAsync();
            Assert.HasCount(first.Count, hit);
            Assert.AreEqual(1, server.Count("SET"));

            // SQL 事实缓存过期/失效；真实 WithCache().ToList().Select 必须重新读取 SQLite。
            server.Value = null;
            var rebuilt = await service.ListAsync();
            Assert.HasCount(first.Count, rebuilt);
            if (hasSubscription) Assert.AreEqual("persisted-subscription", rebuilt[0].Id);
            Assert.AreEqual(2, server.Count("SET"));
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { /* SQLite 连接池可能延后释放。 */ }
        }
    }

    private static SqlSugarRedisCache CreateCache(RedisKeyValueStore redis)
        => new(redis, NullLogger<SqlSugarDatabase>.Instance);

    /// <summary>只监听随机回环端口的 RESP 夹具，实际执行 CSRedis 和缓存适配器。</summary>
    private sealed class RedisServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private readonly ConcurrentQueue<string[]> _commands = new();
        private readonly List<Task> _connections = [];
        private readonly Task _accept;
        private string? _value;
        private int Port { get; }
        public bool FailReads { get; init; }
        public string? ExpectedKey { get; init; } = PhysicalKey;
        public string? Value { get => Volatile.Read(ref _value); set => Volatile.Write(ref _value, value); }

        public RedisServer(string? value)
        {
            Value = value;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accept = AcceptAsync();
        }

        public RedisKeyValueStore CreateStore() => new(Options.Create(new RedisOptions
        {
            ConnectionString = $"127.0.0.1:{Port},poolsize=1,preheat=0,connectTimeout=1000,syncTimeout=1000",
            InstanceName = "cache-test:"
        }));

        public int Count(string command) => _commands.Count(value => value[0] == command);

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _connections.Add(ServeAsync(client));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                await using var stream = client.GetStream();
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var countLine = await ReadLineAsync(stream, _stop.Token);
                        if (countLine is null) return;
                        Assert.StartsWith("*", countLine);
                        var count = int.Parse(countLine.AsSpan(1), CultureInfo.InvariantCulture);
                        var command = new string[count];
                        for (var i = 0; i < count; i++)
                        {
                            var lengthLine = await ReadLineAsync(stream, _stop.Token) ?? throw new EndOfStreamException();
                            Assert.StartsWith("$", lengthLine);
                            var length = int.Parse(lengthLine.AsSpan(1), CultureInfo.InvariantCulture);
                            var bytes = new byte[length + 2];
                            await stream.ReadExactlyAsync(bytes, _stop.Token);
                            command[i] = Encoding.UTF8.GetString(bytes, 0, length);
                        }
                        command[0] = command[0].ToUpperInvariant();
                        _commands.Enqueue(command);
                        var response = Reply(command);
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(response), _stop.Token);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }

        private string Reply(string[] command)
        {
            switch (command[0])
            {
                case "PING": return "+PONG\r\n";
                case "SELECT": return "+OK\r\n";
                case "CLUSTER": return "-ERR This instance has cluster support disabled\r\n";
                case "EXISTS":
                    CheckKey(command[1]);
                    return ":1\r\n";
                case "GET":
                    CheckKey(command[1]);
                    if (FailReads) return "-ERR synthetic cache read failure\r\n";
                    return Value is { } value ? $"${Encoding.UTF8.GetByteCount(value)}\r\n{value}\r\n" : "$-1\r\n";
                case "SET":
                    CheckKey(command[1]);
                    Value = command[2];
                    return "+OK\r\n";
                default: throw new InvalidOperationException("Unexpected Redis command: " + command[0]);
            }
        }

        private void CheckKey(string key)
        {
            if (ExpectedKey is { } expected) Assert.AreEqual(expected, key);
            else Assert.StartsWith("cache-test:sqlsugar:v1:", key);
        }

        private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken token)
        {
            var line = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one, token) == 0) return null;
                if (one[0] == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)one[0]);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accept;
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }
    }
}
