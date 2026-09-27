using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;
using Router.Host.Security;
using Router.Infrastructure.Services;

namespace Router.Host.Plugins;

/// <summary>发现、激活、排空并卸载可回收插件。</summary>
public sealed class PluginCatalog(
    IPlatformRegistry platforms,
    IModelCatalog modelCatalog,
    IPluginHostFactory hostFactory,
    IPluginPolicyRegistry policyRegistry,
    AdminAuthService adminAuth,
    ApiKeyService apiKeys,
    IPluginLogSink pluginLogs,
    ILogger<PluginCatalog> logger,
    IOptions<PluginExecutionOptions> executionOptions) : IPluginCatalog, IAsyncDisposable
{
    private const int MaxPluginRequestBodyBytes = 1_048_576;
    private readonly PluginExecutionOptions _execution = executionOptions.Value;
    private readonly IPluginPackageLoader[] _loaders = [new JintPackageLoader(hostFactory), new DotNetPackageLoader(hostFactory)];
    private readonly ConcurrentDictionary<string, RuntimePlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly string _pluginRoot = Path.Combine(AppContext.BaseDirectory, "plugins");
    private string DisabledPluginRoot => Path.Combine(_pluginRoot, ".disabled");
    private string SubscriptionRoot => Path.Combine(_pluginRoot, ".subscription");
    private int _endpointsMapped;
    private int _stagingInitialized;

    public IReadOnlyList<PluginDescriptor> All
        => _plugins.Values
            .Select(plugin => plugin.ToDescriptor())
            .OrderBy(plugin => plugin.Name)
            .ToArray();

    public PluginDescriptor? Get(string pluginKey)
        => _plugins.TryGetValue(pluginKey, out var plugin) ? plugin.ToDescriptor() : null;

    public PluginMainPage? GetMainPage(string pluginKey)
        => _plugins.TryGetValue(pluginKey, out var plugin) ? plugin.MainPage : null;

    public async Task InstallPackageAsync(PluginReleaseEntry entry, string packageDirectory, CancellationToken cancellationToken)
    {
        var pluginKey = entry.Id;
        if (!SafePluginKey(pluginKey)) throw new ArgumentException("Invalid plugin key.", nameof(entry));
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_pluginRoot);
            var target = Path.Combine(_pluginRoot, pluginKey);
            var backup = Path.Combine(_pluginRoot, ".backup", pluginKey + "-" + Guid.NewGuid().ToString("N"));
            var previous = _plugins.TryGetValue(pluginKey, out var old) ? old : null;
            var previousDescriptor = previous?.Descriptor;
            var wasDisabled = File.Exists(Path.Combine(DisabledPluginRoot, pluginKey + ".disabled"));
            if (Directory.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                Directory.Move(target, backup);
            }
            try
            {
                Directory.Move(packageDirectory, target);
                if (wasDisabled)
                {
                    await KeepDisabledAsync(pluginKey, target, cancellationToken);
                    var disabled = _plugins[pluginKey];
                    if (disabled.Descriptor.State != "Disabled")
                        throw new InvalidOperationException("Plugin is busy; previous package restored.");
                    disabled.Descriptor = disabled.Descriptor with
                    {
                        Name = entry.Name, Version = entry.Version, Description = entry.Description,
                        Runtime = entry.Runtime, State = "Disabled", DirectoryPath = target
                    };
                    await File.WriteAllTextAsync(Path.Combine(DisabledPluginRoot, pluginKey + ".disabled"),
                        JsonSerializer.Serialize(disabled.Descriptor), cancellationToken);
                }
                else await LoadDirectoryAsync(pluginKey, target, cancellationToken);
                if (!wasDisabled && (!_plugins.TryGetValue(pluginKey, out var current)
                    || current.Descriptor.State != "Active" || ReferenceEquals(current, previous)))
                    throw new InvalidOperationException("Plugin activation failed; previous package restored.");
            }
            catch
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                if (Directory.Exists(backup)) Directory.Move(backup, target);
                if (previous is null) _plugins.TryRemove(pluginKey, out _);
                else
                {
                    var previousWasUnloaded = previous.Descriptor.State == "Unloaded";
                    previous.Descriptor = previousDescriptor!;
                    _plugins[pluginKey] = previous;
                    if (previousDescriptor!.State == "Active" && previousWasUnloaded)
                    {
                        _plugins.TryRemove(pluginKey, out _);
                        await LoadDirectoryAsync(pluginKey, target, CancellationToken.None);
                    }
                }
                throw;
            }
            try { if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { logger.LogWarning(exception, "failed to delete plugin backup path={Path}", backup); }
        }
        finally { _reloadLock.Release(); }
    }

    public async Task<bool> RemovePackageAsync(string pluginKey, CancellationToken cancellationToken)
    {
        if (!SafePluginKey(pluginKey)) return false;
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(_pluginRoot, pluginKey);
            if (!Directory.Exists(directory)) return false;
            var removed = Path.Combine(_pluginRoot, ".removed", pluginKey + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(removed)!);
            Directory.Move(directory, removed);
            try
            {
                if (_plugins.TryGetValue(pluginKey, out var plugin)
                    && !await DrainAndUnloadAsync(plugin, cancellationToken))
                    throw new InvalidOperationException("Plugin is busy; removal failed.");
                File.Delete(Path.Combine(DisabledPluginRoot, pluginKey + ".disabled"));
                _plugins.TryRemove(pluginKey, out _);
            }
            catch
            {
                Directory.Move(removed, directory);
                throw;
            }
            try { Directory.Delete(removed, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { logger.LogWarning(exception, "failed to delete removed plugin path={Path}", removed); }
            return true;
        }
        finally { _reloadLock.Release(); }
    }

    private static bool SafePluginKey(string value)
        => !string.IsNullOrEmpty(value) && value.Length <= 64
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');

    public async Task<PluginDescriptor?> SetEnabledAsync(
        string pluginKey,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            if (!_plugins.TryGetValue(pluginKey, out var plugin)) return null;

            var pluginName = plugin.Descriptor.Name;
            var sourceDirectory = Path.Combine(_pluginRoot, pluginName);
            if (!Directory.Exists(sourceDirectory)) return null;

            var disabledMarker = Path.Combine(DisabledPluginRoot, pluginName + ".disabled");
            if (enabled)
            {
                if (plugin.Descriptor.State == "Active")
                {
                    File.Delete(disabledMarker);
                    return plugin.ToDescriptor();
                }

                await LoadDirectoryAsync(pluginName, sourceDirectory, cancellationToken);
                if (_plugins.TryGetValue(pluginName, out var loaded)
                    && loaded.Descriptor.State == "Active")
                    File.Delete(disabledMarker);
                return _plugins.TryGetValue(pluginName, out loaded) ? loaded.ToDescriptor() : null;
            }

            Directory.CreateDirectory(DisabledPluginRoot);
            await File.WriteAllTextAsync(disabledMarker, pluginName, cancellationToken);
            if (plugin.Descriptor.State == "Active"
                && !await DrainAndUnloadAsync(plugin, cancellationToken))
            {
                File.Delete(disabledMarker);
                return plugin.ToDescriptor();
            }

            var descriptor = plugin.Descriptor with { State = "Disabled", DirectoryPath = sourceDirectory };
            var disabledPlugin = CreateDisabledRuntime(descriptor);
            _plugins[pluginName] = disabledPlugin;
            await File.WriteAllTextAsync(disabledMarker, JsonSerializer.Serialize(descriptor), cancellationToken);
            return disabledPlugin.ToDescriptor();
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    public async Task ReloadAsync(string? name, CancellationToken cancellationToken = default)
    {
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_pluginRoot);
            EnsureSubscriptionDirectory();
            CleanStaleStagingOnStartup();

            var sourceDirectories = Directory.EnumerateDirectories(_pluginRoot)
                .Where(path => !Path.GetFileName(path).StartsWith('.'))
                .ToArray();
            var installedNames = sourceDirectories
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var sourceDirectory in sourceDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pluginName = Path.GetFileName(sourceDirectory);
                if (name is not null && !pluginName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (File.Exists(Path.Combine(DisabledPluginRoot, pluginName + ".disabled")))
                {
                    await KeepDisabledAsync(pluginName, sourceDirectory, cancellationToken);
                    continue;
                }

                await LoadDirectoryAsync(pluginName, sourceDirectory, cancellationToken);
            }

            foreach (var (pluginName, plugin) in _plugins.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (name is not null && !pluginName.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || installedNames.Contains(pluginName))
                    continue;

                if (!await DrainAndUnloadAsync(plugin, cancellationToken)) continue;
                _plugins.TryRemove(pluginName, out _);
                logger.LogInformation("plugin removed name={PluginName}", pluginName);
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private async Task KeepDisabledAsync(
        string pluginName,
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        if (!_plugins.TryGetValue(pluginName, out var plugin))
        {
            var markerPath = Path.Combine(DisabledPluginRoot, pluginName + ".disabled");
            var descriptor = new PluginDescriptor(
                PluginKey: pluginName,
                Name: pluginName,
                Version: "unknown",
                State: "Disabled",
                DirectoryPath: sourceDirectory,
                LoadedAt: DateTimeOffset.UtcNow,
                InFlight: 0);
            try
            {
                if (JsonSerializer.Deserialize<PluginDescriptor>(await File.ReadAllTextAsync(markerPath, cancellationToken)) is { } saved)
                    descriptor = saved with { State = "Disabled", DirectoryPath = sourceDirectory, InFlight = 0 };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                logger.LogWarning(exception, "failed to read disabled plugin metadata name={PluginName}", pluginName);
            }

            _plugins[pluginName] = CreateDisabledRuntime(descriptor);
            return;
        }

        if (plugin.Descriptor.State == "Active"
            && !await DrainAndUnloadAsync(plugin, cancellationToken))
            return;

        var disabledDescriptor = plugin.Descriptor with { State = "Disabled", DirectoryPath = sourceDirectory };
        _plugins[pluginName] = CreateDisabledRuntime(disabledDescriptor);
        var disabledMarker = Path.Combine(DisabledPluginRoot, pluginName + ".disabled");
        await File.WriteAllTextAsync(disabledMarker, JsonSerializer.Serialize(disabledDescriptor), cancellationToken);
    }

    private static RuntimePlugin CreateDisabledRuntime(PluginDescriptor descriptor)
        => new(descriptor, [], [], [], null);

    /// <summary>映射启动时发现的插件端点特性。</summary>
    public void MapEndpoints(WebApplication app)
    {
        if (Interlocked.Exchange(ref _endpointsMapped, 1) != 0) return;
        app.MapMethods(
            "/api/plugins/{pluginKey}/{**route}",
            ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"],
            (Delegate)InvokeEndpointAsync);
    }

    private async Task<IResult> InvokeEndpointAsync(HttpContext context)
    {
        var pluginKey = context.Request.RouteValues["pluginKey"]?.ToString();
        var requestedRoute = context.Request.RouteValues["route"]?.ToString();
        if (string.IsNullOrWhiteSpace(pluginKey)
            || !TryNormalizeRoute(requestedRoute, out var route)
            || !_plugins.TryGetValue(pluginKey, out var plugin))
            return Results.NotFound();

        var endpoint = plugin.Endpoints.FirstOrDefault(item =>
            item.Auth != PluginAuthPolicy.Internal
            && item.Route.Equals(route, StringComparison.OrdinalIgnoreCase)
            && item.Method.Equals(context.Request.Method, StringComparison.OrdinalIgnoreCase));
        if (endpoint is null) return Results.NotFound();

        if (!Authorize(PluginAuthPolicy.AdminSession, context))
            return Results.Json(new { error = "admin authentication required" }, statusCode: 401);
        if (IsUnsafeMethod(context.Request.Method) && !HasValidCsrf(context))
            return Results.Json(new { error = "csrf validation failed" }, statusCode: 403);
        if (context.Request.ContentLength > MaxPluginRequestBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        if (!plugin.TryEnterEndpoint()) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await TryWritePluginLogAsync(new PluginLog
            {
                PluginKey = pluginKey,
                EventType = "endpoint.started",
                Message = "插件管理端点开始执行",
                Level = "Debug",
                DetailsJson = JsonSerializer.Serialize(new { method = context.Request.Method, route })
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(_execution.EndpointTimeout);
            try
            {
                var result = await endpoint.InvokeAsync(new PluginHttpContext
                {
                    PluginKey = endpoint.PluginKey, Platform = endpoint.Platform, CancellationToken = timeout.Token,
                    Query = context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                    Body = await ReadBodyAsync(context, timeout.Token)
                });
                await TryWritePluginLogAsync(new PluginLog
                {
                    PluginKey = pluginKey,
                    EventType = "endpoint.completed",
                    Message = "插件管理端点执行完成",
                    Level = "Debug",
                    StatusCode = result.StatusCode,
                    DurationMs = (int)stopwatch.ElapsedMilliseconds,
                    DetailsJson = JsonSerializer.Serialize(new { method = context.Request.Method, route })
                });
                return result.StatusCode == 204
                    ? Results.NoContent()
                    : Results.Json(result.Body, statusCode: result.StatusCode, contentType: result.ContentType);
            }
            catch (PluginRequestBodyTooLargeException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                await TryWritePluginLogAsync(new PluginLog
                {
                    PluginKey = pluginKey,
                    EventType = "endpoint.timeout",
                    Message = "插件管理端点超时",
                    Level = "Error",
                    StatusCode = StatusCodes.Status504GatewayTimeout,
                    DurationMs = (int)stopwatch.ElapsedMilliseconds,
                    DetailsJson = JsonSerializer.Serialize(new { method = context.Request.Method, route })
                });
                return Results.Json(new { error = "plugin endpoint timed out" }, statusCode: 504);
            }
        }
        finally
        {
            plugin.ExitEndpoint();
        }
    }

    private void CleanStaleStagingOnStartup()
    {
        if (Interlocked.Exchange(ref _stagingInitialized, 1) != 0) return;

        var stagingRoot = Path.Combine(_pluginRoot, ".staging");
        try
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "failed to clean stale plugin staging path={Path}", stagingRoot);
        }
    }

    private bool Authorize(PluginAuthPolicy policy, HttpContext context)
    {
        if (policy == PluginAuthPolicy.Anonymous) return true;

        var principal = context.Request.Cookies["router_admin_session"] is { } session
            ? adminAuth.Validate(session)
            : null;
        var isAdmin = principal?.Roles.Contains("admin", StringComparer.OrdinalIgnoreCase) == true;
        if (isAdmin) context.Items[typeof(AdminPrincipal)] = principal!;

        var supplied = context.Request.Headers.Authorization.ToString();
        var bearer = supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? supplied["Bearer ".Length..].Trim()
            : string.Empty;
        var hasApiKey = !string.IsNullOrWhiteSpace(apiKeys.Current)
            && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(apiKeys.Current),
                System.Text.Encoding.UTF8.GetBytes(bearer));

        return policy switch
        {
            PluginAuthPolicy.AdminSession => isAdmin,
            PluginAuthPolicy.ApiKey => hasApiKey,
            PluginAuthPolicy.AdminSessionOrApiKey => isAdmin || hasApiKey,
            _ => false
        };
    }

    private async Task TryWritePluginLogAsync(PluginLog log)
    {
        try
        {
            await pluginLogs.WriteAsync(log, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "plugin endpoint detail log persistence failed pluginKey={PluginKey} event={EventType}",
                log.PluginKey,
                log.EventType);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var plugin in _plugins.Values)
            await DrainAndUnloadAsync(plugin, CancellationToken.None);
        _reloadLock.Dispose();
    }

    private async Task LoadDirectoryAsync(string pluginName, string sourceDirectory, CancellationToken cancellationToken)
    {
        var loader = _loaders.FirstOrDefault(item => item.CanLoad(sourceDirectory, pluginName));
        if (loader is null)
        {
            logger.LogWarning("plugin directory has no supported package name={PluginName} path={Path}", pluginName, sourceDirectory);
            return;
        }
        var stagingDirectory = Path.Combine(_pluginRoot, ".staging", pluginName,
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        LoadedPlugin? candidate = null;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            candidate = await loader.LoadAsync(pluginName, sourceDirectory, stagingDirectory, cancellationToken);
            foreach (var platform in candidate.Platforms)
                if (platforms.Get(platform.Name) is { } owner
                    && !owner.PluginKey.Equals(candidate.PluginKey, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The requested platform belongs to another plugin.");

            var tracked = candidate.Platforms.Select(item => new TrackedTerminal(item.Terminal, item.Tasks)).ToArray();
            var registrations = candidate.Platforms.Select((item, index) => new PlatformRegistration(
                item.Name, item.PluginKey, item.DisplayName, tracked[index], item.ProbeEndpoint)
                { ModelCacheTtl = item.ModelCacheTtl }).ToArray();
            var runtime = new RuntimePlugin(candidate.Describe(pluginName, stagingDirectory), registrations, tracked,
                candidate.Endpoints, candidate.MainPage, candidate);
            await candidate.StartAsync(cancellationToken);

            if (_plugins.TryGetValue(pluginName, out var previous)
                && !await DrainAndUnloadAsync(previous, cancellationToken)) return;
            foreach (var item in candidate.Platforms)
                policyRegistry.Set(item.PluginKey, item.Name, item.Configuration.BuiltProxyPolicy, item.Configuration.BuiltAccountPolicy);
            foreach (var registration in registrations)
            {
                platforms.Register(registration);
                modelCatalog.Invalidate(registration.Name);
            }
            _plugins[pluginName] = runtime;
            candidate = null; // Ownership moves only after validation, start and old-generation drain succeeded.
            logger.LogInformation("plugin activated name={PluginName} version={Version} path={Path}", pluginName, runtime.Descriptor.Version, stagingDirectory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "plugin activation failed name={PluginName}", pluginName);
            if (!_plugins.ContainsKey(pluginName))
                _plugins[pluginName] = CreateDisabledRuntime(new PluginDescriptor(pluginName, pluginName, "unknown", "Failed",
                    sourceDirectory, DateTimeOffset.UtcNow, 0) { Runtime = loader.Runtime });
        }
        finally
        {
            if (candidate is not null) await DisposePackageAsync(candidate);
            if (!_plugins.TryGetValue(pluginName, out var active) || active.Descriptor.DirectoryPath != stagingDirectory)
                TryDeleteSnapshot(stagingDirectory);
        }
    }

    private void EnsureSubscriptionDirectory()
    {
        var legacy = Path.Combine(_pluginRoot, "subscription");
        var legacyStorage = Directory.Exists(legacy)
            && !File.Exists(Path.Combine(legacy, "plugin.json"))
            && !Directory.EnumerateFiles(legacy, "*.dll", SearchOption.TopDirectoryOnly).Any();
        if (legacyStorage && !Directory.Exists(SubscriptionRoot))
            Directory.Move(legacy, SubscriptionRoot);
        else Directory.CreateDirectory(SubscriptionRoot);

        if (!legacyStorage || !Directory.Exists(legacy)) return;
        var legacyState = Path.Combine(legacy, "subscriptions.json");
        if (File.Exists(legacyState))
        {
            var state = Path.Combine(SubscriptionRoot, "subscriptions.json");
            if (File.Exists(state))
                throw new InvalidOperationException("Both legacy and current plugin subscription files exist; keep both and resolve the conflict before starting the host.");
            File.Move(legacyState, state);
        }
        var legacyDownloads = Path.Combine(legacy, ".downloads");
        var downloads = Path.Combine(SubscriptionRoot, ".downloads");
        if (Directory.Exists(legacyDownloads) && !Directory.Exists(downloads))
            Directory.Move(legacyDownloads, downloads);
        if (!Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy);
    }

    private async ValueTask DisposePackageAsync(LoadedPlugin package)
    {
        try { await package.DisposeAsync(); }
        catch (Exception exception) { logger.LogWarning(exception, "plugin cleanup failed name={PluginName}", package.PluginKey); }
    }

    private void TryDeleteSnapshot(string path)
    {
        var boundary = Path.GetFullPath(Path.Combine(_pluginRoot, ".staging")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path);
        if (!target.StartsWith(boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return;
        try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A collectible CLR assembly may remain mapped until GC; startup sweeps these snapshots.
            logger.LogDebug(exception, "plugin snapshot is still in use path={Path}", target);
        }
    }
    private static async Task<object?> ReadBodyAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is 0)
            return null;
        if (context.Request.ContentLength > MaxPluginRequestBodyBytes)
            throw new PluginRequestBodyTooLargeException();

        await using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(chunk, cancellationToken);
            if (count == 0) break;
            if (buffer.Length + count > MaxPluginRequestBodyBytes)
                throw new PluginRequestBodyTooLargeException();
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryNormalizeRoute(string? value, out string route)
    {
        try
        {
            route = PluginEndpointDefinition.NormalizeRoute(value ?? string.Empty);
            return true;
        }
        catch (InvalidOperationException)
        {
            route = string.Empty;
            return false;
        }
    }

    private static bool IsUnsafeMethod(string method)
        => HttpMethods.IsPost(method)
            || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method)
            || HttpMethods.IsDelete(method);

    private static bool HasValidCsrf(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        var expected = $"{context.Request.Scheme}://{context.Request.Host}";
        if (!string.IsNullOrWhiteSpace(origin)
            && !string.Equals(origin.TrimEnd('/'), expected.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return false;

        var cookie = context.Request.Cookies["router_admin_csrf"];
        var header = context.Request.Headers["X-CSRF-Token"].ToString();
        return !string.IsNullOrWhiteSpace(cookie)
            && !string.IsNullOrWhiteSpace(header)
            && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(cookie),
                System.Text.Encoding.UTF8.GetBytes(header));
    }

    private async Task<bool> DrainAndUnloadAsync(RuntimePlugin plugin, CancellationToken cancellationToken)
    {
        plugin.Descriptor = plugin.Descriptor with { State = "Draining" };
        foreach (var terminal in plugin.Terminals)
            terminal.BeginDrain();

        try
        {
            if (plugin.Package is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_execution.DrainTimeout);
                await plugin.Package.DrainJobsAsync(timeout.Token);
            }
            await plugin.WaitForEndpointsAsync(_execution.DrainTimeout, cancellationToken);
            foreach (var terminal in plugin.Terminals)
                await terminal.WaitForDrainAsync(_execution.DrainTimeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            plugin.ResumeEndpoints();
            plugin.Package?.ResumeJobs();
            foreach (var terminal in plugin.Terminals)
                terminal.ResumeAfterDrainFailure();
            logger.LogWarning("plugin drain timed out name={PluginName}", plugin.Descriptor.Name);
            plugin.Descriptor = plugin.Descriptor with { State = "Active" };
            return false;
        }

        if (plugin.Package is not null) await DisposePackageAsync(plugin.Package);
        foreach (var registration in plugin.Platforms)
        {
            platforms.Remove(registration.Name);
            modelCatalog.Invalidate(registration.Name);
        }
        policyRegistry.Remove(plugin.Descriptor.PluginKey);
        TryDeleteSnapshot(plugin.Descriptor.DirectoryPath);
        plugin.Descriptor = plugin.Descriptor with { State = "Unloaded" };
        logger.LogInformation("plugin unloaded name={PluginName}", plugin.Descriptor.Name);
        return true;
    }

    private sealed class RuntimePlugin(
        PluginDescriptor descriptor,
        IReadOnlyList<PlatformRegistration> platforms,
        IReadOnlyList<TrackedTerminal> terminals,
        IReadOnlyList<PluginEndpointDefinition> endpoints,
        PluginMainPage? mainPage,
        LoadedPlugin? package = null)
    {
        private readonly object _endpointGate = new();
        private int _endpointInFlight;
        private bool _endpointsDraining;

        public PluginDescriptor Descriptor { get; set; } = descriptor;
        public IReadOnlyList<PlatformRegistration> Platforms { get; } = platforms;
        public IReadOnlyList<TrackedTerminal> Terminals { get; } = terminals;
        public IReadOnlyList<PluginEndpointDefinition> Endpoints { get; } = endpoints;
        public PluginMainPage? MainPage { get; } = mainPage;
        public LoadedPlugin? Package { get; } = package;

        public bool TryEnterEndpoint()
        {
            lock (_endpointGate)
            {
                if (_endpointsDraining) return false;
                _endpointInFlight++;
                return true;
            }
        }

        public void ExitEndpoint()
        {
            lock (_endpointGate) _endpointInFlight--;
        }

        public async Task WaitForEndpointsAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (_endpointGate) _endpointsDraining = true;
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                while (true)
                {
                    lock (_endpointGate)
                    {
                        if (_endpointInFlight == 0) return;
                    }
                    await Task.Delay(25, timeoutSource.Token);
                }
            }
            catch
            {
                lock (_endpointGate) _endpointsDraining = false;
                throw;
            }
        }

        public void ResumeEndpoints()
        {
            lock (_endpointGate) _endpointsDraining = false;
        }

        public PluginDescriptor ToDescriptor()
        {
            lock (_endpointGate)
                return Descriptor with { InFlight = Terminals.Sum(terminal => terminal.InFlight) + _endpointInFlight + (Package?.JobInFlight ?? 0) };
        }
    }

    private sealed class TrackedTerminal(
        IPlatformTerminal inner,
        IReadOnlyList<ScheduledTaskRegistration> scheduledTasks)
        : IPlatformTerminal, IPluginScheduledTaskProvider
    {
        private int _inFlight;
        private int _draining;
        private readonly object _gate = new();
        private readonly IPlatformTerminal _inner = inner;
        private readonly IReadOnlyList<ScheduledTaskRegistration> _scheduledTasks = scheduledTasks;

        public int InFlight => Volatile.Read(ref _inFlight);

        public async Task<PluginInvocationResult> InvokeAsync(PluginAttemptContext context)
        {
            if (!TryEnterInvocation())
                throw new InvalidOperationException("plugin terminal is draining");

            var transferred = false;
            AdapterResponse? pending = null;
            try
            {
                var result = await _inner.InvokeAsync(context);
                pending = result.Response;
                if (!PluginResponseLifetime.HasStream(result.Response)) return result;
                var response = PluginResponseLifetime.Ensure(result.Response);
                ((PluginResponseLifetime)response.Lifetime!).OnCompleted(_ =>
                {
                    Interlocked.Decrement(ref _inFlight);
                    return ValueTask.CompletedTask;
                });
                transferred = true;
                return result with { Response = response };
            }
            catch
            {
                if (pending?.Lifetime is { } lifetime) await lifetime.DisposeAsync();
                throw;
            }
            finally { if (!transferred) Interlocked.Decrement(ref _inFlight); }
        }

        public async Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(ModelQueryContext context, CancellationToken cancellationToken)
        {
            if (!TryEnterInvocation()) return [];
            try { return await _inner.GetModelsAsync(context, cancellationToken); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        public async Task<CredentialValidationResult> ValidateCredentialAsync(Credential credential, CancellationToken cancellationToken)
        {
            if (!TryEnterInvocation()) return new CredentialValidationResult(false, "plugin is draining");
            try { return await _inner.ValidateCredentialAsync(credential, cancellationToken); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        public IReadOnlyList<ScheduledTaskRegistration> ScheduledTasks
        {
            get
            {
                if (Volatile.Read(ref _draining) != 0) return [];

                return _scheduledTasks
                    .Select(task => task with
                    {
                        ExecuteAsync = context => InvokeScheduledTaskAsync(task, context)
                    })
                    .ToArray();
            }
        }

        public void BeginDrain()
        {
            lock (_gate) Volatile.Write(ref _draining, 1);
        }

        private async Task InvokeScheduledTaskAsync(
            ScheduledTaskRegistration task,
            PluginScheduledTaskContext context)
        {
            if (!TryEnterInvocation()) return;

            try { await task.ExecuteAsync(context); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        private bool TryEnterInvocation()
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _draining) != 0) return false;
                Interlocked.Increment(ref _inFlight);
                return true;
            }
        }

        public async Task WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            BeginDrain();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                while (Volatile.Read(ref _inFlight) > 0)
                    await Task.Delay(25, timeoutSource.Token);
            }
            catch
            {
                lock (_gate) Volatile.Write(ref _draining, 0);
                throw;
            }
        }

        public void ResumeAfterDrainFailure()
        {
            lock (_gate) Volatile.Write(ref _draining, 0);
        }
    }

    private sealed class PluginRequestBodyTooLargeException : Exception { }
}
