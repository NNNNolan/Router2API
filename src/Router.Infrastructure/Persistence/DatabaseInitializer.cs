using Microsoft.Extensions.Logging;

namespace Router.Infrastructure.Persistence;

/// <summary>普通表和日分表均由 CodeFirst 同步；数据回填及旧日志搬迁是独立的显式迁移。</summary>
public sealed class DatabaseInitializer(SqlSugarDatabase database, [Microsoft.Extensions.DependencyInjection.FromKeyedServices(SqlSugarDatabase.LogsKey)] SqlSugarDatabase logsDatabase, ILogger<DatabaseInitializer> logger)
{
    public void Initialize()
    {
        using var db = database.CreateClient();
        using var logs = logsDatabase.CreateClient();
        db.DbMaintenance.CreateDatabase();
        logs.DbMaintenance.CreateDatabase();
        db.CodeFirst.InitTables(
            typeof(AccountEntity),
            typeof(ProxySubscriptionEntity),
            typeof(ProxyEndpointEntity),
            typeof(PluginHttpOriginEntity),
            typeof(ModelMappingEntity));
        logs.CodeFirst.InitTables<UsageBucketEntity>();
        logs.CodeFirst.SplitTables().InitTables<RequestLogEntity>();
        logs.CodeFirst.SplitTables().InitTables<ResourceEventEntity>();
        logs.CodeFirst.SplitTables().InitTables<TaskLogEntity>();
        logs.CodeFirst.SplitTables().InitTables<PluginLogEntity>();

        db.Ado.ExecuteCommand(
            "UPDATE proxy_subscriptions SET RefreshIntervalMinutes = 60 WHERE RefreshIntervalMinutes IS NULL OR RefreshIntervalMinutes <= 0");
        db.Ado.ExecuteCommand(
            "UPDATE proxy_subscriptions SET RefreshIntervalSeconds = CAST(RefreshIntervalMinutes AS BIGINT) * 60 WHERE RefreshIntervalSeconds IS NULL OR RefreshIntervalSeconds <= 0");
        db.Ado.ExecuteCommand(
            "UPDATE accounts SET PluginKey = Platform WHERE PluginKey IS NULL OR PluginKey = ''");
        logger.LogInformation("sqlite business and log schemas synchronized independently");
    }
}
