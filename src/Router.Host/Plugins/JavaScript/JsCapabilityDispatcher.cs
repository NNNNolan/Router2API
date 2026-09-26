using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using static Router.Host.Plugins.JavaScript.JsPluginCodec;

namespace Router.Host.Plugins.JavaScript;

/// <summary>每次调用的能力分发与授权，不把宿主服务、Exception 或取消源暴露给引擎。</summary>
internal sealed class JsCapabilityDispatcher(JsPluginManifest manifest, IPluginServices services,
    Func<string?, JsCallContext, object?, Task<JsonNode?>> invoke)
{
    private readonly JsAccountCapabilities _accounts = new(manifest, services, invoke);
    private readonly JsHttpCapabilities _http = new(manifest, services);

    public async Task<string> DispatchAsync(string operation, string json, JsInvocation invocation, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var input = Input(json, 8 * 1024 * 1024);
            if (invocation.Context.Kind == JsCallKind.Selection && operation is not ("state.get" or "state.getString" or "state.expiry" or "state.available"))
                throw new UnauthorizedAccessException("Account selection may only read state; HTTP, account mutation and task creation are forbidden.");
            object? value;
            if (operation.StartsWith("http.", StringComparison.Ordinal))
                value = await _http.DispatchAsync(operation, input, invocation, token);
            else if (operation.StartsWith("state.", StringComparison.Ordinal))
                value = await StateAsync(operation, input, token);
            else if (operation.StartsWith("jobs.", StringComparison.Ordinal))
                value = await JobsAsync(operation, input, invocation, token);
            else if (operation.StartsWith("accounts.", StringComparison.Ordinal)
                && operation is not ("accounts.ensureAnonymous" or "accounts.currentCredential"))
                value = await _accounts.DispatchAsync(operation, input, invocation, token);
            else
                value = await CommonAsync(operation, input, invocation, token);
            var result = JsonSerializer.Serialize(new { ok = true, value }, JsonOptions);
            if (Encoding.UTF8.GetByteCount(result) > 16 * 1024 * 1024) throw new InvalidOperationException("Host result exceeds the bridge limit.");
            return result;
        }
        catch (Exception exception) { return Error(exception, invocation); }
    }

    public string DispatchSync(string operation, string json, JsInvocation invocation)
    {
        try
        {
            invocation.Token.ThrowIfCancellationRequested();
            var permission = operation switch
            {
                "crypto.randomUUID" => "random", "crypto.hash" => "hash", "crypto.hmac" => "hmac",
                _ when operation.StartsWith("encoding.", StringComparison.Ordinal) => "encoding",
                _ when operation.StartsWith("decimal.", StringComparison.Ordinal) => "decimal",
                _ when operation.StartsWith("url.", StringComparison.Ordinal) => null,
                _ => throw new UnauthorizedAccessException("Unknown synchronous utility.")
            };
            if (permission is not null && !manifest.Permissions.Crypto.Contains(permission, StringComparer.Ordinal))
                throw new UnauthorizedAccessException("Utility capability is not permitted.");
            return JsonSerializer.Serialize(new { ok = true, value = JsUtilities.Execute(operation, Input(json, 1024 * 1024)) }, JsonOptions);
        }
        catch (Exception exception) { return Error(exception, invocation); }
    }

    private async Task<object?> CommonAsync(string operation, JsonObject input, JsInvocation invocation, CancellationToken token)
    {
        var context = invocation.Context;
        switch (operation)
        {
            case "delay":
                var milliseconds = input["milliseconds"]?.GetValue<int>() ?? 0;
                if (milliseconds is < 0 or > 300_000) throw new InvalidOperationException("delay must be 0–300000ms.");
                await Task.Delay(milliseconds, token);
                return null;
            case "accounts.ensureAnonymous":
                if (!manifest.Permissions.CreateAnonymousAccount || !context.Starting)
                    throw new UnauthorizedAccessException("Anonymous account creation requires an authorized start hook.");
                var id = manifest.PackagePlatforms is { Count: > 1 } ? $"{manifest.Id}:{manifest.Platform.Name}:anonymous" : $"{manifest.Id}:anonymous";
                var account = await services.Accounts.GetAsync(id, token) ?? await services.Accounts.SaveAsync(new Account
                {
                    Id = id, PluginKey = manifest.Id, Platform = manifest.Platform.Name,
                    Label = $"{manifest.Name} anonymous", Credential = new ApiKeyCredential("public")
                }, token);
                return AccountMetadata(account);
            case "accounts.currentCredential":
                if ((!manifest.Permissions.ReadCurrentCredential && !manifest.Permissions.Accounts.Contains("readCredentials", StringComparer.Ordinal))
                    || context.Attempt is null)
                    throw new UnauthorizedAccessException("Reading this credential is not permitted.");
                var current = await services.Accounts.GetAsync(context.Attempt.Account.Id, token) ?? context.Attempt.Account;
                invocation.RememberCredential(current.Credential);
                return CredentialJson(current.Credential);
            case "models.list":
            case "models.metadata":
            case "models.refresh":
            case "models.invalidate":
                var permission = operation == "models.refresh" ? "refresh" : operation == "models.invalidate" ? "invalidate" : "read";
                if (!manifest.Permissions.Models.Contains(permission, StringComparer.Ordinal))
                    throw new UnauthorizedAccessException("Model capability is not permitted.");
                var platform = input["platform"]?.GetValue<string>() ?? manifest.Platform.Name;
                if (operation == "models.metadata") return await services.Models.GetMetadataAsync(token);
                if (operation == "models.list") return await services.Models.ListAsync(platform, token);
                if (operation == "models.refresh") return await services.Models.RefreshAsync(platform, token);
                services.Models.Invalidate(platform);
                return null;
            case "tasks.run":
                var taskName = input["name"]?.GetValue<string>() ?? "";
                if (!manifest.Permissions.Tasks.Contains("run", StringComparer.Ordinal)
                    || context.Kind is JsCallKind.Task or JsCallKind.Job || !manifest.Tasks.Any(task => task.Name == taskName))
                    throw new UnauthorizedAccessException("Only declared tasks can run; use jobs.start for detached work, not nested tasks.run.");
                return await services.Tasks.RunAsync(taskName, manifest.Platform.Name, token);
            case "tasks.writeLog":
                if (!manifest.Permissions.Tasks.Contains("writeLog", StringComparer.Ordinal))
                    throw new UnauthorizedAccessException("Task detail logging is not permitted.");
                var entry = input.Deserialize<TaskLogInput>(AttemptJsonOptions) ?? throw new InvalidOperationException("Invalid task log.");
                if (entry.TaskName.Length is 0 or > 128 || entry.DurationMs < 0) throw new InvalidOperationException("Invalid task log.");
                await services.Tasks.WriteLogAsync(new TaskLog
                {
                    PluginKey = manifest.Id, Platform = manifest.Platform.Name, TaskName = entry.TaskName, AccountId = entry.AccountId,
                    Status = entry.Status, Message = entry.Message is null ? null : invocation.Redact(entry.Message),
                    Error = entry.Error is null ? null : invocation.Redact(entry.Error), DetailsJson = SafeDetails(entry.Details, invocation),
                    DurationMs = entry.DurationMs, StartedAt = entry.StartedAt ?? DateTimeOffset.UtcNow,
                    FinishedAt = entry.FinishedAt ?? DateTimeOffset.UtcNow
                }, token);
                return null;
            case "log.write":
                var log = input.Deserialize<LogInput>(AttemptJsonOptions) ?? throw new InvalidOperationException("Invalid log.");
                if (log.Message.Length > 2000 || log.Level is not ("Debug" or "Information" or "Warning" or "Error")
                    || log.EventType.Length > 128 || log.DurationMs < 0 || log.StatusCode is < 100 or > 599)
                    throw new InvalidOperationException("Invalid log entry.");
                await services.Log.WriteAsync(new PluginLog
                {
                    PluginKey = manifest.Id, Platform = manifest.Platform.Name, EventType = log.EventType, Level = log.Level,
                    Message = invocation.Redact(log.Message), TraceId = context.Attempt?.TraceId,
                    TaskName = context.TaskName, AccountId = log.AccountId ?? context.Attempt?.Account.Id, Model = log.Model ?? context.Attempt?.Request.Model,
                    StatusCode = log.StatusCode, DurationMs = log.DurationMs, DetailsJson = SafeDetails(log.Details, invocation)
                }, token);
                return null;
            default: throw new UnauthorizedAccessException("Unknown host capability.");
        }
    }

    private async Task<object?> StateAsync(string operation, JsonObject input, CancellationToken token)
    {
        var scope = input["scope"]?.GetValue<string>() ?? "local";
        if (scope is not ("local" or "shared")) throw new InvalidOperationException("Unknown state scope.");
        var permissions = scope == "shared" ? manifest.Permissions.SharedState : manifest.Permissions.State;
        var read = operation is "state.get" or "state.getString" or "state.expiry" or "state.available";
        if (!permissions.Contains(read ? "read" : "write", StringComparer.Ordinal))
            throw new UnauthorizedAccessException("State capability is not permitted.");
        var store = scope == "shared" ? services.State.Shared : services.State.Local;
        if (operation == "state.available") return store.IsAvailable;
        var key = input["key"]?.GetValue<string>() ?? "";
        if (operation == "state.getString") return await store.GetStringAsync(key, token);
        if (operation == "state.get")
        {
            var json = await store.GetStringAsync(key, token);
            return json is null ? null : JsonNode.Parse(json);
        }
        if (operation == "state.expiry") return await store.GetExpiryAsync(key, token);
        if (operation == "state.remove") { await store.RemoveAsync(key, token); return null; }
        var ttl = TimeSpan.FromSeconds(input["ttlSeconds"]?.GetValue<int>() ?? 300);
        if (operation == "state.setString") return await store.SetStringAsync(key, input["value"]!.GetValue<string>(), ttl, token);
        var value = input["value"]?.ToJsonString(JsonOptions) ?? "null";
        return operation switch
        {
            "state.set" => await store.SetStringAsync(key, value, ttl, token),
            "state.putIfAbsent" => await store.PutIfAbsentAsync(key, value, ttl, token),
            "state.increment" => (await store.IncrementAsync(key,
                long.Parse(input["delta"]?.GetValue<string>() ?? "1", CultureInfo.InvariantCulture), ttl, token)).ToString(CultureInfo.InvariantCulture),
            "state.compareExchange" => await store.CompareExchangeAsync(key,
                input["missing"]?.GetValue<bool>() == true ? null : input["expected"]?.ToJsonString(JsonOptions) ?? "null",
                input["remove"]?.GetValue<bool>() == true ? null : value, ttl, token),
            _ => throw new UnauthorizedAccessException("Unknown state operation.")
        };
    }

    private async Task<object?> JobsAsync(string operation, JsonObject input, JsInvocation invocation, CancellationToken token)
    {
        if (operation == "jobs.progress")
        {
            if (invocation.Context.Job is not { } job) throw new UnauthorizedAccessException("Progress is only available inside a job.");
            job.ReportProgress(input["value"] is { } progress ? JsonSerializer.SerializeToElement(progress, JsonOptions) : null);
            return null;
        }
        var permission = operation == "jobs.start" ? "start" : operation == "jobs.cancel" ? "cancel" : "read";
        if (!manifest.Permissions.Jobs.Contains(permission, StringComparer.Ordinal)) throw new UnauthorizedAccessException("Job capability is not permitted.");
        var id = input["id"]?.GetValue<string>() ?? "";
        return operation switch
        {
            "jobs.start" => await services.Jobs.StartAsync(input["name"]?.GetValue<string>() ?? "",
                input["input"] is { } payload ? JsonSerializer.SerializeToElement(payload, JsonOptions) : null,
                new PluginJobOptions { Key = input["key"]?.GetValue<string>(), Platform = input["platform"]?.GetValue<string>() ?? manifest.Platform.Name }, token),
            "jobs.get" => services.Jobs.Get(id),
            "jobs.list" => services.Jobs.List(),
            "jobs.cancel" => await services.Jobs.CancelAsync(id, token),
            "jobs.wait" when invocation.Context.Kind != JsCallKind.Job => await services.Jobs.WaitAsync(id, token),
            _ => throw new UnauthorizedAccessException("Unknown job operation or nested job wait.")
        };
    }

    private static JsonObject Input(string json, int limit)
    {
        if (Encoding.UTF8.GetByteCount(json) > limit) throw new InvalidOperationException("Host input exceeds its quota.");
        return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 48 }) as JsonObject
            ?? throw new InvalidOperationException("Host input must be an object.");
    }
    private static string Error(Exception exception, JsInvocation invocation)
    {
        var code = exception switch
        {
            UnauthorizedAccessException => "host.denied", OperationCanceledException => "host.cancelled",
            ProxyPoolUnavailableException => "host.proxy_pool_unavailable", HttpRequestException => "host.http_error",
            KeyNotFoundException => "host.not_found", _ => "host.invalid_operation"
        };
        var message = exception is HttpRequestException ? "HTTP transport failed." : invocation.Redact(exception.Message);
        return JsonSerializer.Serialize(new { ok = false, error = new { code, message } }, JsonOptions);
    }
    private static string? SafeDetails(JsonNode? details, JsInvocation invocation)
    {
        if (details is null) return null;
        if (Encoding.UTF8.GetByteCount(details.ToJsonString()) > 64 * 1024) throw new InvalidOperationException("Log details exceed their quota.");
        JsonNode? Redact(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.Select(pair => KeyValuePair.Create(pair.Key, Redact(pair.Value)))),
            JsonArray array => new JsonArray(array.Select(Redact).ToArray()),
            JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(invocation.Redact(text)),
            _ => node?.DeepClone()
        };
        return Redact(details)?.ToJsonString(JsonOptions);
    }
    private sealed record LogInput
    {
        public string Message { get; init; } = "";
        public string Level { get; init; } = "Information";
        public string EventType { get; init; } = "js.log";
        public string? AccountId { get; init; }
        public string? Model { get; init; }
        public int? StatusCode { get; init; }
        public int? DurationMs { get; init; }
        public JsonNode? Details { get; init; }
    }
    private sealed record TaskLogInput(string TaskName, string Status, string? AccountId, string? Message, string? Error,
        JsonNode? Details, int DurationMs = 0, DateTimeOffset? StartedAt = null, DateTimeOffset? FinishedAt = null);
}
