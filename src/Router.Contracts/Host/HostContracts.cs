using System.Text.Json;
using Router.Contracts.Domain;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;

namespace Router.Contracts.Host;

/// <summary>宿主管理的物理 HttpClient 工厂。键只由节点配置决定，不包含 PluginKey。</summary>
public interface IProxyHttpClientFactory : IDisposable
{
    /// <summary>执行接口方法 CreateDirectClient。</summary>
    HttpClient CreateDirectClient(string clientName = "direct");
    /// <summary>执行接口方法 CreateClient。</summary>
    HttpClient CreateClient(ProxyEndpoint proxy, string clientName = "proxy");
}

/// <summary>
/// 宿主提供给插件的单一上游客户端。默认使用宿主当前绑定的代理，单个请求可以显式选择直连。
/// </summary>
public interface IPluginHttpClient
{
    /// <summary>执行接口方法 SendAsync。</summary>
    Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 SendAsync。</summary>

    Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        bool useProxyPool,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default);
}

/// <summary>资源租约管理器。</summary>
public interface IResourceLeaseManager
{
    /// <summary>执行接口方法 Task。</summary>
    Task<ResourceLeaseResult<T>?> AcquireAsync<T>(
        IReadOnlyList<T> resources,
        Func<T, bool> isAvailable,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        where T : class, IResource;
    /// <summary>执行接口方法 Task。</summary>

    Task<T> WithExclusiveAsync<T>(
        string resourceId,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default);
}

/// <summary>按插件维护节点短期策略的共享状态。</summary>
public interface IProxyPolicyStore
{
    /// <summary>执行接口方法 GetAsync。</summary>
    Task<ProxyPolicySnapshot?> GetAsync(
        string pluginKey,
        ProxyEndpoint proxy,
        CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 ReportAsync。</summary>

    Task ReportAsync(
        string pluginKey,
        ProxyEndpoint proxy,
        PluginAttemptResult result,
        CancellationToken cancellationToken = default);
}

/// <summary>Redis 短期运行态的最小抽象。事实数据仍由 SQLite 服务负责。</summary>
public interface ISharedKeyValueStore
{
    /// <summary>获取或设置接口成员 IsConfigured。</summary>
    bool IsConfigured { get; }
    /// <summary>执行接口方法 GetStringAsync。</summary>
    Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 SetStringAsync。</summary>
    Task<bool> SetStringAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 GetExpiryAsync。</summary>
    Task<DateTimeOffset?> GetExpiryAsync(string key, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 SetExpiryAsync。</summary>
    Task<bool> SetExpiryAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 SetExpiryIfLaterAsync。</summary>
    Task<bool> SetExpiryIfLaterAsync(
        string key,
        DateTimeOffset until,
        string value,
        CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 TryAcquireAsync。</summary>
    Task<bool> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 RemoveAsync。</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
    /// <summary>原子写入缺失键，不提供不可用后端的内存回退。</summary>
    Task<bool> PutIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Atomic writes are not supported.");
    /// <summary>原子整数递增及 TTL 更新。</summary>
    Task<long> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Atomic counters are not supported.");
    /// <summary>原子比较并替换/删除字符串。</summary>
    Task<bool> CompareExchangeAsync(string key, string? expected, string? value, TimeSpan ttl, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CAS is not supported.");
}
/// <summary>表示公开契约类型 ProxyPolicySnapshot。</summary>

public sealed record ProxyPolicySnapshot(
    ProxyPolicyState State,
    DateTimeOffset? CooldownUntil,
    int FailureCount,
    string? Reason,
    int? LastStatusCode,
    DateTimeOffset? LastFailureAt);

/// <summary>宿主选择账号、节点、租约和重试的唯一入口。</summary>
public interface IPluginAttemptExecutor
{
    /// <summary>执行接口方法 ExecuteAsync。</summary>
    Task<PluginInvocationResult> ExecuteAsync(
        string pluginKey,
        string platformName,
        IPlatformTerminal terminal,
        AdapterRequest request,
        string traceId,
        TestOverrides? overrides,
        CancellationToken cancellationToken = default);
}
/// <summary>表示公开契约类型 IPluginPolicyRegistry。</summary>

public interface IPluginPolicyRegistry
{
    /// <summary>执行接口方法 Set。</summary>
    void Set(string pluginKey, PluginProxyPolicy policy, PluginAccountPolicy? accountPolicy = null);
    /// <summary>移除插件策略。</summary>
    void Remove(string pluginKey);
    /// <summary>执行接口方法 Get。</summary>
    PluginProxyPolicy Get(string pluginKey);
    /// <summary>执行接口方法 GetAccount。</summary>
    PluginAccountPolicy GetAccount(string pluginKey);
    /// <summary>一次读取两种策略的不可变快照；旧 registry 实现仍兼容。</summary>
    PluginPolicySnapshot GetSnapshot(string pluginKey) => new(Get(pluginKey), GetAccount(pluginKey));
    /// <summary>注册特定平台的策略，旧实现回退到包级策略。</summary>
    void Set(string pluginKey, string platform, PluginProxyPolicy policy, PluginAccountPolicy? accountPolicy = null)
        => Set(pluginKey, policy, accountPolicy);
    /// <summary>读取平台级快照，旧实现仍可提供包级默认值。</summary>
    PluginPolicySnapshot GetSnapshot(string pluginKey, string platform) => GetSnapshot(pluginKey);
}

/// <summary>一次业务调用使用的账号选择/执行策略快照。</summary>
public sealed record PluginPolicySnapshot(PluginProxyPolicy Attempts, PluginAccountPolicy Accounts);

/// <summary>绑定到一个插件句柄的最小宿主服务。</summary>
public interface IPluginHost
{
    /// <summary>获取或设置接口成员 PluginKey。</summary>
    string PluginKey { get; }
    /// <summary>新插件唯一的宿主能力入口。默认实现用于保持旧宿主/插件 ABI 兼容。</summary>
    IPluginServices Services => throw new NotSupportedException("This host does not provide typed plugin services.");
    /// <summary>旧 DLL 兼容入口；新插件使用 Services.Models。</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    IModelCatalog ModelCatalog { get; }
    /// <summary>旧 DLL 兼容入口；新插件使用 Services.Accounts。</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    IAccountService Accounts { get; }
    /// <summary>旧 DLL 审计兼容入口；新插件使用绑定到自身的 Services.Log。</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    ILogSink<ResourceEvent> Audit { get; }
    /// <summary>旧 DLL 兼容入口；新插件使用 Services.Log。</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    IPluginLogSink Logs { get; }
    /// <summary>旧 DLL 兼容入口；新插件应使用 Services，不得依赖任意宿主实现。</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    Task<T> GetServiceAsync<T>() where T : class;
}

/// <summary>供插件管理端点复用宿主任务锁、取消和任务日志的执行入口。</summary>
public interface IPluginTaskInvoker
{
    /// <summary>执行接口方法 RunAsync。</summary>
    Task<bool> RunAsync(
        string pluginKey,
        string taskName,
        string? platformName = null,
        CancellationToken cancellationToken = default);
}
/// <summary>表示公开契约类型 IPluginHostFactory。</summary>

public interface IPluginHostFactory
{
    /// <summary>执行接口方法 Create。</summary>
    IPluginHost Create(string pluginKey);
    /// <summary>创建绑定到插件包所声明平台的宿主。旧实现仍可使用单参数入口。</summary>
    IPluginHost Create(string pluginKey, IReadOnlyList<string> platforms) => Create(pluginKey);
}
/// <summary>表示公开契约类型 AccountQuery。</summary>

public sealed record AccountQuery(
    string? PluginKey = null,
    string? Platform = null,
    ResourceState? State = null,
    string? Keyword = null,
    int Page = 1,
    int PageSize = 20);
/// <summary>表示公开契约类型 ProxyQuery。</summary>

public sealed record ProxyQuery(
    IReadOnlyList<string>? SubscriptionIds = null,
    ResourceState? State = null,
    int Page = 1,
    int PageSize = 20);
/// <summary>表示公开契约类型 PagedResult。</summary>

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int Total)
{
    /// <summary>获取或设置公开成员 TotalPages。</summary>
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

/// <summary>账号长期事实服务。账号不进入二级缓存。</summary>
public interface IAccountService
{
    /// <summary>执行接口方法 GetAsync。</summary>
    Task<Account?> GetAsync(string pluginKey, string id, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 GetAnyAsync。</summary>
    Task<Account?> GetAnyAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 ListAsync。</summary>
    Task<IReadOnlyList<Account>> ListAsync(string pluginKey, string? platform = null, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 QueryAsync。</summary>
    Task<PagedResult<Account>> QueryAsync(AccountQuery query, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 SaveAsync。</summary>
    Task<Account> SaveAsync(Account account, CancellationToken cancellationToken = default);
    /// <summary>按已验证字段列表更新；凭证字段仍受 CredentialVersion 保护。</summary>
    Task<Account> PatchAsync(Account account, IReadOnlyList<string> fields, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Field updates are not supported.");
    /// <summary>执行接口方法 RefreshAsync。</summary>
    Task<Account> RefreshAsync(
        string pluginKey,
        string id,
        Func<Account, CancellationToken, Task<Account>> refresh,
        CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 DeleteAsync。</summary>
    Task DeleteAsync(string pluginKey, string id, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 SetCooldownAsync。</summary>
    Task SetCooldownAsync(string pluginKey, string id, DateTimeOffset until, string reason, int? statusCode = null, CancellationToken cancellationToken = default);
    /// <summary>仅当账号仍为 Active 且冷却原因匹配时，原子清除账号冷却。</summary>
    Task<bool> ClearCooldownAsync(string pluginKey, string id, string expectedReason, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 DisableAsync。</summary>
    Task DisableAsync(string pluginKey, string id, string reason, int? statusCode = null, CancellationToken cancellationToken = default);
    /// <summary>以凭证修订号执行字段级更新；冲突返回 null，不覆盖账号状态。</summary>
    Task<Account?> CompareExchangeCredentialAsync(string pluginKey, string id, long expectedVersion, Credential credential,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Credential CAS is not supported.");
}
/// <summary>表示公开契约类型 IProxyStore。</summary>

public interface IProxyStore
{
    /// <summary>执行接口方法 GetAsync。</summary>
    Task<ProxyEndpoint?> GetAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 ListAsync。</summary>
    Task<IReadOnlyList<ProxyEndpoint>> ListAsync(CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 QueryAsync。</summary>
    Task<PagedResult<ProxyEndpoint>> QueryAsync(ProxyQuery query, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 SaveAsync。</summary>
    Task SaveAsync(ProxyEndpoint proxy, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 RemoveAsync。</summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);
}
/// <summary>表示公开契约类型 ProxyProbeResult。</summary>

public sealed record ProxyProbeResult(
    bool Success,
    int LatencyMs,
    double SpeedBytesPerSecond,
    string Status,
    string? Error = null);
/// <summary>表示公开契约类型 IProxyProbeService。</summary>

public interface IProxyProbeService
{
    /// <summary>执行接口方法 ProbeAsync。</summary>
    Task<ProxyProbeResult> ProbeAsync(ProxyEndpoint proxy, CancellationToken cancellationToken = default);
}
/// <summary>表示公开契约类型 IProxySubscriptionService。</summary>

public interface IProxySubscriptionService
{
    /// <summary>执行接口方法 ListAsync。</summary>
    Task<IReadOnlyList<ProxySubscription>> ListAsync(CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 SaveAsync。</summary>
    Task<ProxySubscription> SaveAsync(ProxySubscription subscription, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 DeleteAsync。</summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 RefreshAsync。</summary>
    Task<RefreshResult> RefreshAsync(string id, CancellationToken cancellationToken = default);
}
/// <summary>表示公开契约类型 RefreshResult。</summary>

public sealed record RefreshResult(int Added, int Updated, int Draining, int Total);
/// <summary>表示公开契约类型 IModelRouter。</summary>

public interface IModelRouter
{
    /// <summary>执行接口方法 TryResolve。</summary>
    bool TryResolve(string model, out string platform, out string normalizedModel);
}
/// <summary>表示公开契约类型 IModelCatalog。</summary>

public interface IModelCatalog
{
    /// <summary>执行接口方法 ListAsync。</summary>
    Task<IReadOnlyList<ModelDescriptor>> ListAsync(CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 ListAsync。</summary>
    Task<IReadOnlyList<ModelDescriptor>> ListAsync(string platform, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 RefreshAsync。</summary>
    Task<IReadOnlyList<ModelDescriptor>> RefreshAsync(string platform, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 Invalidate。</summary>
    void Invalidate(string? platform = null);
}

/// <summary>读取并缓存 models.dev 模型能力元数据。</summary>
public interface IModelMetadataCatalog
{
    /// <summary>执行接口方法 GetAsync。</summary>
    Task<ModelMetadataSnapshot> GetAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 FindAsync。</summary>

    Task<ModelMetadata?> FindAsync(
        string platform,
        string model,
        CancellationToken cancellationToken = default);
}
/// <summary>表示公开契约类型 IPlatformRegistry。</summary>

public interface IPlatformRegistry
{
    /// <summary>获取或设置接口成员 All。</summary>
    IReadOnlyList<PlatformRegistration> All { get; }
    /// <summary>执行接口方法 Get。</summary>
    PlatformRegistration? Get(string platform);
    /// <summary>执行接口方法 Register。</summary>
    void Register(PlatformRegistration registration);
    /// <summary>执行接口方法 SetEnabled。</summary>
    void SetEnabled(string platform, bool enabled);
    /// <summary>执行接口方法 Remove。</summary>
    void Remove(string platform);
}
/// <summary>表示公开契约类型 PlatformRegistration。</summary>

public sealed record PlatformRegistration(
    string Name,
    string PluginKey,
    string DisplayName,
    IPlatformTerminal Terminal,
    string? ProbeEndpoint = null)
{
    /// <summary>获取或设置公开成员 Enabled。</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>宿主模型缓存 TTL，零表示每次查询终端。随插件版本一同替换。</summary>
    public TimeSpan ModelCacheTtl { get; init; } = TimeSpan.FromMinutes(5);
}
/// <summary>表示公开契约类型 ILogSink。</summary>

public interface ILogSink<T>
{
    /// <summary>执行接口方法 WriteAsync。</summary>
    Task WriteAsync(T item, CancellationToken cancellationToken = default);
}

/// <summary>插件详细运行日志接收器。插件不得直接写数据库或宿主日志文件。</summary>
public interface IPluginLogSink
{
    /// <summary>执行接口方法 WriteAsync。</summary>
    Task WriteAsync(PluginLog log, CancellationToken cancellationToken = default);
}

/// <summary>插件详细日志的最低入库等级。</summary>
public sealed class PluginLogOptions
{
    /// <summary>低于此等级的插件日志不会写入数据库。</summary>
    public string MinimumPluginLogLevel { get; set; } = "Information";

    /// <summary>判断日志等级配置是否受支持。</summary>
    public static bool IsSupportedLevel(string? level)
        => level is not null
            && (level.Equals("Debug", StringComparison.OrdinalIgnoreCase)
                || level.Equals("Information", StringComparison.OrdinalIgnoreCase)
                || level.Equals("Warning", StringComparison.OrdinalIgnoreCase)
                || level.Equals("Error", StringComparison.OrdinalIgnoreCase));
}
/// <summary>表示公开契约类型 IPluginLogStore。</summary>

public interface IPluginLogStore : IPluginLogSink
{
    /// <summary>执行接口方法 QueryAsync。</summary>
    Task<PagedResult<PluginLog>> QueryAsync(
        PluginLogQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>宿主记录的插件运行明细；特定诊断事件可能包含原始请求正文和请求头。</summary>
public sealed class PluginLog
{
    /// <summary>获取或设置公开成员 Id。</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>获取或设置公开成员 PluginKey。</summary>
    public string PluginKey { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 Platform。</summary>
    public string? Platform { get; init; }
    /// <summary>获取或设置公开成员 Level。</summary>
    public string Level { get; init; } = "Information";
    /// <summary>获取或设置公开成员 EventType。</summary>
    public string EventType { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 Message。</summary>
    public string Message { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 TraceId。</summary>
    public string? TraceId { get; init; }
    /// <summary>获取或设置公开成员 TaskName。</summary>
    public string? TaskName { get; init; }
    /// <summary>获取或设置公开成员 AccountId。</summary>
    public string? AccountId { get; init; }
    /// <summary>获取或设置公开成员 Model。</summary>
    public string? Model { get; init; }
    /// <summary>获取或设置公开成员 StatusCode。</summary>
    public int? StatusCode { get; init; }
    /// <summary>获取或设置公开成员 DurationMs。</summary>
    public int? DurationMs { get; init; }
    /// <summary>获取或设置公开成员 DetailsJson。</summary>
    public string? DetailsJson { get; init; }
    /// <summary>获取或设置公开成员 CreatedAt。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>插件快速写入宿主详细日志的辅助方法。</summary>
public static class PluginLogExtensions
{
    /// <summary>执行公开方法 LogAsync。</summary>
    public static Task LogAsync(
        this IPluginHost host,
        string platform,
        string eventType,
        string message,
        string level = "Debug",
        string? traceId = null,
        string? taskName = null,
        string? accountId = null,
        string? model = null,
        int? statusCode = null,
        int? durationMs = null,
        object? details = null,
        CancellationToken cancellationToken = default)
        => host.Logs.WriteAsync(new PluginLog
        {
            PluginKey = host.PluginKey,
            Platform = platform,
            EventType = eventType,
            Message = message,
            Level = level,
            TraceId = traceId,
            TaskName = taskName,
            AccountId = accountId,
            Model = model,
            StatusCode = statusCode,
            DurationMs = durationMs,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details)
        }, cancellationToken);

    /// <summary>通过类型化能力写入本插件日志；不允许指定其他 PluginKey。</summary>
    public static Task LogAsync(
        this IPluginServices services,
        string platform,
        string eventType,
        string message,
        string level = "Debug",
        string? traceId = null,
        string? taskName = null,
        string? accountId = null,
        string? model = null,
        int? statusCode = null,
        int? durationMs = null,
        object? details = null,
        CancellationToken cancellationToken = default)
        => services.Log.WriteAsync(new PluginLog
        {
            PluginKey = services.PluginKey, Platform = platform, EventType = eventType, Message = message,
            Level = level, TraceId = traceId, TaskName = taskName, AccountId = accountId, Model = model,
            StatusCode = statusCode, DurationMs = durationMs,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details)
        }, cancellationToken);
}
/// <summary>表示公开契约类型 PluginLogQuery。</summary>

public sealed record PluginLogQuery(
    int Page = 1,
    int PageSize = 20,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? PluginKey = null,
    string? Platform = null,
    string? TaskName = null,
    string? Level = null,
    string? EventType = null,
    string? Keyword = null);

/// <summary>删除超过保留期限的宿主数据日志。</summary>
public interface ILogRetentionStore
{
    /// <summary>执行接口方法 DeleteBeforeAsync。</summary>
    Task<int> DeleteBeforeAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default);
}
/// <summary>表示公开契约类型 IRequestLogStore。</summary>

public interface IRequestLogStore
{
    /// <summary>执行接口方法 QueryAsync。</summary>
    Task<PagedResult<RequestLog>> QueryAsync(RequestLogQuery query, CancellationToken cancellationToken = default);
}
/// <summary>表示公开契约类型 IUsageBucketStore。</summary>

public interface IUsageBucketStore
{
    /// <summary>执行接口方法 RecordAsync。</summary>
    Task RecordAsync(RequestLog log, CancellationToken cancellationToken = default);
}
/// <summary>表示公开契约类型 TaskLog。</summary>

public sealed class TaskLog
{
    /// <summary>获取或设置公开成员 Id。</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>获取或设置公开成员 PluginKey。</summary>
    public string PluginKey { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 Platform。</summary>
    public string? Platform { get; init; }
    /// <summary>获取或设置公开成员 TaskName。</summary>
    public string TaskName { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 AccountId。</summary>
    public string? AccountId { get; init; }
    /// <summary>获取或设置公开成员 Status。</summary>
    public string Status { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 Message。</summary>
    public string? Message { get; init; }
    /// <summary>获取或设置公开成员 Error。</summary>
    public string? Error { get; init; }
    /// <summary>获取或设置公开成员 DetailsJson。</summary>
    public string? DetailsJson { get; init; }
    /// <summary>获取或设置公开成员 DurationMs。</summary>
    public int DurationMs { get; init; }
    /// <summary>获取或设置公开成员 StartedAt。</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>获取或设置公开成员 FinishedAt。</summary>
    public DateTimeOffset? FinishedAt { get; init; }
}
/// <summary>表示公开契约类型 TaskLogQuery。</summary>

public sealed record TaskLogQuery(
    int Page = 1,
    int PageSize = 20,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? PluginKey = null,
    string? Platform = null,
    string? TaskName = null,
    string? Status = null,
    string? Keyword = null);
/// <summary>表示公开契约类型 ITaskLogStore。</summary>

public interface ITaskLogStore
{
    /// <summary>执行接口方法 WriteAsync。</summary>
    Task WriteAsync(TaskLog log, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 UpdateAsync。</summary>
    Task UpdateAsync(TaskLog log, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 QueryAsync。</summary>
    Task<PagedResult<TaskLog>> QueryAsync(TaskLogQuery query, CancellationToken cancellationToken = default);
}
/// <summary>表示公开契约类型 RequestLogQuery。</summary>

public sealed record RequestLogQuery(
    int Page = 1,
    int PageSize = 20,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? Platform = null,
    string? Model = null,
    string? Keyword = null);
/// <summary>表示公开契约类型 ResourceEvent。</summary>

public sealed class ResourceEvent
{
    /// <summary>获取或设置公开成员 Id。</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>获取或设置公开成员 ResourceType。</summary>
    public string ResourceType { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 ResourceId。</summary>
    public string ResourceId { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 PluginKey。</summary>
    public string PluginKey { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 EventType。</summary>
    public string EventType { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 Error。</summary>
    public string? Error { get; init; }
    /// <summary>获取或设置公开成员 CreatedAt。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
/// <summary>表示公开契约类型 RequestLog。</summary>

public sealed class RequestLog
{
    /// <summary>获取或设置公开成员 Id。</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>获取或设置公开成员 TraceId。</summary>
    public string TraceId { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 Platform。</summary>
    public string? Platform { get; init; }
    /// <summary>获取或设置公开成员 Model。</summary>
    public string? Model { get; init; }
    /// <summary>获取或设置公开成员 Success。</summary>
    public bool Success { get; init; }
    /// <summary>获取或设置公开成员 StatusCode。</summary>
    public int StatusCode { get; init; }
    /// <summary>获取或设置公开成员 DurationMs。</summary>
    public int DurationMs { get; init; }
    /// <summary>获取或设置公开成员 TtfbMs。</summary>
    public int? TtfbMs { get; init; }
    /// <summary>获取或设置公开成员 Usage。</summary>
    public Usage? Usage { get; init; }
    /// <summary>获取或设置公开成员 AttemptDetails。</summary>
    public IReadOnlyList<RequestAttemptDetail> AttemptDetails { get; init; } = [];
    /// <summary>获取或设置公开成员 CreatedAt。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
/// <summary>表示公开契约类型 RealtimeSnapshot。</summary>

public sealed record RealtimeSnapshot(
    DateTimeOffset Timestamp,
    int ActiveConnections,
    int ActiveStreaming,
    long RequestsToday,
    long TokensToday,
    long PromptTokensToday,
    long CompletionTokensToday,
    long FailuresToday,
    double AvgLatencyMs,
    double P95LatencyMs,
    double TtfbP95Ms,
    IReadOnlyDictionary<string, PlatformSnapshot> ByPlatform);
/// <summary>表示公开契约类型 PlatformSnapshot。</summary>

public sealed record PlatformSnapshot(long Requests, long Tokens, double AvgLatencyMs);
/// <summary>表示公开契约类型 IRealtimeMetrics。</summary>

public interface IRealtimeMetrics
{
    /// <summary>执行接口方法 OnRequestStarted。</summary>
    void OnRequestStarted(string platform);
    /// <summary>执行接口方法 OnRequestCompleted。</summary>
    void OnRequestCompleted(string platform, int durationMs, bool success, Usage? usage);
    /// <summary>执行接口方法 OnStreamStarted。</summary>
    void OnStreamStarted(string platform);
    /// <summary>执行接口方法 OnStreamEnded。</summary>
    void OnStreamEnded(string platform);
    /// <summary>执行接口方法 Snapshot。</summary>
    RealtimeSnapshot Snapshot();
}
/// <summary>表示公开契约类型 AnalyticsQuery。</summary>

public sealed record AnalyticsQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? Platform = null,
    string? Model = null,
    string Bucket = "hour");
/// <summary>表示公开契约类型 AnalyticsBucket。</summary>

public sealed record AnalyticsBucket(
    DateTimeOffset StartUtc,
    long Requests,
    long Successes,
    long Failures,
    long Tokens,
    double AverageLatencyMs,
    double P95LatencyMs);
/// <summary>表示公开契约类型 AnalyticsReport。</summary>

public sealed record AnalyticsReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long Requests,
    long Successes,
    long Failures,
    long Tokens,
    double AverageLatencyMs,
    double P95LatencyMs,
    IReadOnlyList<AnalyticsBucket> Buckets,
    IReadOnlyDictionary<string, long> ByPlatform);
/// <summary>表示公开契约类型 IAnalyticsService。</summary>

public interface IAnalyticsService
{
    /// <summary>执行接口方法 QueryAsync。</summary>
    Task<AnalyticsReport> QueryAsync(AnalyticsQuery query, CancellationToken cancellationToken = default);
}
/// <summary>表示公开契约类型 IPluginCatalog。</summary>

public interface IPluginCatalog
{
    /// <summary>获取或设置接口成员 All。</summary>
    IReadOnlyList<PluginDescriptor> All { get; }
    /// <summary>执行接口方法 Get。</summary>
    PluginDescriptor? Get(string pluginKey);
    /// <summary>执行接口方法 GetMainPage。</summary>
    PluginMainPage? GetMainPage(string pluginKey);
    /// <summary>设置插件启用状态；返回 null 表示插件不存在。</summary>
    Task<PluginDescriptor?> SetEnabledAsync(string pluginKey, bool enabled, CancellationToken cancellationToken = default);
    /// <summary>执行接口方法 ReloadAsync。</summary>
    Task ReloadAsync(string? name, CancellationToken cancellationToken = default);
}
/// <summary>表示公开契约类型 PluginDescriptor。</summary>

public sealed record PluginDescriptor(
    string PluginKey,
    string Name,
    string Version,
    string State,
    string DirectoryPath,
    DateTimeOffset LoadedAt,
    int InFlight,
    bool HasMainPage = false,
    string? MainPageTitle = null,
    string? MainPageVersion = null,
    IReadOnlyList<PluginTaskDescriptor>? Tasks = null,
    IReadOnlyList<string>? Routes = null)
{
    /// <summary>插件运行时类型；现有 DLL 插件默认为 dotnet。</summary>
    public string Runtime { get; init; } = "dotnet";
}
/// <summary>表示公开契约类型 PluginHttpContext。</summary>

public sealed class PluginHttpContext
{
    /// <summary>获取或设置公开成员 PluginKey。</summary>
    public string PluginKey { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 Platform。</summary>
    public string Platform { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 CancellationToken。</summary>
    public CancellationToken CancellationToken { get; init; }
    /// <summary>获取或设置公开成员 Query。</summary>
    public IReadOnlyDictionary<string, string> Query { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>获取或设置公开成员 Body。</summary>
    public object? Body { get; init; }
    /// <summary>执行公开方法 Ok。</summary>

    public PluginResult Ok(object? value = null) => PluginResult.Json(200, value);
    /// <summary>执行公开方法 NoContent。</summary>
    public PluginResult NoContent() => new() { StatusCode = 204 };
    /// <summary>执行公开方法 BadRequest。</summary>
    public PluginResult BadRequest(string error) => PluginResult.Json(400, new { error });
    /// <summary>执行公开方法 Unauthorized。</summary>
    public PluginResult Unauthorized(string error) => PluginResult.Json(401, new { error });
    /// <summary>执行公开方法 Json。</summary>
    public PluginResult Json(int statusCode, object? value) => PluginResult.Json(statusCode, value);
}
/// <summary>表示公开契约类型 PluginResult。</summary>

public sealed class PluginResult
{
    /// <summary>获取或设置公开成员 StatusCode。</summary>
    public int StatusCode { get; init; }
    /// <summary>获取或设置公开成员 Body。</summary>
    public object? Body { get; init; }
    /// <summary>获取或设置公开成员 ContentType。</summary>
    public string? ContentType { get; init; }
    /// <summary>执行公开方法 Json。</summary>

    public static PluginResult Json(int statusCode, object? body)
        => new() { StatusCode = statusCode, Body = body, ContentType = "application/json" };
}
