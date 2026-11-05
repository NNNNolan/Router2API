using System.Text.Json;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services.Privacy;
using SqlSugar;

namespace Router.Infrastructure.Services;

/// <summary>护栏和追踪均投影现有请求日志，沿用同一保留和删除边界。</summary>
public sealed class RequestObservationsService(SqlSugarDatabase database)
{
    public PagedResult<ObservedRequest> Requests(DateTimeOffset from, DateTimeOffset to, int page, int pageSize, string? pluginKey,
        bool privacyOnly = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var source = Filter(db, from, to, pluginKey, privacyOnly);
        var total = source.Count();
        var items = source.OrderByDescending(item => item.CreatedAtUtc).OrderByDescending(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToList().Select(Project).ToArray();
        return new(items, page, pageSize, total);
    }

    public ObservedRequest? Find(string id)
    {
        using var db = database.CreateClient();
        var row = DailyLogTables.Query<RequestLogEntity>(db).Where(item => item.Id == id).First();
        return row is null ? null : Project(row);
    }

    public PrivacySummary PrivacySummary(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var source = DailyLogTables.Source<RequestLogEntity>(db, from, to);
        var parameters = new[] { new SugarParameter("@from", from.UtcDateTime), new SugarParameter("@to", to.UtcDateTime) };
        var totals = db.Ado.SqlQuery<PrivacyTotals>($"""
            SELECT COUNT(*) AS Inspected,
                COALESCE(SUM(CASE WHEN json_array_length(PrivacyJson, '$.Hits') > 0 THEN 1 ELSE 0 END), 0) AS Matched,
                COALESCE(SUM(json_extract(PrivacyJson, '$.Blocked')), 0) AS Blocked,
                COALESCE(SUM(json_extract(PrivacyJson, '$.DryRun')), 0) AS DryRun,
                COALESCE(SUM(json_extract(PrivacyJson, '$.Redactions')), 0) AS Redactions
            FROM {source} WHERE CreatedAtUtc >= @from AND CreatedAtUtc < @to AND PrivacyJson IS NOT NULL
            """, parameters).Single();
        var categories = db.Ado.SqlQuery<PrivacyCategoryTotal>($"""
            SELECT json_extract(hit.value, '$.Category') AS Category, SUM(json_extract(hit.value, '$.Count')) AS Hits
            FROM {source} logs, json_each(PrivacyJson, '$.Hits') hit
            WHERE CreatedAtUtc >= @from AND CreatedAtUtc < @to AND PrivacyJson IS NOT NULL
            GROUP BY json_extract(hit.value, '$.Category') ORDER BY Hits DESC
            """, parameters);
        return new(totals.Inspected, totals.Matched, totals.Blocked, totals.DryRun, totals.Redactions, categories);
    }

    public TraceTopology Topology(DateTimeOffset from, DateTimeOffset to, string? pluginKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var source = DailyLogTables.Source<RequestLogEntity>(db, from, to);
        var args = new[]
        {
            new SugarParameter("@from", from.UtcDateTime), new SugarParameter("@to", to.UtcDateTime),
            new SugarParameter("@plugin", pluginKey)
        };
        var links = db.Ado.SqlQuery<TraceTopologyLink>($"""
            SELECT r.Endpoint AS Endpoint, json_extract(s.value, '$.PluginKey') AS PluginKey,
                COUNT(DISTINCT r.Id) AS Requests,
                SUM(CASE WHEN json_extract(s.value, '$.Name') = 'plugin.attempt' THEN 1 ELSE 0 END) AS Attempts,
                SUM(CASE WHEN json_extract(s.value, '$.Name') = 'plugin.attempt' AND json_extract(s.value, '$.Outcome') != 'completed' THEN 1 ELSE 0 END) AS Failures,
                COALESCE(AVG(CASE WHEN json_extract(s.value, '$.Name') = 'plugin.attempt' THEN json_extract(s.value, '$.DurationMs') END), 0) AS AverageLatencyMs
            FROM {source} r, json_each(r.TraceJson, '$.Stages') s
            WHERE r.CreatedAtUtc >= @from AND r.CreatedAtUtc < @to AND r.TraceJson IS NOT NULL
                AND json_extract(s.value, '$.Name') IN ('plugin.attempt', 'mapping.target')
                AND json_extract(s.value, '$.PluginKey') IS NOT NULL
                AND (@plugin IS NULL OR json_extract(s.value, '$.PluginKey') = @plugin)
            GROUP BY r.Endpoint, json_extract(s.value, '$.PluginKey')
            ORDER BY Requests DESC, PluginKey
            """, args);
        var clients = db.Ado.SqlQuery<TraceTopologyClient>($"""
            SELECT r.Endpoint AS Endpoint, COUNT(*) AS Requests,
                SUM(CASE WHEN r.Success = 1 THEN 0 ELSE 1 END) AS Failures, AVG(r.DurationMs) AS AverageLatencyMs
            FROM {source} r
            WHERE r.CreatedAtUtc >= @from AND r.CreatedAtUtc < @to AND r.TraceJson IS NOT NULL
                AND (@plugin IS NULL OR EXISTS (
                    SELECT 1 FROM json_each(r.TraceJson, '$.Stages') s WHERE json_extract(s.value, '$.PluginKey') = @plugin))
            GROUP BY r.Endpoint ORDER BY Requests DESC
            """, args);
        return new(clients, links);
    }

    private static ISugarQueryable<RequestLogEntity> Filter(SqlSugarClient db, DateTimeOffset from, DateTimeOffset to,
        string? pluginKey, bool privacyOnly)
    {
        var source = DailyLogTables.Query<RequestLogEntity>(db, from, to)
            .Where(item => item.CreatedAtUtc >= from.UtcDateTime && item.CreatedAtUtc < to.UtcDateTime);
        if (privacyOnly)
            source = source.Where("PrivacyJson IS NOT NULL AND (json_array_length(PrivacyJson, '$.Hits') > 0 OR json_extract(PrivacyJson, '$.Error') IS NOT NULL)");
        if (!string.IsNullOrWhiteSpace(pluginKey))
            source = source.Where("""
                EXISTS (SELECT 1 FROM json_each(TraceJson, '$.Stages') s WHERE json_extract(s.value, '$.PluginKey') = @plugin)
                """, new { plugin = pluginKey });
        return source;
    }

    private static ObservedRequest Project(RequestLogEntity row) => new(row.Id, row.TraceId, row.Endpoint,
        row.Platform, row.Model, row.Success, row.Cancelled ?? false, row.StatusCode, row.DurationMs,
        new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAtUtc, DateTimeKind.Utc)),
        row.PrivacyJson is null ? null : JsonSerializer.Deserialize<PrivacyReport>(row.PrivacyJson),
        row.TraceJson is null ? null : JsonSerializer.Deserialize<RequestTraceData>(row.TraceJson));

    private sealed class PrivacyTotals
    {
        public long Inspected { get; set; }
        public long Matched { get; set; }
        public long Blocked { get; set; }
        public long DryRun { get; set; }
        public long Redactions { get; set; }
    }
}

public sealed record ObservedRequest(string Id, string TraceId, string? Endpoint, string? Platform, string? Model,
    bool Success, bool Cancelled, int StatusCode, int DurationMs, DateTimeOffset CreatedAt, PrivacyReport? Privacy, RequestTraceData? Trace);
public sealed class PrivacyCategoryTotal
{
    public string Category { get; set; } = "";
    public long Hits { get; set; }
}
public sealed record PrivacySummary(long Inspected, long Matched, long Blocked, long DryRun, long Redactions,
    IReadOnlyList<PrivacyCategoryTotal> Categories);
public sealed class TraceTopologyLink
{
    public string Endpoint { get; set; } = "";
    public string PluginKey { get; set; } = "";
    public long Requests { get; set; }
    public long Attempts { get; set; }
    public long Failures { get; set; }
    public double AverageLatencyMs { get; set; }
}
public sealed class TraceTopologyClient
{
    public string Endpoint { get; set; } = "";
    public long Requests { get; set; }
    public long Failures { get; set; }
    public double AverageLatencyMs { get; set; }
}
public sealed record TraceTopology(IReadOnlyList<TraceTopologyClient> Clients, IReadOnlyList<TraceTopologyLink> Links);
