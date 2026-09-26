using System.Runtime.CompilerServices;
using Router.Contracts.Domain;

namespace Router.Infrastructure.Services;

/// <summary>流消费结果，区别正常结束、错误和提前停止消费。</summary>
public sealed record PluginStreamCompletion(bool Success, bool Cancelled, string? Error, Usage? Usage);

/// <summary>组合响应资源和完成通知；标准流及 raw 流共用一个幂等释放所有者。</summary>
public sealed class PluginResponseLifetime : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<Func<ValueTask>> _resources = [];
    private readonly List<Func<PluginStreamCompletion, ValueTask>> _completed = [];
    private readonly bool _httpSuccess;
    private TaskCompletionSource? _disposal;
    private bool _writing;
    private bool _started;
    private bool _ended;
    private bool _cancelled;
    private string? _error;
    private Usage? _usage;

    private PluginResponseLifetime(AdapterResponse response)
    {
        _httpSuccess = response.IsSuccess;
        _error = response.Error;
        _usage = response.Usage;
        if (response.Lifetime is { } inner) _resources.Add(inner.DisposeAsync);
    }

    /// <summary>指示响应是否含有待消费的流。</summary>
    public static bool HasStream(AdapterResponse response) => response.Stream is not null || response.RawStream is not null;

    /// <summary>为响应附加一次性流监控；已有同类所有者时不重复包装。</summary>
    public static AdapterResponse Ensure(AdapterResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Lifetime is PluginResponseLifetime) return response;
        var lifetime = new PluginResponseLifetime(response);
        return new AdapterResponse
        {
            StatusCode = response.StatusCode,
            IsStreaming = response.IsStreaming,
            Completion = response.Completion,
            Error = response.Error,
            ErrorType = response.ErrorType,
            Usage = response.Usage,
            IsRawPassthrough = response.IsRawPassthrough,
            RawContent = response.RawContent,
            ContentType = response.ContentType,
            Lifetime = lifetime,
            Stream = response.Stream is { } stream ? lifetime.ReadChunksAsync(stream) : null,
            RawStream = response.RawStream is { } raw ? lifetime.ReadRawAsync(raw) : null
        };
    }

    /// <summary>登记需在完整响应结束后释放的资源。</summary>
    public void AddResource(Func<ValueTask> release)
    {
        ArgumentNullException.ThrowIfNull(release);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            _resources.Add(release);
        }
    }

    /// <summary>资源释放完毕后执行完成通知，例如 drain 计数和最终日志。</summary>
    public void OnCompleted(Func<PluginStreamCompletion, ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            _completed.Add(callback);
        }
    }

    /// <summary>写出器接管完成点，最终 SSE 标记写出前不提前记录成功。</summary>
    public void BeginWrite()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            _writing = true;
        }
    }

    /// <summary>记录写出器或源流失败；不把原始异常中的凭据写入流错误。</summary>
    public void Fail(bool cancelled, string message)
    {
        _cancelled |= cancelled;
        _error ??= message;
    }

    private void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_started) throw new InvalidOperationException("A response stream can only be consumed once.");
            _started = true;
        }
    }

    private async IAsyncEnumerable<StreamChunk> ReadChunksAsync(
        IAsyncEnumerable<StreamChunk> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Start();
        IAsyncEnumerator<StreamChunk>? reader = null;
        try
        {
            reader = source.GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool next;
                Exception? failure = null;
                try { next = await reader.MoveNextAsync(); }
                catch (Exception exception)
                {
                    Fail(cancellationToken.IsCancellationRequested,
                        cancellationToken.IsCancellationRequested ? "request cancelled" : "upstream stream failed");
                    next = false;
                    failure = exception;
                }
                if (failure is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new StreamChunk(null, Error: "Upstream stream failed.", ErrorType: "upstream_stream_error");
                    yield break;
                }
                if (!next) { _ended = true; yield break; }
                var chunk = reader.Current;
                _usage = chunk.Usage ?? _usage;
                if (chunk.Error is { } error) Fail(false, error);
                yield return chunk;
                if (chunk.Error is not null) yield break;
            }
        }
        finally
        {
            await FinishReaderAsync(reader);
        }
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRawAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Start();
        IAsyncEnumerator<ReadOnlyMemory<byte>>? reader = null;
        try
        {
            reader = source.GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool next;
                try { next = await reader.MoveNextAsync(); }
                catch
                {
                    Fail(cancellationToken.IsCancellationRequested,
                        cancellationToken.IsCancellationRequested ? "request cancelled" : "raw stream failed");
                    throw; // Never inject JSON/SSE into an opaque raw protocol.
                }
                if (!next) { _ended = true; yield break; }
                yield return reader.Current;
            }
        }
        finally
        {
            await FinishReaderAsync(reader);
        }
    }

    private async ValueTask FinishReaderAsync(IAsyncDisposable? reader)
    {
        try
        {
            if (reader is not null) await reader.DisposeAsync();
        }
        catch
        {
            Fail(false, "upstream stream cleanup failed");
            throw;
        }
        finally
        {
            if (!_writing) await DisposeAsync();
        }
    }

    /// <summary>幂等释放；所有调用者等待同一个清理任务，某项失败不跳过其余资源。</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? start = null;
        Task task;
        lock (_gate)
        {
            if (_disposal is null) start = _disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task = _disposal.Task;
        }
        if (start is not null) _ = DisposeCoreAsync(start);
        return new ValueTask(task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        List<Exception>? errors = null;
        foreach (var release in _resources)
        {
            try { await release(); }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
                Fail(false, "response resource cleanup failed");
            }
        }
        var outcome = new PluginStreamCompletion(_httpSuccess && _ended && !_cancelled && _error is null,
            _cancelled, _error ?? (!_ended ? "stream was not fully consumed" : null), _usage);
        foreach (var callback in _completed)
        {
            try { await callback(outcome); }
            catch (Exception exception) { (errors ??= []).Add(exception); }
        }
        _resources.Clear();
        _completed.Clear();
        if (errors is null) completion.TrySetResult();
        else completion.TrySetException(new AggregateException(errors));
    }
}
