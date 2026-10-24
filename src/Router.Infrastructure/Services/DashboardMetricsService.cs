using Router.Infrastructure.Persistence;
using SqlSugar;

namespace Router.Infrastructure.Services;

/// <summary>从已保留的请求日志聚合总览指标，不依赖进程启动后的累计计数。</summary>
/// <param name="database">宿主日志数据库。</param>
public sealed class DashboardMetricsService(SqlSugarDatabase database)
{
    /// <summary>聚合历史、指定本地日期和最近一分钟的已完成请求，并附加实时进行中数。</summary>
    /// <param name="dayStart">今日开始的 UTC 时刻。</param>
    /// <param name="dayEnd">明日开始的 UTC 时刻。</param>
    /// <param name="now">当前 UTC 时刻。</param>
    /// <param name="activeRequests">进程实时进行中请求数。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <returns>总览卡片和平台请求明细。</returns>
    public Task<DashboardMetrics> QueryAsync(DateTimeOffset dayStart, DateTimeOffset dayEnd, DateTimeOffset now,
        int activeRequests, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dayEnd, dayStart);
        using var db = database.CreateClient();
        var totals = db.Ado.SqlQuery<Totals>("""
            SELECT COUNT(*) AS TotalRequests,
                COALESCE(SUM(CASE WHEN Success = 1 THEN 1 ELSE 0 END), 0) AS SuccessfulRequests,
                COALESCE(SUM(CASE WHEN Success = 1 THEN 0 ELSE 1 END), 0) AS FailedRequests,
                COALESCE(AVG(DurationMs), 0) AS AverageLatencyMs,
                COALESCE(SUM(CASE WHEN CreatedAtUtc >= @dayStart AND CreatedAtUtc < @dayEnd THEN 1 ELSE 0 END), 0) AS RequestsToday,
                COALESCE(SUM(CASE WHEN CreatedAtUtc >= @minuteStart AND CreatedAtUtc <= @now THEN 1 ELSE 0 END), 0) AS RequestsPerMinute
            FROM request_logs
            """, new SugarParameter("@dayStart", dayStart.UtcDateTime), new SugarParameter("@dayEnd", dayEnd.UtcDateTime),
            new SugarParameter("@minuteStart", now.AddMinutes(-1).UtcDateTime), new SugarParameter("@now", now.UtcDateTime)).Single();
        cancellationToken.ThrowIfCancellationRequested();
        var platforms = db.Ado.SqlQuery<PlatformTotals>("""
            SELECT COALESCE(Platform, 'unknown') AS Platform, COUNT(*) AS Requests,
                SUM(CASE WHEN Success = 1 THEN 0 ELSE 1 END) AS Errors, AVG(DurationMs) AS AverageLatencyMs
            FROM request_logs
            GROUP BY COALESCE(Platform, 'unknown') COLLATE NOCASE
            ORDER BY Requests DESC, Platform
            """).Select(row => new DashboardPlatformMetric(row.Platform, row.Requests, row.Errors, row.AverageLatencyMs)).ToArray();
        return Task.FromResult(new DashboardMetrics(totals.TotalRequests, totals.SuccessfulRequests, totals.FailedRequests,
            totals.AverageLatencyMs, totals.RequestsToday, totals.RequestsPerMinute, activeRequests, platforms));
    }

    private sealed class Totals
    {
        public long TotalRequests { get; set; }
        public long SuccessfulRequests { get; set; }
        public long FailedRequests { get; set; }
        public double AverageLatencyMs { get; set; }
        public long RequestsToday { get; set; }
        public long RequestsPerMinute { get; set; }
    }

    private sealed class PlatformTotals
    {
        public string Platform { get; set; } = "";
        public long Requests { get; set; }
        public long Errors { get; set; }
        public double AverageLatencyMs { get; set; }
    }
}

/// <summary>管理总览指标；累计值来自已保留日志，进行中数来自进程实时状态。</summary>
/// <param name="TotalRequests">累计已完成请求数。</param>
/// <param name="SuccessfulRequests">累计成功请求数。</param>
/// <param name="FailedRequests">累计失败请求数。</param>
/// <param name="AverageLatencyMs">累计已完成请求平均耗时。</param>
/// <param name="RequestsToday">本地今日已完成请求数。</param>
/// <param name="RequestsPerMinute">最近一分钟已完成请求数。</param>
/// <param name="ActiveRequests">当前进行中请求数。</param>
/// <param name="Platforms">累计平台请求明细。</param>
public sealed record DashboardMetrics(long TotalRequests, long SuccessfulRequests, long FailedRequests,
    double AverageLatencyMs, long RequestsToday, long RequestsPerMinute, int ActiveRequests,
    IReadOnlyList<DashboardPlatformMetric> Platforms);

/// <summary>从已保留日志聚合的平台指标。</summary>
/// <param name="Platform">平台名称。</param>
/// <param name="Requests">已完成请求数。</param>
/// <param name="Errors">失败请求数。</param>
/// <param name="AverageLatencyMs">平台平均耗时。</param>
public sealed record DashboardPlatformMetric(string Platform, long Requests, long Errors, double AverageLatencyMs);
