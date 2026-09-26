using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Net.Http.Headers;
using Acornima.Ast;
using Jint;
using Jint.Runtime;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;
using Router.Infrastructure.Services;
using static Router.Host.Plugins.JavaScript.JsPluginCodec;

namespace Router.Host.Plugins.JavaScript;

/// <summary>Jint 终端：有界调用、宿主管理的 SSE/raw 流、模型、管理端点及 Cron 任务。</summary>
internal sealed partial class JsPlatformTerminal : IPlatformTerminal, IPluginModule, IPluginScheduledTaskProvider, IDisposable, IAsyncDisposable
{
    private static readonly SemaphoreSlim GlobalTerminalSlots = new(48);
    private static readonly SemaphoreSlim GlobalControlSlots = new(12);
    private static readonly SemaphoreSlim GlobalTaskSlots = new(4);
    private static readonly SemaphoreSlim GlobalJobSlots = new(4);
    private static readonly SemaphoreSlim GlobalAuxiliarySlots = new(8);
    private readonly SemaphoreSlim _terminalSlots = new(16);
    private readonly SemaphoreSlim _controlSlots = new(2);
    private readonly SemaphoreSlim _taskSlots = new(1);
    private readonly SemaphoreSlim _jobSlots = new(1);
    private readonly SemaphoreSlim _auxiliarySlots = new(1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifetimeGate = new();
    private int _inFlight;
    private bool _stopping;
    private bool _started;
    private Task? _stopTask;
    private readonly IPluginServices _host;
    private readonly JsCapabilityDispatcher _capabilities;
    private readonly Prepared<Module> _program;
    private int _waiting;
    private bool _closed;

    internal JsPluginManifest Manifest { get; }
    internal PluginMainPage? MainPage { get; private set; }

    internal JsPlatformTerminal(JsPluginPackage package, IPluginHost host)
    {
        _host = host.Services;
        Manifest = package.Manifest;
        if (!_host.PluginKey.Equals(Manifest.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("JS manifest and bound host PluginKey must match.");
        _capabilities = new JsCapabilityDispatcher(Manifest, _host, CallAsync);
        _program = Engine.PrepareModule(package.Source, source: $"{Manifest.Id}/{Manifest.Entry}");
        MainPage = package.Page is null ? null
            : new PluginMainPage(Manifest.Page?.Title ?? Manifest.Name, package.Page, Manifest.Version);
    }

    public IReadOnlyList<ScheduledTaskRegistration> ScheduledTasks => Manifest.Tasks
        .Select(task => new ScheduledTaskRegistration(task.Name, task.Cron,
            async context =>
            {
                await CallAsync(task.Handler,
                    new JsCallContext(JsCallKind.Task, TimeSpan.FromSeconds(task.TimeoutSeconds),
                        TaskName: task.Name, Token: context.CancellationToken), null);
            },
            task.Description))
        .ToArray();

    internal async Task ValidateAsync(CancellationToken cancellationToken)
    {
        await CallAsync(null, new JsCallContext(JsCallKind.Control, TimeSpan.FromSeconds(5), Token: cancellationToken), null);
        if (Manifest.Hooks.GetMainPage is { } handler)
        {
            var page = (await CallAsync(handler, new JsCallContext(JsCallKind.Control, TimeSpan.FromSeconds(5), Token: cancellationToken), null))
                ?.Deserialize<PluginMainPage>(JsonOptions) ?? throw new InvalidOperationException("getMainPage must return title, html and version.");
            if (string.IsNullOrWhiteSpace(page.Title) || Encoding.UTF8.GetByteCount(page.Html) > 1024 * 1024)
                throw new InvalidOperationException("Invalid plugin page.");
            MainPage = page;
        }
    }

    public async Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(ModelQueryContext context, CancellationToken cancellationToken)
    {
        if (Manifest.Hooks.GetModels is null) return [];
        var result = await CallAsync(Manifest.Hooks.GetModels,
            new JsCallContext(JsCallKind.Control, TimeSpan.FromSeconds(25), Token: cancellationToken),
            new { forceRefresh = context.ForceRefresh, requestedModel = context.RequestedModel });
        var models = result?.Deserialize<ModelDescriptor[]>(JsonOptions)
            ?? throw new InvalidOperationException("JS getModels must return an array.");
        if (models.Length > 2000 || models.Any(model => string.IsNullOrWhiteSpace(model.Id) || model.Id.Length > 256))
            throw new InvalidOperationException("Invalid JS model list.");
        return models;
    }

    public async Task<CredentialValidationResult> ValidateCredentialAsync(Credential credential, CancellationToken cancellationToken)
    {
        if (!Manifest.Platform.CredentialKinds.Contains(credential.Kind.ToString(), StringComparer.Ordinal))
            return new CredentialValidationResult(false, "Credential kind is not supported by this plugin.");
        if (Manifest.Hooks.ValidateCredential is null) return new CredentialValidationResult(true);
        var input = JsonSerializer.SerializeToNode(credential, credential.GetType(), JsonOptions);
        var result = await CallAsync(Manifest.Hooks.ValidateCredential,
            new JsCallContext(JsCallKind.Control, TimeSpan.FromSeconds(10), Token: cancellationToken, SecretCredential: credential), input);
        return result?.Deserialize<CredentialValidationResult>(JsonOptions)
            ?? new CredentialValidationResult(false, "JS credential validation returned no result.");
    }

    public async Task<PluginInvocationResult> InvokeAsync(PluginAttemptContext context)
    {
        JsInvocation? invocation = null;
        try
        {
            invocation = await OpenInvocationAsync(
                new JsCallContext(JsCallKind.Terminal, _host.Execution.SetupTimeout, Attempt: context, Token: context.CancellationToken));
            var result = await invocation.CallAsync(Manifest.Hooks.Invoke, null);
            var response = result?["response"] as JsonObject ?? throw new InvalidOperationException("JS invoke must return response and attempt.");
            var attempt = result?["attempt"]?.Deserialize<PluginAttemptResult>(AttemptJsonOptions)
                ?? new PluginAttemptResult(PluginAttemptOutcome.NoPenalty);
            if (!Enum.IsDefined(attempt.Outcome)) throw new InvalidOperationException("Unknown JS attempt outcome.");
            var decision = attempt.Decision ?? PreviewDecision(attempt);
            decision.Validate();
            attempt = decision.ToResult(attempt.StatusCode, attempt.Reason);
            // Only native HTTP failures can establish a transport fault; arbitrary script flags cannot punish a proxy.
            attempt = attempt with { IsTransportFailure = false };
            var status = response["statusCode"]?.GetValue<int>() ?? 200;
            if (status is < 200 or > 599) throw new InvalidOperationException("Invalid response status.");
            if (response["kind"]?.GetValue<string>() == "error")
            {
                if (status < 400) throw new InvalidOperationException("JS error responses need a 4xx/5xx status.");
                return new(new AdapterResponse
                {
                    StatusCode = status,
                    Error = response["message"]?.GetValue<string>() ?? "JS plugin failed.",
                    ErrorType = response["errorType"]?.GetValue<string>() ?? "upstream_error"
                }, attempt);
            }
            var kind = response["kind"]?.GetValue<string>();
            if (kind == "raw" && response["source"] is null)
            {
                var bytes = response["bodyBase64"] is { } binary ? Convert.FromBase64String(binary.GetValue<string>())
                    : Encoding.UTF8.GetBytes(response["bodyText"]?.GetValue<string>() ?? "");
                var contentType = response["contentType"]?.GetValue<string>() ?? "application/octet-stream";
                if (bytes.Length > 32 * 1024 * 1024 || !MediaTypeHeaderValue.TryParse(contentType, out _))
                    throw new InvalidOperationException("Invalid raw response body or content type.");
                var usage = response["usage"]?.Deserialize<Usage>(JsonOptions);
                ValidateUsage(usage);
                return new(new AdapterResponse { StatusCode = status, IsRawPassthrough = true,
                    RawContent = bytes, ContentType = contentType, Usage = usage }, attempt);
            }
            if (kind is "mappedStream" or "raw")
            {
                var source = invocation.BeginStream(response["source"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Missing HTTP source handle."));
                if (kind == "raw")
                {
                    if (context.Request.Stream || source.ContentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
                    {
                        var raw = PluginResponseLifetime.Ensure(new AdapterResponse
                        {
                            StatusCode = source.StatusCode, IsStreaming = true, IsRawPassthrough = true,
                            ContentType = source.ContentType,
                            RawStream = ReadJsRawAsync(invocation, source),
                            Lifetime = invocation
                        });
                        invocation = null;
                        return new(raw, attempt);
                    }
                    var bytes = await source.ReadBufferAsync(32 * 1024 * 1024, invocation.Token);
                    return new(new AdapterResponse
                    {
                        StatusCode = source.StatusCode, IsRawPassthrough = true, RawContent = bytes, ContentType = source.ContentType
                    }, attempt);
                }
                if (status >= 300 || source.StatusCode >= 300
                    || !Manifest.StreamMappers.TryGetValue(response["mapper"]?.GetValue<string>() ?? "", out var mapper))
                    throw new InvalidOperationException("Invalid mapped stream response or mapper name.");
                var stream = ReadMappedStreamAsync(invocation, source, mapper, response["state"]?.DeepClone());
                if (context.Request.Stream)
                {
                    var mapped = PluginResponseLifetime.Ensure(new AdapterResponse
                    {
                        StatusCode = status, IsStreaming = true, Stream = stream, Lifetime = invocation
                    });
                    invocation = null;
                    return new(mapped, attempt);
                }
                var aggregate = await AggregateAsync(stream, context.Request.Model, invocation.Token);
                if (mapper.Completion is { } finalize)
                    aggregate = invocation.FinalizeCompletion(finalize, aggregate)?.Deserialize<AdapterCompletion>(JsonOptions)
                        ?? throw new InvalidOperationException("Completion mapper returned an invalid result.");
                ValidateCompletion(aggregate);
                return new(new AdapterResponse { StatusCode = status, Completion = aggregate }, attempt);
            }
            if (kind != "completion" || status >= 300)
                throw new InvalidOperationException("Expected a completion, mappedStream, raw or error response.");
            var completion = response["completion"]?.Deserialize<AdapterCompletion>(JsonOptions)
                ?? throw new InvalidOperationException("Missing JS completion.");
            completion = completion with { Model = string.IsNullOrWhiteSpace(completion.Model) ? context.Request.Model : completion.Model };
            ValidateCompletion(completion);
            if (context.Request.Stream)
                return new(PluginResponseLifetime.Ensure(new AdapterResponse
                {
                    StatusCode = status, IsStreaming = true, Stream = CompletionChunks(completion)
                }), attempt);
            return new(new AdapterResponse
            {
                StatusCode = status,
                Completion = completion
            }, attempt);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
        catch (JsPluginBusyException exception) { return Failure(503, exception.Message, "plugin_busy"); }
        catch (Exception exception)
        {
            try
            {
                await _host.LogAsync(Manifest.Platform.Name, "js.invoke.failed", invocation?.Redact(exception.Message) ?? SafeError(exception, context.Account),
                    "Error", context.TraceId, cancellationToken: CancellationToken.None);
            }
            catch { /* Logging cannot turn a script failure into a resource/transport failure. */ }
            return Failure(502, invocation?.Redact(exception.Message) ?? SafeError(exception, context.Account), "plugin_execution_error");
        }
        finally
        {
            if (invocation is not null) await invocation.DisposeAsync();
        }
    }

    internal async Task<PluginResult> InvokeEndpointAsync(string handler, PluginHttpContext context)
    {
        try
        {
            var result = await CallAsync(handler,
                new JsCallContext(JsCallKind.Control, TimeSpan.FromSeconds(25), Endpoint: context, Token: context.CancellationToken), null);
            var value = result?.Deserialize<PluginResult>(JsonOptions)
                ?? throw new InvalidOperationException("JS endpoint must return a JSON result.");
            if (value.StatusCode is < 200 or > 599 || value.ContentType is not (null or "application/json"))
                throw new InvalidOperationException("JS endpoints only return JSON/204 with a valid HTTP status.");
            return value;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
        catch (JsPluginBusyException exception) { return context.Json(503, new { error = exception.Message, code = "plugin_busy" }); }
        catch (Exception exception) { return context.Json(502, new { error = SafeError(exception, null), code = "plugin_execution_error" }); }
    }

    public void Configure(IPluginBuilder builder)
    {
        builder.AccountPolicy(policy =>
        {
            policy.SelectAccount(account => Manifest.Platform.CredentialKinds.Contains(account.Credential.Kind.ToString(), StringComparer.Ordinal));
            if (Manifest.Hooks.SelectAccounts is not null) policy.SelectBatch(SelectAccountsAsync);
        });
        builder.ProxyPolicy(policy => policy
            .MaxAttempts(Manifest.Policy.MaxAttempts)
            .AttemptTimeoutSeconds(Manifest.Policy.AttemptTimeoutSeconds)
            .TotalTimeoutSeconds(Manifest.Policy.TotalTimeoutSeconds)
            .OnTransportFailure(Manifest.Policy.TransportFailure.Decision));
        foreach (var task in Manifest.Tasks)
            builder.Job(new PluginJobRegistration(task.Name, Manifest.Platform.Name, async context =>
            {
                // Manual execution of a Cron task must use the same native task lock as the scheduler.
                var succeeded = await _host.Tasks.RunAsync(task.Name, Manifest.Platform.Name, context.CancellationToken);
                context.CancellationToken.ThrowIfCancellationRequested();
                if (!succeeded) throw new InvalidOperationException("Scheduled task failed, is already running, or its task lock is unavailable.");
                return JsonSerializer.SerializeToElement(new { succeeded });
            }, TimeSpan.FromSeconds(task.TimeoutSeconds), task.Description));
        foreach (var job in Manifest.Jobs)
            builder.Job(Job(job.Name, job.Handler, job.TimeoutSeconds, job.Description));
    }

    private PluginJobRegistration Job(string name, string handler, int seconds, string? description)
        => new(name, Manifest.Platform.Name, async context =>
        {
            var value = await CallAsync(handler, new JsCallContext(JsCallKind.Job, TimeSpan.FromSeconds(seconds),
                TaskName: name, Token: context.CancellationToken, Job: context), context.Input);
            return value is null ? null : JsonSerializer.SerializeToElement(value, JsonOptions);
        }, TimeSpan.FromSeconds(seconds), description);

    private async Task<IReadOnlyList<PluginAccountPreference>> SelectAccountsAsync(IReadOnlyList<Account> accounts, AdapterRequest request, CancellationToken token)
    {
        await using var invocation = await OpenInvocationAsync(new JsCallContext(JsCallKind.Selection, TimeSpan.FromSeconds(5),
            Token: token, SelectionRequest: request));
        var includeCredentials = Manifest.Permissions.Accounts.Contains("readCredentials", StringComparer.Ordinal);
        if (includeCredentials) foreach (var account in accounts) invocation.RememberCredential(account.Credential);
        var result = await invocation.CallAsync(Manifest.Hooks.SelectAccounts, new
        {
            candidates = accounts.Select(account => AccountMetadata(account, includeCredentials)).ToArray()
        });
        return result?.Deserialize<PluginAccountPreference[]>(AttemptJsonOptions) ?? throw new InvalidOperationException("selectAccounts must return a preference array.");
    }

    private static PluginAttemptDecision PreviewDecision(PluginAttemptResult attempt) => new()
    {
        FailureKind = attempt.Outcome == PluginAttemptOutcome.Healthy ? PluginFailureKind.None : PluginFailureKind.Upstream,
        Retry = attempt.Outcome is PluginAttemptOutcome.Retry or PluginAttemptOutcome.CooldownAccount or PluginAttemptOutcome.CooldownNode
            ? PluginRetryAction.NextAttempt : PluginRetryAction.None,
        AccountAction = attempt.Outcome == PluginAttemptOutcome.DisableAccount ? PluginAccountAction.Disable
            : attempt.Outcome == PluginAttemptOutcome.CooldownAccount ? PluginAccountAction.Cooldown : PluginAccountAction.None,
        AccountCooldownUntil = attempt.Outcome == PluginAttemptOutcome.CooldownAccount ? DateTimeOffset.UtcNow.AddMinutes(5) : null,
        // The native executor still requires actual evidence before executing this request.
        ProxyAction = attempt.Outcome == PluginAttemptOutcome.CooldownNode ? PluginProxyAction.Cooldown : PluginProxyAction.None,
        ReasonCode = "js.preview"
    };

    public async ValueTask StartAsync(PluginStartContext context, CancellationToken cancellationToken)
    {
        _started = true;
        if (Manifest.Hooks.Start is { } handler)
            await CallAsync(handler, new JsCallContext(JsCallKind.Control, TimeSpan.FromSeconds(10), Starting: true, Token: cancellationToken), null);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifetimeGate)
        {
            _stopping = true;
            return new ValueTask(_stopTask ??= StopCoreAsync());
        }
    }

    private async Task StopCoreAsync()
    {
        try
        {
            await _shutdown.CancelAsync();
            while (Volatile.Read(ref _inFlight) > 0) await Task.Delay(10);
            if (_started && Manifest.Hooks.Stop is { } handler)
                await CallAsync(handler, new JsCallContext(JsCallKind.Stop, TimeSpan.FromSeconds(5)), null);
        }
        finally
        {
            _closed = true;
            _terminalSlots.Dispose();
            _controlSlots.Dispose();
            _taskSlots.Dispose();
            _jobSlots.Dispose();
            _auxiliarySlots.Dispose();
            _shutdown.Dispose();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);

    private async Task<JsonNode?> CallAsync(string? handler, JsCallContext context, object? input)
    {
        await using var invocation = await OpenInvocationAsync(context);
        try { return await invocation.CallAsync(handler, input); }
        catch (OperationCanceledException) when (context.Token.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw new InvalidOperationException(invocation.Redact(exception.Message)); }
    }

    private static PluginInvocationResult Failure(int status, string error, string type)
        => new(new AdapterResponse { StatusCode = status, Error = error, ErrorType = type },
            new PluginAttemptDecision { FailureKind = PluginFailureKind.Plugin, ReasonCode = type }.ToResult(status, type));

    private async Task<JsInvocation> OpenInvocationAsync(JsCallContext context)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_closed || _stopping && context.Kind != JsCallKind.Stop, this);
            Interlocked.Increment(ref _inFlight);
        }
        var ownerToken = context.Kind == JsCallKind.Stop ? CancellationToken.None : _shutdown.Token;
        var local = context.Kind switch
        {
            JsCallKind.Terminal => _terminalSlots, JsCallKind.Task => _taskSlots, JsCallKind.Job => _jobSlots,
            JsCallKind.Selection or JsCallKind.Callback => _auxiliarySlots, _ => _controlSlots
        };
        var global = context.Kind switch
        {
            JsCallKind.Terminal => GlobalTerminalSlots, JsCallKind.Task => GlobalTaskSlots, JsCallKind.Job => GlobalJobSlots,
            JsCallKind.Selection or JsCallKind.Callback => GlobalAuxiliarySlots, _ => GlobalControlSlots
        };
        var localTaken = false;
        var globalTaken = false;
        var owned = false;
        if (Interlocked.Increment(ref _waiting) > 32)
        {
            Interlocked.Decrement(ref _waiting);
            Interlocked.Decrement(ref _inFlight);
            throw new JsPluginBusyException();
        }
        try
        {
            using var queue = CancellationTokenSource.CreateLinkedTokenSource(context.Token, ownerToken);
            queue.CancelAfter(_host.Execution.QueueTimeout);
            try
            {
                await local.WaitAsync(queue.Token);
                localTaken = true;
                await global.WaitAsync(queue.Token);
                globalTaken = true;
            }
            catch (OperationCanceledException) when (!context.Token.IsCancellationRequested)
            {
                throw new JsPluginBusyException();
            }
            finally { Interlocked.Decrement(ref _waiting); }

            var invocation = new JsInvocation(_program, Manifest, _capabilities, _host.Execution, context, local, global,
                () => Interlocked.Decrement(ref _inFlight), ownerToken);
            owned = true;
            localTaken = globalTaken = false;
            try
            {
                await invocation.InitializeAsync();
                return invocation;
            }
            catch { await invocation.DisposeAsync(); throw; }
        }
        finally
        {
            if (globalTaken) global.Release();
            if (localTaken) local.Release();
            if (!owned) Interlocked.Decrement(ref _inFlight);
        }
    }

    private sealed class JsPluginBusyException() : Exception("JS plugin is busy; try again later.");
}
