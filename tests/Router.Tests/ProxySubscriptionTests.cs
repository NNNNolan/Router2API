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
            database.Scope.Dispose();
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
