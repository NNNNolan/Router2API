using System.Text;
using System.Text.Json;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Router.Infrastructure.Services;

/// <summary>绑定一个插件版本的后台任务所有者；输入、结果、队列和历史均有界，卸载前取消并等待。</summary>
public sealed class PluginJobManager(string pluginKey, IPluginLogSink logs) : IPluginJobs, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Platform, string Name), PluginJobRegistration> _registrations = [];
    private readonly Dictionary<string, Entry> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slot = new(1, 1);
    private bool _accepting = true;
    private Task? _disposeTask;

    /// <summary>只由包加载器在启动 hook 前注册任务。</summary>
    public void Register(IEnumerable<PluginJobRegistration> jobs)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            foreach (var job in jobs)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(job.Name);
                ArgumentException.ThrowIfNullOrWhiteSpace(job.Platform);
                if (job.Timeout <= TimeSpan.Zero || job.Timeout > TimeSpan.FromHours(1))
                    throw new ArgumentException("Job timeout must be positive and at most one hour.", nameof(jobs));
                if (!_registrations.TryAdd((job.Platform, job.Name), job))
                    throw new InvalidOperationException($"Duplicate job {job.Platform}/{job.Name}.");
            }
        }
    }

    /// <summary>包括尚未真正退出的取消任务。</summary>
    public int InFlight { get { lock (_gate) return _jobs.Values.Count(job => !job.Run.IsCompleted); } }

    /// <inheritdoc />
    public Task<PluginJobSnapshot> StartAsync(string name, JsonElement? input = null, PluginJobOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new PluginJobOptions();
        var payload = Clone(input, 64 * 1024);
        if (options.Key is { } key && (key.Length is 0 or > 128 || key.Any(char.IsControl)))
            throw new ArgumentException("Job keys must be 1–128 non-control characters.", nameof(options));
        lock (_gate)
        {
            if (!_accepting) throw new InvalidOperationException("The plugin is draining; new jobs are not accepted.");
            Prune();
            var matches = _registrations.Values.Where(job => job.Name == name
                && (options.Platform is null || options.Platform == job.Platform)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Unknown or ambiguous job; specify its declared platform.");
            var registration = matches[0];
            if (options.Key is not null)
            {
                var existing = _jobs.Values.FirstOrDefault(job => !job.Run.IsCompleted && job.Snapshot.Name == name
                    && job.Snapshot.Platform == registration.Platform && job.Snapshot.Key == options.Key);
                if (existing is not null) return Task.FromResult(existing.Snapshot);
            }
            if (_jobs.Values.Count(job => !job.Run.IsCompleted) >= 128)
                throw new InvalidOperationException("Plugin job queue quota exceeded.");
            var entry = new Entry(registration, payload, new PluginJobSnapshot(Guid.NewGuid().ToString("N"), name,
                registration.Platform, options.Key, PluginJobState.Queued, DateTimeOffset.UtcNow));
            _jobs.Add(entry.Snapshot.Id, entry);
            entry.Run = Task.Run(() => RunAsync(entry), CancellationToken.None);
            return Task.FromResult(entry.Snapshot);
        }
    }

    /// <inheritdoc />
    public PluginJobSnapshot? Get(string id)
    {
        lock (_gate) { Prune(); return _jobs.GetValueOrDefault(id)?.Snapshot; }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginJobSnapshot> List()
    {
        lock (_gate) { Prune(); return _jobs.Values.Select(job => job.Snapshot).OrderByDescending(job => job.QueuedAt).ToArray(); }
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entry? entry;
        lock (_gate) entry = _jobs.GetValueOrDefault(id);
        if (entry is null || entry.Run.IsCompleted) return false;
        try { await entry.Stop.CancelAsync(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <inheritdoc />
    public async Task<PluginJobSnapshot?> WaitAsync(string id, CancellationToken cancellationToken = default)
    {
        Entry? entry;
        lock (_gate) entry = _jobs.GetValueOrDefault(id);
        if (entry is null) return null;
        await entry.Run.WaitAsync(cancellationToken);
        lock (_gate) return entry.Snapshot;
    }

    /// <summary>拒绝新任务，取消并等待当前任务真实结束；超时由宿主的 drain 令牌控制。</summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        Entry[] entries;
        lock (_gate) { _accepting = false; entries = _jobs.Values.ToArray(); }
        foreach (var entry in entries)
        {
            try { await entry.Stop.CancelAsync(); }
            catch (ObjectDisposedException) { }
        }
        await Task.WhenAll(entries.Select(entry => entry.Run)).WaitAsync(cancellationToken);
    }

    /// <summary>切换失败后恢复旧版本的任务入队，不重新执行已经取消的任务。</summary>
    public void Resume()
    {
        lock (_gate) if (_disposeTask is null) _accepting = true;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await DrainAsync(CancellationToken.None);
        lock (_gate) { _jobs.Clear(); _registrations.Clear(); }
        _slot.Dispose();
    }

    private async Task RunAsync(Entry entry)
    {
        var entered = false;
        try
        {
            await _slot.WaitAsync(entry.Stop.Token);
            entered = true;
            entry.Stop.CancelAfter(entry.Registration.Timeout);
            lock (_gate) entry.Snapshot = entry.Snapshot with { State = PluginJobState.Running, StartedAt = DateTimeOffset.UtcNow };
            await WriteLogAsync(entry, "job.started");
            var result = await entry.Registration.ExecuteAsync(new PluginJobContext(entry.Snapshot.Id, pluginKey,
                entry.Snapshot.Platform, entry.Input, progress =>
                {
                    var copy = Clone(progress, 64 * 1024);
                    lock (_gate) entry.Snapshot = entry.Snapshot with { Progress = copy };
                }, entry.Stop.Token));
            entry.Stop.Token.ThrowIfCancellationRequested();
            var output = Clone(result, 256 * 1024);
            lock (_gate) entry.Snapshot = entry.Snapshot with { State = PluginJobState.Completed, Result = output };
        }
        catch (OperationCanceledException) when (entry.Stop.IsCancellationRequested)
        {
            lock (_gate) entry.Snapshot = entry.Snapshot with { State = PluginJobState.Cancelled, Error = "Job cancelled or deadline reached." };
        }
        catch (Exception exception)
        {
            var message = exception.Message;
            lock (_gate) entry.Snapshot = entry.Snapshot with
            {
                State = PluginJobState.Failed, Error = message.Length > 1000 ? message[..1000] : message
            };
        }
        finally
        {
            lock (_gate) entry.Snapshot = entry.Snapshot with { FinishedAt = DateTimeOffset.UtcNow };
            await WriteLogAsync(entry, "job.completed");
            entry.Dispose();
            if (entered) _slot.Release();
        }
    }

    private async Task WriteLogAsync(Entry entry, string eventType)
    {
        try
        {
            await logs.WriteAsync(new PluginLog
            {
                PluginKey = pluginKey, Platform = entry.Snapshot.Platform, TaskName = entry.Snapshot.Name,
                EventType = eventType, Message = entry.Snapshot.Error ?? eventType, Level = entry.Snapshot.State == PluginJobState.Failed ? "Error" : "Information",
                DetailsJson = JsonSerializer.Serialize(new { jobId = entry.Snapshot.Id, state = entry.Snapshot.State.ToString() })
            });
        }
        catch { /* Job ownership cannot depend on diagnostic persistence. */ }
    }

    private void Prune()
    {
        var finished = _jobs.Values.Where(job => job.Run.IsCompleted).OrderByDescending(job => job.Snapshot.FinishedAt).ToArray();
        foreach (var entry in finished.Where((job, index) => index >= 128 || job.Snapshot.FinishedAt < DateTimeOffset.UtcNow.AddHours(-1)))
            _jobs.Remove(entry.Snapshot.Id);
    }

    private static JsonElement? Clone(JsonElement? value, int maxBytes)
    {
        if (value is null) return null;
        if (value.Value.ValueKind == JsonValueKind.Undefined || Encoding.UTF8.GetByteCount(value.Value.GetRawText()) > maxBytes)
            throw new InvalidOperationException("Job JSON exceeds its quota.");
        return value.Value.Clone();
    }

    private sealed class Entry(PluginJobRegistration registration, JsonElement? input, PluginJobSnapshot snapshot) : IDisposable
    {
        public PluginJobRegistration Registration { get; } = registration;
        public JsonElement? Input { get; } = input;
        public PluginJobSnapshot Snapshot { get; set; } = snapshot;
        public CancellationTokenSource Stop { get; } = new();
        public Task Run { get; set; } = Task.CompletedTask;
        public void Dispose() => Stop.Dispose();
    }
}
