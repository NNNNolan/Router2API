using Router.Contracts.Host;
using Router.Contracts.Plugins;
using Router.Host.Plugins.JavaScript;

namespace Router.Host.Plugins;

/// <summary>只负责 JS manifest、安全快照和 Jint 导出检查，不处理端点认证或版本切换。</summary>
internal sealed class JintPackageLoader(IPluginHostFactory hosts) : IPluginPackageLoader
{
    public string Runtime => "jint";
    public bool CanLoad(string sourceDirectory, string name) => JsPluginPackage.IsJavaScriptDirectory(sourceDirectory);

    public async Task<LoadedPlugin> LoadAsync(string name, string sourceDirectory, string snapshotDirectory, CancellationToken cancellationToken)
    {
        var source = await JsPluginPackage.ReadAsync(sourceDirectory, name, cancellationToken);
        await source.WriteSnapshotAsync(snapshotDirectory, cancellationToken);
        var manifest = source.Manifest;
        var host = hosts.Create(manifest.Id, manifest.DeclaredPlatforms.Select(platform => platform.Name).ToArray());
        var created = new List<JsPlatformTerminal>();
        try
        {
            var platforms = new List<LoadedPlatform>();
            var endpoints = new List<PluginEndpointDefinition>();
            foreach (var definition in manifest.DeclaredPlatforms)
            {
                var selected = manifest.ForPlatform(definition);
                var terminal = new JsPlatformTerminal(source with { Manifest = selected }, host);
                created.Add(terminal);
                await terminal.ValidateAsync(cancellationToken);
                var builder = new PluginBuilder();
                terminal.Configure(builder);
                platforms.Add(new LoadedPlatform(definition.Name, manifest.Id,
                    string.IsNullOrWhiteSpace(definition.DisplayName) ? manifest.Name : definition.DisplayName,
                    definition.ProbeEndpoint, terminal, host, builder, [.. terminal.ScheduledTasks, .. builder.Tasks],
                    TimeSpan.FromSeconds(definition.ModelCacheTtlSeconds)));
                endpoints.AddRange(selected.Endpoints.Select(endpoint => new PluginEndpointDefinition(manifest.Id, definition.Name,
                    endpoint.Method, PluginEndpointDefinition.NormalizeRoute(endpoint.Path), Enum.Parse<PluginAuthPolicy>(endpoint.Auth),
                    context => terminal.InvokeEndpointAsync(endpoint.Handler, context))));
            }
            var package = new LoadedPlugin(manifest.Id, manifest.Version, Runtime, platforms, endpoints,
                created.Select(terminal => terminal.MainPage).FirstOrDefault(page => page is not null));
            package.Validate();
            return package;
        }
        catch
        {
            foreach (var terminal in created) await terminal.DisposeAsync();
            throw;
        }
    }
}
