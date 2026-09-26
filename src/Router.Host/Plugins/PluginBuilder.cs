using Router.Contracts.Plugins;

namespace Router.Host.Plugins;

internal sealed class PluginBuilder : IPluginBuilder
{
    private readonly List<ScheduledTaskRegistration> _tasks = [];
    private readonly List<PluginJobRegistration> _jobs = [];

    public PluginProxyPolicy BuiltProxyPolicy { get; private set; } = PluginProxyPolicy.Default;
    public PluginAccountPolicy BuiltAccountPolicy { get; private set; } = new(null, null, null);
    public IReadOnlyList<ScheduledTaskRegistration> Tasks => _tasks;
    public IReadOnlyList<PluginJobRegistration> Jobs => _jobs;

    public void ProxyPolicy(Action<PluginProxyPolicyBuilder> configure)
    {
        var builder = new PluginProxyPolicyBuilder();
        configure(builder);
        BuiltProxyPolicy = builder.Build();
    }

    public void AccountPolicy(Action<PluginAccountPolicyBuilder> configure)
    {
        var builder = new PluginAccountPolicyBuilder();
        configure(builder);
        BuiltAccountPolicy = builder.Build();
    }

    public void ScheduledTask(ScheduledTaskRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _tasks.Add(registration);
    }

    public void Job(PluginJobRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _jobs.Add(registration);
    }
}
