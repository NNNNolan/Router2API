using Polly;
using Polly.Registry;
using Polly.Retry;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Infrastructure.Services;

/// <summary>
/// 集中注册有限的 Polly v8 profile。业务 failover 与 HTTP 重放使用独立上下文和预算，
/// 不给 HttpClient 叠加状态码重试、hedging 或 SSE body 重发。
/// </summary>
public sealed class PluginResiliencePipelines : IDisposable
{
    internal static readonly ResiliencePropertyKey<int> AttemptRetries = new("plugin-attempt-retries");
    internal static readonly ResiliencePropertyKey<TransportRetryState?> TransportState = new("transport-retry-state");
    private readonly ResiliencePipelineRegistry<string> _registry = new();

    /// <summary>一次性注册命名 profile 的配置 Lambda，按需构建并共享 pipeline。</summary>
    public PluginResiliencePipelines()
    {
        _registry.TryAddBuilder<PluginInvocationResult>("plugin-attempt", (builder, _) =>
            builder.AddRetry(new RetryStrategyOptions<PluginInvocationResult>
            {
                MaxRetryAttempts = 34,
                Delay = TimeSpan.Zero,
                ShouldHandle = args => ValueTask.FromResult(
                    !args.Context.CancellationToken.IsCancellationRequested
                    && args.AttemptNumber < args.Context.Properties.GetValue(AttemptRetries, 0)
                    && args.Outcome.Result is { Attempt.Decision.Retry: PluginRetryAction.NextAttempt } result
                    && !PluginResponseLifetime.HasStream(result.Response)),
                OnRetry = async args =>
                {
                    if (args.Outcome.Result?.Response.Lifetime is { } lifetime) await lifetime.DisposeAsync();
                }
            }));
        RegisterTransport("task-safe-read", allowUnsafe: false);
        RegisterTransport("explicit-replay", allowUnsafe: true);
    }

    internal ResiliencePipeline<PluginInvocationResult> Attempts => _registry.GetPipeline<PluginInvocationResult>("plugin-attempt");

    internal ResiliencePipeline<HttpResponseMessage> Transport(ProxyPoolRetryOptions retry)
        => retry.MaxRetries == 0 ? ResiliencePipeline<HttpResponseMessage>.Empty
            : _registry.GetPipeline<HttpResponseMessage>(retry.AllowUnsafeMethods ? "explicit-replay" : "task-safe-read");

    private void RegisterTransport(string name, bool allowUnsafe)
        => _registry.TryAddBuilder<HttpResponseMessage>(name, (builder, _) =>
            builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 5,
                ShouldHandle = args =>
                {
                    var state = args.Context.Properties.GetValue(TransportState, null);
                    return ValueTask.FromResult(state is not null
                        && state.Sending && (allowUnsafe || state.SafeMethod)
                        && args.AttemptNumber < state.Options.MaxRetries
                        && !args.Context.CancellationToken.IsCancellationRequested
                        && args.Outcome.Exception is HttpRequestException or OperationCanceledException);
                },
                DelayGenerator = args => ValueTask.FromResult<TimeSpan?>(
                    args.Context.Properties.GetValue(TransportState, null)?.Options.Delay ?? TimeSpan.Zero)
            }));

    /// <summary>宿主关闭时释放 profile registry。</summary>
    public void Dispose() => _registry.Dispose();

    internal sealed class TransportRetryState(ProxyPoolRetryOptions options)
    {
        public ProxyPoolRetryOptions Options { get; } = options;
        public bool Sending { get; set; }
        public bool SafeMethod { get; set; }
    }
}
