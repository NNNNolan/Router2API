using Microsoft.Extensions.Logging;

namespace Router.Infrastructure.Persistence;

/// <summary>使用 SqlSugar CodeFirst 初始化并增量补齐 SQLite 结构。</summary>
public sealed class DatabaseInitializer(SqlSugarDatabase database, ILogger<DatabaseInitializer> logger)
{
    public void Initialize()
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);

        using var db = database.CreateClient();
        db.DbMaintenance.CreateDatabase();
        db.CodeFirst.InitTables(
            typeof(AccountEntity),
            typeof(ProxySubscriptionEntity),
            typeof(ProxyEndpointEntity),
            typeof(RequestLogEntity),
            typeof(UsageBucketEntity),
            typeof(ResourceEventEntity),
            typeof(TaskLogEntity),
            typeof(PluginLogEntity),
            typeof(PluginHttpOriginEntity));

        EnsureColumn("accounts", "PluginKey", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("accounts", "CooldownUntilUtc", "DATETIME NULL");
        EnsureColumn("accounts", "DisabledUntilUtc", "DATETIME NULL");
        EnsureColumn("accounts", "Reason", "TEXT NULL");
        EnsureColumn("accounts", "LastStatusCode", "INTEGER NULL");
        EnsureColumn("accounts", "ConsecutiveFailures", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("proxy_subscriptions", "RefreshIntervalMinutes", "INTEGER NOT NULL DEFAULT 60");
        EnsureColumn("proxy_subscriptions", "RefreshIntervalSeconds", "BIGINT NULL");
        EnsureColumn("proxy_endpoints", "LatencyMs", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("proxy_endpoints", "AverageLatencyMs", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("proxy_endpoints", "AverageSpeedBytesPerSecond", "REAL NOT NULL DEFAULT 0");
        EnsureColumn("proxy_endpoints", "CompositeScore", "REAL NOT NULL DEFAULT 0");
        EnsureColumn("proxy_endpoints", "ConsecutiveFailures", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("proxy_endpoints", "LastProbeSuccessAtUtc", "DATETIME NULL");
        EnsureColumn("proxy_endpoints", "ProbeConsecutiveFailures", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("proxy_endpoints", "ProbeStatus", "TEXT NOT NULL DEFAULT 'Unknown'");
        EnsureColumn("proxy_endpoints", "ProbeConfigurationVersion", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("proxy_endpoints", "ProbeError", "TEXT NULL");
        EnsureColumn("request_logs", "AttemptDetailsJson", "TEXT NULL");
        EnsureColumn("task_logs", "AccountId", "TEXT NULL");
        EnsureColumn("task_logs", "Message", "TEXT NULL");
        EnsureColumn("task_logs", "DetailsJson", "TEXT NULL");

        RemoveLegacySchema();

        db.Ado.ExecuteCommand(
            "UPDATE proxy_subscriptions SET RefreshIntervalMinutes = 60 WHERE RefreshIntervalMinutes IS NULL OR RefreshIntervalMinutes <= 0");
        db.Ado.ExecuteCommand(
            "UPDATE proxy_subscriptions SET RefreshIntervalSeconds = CAST(RefreshIntervalMinutes AS BIGINT) * 60 WHERE RefreshIntervalSeconds IS NULL OR RefreshIntervalSeconds <= 0");
        db.Ado.ExecuteCommand(
            "UPDATE accounts SET PluginKey = Platform WHERE PluginKey IS NULL OR PluginKey = ''");
        logger.LogInformation("sqlite database initialized with host resource schema");
    }

    private void EnsureColumn(string tableName, string columnName, string definition)
    {
        using var db = database.CreateClient();
        var columns = db.Ado.SqlQuery<SqliteColumnInfo>($"PRAGMA table_info('{tableName}')");
        if (columns.Any(column => string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase)))
            return;

        db.Ado.ExecuteCommand($"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}");
    }

    private void RemoveLegacySchema()
    {
        using var db = database.CreateClient();
        var subscriptionColumns = db.Ado.SqlQuery<SqliteColumnInfo>(
            "PRAGMA table_info('proxy_subscriptions')");
        if (subscriptionColumns.Any(column =>
                string.Equals(column.Name, "TargetPluginKeysJson", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                db.Ado.ExecuteCommand(
                    "ALTER TABLE proxy_subscriptions DROP COLUMN TargetPluginKeysJson");
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "failed to remove legacy proxy subscription plugin binding column");
            }
        }

        db.Ado.ExecuteCommand("DROP TABLE IF EXISTS resource_cooldowns");
    }

    private sealed class SqliteColumnInfo
    {
        public string Name { get; set; } = string.Empty;
    }
}
