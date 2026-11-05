using System.Globalization;
using Router.Infrastructure.Persistence;
using SqlSugar;

namespace Router.Infrastructure.Services;

/// <summary>按时区对应的每日 UTC 边界在 SQLite 聚合，不把全年日志加载到前端或内存。</summary>
public sealed class ActivityHeatmapService(SqlSugarDatabase database)
{
    public Task<ActivityHeatmapReport> QueryAsync(TimeZoneInfo zone, DateTimeOffset now, int retentionDays,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var first = today.AddDays(-364);
        var boundaries = Enumerable.Range(0, 366).Select(index => Boundary(first.AddDays(index), zone)).ToArray();
        using var db = database.CreateClient();
        var source = DailyLogTables.Source<RequestLogEntity>(db, boundaries[0], boundaries[^1]);
        var parameters = new List<SugarParameter>();
        var days = Enumerable.Range(0, 365).Select(index =>
        {
            parameters.Add(new($"@start{index}", boundaries[index].UtcDateTime));
            parameters.Add(new($"@end{index}", boundaries[index + 1].UtcDateTime));
            return $"SELECT '{first.AddDays(index):yyyy-MM-dd}' AS Day, @start{index} AS Start, @end{index} AS End";
        });
        // 日期常量完全由宿主 DateOnly 产生；时刻均用参数绑定。
        var sql = $"""
            WITH days AS ({string.Join(" UNION ALL ", days)})
            SELECT days.Day, COUNT(log.Id) AS Requests
            FROM days LEFT JOIN {source} log ON log.CreatedAtUtc >= days.Start AND log.CreatedAtUtc < days.End
            GROUP BY days.Day ORDER BY days.Day
            """;
        var counts = db.Ado.SqlQuery<DayCount>(sql, parameters.ToArray())
            .Select(row => new ActivityHeatmapDay(row.Day, row.Requests)).ToArray();
        var earliest = DailyLogTables.Query<RequestLogEntity>(db).OrderBy(item => item.CreatedAtUtc)
            .Select(item => new { item.CreatedAtUtc }).First();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ActivityHeatmapReport(first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), zone.Id, retentionDays,
            earliest is null ? null : new DateTimeOffset(DateTime.SpecifyKind(earliest.CreatedAtUtc, DateTimeKind.Utc)),
            counts, counts.Sum(day => day.Requests), counts.Count(day => day.Requests > 0)));
    }

    private static DateTimeOffset Boundary(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // 少数时区在午夜跳时；使用该自然日第一个实际存在的时刻。
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private sealed class DayCount
    {
        public string Day { get; set; } = "";
        public long Requests { get; set; }
    }
}

public sealed record ActivityHeatmapDay(string Day, long Requests);
public sealed record ActivityHeatmapReport(string FromDay, string ToDay, string Timezone, int RetentionDays,
    DateTimeOffset? EarliestLogUtc, IReadOnlyList<ActivityHeatmapDay> Days, long Requests, int ActiveDays);
