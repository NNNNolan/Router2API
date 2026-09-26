using System.Reflection;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;

namespace Router.Host.Plugins;

/// <summary>只负责 DLL 快照、契约检查、反射发现及 CLR 运行时所有权。</summary>
internal sealed class DotNetPackageLoader(IPluginHostFactory hosts) : IPluginPackageLoader
{
    public string Runtime => "dotnet";
    public bool CanLoad(string sourceDirectory, string name) => FindMainAssembly(sourceDirectory, name) is not null;

    public async Task<LoadedPlugin> LoadAsync(string name, string sourceDirectory, string snapshotDirectory, CancellationToken cancellationToken)
    {
        var mainAssembly = FindMainAssembly(sourceDirectory, name)
            ?? throw new InvalidOperationException("No unambiguous main plugin assembly.");
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(snapshotDirectory, Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
        var assemblyPath = Path.Combine(snapshotDirectory, Path.GetFileName(mainAssembly));
        var loadContext = new PluginAssemblyLoadContext(assemblyPath);
        var created = new List<LoadedPlatform>();
        LoadedPlugin? package = null;
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            ValidateContract(assembly);
            var types = assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(IPlatformTerminal).IsAssignableFrom(type))
                .Select(type => (Type: type, Attribute: type.GetCustomAttribute<PlatformAdapterAttribute>()))
                .Where(item => item.Attribute is not null).ToArray();
            if (types.Length == 0 || types.Any(item => !name.Equals(item.Attribute!.PluginKey, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("All terminal PluginKeys must match the package directory.");
            var pluginKey = types[0].Attribute!.PluginKey;
            var host = hosts.Create(pluginKey, types.Select(item => item.Attribute!.Name).ToArray());
            foreach (var (type, attribute) in types)
            {
                var terminal = (IPlatformTerminal)Activator.CreateInstance(type, host)!;
                var builder = new PluginBuilder();
                var cache = type.GetCustomAttribute<ModelCacheAttribute>();
                var ttl = cache?.Disabled == true ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Clamp(cache?.TtlSeconds ?? 300, 1, 86400));
                // Add immediately: Configure, page and discovery can throw before StartAsync is entered.
                created.Add(new LoadedPlatform(attribute!.Name, pluginKey, attribute.DisplayName, attribute.ProbeEndpoint,
                    terminal, host, builder, [], ttl));
                if (terminal is IPluginModule module) module.Configure(builder);
                created[^1] = created[^1] with { Tasks = [.. CreateScheduledTasks(terminal), .. builder.Tasks] };
            }
            package = new LoadedPlugin(pluginKey, assembly.GetName().Version?.ToString() ?? "0.0.0", Runtime, created,
                created.SelectMany(CreateEndpoints).ToArray(),
                created.Select(platform => (platform.Terminal as IPluginMainPageProvider)?.GetMainPage()).FirstOrDefault(page => page is not null),
                loadContext.Unload);
            package.Validate();
            return package;
        }
        catch
        {
            package ??= new LoadedPlugin(name, "unknown", Runtime, created, [], null, loadContext.Unload);
            try { await package.DisposeAsync(); }
            catch { /* Preserve the load error after attempting cleanup of every created terminal. */ }
            throw;
        }
    }

    private static IEnumerable<PluginEndpointDefinition> CreateEndpoints(LoadedPlatform platform)
    {
        foreach (var method in platform.Terminal.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = method.GetCustomAttribute<PluginEndpointAttribute>();
            if (attribute is null) continue;
            var parameters = method.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType != typeof(PluginHttpContext)
                || method.ReturnType != typeof(Task<PluginResult>))
                throw new InvalidOperationException($"Plugin endpoint {method.Name} must return Task<PluginResult> and accept PluginHttpContext.");
            var verb = attribute.Method.ToUpperInvariant();
            if (verb is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD"))
                throw new InvalidOperationException($"Unsupported plugin endpoint method {verb}.");
            yield return new PluginEndpointDefinition(platform.PluginKey, platform.Name, verb,
                PluginEndpointDefinition.NormalizeRoute(attribute.Path), attribute.Auth,
                method.CreateDelegate<Func<PluginHttpContext, Task<PluginResult>>>(platform.Terminal));
        }
    }

    internal static ScheduledTaskRegistration[] CreateScheduledTasks(IPlatformTerminal terminal)
    {
        var attributed = terminal.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<ScheduledTaskAttribute>()))
            .Where(item => item.Attribute is not null)
            .Select(item =>
            {
                var parameters = item.Method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(PluginScheduledTaskContext)
                    || !typeof(Task).IsAssignableFrom(item.Method.ReturnType))
                    throw new InvalidOperationException($"Scheduled task {item.Method.Name} must return Task and accept PluginScheduledTaskContext.");
                return new ScheduledTaskRegistration(item.Attribute!.Name, item.Attribute.Cron,
                    item.Method.CreateDelegate<Func<PluginScheduledTaskContext, Task>>(terminal), item.Attribute.Description);
            });
        return (terminal is IPluginScheduledTaskProvider provider ? provider.ScheduledTasks : [])
            .Concat(attributed).DistinctBy(task => task.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? FindMainAssembly(string directory, string name)
    {
        var assemblies = Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).ToArray();
        var named = assemblies.FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(name, StringComparison.OrdinalIgnoreCase));
        if (named is not null) return named;
        var manifests = Directory.EnumerateFiles(directory, "*.deps.json", SearchOption.TopDirectoryOnly)
            .Select(path => Path.Combine(directory, Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)) + ".dll"))
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return manifests.Length == 1 ? manifests[0] : assemblies.Length == 1 ? assemblies[0] : null;
    }

    private static void ValidateContract(Assembly assembly)
    {
        var contract = assembly.GetCustomAttribute<PluginContractAttribute>();
        if (contract is null) return;
        if (!Version.TryParse(contract.MinVersion, out var minimum) || !Version.TryParse(contract.MaxVersion, out var maximum)
            || new Version(2, 0) < minimum || new Version(2, 0) > maximum)
            throw new InvalidOperationException($"Plugin contract {contract.MinVersion}-{contract.MaxVersion} is not supported.");
    }
}
