using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Router.Contracts.Domain;
using Router.Contracts.Plugins;

namespace Router.Host.Plugins.JavaScript;

internal sealed record JsPluginManifest
{
    public int SchemaVersion { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string Version { get; init; } = "";
    public string Runtime { get; init; } = "";
    public string HostApi { get; init; } = "";
    public string Entry { get; init; } = "";
    public string Format { get; init; } = "";
    public JsPlatformManifest Platform { get; init; } = new();
    public JsPlatformManifest[] Platforms { get; init; } = [];
    public JsHooksManifest Hooks { get; init; } = new();
    public JsPermissions Permissions { get; init; } = new();
    public JsPolicyManifest Policy { get; init; } = new();
    public JsEndpointManifest[] Endpoints { get; init; } = [];
    public JsTaskManifest[] Tasks { get; init; } = [];
    public JsJobManifest[] Jobs { get; init; } = [];
    public JsPageManifest? Page { get; init; }
    public Dictionary<string, JsStreamMapperManifest> StreamMappers { get; init; } = new(StringComparer.Ordinal);
    [JsonIgnore]
    public IReadOnlyList<string>? PackagePlatforms { get; init; }
    [JsonIgnore]
    public IReadOnlyDictionary<string, string[]>? PackageCredentialKinds { get; init; }
    [JsonIgnore]
    public string GenerationId { get; init; } = Guid.NewGuid().ToString("N");
    [JsonIgnore]
    public IReadOnlyList<JsPlatformManifest> DeclaredPlatforms => Platforms.Length > 0 ? Platforms : [Platform];

    [JsonIgnore]
    public IEnumerable<string> Handlers =>
        HookNames(Hooks).Concat(Platforms.SelectMany(platform => platform.Hooks is null ? [] : HookNames(platform.Hooks)))
            .Where(name => !string.IsNullOrEmpty(name)).Select(name => name!)
            .Concat(Endpoints.Select(endpoint => endpoint.Handler))
            .Concat(Tasks.Select(task => task.Handler))
            .Concat(Jobs.Select(job => job.Handler))
            .Concat(StreamMappers.Values.SelectMany(mapper => new[] { mapper.Event, mapper.End, mapper.Completion })
                .Where(name => !string.IsNullOrEmpty(name)).Select(name => name!))
            .Distinct(StringComparer.Ordinal);

    public void Validate(string directoryName)
    {
        if (SchemaVersion != 1 || Runtime != "jint" || HostApi is not ("1" or "1-preview") || Format != "esm-bundle")
            throw new InvalidOperationException("JS plugins require schemaVersion=1, runtime=jint, hostApi=1 or 1-preview, format=esm-bundle.");
        if (!Identifier(Id) || Id != directoryName)
            throw new InvalidOperationException("Plugin id must match its directory; plugin/platform names must be lowercase identifiers.");
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Version))
            throw new InvalidOperationException("Plugin name and version are required.");
        if (DeclaredPlatforms.Count is 0 or > 16 || DeclaredPlatforms.Select(platform => platform.Name).Distinct(StringComparer.Ordinal).Count() != DeclaredPlatforms.Count)
            throw new InvalidOperationException("Declare 1–16 unique platforms.");
        foreach (var platform in DeclaredPlatforms)
        {
            if (!Identifier(platform.Name) || platform.CredentialKinds.Length == 0
                || platform.CredentialKinds.Any(kind => !Enum.TryParse<CredentialKind>(kind, false, out var value) || !Enum.IsDefined(value))
                || platform.ModelCacheTtlSeconds is < 0 or > 86400 || string.IsNullOrWhiteSpace((platform.Hooks ?? Hooks).Invoke))
                throw new InvalidOperationException("Invalid platform identity, credential kinds, model cache or invoke hook.");
            (platform.Policy ?? Policy).Validate();
            if (platform.ProbeEndpoint is { } probe && (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
                throw new InvalidOperationException("Invalid platform probe endpoint.");
        }
        if (StreamMappers.Count > 32 || StreamMappers.Any(pair => !Identifier(pair.Key)
                || string.IsNullOrWhiteSpace(pair.Value.Event) || string.IsNullOrWhiteSpace(pair.Value.End)))
            throw new InvalidOperationException("Stream mappers need a name, event and end exports.");
        foreach (var handler in Handlers)
            if (!Regex.IsMatch(handler, @"^[A-Za-z_$][A-Za-z0-9_$]{0,127}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                throw new InvalidOperationException("Handlers must be named JS exports, not expressions.");
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in Endpoints)
        {
            if (!Enum.TryParse<PluginAuthPolicy>(endpoint.Auth, false, out var auth) || !Enum.IsDefined(auth)
                || !AllowedMethod(endpoint.Method) || endpoint.Method == "OPTIONS" || !SafeRoute(endpoint.Path)
                || endpoint.Platform is { } owner && !DeclaredPlatforms.Any(platform => platform.Name == owner)
                || !routes.Add($"{endpoint.Method}:{endpoint.Path.Trim('/')}"))
                throw new InvalidOperationException("Invalid or duplicate JS endpoint.");
        }
        if (Tasks.Select(task => $"{task.Platform ?? DeclaredPlatforms[0].Name}:{task.Name}").Distinct(StringComparer.OrdinalIgnoreCase).Count() != Tasks.Length)
            throw new InvalidOperationException("Duplicate JS task name.");
        foreach (var task in Tasks)
        {
            if (!Identifier(task.Name) || string.IsNullOrWhiteSpace(task.Cron) || task.TimeoutSeconds is < 1 or > 3600
                || task.Platform is { } owner && !DeclaredPlatforms.Any(platform => platform.Name == owner))
                throw new InvalidOperationException("JS tasks need a declared platform, name, Cron and a timeout of 1–3600 seconds.");
            _ = Router.Host.Services.CronSchedule.GetNext(task.Cron, DateTimeOffset.UtcNow);
        }
        if (Jobs.Any(job => !Identifier(job.Name) || job.TimeoutSeconds is < 1 or > 3600
                || job.Platform is { } owner && !DeclaredPlatforms.Any(platform => platform.Name == owner))
            || Jobs.Select(job => $"{job.Platform}:{job.Name}").Distinct(StringComparer.Ordinal).Count() != Jobs.Length)
            throw new InvalidOperationException("Invalid or duplicate background job.");
        if (Permissions.Http.Routes.Any(route => route is not ("pool" or "direct" or "attempt")))
            throw new InvalidOperationException("Unknown HTTP route.");
        foreach (var origin in Permissions.Http.Origins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http")
                || uri.Host.Contains('*') || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidOperationException("HTTP origins must be exact HTTP(S) origins without credentials, paths or wildcards.");
        }
        if (Permissions.State.Any(value => value is not ("read" or "write")))
            throw new InvalidOperationException("Unknown state permission.");
        if (Permissions.SharedState.Any(value => value is not ("read" or "write"))
            || Permissions.Accounts.Any(value => value is not ("read" or "readCredentials" or "write" or "refresh" or "setCooldown" or "disable"))
            || Permissions.Jobs.Any(value => value is not ("start" or "read" or "cancel"))
            || Permissions.Crypto.Any(value => value is not ("random" or "hash" or "hmac" or "encoding" or "decimal")))
            throw new InvalidOperationException("Unknown state/account/job/utility permission.");
        if (Permissions.Models.Any(value => value is not ("read" or "invalidate" or "refresh"))
            || Permissions.Tasks.Any(value => value is not ("run" or "writeLog")))
            throw new InvalidOperationException("Unknown models/tasks permission.");
    }

    public JsPluginManifest ForPlatform(JsPlatformManifest platform) => this with
    {
        Platform = platform, Platforms = [], PackagePlatforms = DeclaredPlatforms.Select(item => item.Name).ToArray(),
        PackageCredentialKinds = DeclaredPlatforms.ToDictionary(item => item.Name, item => item.CredentialKinds, StringComparer.Ordinal),
        Hooks = platform.Hooks ?? Hooks, Policy = platform.Policy ?? Policy,
        Endpoints = Endpoints.Where(item => (item.Platform ?? DeclaredPlatforms[0].Name) == platform.Name).ToArray(),
        Tasks = Tasks.Where(item => (item.Platform ?? DeclaredPlatforms[0].Name) == platform.Name).ToArray(),
        Jobs = Jobs.Where(item => (item.Platform ?? DeclaredPlatforms[0].Name) == platform.Name).ToArray()
    };

    private static IEnumerable<string?> HookNames(JsHooksManifest hooks)
        => [hooks.Invoke, hooks.GetModels, hooks.ValidateCredential, hooks.Start, hooks.Stop, hooks.SelectAccounts, hooks.RefreshCredential, hooks.GetMainPage];

    internal static bool Identifier(string value)
        => Regex.IsMatch(value, @"^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static bool AllowedMethod(string value)
        => value is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS";

    private static bool SafeRoute(string value)
        => !string.IsNullOrWhiteSpace(value.Trim('/')) && value.Length <= 200 && !value.Contains("..", StringComparison.Ordinal)
            && !value.Contains('\\') && !value.Contains('?') && !value.Contains('#')
            && !value.Contains("://", StringComparison.Ordinal) && !value.Trim('/').StartsWith("api/admin", StringComparison.OrdinalIgnoreCase);
}

internal sealed record JsPlatformManifest
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string[] CredentialKinds { get; init; } = ["ApiKey"];
    public int ModelCacheTtlSeconds { get; init; } = 300;
    public string? ProbeEndpoint { get; init; }
    public JsHooksManifest? Hooks { get; init; }
    public JsPolicyManifest? Policy { get; init; }
}
internal sealed record JsHooksManifest
{
    public string Invoke { get; init; } = "";
    public string? GetModels { get; init; }
    public string? ValidateCredential { get; init; }
    public string? Start { get; init; }
    public string? Stop { get; init; }
    public string? SelectAccounts { get; init; }
    public string? RefreshCredential { get; init; }
    public string? GetMainPage { get; init; }
}
internal sealed record JsPermissions
{
    public JsHttpPermissions Http { get; init; } = new();
    public string[] State { get; init; } = [];
    public string[] SharedState { get; init; } = [];
    public string[] Accounts { get; init; } = [];
    public string[] Jobs { get; init; } = [];
    public string[] Crypto { get; init; } = [];
    public string[] Models { get; init; } = [];
    public string[] Tasks { get; init; } = [];
    public bool CreateAnonymousAccount { get; init; }
    public bool ReadCurrentCredential { get; init; }
}
internal sealed record JsHttpPermissions
{
    public string[] Origins { get; init; } = [];
    public string[] Routes { get; init; } = [];
    public bool ManageOrigins { get; init; }
}
internal sealed record JsPolicyManifest
{
    public int MaxAttempts { get; init; } = 1;
    public int AttemptTimeoutSeconds { get; init; } = 60;
    public int TotalTimeoutSeconds { get; init; } = 180;
    public JsTransportFailurePolicy TransportFailure { get; init; } = new();
    public void Validate()
    {
        if (MaxAttempts is < 1 or > 35 || AttemptTimeoutSeconds is < 1 or > 60 || TotalTimeoutSeconds is < 1 or > 180)
            throw new InvalidOperationException("Invalid model attempt count or setup timeout.");
        TransportFailure.Decision().Validate();
    }
}
internal sealed record JsTransportFailurePolicy
{
    public bool Retry { get; init; } = true;
    public bool CooldownProxy { get; init; } = true;
    public int AccountCooldownSeconds { get; init; }
    public PluginAttemptDecision Decision()
    {
        if (AccountCooldownSeconds is < 0 or > 86400) throw new InvalidOperationException("Invalid transport account cooldown.");
        return new PluginAttemptDecision
        {
            FailureKind = PluginFailureKind.Transport, Retry = Retry ? PluginRetryAction.NextAttempt : PluginRetryAction.None,
            ProxyAction = CooldownProxy ? PluginProxyAction.Cooldown : PluginProxyAction.None,
            AccountAction = AccountCooldownSeconds > 0 ? PluginAccountAction.Cooldown : PluginAccountAction.None,
            AccountCooldownUntil = AccountCooldownSeconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(AccountCooldownSeconds) : null,
            ReasonCode = "transport-failure"
        };
    }
}
internal sealed record JsEndpointManifest
{
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "";
    public string Auth { get; init; } = "AdminSession";
    public string Handler { get; init; } = "";
    public string? Platform { get; init; }
}
internal sealed record JsTaskManifest
{
    public string Name { get; init; } = "";
    public string Handler { get; init; } = "";
    public string Cron { get; init; } = "";
    public string? Description { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public string? Platform { get; init; }
}
internal sealed record JsJobManifest
{
    public string Name { get; init; } = "";
    public string Handler { get; init; } = "";
    public string? Platform { get; init; }
    public int TimeoutSeconds { get; init; } = 1800;
    public string? Description { get; init; }
}
internal sealed record JsPageManifest
{
    public string Title { get; init; } = "";
    public string Entry { get; init; } = "";
}
internal sealed record JsStreamMapperManifest
{
    public string Event { get; init; } = "";
    public string End { get; init; } = "";
    public string? Completion { get; init; }
}

internal sealed record JsPluginPackage(JsPluginManifest Manifest, string Source, string? Page, string ManifestJson)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 48,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static bool IsJavaScriptDirectory(string root)
    {
        var path = Path.Combine(root, "plugin.json");
        if (!File.Exists(path)) return false;
        try
        {
            if (new FileInfo(path).Length > 64 * 1024 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return true;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return !document.RootElement.TryGetProperty("runtime", out var runtime) || runtime.GetString() != "dotnet";
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return true; // Let the normal loader report a failed manifest without aborting other plugins.
        }
    }

    public async Task WriteSnapshotAsync(string directory, CancellationToken cancellationToken)
    {
        foreach (var (relative, text) in new[]
        {
            ("plugin.json", ManifestJson),
            (Manifest.Entry, Source),
            (Manifest.Page?.Entry, Page)
        })
        {
            if (relative is null || text is null) continue;
            var path = Path.GetFullPath(Path.Combine(directory, relative));
            if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Plugin snapshot path escapes its directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text, cancellationToken);
        }
    }

    public static async Task<JsPluginPackage> ReadAsync(string root, string directoryName, CancellationToken cancellationToken)
    {
        var manifestJson = await ReadFileAsync(root, "plugin.json", 64 * 1024, cancellationToken);
        var manifest = JsonSerializer.Deserialize<JsPluginManifest>(manifestJson, JsonOptions)
            ?? throw new InvalidOperationException("Empty JS manifest.");
        manifest.Validate(directoryName);
        var source = await ReadFileAsync(root, manifest.Entry, 2 * 1024 * 1024, cancellationToken);
        var page = manifest.Page is { } pageManifest
            ? await ReadFileAsync(root, pageManifest.Entry, 1024 * 1024, cancellationToken)
            : null;
        return new JsPluginPackage(manifest, source, page, manifestJson);
    }

    private static async Task<string> ReadFileAsync(string root, string relative, int maximumBytes, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Plugin asset paths must be relative.");
        root = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Plugin asset path escapes its package.");
        for (var current = new FileInfo(path) as FileSystemInfo; current is not null; current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Plugin asset symlinks/reparse points are not allowed.");
            if (current.FullName.Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }
        if (new FileInfo(path).Length > maximumBytes) throw new InvalidOperationException("Plugin asset exceeds its size limit.");
        var text = await File.ReadAllTextAsync(path, token);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > maximumBytes)
            throw new InvalidOperationException("Plugin asset exceeds its size limit.");
        return text;
    }
}
