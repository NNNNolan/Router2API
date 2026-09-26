using System.Text.Json;
using Router.Contracts.Host;

namespace Router.Contracts.Domain;

/// <summary>账号和代理共用的资源生命周期状态。</summary>
public enum ResourceState
{
    /// <summary>表示枚举值 Active。</summary>
    Active,
    /// <summary>表示枚举值 Cooling。</summary>
    Cooling,
    /// <summary>表示枚举值 Draining。</summary>
    Draining,
    /// <summary>表示枚举值 Invalid。</summary>
    Invalid,
    /// <summary>表示枚举值 Disabled。</summary>
    Disabled,
    /// <summary>表示枚举值 Removed。</summary>
    Removed
}

/// <summary>网关支持的代理协议。</summary>
public enum ProxyScheme
{
    /// <summary>表示枚举值 Http。</summary>
    Http,
    /// <summary>表示枚举值 Https。</summary>
    Https,
    /// <summary>表示枚举值 Socks5。</summary>
    Socks5
}

/// <summary>代理策略的短期状态。节点状态按插件存储在 Redis。</summary>
public enum ProxyPolicyState
{
    /// <summary>表示枚举值 Available。</summary>
    Available,
    /// <summary>表示枚举值 Cooldown。</summary>
    Cooldown
}

/// <summary>平台插件支持的凭证类型。</summary>
public enum CredentialKind
{
    /// <summary>表示枚举值 ApiKey。</summary>
    ApiKey,
    /// <summary>表示枚举值 OAuth。</summary>
    OAuth,
    /// <summary>表示枚举值 Custom。</summary>
    Custom,
    /// <summary>表示枚举值 BearerToken。</summary>
    BearerToken,
    /// <summary>表示枚举值 BasicAuth。</summary>
    BasicAuth,
    /// <summary>表示枚举值 Cookie。</summary>
    Cookie
}

/// <summary>资源的长期事实状态。</summary>
public sealed class ResourceStatus
{
    /// <summary>获取或设置公开成员 State。</summary>
    public ResourceState State { get; set; } = ResourceState.Active;
    /// <summary>获取或设置公开成员 CooldownUntil。</summary>
    public DateTimeOffset? CooldownUntil { get; set; }
    /// <summary>获取或设置公开成员 DisabledUntil。</summary>
    public DateTimeOffset? DisabledUntil { get; set; }
    /// <summary>获取或设置公开成员 CooldownReason。</summary>
    public string? CooldownReason { get; set; }
    /// <summary>获取或设置公开成员 Reason。</summary>
    public string? Reason { get; set; }
    /// <summary>获取或设置公开成员 LastStatusCode。</summary>
    public int? LastStatusCode { get; set; }
    /// <summary>获取或设置公开成员 ConsecutiveFailures。</summary>
    public int ConsecutiveFailures { get; set; }
    /// <summary>获取或设置公开成员 InFlight。</summary>
    public int InFlight { get; set; }
    /// <summary>获取或设置公开成员 Version。</summary>
    public long Version { get; set; }
}

/// <summary>资源通用契约。短期插件节点策略不属于资源事实模型。</summary>
public interface IResource
{
    /// <summary>获取或设置接口成员 Id。</summary>
    string Id { get; }
    /// <summary>获取或设置接口成员 Status。</summary>
    ResourceStatus Status { get; set; }
}

/// <summary>代理订阅配置。节点来源是全局共享的，不绑定插件。</summary>
public sealed class ProxySubscription
{
    /// <summary>获取或设置公开成员 Id。</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>获取或设置公开成员 Name。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>获取或设置公开成员 Url。</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>获取或设置公开成员 Scheme。</summary>
    public ProxyScheme Scheme { get; set; } = ProxyScheme.Http;
    /// <summary>获取或设置公开成员 Enabled。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>获取或设置公开成员 ParserName。</summary>
    public string? ParserName { get; set; }
    /// <summary>获取或设置公开成员 RefreshIntervalMinutes。</summary>
    public int RefreshIntervalMinutes { get; set; } = 60;
    /// <summary>获取或设置公开成员 LastFetchedAt。</summary>
    public DateTimeOffset? LastFetchedAt { get; set; }
    /// <summary>获取或设置公开成员 LastFetchedCount。</summary>
    public int LastFetchedCount { get; set; }
    /// <summary>获取或设置公开成员 LastError。</summary>
    public string? LastError { get; set; }
}

/// <summary>代理端点及其持久化质量观测。</summary>
public sealed class ProxyEndpoint : IResource
{
    /// <summary>获取或设置公开成员 Id。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>获取或设置公开成员 SubscriptionId。</summary>
    public string SubscriptionId { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 Host。</summary>
    public string Host { get; init; } = string.Empty;
    /// <summary>获取或设置公开成员 Port。</summary>
    public int Port { get; init; }
    /// <summary>获取或设置公开成员 Scheme。</summary>
    public ProxyScheme Scheme { get; init; } = ProxyScheme.Http;
    /// <summary>获取或设置公开成员 Username。</summary>
    public string? Username { get; init; }
    /// <summary>获取或设置公开成员 Password。</summary>
    public string? Password { get; init; }
    /// <summary>获取或设置公开成员 ConfigurationVersion。</summary>
    public long ConfigurationVersion { get; set; } = 1;
    /// <summary>获取或设置公开成员 Status。</summary>
    public ResourceStatus Status { get; set; } = new();
    /// <summary>获取或设置公开成员 LatencyMs。</summary>

    public int LatencyMs { get; set; }
    /// <summary>获取或设置公开成员 AverageLatencyMs。</summary>
    public int AverageLatencyMs { get; set; }
    /// <summary>获取或设置公开成员 AverageSpeedBytesPerSecond。</summary>
    public double AverageSpeedBytesPerSecond { get; set; }
    /// <summary>获取或设置公开成员 CompositeScore。</summary>
    public double CompositeScore { get; set; }
    /// <summary>获取或设置公开成员 LastProbeAt。</summary>
    public DateTimeOffset? LastProbeAt { get; set; }
    /// <summary>获取或设置公开成员 LastProbeSuccessAt。</summary>
    public DateTimeOffset? LastProbeSuccessAt { get; set; }
    /// <summary>获取或设置公开成员 ProbeConsecutiveFailures。</summary>
    public int ProbeConsecutiveFailures { get; set; }
    /// <summary>获取或设置公开成员 ProbeError。</summary>
    public string? ProbeError { get; set; }
    /// <summary>获取或设置公开成员 成员。</summary>
    public DateTimeOffset? LastProbedAt
    {
        get => LastProbeAt;
        set => LastProbeAt = value;
    }
    /// <summary>获取或设置公开成员 ProbeStatus。</summary>
    public string ProbeStatus { get; set; } = "Unknown";
    /// <summary>获取或设置公开成员 ProbeConfigurationVersion。</summary>
    public long ProbeConfigurationVersion { get; set; }

    // Kept as a source-compatible read/write alias for integrations that only display the old metric name.
    /// <summary>获取或设置公开成员 成员。</summary>
    public int AvgLatencyMs
    {
        get => AverageLatencyMs;
        set => AverageLatencyMs = value;
    }
    /// <summary>获取或设置公开成员 FirstSeenAt。</summary>

    public DateTimeOffset FirstSeenAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>获取或设置公开成员 LastSeenInSubscriptionAt。</summary>
    public DateTimeOffset? LastSeenInSubscriptionAt { get; set; }
    /// <summary>获取或设置公开成员 EndpointKey。</summary>

    public string EndpointKey => $"{Scheme}://{Host}:{Port}:{Username ?? "anon"}";
    /// <summary>执行公开方法 ToUri。</summary>

    public Uri ToUri()
    {
        var scheme = Scheme switch
        {
            ProxyScheme.Https => "https",
            ProxyScheme.Socks5 => "socks5",
            _ => "http"
        };

        var userInfo = string.IsNullOrWhiteSpace(Username)
            ? string.Empty
            : $"{Uri.EscapeDataString(Username)}:{Uri.EscapeDataString(Password ?? string.Empty)}@";

        return new Uri($"{scheme}://{userInfo}{Host}:{Port}");
    }
}

/// <summary>凭证基类。</summary>
public abstract record Credential(CredentialKind Kind);
/// <summary>表示公开契约类型 ApiKeyCredential。</summary>

public sealed record ApiKeyCredential(string ApiKey) : Credential(CredentialKind.ApiKey);
/// <summary>表示公开契约类型 OAuthCredential。</summary>

public sealed record OAuthCredential(
    string AccessToken,
    DateTimeOffset? ExpiresAt = null,
    string? RefreshToken = null,
    string? IdToken = null,
    string? AccountId = null,
    string? Domain = null,
    string? EnterpriseId = null,
    string? Nickname = null) : Credential(CredentialKind.OAuth);
/// <summary>表示公开契约类型 BearerTokenCredential。</summary>

public sealed record BearerTokenCredential(string Token, DateTimeOffset? ExpiresAt = null)
    : Credential(CredentialKind.BearerToken);
/// <summary>表示公开契约类型 BasicAuthCredential。</summary>

public sealed record BasicAuthCredential(string Username, string Password) : Credential(CredentialKind.BasicAuth);
/// <summary>表示公开契约类型 CookieCredential。</summary>

public sealed record CookieCredential(string Cookie) : Credential(CredentialKind.Cookie);
/// <summary>表示公开契约类型 CustomCredential。</summary>

public sealed record CustomCredential(IReadOnlyDictionary<string, string?> Fields) : Credential(CredentialKind.Custom);

/// <summary>属于单个插件账号池的账号。</summary>
public sealed class Account : IResource
{
    /// <summary>获取或设置公开成员 Id。</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>获取或设置公开成员 PluginKey。</summary>
    public string PluginKey { get; set; } = string.Empty;
    /// <summary>获取或设置公开成员 Platform。</summary>
    public string Platform { get; set; } = string.Empty;
    /// <summary>获取或设置公开成员 Credential。</summary>
    public Credential Credential { get; set; } = new ApiKeyCredential(string.Empty);
    /// <summary>宿主管理的凭证修订号，用于字段级 CAS；不是租约计数或账号状态版本。</summary>
    public long CredentialVersion { get; set; }
    /// <summary>获取或设置公开成员 Status。</summary>
    public ResourceStatus Status { get; set; } = new();
    /// <summary>获取或设置公开成员 ExpiresAt。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>获取或设置公开成员 Label。</summary>
    public string? Label { get; set; }
}

/// <summary>统一请求内容块的类型。</summary>
public enum AdapterContentPartKind
{
    /// <summary>文本内容。</summary>
    Text,
    /// <summary>图片内容。</summary>
    Image,
    /// <summary>文件内容。</summary>
    File
}

/// <summary>按原顺序保存文本、图片和文件；文件 ID 只在原提供方有效。</summary>
public sealed record AdapterContentPart(
    AdapterContentPartKind Kind,
    string? Text = null,
    string? ImageUrl = null,
    string? FileId = null,
    string? Detail = null,
    string? FileName = null,
    string? FileData = null,
    string? FileUrl = null,
    string? MediaType = null);

/// <summary>统一请求中的消息；工具调用保留 OpenAI-compatible 结构。</summary>
public sealed record AdapterMessage(
    string Role,
    string Content,
    string? Name = null,
    IReadOnlyList<object>? ToolCalls = null,
    string? ToolCallId = null,
    JsonElement? ReasoningContent = null,
    JsonElement? Reasoning = null)
{
    /// <summary>文本、图片和文件按输入顺序排列；只有文本时使用 Content。</summary>
    public IReadOnlyList<AdapterContentPart>? ContentParts { get; init; }
}
/// <summary>表示公开契约类型 AdapterRequest。</summary>

public sealed class AdapterRequest
{
    /// <summary>获取或设置公开成员 Model。</summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>下游请求使用的 API 路径。</summary>
    public string Endpoint { get; set; } = "/v1/chat/completions";
    /// <summary>宿主解析前的请求体副本，供需要协议直通的插件使用。</summary>
    public JsonElement? OriginalBody { get; set; }
    /// <summary>获取或设置公开成员 Messages。</summary>
    public IReadOnlyList<AdapterMessage> Messages { get; set; } = [];
    /// <summary>获取或设置公开成员 Stream。</summary>
    public bool Stream { get; set; }
    /// <summary>获取或设置公开成员 MaxTokens。</summary>
    public int? MaxTokens { get; set; }
    /// <summary>获取或设置公开成员 Temperature。</summary>
    public double? Temperature { get; set; }
    /// <summary>获取或设置公开成员 Tools。</summary>
    public IReadOnlyList<object> Tools { get; set; } = [];
    /// <summary>宿主无法归类的请求参数；由具体插件按上游协议解释。</summary>
    public Dictionary<string, object?> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>宿主保留的非敏感下游请求头；具体插件自行解释。</summary>
    public Dictionary<string, string> RequestHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>完整的下游请求头，仅供诊断日志使用，可能包含认证信息。</summary>
    public Dictionary<string, string?[]> DownstreamRequestHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
}
/// <summary>表示公开契约类型 Usage。</summary>

public sealed record Usage(int PromptTokens, int CompletionTokens, int TotalTokens);

/// <summary>插件返回的标准化助手完成结果；公共 API 响应由宿主按下游协议生成。</summary>
public sealed record AdapterCompletion(
    string Model,
    string? Content,
    string FinishReason = "stop",
    Usage? Usage = null,
    IReadOnlyList<AdapterToolCall>? ToolCalls = null,
    string? ReasoningContent = null,
    string? ReasoningSignature = null);

/// <summary>插件返回的完整函数工具调用。</summary>
public sealed record AdapterToolCall(
    int Index,
    string? Id,
    string? Name,
    string? Arguments);

/// <summary>流式工具调用增量。</summary>
public sealed record ToolCallDelta(
    int Index,
    string? Id = null,
    string? Type = null,
    string? Name = null,
    string? Arguments = null);

/// <summary>单次模型请求中的代理尝试记录，不包含代理认证信息。</summary>
public sealed record RequestAttemptDetail(
    string? ProxyId,
    string ProxyAddress,
    int? StatusCode,
    string Outcome,
    bool IsTransportFailure,
    string? Reason,
    int DurationMs);
/// <summary>表示公开契约类型 struct。</summary>

public readonly record struct StreamChunk(
    string? Delta,
    string? FinishReason = null,
    Usage? Usage = null,
    string? Role = null,
    IReadOnlyList<ToolCallDelta>? ToolCalls = null,
    string? ReasoningDelta = null,
    string? ReasoningSignature = null,
    string? Error = null,
    string? ErrorType = null);
/// <summary>表示公开契约类型 AdapterResponse。</summary>

public sealed class AdapterResponse
{
    private Usage? _usage;

    /// <summary>获取或设置公开成员 StatusCode。</summary>
    public int StatusCode { get; init; }
    /// <summary>获取或设置公开成员 IsStreaming。</summary>
    public bool IsStreaming { get; init; }
    /// <summary>标准化非流式完成结果；错误、流式和原始透传响应不设置此成员。</summary>
    public AdapterCompletion? Completion { get; init; }
    /// <summary>获取或设置公开成员 Stream。</summary>
    public IAsyncEnumerable<StreamChunk>? Stream { get; init; }
    /// <summary>获取或设置公开成员 Error。</summary>
    public string? Error { get; init; }
    /// <summary>可选的协议无关错误类型提示，由宿主映射成下游协议的错误类型。</summary>
    public string? ErrorType { get; init; }
    /// <summary>仅供宿主统计使用；标准完成结果优先读取其内含 usage，原始透传响应可单独提供。</summary>
    public Usage? Usage
    {
        get => Completion?.Usage ?? _usage;
        init => _usage = value;
    }
    /// <summary>指示是否由宿主将原始协议响应直接写回客户端。</summary>
    public bool IsRawPassthrough { get; init; }
    /// <summary>原始非流式协议响应内容。</summary>
    public byte[]? RawContent { get; init; }
    /// <summary>原始协议响应流。</summary>
    public IAsyncEnumerable<ReadOnlyMemory<byte>>? RawStream { get; init; }
    /// <summary>原始协议响应的 Content-Type。</summary>
    public string? ContentType { get; init; }
    /// <summary>响应所有权；宿主在写出完成或中止时释放，不参与 JSON 序列化。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IAsyncDisposable? Lifetime { get; init; }
    /// <summary>获取或设置公开成员 IsSuccess。</summary>

    public bool IsSuccess => StatusCode is >= 200 and < 300;
    /// <summary>执行公开方法 Unauthorized。</summary>

    public static AdapterResponse Unauthorized(string error)
        => new() { StatusCode = 401, Error = error, ErrorType = "authentication_error" };
    /// <summary>执行公开方法 BadRequest。</summary>

    public static AdapterResponse BadRequest(string error)
        => new() { StatusCode = 400, Error = error, ErrorType = "invalid_request_error" };
    /// <summary>执行公开方法 ServerError。</summary>

    public static AdapterResponse ServerError(string error)
        => new() { StatusCode = 500, Error = error, ErrorType = "server_error" };
}
/// <summary>表示公开契约类型 ModelDescriptor。</summary>

public sealed record ModelDescriptor(
    string Id,
    string DisplayName,
    int ContextWindow = 0,
    bool SupportsStreaming = true,
    int InputLimit = 0,
    int OutputLimit = 0,
    bool SupportsReasoning = false,
    IReadOnlyList<string>? ReasoningLevels = null,
    int? ReasoningTokenLimit = null,
    string? CreditMultiplier = null);

/// <summary>模型广场使用的 models.dev 模型能力元数据。</summary>
public sealed record ModelMetadata(
    string Id,
    string Platform,
    string PlatformName,
    string DisplayName,
    int ContextWindow,
    int InputLimit,
    int OutputLimit,
    bool SupportsReasoning,
    IReadOnlyList<string>? ReasoningLevels = null,
    int? ReasoningTokenLimit = null);

/// <summary>带缓存时间的模型能力和协议元数据快照。</summary>
public sealed record ModelMetadataSnapshot(
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ModelMetadata> Models,
    IReadOnlyDictionary<string, ModelProtocol>? Protocols = null,
    DateTimeOffset? ProtocolsUpdatedAt = null);
/// <summary>表示公开契约类型 ModelQueryContext。</summary>

public sealed record ModelQueryContext(
    string? RequestedModel = null,
    HttpClient? HttpClient = null,
    bool ForceRefresh = false);
/// <summary>表示公开契约类型 TestOverrides。</summary>

public sealed class TestOverrides
{
    /// <summary>获取或设置公开成员 AccountId。</summary>
    public string? AccountId { get; set; }
    /// <summary>获取或设置公开成员 ProxyId。</summary>
    public string? ProxyId { get; set; }
    /// <summary>获取或设置公开成员 SkipFallback。</summary>
    public bool SkipFallback { get; set; }
    /// <summary>获取或设置公开成员 SkipCooldown。</summary>
    public bool SkipCooldown { get; set; }
    /// <summary>获取或设置公开成员 SkipRateLimit。</summary>
    public bool SkipRateLimit { get; set; }
}
/// <summary>表示公开契约类型 struct。</summary>

public readonly record struct ResourceLeaseResult<T>(T Resource, IAsyncDisposable Lease);
/// <summary>表示公开契约类型 ResourceAvailability。</summary>

public static class ResourceAvailability
{
    /// <summary>执行公开方法 IsAvailable。</summary>
    public static bool IsAvailable(IResource resource, DateTimeOffset now)
        => resource.Status.State == ResourceState.Active
            && (resource.Status.CooldownUntil is null || resource.Status.CooldownUntil <= now)
            && (resource.Status.DisabledUntil is null || resource.Status.DisabledUntil <= now);
}

/// <summary>插件对单次宿主尝试的业务分类。</summary>
public enum PluginAttemptOutcome
{
    /// <summary>表示枚举值 Healthy。</summary>
    Healthy,
    /// <summary>表示枚举值 Retry。</summary>
    Retry,
    /// <summary>表示枚举值 CooldownNode。</summary>
    CooldownNode,
    /// <summary>表示枚举值 CooldownAccount。</summary>
    CooldownAccount,
    /// <summary>表示枚举值 DisableAccount。</summary>
    DisableAccount,
    /// <summary>表示枚举值 NoPenalty。</summary>
    NoPenalty
}
/// <summary>表示公开契约类型 PluginAttemptResult。</summary>

public sealed record PluginAttemptResult(
    PluginAttemptOutcome Outcome,
    int? StatusCode = null,
    bool IsTransportFailure = false,
    bool IndicatesInvalidCredential = false,
    string? Reason = null)
{
    /// <summary>新插件返回的显式决策；为空时宿主一次性映射旧策略，保留 DLL 兼容性。</summary>
    public PluginAttemptDecision? Decision { get; init; }
}

/// <summary>插件可见的单次尝试上下文。宿主隐藏节点身份与候选资源。</summary>
public sealed class PluginAttemptContext
{
    /// <summary>获取或设置公开成员 Request。</summary>
    public required AdapterRequest Request { get; init; }
    /// <summary>获取或设置公开成员 PlatformName。</summary>
    public required string PlatformName { get; init; }
    /// <summary>获取或设置公开成员 PluginKey。</summary>
    public required string PluginKey { get; init; }
    /// <summary>获取或设置公开成员 Account。</summary>
    public required Account Account { get; init; }
    /// <summary>获取或设置公开成员 HttpClient。</summary>
    public required IPluginHttpClient HttpClient { get; init; }
    /// <summary>获取或设置公开成员 CancellationToken。</summary>
    public required CancellationToken CancellationToken { get; init; }
    /// <summary>上游进入响应体阶段时通知宿主切换超时预算；仅供原生适配器使用，不向 JS 投影。</summary>
    public Action? NotifyUpstreamResponseStarted { get; init; }
    /// <summary>获取或设置公开成员 TraceId。</summary>
    public string TraceId { get; init; } = Guid.NewGuid().ToString("N");
}
/// <summary>表示公开契约类型 PluginInvocationResult。</summary>

public sealed record PluginInvocationResult(
    AdapterResponse Response,
    PluginAttemptResult Attempt,
    IReadOnlyList<RequestAttemptDetail>? AttemptDetails = null);
