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

    [TestMethod]
    public async Task RefreshOfUnchangedProxyOnlyUpdatesSubscriptionSeenTime()
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
            var proxy = new ProxyEndpoint
            {
                Id = "unchanged-proxy",
                SubscriptionId = "subscription-seen",
                Host = "proxy.example.com",
                Port = 8080,
                Username = "user",
                Password = "pass",
                LatencyMs = 37,
                AverageLatencyMs = 45,
                AverageSpeedBytesPerSecond = 12_345,
                CompositeScore = 91.25,
                LastProbeAt = DateTimeOffset.UtcNow,
                LastProbeSuccessAt = DateTimeOffset.UtcNow,
                ProbeStatus = "Healthy",
                ProbeConfigurationVersion = 1,
                ProbeError = "retained diagnostic",
                Status = new ResourceStatus { State = ResourceState.Draining, InFlight = 2, Version = 7 }
            };
            await store.SaveAsync(proxy);
            using var service = new ProxySubscriptionService(
                database,
                store,
                new StaticHttpClientFactory("http://user:pass@proxy.example.com:8080"),
                Mock.Of<IProxyProbeService>());
            await service.SaveAsync(new ProxySubscription
            {
                Id = "subscription-seen",
                Name = "unchanged",
                Url = "https://feed.test/proxies"
            });

            var result = await service.RefreshAsync("subscription-seen");

            result.Updated.Should().Be(1);
            var updated = await store.GetAsync(proxy.Id);
            updated.Should().NotBeNull();
            updated!.LastSeenInSubscriptionAt.Should().NotBeNull();
            updated.LatencyMs.Should().Be(37);
            updated.AverageLatencyMs.Should().Be(45);
            updated.AverageSpeedBytesPerSecond.Should().Be(12_345);
            updated.CompositeScore.Should().Be(91.25);
            updated.ProbeStatus.Should().Be("Healthy");
            updated.ProbeError.Should().Be("retained diagnostic");
            updated.Status.State.Should().Be(ResourceState.Active);
            updated.Status.InFlight.Should().Be(2);
            updated.Status.Version.Should().Be(7);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task SeenAndConnectionWritesDoNotUndoConcurrentAdminState()
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
            var seenProxy = new ProxyEndpoint
            {
                Id = "admin-disabled-seen",
                SubscriptionId = "subscription-admin-race",
                Host = "seen.proxy.test",
                Port = 8001,
                Status = new ResourceStatus { State = ResourceState.Draining }
            };
            var changedProxy = new ProxyEndpoint
            {
                Id = "admin-invalid-connection",
                SubscriptionId = "subscription-admin-race",
                Host = "old.proxy.test",
                Port = 8002
            };
            await store.SaveAsync(seenProxy);
            await store.SaveAsync(changedProxy);
            var snapshot = await store.ListBySubscriptionAsync("subscription-admin-race");

            seenProxy.Status.State = ResourceState.Disabled;
            changedProxy.Status.State = ResourceState.Invalid;
            await store.SaveAsync(seenProxy);
            await store.SaveAsync(changedProxy);
            await store.UpdateSubscriptionSeenBatchAsync([snapshot.Single(proxy => proxy.Id == seenProxy.Id)]);
            await store.ReactivateDrainingBatchAsync([seenProxy.Id]);
            var changed = snapshot.Single(proxy => proxy.Id == changedProxy.Id);
            var connectionUpdate = new ProxyEndpoint
            {
                Id = changed.Id,
                SubscriptionId = changed.SubscriptionId,
                Host = "new.proxy.test",
                Port = changed.Port,
                ConfigurationVersion = changed.ConfigurationVersion + 1,
                ProbeStatus = "Pending",
                ProbeConfigurationVersion = changed.ConfigurationVersion + 1
            };
            await store.UpdateSubscriptionConnectionAsync(connectionUpdate, changed.ConfigurationVersion);

            (await store.GetAsync(seenProxy.Id))!.Status.State.Should().Be(ResourceState.Disabled);
            var current = await store.GetAsync(changedProxy.Id);
            current!.Status.State.Should().Be(ResourceState.Invalid);
            current.Host.Should().Be("new.proxy.test");
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
        }
    }

    [TestMethod]
    public async Task RefreshBatches101NodesAndOnlyReadsTheTargetSubscription()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"router2api-{Guid.NewGuid():N}.db");
        var logger = new SqlCaptureLogger();
        var database = new SqlSugarDatabase(
            Options.Create(new DatabaseOptions { Path = path }),
            logger);
        try
        {
            new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance).Initialize();
            var store = new ProxyStore(database);
            await store.SaveAsync(new ProxyEndpoint
            {
                Id = "other-subscription-proxy",
                SubscriptionId = "other-subscription",
                Host = "other.proxy.test",
                Port = 8000
            });
            var feed = string.Join(Environment.NewLine, Enumerable.Range(0, 101)
                .Select(index => $"http://user:pass@proxy{index}.example.com:8080"));
            var probe = new Mock<IProxyProbeService>();
            probe.Setup(item => item.ProbeAsync(It.IsAny<ProxyEndpoint>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProxyProbeResult(true, 15, 20_000, "Healthy"));
            using var service = new ProxySubscriptionService(
                database, store, new StaticHttpClientFactory(feed), probe.Object);
            await service.SaveAsync(new ProxySubscription
            {
                Id = "target-subscription",
                Name = "target",
                Url = "https://feed.test/proxies"
            });

            var beforeFirstRefresh = logger.Statements.Count;
            var first = await service.RefreshAsync("target-subscription");
            var firstSql = logger.Statements.Skip(beforeFirstRefresh).ToArray();

            first.Added.Should().Be(101);
            first.Updated.Should().Be(0);
            first.Total.Should().Be(101);
            firstSql.Count(statement => statement.Contains("proxy_endpoints", StringComparison.OrdinalIgnoreCase)
                && statement.Contains("INSERT", StringComparison.OrdinalIgnoreCase)).Should().Be(2);

            var beforeSecondRefresh = logger.Statements.Count;
            var second = await service.RefreshAsync("target-subscription");
            var secondSql = logger.Statements.Skip(beforeSecondRefresh).ToArray();

            second.Added.Should().Be(0);
            second.Updated.Should().Be(101);
            second.Total.Should().Be(101);
            var proxyReads = secondSql.Where(statement =>
                statement.Contains("proxy_endpoints", StringComparison.OrdinalIgnoreCase)
                && statement.Contains("SELECT", StringComparison.OrdinalIgnoreCase)).ToArray();
            proxyReads.Should().ContainSingle();
            proxyReads[0].Contains("SubscriptionId", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
            secondSql.Where(statement => statement.Contains("proxy_endpoints", StringComparison.OrdinalIgnoreCase))
                .Should().NotContain(statement => statement.Contains("COUNT(", StringComparison.OrdinalIgnoreCase));

            var proxies = await store.ListAsync();
            proxies.Should().HaveCount(102);
            proxies.Should().ContainSingle(proxy => proxy.SubscriptionId == "other-subscription"
                && proxy.Id == "other-subscription-proxy");
            proxies.Count(proxy => proxy.SubscriptionId == "target-subscription").Should().Be(101);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
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
