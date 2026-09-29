using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Host.Api;
using Router.Host.Plugins;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class ProxySubscriptionIntervalTests
{
    [TestMethod]
    [DataRow(null, 3600L)]
    [DataRow("", 3600L)]
    [DataRow("1S", 1L)]
    [DataRow(" 30s ", 30L)]
    [DataRow("90S", 90L)]
    [DataRow("30M", 1800L)]
    [DataRow("2h", 7200L)]
    [DataRow("2147483647M", 128849018820L)]
    [DataRow("128849018820S", 128849018820L)]
    public void ParsesSecondsAndLegacyUnits(string? input, long expected)
    {
        ApiEndpoints.TryParseRefreshInterval(input, out var seconds).Should().BeTrue();
        seconds.Should().Be(expected);
    }

    [TestMethod]
    [DataRow("0S")]
    [DataRow("-1S")]
    [DataRow("0.5M")]
    [DataRow("01S")]
    [DataRow("1 S")]
    [DataRow("1D")]
    [DataRow("1e2S")]
    [DataRow("2147483648M")]
    [DataRow("128849018821S")]
    [DataRow("9223372036854775807H")]
    [DataRow("999999999999999999999S")]
    public async Task RejectsInvalidIntervalsWithoutSavingOrEnqueuing(string input)
    {
        var subscriptions = new Mock<IProxySubscriptionService>(MockBehavior.Strict);
        var queue = new Mock<IProxySubscriptionRefreshQueue>(MockBehavior.Strict);
        var result = await ApiEndpoints.SaveSubscriptionAsync(
            new(null, "test", "https://feed.test/proxies", "http", input),
            subscriptions.Object, queue.Object, CancellationToken.None);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(400);
        subscriptions.VerifyNoOtherCalls();
        queue.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task UpgradesLegacyRowsOnceAndRoundTripsSecondsThroughApiAndStorage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-data", $"subscription-migration-{Guid.NewGuid():N}.db");
        var database = new SqlSugarDatabase(Options.Create(new DatabaseOptions { Path = path }),
            NullLogger<SqlSugarDatabase>.Instance);
        try
        {
            using (var db = database.CreateClient())
            {
                // 真正的旧表：不存在秒字段，保留订阅元数据和历史状态。
                db.Ado.ExecuteCommand("""
                    CREATE TABLE proxy_subscriptions (
                        Id TEXT PRIMARY KEY NOT NULL, Name TEXT NOT NULL, Url TEXT NOT NULL,
                        Scheme TEXT NOT NULL, Enabled INTEGER NOT NULL, ParserName TEXT NULL,
                        RefreshIntervalMinutes INTEGER NULL, LastFetchedAtUtc DATETIME NULL,
                        LastFetchedCount INTEGER NOT NULL, LastError TEXT NULL);
                    INSERT INTO proxy_subscriptions VALUES
                        ('hours', 'legacy hours', 'https://feed.test/hours', 'Socks5', 0, 'legacy', 120, '2026-01-02 03:04:05', 7, 'previous error'),
                        ('minutes', 'legacy minutes', 'https://feed.test/minutes', 'Http', 1, NULL, 30, NULL, 0, NULL),
                        ('maximum', 'large interval', 'https://feed.test/max', 'Http', 1, NULL, 2147483647, NULL, 0, NULL),
                        ('zero', 'zero interval', 'https://feed.test/zero', 'Http', 1, NULL, 0, NULL, 0, NULL),
                        ('negative', 'negative interval', 'https://feed.test/negative', 'Http', 1, NULL, -1, NULL, 0, NULL),
                        ('missing', 'missing interval', 'https://feed.test/missing', 'Http', 1, NULL, NULL, NULL, 0, NULL);
                    """);
            }

            var initializer = new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance);
            initializer.Initialize();
            using var service = new ProxySubscriptionService(database, new ProxyStore(database),
                Mock.Of<IHttpClientFactory>(), Mock.Of<IProxyProbeService>());
            var rows = (await service.ListAsync()).ToDictionary(row => row.Id);
            rows.Should().HaveCount(6);
            rows["hours"].RefreshIntervalSeconds.Should().Be(7200);
            rows["minutes"].RefreshIntervalSeconds.Should().Be(1800);
            rows["maximum"].RefreshIntervalSeconds.Should().Be((long)int.MaxValue * 60);
            foreach (var id in new[] { "zero", "negative", "missing" })
                rows[id].RefreshIntervalSeconds.Should().Be(3600);
            var legacy = rows["hours"];
            legacy.Name.Should().Be("legacy hours");
            legacy.Url.Should().Be("https://feed.test/hours");
            legacy.Scheme.Should().Be(ProxyScheme.Socks5);
            legacy.Enabled.Should().BeFalse();
            legacy.ParserName.Should().Be("legacy");
            legacy.LastFetchedAt.Should().Be(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
            legacy.LastFetchedCount.Should().Be(7);
            legacy.LastError.Should().Be("previous error");

            var queue = new Mock<IProxySubscriptionRefreshQueue>();
            var result = await ApiEndpoints.SaveSubscriptionAsync(
                new("seconds", "seconds", "https://feed.test/seconds", "http", "30S"),
                service, queue.Object, CancellationToken.None);
            ((IStatusCodeHttpResult)result).StatusCode.Should().Be(202);
            var json = JsonSerializer.SerializeToNode(((IValueHttpResult)result).Value)!;
            json["refreshIntervalSeconds"]!.GetValue<long>().Should().Be(30);
            json["refreshIntervalMinutes"]!.GetValue<int>().Should().Be(1);
            queue.Verify(value => value.Enqueue("seconds"), Times.Once);

            // 重复启动不会覆盖 30S，也不会把已经迁移的旧值再次乘以 60。
            initializer.Initialize();
            initializer.Initialize();
            rows = (await service.ListAsync()).ToDictionary(row => row.Id);
            rows["hours"].RefreshIntervalSeconds.Should().Be(7200);
            rows["minutes"].RefreshIntervalSeconds.Should().Be(1800);
            rows["seconds"].RefreshIntervalSeconds.Should().Be(30);
            var roundTrip = JsonSerializer.Deserialize<ProxySubscription>(JsonSerializer.Serialize(rows["seconds"]))!;
            roundTrip.RefreshIntervalSeconds.Should().Be(30);

            // 旧插件继续用分钟属性保存时，秒值同步更新。
            legacy.RefreshIntervalMinutes = 45;
            await service.SaveAsync(legacy);
            (await service.ListAsync()).Single(row => row.Id == legacy.Id).RefreshIntervalSeconds.Should().Be(2700);
            using var check = database.CreateClient();
            check.Queryable<ProxySubscriptionEntity>().Single(row => row.Id == "hours")
                .RefreshIntervalMinutes.Should().Be(45);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { /* SQLite 连接池可能延后释放。 */ }
        }
    }

    [TestMethod]
    public async Task SchedulerUsesSecondsAndBacksOffFailedRefreshes()
    {
        var rows = new[]
        {
            new ProxySubscription { Id = "due", RefreshIntervalSeconds = 30, LastFetchedAt = DateTimeOffset.UtcNow.AddSeconds(-40) },
            new ProxySubscription { Id = "not-due", RefreshIntervalSeconds = 90, LastFetchedAt = DateTimeOffset.UtcNow },
            new ProxySubscription { Id = "disabled", Enabled = false, RefreshIntervalSeconds = 1 },
            new ProxySubscription { Id = "fails", RefreshIntervalSeconds = 1 }
        };
        var subscriptions = new Mock<IProxySubscriptionService>(MockBehavior.Strict);
        subscriptions.Setup(value => value.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(rows);
        subscriptions.Setup(value => value.RefreshAsync("due", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                rows[0].LastFetchedAt = DateTimeOffset.UtcNow;
                return Task.FromResult(new RefreshResult(0, 0, 0, 0));
            });
        subscriptions.Setup(value => value.RefreshAsync("fails", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("test failure"));
        using var scheduler = new ProxySubscriptionRefreshHostedService(subscriptions.Object,
            NullLogger<ProxySubscriptionRefreshHostedService>.Instance);

        await scheduler.RefreshDueAsync(CancellationToken.None);
        await scheduler.RefreshDueAsync(CancellationToken.None);
        subscriptions.Verify(value => value.RefreshAsync("due", It.IsAny<CancellationToken>()), Times.Once);
        subscriptions.Verify(value => value.RefreshAsync("fails", It.IsAny<CancellationToken>()), Times.Once);
        subscriptions.Verify(value => value.RefreshAsync("disabled", It.IsAny<CancellationToken>()), Times.Never);
        subscriptions.Verify(value => value.RefreshAsync("not-due", It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task BackgroundTimerRefreshesBeforeOneMinute()
    {
        var row = new ProxySubscription { Id = "seconds", RefreshIntervalSeconds = 1, LastFetchedAt = DateTimeOffset.UtcNow };
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = new Mock<IProxySubscriptionService>();
        subscriptions.Setup(value => value.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { row });
        subscriptions.Setup(value => value.RefreshAsync(row.Id, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                row.LastFetchedAt = DateTimeOffset.UtcNow;
                refreshed.TrySetResult();
                return Task.FromResult(new RefreshResult(0, 0, 0, 0));
            });
        using var scheduler = new ProxySubscriptionRefreshHostedService(subscriptions.Object,
            NullLogger<ProxySubscriptionRefreshHostedService>.Instance);
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }
}
