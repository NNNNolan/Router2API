using System.Globalization;
using System.Reflection;
using System.Data;
using SqlSugar;

namespace Router.Infrastructure.Persistence;

/// <summary>迁移显式允许的历史日志表族。</summary>
public static class LogDatabaseMigration
{
    private const int BatchSize = 500;
    private static readonly IReadOnlyDictionary<string, Family> Families = new Dictionary<string, Family>(StringComparer.Ordinal)
    {
        ["request_logs"] = new("request_logs", "CreatedAtUtc", "Id"),
        ["resource_events"] = new("resource_events", "CreatedAtUtc", "Id"),
        ["task_logs"] = new("task_logs", "StartedAtUtc", "Id"),
        ["plugin_logs"] = new("plugin_logs", "CreatedAtUtc", "Id"),
        ["usage_buckets"] = new("usage_buckets", "Day", "Id")
    };

    public static MigrationInventory Scan(SqlSugarDatabase target, string sourcePath)
    {
        ValidateDistinctPaths(target.FilePath, sourcePath);
        using var db = CreatePinnedClient(target);
        Attach(db, sourcePath);
        try
        {
            var tables = ReadTableNames(db);
            ValidateReservedNames(tables);
            var selected = tables.Where(name => TryFamily(name, out _)).Order(StringComparer.Ordinal).ToArray();
            long rows = 0;
            var tableRows = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var table in selected)
            {
                var family = GetFamily(table);
                ValidateColumns(db, table, family);
                var count = ScalarLong(db, $"SELECT COUNT(*) FROM legacy.[{table}]");
                tableRows.Add(table, count);
                rows += count;
            }
            return new MigrationInventory(selected, tableRows, rows);
        }
        finally { Detach(db); }
    }

    public static async Task<MigrationResult> MigrateAsync(SqlSugarDatabase target, string sourcePath,
        MigrationInventory inventory, Action<MigrationProgress> report, CancellationToken cancellationToken)
    {
        ValidateDistinctPaths(target.FilePath, sourcePath);
        using var db = CreatePinnedClient(target);
        Attach(db, sourcePath);
        try
        {
            var completed = new List<string>();
            long copied = 0;
            long verified = 0;
            var initializedTables = new HashSet<string>(StringComparer.Ordinal);
            foreach (var table in inventory.Tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var family = GetFamily(table);
                ValidateColumns(db, table, family);
                var sourceColumns = ReadColumns(db, table);
                var targetColumns = TargetColumns(family.Name);
                var shared = sourceColumns.Where(targetColumns.Contains).ToArray();
                var optionalMissing = targetColumns.Where(OptionalColumns(family.Name).Contains).Except(shared, StringComparer.OrdinalIgnoreCase).ToArray();
                var sourceCount = ScalarLong(db, $"SELECT COUNT(*) FROM legacy.[{table}]");
                if (!inventory.TableRows.TryGetValue(table, out var inventoriedCount) || sourceCount != inventoriedCount)
                    throw new LogMigrationException($"来源表 {table} 在迁移开始前发生变化，请重新扫描后重试。");
                long? lastRowId = null;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rows = db.Ado.GetDataTable($"SELECT rowid AS __rowid, * FROM legacy.[{table}] {(lastRowId is null ? "" : "WHERE rowid > @rowId")} ORDER BY rowid LIMIT {BatchSize}",
                        lastRowId is null ? Array.Empty<SugarParameter>() : [new SugarParameter("@rowId", lastRowId.Value)]);
                    if (rows.Rows.Count == 0) break;
                    ValidateBatchRows(rows, family);
                    var groups = rows.Rows.Cast<DataRow>().GroupBy(row => GetUtcDay(row[family.TimeColumn])).ToArray();
                    var endRowId = Convert.ToInt64(rows.Rows[^1]["__rowid"], CultureInfo.InvariantCulture);
                    foreach (var group in groups)
                    {
                        var targetTable = EnsureTargetTable(db, family.Name, group.Key, initializedTables);
                        var targetFields = shared.Concat(optionalMissing).ToArray();
                        var insertFields = string.Join(", ", targetFields.Select(Quote));
                        db.Ado.BeginTran();
                        try
                        {
                            var fields = string.Join(", ", targetFields.Select(column => shared.Contains(column, StringComparer.OrdinalIgnoreCase) ? $"src.{Quote(column)}" : "NULL"));
                            var from = group.Key;
                            var to = from.AddDays(1);
                            var range = (lastRowId is null ? "" : "src.rowid > @start AND ") + $"src.rowid <= @end AND src.{Quote(family.TimeColumn)} >= @from AND src.{Quote(family.TimeColumn)} < @to";
                            EnsureBatchHasNoConflicts(db, table, targetTable, family, shared, optionalMissing, range, lastRowId, endRowId, from, to);
                            var sql = $"INSERT INTO main.[{targetTable}] ({insertFields}) SELECT DISTINCT {fields} FROM legacy.[{table}] src WHERE {range} AND NOT EXISTS (SELECT 1 FROM main.[{targetTable}] dst WHERE dst.[{family.KeyColumn}] = src.[{family.KeyColumn}])";
                            var args = new List<SugarParameter> { new("@end", endRowId), new("@from", from), new("@to", to) };
                            if (lastRowId is not null) args.Add(new SugarParameter("@start", lastRowId.Value));
                            db.Ado.ExecuteCommand(sql, args.ToArray());
                            EnsureBatchFullyPresent(db, table, targetTable, family, shared, optionalMissing, range, lastRowId, endRowId, from, to);
                            db.Ado.CommitTran();
                            copied += group.LongCount();
                        }
                        catch { db.Ado.RollbackTran(); throw; }
                    }
                    lastRowId = endRowId;
                    report(new MigrationProgress("copying", table, copied, verified, 0, inventory.Rows, inventory.Tables.Count));
                }
                if (ScalarLong(db, $"SELECT COUNT(*) FROM legacy.[{table}]") != sourceCount)
                    throw new LogMigrationException($"来源表 {table} 在迁移期间发生变化，请重试。");
                report(new MigrationProgress("verifying", table, copied, verified, 0, inventory.Rows, inventory.Tables.Count));
                await VerifyTableAsync(db, table, family, sourceCount, count =>
                {
                    verified += count;
                    report(new MigrationProgress("verifying", table, copied, verified, 0, inventory.Rows, inventory.Tables.Count));
                }, initializedTables, cancellationToken);
                completed.Add(table);
                report(new MigrationProgress("verifying", table, copied, verified, 0, inventory.Rows, inventory.Tables.Count));
            }

            // 所有表完成逐字段验证后才开始清理来源。
            var deleted = 0;
            foreach (var table in completed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var family = GetFamily(table);
                db.Ado.BeginTran();
                try
                {
                    var count = ScalarLong(db, $"SELECT COUNT(*) FROM legacy.[{table}]");
                    if (count != inventory.TableRows[table]) throw new LogMigrationException($"来源表 {table} 在清理前发生变化。");
                    report(new MigrationProgress("deleting", table, copied, verified, deleted, inventory.Rows, inventory.Tables.Count));
                    await VerifyTableAsync(db, table, family, inventory.TableRows[table], _ => { }, initializedTables, cancellationToken);
                    db.Ado.ExecuteCommand($"DROP TABLE legacy.[{table}]");
                    db.Ado.CommitTran();
                    deleted++;
                    report(new MigrationProgress("deleting", table, copied, verified, deleted, inventory.Rows, inventory.Tables.Count));
                }
                catch { db.Ado.RollbackTran(); throw; }
            }
            return new MigrationResult(copied, verified, deleted);
        }
        finally { Detach(db); }
    }

    private static async Task VerifyTableAsync(SqlSugarClient db, string source, Family family, long expected,
        Action<long> verified, HashSet<string> initializedTables, CancellationToken cancellationToken)
    {
        ValidateColumns(db, source, family);
        var columns = ReadColumns(db, source);
        var known = TargetColumns(family.Name);
        var fields = columns.Where(known.Contains).ToArray();
        var optionalMissing = known.Where(OptionalColumns(family.Name).Contains).Except(fields, StringComparer.OrdinalIgnoreCase).ToArray();
        long? lastRowId = null;
        long found = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = db.Ado.GetDataTable($"SELECT rowid AS __rowid, * FROM legacy.[{source}] {(lastRowId is null ? "" : "WHERE rowid > @rowId")} ORDER BY rowid LIMIT {BatchSize}",
                lastRowId is null ? Array.Empty<SugarParameter>() : [new SugarParameter("@rowId", lastRowId.Value)]);
            if (rows.Rows.Count == 0) break;
            ValidateBatchRows(rows, family);
            foreach (var group in rows.Rows.Cast<DataRow>().GroupBy(row => GetUtcDay(row[family.TimeColumn])))
            {
                var day = group.Key;
                var target = family.Name == "usage_buckets" ? "usage_buckets" : EnsureTargetTable(db, family.Name, day, initializedTables);
                var end = Convert.ToInt64(rows.Rows[^1]["__rowid"], CultureInfo.InvariantCulture);
                var range = (lastRowId is null ? "" : "src.rowid > @start AND ") + $"src.rowid <= @end AND src.{Quote(family.TimeColumn)} >= @from AND src.{Quote(family.TimeColumn)} < @to";
                var args = new[] { new SugarParameter("@end", end), new SugarParameter("@from", day), new SugarParameter("@to", day.AddDays(1)) };
                if (lastRowId is not null) args = [.. args, new SugarParameter("@start", lastRowId.Value)];
                EnsureBatchFullyPresent(db, source, target, family, fields, optionalMissing, range, lastRowId, end, day, day.AddDays(1));
                var count = ScalarLong(db, $"SELECT COUNT(*) FROM legacy.[{source}] src WHERE {range}", args);
                found += count;
                verified(count);
            }
            lastRowId = Convert.ToInt64(rows.Rows[^1]["__rowid"], CultureInfo.InvariantCulture);
            await Task.Yield();
        }
        if (found != expected) throw new LogMigrationException($"来源表 {source} 核对条数不一致。");
    }

    private static void EnsureBatchHasNoConflicts(SqlSugarClient db, string source, string target, Family family,
        IReadOnlyList<string> sourceFields, IReadOnlyList<string> optionalMissing, string range,
        long? start, long end, DateTime from, DateTime to)
    {
        var comparison = Comparison(sourceFields, optionalMissing);
        var args = BatchArgs(start, end, from, to);
        var conflicts = ScalarLong(db, $"SELECT COUNT(*) FROM legacy.[{source}] src JOIN main.[{target}] dst ON dst.[{family.KeyColumn}] = src.[{family.KeyColumn}] WHERE {range} AND NOT ({comparison})", args);
        if (conflicts != 0)
        {
            foreach (var column in sourceFields.Concat(optionalMissing).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var sourceValue = sourceFields.Contains(column, StringComparer.OrdinalIgnoreCase) ? $"src.{Quote(column)}" : "NULL";
                var mismatch = ScalarLong(db, $"SELECT COUNT(*) FROM legacy.[{source}] src JOIN main.[{target}] dst ON dst.[{family.KeyColumn}] = src.[{family.KeyColumn}] WHERE {range} AND NOT (typeof(dst.{Quote(column)}) = typeof({sourceValue}) AND dst.{Quote(column)} IS {sourceValue})", BatchArgs(start, end, from, to));
                if (mismatch != 0) throw new LogMigrationException($"日志记录冲突：{target} 字段 {column}。");
            }
            throw new LogMigrationException($"日志记录冲突：{target}。");
        }
    }

    private static void EnsureBatchFullyPresent(SqlSugarClient db, string source, string target, Family family,
        IReadOnlyList<string> sourceFields, IReadOnlyList<string> optionalMissing, string range,
        long? start, long end, DateTime from, DateTime to)
    {
        var args = BatchArgs(start, end, from, to);
        var comparison = Comparison(sourceFields, optionalMissing);
        var missing = ScalarLong(db, $"SELECT COUNT(*) FROM legacy.[{source}] src LEFT JOIN main.[{target}] dst ON dst.[{family.KeyColumn}] = src.[{family.KeyColumn}] WHERE {range} AND (dst.[{family.KeyColumn}] IS NULL OR NOT ({comparison}))", args);
        if (missing != 0) throw new LogMigrationException($"目标表 {target} 未完整保存来源记录。");
    }

    private static string Comparison(IReadOnlyList<string> sourceFields, IReadOnlyList<string> optionalMissing)
        => string.Join(" AND ", sourceFields.Concat(optionalMissing).Distinct(StringComparer.OrdinalIgnoreCase).Select(column =>
        {
            var sourceValue = sourceFields.Contains(column, StringComparer.OrdinalIgnoreCase) ? $"src.{Quote(column)}" : "NULL";
            return $"typeof(dst.{Quote(column)}) = typeof({sourceValue}) AND dst.{Quote(column)} IS {sourceValue}";
        }));

    private static SugarParameter[] BatchArgs(long? start, long end, DateTime from, DateTime to)
    {
        var values = new List<SugarParameter> { new("@end", end), new("@from", from), new("@to", to) };
        if (start is not null) values.Add(new SugarParameter("@start", start.Value));
        return values.ToArray();
    }

    private static long ScalarLong(SqlSugarClient db, string sql, params SugarParameter[] parameters)
        => Convert.ToInt64(db.Ado.GetScalar(sql, parameters), CultureInfo.InvariantCulture);

    private static string EnsureTargetTable(SqlSugarClient db, string family, DateTime day, HashSet<string> initializedTables)
    {
        if (family == "usage_buckets") return family;
        var table = $"{family}_{day:yyyyMMdd}";
        if (!initializedTables.Add(table)) return table;
        switch (family)
        {
            case "request_logs": DailyLogTables.EnsureTable<RequestLogEntity>(db, day); break;
            case "resource_events": DailyLogTables.EnsureTable<ResourceEventEntity>(db, day); break;
            case "task_logs": DailyLogTables.EnsureTable<TaskLogEntity>(db, day); break;
            case "plugin_logs": DailyLogTables.EnsureTable<PluginLogEntity>(db, day); break;
        }
        return table;
    }

    private static HashSet<string> TargetColumns(string family) => EntityType(family).GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> OptionalColumns(string family)
    {
        var nullable = new NullabilityInfoContext();
        return EntityType(family).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => Nullable.GetUnderlyingType(property.PropertyType) is not null
                || !property.PropertyType.IsValueType && nullable.Create(property).WriteState == NullabilityState.Nullable)
            .Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Type EntityType(string family) => family switch
    {
        "request_logs" => typeof(RequestLogEntity), "resource_events" => typeof(ResourceEventEntity),
        "task_logs" => typeof(TaskLogEntity), "plugin_logs" => typeof(PluginLogEntity),
        "usage_buckets" => typeof(UsageBucketEntity), _ => throw new LogMigrationException("未允许的日志表族。")
    };

    private static void ValidateColumns(SqlSugarClient db, string table, Family family)
    {
        var source = ReadColumns(db, table);
        var known = TargetColumns(family.Name);
        if (source.Any(column => !known.Contains(column))) throw new LogMigrationException($"来源表 {table} 含有未知字段。");
        foreach (var required in known.Except(OptionalColumns(family.Name), StringComparer.OrdinalIgnoreCase))
            if (!source.Contains(required, StringComparer.OrdinalIgnoreCase)) throw new LogMigrationException($"来源表 {table} 缺少必需字段 {required}。");
        if (family.Name == "usage_buckets" && table != "usage_buckets") throw new LogMigrationException("usage_buckets 表名无效。");
    }

    private static void ValidateBatchRows(DataTable rows, Family family)
    {
        foreach (DataRow row in rows.Rows)
        {
            if (row[family.KeyColumn] is not string id || string.IsNullOrWhiteSpace(id))
                throw new LogMigrationException("来源日志包含无效 ID。");
            _ = GetUtcDay(row[family.TimeColumn]);
        }
    }

    private static string[] ReadColumns(SqlSugarClient db, string table)
        => db.Ado.GetDataTable($"PRAGMA legacy.table_info('{table}')").Rows.Cast<DataRow>()
            .Select(row => Convert.ToString(row["name"], CultureInfo.InvariantCulture)!).ToArray();

    private static string[] ReadTableNames(SqlSugarClient db)
        => db.Ado.GetDataTable("SELECT name FROM legacy.sqlite_master WHERE type='table'").Rows.Cast<DataRow>()
            .Select(row => Convert.ToString(row["name"], CultureInfo.InvariantCulture)!).ToArray();

    private static void ValidateReservedNames(IEnumerable<string> tables)
    {
        foreach (var name in tables)
        {
            foreach (var family in Families.Values.Where(value => value.Name != "usage_buckets"))
                if (name.StartsWith(family.Name + "_", StringComparison.Ordinal) && !IsDailyName(name, family.Name))
                    throw new LogMigrationException($"来源表名 {name} 不符合有效 UTC 日期格式。");
        }
    }

    private static bool TryFamily(string table, out Family family)
    {
        if (table == "usage_buckets") { family = Families["usage_buckets"]; return true; }
        foreach (var candidate in Families.Values.Where(value => value.Name != "usage_buckets"))
        {
            if (table == candidate.Name || IsDailyName(table, candidate.Name)) { family = candidate; return true; }
        }
        family = null!;
        return false;
    }

    private static bool IsDailyName(string table, string family)
        => table.StartsWith(family + "_", StringComparison.Ordinal)
            && table.Length == family.Length + 9
            && DateTime.TryParseExact(table[^8..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static Family GetFamily(string table)
        => TryFamily(table, out var family) ? family : throw new LogMigrationException("来源表名无效。");

    private static DateTime GetUtcDay(object value)
    {
        if (value is DBNull || !DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            throw new LogMigrationException("来源日志含有无效 UTC 日期。");
        return date.Date;
    }

    private static SqlSugarClient CreatePinnedClient(SqlSugarDatabase target)
    {
        var db = target.CreateClient();
        db.CurrentConnectionConfig.IsAutoCloseConnection = false;
        return db;
    }

    private static void Attach(SqlSugarClient db, string sourcePath)
    {
        db.Ado.Connection.Open();
        db.Ado.ExecuteCommand("ATTACH DATABASE @path AS legacy", new SugarParameter("@path", Path.GetFullPath(sourcePath)));
    }

    private static void Detach(SqlSugarClient db)
    {
        try { db.Ado.ExecuteCommand("DETACH DATABASE legacy"); }
        finally { db.Ado.Connection.Close(); }
    }

    private static string Quote(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static void ValidateDistinctPaths(string targetPath, string sourcePath)
    {
        var target = Path.GetFullPath(targetPath);
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new LogMigrationException("来源数据库不存在。");
        if (string.Equals(target, source, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new LogMigrationException("来源与目标数据库路径必须不同。");
    }

    public sealed record MigrationInventory(IReadOnlyList<string> Tables, IReadOnlyDictionary<string, long> TableRows, long Rows);
    public sealed record MigrationProgress(string Phase, string Table, long CopiedRows, long VerifiedRows, int DeletedTables, long TotalRows, int TotalTables);
    public sealed record MigrationResult(long CopiedRows, long VerifiedRows, int DeletedTables);
    private sealed record Family(string Name, string TimeColumn, string KeyColumn);
}

public sealed class LogMigrationException(string message) : InvalidOperationException(message);
