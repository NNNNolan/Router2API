using Router.Contracts.Domain;

namespace Router.Contracts.Pipeline;

/// <summary>宿主内部的标准请求上下文；插件只接收 PluginAttemptContext。</summary>
public sealed class RequestContext
{
    /// <summary>获取或设置公开成员 Request。</summary>
    public required AdapterRequest Request { get; set; }
    /// <summary>获取或设置公开成员 Response。</summary>
    public AdapterResponse? Response { get; set; }
    /// <summary>获取或设置公开成员 AttemptDetails。</summary>
    public IReadOnlyList<RequestAttemptDetail> AttemptDetails { get; set; } = [];
    /// <summary>获取或设置公开成员 SelectedPlatform。</summary>
    public string? SelectedPlatform { get; set; }
    /// <summary>获取或设置公开成员 PluginKey。</summary>
    public string? PluginKey { get; set; }
    /// <summary>获取或设置公开成员 TestOverrides。</summary>
    public TestOverrides? TestOverrides { get; set; }
    /// <summary>获取或设置公开成员 TraceId。</summary>
    public string TraceId { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>获取或设置公开成员 Services。</summary>
    public IServiceProvider Services { get; init; } = default!;
    /// <summary>获取或设置公开成员 CancellationToken。</summary>
    public CancellationToken CancellationToken { get; set; }
    /// <summary>获取或设置公开成员 Items。</summary>
    public Dictionary<string, object?> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
}
/// <summary>表示公开契约类型 PluginDelegate。</summary>

public delegate Task PluginDelegate(RequestContext context);
/// <summary>表示公开契约类型 IPluginMiddleware。</summary>

public interface IPluginMiddleware
{
    /// <summary>执行接口方法 InvokeAsync。</summary>
    Task InvokeAsync(RequestContext context, PluginDelegate next);
}

/// <summary>平台插件实现。账号、节点和重试均由宿主控制。</summary>
public interface IPlatformTerminal
{
    /// <summary>执行接口方法 InvokeAsync。</summary>
    Task<PluginInvocationResult> InvokeAsync(PluginAttemptContext context);
    /// <summary>执行接口方法 GetModelsAsync。</summary>

    Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(
        ModelQueryContext context,
        CancellationToken cancellationToken);
    /// <summary>执行接口方法 ValidateCredentialAsync。</summary>

    Task<CredentialValidationResult> ValidateCredentialAsync(
        Credential credential,
        CancellationToken cancellationToken);
}
/// <summary>表示公开契约类型 CredentialValidationResult。</summary>

public sealed record CredentialValidationResult(bool Success, string? Error = null);
/// <summary>表示公开契约类型 PipelineStage。</summary>

public enum PipelineStage
{
    /// <summary>表示枚举值 Auth。</summary>
    Auth = 100,
    /// <summary>表示枚举值 RateLimit。</summary>
    RateLimit = 200,
    /// <summary>表示枚举值 Routing。</summary>
    Routing = 300,
    /// <summary>表示枚举值 AccountSelection。</summary>
    AccountSelection = 400,
    /// <summary>表示枚举值 ProxySelection。</summary>
    ProxySelection = 500,
    /// <summary>表示枚举值 Failover。</summary>
    Failover = 700,
    /// <summary>表示枚举值 Terminal。</summary>
    Terminal = 900,
    /// <summary>表示枚举值 ResponseTransform。</summary>
    ResponseTransform = 1000,
    /// <summary>表示枚举值 PostProcess。</summary>
    PostProcess = 1100
}
