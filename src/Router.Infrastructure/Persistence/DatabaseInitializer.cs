using Microsoft.Extensions.Logging;

namespace Router.Infrastructure.Persistence;

/// <summary>普通表和日分表均由 CodeFirst 同步；数据回填及旧日志搬迁是独立的显式迁移。</summary>
public sealed class DatabaseInitializer(SqlSugarDatabase database, ILogger<DatabaseInitializer> logger)
{
    public void Initialize()
    {
        using var db = database.CreateClient();
        db.DbMaintenance.CreateDatabase();
        db.CodeFirst.InitTables(
            typeof(AccountEntity),
            typeof(ProxySubscriptionEntity),
            typeof(ProxyEndpointEntity),
            typeof(UsageBucketEntity),
            typeof(PluginHttpOriginEntity),
            typeof(ModelMappingEntity));
        db.CodeFirst.SplitTables().InitTables<RequestLogEntity>();
        db.CodeFirst.SplitTables().InitTables<ResourceEventEntity>();
        db.CodeFirst.SplitTables().InitTables<TaskLogEntity>();
        db.CodeFirst.SplitTables().InitTables<PluginLogEntity>();

        db.Ado.ExecuteCommand(
            "UPDATE proxy_subscriptions SET RefreshIntervalMinutes = 60 WHERE RefreshIntervalMinutes IS NULL OR RefreshIntervalMinutes <= 0");
        db.Ado.ExecuteCommand(
            "UPDATE proxy_subscriptions SET RefreshIntervalSeconds = CAST(RefreshIntervalMinutes AS BIGINT) * 60 WHERE RefreshIntervalSeconds IS NULL OR RefreshIntervalSeconds <= 0");
        db.Ado.ExecuteCommand(
            "UPDATE accounts SET PluginKey = Platform WHERE PluginKey IS NULL OR PluginKey = ''");
        DailyLogTables.Migrate<RequestLogEntity>(db, "request_logs", nameof(RequestLogEntity.CreatedAtUtc));
        DailyLogTables.Migrate<ResourceEventEntity>(db, "resource_events", nameof(ResourceEventEntity.CreatedAtUtc));
        DailyLogTables.Migrate<TaskLogEntity>(db, "task_logs", nameof(TaskLogEntity.StartedAtUtc));
        DailyLogTables.Migrate<PluginLogEntity>(db, "plugin_logs", nameof(PluginLogEntity.CreatedAtUtc));
        logger.LogInformation("sqlite CodeFirst schema synchronized; logs use UTC daily tables");
    }
}
