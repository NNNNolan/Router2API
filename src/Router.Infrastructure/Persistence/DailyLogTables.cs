using System.Globalization;
using SqlSugar;

namespace Router.Infrastructure.Persistence;

/// <summary>原始 SQL 聚合和 ORM 共用 SqlSugar 分表发现；不维护另一份表目录或旧表读写路径。</summary>
public static class DailyLogTables
{
    /// <summary>生成时间裁剪的 UNION ALL 子查询。表名仅来自 ORM 分表元数据。</summary>
    public static string Source<T>(SqlSugarClient db, DateTimeOffset? from = null, DateTimeOffset? to = null) where T : class, new()
        => "(" + Query<T>(db, from, to).ToSql().Key + ")";

    /// <summary>范围为 UTC 半开区间；分页和排序在合并各日表后应用。</summary>
    public static ISugarQueryable<T> Query<T>(SqlSugarClient db, DateTimeOffset? from = null, DateTimeOffset? to = null) where T : class, new()
        => db.Queryable<T>().SplitTable(tables => tables.Where(table => InRange(table.TableName, from, to)));

    private static bool InRange(string name, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (!name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            || name.Length < 8 || !DateTime.TryParseExact(name[^8..], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day))
            throw new InvalidOperationException("日志分表名称无效。");
        return (from is null || day.AddDays(1) > from.Value.UtcDateTime)
            && (to is null || day < to.Value.UtcDateTime);
    }

    /// <summary>旧单表在事务里按日期迁入日表，核对条数后才移除旧表；失败不留下部分搬迁。</summary>
    internal static void Migrate<T>(SqlSugarClient db, string legacy, string timeColumn) where T : class, new()
    {
        if (!db.DbMaintenance.IsAnyTable(legacy, false)) return;
        var legacyColumns = db.DbMaintenance.GetColumnInfosByTableName(legacy, false).Select(column => column.DbColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!legacyColumns.Contains(timeColumn)) throw new InvalidOperationException($"旧日志表 {legacy} 缺少日期字段。");
        var days = db.Ado.SqlQuery<LegacyDay>($"SELECT date([{timeColumn}]) AS Day, COUNT(*) AS Count FROM [{legacy}] GROUP BY date([{timeColumn}])");
        db.Ado.BeginTran();
        try
        {
            foreach (var row in days)
            {
                if (!DateTime.TryParseExact(row.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    throw new InvalidOperationException($"旧日志表 {legacy} 包含无效日期。");
                var table = db.SplitHelper<T>().GetTableName(day);
                // 分表实体的 CodeFirst.As 在进入普通初始化前会走分表分支；显式映射单个物理表。
                db.MappingTables.Add(typeof(T).Name, table);
                try { db.CodeFirst.InitTables<T>(); }
                finally { db.MappingTables.RemoveAll(mapping => mapping.EntityName == typeof(T).Name); }
                var columns = db.DbMaintenance.GetColumnInfosByTableName(table, false)
                    .Select(column => column.DbColumnName).Where(legacyColumns.Contains).ToArray();
                var names = string.Join(", ", columns.Select(column => $"[{column}]"));
                var inserted = db.Ado.ExecuteCommand($"""
                    INSERT INTO [{table}] ({names}) SELECT {names} FROM [{legacy}]
                    WHERE [{timeColumn}] >= @from AND [{timeColumn}] < @to
                    """, new SugarParameter("@from", day), new SugarParameter("@to", day.AddDays(1)));
                if (inserted != row.Count) throw new InvalidOperationException($"旧日志表 {legacy} 迁移条数不一致。");
            }
            db.DbMaintenance.DropTable(legacy);
            db.Ado.CommitTran();
        }
        catch { db.Ado.RollbackTran(); throw; }
    }

    /// <summary>整天过期的表直接清理，边界日按真实 UTC 时刻逐行删除。</summary>
    public static int DeleteBefore<T>(SqlSugarClient db, DateTimeOffset cutoff, string timeColumn) where T : class, new()
    {
        var deleted = 0;
        foreach (var table in db.SplitHelper<T>().GetTables().Where(table => InRange(table.TableName, null, cutoff)))
        {
            var count = db.Ado.GetInt($"SELECT COUNT(*) FROM [{table.TableName}] WHERE [{timeColumn}] < @cutoff",
                new SugarParameter("@cutoff", cutoff.UtcDateTime));
            var day = DateTime.ParseExact(table.TableName[^8..], "yyyyMMdd", CultureInfo.InvariantCulture);
            if (day.AddDays(1) <= cutoff.UtcDateTime)
                db.DbMaintenance.DropTable(table.TableName);
            else
                db.Ado.ExecuteCommand($"DELETE FROM [{table.TableName}] WHERE [{timeColumn}] < @cutoff",
                    new SugarParameter("@cutoff", cutoff.UtcDateTime));
            deleted += count;
        }
        return deleted;
    }

    private sealed class LegacyDay
    {
        public string Day { get; set; } = "";
        public int Count { get; set; }
    }
}
