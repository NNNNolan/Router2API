using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;

namespace Router.Infrastructure.Services;

/// <summary>代理节点事实存储。短期插件策略只在 Redis，不在进程内缓存。</summary>
public sealed class ProxyStore(SqlSugarDatabase database) : IProxyStore
{
    private const int RefreshBatchSize = 100;

    public Task<ProxyEndpoint?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var entity = db.Queryable<ProxyEndpointEntity>().First(x => x.Id == id);
        return Task.FromResult(entity?.ToDomain());
    }

    public Task<IReadOnlyList<ProxyEndpoint>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var result = db.Queryable<ProxyEndpointEntity>()
            .OrderBy(x => x.Id)
            .ToList()
            .Select(x => x.ToDomain())
            .ToArray();
        return Task.FromResult<IReadOnlyList<ProxyEndpoint>>(result);
    }

    internal Task<IReadOnlyList<ProxyEndpoint>> ListBySubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var result = db.Queryable<ProxyEndpointEntity>()
            .Where(x => x.SubscriptionId == subscriptionId)
            .OrderBy(x => x.Id)
            .ToList()
            .Select(x => x.ToDomain())
            .ToArray();
        return Task.FromResult<IReadOnlyList<ProxyEndpoint>>(result);
    }

    public Task<PagedResult<ProxyEndpoint>> QueryAsync(
        ProxyQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = Math.Max(1, query.Page);
        var pageSize = query.PageSize is 20 or 50 or 100 ? query.PageSize : 20;
        using var db = database.CreateClient();
        var source = db.Queryable<ProxyEndpointEntity>();
        if (query.SubscriptionIds is { Count: > 0 })
            source = source.Where(x => query.SubscriptionIds.Contains(x.SubscriptionId));
        if (query.State is { } state)
            source = source.Where(x => x.State == state.ToString());

        var total = source.Count();
        var items = source.OrderByDescending(x => x.CompositeScore)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList()
            .Select(x => x.ToDomain())
            .ToArray();
        return Task.FromResult(new PagedResult<ProxyEndpoint>(items, page, pageSize, total));
    }

    public Task SaveAsync(ProxyEndpoint proxy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var entity = proxy.ToEntity();
        if (db.Queryable<ProxyEndpointEntity>().Any(x => x.Id == entity.Id))
            db.Updateable(entity).ExecuteCommand();
        else
            db.Insertable(entity).ExecuteCommand();
        return Task.CompletedTask;
    }

    internal Task InsertSubscriptionBatchAsync(IReadOnlyList<ProxyEndpoint> proxies, CancellationToken cancellationToken = default)
    {
        foreach (var batch in proxies.Chunk(RefreshBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var db = database.CreateClient();
            db.Insertable(batch.Select(proxy => proxy.ToEntity()).ToList()).ExecuteCommand();
        }
        return Task.CompletedTask;
    }

    internal Task UpdateSubscriptionSeenBatchAsync(IReadOnlyList<ProxyEndpoint> proxies, CancellationToken cancellationToken = default)
    {
        foreach (var batch in proxies.Chunk(RefreshBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var db = database.CreateClient();
            db.Updateable(batch.Select(proxy => proxy.ToEntity()).ToList())
                .UpdateColumns(x => new { x.LastSeenInSubscriptionAtUtc })
                .WhereColumns(x => new { x.Id })
                .ExecuteCommand();
        }
        return Task.CompletedTask;
    }

    internal Task UpdateSubscriptionConnectionAsync(ProxyEndpoint proxy, long expectedVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var entity = proxy.ToEntity();
        var changed = db.Updateable(entity)
            .UpdateColumns(x => new
            {
                x.Host, x.Port, x.Scheme, x.Username, x.Password, x.ConfigurationVersion, x.LastSeenInSubscriptionAtUtc,
                x.LatencyMs, x.AverageLatencyMs, x.AverageSpeedBytesPerSecond, x.CompositeScore, x.LastProbedAtUtc,
                x.LastProbeSuccessAtUtc, x.ProbeConsecutiveFailures, x.ProbeError, x.ProbeStatus, x.ProbeConfigurationVersion
            })
            .Where(row => row.Id == entity.Id && row.ConfigurationVersion == expectedVersion)
            .ExecuteCommand();
        if (changed != 1) throw new InvalidOperationException("Proxy subscription changed while refreshing.");
        return Task.CompletedTask;
    }

    internal Task ReactivateDrainingBatchAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        foreach (var batch in ids.Chunk(RefreshBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var db = database.CreateClient();
            db.Updateable<ProxyEndpointEntity>()
                .SetColumns(x => new ProxyEndpointEntity { State = ResourceState.Active.ToString() })
                .Where(x => batch.Contains(x.Id) && x.State == ResourceState.Draining.ToString())
                .ExecuteCommand();
        }
        return Task.CompletedTask;
    }

    internal Task<bool> SaveProbeResultAsync(ProxyEndpoint proxy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var entity = proxy.ToEntity();
        var changed = db.Updateable(entity)
            .UpdateColumns(x => new
            {
                x.LatencyMs, x.AverageLatencyMs, x.AverageSpeedBytesPerSecond, x.CompositeScore,
                x.LastProbedAtUtc, x.LastProbeSuccessAtUtc, x.ProbeConsecutiveFailures,
                x.ProbeError, x.ProbeStatus, x.ProbeConfigurationVersion
            })
            .Where(row => row.Id == entity.Id && row.ConfigurationVersion == entity.ConfigurationVersion)
            .ExecuteCommand();
        return Task.FromResult(changed == 1);
    }

    internal Task MarkSubscriptionDrainingBatchAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        foreach (var batch in ids.Chunk(RefreshBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var db = database.CreateClient();
            var entities = batch.Select(id => new ProxyEndpointEntity { Id = id, State = ResourceState.Draining.ToString() }).ToList();
            db.Updateable(entities).UpdateColumns(x => new { x.State }).WhereColumns(x => new { x.Id }).ExecuteCommand();
        }
        return Task.CompletedTask;
    }

    internal Task RemoveBatchAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        foreach (var batch in ids.Chunk(RefreshBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var db = database.CreateClient();
            db.Deleteable<ProxyEndpointEntity>().Where(x => batch.Contains(x.Id) && x.InFlight == 0).ExecuteCommand();
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        db.Deleteable<ProxyEndpointEntity>().Where(x => x.Id == id).ExecuteCommand();
        return Task.CompletedTask;
    }
}
