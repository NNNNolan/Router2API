using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;

namespace Router.Infrastructure.Services;

/// <summary>线程安全的平台注册表。</summary>
public sealed class PlatformRegistry : IPlatformRegistry
{
    private readonly ConcurrentDictionary<string, PlatformRegistration> _items = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PlatformRegistration> All => _items.Values.OrderBy(x => x.Name).ToArray();

    public PlatformRegistration? Get(string platform)
        => _items.TryGetValue(platform, out var registration) ? registration : null;

    public void Register(PlatformRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _items[registration.Name] = registration;
    }

    public void SetEnabled(string platform, bool enabled)
    {
        _items.AddOrUpdate(
            platform,
            _ => throw new KeyNotFoundException($"platform '{platform}' is not registered"),
            (_, current) => current with { Enabled = enabled });
    }

    public void Remove(string platform) => _items.TryRemove(platform, out _);
}

/// <summary>使用平台名和模型名进行路由的模型路由器。</summary>
public sealed class ModelRouter(IPlatformRegistry platforms) : IModelRouter
{
    public bool TryResolve(string model, out string platform, out string normalizedModel)
    {
        platform = string.Empty;
        normalizedModel = model;
        var separator = model.IndexOf('/');
        if (separator <= 0 || separator == model.Length - 1) return false;

        platform = model[..separator];
        normalizedModel = model[(separator + 1)..];
        return platforms.Get(platform)?.Enabled == true;
    }
}

/// <summary>聚合并缓存插件模型描述。</summary>
public sealed class ModelCatalog(
    IPlatformRegistry platforms,
    ProxyTransportFactory transport) : IModelCatalog
{
    private readonly ConcurrentDictionary<string, CacheSlot> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ModelDescriptor>> ListAsync(CancellationToken cancellationToken = default)
    {
        var values = new List<ModelDescriptor>();
        foreach (var platform in platforms.All.Where(item => item.Enabled))
        {
            var models = await ListAsync(platform.Name, cancellationToken);
            values.AddRange(models.Select(model => model with { Id = $"{platform.Name}/{model.Id}" }));
        }

        return values;
    }

    public Task<IReadOnlyList<ModelDescriptor>> ListAsync(
        string platform,
        CancellationToken cancellationToken = default)
        => QueryAsync(platform, forceRefresh: false, cancellationToken);

    public Task<IReadOnlyList<ModelDescriptor>> RefreshAsync(
        string platform,
        CancellationToken cancellationToken = default)
        => QueryAsync(platform, forceRefresh: true, cancellationToken);

    private async Task<IReadOnlyList<ModelDescriptor>> QueryAsync(string platform, bool forceRefresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registration = platforms.Get(platform);
        if (registration is null) return [];
        if (forceRefresh) _cache.TryRemove(platform, out _);
        var slot = _cache.GetOrAdd(platform, _ => new CacheSlot());
        var cached = Volatile.Read(ref slot.Value);
        if (!forceRefresh && registration.ModelCacheTtl > TimeSpan.Zero
            && cached is not null && ReferenceEquals(cached.Terminal, registration.Terminal) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Models;
        using var client = transport.CreateClient(null, new PluginHttpClientOptions
        {
            AllowAutoRedirect = true, RequestTimeout = TimeSpan.FromSeconds(30)
        });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Router2API/1.0 model-discovery");
        var models = await registration.Terminal.GetModelsAsync(
            new ModelQueryContext(HttpClient: client, ForceRefresh: forceRefresh), cancellationToken);
        // Invalidation removes the slot. A late result from an old generation cannot republish it.
        if (registration.ModelCacheTtl > TimeSpan.Zero)
            Volatile.Write(ref slot.Value, new CacheEntry(registration.Terminal, models, DateTimeOffset.UtcNow.Add(registration.ModelCacheTtl)));
        return models;
    }

    public void Invalidate(string? platform = null)
    {
        if (platform is null) _cache.Clear();
        else _cache.TryRemove(platform, out _);
    }
    private sealed record CacheEntry(IPlatformTerminal Terminal, IReadOnlyList<ModelDescriptor> Models, DateTimeOffset ExpiresAt);
    private sealed class CacheSlot
    {
        public CacheEntry? Value;
    }
}

/// <summary>绑定到稳定 PluginKey 的插件宿主。</summary>
public sealed class PluginHost(
    string pluginKey,
    IModelCatalog modelCatalog,
    IAccountService accounts,
    ILogSink<ResourceEvent> audit,
    IPluginLogSink logs,
    IServiceProvider services,
    IPluginServices capabilities) : IPluginHost
{
    public string PluginKey { get; } = pluginKey;
    public IModelCatalog ModelCatalog { get; } = modelCatalog;
    public IAccountService Accounts { get; } = accounts;
    public ILogSink<ResourceEvent> Audit { get; } = audit;
    public IPluginLogSink Logs { get; } = logs;
    public IPluginServices Services { get; } = capabilities;

    public Task<T> GetServiceAsync<T>() where T : class
        => Task.FromResult(services.GetRequiredService<T>());
}

/// <summary>创建使用正确资源命名空间的插件宿主。</summary>
public sealed class PluginHostFactory(
    IModelCatalog modelCatalog,
    IAccountService accounts,
    ILogSink<ResourceEvent> audit,
    IPluginLogSink logs,
    IModelMetadataCatalog metadata,
    ProxyTransportFactory transport,
    IProxyPoolHttpClientFactory pool,
    ISharedKeyValueStore state,
    ITaskLogStore taskLogs,
    IOptions<PluginExecutionOptions> execution,
    IServiceProvider services,
    IResourceLeaseManager? leases = null,
    PluginHttpOriginStore? origins = null) : IPluginHostFactory
{
    public IPluginHost Create(string pluginKey) => Create(pluginKey, [pluginKey]);

    public IPluginHost Create(string pluginKey, IReadOnlyList<string> platforms)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginKey);
        ArgumentNullException.ThrowIfNull(platforms);
        var capabilities = new PluginServices(pluginKey, platforms, accounts, modelCatalog, metadata,
            transport, pool, state, () => services.GetRequiredService<IPluginTaskInvoker>(), taskLogs, logs, execution.Value, leases, origins);
        return new PluginHost(
            pluginKey,
            modelCatalog,
            accounts,
            audit,
            logs,
            services,
            capabilities);
    }
}
