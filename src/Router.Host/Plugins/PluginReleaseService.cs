using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Router.Host.Plugins;

public sealed record PluginRepository(string Owner, string Repo);
public sealed record PluginReleaseSummary(string Tag, DateTimeOffset? PublishedAt);
public sealed record PluginReleaseEntry(string Id, string Name, string Description, string Runtime,
    string Version, string Asset, string Sha256, long SizeBytes, string? ContentSha256 = null);
public sealed record PluginReleaseIndex(int SchemaVersion, string Tag, PluginReleaseEntry[] Plugins);
public sealed record PluginInstallation(string PluginId, string Owner, string Repo, string Tag, string Sha256,
    string Description, string? ContentSha256 = null);
public sealed record PluginAvailableUpdate(PluginInstallation Installed, PluginReleaseEntry Available, string Tag);

/// <summary>读取公开 GitHub Release 索引，并将用户选择的单个包安装到宿主插件目录。</summary>
public sealed partial class PluginReleaseService(IHttpClientFactory clients, PluginCatalog catalog) : IDisposable
{
    internal const long MaxArchiveBytes = 100 * 1024 * 1024;
    private const long MaxExtractedBytes = 300 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root = catalog.PluginRoot;
    private string SubscriptionRoot => Path.Combine(_root, ".subscription");
    private string StatePath => Path.Combine(SubscriptionRoot, "subscriptions.json");

    public async Task<IReadOnlyList<PluginRepository>> RepositoriesAsync(CancellationToken token)
        => (await ReadStateAsync(token)).Repositories;

    public async Task<IReadOnlyList<PluginInstallation>> InstallationsAsync(CancellationToken token)
        => (await ReadStateAsync(token)).Installations;

    public async Task<PluginRepository> AddRepositoryAsync(string owner, string repo, CancellationToken token)
    {
        ValidateRepository(owner, repo);
        _ = await ReleasesAsync(owner, repo, token); // Also confirms that GitHub can see the repository.
        await _gate.WaitAsync(token);
        try
        {
            var state = await ReadStateAsync(token);
            if (!state.Repositories.Any(item => SameRepository(item.Owner, item.Repo, owner, repo)))
            {
                state.Repositories.Add(new PluginRepository(owner, repo));
                await WriteStateAsync(state, token);
            }
            return new PluginRepository(owner, repo);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<PluginReleaseSummary>> ReleasesAsync(string owner, string repo, CancellationToken token)
    {
        ValidateRepository(owner, repo);
        using var document = await GetApiJsonAsync($"repos/{owner}/{repo}/releases?per_page=100", token);
        return document.RootElement.EnumerateArray()
            .Where(item => !item.GetProperty("draft").GetBoolean() && !item.GetProperty("prerelease").GetBoolean())
            .Select(item => new PluginReleaseSummary(item.GetProperty("tag_name").GetString() ?? "",
                item.TryGetProperty("published_at", out var date) && date.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(date.GetString(), out var published) ? published : null))
            .Where(item => item.Tag.Length > 0).ToArray();
    }

    public async Task<PluginReleaseIndex> IndexAsync(string owner, string repo, string tag, CancellationToken token)
    {
        using var release = await GetReleaseAsync(owner, repo, tag, token);
        var asset = AssetUrl(release, "release-index.json", owner, repo, tag);
        using var client = CreateClient();
        using var response = await client.GetAsync(asset, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(stream, buffer, 128 * 1024, token);
        buffer.Position = 0;
        var index = await JsonSerializer.DeserializeAsync<PluginReleaseIndex>(buffer, JsonOptions, token)
            ?? throw new InvalidDataException("Release index is empty.");
        if (index.SchemaVersion != 1 || index.Tag != tag || index.Plugins is null || index.Plugins.Length is 0 or > 100)
            throw new InvalidDataException("Release index version, tag or plugin list is invalid.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in index.Plugins)
        {
            if (plugin is null || !SafeId(plugin.Id) || !ids.Add(plugin.Id) || string.IsNullOrWhiteSpace(plugin.Name)
                || string.IsNullOrWhiteSpace(plugin.Description) || plugin.Runtime is not ("dotnet" or "jint")
                || string.IsNullOrWhiteSpace(plugin.Version) || plugin.Asset != plugin.Id + ".zip"
                || !assets.Add(plugin.Asset) || plugin.SizeBytes is <= 0 or > MaxArchiveBytes
                || !Regex.IsMatch(plugin.Sha256 ?? "", "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
                || plugin.ContentSha256 is not null && !Regex.IsMatch(plugin.ContentSha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Release index contains an invalid plugin entry.");
            _ = AssetUrl(release, plugin.Asset, owner, repo, tag);
        }
        return index;
    }

    public async Task<IReadOnlyList<PluginAvailableUpdate>> UpdatesAsync(CancellationToken token)
    {
        var state = await ReadStateAsync(token);
        var updates = new List<PluginAvailableUpdate>();
        foreach (var group in state.Installations.GroupBy(item => (item.Owner, item.Repo)))
        {
            var releases = await ReleasesAsync(group.Key.Owner, group.Key.Repo, token);
            var latest = releases.Count > 0 ? releases[0] : null;
            if (latest is null) continue;
            var index = await IndexAsync(group.Key.Owner, group.Key.Repo, latest.Tag, token);
            foreach (var installed in group)
            {
                if (catalog.Get(installed.PluginId) is null) continue;
                var available = index.Plugins.FirstOrDefault(item => item.Id == installed.PluginId);
                if (available is not null
                    && (available.ContentSha256 ?? available.Sha256) != (installed.ContentSha256 ?? installed.Sha256))
                    updates.Add(new PluginAvailableUpdate(installed, available, latest.Tag));
            }
        }
        return updates;
    }

    public async Task InstallAsync(string owner, string repo, string tag, IReadOnlyList<string> pluginIds, CancellationToken token)
    {
        if (pluginIds.Count is 0 or > 100 || pluginIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pluginIds.Count)
            throw new ArgumentException("Select one or more distinct plugins.");
        await _gate.WaitAsync(token);
        try
        {
            var state = await ReadStateAsync(token);
            if (!state.Repositories.Any(item => SameRepository(item.Owner, item.Repo, owner, repo)))
                throw new InvalidOperationException("Subscribe to this repository first.");
            var index = await IndexAsync(owner, repo, tag, token);
            if (pluginIds.Any(id => index.Plugins.All(item => item.Id != id)))
                throw new ArgumentException("A selected plugin is not in this release.");
            if (pluginIds.Any(id => catalog.Get(id) is not null && state.Installations.Any(item => item.PluginId == id
                && !SameRepository(item.Owner, item.Repo, owner, repo))))
                throw new InvalidOperationException("A selected plugin belongs to another subscribed repository.");
            foreach (var id in pluginIds)
            {
                var entry = index.Plugins.FirstOrDefault(item => item.Id == id)
                    ?? throw new ArgumentException($"Plugin '{id}' is not in this release.");
                await InstallOneAsync(owner, repo, tag, entry, token);
                state.Installations.RemoveAll(item => item.PluginId == id);
                state.Installations.Add(new PluginInstallation(id, owner, repo, tag, entry.Sha256, entry.Description,
                    entry.ContentSha256));
                await WriteStateAsync(state, token);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ForgetAsync(string pluginId, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var state = await ReadStateAsync(token);
            if (state.Installations.RemoveAll(item => item.PluginId == pluginId) > 0)
                await WriteStateAsync(state, token);
        }
        finally { _gate.Release(); }
    }

    private async Task InstallOneAsync(string owner, string repo, string tag, PluginReleaseEntry entry, CancellationToken token)
    {
        using var release = await GetReleaseAsync(owner, repo, tag, token);
        var url = AssetUrl(release, entry.Asset, owner, repo, tag);
        var work = Path.Combine(SubscriptionRoot, ".downloads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var archivePath = Path.Combine(work, entry.Asset);
            using var client = CreateClient();
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(archivePath);
                await CopyBoundedAsync(input, output, MaxArchiveBytes, token);
            }
            var bytes = new FileInfo(archivePath).Length;
            await using var archiveBytes = File.OpenRead(archivePath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(archiveBytes, token)).ToLowerInvariant();
            if (bytes != entry.SizeBytes || hash != entry.Sha256)
                throw new InvalidDataException("Downloaded plugin does not match the release index.");
            archiveBytes.Position = 0;
            using (var zip = new ZipArchive(archiveBytes, ZipArchiveMode.Read, leaveOpen: true))
                await ExtractAsync(zip, work, entry.Id, token);
            await catalog.InstallPackageAsync(entry, Path.Combine(work, entry.Id), token);
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
    }

    private static Task ExtractAsync(ZipArchive zip, string work, string id, CancellationToken token)
        => ExtractPackageAsync(zip, Path.Combine(work, id), id + "/", token);

    internal static async Task ExtractPackageAsync(ZipArchive zip, string root, string prefix, CancellationToken token)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (zip.Entries.Count is 0 or > 2000) throw new InvalidDataException("Plugin archive has an invalid entry count.");
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName;
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Contains('\\')
                || !SafeArchivePath(path)
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Plugin archive contains an unsafe path or link.");
            if (prefix.Length > 0 && path == prefix) continue;
            var relative = path[prefix.Length..];
            if (!paths.Add(relative.TrimEnd('/')))
                throw new InvalidDataException("Plugin archive contains duplicate paths.");
            var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Plugin archive path escapes its directory.");
            if (path.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            if (entry.Length > MaxExtractedBytes - total) throw new InvalidDataException("Plugin archive is too large after extraction.");
            total += entry.Length;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew);
            await CopyBoundedAsync(input, output, entry.Length, token);
            if (output.Length != entry.Length) throw new InvalidDataException("Plugin archive entry length is invalid.");
        }
        if (!File.Exists(Path.Combine(root, "plugin.json")) &&
            !Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly).Any())
            throw new InvalidDataException("Plugin archive has no manifest or assembly.");
    }

    private static bool SafeArchivePath(string path)
        => path.TrimEnd('/').Split('/').All(part =>
            part.Length > 0 && part is not ("." or "..")
            && !part.EndsWith('.') && !part.EndsWith(' ')
            && !part.Any(character => character < 32 || "<>:\"\\|?*".Contains(character))
            && !Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    private async Task<JsonDocument> GetReleaseAsync(string owner, string repo, string tag, CancellationToken token)
    {
        ValidateRepository(owner, repo);
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 100) throw new ArgumentException("Invalid release tag.");
        var release = await GetApiJsonAsync($"repos/{owner}/{repo}/releases/tags/{Uri.EscapeDataString(tag)}", token);
        if (release.RootElement.GetProperty("draft").GetBoolean() || release.RootElement.GetProperty("prerelease").GetBoolean()
            || release.RootElement.GetProperty("tag_name").GetString() != tag)
        { release.Dispose(); throw new InvalidDataException("Release is not a published stable version."); }
        return release;
    }

    private static Uri AssetUrl(JsonDocument release, string name, string owner, string repo, string tag)
    {
        foreach (var asset in release.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            var address = asset.GetProperty("browser_download_url").GetString();
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                && uri.Host == "github.com"
                && uri.AbsolutePath.StartsWith($"/{owner}/{repo}/releases/download/{Uri.EscapeDataString(tag)}/", StringComparison.OrdinalIgnoreCase))
                return uri;
        }
        throw new InvalidDataException($"Release asset '{name}' is missing or invalid.");
    }

    private async Task<JsonDocument> GetApiJsonAsync(string path, CancellationToken token)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync("https://api.github.com/" + path,
            HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(stream, buffer, 8 * 1024 * 1024, token);
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: token);
    }

    private HttpClient CreateClient()
    {
        var client = clients.CreateClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Router2API", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        long count = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, token);
            if (read == 0) break;
            count += read;
            if (count > limit) throw new InvalidDataException("Downloaded data exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private async Task<SubscriptionState> ReadStateAsync(CancellationToken token)
    {
        if (!File.Exists(StatePath)) return new();
        await using var stream = File.OpenRead(StatePath);
        return await JsonSerializer.DeserializeAsync<SubscriptionState>(stream, JsonOptions, token) ?? new();
    }

    private async Task WriteStateAsync(SubscriptionState state, CancellationToken token)
    {
        Directory.CreateDirectory(SubscriptionRoot);
        var temp = StatePath + "." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, token);
            File.Move(temp, StatePath, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static void ValidateRepository(string owner, string repo)
    {
        if (!Regex.IsMatch(owner ?? "", "^[A-Za-z0-9][A-Za-z0-9-]{0,38}$", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(repo ?? "", "^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant)
            || repo is "." or "..")
            throw new ArgumentException("Use a GitHub repository in owner/repo form.");
    }

    private static bool SafeId(string value)
        => !string.IsNullOrEmpty(value) && value.Length <= 64
            && Regex.IsMatch(value, "^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)
            && SafeArchivePath(value);
    private static bool SameRepository(string leftOwner, string leftRepo, string rightOwner, string rightRepo)
        => leftOwner.Equals(rightOwner, StringComparison.OrdinalIgnoreCase)
            && leftRepo.Equals(rightRepo, StringComparison.OrdinalIgnoreCase);

    private sealed class SubscriptionState
    {
        public List<PluginRepository> Repositories { get; init; } = [];
        public List<PluginInstallation> Installations { get; init; } = [];
    }

    public void Dispose() => _gate.Dispose();
}
