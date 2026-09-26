namespace Router.Contracts.Host;

/// <summary>无需账号或插件尝试上下文的代理池 HTTP 工厂；不解释 HTTP 状态码或更新资源惩罚。</summary>
public interface IProxyPoolHttpClientFactory
{
    /// <summary>选择一个可用代理并创建固定使用该代理的客户端，调用方负责释放客户端与响应。</summary>
    /// <param name="options">节点范围、超时及显式直连回退设置。</param>
    /// <param name="cancellationToken">取消节点查询。</param>
    /// <returns>调用方拥有的客户端。</returns>
    Task<HttpClient> CreateClientAsync(
        ProxyPoolHttpClientOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>发送请求；可选的 Polly 重试只处理传输异常，每次尝试创建新请求并重新选择代理。</summary>
    /// <param name="requestFactory">每次返回一个新请求；请求所有权转移给工厂。</param>
    /// <param name="options">节点范围、超时及显式直连回退设置。</param>
    /// <param name="retry">默认不重试。调用方须确认启用重试的操作可以安全重放。</param>
    /// <param name="completionOption">默认只等待响应头；返回后的流读取不会自动重试。</param>
    /// <param name="cancellationToken">取消查询、发送、缓冲和重试等待。</param>
    /// <returns>调用方必须释放的响应；释放响应也释放工厂为其持有的请求和客户端。</returns>
    Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        ProxyPoolHttpClientOptions? options = null,
        ProxyPoolRetryOptions? retry = null,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead,
        CancellationToken cancellationToken = default);
}

/// <summary>独立代理池客户端的传输选项，不包含账号或业务状态码策略。</summary>
public sealed record ProxyPoolHttpClientOptions
{
    /// <summary>限定订阅 ID；空值或空列表表示所有启用订阅。</summary>
    public IReadOnlyList<string>? SubscriptionIds { get; init; }
    /// <summary>选不到代理时是否允许直连；默认拒绝直连。</summary>
    public bool AllowDirectFallback { get; init; }
    /// <summary>是否允许底层自动重定向；JS 宿主桥接必须保持关闭。</summary>
    public bool AllowAutoRedirect { get; init; }
    /// <summary>发送/响应缓冲超时；HeadersRead 返回后的流读取由调用方负责取消。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>代理池发送方法的可选 Polly 传输重试设置。</summary>
public sealed record ProxyPoolRetryOptions
{
    /// <summary>额外重试次数，0 至 5；默认不重试。</summary>
    public int MaxRetries { get; init; }
    /// <summary>重试间隔，默认 200 毫秒。</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(200);
    /// <summary>明确允许重放 GET/HEAD/OPTIONS 之外的方法；可能导致重复业务操作。</summary>
    public bool AllowUnsafeMethods { get; init; }
}

/// <summary>代理池中没有满足选项的可用节点，且调用方未允许直连回退。</summary>
public sealed class ProxyPoolUnavailableException()
    : InvalidOperationException("No available proxy in the enabled subscriptions; direct fallback is disabled.");
