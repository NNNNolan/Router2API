using System.Text.Json;
using Router.Contracts.Host;
using Router.Contracts.Domain;
using Router.Infrastructure.Persistence;

namespace Router.Infrastructure.Services;

/// <summary>从 request_logs 持久化事实生成时间范围分析，不把历史图表建立在内存计数器上。</summary>
public sealed class AnalyticsService(SqlSugarDatabase database) : IAnalyticsService
{
    public Task<AnalyticsReport> QueryAsync(
        AnalyticsQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.ToUtc <= query.FromUtc)
            throw new ArgumentException("analytics range must be positive", nameof(query));

        var source = database.Scope.Queryable<RequestLogEntity>()
            .Where(item => item.CreatedAtUtc >= query.FromUtc.UtcDateTime
                && item.CreatedAtUtc < query.ToUtc.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(query.Platform)) source = source.Where(item => item.Platform == query.Platform);
        if (!string.IsNullOrWhiteSpace(query.Model)) source = source.Where(item => item.Model == query.Model);

        var rows = source.ToList();
        var byPlatform = rows.Where(row => !string.IsNullOrWhiteSpace(row.Platform))
            .GroupBy(row => row.Platform!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.OrdinalIgnoreCase);
        var buckets = rows.GroupBy(row => BucketStart(row.CreatedAtUtc, query.Bucket))
            .OrderBy(group => group.Key)
            .Select(group => new AnalyticsBucket(
                new DateTimeOffset(group.Key, TimeSpan.Zero),
                group.LongCount(),
                group.LongCount(row => row.Success),
                group.LongCount(row => !row.Success),
                group.Sum(row => ReadTokens(row.UsageJson)),
                group.Any() ? group.Average(row => row.DurationMs) : 0,
                Percentile(group.Select(row => row.DurationMs), 0.95)))
            .ToArray();

        var report = new AnalyticsReport(
            query.FromUtc,
            query.ToUtc,
            (long)rows.Count,
            rows.LongCount(row => row.Success),
            rows.LongCount(row => !row.Success),
            rows.Sum(row => ReadTokens(row.UsageJson)),
            rows.Count == 0 ? 0 : rows.Average(row => row.DurationMs),
            Percentile(rows.Select(row => row.DurationMs), 0.95),
            buckets,
            byPlatform);
        return Task.FromResult(report);
    }

    private static DateTime BucketStart(DateTime value, string bucket)
    {
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        if (string.Equals(bucket, "day", StringComparison.OrdinalIgnoreCase))
            return utc.Date;
        if (string.Equals(bucket, "15m", StringComparison.OrdinalIgnoreCase))
            return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute / 15 * 15, 0, DateTimeKind.Utc);
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
    }

    private static long ReadTokens(string? usageJson)
    {
        if (string.IsNullOrWhiteSpace(usageJson)) return 0;
        try { return JsonSerializer.Deserialize<Usage>(usageJson)?.TotalTokens ?? 0; }
        catch (JsonException) { return 0; }
    }

    private static double Percentile(IEnumerable<int> values, double percentile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }
}
