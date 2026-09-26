using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using System.Text.Json;

namespace Router.Contracts.Plugins;

/// <summary>声明管道中间件或终端插件。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class PipelinePluginAttribute(PipelineStage stage, int order, string? name = null) : Attribute
{
    /// <summary>获取或设置公开成员 Stage。</summary>
    public PipelineStage Stage { get; } = stage;
    /// <summary>获取或设置公开成员 Order。</summary>
    public int Order { get; } = order;
    /// <summary>获取或设置公开成员 Name。</summary>
    public string? Name { get; } = name;
}

/// <summary>声明插件的平台元数据。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class PlatformAdapterAttribute(string name) : Attribute
{
    /// <summary>获取或设置公开成员 Name。</summary>
    public string Name { get; } = name;
    /// <summary>获取或设置公开成员 PluginKey。</summary>
    public string PluginKey { get; init; } = name;
    /// <summary>获取或设置公开成员 DisplayName。</summary>
    public string DisplayName { get; init; } = name;
    /// <summary>获取或设置公开成员 ProbeEndpoint。</summary>
    public string? ProbeEndpoint { get; init; }
}

/// <summary>声明凭证模式。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CredentialSchemaAttribute(CredentialKind kind) : Attribute
{
    /// <summary>获取或设置公开成员 Kind。</summary>
    public CredentialKind Kind { get; } = kind;
}

/// <summary>声明模型缓存行为。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ModelCacheAttribute : Attribute
{
    /// <summary>获取或设置公开成员 Disabled。</summary>
    public bool Disabled { get; init; }
    /// <summary>获取或设置公开成员 TtlSeconds。</summary>
    public int TtlSeconds { get; init; } = 300;
}

/// <summary>声明模型别名。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ModelAliasAttribute(string alias, string target) : Attribute
{
    /// <summary>获取或设置公开成员 Alias。</summary>
    public string Alias { get; } = alias;
    /// <summary>获取或设置公开成员 Target。</summary>
    public string Target { get; } = target;
}

/// <summary>声明回退平台名称。</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class FallbackAttribute(params string[] platforms) : Attribute
{
    /// <summary>获取或设置公开成员 Platforms。</summary>
    public IReadOnlyList<string> Platforms { get; } = platforms;
}

/// <summary>声明后台定时任务。</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ScheduledTaskAttribute(string name, string cron) : Attribute
{
    /// <summary>获取或设置公开成员 Name。</summary>
    public string Name { get; } = name;
    /// <summary>获取或设置公开成员 Cron。</summary>
    public string Cron { get; } = cron;
    /// <summary>获取或设置公开成员 Description。</summary>
    public string? Description { get; init; }
}

/// <summary>传递给插件定时任务的上下文。</summary>
public sealed record PluginScheduledTaskContext(
    string PlatformName,
    string PluginKey,
    CancellationToken CancellationToken);

/// <summary>插件终端公开的可执行定时任务注册信息。</summary>
public sealed record ScheduledTaskRegistration(
    string Name,
    string Cron,
    Func<PluginScheduledTaskContext, Task> ExecuteAsync,
    string? Description = null);

/// <summary>可由管理端或插件启动的命名后台任务；执行与当前请求的 Engine/令牌分离。</summary>
public sealed record PluginJobRegistration(string Name, string Platform,
    Func<PluginJobContext, Task<JsonElement?>> ExecuteAsync, TimeSpan Timeout, string? Description = null);

/// <summary>宿主创建的后台任务上下文。</summary>
public sealed record PluginJobContext(string Id, string PluginKey, string Platform, JsonElement? Input,
    Action<JsonElement?> ReportProgress, CancellationToken CancellationToken);

/// <summary>提供后台任务的终端可选实现能力。</summary>
public interface IPluginScheduledTaskProvider
{
    /// <summary>获取或设置接口成员 ScheduledTasks。</summary>
    IReadOnlyList<ScheduledTaskRegistration> ScheduledTasks { get; }
}

/// <summary>任务的只读清单信息，供管理端展示。</summary>
public sealed record PluginTaskDescriptor(
    string Name,
    string Cron,
    string? Description = null);

/// <summary>插件启动上下文。</summary>
public sealed record PluginStartContext(string PluginKey, IPluginHost Host);

/// <summary>插件主页面。宿主缓存一份 HTML，并通过隔离页面呈现。</summary>
public sealed record PluginMainPage(
    string Title,
    string Html,
    string Version = "1");
/// <summary>表示公开契约类型 IPluginMainPageProvider。</summary>

public interface IPluginMainPageProvider
{
    /// <summary>执行接口方法 GetMainPage。</summary>
    PluginMainPage GetMainPage();
}

/// <summary>可选的代码优先插件生命周期。</summary>
public interface IPluginModule
{
    /// <summary>执行接口方法 Configure。</summary>
    void Configure(IPluginBuilder builder);
    /// <summary>执行接口方法 StartAsync。</summary>
    ValueTask StartAsync(PluginStartContext context, CancellationToken cancellationToken);
    /// <summary>执行接口方法 StopAsync。</summary>
    ValueTask StopAsync(CancellationToken cancellationToken);
}
/// <summary>表示公开契约类型 IPluginBuilder。</summary>

public interface IPluginBuilder
{
    /// <summary>执行接口方法 ProxyPolicy。</summary>
    void ProxyPolicy(Action<PluginProxyPolicyBuilder> configure);
    /// <summary>执行接口方法 AccountPolicy。</summary>
    void AccountPolicy(Action<PluginAccountPolicyBuilder> configure);
    /// <summary>执行接口方法 ScheduledTask。</summary>
    void ScheduledTask(ScheduledTaskRegistration registration);
    /// <summary>声明命名后台任务；不会在安装/配置时执行任务。</summary>
    void Job(PluginJobRegistration registration) => throw new NotSupportedException("Background jobs are not supported.");
}
/// <summary>表示公开契约类型 PluginProxyPolicyBuilder。</summary>

public sealed class PluginProxyPolicyBuilder
{
    internal Func<PluginAttemptResult, bool>? RetryPredicate { get; private set; }
    internal Func<PluginAttemptResult, bool>? CooldownNodePredicate { get; private set; }
    internal Func<PluginAttemptResult, bool>? CooldownAccountPredicate { get; private set; }
    internal Func<PluginAttemptResult, bool>? DisableAccountPredicate { get; private set; }

    internal int MaxAttemptCount { get; private set; } = 3;
    private Func<PluginAttemptDecision>? _transportFailure;
    /// <summary>获取或设置公开成员 AttemptTimeout。</summary>
    public TimeSpan AttemptTimeout { get; private set; } = TimeSpan.FromSeconds(60);
    /// <summary>获取或设置公开成员 TotalTimeout。</summary>
    public TimeSpan TotalTimeout { get; private set; } = TimeSpan.FromSeconds(180);
    /// <summary>执行公开方法 RetryWhen。</summary>

    public PluginProxyPolicyBuilder RetryWhen(Func<PluginAttemptResult, bool> predicate)
    {
        RetryPredicate = predicate;
        return this;
    }
    /// <summary>执行公开方法 CooldownNodeWhen。</summary>

    public PluginProxyPolicyBuilder CooldownNodeWhen(Func<PluginAttemptResult, bool> predicate)
    {
        CooldownNodePredicate = predicate;
        return this;
    }
    /// <summary>执行公开方法 CooldownAccountWhen。</summary>

    public PluginProxyPolicyBuilder CooldownAccountWhen(Func<PluginAttemptResult, bool> predicate)
    {
        CooldownAccountPredicate = predicate;
        return this;
    }
    /// <summary>执行公开方法 DisableAccountWhen。</summary>

    public PluginProxyPolicyBuilder DisableAccountWhen(Func<PluginAttemptResult, bool> predicate)
    {
        DisableAccountPredicate = predicate;
        return this;
    }
    /// <summary>执行公开方法 MaxAttempts。</summary>

    public PluginProxyPolicyBuilder MaxAttempts(int value)
    {
        MaxAttemptCount = Math.Clamp(value, 1, 35);
        return this;
    }
    /// <summary>声明宿主实际观察到 HTTP 传输异常时的动作，不处理 HTTP 业务状态码。</summary>
    /// <param name="decision">为每次故障创建决策，冷却截止时间应以调用时刻计算。</param>
    /// <returns>当前构建器。</returns>
    public PluginProxyPolicyBuilder OnTransportFailure(Func<PluginAttemptDecision> decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        _transportFailure = decision;
        return this;
    }
    /// <summary>执行公开方法 AttemptTimeoutSeconds。</summary>

    public PluginProxyPolicyBuilder AttemptTimeoutSeconds(int value)
    {
        AttemptTimeout = TimeSpan.FromSeconds(Math.Clamp(value, 1, 60));
        return this;
    }
    /// <summary>执行公开方法 TotalTimeoutSeconds。</summary>

    public PluginProxyPolicyBuilder TotalTimeoutSeconds(int value)
    {
        TotalTimeout = TimeSpan.FromSeconds(Math.Clamp(value, 1, 180));
        return this;
    }
    /// <summary>执行公开方法 Build。</summary>

    public PluginProxyPolicy Build()
        => new(
            RetryPredicate,
            CooldownNodePredicate,
            CooldownAccountPredicate,
            DisableAccountPredicate,
            MaxAttemptCount,
            AttemptTimeout,
            TotalTimeout) { TransportFailureDecision = _transportFailure };
}
/// <summary>表示公开契约类型 PluginAccountPolicyBuilder。</summary>

public sealed class PluginAccountPolicyBuilder
{
    internal Func<Account, bool>? Selector { get; private set; }
    internal Func<Account, AdapterRequest, bool>? RequestSelector { get; private set; }
    internal Func<Account, AdapterRequest, int>? WeightSelector { get; private set; }
    private Func<Account, DateTimeOffset?>? _preferredExpiry;
    private Func<IReadOnlyList<Account>, AdapterRequest, CancellationToken, Task<IReadOnlyList<PluginAccountPreference>>>? _batchSelector;
    internal Func<PluginAttemptResult, bool>? CooldownPredicate { get; private set; }
    internal Func<PluginAttemptResult, bool>? DisablePredicate { get; private set; }
    /// <summary>执行公开方法 SelectAccount。</summary>

    public PluginAccountPolicyBuilder SelectAccount(Func<Account, bool> selector)
    {
        Selector = selector;
        return this;
    }
    /// <summary>按当前模型和端点筛选账号。</summary>
    public PluginAccountPolicyBuilder SelectForRequest(Func<Account, AdapterRequest, bool> selector)
    {
        RequestSelector = selector;
        return this;
    }
    /// <summary>按当前请求为账号计算优先级，较大的值优先。</summary>
    public PluginAccountPolicyBuilder WeightBy(Func<Account, AdapterRequest, int> selector)
    {
        WeightSelector = selector;
        return this;
    }
    /// <summary>优先选择截止时间较早的账号；相同时间再按 WeightBy 排序，null 不获得到期优先。</summary>
    /// <param name="selector">读取账号的本地截止时间快照，不应同步等待网络查询。</param>
    /// <returns>当前构建器。</returns>
    public PluginAccountPolicyBuilder PreferEarlier(Func<Account, DateTimeOffset?> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _preferredExpiry = selector;
        return this;
    }
    /// <summary>一次处理整批候选账号，避免每个账号调用解释器；结果不能包含输入之外的账号。</summary>
    /// <param name="selector">返回账号筛选、权重及可选主排序截止时间的异步回调。</param>
    /// <returns>当前构建器。</returns>
    public PluginAccountPolicyBuilder SelectBatch(
        Func<IReadOnlyList<Account>, AdapterRequest, CancellationToken, Task<IReadOnlyList<PluginAccountPreference>>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _batchSelector = selector;
        return this;
    }
    /// <summary>执行公开方法 CooldownWhen。</summary>

    public PluginAccountPolicyBuilder CooldownWhen(Func<PluginAttemptResult, bool> predicate)
    {
        CooldownPredicate = predicate;
        return this;
    }
    /// <summary>执行公开方法 DisableWhen。</summary>

    public PluginAccountPolicyBuilder DisableWhen(Func<PluginAttemptResult, bool> predicate)
    {
        DisablePredicate = predicate;
        return this;
    }
    /// <summary>执行公开方法 Build。</summary>

    public PluginAccountPolicy Build()
        => new(Selector, CooldownPredicate, DisablePredicate, RequestSelector, WeightSelector)
        {
            PreferredExpirySelector = _preferredExpiry,
            BatchSelector = _batchSelector
        };
}
/// <summary>表示公开契约类型 PluginProxyPolicy。</summary>

public sealed record PluginProxyPolicy(
    Func<PluginAttemptResult, bool>? RetryPredicate,
    Func<PluginAttemptResult, bool>? CooldownNodePredicate,
    Func<PluginAttemptResult, bool>? CooldownAccountPredicate,
    Func<PluginAttemptResult, bool>? DisableAccountPredicate,
    int MaxAttempts,
    TimeSpan AttemptTimeout,
    TimeSpan TotalTimeout)
{
    /// <summary>新插件的宿主 HTTP 异常决策；为空时保留旧 predicate 映射。</summary>
    public Func<PluginAttemptDecision>? TransportFailureDecision { get; init; }
    /// <summary>获取或设置公开成员 Default。</summary>
    public static PluginProxyPolicy Default { get; } = new(
        result => result.IsTransportFailure || result.StatusCode is 408 or 425 or 429 or >= 500,
        result => result.IsTransportFailure || result.StatusCode is 407,
        result => result.StatusCode is 429,
        result => result.IndicatesInvalidCredential,
        3,
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(180));
}
/// <summary>表示公开契约类型 PluginAccountPolicy。</summary>

public sealed record PluginAccountPolicy(
    Func<Account, bool>? Selector,
    Func<PluginAttemptResult, bool>? CooldownPredicate,
    Func<PluginAttemptResult, bool>? DisablePredicate,
    Func<Account, AdapterRequest, bool>? RequestSelector = null,
    Func<Account, AdapterRequest, int>? WeightSelector = null)
{
    /// <summary>可选的主排序截止时间；null 排最后，再以 WeightSelector 保留会话粘性或权重。</summary>
    public Func<Account, DateTimeOffset?>? PreferredExpirySelector { get; init; }
    /// <summary>宿主硬条件筛选后的批量选择回调；不能扩大候选范围。</summary>
    public Func<IReadOnlyList<Account>, AdapterRequest, CancellationToken, Task<IReadOnlyList<PluginAccountPreference>>>? BatchSelector { get; init; }
}

/// <summary>插件对宿主候选账号的偏好，不携带凭据或资源对象。</summary>
public sealed record PluginAccountPreference(string AccountId, bool Eligible = true, int Weight = 0, DateTimeOffset? PreferredExpiry = null);

/// <summary>插件路由授权策略。</summary>
public enum PluginAuthPolicy
{
    /// <summary>表示枚举值 Anonymous。</summary>
    Anonymous,
    /// <summary>表示枚举值 AdminSession。</summary>
    AdminSession,
    /// <summary>表示枚举值 ApiKey。</summary>
    ApiKey,
    /// <summary>表示枚举值 AdminSessionOrApiKey。</summary>
    AdminSessionOrApiKey,
    /// <summary>表示枚举值 Internal。</summary>
    Internal,
    /// <summary>表示枚举值 Custom。</summary>
    Custom
}

/// <summary>声明插件拥有的 HTTP 路由。</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PluginEndpointAttribute(string method, string path) : Attribute
{
    /// <summary>获取或设置公开成员 Method。</summary>
    public string Method { get; } = method;
    /// <summary>获取或设置公开成员 Path。</summary>
    public string Path { get; } = path;
    /// <summary>获取或设置公开成员 Auth。</summary>
    public PluginAuthPolicy Auth { get; init; } = PluginAuthPolicy.AdminSession;
    /// <summary>获取或设置公开成员 Name。</summary>
    public string? Name { get; init; }
}

/// <summary>声明插件契约兼容范围。</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class PluginContractAttribute : Attribute
{
    /// <summary>获取或设置公开成员 MinVersion。</summary>
    public required string MinVersion { get; init; }
    /// <summary>获取或设置公开成员 MaxVersion。</summary>
    public required string MaxVersion { get; init; }
}

/// <summary>声明管理员角色要求。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireRoleAttribute(params string[] roles) : Attribute
{
    /// <summary>获取或设置公开成员 Roles。</summary>
    public IReadOnlyList<string> Roles { get; } = roles;
}

/// <summary>凭证字段元数据。</summary>
public sealed record CredentialField(
    string Name,
    string DisplayName,
    bool Secret = false,
    bool Required = true);
