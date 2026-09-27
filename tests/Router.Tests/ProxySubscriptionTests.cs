using System.Net;
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
public sealed class ProxySubscriptionTests
{
    [TestMethod]
    public async Task ListingSubscriptionsDoesNotCloseAnActiveReaderInTheParentContext()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-data", $"router2api-{Guid.NewGuid():N}.db");
        var database = new SqlSugarDatabase(
            Options.Create(new DatabaseOptions { Path = path }),
            NullLogger<SqlSugarDatabase>.Instance);
        try
        {
            new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance).Initialize();
            using var service = new ProxySubscriptionService(
                database, new ProxyStore(database), new StaticHttpClientFactory(string.Empty), Mock.Of<IProxyProbeService>());
            await service.SaveAsync(new ProxySubscription
            {
                Id = "subscription-reader",
                Name = "reader isolation",
                Url = "https://feed.test/proxies"
            });

            using var db = database.CreateClient();
            using var command = db.Ado.Connection.CreateCommand();
            command.Connection!.Open();
            command.CommandText = "SELECT 1 UNION ALL SELECT 2";
            using var reader = command.ExecuteReader();
            reader.FieldCount.Should().Be(1);

            // 子任务继承父级 ExecutionContext，查询结束时不能关闭父级仍在读取的连接。
            await Task.Run(async () =>
            {
                await Task.Yield();
                (await service.ListAsync()).Should().ContainSingle(item => item.Id == "subscription-reader");
            });

            reader.FieldCount.Should().Be(1);
            reader.Read().Should().BeTrue();
            reader.GetInt64(0).Should().Be(1);
            reader.Read().Should().BeTrue();
            reader.GetInt64(0).Should().Be(2);
            reader.Read().Should().BeFalse();
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { /* SQLite 连接池可能延后释放。 */ }
        }
    }

    [TestMethod]
    public async Task RefreshParsesAuthenticatedProxyUrisAndDeleteRemovesNodes()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"router2api-{Guid.NewGuid():N}.db");
        var database = new SqlSugarDatabase(
            Options.Create(new DatabaseOptions { Path = path }),
            NullLogger<SqlSugarDatabase>.Instance);

        try
        {
            new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance).Initialize();
            var store = new ProxyStore(database);
            var probes = new Mock<IProxyProbeService>();
            probes
                .Setup(item => item.ProbeAsync(It.IsAny<ProxyEndpoint>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProxyProbeResult(true, 20, 10_000, "Healthy"));
            var service = new ProxySubscriptionService(
                database,
                store,
                new StaticHttpClientFactory("""
                    socks5://user:pass@proxy.example.com:1080
                    socks5h://user:pass@proxy.example.com:1081
                    http://user:pass@proxy.example.com:8080
                    https://user:pass@proxy.example.com:8443
                    """),
                probes.Object);

            await service.SaveAsync(new ProxySubscription
            {
                Id = "subscription-1",
                Name = "authenticated proxies",
                Url = "https://feed.test/proxies",
                RefreshIntervalMinutes = 120
            });

            (await service.ListAsync()).Single().RefreshIntervalMinutes.Should().Be(120);

            var result = await service.RefreshAsync("subscription-1");
            result.Added.Should().Be(4);

            var proxies = await store.ListAsync();
            proxies.Should().HaveCount(4);
            proxies.Should().ContainSingle(proxy =>
                proxy.Scheme == ProxyScheme.Socks5
                && proxy.Port == 1080
                && proxy.Username == "user"
                && proxy.Password == "pass");
            proxies.Should().ContainSingle(proxy =>
                proxy.Scheme == ProxyScheme.Socks5
                && proxy.Port == 1081
                && proxy.Username == "user"
                && proxy.Password == "pass");
            proxies.Should().ContainSingle(proxy =>
                proxy.Scheme == ProxyScheme.Http
                && proxy.Port == 8080
                && proxy.Username == "user"
                && proxy.Password == "pass");
            proxies.Should().ContainSingle(proxy =>
                proxy.Scheme == ProxyScheme.Https
                && proxy.Port == 8443
                && proxy.Username == "user"
                && proxy.Password == "pass");

            await service.DeleteAsync("subscription-1");

            (await store.ListAsync()).Should().BeEmpty();
            (await service.ListAsync()).Should().BeEmpty();
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // SqlSugar 可能会一直持有 SQLite 句柄，直到进程级连接池被回收。
            }
        }
    }

    private sealed class StaticHttpClientFactory(string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StaticHttpMessageHandler(body));
    }

    private sealed class StaticHttpMessageHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
    }
}
