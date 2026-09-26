using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Infrastructure;

/// <summary>基础设施依赖注入注册。</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddRouterInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection("Database"));
        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection("Redis"));
        services.AddOptions<PluginLogOptions>()
            .Bind(configuration.GetSection("Logging"));
        services.AddOptions<PluginExecutionOptions>()
            .Bind(configuration.GetSection("Plugins:Execution"))
            .Validate(options => options.IsValid(), "Plugin execution budgets must be positive, finite and at most one day.")
            .ValidateOnStart();

        services.AddSingleton<SqlSugarDatabase>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<RedisKeyValueStore>();
        services.AddSingleton<ISharedKeyValueStore>(sp => sp.GetRequiredService<RedisKeyValueStore>());
        services.AddSingleton<IProxyPolicyStore, RedisProxyPolicyStore>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IProxyStore, ProxyStore>();
        services.AddSingleton<IProxyProbeService, ProxyProbeService>();
        services.AddSingleton<IProxySubscriptionService, ProxySubscriptionService>();
        services.AddSingleton<IResourceLeaseManager, ResourceLeaseManager>();
        services.AddSingleton<ProxyTransportFactory>();
        services.AddSingleton<PluginHttpOriginStore>();
        services.AddSingleton<PluginResiliencePipelines>();
        services.AddSingleton<IProxyHttpClientFactory, ProxyHttpClientFactory>();
        services.AddSingleton<IProxyPoolHttpClientFactory, ProxyPoolHttpClientFactory>();
        services.AddSingleton<IPluginAttemptExecutor, PluginAttemptExecutor>();
        services.AddSingleton<IPluginPolicyRegistry, PluginPolicyRegistry>();
        services.AddSingleton<IPlatformRegistry, PlatformRegistry>();
        services.AddSingleton<IModelRouter, ModelRouter>();
        services.AddSingleton<IModelMetadataCatalog, ModelMetadataCatalog>();
        services.AddSingleton<IModelCatalog, ModelCatalog>();
        services.AddSingleton<IRealtimeMetrics, RealtimeMetrics>();
        services.AddSingleton<IAnalyticsService, AnalyticsService>();
        services.AddSingleton<ILogSink<ResourceEvent>, ResourceEventLogSink>();
        services.AddSingleton<RequestLogStore>();
        services.AddSingleton<ILogSink<RequestLog>>(sp => sp.GetRequiredService<RequestLogStore>());
        services.AddSingleton<IRequestLogStore>(sp => sp.GetRequiredService<RequestLogStore>());
        services.AddSingleton<IUsageBucketStore, UsageBucketStore>();
        services.AddSingleton<TaskLogStore>();
        services.AddSingleton<ITaskLogStore>(sp => sp.GetRequiredService<TaskLogStore>());
        services.AddSingleton<PluginLogStore>();
        services.AddSingleton<IPluginLogSink>(sp => sp.GetRequiredService<PluginLogStore>());
        services.AddSingleton<IPluginLogStore>(sp => sp.GetRequiredService<PluginLogStore>());
        services.AddSingleton<ILogRetentionStore, LogRetentionStore>();
        services.AddSingleton<IPluginHostFactory, PluginHostFactory>();

        services.AddHttpClient();
        services.AddHttpClient("models-dev", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Router2API/1.0 model-plaza");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });
        return services;
    }
}
