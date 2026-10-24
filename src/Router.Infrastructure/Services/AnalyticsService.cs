using System.Text.Json;
using Router.Contracts.Host;
using Router.Contracts.Domain;
using Router.Infrastructure.Persistence;

namespace Router.Infrastructure.Services;

/// <summary>从 request_logs 持久化事实生成时间范围分析，不把历史图表建立在内存计数器上。</summary>
public sealed class AnalyticsService(SqlSugarDatabase database) : IAnalyticsService
{
    public async Task<AnalyticsReport> QueryAsync(
        AnalyticsQuery query,
        CancellationToken cancellationToken = default)
    {
        var report = await QueryUsageAsync(query, cancellationToken);
        return new(report.FromUtc, report.ToUtc, report.Requests, report.Successes, report.Failures, report.Tokens,
            report.AverageLatencyMs, report.P95LatencyMs,
            report.Buckets.Select(bucket => new AnalyticsBucket(bucket.StartUtc, bucket.Requests, bucket.Successes,
                bucket.Failures, bucket.Tokens, bucket.AverageLatencyMs, bucket.P95LatencyMs)).ToArray(), report.ByPlatform);
    }

    /// <summary>从同一批持久化请求生成用量明细，保留原分析契约的所有字段。</summary>
    public Task<UsageAnalyticsReport> QueryUsageAsync(
        AnalyticsQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.ToUtc <= query.FromUtc)
            throw new ArgumentException("analytics range must be positive", nameof(query));

        using var db = database.CreateClient();
        var source = db.Queryable<RequestLogEntity>()
            .Where(item => item.CreatedAtUtc >= query.FromUtc.UtcDateTime
                && item.CreatedAtUtc < query.ToUtc.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(query.Platform)) source = source.Where(item => item.Platform == query.Platform);
        if (!string.IsNullOrWhiteSpace(query.Model)) source = source.Where(item => item.Model == query.Model);

        var rows = source.ToList().Select(row => new UsageRow(row,
            row.UsageJson is null ? null : JsonSerializer.Deserialize<Usage>(row.UsageJson),
            row.UsageMetadataJson is null ? null : JsonSerializer.Deserialize<UsageMetadata>(row.UsageMetadataJson))).ToArray();
        var byPlatform = rows.Where(row => !string.IsNullOrWhiteSpace(row.Log.Platform))
            .GroupBy(row => row.Log.Platform!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.OrdinalIgnoreCase);
        var buckets = rows.GroupBy(row => BucketStart(row.Log.CreatedAtUtc, query.Bucket))
            .OrderBy(group => group.Key)
            .Select(group => new UsageAnalyticsBucket(
                new DateTimeOffset(group.Key, TimeSpan.Zero),
                group.LongCount(),
                group.LongCount(row => row.Log.Success),
                group.LongCount(row => !row.Log.Success),
                group.Sum(row => (long)(row.Usage?.TotalTokens ?? 0)),
                group.Average(row => row.Log.DurationMs),
                Percentile(group.Select(row => row.Log.DurationMs), 0.95),
                group.Sum(row => (long)(row.Usage?.PromptTokens ?? 0)),
                group.Sum(row => (long)(row.Usage?.CompletionTokens ?? 0))))
            .ToArray();

        var report = new UsageAnalyticsReport(
            query.FromUtc,
            query.ToUtc,
            rows.LongLength,
            rows.LongCount(row => row.Log.Success),
            rows.LongCount(row => !row.Log.Success),
            rows.Sum(row => (long)(row.Usage?.TotalTokens ?? 0)),
            rows.Length == 0 ? 0 : rows.Average(row => row.Log.DurationMs),
            Percentile(rows.Select(row => row.Log.DurationMs), 0.95),
            buckets,
            byPlatform,
            rows.Sum(row => (long)(row.Usage?.PromptTokens ?? 0)),
            rows.Sum(row => (long)(row.Usage?.CompletionTokens ?? 0)),
            rows.LongCount(row => IsEstimated(row.Metadata)),
            rows.LongCount(row => row.Metadata?.IsPartial == true),
            rows.LongCount(row => row.Usage is null || row.Metadata is null || row.Metadata.Source == "unknown"),
            Breakdown(rows, row => row.Log.Platform ?? "unknown"),
            Breakdown(rows, row => row.Log.Model ?? "unknown"));
        return Task.FromResult(report);
    }

    private static UsageBreakdown[] Breakdown(IEnumerable<UsageRow> rows, Func<UsageRow, string> key)
        => rows.GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new UsageBreakdown(group.Key, group.LongCount(),
                group.Sum(row => (long)(row.Usage?.PromptTokens ?? 0)),
                group.Sum(row => (long)(row.Usage?.CompletionTokens ?? 0)),
                group.Sum(row => (long)(row.Usage?.TotalTokens ?? 0)),
                group.LongCount(row => IsEstimated(row.Metadata))))
            .OrderByDescending(row => row.Tokens)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsEstimated(UsageMetadata? metadata)
        => metadata?.InputSource == "estimated" || metadata?.OutputSource == "estimated";

    private static DateTime BucketStart(DateTime value, string bucket)
    {
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        if (string.Equals(bucket, "day", StringComparison.OrdinalIgnoreCase))
            return utc.Date;
        if (string.Equals(bucket, "15m", StringComparison.OrdinalIgnoreCase))
            return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute / 15 * 15, 0, DateTimeKind.Utc);
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
    }

    private static double Percentile(IEnumerable<int> values, double percentile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private sealed record UsageRow(RequestLogEntity Log, Usage? Usage, UsageMetadata? Metadata);
}

/// <summary>管理端输入输出用量时间桶。</summary>
public sealed record UsageAnalyticsBucket(DateTimeOffset StartUtc, long Requests, long Successes, long Failures,
    long Tokens, double AverageLatencyMs, double P95LatencyMs, long InputTokens, long OutputTokens);

/// <summary>按平台或模型聚合的已记录消耗。</summary>
public sealed record UsageBreakdown(string Name, long Requests, long InputTokens, long OutputTokens, long Tokens, long EstimatedRequests);

/// <summary>扩展管理端分析，公共插件的 AnalyticsReport 保持不变。</summary>
public sealed record UsageAnalyticsReport(DateTimeOffset FromUtc, DateTimeOffset ToUtc, long Requests, long Successes, long Failures,
    long Tokens, double AverageLatencyMs, double P95LatencyMs, IReadOnlyList<UsageAnalyticsBucket> Buckets,
    IReadOnlyDictionary<string, long> ByPlatform, long InputTokens, long OutputTokens, long EstimatedRequests,
    long PartialRequests, long UnknownUsageRequests, IReadOnlyList<UsageBreakdown> Platforms, IReadOnlyList<UsageBreakdown> Models);
