using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Infrastructure.Services;

/// <summary>插件版本的宿主能力实现。全局基础设施只在宿主组合根中解析。</summary>
internal sealed class PluginServices : IPluginServices
{
    public string PluginKey { get; }
    public IPluginAccounts Accounts { get; }
    public IPluginHttpServices Http { get; }
    public PluginStateServices State { get; }
    public IPluginModels Models { get; }
    public IPluginTasks Tasks { get; }
    public IPluginJobs Jobs { get; }
    public IPluginLogSink Log { get; }
    public PluginExecutionOptions Execution { get; }

    public PluginServices(string pluginKey, IReadOnlyList<string> platforms,
        IAccountService accounts, IModelCatalog models, IModelMetadataCatalog metadata,
        ProxyTransportFactory transport, IProxyPoolHttpClientFactory pool, ISharedKeyValueStore state,
        Func<IPluginTaskInvoker> tasks, ITaskLogStore taskLogs, IPluginLogSink logs, PluginExecutionOptions execution,
        IResourceLeaseManager? leases = null, PluginHttpOriginStore? origins = null)
    {
        PluginKey = pluginKey;
        var scope = new PluginScope(pluginKey, platforms);
        Accounts = new ScopedAccounts(scope, accounts, leases ?? new ResourceLeaseManager());
        Http = new HttpServices(pluginKey, transport, pool, origins);
        State = new PluginStateServices(new PluginMemoryState(), new PluginSharedState(pluginKey, state));
        Models = new ScopedModels(scope, models, metadata);
        Tasks = new ScopedTasks(scope, tasks, taskLogs);
        Log = new ScopedLog(scope, logs);
        Jobs = new PluginJobManager(pluginKey, Log);
        Execution = execution;
    }

    private sealed class PluginScope(string pluginKey, IReadOnlyList<string> platforms)
    {
        private readonly HashSet<string> _platforms = new(platforms, StringComparer.OrdinalIgnoreCase);
        public string Key { get; } = pluginKey;
        public void RequireKey(string value)
        {
            if (!Key.Equals(value, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The resource belongs to another plugin.");
        }
        public void RequirePlatform(string? value, bool optional = false)
        {
            if (value is null && optional) return;
            if (value is null || !_platforms.Contains(value))
                throw new UnauthorizedAccessException("The platform is not declared by this plugin.");
        }
    }

    private sealed class ScopedAccounts(PluginScope scope, IAccountService accounts, IResourceLeaseManager leases) : IPluginAccounts
    {
        public Task<Account?> GetAsync(string id, CancellationToken cancellationToken = default)
            => accounts.GetAsync(scope.Key, id, cancellationToken);

        public Task<IReadOnlyList<Account>> ListAsync(string? platform = null, CancellationToken cancellationToken = default)
        {
            scope.RequirePlatform(platform, optional: true);
            return accounts.ListAsync(scope.Key, platform, cancellationToken);
        }

        public Task<Account> SaveAsync(Account account, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(account);
            scope.RequireKey(account.PluginKey);
            scope.RequirePlatform(account.Platform);
            return accounts.SaveAsync(account, cancellationToken);
        }
        public Task<Account> PatchAsync(Account account, IReadOnlyList<string> fields, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(account);
            scope.RequireKey(account.PluginKey);
            scope.RequirePlatform(account.Platform);
            return accounts.PatchAsync(account, fields, cancellationToken);
        }

        public Task<Account> RefreshAsync(string id, Func<Account, CancellationToken, Task<Account>> refresh, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(refresh);
            return accounts.RefreshAsync(scope.Key, id, async (current, token) =>
            {
                var platform = current.Platform;
                var result = await refresh(current, token);
                if (result is null || !id.Equals(result.Id, StringComparison.OrdinalIgnoreCase)
                    || !platform.Equals(result.Platform, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Account refresh cannot change identity or platform.");
                scope.RequireKey(result.PluginKey);
                scope.RequirePlatform(result.Platform);
                return result;
            }, cancellationToken);
        }

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
            => accounts.DeleteAsync(scope.Key, id, cancellationToken);
        public Task SetCooldownAsync(string id, DateTimeOffset until, string reason, int? statusCode = null, CancellationToken cancellationToken = default)
            => accounts.SetCooldownAsync(scope.Key, id, until, reason, statusCode, cancellationToken);
        public Task<bool> ClearCooldownAsync(string id, string expectedReason, CancellationToken cancellationToken = default)
            => accounts.ClearCooldownAsync(scope.Key, id, expectedReason, cancellationToken);
        public Task DisableAsync(string id, string reason, int? statusCode = null, CancellationToken cancellationToken = default)
            => accounts.DisableAsync(scope.Key, id, reason, statusCode, cancellationToken);

        public Task<Account?> CompareExchangeCredentialAsync(string id, long expectedVersion, Credential credential, CancellationToken cancellationToken = default)
            => accounts.CompareExchangeCredentialAsync(scope.Key, id, expectedVersion, credential, cancellationToken);

        public Task<Account> RefreshCredentialAsync(string id, Func<Account, CancellationToken, Task<Credential>> refresh,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(refresh);
            return leases.WithExclusiveAsync($"credential-refresh:{scope.Key}:{id}", async token =>
            {
                var account = await accounts.GetAsync(scope.Key, id, token) ?? throw new KeyNotFoundException("Account not found.");
                scope.RequirePlatform(account.Platform);
                var revision = account.CredentialVersion;
                var updated = await refresh(account, token);
                return await accounts.CompareExchangeCredentialAsync(scope.Key, id, revision, updated, token)
                    ?? throw new InvalidOperationException("Account credentials changed during refresh.");
            }, cancellationToken);
        }
    }

    private sealed class HttpServices(string pluginKey, ProxyTransportFactory transport, IProxyPoolHttpClientFactory pool,
        PluginHttpOriginStore? origins) : IPluginHttpServices
    {
        public IProxyPoolHttpClientFactory Pool { get; } = pool;
        public HttpClient CreateDirectClient(PluginHttpClientOptions? options = null) => transport.CreateClient(null, options);
        public Task<IReadOnlyList<string>> GetApprovedOriginsAsync(CancellationToken cancellationToken = default)
            => RequireOrigins().ListAsync(pluginKey, cancellationToken);
        public Task ApproveOriginAsync(string origin, CancellationToken cancellationToken = default)
            => RequireOrigins().ApproveAsync(pluginKey, origin, cancellationToken);
        public Task RevokeOriginAsync(string origin, CancellationToken cancellationToken = default)
            => RequireOrigins().RevokeAsync(pluginKey, origin, cancellationToken);
        private PluginHttpOriginStore RequireOrigins() => origins ?? throw new NotSupportedException("Origin approvals are not configured.");
    }

    private sealed class ScopedModels(PluginScope scope, IModelCatalog models, IModelMetadataCatalog metadata) : IPluginModels
    {
        public Task<IReadOnlyList<ModelDescriptor>> ListAsync(string platform, CancellationToken cancellationToken = default)
        {
            scope.RequirePlatform(platform);
            return models.ListAsync(platform, cancellationToken);
        }
        public Task<IReadOnlyList<ModelDescriptor>> RefreshAsync(string platform, CancellationToken cancellationToken = default)
        {
            scope.RequirePlatform(platform);
            return models.RefreshAsync(platform, cancellationToken);
        }
        public void Invalidate(string platform)
        {
            scope.RequirePlatform(platform);
            models.Invalidate(platform);
        }
        public Task<ModelMetadataSnapshot> GetMetadataAsync(CancellationToken cancellationToken = default)
            => metadata.GetAsync(cancellationToken: cancellationToken);
    }

    private sealed class ScopedTasks(PluginScope scope, Func<IPluginTaskInvoker> tasks, ITaskLogStore logs) : IPluginTasks
    {
        public Task<bool> RunAsync(string taskName, string? platform = null, CancellationToken cancellationToken = default)
        {
            scope.RequirePlatform(platform, optional: true);
            return tasks().RunAsync(scope.Key, taskName, platform, cancellationToken);
        }
        public Task WriteLogAsync(TaskLog log, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(log);
            scope.RequireKey(log.PluginKey);
            scope.RequirePlatform(log.Platform, optional: true);
            return logs.WriteAsync(log, cancellationToken);
        }
    }

    private sealed class ScopedLog(PluginScope scope, IPluginLogSink logs) : IPluginLogSink
    {
        public Task WriteAsync(PluginLog log, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(log);
            scope.RequireKey(log.PluginKey);
            scope.RequirePlatform(log.Platform, optional: true);
            return logs.WriteAsync(log, cancellationToken);
        }
    }
}
