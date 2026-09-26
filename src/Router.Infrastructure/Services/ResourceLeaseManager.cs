using System.Collections.Concurrent;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Infrastructure.Services;

/// <summary>进程内资源租约管理器。</summary>
public sealed class ResourceLeaseManager : IResourceLeaseManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _exclusive = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _exclusivePending = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ResourceLeaseResult<T>?> AcquireAsync<T>(
        IReadOnlyList<T> resources,
        Func<T, bool> isAvailable,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        where T : class, IResource
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(isAvailable);

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = resources.FirstOrDefault(resource =>
                !_exclusivePending.ContainsKey(resource.Id)
                && isAvailable(resource));

            if (selected is not null)
            {
                lock (selected.Status)
                    selected.Status.InFlight++;
                return new ResourceLeaseResult<T>(selected, new Lease(() =>
                {
                    lock (selected.Status)
                        selected.Status.InFlight--;
                    return ValueTask.CompletedTask;
                }));
            }

            await Task.Delay(50, cancellationToken);
        }

        return null;
    }

    public async Task<T> WithExclusiveAsync<T>(
        string resourceId,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentNullException.ThrowIfNull(action);

        var gate = _exclusive.GetOrAdd(resourceId, static _ => new SemaphoreSlim(1, 1));
        _exclusivePending.AddOrUpdate(resourceId, 1, static (_, count) => count + 1);
        var acquired = false;
        try
        {
            await gate.WaitAsync(cancellationToken);
            acquired = true;
            return await action(cancellationToken);
        }
        finally
        {
            if (acquired)
                gate.Release();
            RemovePending(resourceId);
        }
    }

    private void RemovePending(string resourceId)
    {
        while (_exclusivePending.TryGetValue(resourceId, out var count))
        {
            if (count > 1)
            {
                if (_exclusivePending.TryUpdate(resourceId, count - 1, count))
                    return;
            }
            else if (((ICollection<KeyValuePair<string, int>>)_exclusivePending)
                .Remove(new KeyValuePair<string, int>(resourceId, count)))
            {
                return;
            }
        }
    }

    private sealed class Lease(Func<ValueTask> release) : IAsyncDisposable
    {
        private int _released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                await release();
        }
    }
}
