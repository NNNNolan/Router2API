using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Constraints;
using Jint.Runtime;
using Acornima.Ast;
using Router.Contracts.Host;
using Router.Contracts.Domain;
using static Router.Host.Plugins.JavaScript.JsPluginCodec;

namespace Router.Host.Plugins.JavaScript;

internal sealed class JsInvocation : IAsyncDisposable
{
    private readonly Prepared<Module> _program;
    private readonly JsPluginManifest _manifest;
    private readonly JsCapabilityDispatcher _capabilities;
    private readonly PluginExecutionOptions _execution;
    private readonly SemaphoreSlim _local;
    private readonly SemaphoreSlim _global;
    private readonly CancellationTokenSource _operation;
    private readonly Engine _engine;
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private readonly OperationDeadlineConstraint _step = new();
    private readonly HashSet<Task<string>> _calls = [];
    private readonly Dictionary<string, JsHttpSource> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (HttpClient Client, string Route)> _clients = new(StringComparer.Ordinal);
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private readonly Action? _onDisposed;
    private readonly object _sourceGate = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _totalCalls;
    private int _syncCalls;
    private bool _mapping;
    private bool _mappingViolation;
    private bool _claimed;
    public JsCallContext Context { get; }
    public CancellationToken Token => _operation.Token;
    public string? OriginalBodyHandle { get; }
    public JsonElement? OriginalBody => Context.Attempt?.Request.OriginalBody;

    public JsInvocation(Prepared<Module> program, JsPluginManifest manifest, JsCapabilityDispatcher capabilities,
        PluginExecutionOptions execution, JsCallContext context, SemaphoreSlim local, SemaphoreSlim global,
        Action? onDisposed = null, CancellationToken ownerToken = default)
    {
        _program = program;
        _manifest = manifest;
        _capabilities = capabilities;
        _execution = execution;
        Context = context;
        _local = local;
        _global = global;
        _onDisposed = onDisposed;
        OriginalBodyHandle = context.Attempt?.Request.OriginalBody is null ? null : Guid.NewGuid().ToString("N");
        if ((context.Attempt?.Account ?? context.CredentialAccount) is { } account) RememberCredential(account.Credential);
        if (context.SecretCredential is not null) RememberCredential(context.SecretCredential);
        _operation = CancellationTokenSource.CreateLinkedTokenSource(context.Token, ownerToken);
        _operation.CancelAfter(context.Timeout);
        _engine = new Engine(options =>
        {
            options.ExperimentalFeatures = ExperimentalFeature.TaskInterop;
            options.DisableStringCompilation();
            options.Interop.AllowGetType = false;
            options.Interop.AllowSystemReflection = false;
            options.Interop.AllowWrite = false;
            options.Constraints.StackOverflowGuard = true;
            options.Constraints.PromiseTimeout = context.Timeout;
            options.TimeoutInterval(context.Timeout);
            options.RegexTimeoutInterval(TimeSpan.FromMilliseconds(100));
            options.MaxStatements(context.Kind is JsCallKind.Task or JsCallKind.Job ? 5_000_000 : 500_000);
            options.LimitMemory(64 * 1024 * 1024);
            options.LimitRecursion(64);
            options.CancellationToken(Token);
            options.Constraint(_step);
        });
    }

    public async Task InitializeAsync()
    {
        await _engineGate.WaitAsync(Token);
        try
        {
            _engine.Modules.Add("plugin", builder => builder.AddModule(in _program));
            var exports = await _engine.Modules.ImportAsync("plugin", Token);
            _engine.SetValue("__plugin", exports);
            _engine.SetValue("__hostCall", new Func<string, string, Task<string>>((name, json) =>
            {
                if (_mapping)
                {
                    _mappingViolation = true;
                    throw new InvalidOperationException("Host operations are not allowed inside stream mappers.");
                }
                _calls.RemoveWhere(task => task.IsCompleted);
                if (_calls.Count >= 8 || ++_totalCalls > (Context.Kind is JsCallKind.Task or JsCallKind.Job ? 8192 : 512))
                    return Task.FromResult("""{"ok":false,"error":{"code":"host.limit","message":"Host call quota exceeded."}}""");
                var task = _capabilities.DispatchAsync(name, json, this, Token);
                _calls.Add(task);
                return task;
            }));
            _engine.SetValue("__hostSync", new Func<string, string, string>((name, json) =>
            {
                if (++_syncCalls > 4096) throw new InvalidOperationException("Synchronous utility quota exceeded.");
                return _capabilities.DispatchSync(name, json, this);
            }));
            _engine.Execute(in Bootstrap);
            _engine.Invoke("__validate", JsonSerializer.Serialize(_manifest.Handlers));
        }
        finally { _engineGate.Release(); }
    }

    public async Task<JsonNode?> CallAsync(string? handler, object? input)
    {
        if (handler is null) return null;
        var metadata = JsonSerializer.Serialize(BuildContext(_manifest, Context, OriginalBodyHandle), JsonOptions);
        var inputJson = JsonSerializer.Serialize(input, JsonOptions);
        if (Encoding.UTF8.GetByteCount(metadata) + Encoding.UTF8.GetByteCount(inputJson) > 16 * 1024 * 1024)
            throw new InvalidOperationException("JS input exceeds the bridge limit.");
        await _engineGate.WaitAsync(Token);
        try
        {
            var result = await _engine.InvokeAsync("__call", Token, handler, metadata, inputJson);
            if (_calls.Any(task => !task.IsCompleted))
                throw new InvalidOperationException("JS handler returned with unawaited host operations.");
            return ParseResult(result.AsString(), 8 * 1024 * 1024);
        }
        catch (ExecutionCanceledException) when (Token.IsCancellationRequested)
        {
            Context.Token.ThrowIfCancellationRequested();
            throw new TimeoutException("JS operation exceeded its deadline.");
        }
        catch (OperationCanceledException) when (!Context.Token.IsCancellationRequested)
        {
            throw new TimeoutException("JS operation exceeded its deadline.");
        }
        finally { _engineGate.Release(); }
    }

    public void RememberCredential(Credential credential)
    {
        var node = CredentialJson(credential);
        lock (_sourceGate)
        {
            foreach (var secret in Secrets(node)) if (!string.IsNullOrEmpty(secret)) _secrets.Add(secret);
        }
    }

    public string Redact(string message)
    {
        lock (_sourceGate)
            foreach (var secret in _secrets.OrderByDescending(value => value.Length))
                message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return message.Length > 2000 ? message[..2000] : message;
    }

    private static IEnumerable<string> Secrets(JsonNode? node)
    {
        if (node is not JsonObject obj) yield break;
        foreach (var (key, value) in obj)
        {
            if (key is "apiKey" or "accessToken" or "refreshToken" or "idToken" or "token" or "password" or "cookie")
            {
                if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) yield return text;
            }
            if (key == "fields" && value is JsonObject fields)
                foreach (var field in fields)
                {
                    if (field.Value is not JsonValue fieldValue || !fieldValue.TryGetValue<string>(out var text)) continue;
                    yield return text;
                    JsonNode? nested = null;
                    try { nested = JsonNode.Parse(text); } catch (JsonException) { }
                    foreach (var inner in Secrets(nested)) yield return inner;
                }
        }
    }

    public string AddClient(HttpClient client, string route)
    {
        lock (_sourceGate)
        {
            if (_clients.Count >= 4) throw new InvalidOperationException("Too many HTTP clients.");
            var handle = Guid.NewGuid().ToString("N");
            _clients.Add(handle, (client, route));
            return handle;
        }
    }
    public HttpClient Client(string handle)
    {
        lock (_sourceGate) return _clients.TryGetValue(handle, out var item) ? item.Client : throw new UnauthorizedAccessException("Unknown HTTP client handle.");
    }
    public string ClientRoute(string handle)
    {
        lock (_sourceGate) return _clients.TryGetValue(handle, out var item) ? item.Route : throw new UnauthorizedAccessException("Unknown HTTP client handle.");
    }
    public void CloseClient(string handle)
    {
        lock (_sourceGate) if (_clients.Remove(handle, out var item)) item.Client.Dispose();
    }

    public string AddSource(JsHttpSource source)
    {
        lock (_sourceGate)
        {
            if (_sources.Count >= 4) throw new InvalidOperationException("Too many open HTTP sources.");
            var handle = Guid.NewGuid().ToString("N");
            _sources.Add(handle, source);
            return handle;
        }
    }

    public JsHttpSource Source(string handle)
    {
        lock (_sourceGate)
            return _sources.TryGetValue(handle, out var source) ? source : throw new UnauthorizedAccessException("Unknown HTTP source.");
    }

    public JsHttpSource TakeSource(string handle)
    {
        lock (_sourceGate)
            return _sources.Remove(handle, out var source) ? source : throw new UnauthorizedAccessException("Unknown HTTP source.");
    }

    public void CloseSource(string handle)
    {
        lock (_sourceGate)
            if (_sources.Remove(handle, out var source)) source.Dispose();
    }

    public JsHttpSource BeginStream(string handle)
    {
        JsHttpSource selected;
        lock (_sourceGate)
        {
            if (_claimed) throw new InvalidOperationException("A source can only be transferred once.");
            selected = Source(handle);
            foreach (var item in _sources.Where(item => item.Key != handle).ToArray())
            {
                item.Value.Dispose();
                _sources.Remove(item.Key);
            }
            _claimed = true;
        }
        _mapping = true;
        _operation.CancelAfter(_execution.ResponseTimeout);
        Context.Attempt?.NotifyUpstreamResponseStarted?.Invoke();
        return selected;
    }

    public JsonObject Map(string handler, object input, JsonNode? state)
    {
        _engineGate.Wait(Token);
        try
        {
            _mappingViolation = false;
            var stateJson = state?.ToJsonString(JsonOptions) ?? "null";
            if (Encoding.UTF8.GetByteCount(stateJson) > 512 * 1024)
                throw new InvalidOperationException("Stream state exceeds its size limit.");
            _step.Begin(_execution.MapperBudget, Token);
            var output = _engine.Invoke("__map", handler, JsonSerializer.Serialize(input, JsonOptions), stateJson).AsString();
            if (_mappingViolation) throw new InvalidOperationException("Host operations are not allowed inside stream mappers.");
            return ParseResult(output, 2 * 1024 * 1024) as JsonObject
                ?? throw new InvalidOperationException("Stream mapper must return an object.");
        }
        finally { _step.End(); _engineGate.Release(); }
    }

    public JsonNode? FinalizeCompletion(string handler, object completion)
    {
        _engineGate.Wait(Token);
        try
        {
            _mappingViolation = false;
            _step.Begin(_execution.CompletionMapperBudget, Token);
            var output = _engine.Invoke("__finalize", handler, JsonSerializer.Serialize(completion, JsonOptions)).AsString();
            if (_mappingViolation) throw new InvalidOperationException("Host operations are not allowed inside completion mappers.");
            return ParseResult(output, 32 * 1024 * 1024);
        }
        finally { _step.End(); _engineGate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception>? errors = null;
        try { await _operation.CancelAsync(); }
        catch (Exception exception) { (errors ??= []).Add(exception); }
        // Cancellation must unwind any active Jint call before its engine/realm is disposed.
        await _engineGate.WaitAsync();
        try { await Task.WhenAll(_calls); }
        catch (Exception exception) { (errors ??= []).Add(exception); }
        JsHttpSource[] sources;
        lock (_sourceGate) { sources = _sources.Values.ToArray(); _sources.Clear(); }
        foreach (var source in sources)
        {
            try { source.Dispose(); }
            catch (Exception exception) { (errors ??= []).Add(exception); }
        }
        HttpClient[] clients;
        lock (_sourceGate) { clients = _clients.Values.Select(item => item.Client).ToArray(); _clients.Clear(); }
        foreach (var client in clients)
        {
            try { client.Dispose(); }
            catch (Exception exception) { (errors ??= []).Add(exception); }
        }
        try { _engine.Dispose(); }
        catch (Exception exception) { (errors ??= []).Add(exception); }
        finally
        {
            _operation.Dispose();
            _engineGate.Release();
            _engineGate.Dispose();
            _global.Release();
            _local.Release();
            _onDisposed?.Invoke();
        }
        if (errors is not null) throw new AggregateException(errors);
    }

    private static JsonNode? ParseResult(string json, int limit)
    {
        if (Encoding.UTF8.GetByteCount(json) > limit) throw new InvalidOperationException("JS output exceeds its size limit.");
        return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 48 });
    }
}
