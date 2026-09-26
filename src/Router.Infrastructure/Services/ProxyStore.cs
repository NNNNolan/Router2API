using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;

namespace Router.Infrastructure.Services;

/// <summary>代理节点事实存储。短期插件策略只在 Redis，不在进程内缓存。</summary>
public sealed class ProxyStore(SqlSugarDatabase database) : IProxyStore
{
    public Task<ProxyEndpoint?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entity = database.Scope.Queryable<ProxyEndpointEntity>().First(x => x.Id == id);
        return Task.FromResult(entity?.ToDomain());
    }

    public Task<IReadOnlyList<ProxyEndpoint>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = database.Scope.Queryable<ProxyEndpointEntity>()
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
        var source = database.Scope.Queryable<ProxyEndpointEntity>();
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
        var entity = proxy.ToEntity();
        if (database.Scope.Queryable<ProxyEndpointEntity>().Any(x => x.Id == entity.Id))
            database.Scope.Updateable(entity).ExecuteCommand();
        else
            database.Scope.Insertable(entity).ExecuteCommand();
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        database.Scope.Deleteable<ProxyEndpointEntity>().Where(x => x.Id == id).ExecuteCommand();
        return Task.CompletedTask;
    }
}
