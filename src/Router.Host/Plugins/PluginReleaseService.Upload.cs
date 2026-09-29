using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Host;
using Router.Host.Plugins.JavaScript;

namespace Router.Host.Plugins;

public sealed partial class PluginReleaseService
{
    /// <summary>安装管理员上传的单插件 ZIP，替换同 ID 旧包并激活；失败由目录安装器回滚。</summary>
    public async Task<PluginDescriptor> UploadAsync(Stream input, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        var work = Path.Combine(SubscriptionRoot, ".uploads", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            var archivePath = Path.Combine(work, "package.zip");
            await using (var output = File.Create(archivePath))
                await CopyBoundedAsync(input, output, MaxArchiveBytes, token);
            var package = Path.Combine(work, "package");
            using (var zip = ZipFile.OpenRead(archivePath))
                await ExtractPackageAsync(zip, package, UploadPrefix(zip), token);
            var entry = await InspectUploadAsync(package, token);
            var state = await ReadStateAsync(token);
            await catalog.InstallPackageAsync(entry, package, token, activate: true);
            // 本地包替换后不再冒充原 GitHub 发行版；保留已订阅仓库本身。
            if (state.Installations.RemoveAll(item => item.PluginId.Equals(entry.Id, StringComparison.OrdinalIgnoreCase)) > 0)
                await WriteStateAsync(state, token);
            return catalog.Get(entry.Id)!;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            finally { _gate.Release(); }
        }
    }

    internal static string UploadPrefix(ZipArchive zip)
    {
        var files = zip.Entries.Where(entry => !entry.FullName.EndsWith('/')).Select(entry => entry.FullName).ToArray();
        if (files.Any(path => !path.Contains('/') && (path == "plugin.json" || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))))
            return "";
        var folders = files.Select(path => path.Split('/')[0]).Distinct(StringComparer.Ordinal).ToArray();
        if (folders.Length != 1 || files.Any(path => !path.Contains('/')))
            throw new InvalidDataException("ZIP must contain one plugin at its root or inside one top-level directory.");
        return folders[0] + "/";
    }

    internal static async Task<PluginReleaseEntry> InspectUploadAsync(string package, CancellationToken token)
    {
        string? id = null, name = null, description = null, version = null, runtime = null;
        var manifestPath = Path.Combine(package, "plugin.json");
        if (Directory.EnumerateFiles(package, "plugin.json", SearchOption.AllDirectories)
            .Any(path => path != manifestPath))
            throw new InvalidDataException("Nested plugin packages are not supported; upload one plugin at a time.");
        if (File.Exists(manifestPath))
        {
            if (new FileInfo(manifestPath).Length > 64 * 1024)
                throw new InvalidDataException("Plugin manifest exceeds 64 KiB.");
            try
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, token),
                    new JsonDocumentOptions { MaxDepth = 16 });
                var root = json.RootElement;
                id = ReadString(root, "id");
                runtime = ReadString(root, "runtime");
                name = ReadString(root, "name");
                description = ReadString(root, "description");
                version = ReadString(root, "version");
                if (runtime is not ("jint" or "dotnet") || !SafeId(id ?? ""))
                    throw new InvalidDataException("Plugin manifest must declare a valid id and runtime (jint/dotnet).");
            }
            catch (JsonException exception) { throw new InvalidDataException("Invalid plugin.json.", exception); }
        }

        if (runtime == "jint")
        {
            if (Directory.EnumerateFiles(package, "*.dll").Any(path => ReadAssemblyIdentity(path) is not null))
                throw new InvalidDataException("ZIP mixes JavaScript and C# plugin entries.");
            // 验证 JS 清单和文件边界，不运行任何插件代码。
            try { _ = await JsPluginPackage.ReadAsync(package, id!, token); }
            catch (Exception exception) when (exception is JsonException or FileNotFoundException or DirectoryNotFoundException)
            { throw new InvalidDataException("JS plugin manifest or required assets are invalid.", exception); }
        }
        else
        {
            // 只读 PE 元数据，识别 DLL 的 PlatformAdapter，不执行构造函数或模块初始化器。
            var candidates = new List<(string Path, string Id, string Version)>();
            foreach (var dll in Directory.EnumerateFiles(package, "*.dll"))
            {
                token.ThrowIfCancellationRequested();
                var identity = ReadAssemblyIdentity(dll);
                if (identity is { } found) candidates.Add((dll, found.Id, found.Version));
            }
            if (candidates.Count != 1)
                throw new InvalidDataException("ZIP must contain exactly one C# plugin assembly (private dependency DLLs are allowed).");
            var candidate = candidates[0];
            if (id is not null && id != candidate.Id)
                throw new InvalidDataException("Manifest id does not match the assembly PluginKey.");
            id = candidate.Id;
            runtime = "dotnet";
            version ??= candidate.Version;
            // 补齐无清单的 C# 包，并记录主程序集，避免多个依赖 DLL 导致 loader 无法识别入口。
            var manifest = File.Exists(manifestPath)
                ? JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, token))!.AsObject()
                : new JsonObject { ["id"] = id, ["runtime"] = runtime, ["name"] = name ?? id, ["version"] = version };
            manifest["assembly"] = Path.GetFileName(candidate.Path);
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonOptions), token);
        }
        if (!SafeId(id ?? "")) throw new InvalidDataException("Invalid plugin id.");
        return new PluginReleaseEntry(id!, name ?? id!, description ?? "", runtime!, version ?? "unknown",
            id + ".zip", "", 0);
    }

    private static string? ReadString(JsonElement root, string key)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static (string Id, string Version)? ReadAssemblyIdentity(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null; // 原生依赖不是插件入口。
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly) return null;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                if ((type.Attributes & TypeAttributes.Abstract) != 0) continue;
                foreach (var attributeHandle in type.GetCustomAttributes())
                {
                    var attribute = metadata.GetCustomAttribute(attributeHandle);
                    if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                    var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                    var reference = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                    if (metadata.GetString(reference.Namespace) != "Router.Contracts.Plugins"
                        || metadata.GetString(reference.Name) != "PlatformAdapterAttribute") continue;
                    var blob = metadata.GetBlobReader(attribute.Value);
                    if (blob.ReadUInt16() != 1) throw new InvalidDataException("Invalid PlatformAdapter attribute.");
                    var id = blob.ReadSerializedString();
                    var count = blob.ReadUInt16();
                    for (var index = 0; index < count; index++)
                    {
                        if (blob.ReadByte() is not (0x53 or 0x54) || blob.ReadByte() != 0x0e)
                            throw new InvalidDataException("Unsupported PlatformAdapter attribute field.");
                        var key = blob.ReadSerializedString();
                        var value = blob.ReadSerializedString();
                        if (key == "PluginKey") id = value;
                    }
                    if (!SafeId(id ?? "")) throw new InvalidDataException("Assembly contains an invalid PluginKey.");
                    ids.Add(id!);
                }
            }
            if (ids.Count == 0) return null;
            if (ids.Count != 1) throw new InvalidDataException("All platforms in a package must use the same PluginKey.");
            return (ids.Single(), metadata.GetAssemblyDefinition().Version.ToString());
        }
        catch (BadImageFormatException) { return null; }
    }
}
