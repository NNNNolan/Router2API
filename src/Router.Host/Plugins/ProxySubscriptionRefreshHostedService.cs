using Microsoft.Extensions.Hosting;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Router.Host.Plugins;

public interface IProxySubscriptionRefreshQueue
{
    void Enqueue(string subscriptionId);
}

/// <summary>按订阅配置周期自动拉取代理节点。</summary>
public sealed class ProxySubscriptionRefreshHostedService(
    IProxySubscriptionService subscriptions,
    ILogger<ProxySubscriptionRefreshHostedService> logger) : BackgroundService, IProxySubscriptionRefreshQueue
{
    private readonly Channel<string> _refreshQueue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public void Enqueue(string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId)
            || !_queued.TryAdd(subscriptionId, 0)
            || _refreshQueue.Writer.TryWrite(subscriptionId))
            return;

        _queued.TryRemove(subscriptionId, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(RefreshDueLoopAsync(stoppingToken), ProcessQueuedAsync(stoppingToken));
    }

    private async Task RefreshDueLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            await RefreshDueAsync(cancellationToken);
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await RefreshDueAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 宿主正常关闭。
        }
    }

    private async Task ProcessQueuedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var id in _refreshQueue.Reader.ReadAllAsync(cancellationToken))
            {
                _queued.TryRemove(id, out _);
                await RefreshOneAsync(id, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 宿主正常关闭。
        }
    }

    private async Task RefreshDueAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProxySubscription> values;
        try
        {
            values = await subscriptions.ListAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "failed to list proxy subscriptions for scheduled refresh");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var subscription in values.Where(value =>
                     value.Enabled
                     && value.RefreshIntervalMinutes > 0
                     && (value.LastFetchedAt is null
                         || value.LastFetchedAt.Value.AddMinutes(value.RefreshIntervalMinutes) <= now)))
            await RefreshOneAsync(subscription.Id, cancellationToken, subscription.RefreshIntervalMinutes);
    }

    private async Task RefreshOneAsync(
        string subscriptionId,
        CancellationToken cancellationToken,
        int? intervalMinutes = null)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            await subscriptions.RefreshAsync(subscriptionId, cancellationToken);
            logger.LogDebug(
                "proxy subscription refreshed id={SubscriptionId} intervalMinutes={IntervalMinutes}",
                subscriptionId,
                intervalMinutes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "proxy subscription refresh failed id={SubscriptionId}",
                subscriptionId);
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
