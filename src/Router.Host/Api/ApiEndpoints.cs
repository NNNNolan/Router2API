using System.Text.Json;
using System.Security.Cryptography;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Host.Configuration;
using Router.Host.Pipeline;
using Router.Host.Plugins;
using Router.Host.Security;
using Router.Host.Tracing;

namespace Router.Host.Api;

/// <summary>映射文档定义的 HTTP API。</summary>
public static class ApiEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/admin/login", LoginAsync);
        app.MapPost("/api/admin/logout", LogoutAsync);
        app.MapGet("/api/admin/me", Me);
        app.MapPost("/api/admin/password", ChangePasswordAsync);
        app.MapGet("/api/admin/health", () => Results.Ok(new { ok = true, utc = DateTimeOffset.UtcNow }));
        app.MapGet("/api/admin/api-key", (ApiKeyService keys) => Results.Ok(new { key = keys.Masked() }));
        app.MapGet("/api/admin/api-key/raw", (ApiKeyService keys) => Results.Ok(new { key = keys.Current }));
        app.MapPost("/api/admin/api-key/rotate", (ApiKeyService keys) => Results.Ok(new { key = keys.Rotate() }));
        app.MapGet("/api/admin/config", (IConfigFileService config) => Results.Ok(config.Get()));
        app.MapPut("/api/admin/config", SaveConfigAsync);
        app.MapGet("/api/admin/metrics", (IRealtimeMetrics metrics) => Results.Ok(metrics.Snapshot()));
        app.MapGet("/api/admin/analytics/overview", GetAnalyticsOverviewAsync);
        app.MapGet("/api/admin/analytics", GetAnalyticsAsync);
        app.MapGet("/api/admin/metrics/stream", StreamMetricsAsync);
        app.MapGet("/api/admin/logs", GetLogsAsync);
        app.MapGet("/api/admin/model-plaza", GetModelPlazaAsync);
        app.MapPost("/api/admin/model-plaza/refresh", RefreshModelPlazaAsync);
        app.MapPost("/api/admin/model-metadata/refresh", RefreshModelMetadataAsync);

        app.MapGet("/api/admin/platforms", (IPlatformRegistry registry) => Results.Ok(registry.All.Select(ToPlatformDto)));
        app.MapGet("/api/admin/platforms/{platform}/models", GetPlatformModelsAsync);
        app.MapGet("/api/admin/platforms/{platform}/state", (string platform, IPlatformRegistry registry)
            => registry.Get(platform) is { } value
                ? Results.Ok(new { platform = value.Name, enabled = value.Enabled, pluginKey = value.PluginKey })
                : Results.NotFound());
        app.MapPost("/api/admin/platforms/{platform}/state", SetPlatformStateAsync);

        app.MapGet("/api/admin/accounts", GetAccountsAsync);
        app.MapGet("/api/admin/platforms/{platform}/accounts", GetPlatformAccountsAsync);
        app.MapPost("/api/admin/platforms/{platform}/accounts", SaveAccountAsync);
        app.MapDelete("/api/admin/accounts/{id}", DeleteAccountAsync);

        app.MapGet("/api/admin/proxies", GetProxiesAsync);
        app.MapPost("/api/admin/proxies", SaveProxyAsync);
        app.MapDelete("/api/admin/proxies/{id}", DeleteProxyAsync);
        app.MapPost("/api/admin/proxies/{id}/probe", ProbeProxyAsync);
        app.MapGet("/api/admin/proxy-subscriptions", ListSubscriptionsAsync);
        app.MapPost("/api/admin/proxy-subscriptions", SaveSubscriptionAsync);
        app.MapDelete("/api/admin/proxy-subscriptions/{id}", DeleteSubscriptionAsync);
        app.MapPost("/api/admin/proxy-subscriptions/{id}/refresh", RefreshSubscriptionAsync);

        app.MapGet("/api/admin/plugins", (IPluginCatalog catalog) => Results.Ok(catalog.All));
        app.MapPost("/api/admin/plugins/reload", ReloadPluginsAsync);
        app.MapPost("/api/admin/plugins/{name}/reload", ReloadPluginAsync);
        app.MapPost("/api/admin/plugins/{pluginKey}/state", SetPluginStateAsync);
        app.MapGet("/api/admin/plugins/{pluginKey}/manifest", GetPluginManifest);
        app.MapGet("/api/admin/plugins/{pluginKey}/page", GetPluginPage);
        app.MapPost("/api/admin/plugins/{pluginKey}/tasks/{taskName}/run", RunPluginTaskAsync);
        app.MapGet("/api/admin/task-logs", GetTaskLogsAsync);
        app.MapGet("/api/admin/plugin-logs", GetPluginLogsAsync);

        app.MapPost("/v1/chat/completions", ChatAsync).RequireCors("v1");
        app.MapPost("/v1/completions", CompletionsAsync).RequireCors("v1");
        app.MapPost("/v1/responses", ResponsesAsync).RequireCors("v1");
        app.MapPost("/v1/messages", AnthropicMessagesAsync).RequireCors("v1");
        app.MapPost("/v1/embeddings", EmbeddingsAsync).RequireCors("v1");
        app.MapGet("/v1/models", ModelsAsync).RequireCors("v1");

        app.MapPost("/api/admin/test/chat", TestChatAsync);
        app.MapPost("/api/admin/test/models", ModelsAsync);
        app.MapPost("/api/admin/test/credentials/{accountId}", TestCredentialAsync);
        app.MapPost("/api/admin/test/proxy/{proxyId}", TestProxyAsync);
        app.MapPost("/api/admin/test/compare", CompareAsync);
        app.MapGet("/api/admin/test/sessions", () => Results.Ok(Array.Empty<object>()));
    }

    private static IResult Me(HttpContext context)
        => context.Items.TryGetValue(typeof(AdminPrincipal), out var value) && value is AdminPrincipal principal
            ? Results.Ok(principal)
            : Results.Unauthorized();

    private static IResult LoginAsync(
        LoginRequest request,
        AdminAuthService auth,
        HttpResponse response)
    {
        var result = auth.Login(request.Username, request.Password);
        if (!result.Success || result.SessionId is null) return Results.Unauthorized();

        response.Cookies.Append("router_admin_session", result.SessionId, new CookieOptions
        {
            HttpOnly = true,
            Secure = response.HttpContext.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddHours(12)
        });
        response.Cookies.Append("router_admin_csrf", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), new CookieOptions
        {
            HttpOnly = false,
            Secure = response.HttpContext.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddHours(12)
        });
        return Results.Ok(result.Principal);
    }

    private static IResult LogoutAsync(HttpContext context, AdminAuthService auth)
    {
        if (context.Request.Cookies["router_admin_session"] is { } session)
            auth.Logout(session);
        context.Response.Cookies.Delete("router_admin_session");
        context.Response.Cookies.Delete("router_admin_csrf");
        return Results.NoContent();
    }

    private static IResult ChangePasswordAsync(
        PasswordChangeRequest request,
        HttpContext context,
        AdminAuthService auth)
    {
        if (!context.Items.TryGetValue(typeof(AdminPrincipal), out var value) || value is not AdminPrincipal principal)
            return Results.Unauthorized();
        return auth.ChangePassword(principal, request.CurrentPassword, request.NewPassword)
            ? Results.NoContent()
            : Results.BadRequest(new { error = "current password is invalid or new password is too short" });
    }

    private static async Task<IResult> GetPlatformModelsAsync(
        string platform,
        IModelCatalog catalog,
        CancellationToken cancellationToken)
        => Results.Ok((await catalog.ListAsync(platform, cancellationToken)));

    private static async Task<IResult> GetModelPlazaAsync(
        IModelCatalog modelCatalog,
        IModelMetadataCatalog metadataCatalog,
        IPlatformRegistry platforms,
        CancellationToken cancellationToken)
    {
        var pluginModels = await modelCatalog.ListAsync(cancellationToken);
        var metadata = await ReadModelMetadataAsync(metadataCatalog, forceRefresh: false, cancellationToken);
        return Results.Ok(ToModelPlazaDto(pluginModels, metadata, platforms));
    }

    private static async Task<IResult> RefreshModelPlazaAsync(
        IModelCatalog modelCatalog,
        IModelMetadataCatalog metadataCatalog,
        IPlatformRegistry platforms,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            platforms.All
                .Where(platform => platform.Enabled)
                .Select(platform => modelCatalog.RefreshAsync(platform.Name, cancellationToken)));

        var pluginModels = await modelCatalog.ListAsync(cancellationToken);
        var metadata = await ReadModelMetadataAsync(metadataCatalog, forceRefresh: true, cancellationToken);
        return Results.Ok(ToModelPlazaDto(pluginModels, metadata, platforms));
    }

    private static async Task<IResult> RefreshModelMetadataAsync(
        IModelMetadataCatalog catalog,
        CancellationToken cancellationToken)
    {
        var snapshot = await catalog.GetAsync(forceRefresh: true, cancellationToken);
        return Results.Ok(new
        {
            updatedAt = snapshot.UpdatedAt,
            modelCount = snapshot.Models.Count,
            protocolCount = snapshot.Protocols?.Count ?? 0,
            protocolsUpdatedAt = snapshot.ProtocolsUpdatedAt
        });
    }

    private static async Task<ModelMetadataSnapshot> ReadModelMetadataAsync(
        IModelMetadataCatalog catalog,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        try
        {
            return await catalog.GetAsync(forceRefresh, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // 插件模型仍然是模型广场的主数据源；models.dev 暂时不可用时保留插件声明的能力。
            return new ModelMetadataSnapshot(DateTimeOffset.MinValue, []);
        }
    }

    private static async Task<IResult> GetLogsAsync(
        int? page,
        int? pageSize,
        string? fromUtc,
        string? toUtc,
        string? platform,
        string? model,
        string? keyword,
        IRequestLogStore logs,
        CancellationToken cancellationToken)
        => Results.Ok(await logs.QueryAsync(
            new RequestLogQuery(
                page ?? 1,
                pageSize ?? 20,
                ParseDate(fromUtc),
                ParseDate(toUtc),
                platform,
                model,
                keyword),
            cancellationToken));

    private static async Task<IResult> GetAnalyticsOverviewAsync(
        string? fromUtc,
        string? toUtc,
        string? timezone,
        IAnalyticsService analytics,
        IRealtimeMetrics metrics,
        CancellationToken cancellationToken)
    {
        var (from, to) = ResolveRange(fromUtc, toUtc, timezone);
        var report = await analytics.QueryAsync(new AnalyticsQuery(from, to, Bucket: "hour"), cancellationToken);
        return Results.Ok(new { range = report, live = metrics.Snapshot(), timezone = timezone ?? "Asia/Shanghai" });
    }

    private static async Task<IResult> GetAnalyticsAsync(
        string? fromUtc,
        string? toUtc,
        string? platform,
        string? model,
        string? bucket,
        IAnalyticsService analytics,
        CancellationToken cancellationToken)
    {
        var (from, to) = ResolveRange(fromUtc, toUtc, null);
        return Results.Ok(await analytics.QueryAsync(
            new AnalyticsQuery(from, to, platform, model, bucket ?? "hour"),
            cancellationToken));
    }

    private static async Task<IResult> GetAccountsAsync(
        int? page,
        int? pageSize,
        string? pluginKey,
        string? platform,
        ResourceState? state,
        string? keyword,
        IAccountService accounts,
        CancellationToken cancellationToken)
        => Results.Ok(ToPagedDto(await accounts.QueryAsync(
            new AccountQuery(pluginKey, platform, state, keyword, page ?? 1, pageSize ?? 20),
            cancellationToken)));

    private static async Task<IResult> GetPlatformAccountsAsync(
        string platform,
        int? page,
        int? pageSize,
        IPlatformRegistry registry,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        var registration = registry.Get(platform);
        if (registration is null) return Results.NotFound();
        return Results.Ok(ToPagedDto(await accounts.QueryAsync(
            new AccountQuery(registration.PluginKey, platform, Page: page ?? 1, PageSize: pageSize ?? 20),
            cancellationToken)));
    }

    private static async Task<IResult> SaveAccountAsync(
        string platform,
        JsonElement body,
        IAccountService accounts,
        IPlatformRegistry registry,
        CancellationToken cancellationToken)
    {
        var registration = registry.Get(platform);
        if (registration is null) return Results.NotFound(new { error = "platform not found" });
        var accountId = ReadString(body, "id");
        var existing = accountId is null
            ? null
            : await accounts.GetAsync(registration.PluginKey, accountId, cancellationToken);
        if (accountId is not null && existing is null)
            return Results.NotFound(new { error = "account not found" });
        if (existing is not null && !string.Equals(existing.Platform, platform, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "account platform cannot be changed" });
        var credential = ParseCredential(body);
        if (credential is null)
            return Results.BadRequest(new { error = "a supported credential is required" });

        var account = new Account
        {
            Id = accountId ?? Guid.NewGuid().ToString("N"),
            PluginKey = registration.PluginKey,
            Platform = platform,
            Credential = credential,
            Label = ReadString(body, "label", "displayName"),
            Status = existing?.Status ?? new ResourceStatus(),
            ExpiresAt = credential switch
            {
                OAuthCredential oauth => oauth.ExpiresAt ?? existing?.ExpiresAt,
                BearerTokenCredential bearer => bearer.ExpiresAt ?? existing?.ExpiresAt,
                _ => existing?.ExpiresAt
            }
        };
        return Results.Ok(ToAccountDto(await accounts.SaveAsync(account, cancellationToken)));
    }

    private static async Task<IResult> DeleteAccountAsync(
        string id,
        string? pluginKey,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        var account = pluginKey is null
            ? await accounts.GetAnyAsync(id, cancellationToken)
            : await accounts.GetAsync(pluginKey, id, cancellationToken);
        if (account is null) return Results.NotFound();
        await accounts.DeleteAsync(account.PluginKey, id, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetProxiesAsync(
        int? page,
        int? pageSize,
        string? subscriptionIds,
        ResourceState? state,
        IProxyStore proxies,
        CancellationToken cancellationToken)
        => Results.Ok(ToPagedDto(await proxies.QueryAsync(
            new ProxyQuery(
                string.IsNullOrWhiteSpace(subscriptionIds)
                    ? null
                    : subscriptionIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                state,
                page ?? 1,
                pageSize ?? 20),
            cancellationToken)));

    private static async Task<IResult> SaveProxyAsync(
        ProxyRequest request,
        IProxyStore proxies,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Host) || request.Port is <= 0 or > 65535)
            return Results.BadRequest(new { error = "host and port are required" });

        var proxy = new ProxyEndpoint
        {
            Id = request.Id ?? Guid.NewGuid().ToString("N"),
            SubscriptionId = request.SubscriptionId ?? "manual",
            Host = request.Host,
            Port = request.Port,
            Scheme = ParseProxyScheme(request.Scheme),
            Username = request.Username,
            Password = request.Password
        };
        await proxies.SaveAsync(proxy, cancellationToken);
        return Results.Ok(ToProxyDto(proxy));
    }

    private static async Task<IResult> DeleteProxyAsync(
        string id,
        IProxyStore proxies,
        CancellationToken cancellationToken)
    {
        await proxies.RemoveAsync(id, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ProbeProxyAsync(
        string id,
        IProxyStore proxies,
        IProxyProbeService probe,
        CancellationToken cancellationToken)
    {
        var proxy = await proxies.GetAsync(id, cancellationToken);
        if (proxy is null) return Results.NotFound();
        return Results.Ok(await probe.ProbeAsync(proxy, cancellationToken));
    }

    private static async Task<IResult> ListSubscriptionsAsync(
        IProxySubscriptionService subscriptions,
        CancellationToken cancellationToken)
        => Results.Ok((await subscriptions.ListAsync(cancellationToken)).Select(ToSubscriptionDto));

    private static async Task<IResult> SaveSubscriptionAsync(
        ProxySubscriptionRequest request,
        IProxySubscriptionService subscriptions,
        IProxySubscriptionRefreshQueue refreshQueue,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name)
            || !Uri.TryCreate(request.Url, UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https"))
            return Results.BadRequest(new { error = "subscription name and an http(s) url are required" });
        if (!TryParseRefreshInterval(request.RefreshInterval, out var refreshIntervalMinutes))
            return Results.BadRequest(new { error = "refresh interval must use a positive number followed by H or M, for example 2H or 30M" });

        var subscription = new ProxySubscription
        {
            Id = request.Id ?? Guid.NewGuid().ToString("N"),
            Name = request.Name.Trim(),
            Url = request.Url.Trim(),
            Scheme = ParseProxyScheme(request.Scheme),
            RefreshIntervalMinutes = refreshIntervalMinutes
        };
        var saved = await subscriptions.SaveAsync(subscription, cancellationToken);
        refreshQueue.Enqueue(saved.Id);
        return Results.Accepted($"/api/admin/proxy-subscriptions/{saved.Id}", ToSubscriptionDto(saved));
    }

    private static async Task<IResult> DeleteSubscriptionAsync(
        string id,
        IProxySubscriptionService subscriptions,
        CancellationToken cancellationToken)
    {
        await subscriptions.DeleteAsync(id, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RefreshSubscriptionAsync(
        string id,
        IProxySubscriptionService subscriptions,
        IProxySubscriptionRefreshQueue refreshQueue,
        CancellationToken cancellationToken)
    {
        if (!(await subscriptions.ListAsync(cancellationToken)).Any(subscription => subscription.Id == id))
            return Results.NotFound();

        refreshQueue.Enqueue(id);
        return Results.Accepted($"/api/admin/proxy-subscriptions/{id}", new { queued = true });
    }

    private static async Task<IResult> ReloadPluginsAsync(
        IPluginCatalog catalog,
        CancellationToken cancellationToken)
    {
        await catalog.ReloadAsync(null, cancellationToken);
        return Results.Ok(catalog.All);
    }

    private static async Task<IResult> ReloadPluginAsync(
        string name,
        IPluginCatalog catalog,
        CancellationToken cancellationToken)
    {
        await catalog.ReloadAsync(name, cancellationToken);
        return catalog.Get(name) is { } plugin ? Results.Ok(plugin) : Results.NotFound();
    }

    private static async Task<IResult> SetPluginStateAsync(
        string pluginKey,
        PluginStateRequest request,
        IPluginCatalog catalog,
        CancellationToken cancellationToken)
    {
        var plugin = await catalog.SetEnabledAsync(pluginKey, request.Enabled, cancellationToken);
        if (plugin is null) return Results.NotFound();

        var reachedRequestedState = request.Enabled
            ? plugin.State == "Active"
            : plugin.State == "Disabled";
        return reachedRequestedState
            ? Results.Ok(plugin)
            : Results.Conflict(new { error = request.Enabled ? "plugin activation failed" : "plugin is busy; disable failed" });
    }

    private static IResult GetPluginManifest(string pluginKey, IPluginCatalog catalog)
        => catalog.Get(pluginKey) is { } descriptor
            ? Results.Ok(new
            {
                descriptor.PluginKey,
                descriptor.Name,
                descriptor.Description,
                descriptor.Version,
                descriptor.Runtime,
                descriptor.HasMainPage,
                descriptor.MainPageTitle,
                descriptor.MainPageVersion,
                descriptor.Tasks,
                descriptor.Routes
            })
            : Results.NotFound();

    private static IResult GetPluginPage(string pluginKey, IPluginCatalog catalog)
        => catalog.GetMainPage(pluginKey) is { } page
            ? Results.Content(page.Html, "text/html; charset=utf-8")
            : Results.NotFound();

    private static async Task<IResult> RunPluginTaskAsync(
        string pluginKey,
        string taskName,
        PluginTaskRunner runner,
        CancellationToken cancellationToken)
        => await runner.RunAsync(pluginKey, taskName, cancellationToken: cancellationToken)
            ? Results.Accepted()
            : Results.NotFound(new { error = "plugin task not found" });

    private static async Task<IResult> GetTaskLogsAsync(
        int? page,
        int? pageSize,
        string? fromUtc,
        string? toUtc,
        string? pluginKey,
        string? platform,
        string? taskName,
        string? status,
        string? keyword,
        ITaskLogStore logs,
        CancellationToken cancellationToken)
        => Results.Ok(ToPagedDto(await logs.QueryAsync(
            new TaskLogQuery(
                page ?? 1,
                pageSize ?? 20,
                ParseDate(fromUtc),
                ParseDate(toUtc),
                pluginKey,
                platform,
                taskName,
                status,
                keyword),
            cancellationToken), log => new
            {
                id = log.Id,
                pluginKey = log.PluginKey,
                platform = log.Platform,
                taskName = log.TaskName,
                accountId = log.AccountId,
                status = log.Status,
                message = log.Message,
                error = log.Error,
                detailsJson = log.DetailsJson,
                durationMs = log.DurationMs,
                startedAt = log.StartedAt,
                finishedAt = log.FinishedAt
            }));

    private static async Task<IResult> GetPluginLogsAsync(
        int? page,
        int? pageSize,
        string? fromUtc,
        string? toUtc,
        string? pluginKey,
        string? platform,
        string? taskName,
        string? level,
        string? eventType,
        string? keyword,
        IPluginLogStore logs,
        CancellationToken cancellationToken)
        => Results.Ok(ToPagedDto(await logs.QueryAsync(
            new PluginLogQuery(
                page ?? 1,
                pageSize ?? 20,
                ParseDate(fromUtc),
                ParseDate(toUtc),
                pluginKey,
                platform,
                taskName,
                level,
                eventType,
                keyword),
            cancellationToken), log => new
            {
                id = log.Id,
                pluginKey = log.PluginKey,
                platform = log.Platform,
                level = log.Level,
                eventType = log.EventType,
                message = log.Message,
                traceId = log.TraceId,
                taskName = log.TaskName,
                accountId = log.AccountId,
                model = log.Model,
                statusCode = log.StatusCode,
                durationMs = log.DurationMs,
                detailsJson = log.DetailsJson,
                createdAt = log.CreatedAt
            }));

    private static async Task<IResult> SaveConfigAsync(
        ConfigUpdateRequest request,
        IConfigFileService config,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await config.SaveAsync(request, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static Task<IResult> ChatAsync(
        JsonElement body,
        RouterPipeline pipeline,
        HttpContext httpContext)
        => ExecuteRequestAsync(body, TestEndpoint.ChatCompletions, pipeline, httpContext);

    private static Task<IResult> CompletionsAsync(
        JsonElement body,
        RouterPipeline pipeline,
        HttpContext httpContext)
        => ExecuteRequestAsync(body, TestEndpoint.Completions, pipeline, httpContext);

    private static Task<IResult> ResponsesAsync(
        JsonElement body,
        RouterPipeline pipeline,
        HttpContext httpContext)
        => ExecuteRequestAsync(body, TestEndpoint.Responses, pipeline, httpContext);

    private static Task<IResult> AnthropicMessagesAsync(
        JsonElement body,
        RouterPipeline pipeline,
        HttpContext httpContext)
        => ExecuteRequestAsync(body, TestEndpoint.AnthropicMessages, pipeline, httpContext);

    private static async Task<IResult> ExecuteRequestAsync(
        JsonElement body,
        TestEndpoint endpoint,
        RouterPipeline pipeline,
        HttpContext httpContext,
        TestOverrides? overrides = null)
    {
        var request = RequestParser.Parse(body, endpoint);
        CaptureRequestHeaders(
            httpContext,
            request,
            captureAllHeaders: httpContext.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase));
        var context = new RequestContext
        {
            Request = request,
            Services = httpContext.RequestServices,
            TestOverrides = overrides,
            CancellationToken = httpContext.RequestAborted,
            TraceId = TraceIdContext.GetOrCreate(httpContext)
        };
        try
        {
            await pipeline.ExecuteAsync(context);
            await ProtocolResponseWriter.WriteAsync(
                httpContext,
                context.Response ?? AdapterResponse.ServerError("empty response"),
                endpoint,
                request.Model,
                httpContext.RequestAborted,
                request);
            return Results.Empty;
        }
        finally
        {
            if (context.Response?.Lifetime is { } lifetime) await lifetime.DisposeAsync();
        }
    }

    private static void CaptureRequestHeaders(
        HttpContext httpContext,
        AdapterRequest request,
        bool captureAllHeaders = false)
    {
        foreach (var header in httpContext.Request.Headers)
        {
            var name = header.Key;
            if (captureAllHeaders)
                request.DownstreamRequestHeaders[name] = header.Value.ToArray();
            if (!IsSafeRequestHeader(name)) continue;
            request.RequestHeaders[name] = header.Value.ToString();
        }
    }

    private static bool IsSafeRequestHeader(string name)
    {
        if (name.StartsWith("x-forwarded-", StringComparison.OrdinalIgnoreCase)
            || name.Equals("x-real-ip", StringComparison.OrdinalIgnoreCase)
            || name.Equals("x-trace-id", StringComparison.OrdinalIgnoreCase)
            || name.Equals("x-csrf-token", StringComparison.OrdinalIgnoreCase)
            || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
            return false;

        return name.StartsWith("x-", StringComparison.OrdinalIgnoreCase)
            || name.Equals("user-agent", StringComparison.OrdinalIgnoreCase)
            || name.Equals("accept", StringComparison.OrdinalIgnoreCase)
            || name.Equals("anthropic-version", StringComparison.OrdinalIgnoreCase)
            || name.Equals("anthropic-beta", StringComparison.OrdinalIgnoreCase)
            || name.Equals("content-type", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IResult> TestChatAsync(
        JsonElement body,
        RouterPipeline pipeline,
        HttpContext httpContext)
    {
        if (!RequestParser.TryParseEndpoint(body, out var endpoint))
            return Results.BadRequest(new { error = "endpoint must be Chat Completions, Completions, Responses, or Anthropic Messages" });

        var overrides = body.TryGetProperty("overrides", out var rawOverrides)
            ? JsonSerializer.Deserialize<TestOverrides>(rawOverrides.GetRawText())
            : null;
        return await ExecuteRequestAsync(body, endpoint, pipeline, httpContext, overrides);
    }

    private static async Task<IResult> TestCredentialAsync(
        string accountId,
        IAccountService accounts,
        IPlatformRegistry platforms,
        CancellationToken cancellationToken)
    {
        var account = await accounts.GetAnyAsync(accountId, cancellationToken);
        if (account is null) return Results.NotFound(new { error = "account not found" });
        var registration = platforms.Get(account.Platform);
        if (registration is null) return Results.NotFound(new { error = "platform not found" });

        var result = await registration.Terminal.ValidateCredentialAsync(account.Credential, cancellationToken);
        return result.Success
            ? Results.Ok(new { accountId, valid = true })
            : Results.BadRequest(new { accountId, valid = false, error = result.Error });
    }

    internal static async Task<IResult> TestProxyAsync(
        string proxyId,
        IProxyStore proxies,
        IProxyPolicyStore policyStore,
        IPlatformRegistry platforms,
        JsonElement body,
        CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("pluginKey", out var plugin)
            || plugin.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(plugin.GetString()))
            return Results.BadRequest(new { error = "pluginKey is required" });
        var registration = platforms.All.FirstOrDefault(item =>
            item.PluginKey.Equals(plugin.GetString()!.Trim(), StringComparison.OrdinalIgnoreCase));
        if (registration is null)
            return Results.BadRequest(new { error = "pluginKey must identify an installed platform" });

        var proxy = await proxies.GetAsync(proxyId, cancellationToken);
        if (proxy is null) return Results.NotFound(new { error = "proxy not found" });
        var pluginKey = registration.PluginKey;
        var policy = await policyStore.GetAsync(pluginKey, proxy, cancellationToken);
        var available = proxy.Status.State == ResourceState.Active
            && policy?.State != ProxyPolicyState.Cooldown;
        return Results.Ok(new
        {
            proxyId,
            pluginKey,
            available,
            endpoint = proxy.EndpointKey,
            state = proxy.Status.State.ToString()
        });
    }

    private static async Task<IResult> CompareAsync(
        JsonElement body,
        RouterPipeline pipeline,
        HttpContext httpContext)
    {
        if (!body.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
            return Results.BadRequest(new { error = "models must be an array" });

        var baseRequest = RequestParser.Parse(body);
        var overrides = body.TryGetProperty("overrides", out var rawOverrides)
            ? JsonSerializer.Deserialize<TestOverrides>(rawOverrides.GetRawText())
            : null;
        var results = new List<object>();
        foreach (var model in models.EnumerateArray())
        {
            var modelName = model.GetString();
            if (string.IsNullOrWhiteSpace(modelName)) continue;
            var request = new AdapterRequest
            {
                Model = modelName,
                Endpoint = baseRequest.Endpoint,
                OriginalBody = baseRequest.OriginalBody,
                Messages = baseRequest.Messages,
                Stream = false,
                MaxTokens = baseRequest.MaxTokens,
                Temperature = baseRequest.Temperature,
                Tools = baseRequest.Tools
            };
            foreach (var extension in baseRequest.Extensions)
                request.Extensions[extension.Key] = extension.Value;
            foreach (var header in baseRequest.RequestHeaders)
                request.RequestHeaders[header.Key] = header.Value;
            var context = new RequestContext
            {
                Request = request,
                Services = httpContext.RequestServices,
                TestOverrides = overrides,
                CancellationToken = httpContext.RequestAborted
            };
            await pipeline.ExecuteAsync(context);
            results.Add(new
            {
                model = modelName,
                statusCode = context.Response?.StatusCode ?? 500,
                success = context.Response?.IsSuccess == true,
                error = context.Response?.Error,
                usage = context.Response?.Usage
            });
        }

        return Results.Ok(new { results });
    }

    private static async Task<IResult> ModelsAsync(
        IModelCatalog catalog,
        CancellationToken cancellationToken)
        => Results.Ok(new
        {
            @object = "list",
            data = await catalog.ListAsync(cancellationToken)
        });

    private static IResult EmbeddingsAsync()
        => Results.Json(
            ProtocolApiErrors.OpenAi(
                "embeddings adapter is not enabled in the MVP",
                StatusCodes.Status501NotImplemented),
            statusCode: StatusCodes.Status501NotImplemented);

    private static async Task StreamMetricsAsync(HttpContext context, IRealtimeMetrics metrics)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        while (!context.RequestAborted.IsCancellationRequested)
        {
            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(metrics.Snapshot())}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted);
        }
    }

    private static object ToPlatformDto(PlatformRegistration registration)
        => new
        {
            name = registration.Name,
            pluginKey = registration.PluginKey,
            displayName = registration.DisplayName,
            probeEndpoint = registration.ProbeEndpoint,
            enabled = registration.Enabled
        };

    private static object ToModelPlazaDto(
        IReadOnlyList<ModelDescriptor> pluginModels,
        ModelMetadataSnapshot snapshot,
        IPlatformRegistry platforms)
    {
        DateTimeOffset? updatedAt = snapshot.UpdatedAt == DateTimeOffset.MinValue
            ? null
            : snapshot.UpdatedAt;

        var models = pluginModels
            .Select(pluginModel =>
            {
                var separator = pluginModel.Id.IndexOf('/');
                if (separator <= 0 || separator == pluginModel.Id.Length - 1)
                    return null;

                var platform = pluginModel.Id[..separator];
                var modelId = pluginModel.Id[(separator + 1)..];
                var metadata = FindModelMetadata(snapshot, platform, modelId);
                var registration = platforms.Get(platform);

                return new
                {
                    id = pluginModel.Id,
                    platform,
                    platformName = registration?.DisplayName ?? metadata?.PlatformName ?? platform,
                    displayName = string.IsNullOrWhiteSpace(pluginModel.DisplayName)
                        ? metadata?.DisplayName ?? modelId
                        : pluginModel.DisplayName,
                    contextWindow = metadata is { ContextWindow: > 0 }
                        ? metadata.ContextWindow
                        : pluginModel.ContextWindow,
                    inputLimit = metadata is { InputLimit: > 0 }
                        ? metadata.InputLimit
                        : pluginModel.InputLimit,
                    outputLimit = metadata is { OutputLimit: > 0 }
                        ? metadata.OutputLimit
                        : pluginModel.OutputLimit,
                    supportsReasoning = metadata?.SupportsReasoning == true || pluginModel.SupportsReasoning,
                    reasoningLevels = metadata?.ReasoningLevels is { Count: > 0 }
                        ? metadata.ReasoningLevels
                        : pluginModel.ReasoningLevels ?? Array.Empty<string>(),
                    reasoningTokenLimit = metadata?.ReasoningTokenLimit ?? pluginModel.ReasoningTokenLimit
                };
            })
            .Where(model => model is not null)
            .GroupBy(model => model!.id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()!)
            .OrderBy(model => model.platformName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.displayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new
        {
            updatedAt,
            source = snapshot.Models.Count > 0 ? "plugins + models.dev" : "plugins",
            models
        };
    }

    private static ModelMetadata? FindModelMetadata(
        ModelMetadataSnapshot snapshot,
        string platform,
        string modelId)
    {
        var fullId = $"{platform}/{modelId}";
        return snapshot.Models.FirstOrDefault(model =>
                   model.Id.Equals(fullId, StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Models.FirstOrDefault(model =>
                model.Id.EndsWith('/' + modelId, StringComparison.OrdinalIgnoreCase));
    }

    private static object ToSubscriptionDto(ProxySubscription subscription)
        => new
        {
            id = subscription.Id,
            name = subscription.Name,
            url = RemoveUrlUserInfo(subscription.Url),
            scheme = subscription.Scheme.ToString(),
            enabled = subscription.Enabled,
            parserName = subscription.ParserName,
            refreshIntervalMinutes = subscription.RefreshIntervalMinutes,
            lastRefreshAt = subscription.LastFetchedAt,
            endpointCount = subscription.LastFetchedCount,
            lastError = subscription.LastError
        };

    private static string RemoveUrlUserInfo(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            return value;

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty
        };
        return builder.Uri.ToString();
    }

    private static object ToAccountDto(Account account)
        => new
        {
            id = account.Id,
            pluginKey = account.PluginKey,
            platform = account.Platform,
            label = account.Label,
            enabled = account.Status.State == ResourceState.Active,
            state = account.Status.State.ToString(),
            credentialKind = account.Credential.Kind.ToString(),
            credential = CredentialValues(account.Credential),
            credentialFields = CredentialFields(account.Credential),
            expiresAt = account.ExpiresAt
        };

    private static object ToPagedDto<T>(PagedResult<T> result, Func<T, object>? map = null)
    {
        var items = map is null
            ? result.Items.Cast<object>().ToArray()
            : result.Items.Select(map).ToArray();
        return new
        {
            items,
            page = result.Page,
            pageSize = result.PageSize,
            total = result.Total,
            totalPages = result.TotalPages
        };
    }

    private static object ToPagedDto(PagedResult<Account> result)
        => ToPagedDto(result, ToAccountDto);

    private static object ToPagedDto(PagedResult<ProxyEndpoint> result)
        => ToPagedDto(result, ToProxyDto);

    private static object ToProxyDto(ProxyEndpoint proxy)
        => new
        {
            id = proxy.Id,
            subscriptionId = proxy.SubscriptionId,
            host = proxy.Host,
            port = proxy.Port,
            scheme = proxy.Scheme.ToString(),
            state = proxy.Status.State.ToString(),
            latencyMs = proxy.LatencyMs,
            averageLatencyMs = proxy.AverageLatencyMs,
            averageSpeedBytesPerSecond = proxy.AverageSpeedBytesPerSecond,
            compositeScore = proxy.CompositeScore,
            lastProbeAt = proxy.LastProbeAt,
            probeStatus = proxy.ProbeStatus,
            probeConfigurationVersion = proxy.ProbeConfigurationVersion,
            lastProbeSuccessAt = proxy.LastProbeSuccessAt,
            probeConsecutiveFailures = proxy.ProbeConsecutiveFailures,
            probeError = proxy.ProbeError
        };

    private static string[] CredentialFields(Credential credential)
        => credential switch
        {
            ApiKeyCredential => ["apiKey"],
            BasicAuthCredential => ["username", "password"],
            OAuthCredential => ["access_token", "refresh_token", "id_token", "account_id", "domain", "enterprise_id", "nickname"],
            BearerTokenCredential => ["token"],
            CookieCredential => ["cookie"],
            CustomCredential custom => custom.Fields.Keys.ToArray(),
            _ => []
        };

    private static Dictionary<string, object?> CredentialValues(Credential credential)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        switch (credential)
        {
            case ApiKeyCredential apiKey:
                values["apiKey"] = apiKey.ApiKey;
                break;
            case BasicAuthCredential basic:
                values["username"] = basic.Username;
                values["password"] = basic.Password;
                break;
            case OAuthCredential oauth:
                values["accessToken"] = oauth.AccessToken;
                values["refreshToken"] = oauth.RefreshToken;
                values["idToken"] = oauth.IdToken;
                values["accountId"] = oauth.AccountId;
                values["domain"] = oauth.Domain;
                values["enterpriseId"] = oauth.EnterpriseId;
                values["nickname"] = oauth.Nickname;
                values["expiresAt"] = oauth.ExpiresAt;
                break;
            case BearerTokenCredential bearer:
                values["token"] = bearer.Token;
                break;
            case CookieCredential cookie:
                values["cookie"] = cookie.Cookie;
                break;
            case CustomCredential custom:
                foreach (var pair in custom.Fields)
                    values[pair.Key] = pair.Value;
                break;
        }

        return values;
    }

    private static Credential? ParseCredential(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        var credential = ReadObject(body, "credential", "credentials") ?? body;
        var kind = Normalize(ReadString(body, "credentialKind", "credential_kind", "credentialType", "credential_type")
            ?? ReadString(credential, "kind", "credentialKind", "type"));
        var apiKey = ReadCredentialString(body, credential, "apiKey", "api_key", "key");
        if (kind is "apikey" or "key" || apiKey is not null)
            return new ApiKeyCredential(apiKey ?? string.Empty);

        var username = ReadCredentialString(body, credential, "username", "user");
        var password = ReadCredentialString(body, credential, "password", "pass");
        if (kind is "basicauth" or "basic" or "usernamepassword" || username is not null || password is not null)
            return new BasicAuthCredential(username ?? string.Empty, password ?? string.Empty);

        var accessToken = ReadCredentialString(body, credential, "accessToken", "access_token");
        var refreshToken = ReadCredentialString(body, credential, "refreshToken", "refresh_token");
        var idToken = ReadCredentialString(body, credential, "idToken", "id_token");
        var accountId = ReadCredentialString(body, credential, "accountId", "account_id");
        var domain = ReadCredentialString(body, credential, "domain");
        var enterpriseId = ReadCredentialString(body, credential, "enterpriseId", "enterprise_id");
        var nickname = ReadCredentialString(body, credential, "nickname", "displayName", "display_name");
        if (kind is "oauth" or "oauth2" or "tokenbundle"
            || accessToken is not null || refreshToken is not null || idToken is not null || accountId is not null
            || domain is not null || enterpriseId is not null || nickname is not null)
            return new OAuthCredential(
                accessToken ?? string.Empty,
                ParseDate(ReadCredentialString(body, credential, "expiresAt", "expires_at")),
                refreshToken,
                idToken,
                accountId,
                domain,
                enterpriseId,
                nickname);

        var token = ReadCredentialString(body, credential, "token");
        if (kind is "bearer" or "bearertoken" or "token" || token is not null)
            return new BearerTokenCredential(
                token ?? string.Empty,
                ParseDate(ReadCredentialString(body, credential, "expiresAt", "expires_at")));

        var cookie = ReadCredentialString(body, credential, "cookie");
        if (kind == "cookie" || cookie is not null)
            return new CookieCredential(cookie ?? string.Empty);

        var fields = credential.ValueKind == JsonValueKind.Object
            ? credential.EnumerateObject()
                .Where(property => Normalize(property.Name) is not ("id" or "label" or "platform" or "credentialkind"))
                .Where(property => property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                .ToDictionary(property => property.Name, property => (string?)property.Value.ToString(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        return fields.Count > 0 ? new CustomCredential(fields) : null;
    }

    private static string? ReadCredentialString(JsonElement body, JsonElement credential, params string[] names)
        => ReadString(credential, names) ?? (credential.Equals(body) ? null : ReadString(body, names));

    private static string? ReadString(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Any(name => Normalize(property.Name) == Normalize(name))) continue;
            return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.ToString();
        }

        return null;
    }

    private static JsonElement? ReadObject(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in value.EnumerateObject())
        {
            if (names.Any(name => Normalize(property.Name) == Normalize(name))
                && property.Value.ValueKind == JsonValueKind.Object)
                return property.Value;
        }

        return null;
    }

    private static string Normalize(string? value)
        => (value ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var date) ? date : null;

    private static (DateTimeOffset From, DateTimeOffset To) ResolveRange(
        string? fromUtc,
        string? toUtc,
        string? timezone)
    {
        var parsedFrom = ParseDate(fromUtc);
        var parsedTo = ParseDate(toUtc);
        if (parsedFrom is { } from && parsedTo is { } to && to > from)
            return (from.ToUniversalTime(), to.ToUniversalTime());

        var zone = ResolveTimeZone(timezone);
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var localStart = new DateTimeOffset(localNow.Date, localNow.Offset);
        return (localStart.ToUniversalTime(), localStart.AddDays(1).ToUniversalTime());
    }

    private static TimeZoneInfo ResolveTimeZone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TryFindTimeZone("Asia/Shanghai")
                ?? TryFindTimeZone("China Standard Time")
                ?? TimeZoneInfo.Utc;
        }

        return TryFindTimeZone(value)
            ?? (value.Equals("Asia/Shanghai", StringComparison.OrdinalIgnoreCase)
                ? TryFindTimeZone("China Standard Time") ?? TimeZoneInfo.Utc
                : TimeZoneInfo.Utc);
    }

    private static TimeZoneInfo? TryFindTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }

    private static bool TryParseRefreshInterval(string? value, out int minutes)
    {
        minutes = 60;
        if (string.IsNullOrWhiteSpace(value)) return true;

        var normalized = value.Trim();
        if (normalized.Length < 2) return false;

        var unit = char.ToUpperInvariant(normalized[^1]);
        var amountText = normalized[..^1];
        if (unit is not ('H' or 'M')
            || amountText.Length == 0
            || amountText[0] == '0'
            || amountText.Any(character => character is < '0' or > '9')
            || !int.TryParse(amountText, out var amount)
            || amount <= 0)
            return false;

        try
        {
            minutes = unit == 'H' ? checked(amount * 60) : amount;
            return true;
        }
        catch (OverflowException)
        {
            minutes = 60;
            return false;
        }
    }

    private static ProxyScheme ParseProxyScheme(string? value)
        => Normalize(value) switch
        {
            "https" => ProxyScheme.Https,
            "socks" or "socks5" or "socks5h" or "s5" => ProxyScheme.Socks5,
            _ => ProxyScheme.Http
        };

    private static IResult SetPlatformStateAsync(string platform, PlatformStateRequest request, IPlatformRegistry registry)
    {
        if (registry.Get(platform) is null) return Results.NotFound();
        registry.SetEnabled(platform, request.Enabled);
        return Results.Ok(new { platform, enabled = request.Enabled });
    }

    public sealed record LoginRequest(string Username, string Password);
    public sealed record PluginStateRequest(bool Enabled);
    public sealed record PlatformStateRequest(bool Enabled);
    public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);
    public sealed record ProxyRequest(string? Id, string? SubscriptionId, string Host, int Port, string? Scheme, string? Username, string? Password);
    public sealed record ProxySubscriptionRequest(
        string? Id,
        string Name,
        string Url,
        string? Scheme,
        string? RefreshInterval);
}
