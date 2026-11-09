using System.Globalization;
using SqlSugar;

namespace Router.Infrastructure.Persistence;

/// <summary>原始 SQL 聚合和 ORM 共用 SqlSugar 分表发现；不维护另一份表目录或旧表读写路径。</summary>
public static class DailyLogTables
{
    public static string EnsureTable<T>(SqlSugarClient db, DateTime utcDay) where T : class, new()
    {
        var table = db.SplitHelper<T>().GetTableName(DateTime.SpecifyKind(utcDay.Date, DateTimeKind.Utc));
        db.MappingTables.Add(typeof(T).Name, table);
        try { db.CodeFirst.InitTables<T>(); }
        finally { db.MappingTables.RemoveAll(mapping => mapping.EntityName == typeof(T).Name); }
        return table;
    }

    /// <summary>生成时间裁剪的 UNION ALL 子查询。表名仅来自 ORM 分表元数据。</summary>
    public static string Source<T>(SqlSugarClient db, DateTimeOffset? from = null, DateTimeOffset? to = null) where T : class, new()
        => db.SplitHelper<T>().GetTables().Any(table => InRange(table.TableName, from, to))
            ? "(" + Query<T>(db, from, to).ToSql().Key + ")"
            : EmptySource<T>(db);

    /// <summary>SqlSugar 在无匹配分表时生成注释占位符；显式构造同列空集合，保证外层聚合 SQL 可执行。</summary>
    private static string EmptySource<T>(SqlSugarClient db) where T : class, new()
    {
        var columns = db.EntityMaintenance.GetEntityInfo<T>().Columns
            .Where(column => !column.IsIgnore)
            .Select(column => $"NULL AS [{column.DbColumnName}]");
        return "(SELECT " + string.Join(", ", columns) + " WHERE 0)";
    }

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

}
