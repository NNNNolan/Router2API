using System.Collections.Concurrent;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Router.Infrastructure.Services;

public sealed class PluginPolicyRegistry : IPluginPolicyRegistry
{
    private static readonly PluginPolicySnapshot Default = new(PluginProxyPolicy.Default, new PluginAccountPolicy(null, null, null));
    private readonly ConcurrentDictionary<string, PluginPolicySnapshot> _policies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Plugin, string Platform), PluginPolicySnapshot> _platformPolicies = new();

    public void Set(string pluginKey, PluginProxyPolicy policy, PluginAccountPolicy? accountPolicy = null)
    {
        _policies[pluginKey] = new PluginPolicySnapshot(policy, accountPolicy ?? Default.Accounts);
    }

    public void Remove(string pluginKey)
    {
        _policies.TryRemove(pluginKey, out _);
        foreach (var key in _platformPolicies.Keys.Where(key => key.Plugin.Equals(pluginKey, StringComparison.OrdinalIgnoreCase)))
            _platformPolicies.TryRemove(key, out _);
    }

    public PluginProxyPolicy Get(string pluginKey)
        => GetSnapshot(pluginKey).Attempts;

    public PluginAccountPolicy GetAccount(string pluginKey)
        => GetSnapshot(pluginKey).Accounts;

    public PluginPolicySnapshot GetSnapshot(string pluginKey) => _policies.GetValueOrDefault(pluginKey, Default);

    public void Set(string pluginKey, string platform, PluginProxyPolicy policy, PluginAccountPolicy? accountPolicy = null)
        => _platformPolicies[(pluginKey.ToLowerInvariant(), platform.ToLowerInvariant())] = new(policy, accountPolicy ?? Default.Accounts);

    public PluginPolicySnapshot GetSnapshot(string pluginKey, string platform)
        => _platformPolicies.GetValueOrDefault((pluginKey.ToLowerInvariant(), platform.ToLowerInvariant()), GetSnapshot(pluginKey));
}
