using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Host;
using static Router.Host.Plugins.JavaScript.JsPluginCodec;

namespace Router.Host.Plugins.JavaScript;

/// <summary>HTTP 权限与 JSON/source/client 句柄投影；连接池、发送和重试仍由共享宿主工厂负责。</summary>
internal sealed class JsHttpCapabilities(JsPluginManifest manifest, IPluginServices host)
{
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    public async Task<object?> DispatchAsync(string operation, JsonObject input, JsInvocation invocation, CancellationToken token)
    {
        switch (operation)
        {
            case "http.request": return await SendAsync(input, invocation, false, token);
            case "http.open": return await SendAsync(input, invocation, true, token);
            case "http.createClient":
                var options = input.Deserialize<ClientOptions>(AttemptJsonOptions) ?? new ClientOptions();
                RequireRoute(options.Route);
                if (options.Route == "attempt") throw new InvalidOperationException("The attempt route already owns a fixed client.");
                if (options.AllowDirectFallback) RequireRoute("direct");
                var client = options.Route == "pool"
                    ? await host.Http.Pool.CreateClientAsync(new ProxyPoolHttpClientOptions
                    {
                        SubscriptionIds = options.SubscriptionIds, AllowDirectFallback = options.AllowDirectFallback,
                        RequestTimeout = Timeout.InfiniteTimeSpan
                    }, token)
                    : host.Http.CreateDirectClient(new PluginHttpClientOptions { RequestTimeout = Timeout.InfiniteTimeSpan });
                try { return new { handle = invocation.AddClient(client, options.Route) }; }
                catch { client.Dispose(); throw; }
            case "http.closeClient":
                invocation.CloseClient(Handle(input));
                return null;
            case "http.readText":
            case "http.readJson":
            case "http.readBase64":
                using (var source = invocation.TakeSource(Handle(input)))
                {
                    var bytes = await source.ReadBufferAsync(MaxBodyBytes, token);
                    if (operation == "http.readBase64") return Convert.ToBase64String(bytes);
                    var text = Encoding.UTF8.GetString(bytes);
                    return operation == "http.readJson" ? (text.Length == 0 ? null : JsonNode.Parse(text)) : text;
                }
            case "http.drain":
                using (var source = invocation.TakeSource(Handle(input)))
                {
                    await using var stream = await source.OpenStreamAsync(token);
                    var count = 0L;
                    await foreach (var chunk in source.ReadRawAsync(stream, token))
                    {
                        count += chunk.Length;
                        if (count > 32 * 1024 * 1024) throw new InvalidOperationException("Drain exceeds the response size limit.");
                    }
                    return new { bytesRead = count };
                }
            case "http.snapshotError":
                var errorSource = invocation.Source(Handle(input));
                if (errorSource.StatusCode < 400) throw new InvalidOperationException("snapshotError requires an HTTP error.");
                var snapshot = Encoding.UTF8.GetString(await errorSource.ReadBufferAsync(MaxBodyBytes, token));
                return new { text = snapshot.Length > 4000 ? snapshot[..4000] : snapshot, truncated = snapshot.Length > 4000 };
            case "http.close":
                invocation.CloseSource(Handle(input));
                return null;
            case "http.approvedOrigins":
                if (!manifest.Permissions.Http.ManageOrigins) throw new UnauthorizedAccessException("Origin approvals are not permitted.");
                return await host.Http.GetApprovedOriginsAsync(token);
            case "http.approveOrigin":
            case "http.revokeOrigin":
                if (!manifest.Permissions.Http.ManageOrigins || invocation.Context.Kind != JsCallKind.Control || invocation.Context.Endpoint is null)
                    throw new UnauthorizedAccessException("Origins may only be approved in an authenticated plugin management endpoint.");
                var origin = input["origin"]?.GetValue<string>() ?? throw new InvalidOperationException("An exact origin is required.");
                if (operation == "http.approveOrigin") await host.Http.ApproveOriginAsync(origin, token);
                else await host.Http.RevokeOriginAsync(origin, token);
                return null;
            default: throw new UnauthorizedAccessException("Unknown HTTP capability.");
        }
    }

    private async Task<object> SendAsync(JsonObject input, JsInvocation invocation, bool open, CancellationToken token)
    {
        var spec = input.Deserialize<RequestOptions>(JsPluginPackage.JsonOptions) ?? throw new InvalidOperationException("Invalid HTTP request.");
        var route = spec.Client is null ? spec.Route ?? "pool" : invocation.ClientRoute(spec.Client);
        RequireRoute(route);
        if (spec.Client is not null && spec.Route is not null && spec.Route != route)
            throw new InvalidOperationException("A fixed client's route cannot be changed.");
        var uri = await AllowedUriAsync(spec.Url, token);
        if (!JsPluginManifest.AllowedMethod(spec.Method) || spec.TimeoutMs is < 1 or > 60_000
            || spec.ReadIdleTimeoutMs is < 100 or > 600_000 || spec.ResponseType is not ("json" or "text" or "base64")
            || new[] { spec.Body.HasValue, spec.BodyText is not null, spec.BodyBase64 is not null, spec.Form is not null, spec.OriginalJson is not null }.Count(value => value) > 1)
            throw new InvalidOperationException("Invalid HTTP options.");
        if (spec.Retry is { MaxRetries: > 0 } && (route != "pool" || spec.Client is not null))
            throw new InvalidOperationException("Transport retries require the independent pool send operation, not a fixed client.");
        if (spec.Retry is { MaxRetries: > 0 } && invocation.Context.Attempt is not null && manifest.Policy.MaxAttempts > 1)
            throw new InvalidOperationException("Choose one retry owner: transport replay cannot be combined with multiple model attempts.");
        if (route == "attempt" && invocation.Context.Attempt is null)
            throw new UnauthorizedAccessException("The attempt route requires a terminal attempt context.");
        if (spec.AllowDirectFallback) RequireRoute("direct");
        var original = spec.OriginalJson is null ? null : OriginalJson(spec.OriginalJson, invocation);
        var method = new HttpMethod(spec.Method);
        var omitBody = false;
        var stripHeaders = false;
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(spec.TimeoutMs));
        var transferred = false;
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                HttpClient? ownedClient = null;
                HttpRequestMessage? ownedRequest = null;
                HttpResponseMessage? response = null;
                try
                {
                    HttpRequestMessage CreateRequest() => BuildRequest(spec, uri, method, original, omitBody, stripHeaders);
                    if (spec.Client is { } handle)
                    {
                        ownedRequest = CreateRequest();
                        response = await invocation.Client(handle).SendAsync(ownedRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    }
                    else if (route == "pool")
                    {
                        response = await host.Http.Pool.SendAsync(CreateRequest, new ProxyPoolHttpClientOptions
                        {
                            SubscriptionIds = spec.SubscriptionIds, AllowDirectFallback = spec.AllowDirectFallback,
                            RequestTimeout = TimeSpan.FromMilliseconds(spec.TimeoutMs)
                        }, spec.Retry is null ? null : new ProxyPoolRetryOptions
                        {
                            MaxRetries = spec.Retry.MaxRetries, Delay = TimeSpan.FromMilliseconds(spec.Retry.DelayMs),
                            AllowUnsafeMethods = spec.Retry.AllowUnsafeMethods
                        }, cancellationToken: timeout.Token);
                    }
                    else
                    {
                        ownedRequest = CreateRequest();
                        if (route == "attempt")
                            response = await invocation.Context.Attempt!.HttpClient.SendAsync(ownedRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        else
                        {
                            ownedClient = host.Http.CreateDirectClient(new PluginHttpClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(spec.TimeoutMs) });
                            response = await ownedClient.SendAsync(ownedRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        }
                    }
                    if (spec.FollowRedirects && response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                        && response.Headers.Location is { } location)
                    {
                        if (redirects >= 5) throw new InvalidOperationException("Too many HTTP redirects.");
                        var next = await AllowedUriAsync(new Uri(uri, location).AbsoluteUri, timeout.Token);
                        if (response.StatusCode == HttpStatusCode.SeeOther && method != HttpMethod.Head
                            || response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && method == HttpMethod.Post)
                        {
                            method = HttpMethod.Get;
                            omitBody = true;
                        }
                        if (next.GetLeftPart(UriPartial.Authority) != uri.GetLeftPart(UriPartial.Authority))
                        {
                            if (method != HttpMethod.Get && method != HttpMethod.Head)
                                throw new UnauthorizedAccessException("Cross-origin redirects cannot replay a request body.");
                            stripHeaders = true;
                        }
                        uri = next;
                        continue; // Dispose the previous response/request/client before the next hop.
                    }
                    var source = new JsHttpSource(response, ownedClient, ownedRequest, timeout,
                        spec.ReadIdleTimeoutMs is { } idle ? TimeSpan.FromMilliseconds(idle) : host.Execution.ReadIdleTimeout);
                    response = null;
                    ownedClient = null;
                    ownedRequest = null;
                    transferred = true;
                    if (open)
                    {
                        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                        try { return source.Metadata(invocation.AddSource(source)); }
                        catch { source.Dispose(); throw; }
                    }
                    using (source)
                    {
                        var bytes = await source.ReadBufferAsync(MaxBodyBytes, timeout.Token);
                        if (spec.ResponseType == "base64")
                            return new { statusCode = source.StatusCode, headers = source.Headers, bodyBase64 = Convert.ToBase64String(bytes) };
                        var text = Encoding.UTF8.GetString(bytes);
                        object? body = spec.ResponseType == "json"
                            ? (text.Length == 0 ? null : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 48 })) : text;
                        return new { statusCode = source.StatusCode, headers = source.Headers, body, bodyText = text };
                    }
                }
                finally
                {
                    response?.Dispose();
                    ownedRequest?.Dispose();
                    ownedClient?.Dispose();
                }
            }
        }
        finally { if (!transferred) timeout.Dispose(); }
    }

    private static HttpRequestMessage BuildRequest(RequestOptions spec, Uri uri, HttpMethod method, string? original, bool omitBody, bool stripHeaders)
    {
        var request = new HttpRequestMessage(method, uri);
        try
        {
            if (!omitBody)
            {
                if (spec.BodyBase64 is { } base64)
                {
                    var bytes = Convert.FromBase64String(base64);
                    if (bytes.Length > MaxBodyBytes) throw new InvalidOperationException("HTTP request body is too large.");
                    request.Content = new ByteArrayContent(bytes);
                    request.Content.Headers.ContentType = new(spec.ContentType ?? "application/octet-stream");
                }
                else if (spec.Form is { } form)
                {
                    request.Content = new FormUrlEncodedContent(form);
                    if (request.Content.Headers.ContentLength > MaxBodyBytes) throw new InvalidOperationException("HTTP request body is too large.");
                }
                else if ((original ?? spec.Body?.GetRawText() ?? spec.BodyText) is { } body)
                {
                    if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes) throw new InvalidOperationException("HTTP request body is too large.");
                    request.Content = new StringContent(body, Encoding.UTF8, spec.ContentType ?? (original is not null || spec.Body is not null ? "application/json" : "text/plain"));
                }
            }
            foreach (var (name, value) in spec.Headers)
            {
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
                    || value.Any(char.IsControl))
                    throw new UnauthorizedAccessException("Restricted HTTP header.");
                if (stripHeaders && !name.Equals("Accept", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase) && !name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    if (request.Content is null && omitBody) continue;
                    if (request.Content is null) throw new InvalidOperationException("Invalid HTTP content header.");
                    request.Content.Headers.Remove(name);
                    if (!request.Content.Headers.TryAddWithoutValidation(name, value)) throw new InvalidOperationException("Invalid HTTP header.");
                }
            }
            return request;
        }
        catch { request.Dispose(); throw; }
    }

    private static string OriginalJson(OriginalJsonOptions options, JsInvocation invocation)
    {
        if (options.Source != invocation.OriginalBodyHandle || invocation.OriginalBody is not { } source)
            throw new UnauthorizedAccessException("Unknown original request handle.");
        var body = JsonNode.Parse(source.GetRawText()) as JsonObject ?? throw new InvalidOperationException("Original request must be an object.");
        if (options.Remove.Length + options.Set.Count > 128) throw new InvalidOperationException("Too many original JSON edits.");
        foreach (var key in options.Remove) body.Remove(key);
        foreach (var (key, value) in options.Set) body[key] = JsonNode.Parse(value.GetRawText());
        return body.ToJsonString(); // JsonElement-backed untouched numbers keep their original precision.
    }
    private void RequireRoute(string route)
    {
        if (!manifest.Permissions.Http.Routes.Contains(route, StringComparer.Ordinal)) throw new UnauthorizedAccessException("HTTP route is not permitted.");
    }
    private async Task<Uri> AllowedUriAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new UnauthorizedAccessException("Invalid HTTP destination.");
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (manifest.Permissions.Http.Origins.Any(allowed => new Uri(allowed).GetLeftPart(UriPartial.Authority).Equals(origin, StringComparison.OrdinalIgnoreCase)))
            return uri;
        if (manifest.Permissions.Http.ManageOrigins && (await host.Http.GetApprovedOriginsAsync(token)).Contains(origin, StringComparer.OrdinalIgnoreCase))
            return uri;
        throw new UnauthorizedAccessException("HTTP destination is not permitted.");
    }
    private static string Handle(JsonObject input) => input["handle"]?.GetValue<string>() ?? "";

    private sealed record ClientOptions
    {
        public string Route { get; init; } = "pool";
        public string[]? SubscriptionIds { get; init; }
        public bool AllowDirectFallback { get; init; }
    }
    private sealed record RequestOptions
    {
        public string Method { get; init; } = "GET";
        public string Url { get; init; } = "";
        public string? Route { get; init; }
        public string? Client { get; init; }
        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public JsonElement? Body { get; init; }
        public string? BodyText { get; init; }
        public string? BodyBase64 { get; init; }
        public Dictionary<string, string>? Form { get; init; }
        public string? ContentType { get; init; }
        public OriginalJsonOptions? OriginalJson { get; init; }
        public string ResponseType { get; init; } = "json";
        public int TimeoutMs { get; init; } = 30_000;
        public int? ReadIdleTimeoutMs { get; init; }
        public string[]? SubscriptionIds { get; init; }
        public bool AllowDirectFallback { get; init; }
        public bool FollowRedirects { get; init; }
        public RetryOptions? Retry { get; init; }
    }
    private sealed record OriginalJsonOptions
    {
        public string Source { get; init; } = "";
        public string[] Remove { get; init; } = [];
        public Dictionary<string, JsonElement> Set { get; init; } = new(StringComparer.Ordinal);
    }
    private sealed record RetryOptions
    {
        public int MaxRetries { get; init; }
        public int DelayMs { get; init; } = 200;
        public bool AllowUnsafeMethods { get; init; }
    }
}
