using Router.Contracts.Domain;

namespace Router.Contracts.Host;

/// <summary>
/// 绑定到一个插件版本的类型化宿主能力。C# 和 JS 使用相同的资源命名空间；
/// 此接口是架构边界，不是原生 DLL 的安全沙箱。
/// </summary>
public interface IPluginServices
{
    /// <summary>宿主绑定的插件标识，不能由调用参数覆盖。</summary>
    string PluginKey { get; }
    /// <summary>仅操作本插件的账号。</summary>
    IPluginAccounts Accounts { get; }
    /// <summary>无账号的 HTTP 能力；模型尝试使用 PluginAttemptContext.HttpClient。</summary>
    IPluginHttpServices Http { get; }
    /// <summary>本版本内存状态和本插件共享状态。</summary>
    PluginStateServices State { get; }
    /// <summary>本插件模型目录及只读公共模型元数据。</summary>
    IPluginModels Models { get; }
    /// <summary>本插件任务执行和任务明细日志。</summary>
    IPluginTasks Tasks { get; }
    /// <summary>本插件版本的命名后台任务，支持去重、状态、进度和取消。</summary>
    IPluginJobs Jobs => throw new NotSupportedException("Background jobs are not supported by this host.");
    /// <summary>绑定到本插件的诊断日志接收器。</summary>
    IPluginLogSink Log { get; }
    /// <summary>宿主提供的不可变执行预算快照。</summary>
    PluginExecutionOptions Execution { get; }
}

/// <summary>不暴露全局查询或 PluginKey 参数的账号能力。</summary>
public interface IPluginAccounts
{
    /// <summary>读取本插件账号；不存在时返回 null。</summary>
    Task<Account?> GetAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>列出本插件账号，可进一步限定平台。</summary>
    Task<IReadOnlyList<Account>> ListAsync(string? platform = null, CancellationToken cancellationToken = default);
    /// <summary>保存本插件账号，拒绝变更资源归属。</summary>
    Task<Account> SaveAsync(Account account, CancellationToken cancellationToken = default);
    /// <summary>更新明确列出的字段，保留并发发生的未指定状态变化。字段名使用账户 JSON 路径。</summary>
    Task<Account> PatchAsync(Account account, IReadOnlyList<string> fields, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Field updates are not supported.");
    /// <summary>刷新凭据；回调不得改变账号标识、插件或平台。</summary>
    Task<Account> RefreshAsync(string id, Func<Account, CancellationToken, Task<Account>> refresh, CancellationToken cancellationToken = default);
    /// <summary>删除本插件账号及其短期状态。</summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>设置账号冷却。模型尝试应优先返回显式决策，避免重复执行动作。</summary>
    Task SetCooldownAsync(string id, DateTimeOffset until, string reason, int? statusCode = null, CancellationToken cancellationToken = default);
    /// <summary>仅当账号仍启用且原因匹配时清除冷却。</summary>
    Task<bool> ClearCooldownAsync(string id, string expectedReason, CancellationToken cancellationToken = default);
    /// <summary>停用本插件账号。</summary>
    Task DisableAsync(string id, string reason, int? statusCode = null, CancellationToken cancellationToken = default);
    /// <summary>仅在凭证修订号仍匹配时更新凭证，不覆盖账号的停用/冷却等状态。</summary>
    Task<Account?> CompareExchangeCredentialAsync(string id, long expectedVersion, Credential credential,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Credential CAS is not supported.");
    /// <summary>串行读取最新账号并刷新凭证，提交时仍执行 CAS。回调不得修改资源身份。</summary>
    Task<Account> RefreshCredentialAsync(string id, Func<Account, CancellationToken, Task<Credential>> refresh,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Credential refresh is not supported.");
}

/// <summary>任务、模型发现等无账号场景可使用的 HTTP 能力。</summary>
public interface IPluginHttpServices
{
    /// <summary>独立代理池；不换账号、不处理业务状态码、不隐式直连。</summary>
    IProxyPoolHttpClientFactory Pool { get; }
    /// <summary>创建调用方拥有的显式直连客户端，无共享 Cookie、系统代理或隐式重试。</summary>
    /// <param name="options">超时和重定向设置，默认禁止重定向。</param>
    /// <returns>调用方必须释放的客户端。</returns>
    HttpClient CreateDirectClient(PluginHttpClientOptions? options = null);
    /// <summary>列出本插件经管理员端点批准的额外 HTTP origin。</summary>
    Task<IReadOnlyList<string>> GetApprovedOriginsAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Origin approvals are not supported.");
    /// <summary>保存本插件的明确 origin 授权；脚本桥仅允许管理员端点调用。</summary>
    Task ApproveOriginAsync(string origin, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Origin approvals are not supported.");
    /// <summary>撤销本插件的额外 origin 授权。</summary>
    Task RevokeOriginAsync(string origin, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Origin approvals are not supported.");
}

/// <summary>直连客户端的有限传输选项。</summary>
public sealed record PluginHttpClientOptions
{
    /// <summary>是否自动跟随重定向；脚本的 origin 白名单要求此值为 false。</summary>
    public bool AllowAutoRedirect { get; init; }
    /// <summary>发送/缓冲超时，不代表返回响应头后的流生命周期。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>两种明确的状态生命周期，不允许访问宿主账号冷却或任务锁键。</summary>
/// <param name="Local">本插件版本的有界内存状态，卸载即释放。</param>
/// <param name="Shared">带插件命名空间的可选 Redis 状态，不提供内存回退。</param>
public sealed record PluginStateServices(IPluginStateStore Local, IPluginStateStore Shared);

/// <summary>有界的字符串状态。序列化由调用方完成，所有写入必须有 TTL。</summary>
public interface IPluginStateStore
{
    /// <summary>后端是否已配置；不代表当前网络一定可用。</summary>
    bool IsAvailable { get; }
    /// <summary>读取本插件键；不存在或过期时返回 null。</summary>
    Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default);
    /// <summary>写入本插件键，超过键、值或 TTL 配额时拒绝。</summary>
    Task<bool> SetStringAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default);
    /// <summary>删除本插件键。</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
    /// <summary>仅当键不存在时写入带 TTL 的值。</summary>
    Task<bool> PutIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Atomic state writes are not supported.");
    /// <summary>原子递增整数，并设置 TTL；非整数值不会被静默覆盖。</summary>
    Task<long> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Atomic counters are not supported.");
    /// <summary>比较原始字符串值后替换；expected 为 null 表示缺失，value 为 null 表示删除。</summary>
    Task<bool> CompareExchangeAsync(string key, string? expected, string? value, TimeSpan ttl, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("State CAS is not supported.");
    /// <summary>读取键的到期时间，缺失时返回 null。</summary>
    Task<DateTimeOffset?> GetExpiryAsync(string key, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("State expiry is not supported.");
}

/// <summary>本插件模型目录及公共只读能力元数据。</summary>
public interface IPluginModels
{
    /// <summary>读取属于本插件的平台模型。</summary>
    Task<IReadOnlyList<ModelDescriptor>> ListAsync(string platform, CancellationToken cancellationToken = default);
    /// <summary>强制刷新属于本插件的平台模型。</summary>
    Task<IReadOnlyList<ModelDescriptor>> RefreshAsync(string platform, CancellationToken cancellationToken = default);
    /// <summary>使属于本插件的平台缓存失效。</summary>
    void Invalidate(string platform);
    /// <summary>读取宿主管理的公共模型元数据。</summary>
    Task<ModelMetadataSnapshot> GetMetadataAsync(CancellationToken cancellationToken = default);
}

/// <summary>只允许调用本插件注册任务的能力。</summary>
public interface IPluginTasks
{
    /// <summary>使用宿主任务锁执行已注册任务；取消由调用令牌传递。</summary>
    Task<bool> RunAsync(string taskName, string? platform = null, CancellationToken cancellationToken = default);
    /// <summary>写入绑定到本插件的账号任务明细。</summary>
    Task WriteLogAsync(TaskLog log, CancellationToken cancellationToken = default);
}

/// <summary>宿主执行预算；HTTP 发送超时与流读取截止时间分别管理。</summary>
public sealed record PluginExecutionOptions
{
    /// <summary>JS 调用排队时间上限。</summary>
    public TimeSpan QueueTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>单次尝试建立响应前的上限，插件只能进一步缩短。</summary>
    public TimeSpan SetupTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>业务尝试序列建立响应前的总上限。</summary>
    public TimeSpan TotalSetupTimeout { get; init; } = TimeSpan.FromSeconds(180);
    /// <summary>进入响应体阶段后的整体截止时间，不因重试或 chunk 到达而续期。</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>脚本 HTTP source 默认空闲读取超时。</summary>
    public TimeSpan ReadIdleTimeout { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>同步 SSE mapper 的单次预算。</summary>
    public TimeSpan MapperBudget { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>同步非流式聚合 finalizer 的预算。</summary>
    public TimeSpan CompletionMapperBudget { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>管理端点的整体预算。</summary>
    public TimeSpan EndpointTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>热更新时旧版本的排空等待上限。</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>检查配置是否包含有限、正数的执行预算。</summary>
    public bool IsValid() => new[]
    {
        QueueTimeout, SetupTimeout, TotalSetupTimeout, ResponseTimeout, ReadIdleTimeout,
        MapperBudget, CompletionMapperBudget, EndpointTimeout, DrainTimeout
    }.All(value => value > TimeSpan.Zero && value <= TimeSpan.FromDays(1));
}
