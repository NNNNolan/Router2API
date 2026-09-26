using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Router.Infrastructure.Services;

/// <summary>一次业务调用的截止时间与响应所有权；尝试返回不代表流已经完成。</summary>
internal sealed class PluginInvocationScope : IDisposable
{
    private readonly CancellationTokenSource _operation;
    private readonly TimeSpan _attemptTimeout;
    private readonly TimeSpan _responseTimeout;
    private int _bodyPhase;
    private bool _transferred;

    public PluginInvocationScope(PluginProxyPolicy policy, PluginExecutionOptions options, CancellationToken token)
    {
        _operation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _operation.CancelAfter(Min(policy.TotalTimeout, options.TotalSetupTimeout));
        _attemptTimeout = Min(policy.AttemptTimeout, options.SetupTimeout);
        _responseTimeout = options.ResponseTimeout;
    }

    public CancellationToken Token => _operation.Token;
    public bool IsCancellationRequested => _operation.IsCancellationRequested;

    public CancellationTokenSource CreateAttemptTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(_attemptTimeout);
        return timeout;
    }

    public void BeginResponse(CancellationTokenSource? attempt = null)
    {
        attempt?.CancelAfter(Timeout.InfiniteTimeSpan);
        if (Interlocked.Exchange(ref _bodyPhase, 1) == 0)
            _operation.CancelAfter(_responseTimeout);
    }

    public AdapterResponse TransferToResponse(AdapterResponse response)
    {
        if (_transferred) throw new InvalidOperationException("Invocation scope has already been transferred.");
        BeginResponse();
        response = PluginResponseLifetime.Ensure(response);
        ((PluginResponseLifetime)response.Lifetime!).AddResource(() =>
        {
            _operation.Dispose();
            return ValueTask.CompletedTask;
        });
        _transferred = true;
        return response;
    }

    public void Dispose()
    {
        if (!_transferred) _operation.Dispose();
    }

    private static TimeSpan Min(TimeSpan requested, TimeSpan limit)
        => requested > TimeSpan.Zero && requested < limit ? requested : limit;
}
