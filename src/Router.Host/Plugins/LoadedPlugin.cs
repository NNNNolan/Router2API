using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;
using Router.Infrastructure.Services;

namespace Router.Host.Plugins;

/// <summary>两种运行时输出相同的已验证包；Catalog 不再反射 DLL 或解释 JS manifest。</summary>
internal interface IPluginPackageLoader
{
    string Runtime { get; }
    bool CanLoad(string sourceDirectory, string name);
    Task<LoadedPlugin> LoadAsync(string name, string sourceDirectory, string snapshotDirectory, CancellationToken cancellationToken);
}

internal sealed record LoadedPlatform(
    string Name, string PluginKey, string DisplayName, string? ProbeEndpoint,
    IPlatformTerminal Terminal, IPluginHost Host, PluginBuilder Configuration,
    IReadOnlyList<ScheduledTaskRegistration> Tasks, TimeSpan ModelCacheTtl);

/// <summary>已绑定的端点调用，不依赖 ASP.NET、Jint 或反射。</summary>
internal sealed record PluginEndpointDefinition(
    string PluginKey, string Platform, string Method, string Route, PluginAuthPolicy Auth,
    Func<PluginHttpContext, Task<PluginResult>> InvokeAsync)
{
    public static string NormalizeRoute(string value)
    {
        var route = value.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(route) || route.Length > 200
            || route.Contains("..", StringComparison.Ordinal) || route.Contains('\\') || route.Contains('?') || route.Contains('#')
            || route.Contains("://", StringComparison.Ordinal) || route.StartsWith("api/admin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Plugin route '{value}' is not allowed.");
        return route;
    }
}

/// <summary>候选版本的完整所有权；部分启动失败也能停止已进入的 hook 并释放未启动的终端。</summary>
internal sealed class LoadedPlugin(
    string pluginKey, string version, string runtime,
    IReadOnlyList<LoadedPlatform> platforms,
    IReadOnlyList<PluginEndpointDefinition> endpoints,
    PluginMainPage? mainPage,
    Action? releaseRuntime = null) : IAsyncDisposable
{
    private readonly HashSet<IPluginModule> _started = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;
    public string PluginKey { get; } = pluginKey;
    public string Version { get; } = version;
    public string Runtime { get; } = runtime;
    public string? Description { get; init; }
    public IReadOnlyList<LoadedPlatform> Platforms { get; } = platforms;
    public IReadOnlyList<PluginEndpointDefinition> Endpoints { get; } = endpoints;
    public PluginMainPage? MainPage { get; } = mainPage;
    private IEnumerable<PluginJobManager> JobManagers => Platforms.Select(platform => platform.Host.Services.Jobs).OfType<PluginJobManager>().Distinct();
    public int JobInFlight => JobManagers.Sum(manager => manager.InFlight);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var group in Platforms.GroupBy(platform => platform.Host))
        {
            var jobs = group.SelectMany(platform => platform.Configuration.Jobs).ToArray();
            if (jobs.Length > 0)
            {
                if (group.Key.Services.Jobs is not PluginJobManager manager)
                    throw new InvalidOperationException("Host does not support registered background jobs.");
                manager.Register(jobs);
            }
        }
        foreach (var platform in Platforms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (platform.Terminal is not IPluginModule module || !_started.Add(module)) continue;
            await module.StartAsync(new PluginStartContext(PluginKey, platform.Host), cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? errors = null;
        try
        {
            foreach (var manager in JobManagers)
            {
                try { await manager.DisposeAsync(); }
                catch (Exception exception) { (errors ??= []).Add(exception); }
            }
            foreach (var terminal in Platforms.Select(platform => platform.Terminal).Distinct().Reverse())
            {
                try
                {
                    // Legacy module StopAsync owns its cleanup; don't dispose it a second time.
                    if (terminal is IPluginModule module && _started.Contains(module))
                        await module.StopAsync(CancellationToken.None);
                    else if (terminal is IAsyncDisposable asyncDisposable)
                        await asyncDisposable.DisposeAsync();
                    else if (terminal is IDisposable disposable)
                        disposable.Dispose();
                }
                catch (Exception exception) { (errors ??= []).Add(exception); }
            }
        }
        finally { releaseRuntime?.Invoke(); }
        if (errors is not null) throw new AggregateException("Plugin cleanup failed.", errors);
    }

    public Task DrainJobsAsync(CancellationToken cancellationToken)
        => Task.WhenAll(JobManagers.Select(manager => manager.DrainAsync(cancellationToken)));

    public void ResumeJobs()
    {
        foreach (var manager in JobManagers) manager.Resume();
    }

    public PluginDescriptor Describe(string name, string directory) => new(
        PluginKey, name, Version, "Active", directory, DateTimeOffset.UtcNow, 0,
        HasMainPage: MainPage is not null, MainPageTitle: MainPage?.Title, MainPageVersion: MainPage?.Version,
        Tasks: Platforms.SelectMany(platform => platform.Tasks)
            .Select(task => new PluginTaskDescriptor(task.Name, task.Cron, task.Description))
            .DistinctBy(task => task.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
        Routes: Endpoints.Where(endpoint => endpoint.Auth != PluginAuthPolicy.Internal)
            .Select(endpoint => endpoint.Route).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        { Runtime = Runtime, Description = Description };

    public void Validate()
    {
        if (Platforms.Count == 0 || Platforms.Any(platform => !PluginKey.Equals(platform.PluginKey, StringComparison.OrdinalIgnoreCase))
            || Platforms.Select(platform => platform.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Platforms.Count)
            throw new InvalidOperationException("A package must declare unique platforms under one PluginKey.");
        if (Endpoints.Select(endpoint => $"{endpoint.Method}:{endpoint.Route}").Distinct(StringComparer.OrdinalIgnoreCase).Count() != Endpoints.Count)
            throw new InvalidOperationException("Duplicate plugin endpoint.");
        foreach (var platform in Platforms)
        {
            if (platform.Tasks.Select(task => task.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != platform.Tasks.Count)
                throw new InvalidOperationException("Duplicate plugin task name.");
            foreach (var task in platform.Tasks)
                _ = Router.Host.Services.CronSchedule.GetNext(task.Cron, DateTimeOffset.UtcNow);
            if (platform.Configuration.Jobs.Any(job => job.Platform != platform.Name
                || job.Timeout <= TimeSpan.Zero || job.Timeout > TimeSpan.FromHours(1)))
                throw new InvalidOperationException("Invalid job platform or timeout.");
            if (platform.Configuration.Jobs.Select(job => job.Name).Distinct(StringComparer.Ordinal).Count() != platform.Configuration.Jobs.Count)
                throw new InvalidOperationException("Duplicate background job name.");
        }
    }
}
