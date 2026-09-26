using System.Reflection;
using System.Runtime.Loader;

namespace Router.Host.Plugins;

/// <summary>与宿主共享契约程序集的可回收加载上下文。</summary>
public sealed class PluginAssemblyLoadContext(string mainAssemblyPath)
    : AssemblyLoadContext($"plugin:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}:{Guid.NewGuid():N}", isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);
    private readonly string _pluginDirectory = Path.GetDirectoryName(Path.GetFullPath(mainAssemblyPath))!;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var contractsName = typeof(Router.Contracts.Host.IPluginHost).Assembly.GetName().Name;
        if (string.Equals(assemblyName.Name, contractsName, StringComparison.OrdinalIgnoreCase))
            return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is not null)
        {
            var fullPath = Path.GetFullPath(path);
            if (!IsInsidePluginDirectory(fullPath))
                throw new FileLoadException($"plugin dependency '{assemblyName.Name}' resolved outside its plugin directory", fullPath);
            return LoadFromAssemblyPath(fullPath);
        }

        if (IsFrameworkAssembly(assemblyName.Name)) return null;
        throw new FileNotFoundException($"plugin dependency '{assemblyName.Name}' is missing from its plugin directory", assemblyName.Name);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is null) return IntPtr.Zero;

        var fullPath = Path.GetFullPath(path);
        if (!IsInsidePluginDirectory(fullPath))
            throw new DllNotFoundException($"plugin native dependency '{unmanagedDllName}' resolved outside its plugin directory");
        return LoadUnmanagedDllFromPath(fullPath);
    }

    private bool IsInsidePluginDirectory(string path)
        => path.StartsWith(_pluginDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, _pluginDirectory, StringComparison.OrdinalIgnoreCase);

    private static bool IsFrameworkAssembly(string? name)
        => name is "System.Private.CoreLib" or "mscorlib" or "netstandard" or "System"
            || name?.StartsWith("System.", StringComparison.OrdinalIgnoreCase) == true;
}
