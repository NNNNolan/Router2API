using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class DashboardMetricsTests
{
    [TestMethod]
    public async Task PersistedTotalsSurviveRestartAndTodayMatchesUsageAnalytics()
    {
        var database = Database();
        var logs = new RequestLogStore(database);
        var dayStart = new DateTimeOffset(2026, 10, 24, 16, 0, 0, TimeSpan.Zero);
        var now = dayStart.AddHours(12);
        await Write(logs, "a", dayStart.AddSeconds(-1), true, 100);
        await Write(logs, "a", dayStart, true, 200);
        await Write(logs, "b", now.AddSeconds(-61), false, 300);
        await Write(logs, "a", now.AddSeconds(-60), true, 400);
        await Write(logs, "b", now.AddSeconds(-1), false, 500);
        var restartedMetrics = new RealtimeMetrics();
        restartedMetrics.OnRequestStarted("a");
        restartedMetrics.OnRequestStarted("b");
        restartedMetrics.Snapshot().RequestsToday.Should().Be(0);
        var result = await new DashboardMetricsService(database).QueryAsync(dayStart, dayStart.AddDays(1), now,
            restartedMetrics.Snapshot().ActiveConnections);
        result.TotalRequests.Should().Be(5);
        result.SuccessfulRequests.Should().Be(3);
        result.FailedRequests.Should().Be(2);
        result.RequestsToday.Should().Be(4);
        result.RequestsPerMinute.Should().Be(2);
        result.AverageLatencyMs.Should().Be(300);
        result.ActiveRequests.Should().Be(2);
        result.Platforms.Single(platform => platform.Platform == "b").Should().BeEquivalentTo(new DashboardPlatformMetric("b", 2, 2, 400));
        var analysis = await new AnalyticsService(database).QueryUsageAsync(new(dayStart, now));
        result.RequestsToday.Should().Be(analysis.Requests);
        var reloaded = await new DashboardMetricsService(database).QueryAsync(dayStart, dayStart.AddDays(1), now, 0);
        reloaded.Should().BeEquivalentTo(result with { ActiveRequests = 0 });
    }

    [TestMethod]
    public async Task DayBoundariesUseTheProvidedLocalDayAndRecentCountsExcludeFutureLogs()
    {
        var database = Database();
        var logs = new RequestLogStore(database);
        var dayStart = new DateTimeOffset(2026, 10, 24, 16, 0, 0, TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);
        await Write(logs, "a", dayStart.AddMilliseconds(-1), true, 10);
        await Write(logs, "a", dayStart, true, 20);
        await Write(logs, null, dayEnd.AddMilliseconds(-1), false, 30);
        await Write(logs, "a", dayEnd, true, 40);
        var result = await new DashboardMetricsService(database).QueryAsync(dayStart, dayEnd, dayEnd.AddMilliseconds(-1), 0);
        result.TotalRequests.Should().Be(4);
        result.RequestsToday.Should().Be(2);
        result.RequestsPerMinute.Should().Be(1);
        result.Platforms.Should().Contain(platform => platform.Platform == "unknown" && platform.Errors == 1);
    }

    [TestMethod]
    public async Task NoCompletedLogsStillReportsRealInflightRequests()
    {
        var now = DateTimeOffset.UtcNow;
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var result = await new DashboardMetricsService(Database()).QueryAsync(dayStart, dayStart.AddDays(1), now, 3);
        result.Should().BeEquivalentTo(new DashboardMetrics(0, 0, 0, 0, 0, 0, 3, []));
    }

    private static SqlSugarDatabase Database()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-data", $"dashboard-{Guid.NewGuid():N}.db");
        var database = new SqlSugarDatabase(Options.Create(new DatabaseOptions { Path = path }), NullLogger<SqlSugarDatabase>.Instance);
        new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance).Initialize();
        return database;
    }

    private static Task Write(RequestLogStore store, string? platform, DateTimeOffset at, bool success, int durationMs)
        => store.WriteAsync(new RequestLog { TraceId = Guid.NewGuid().ToString("N"), Platform = platform,
            Model = "model", CreatedAt = at, Success = success, StatusCode = success ? 200 : 503, DurationMs = durationMs });
}
