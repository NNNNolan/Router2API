using System.Runtime.CompilerServices;

namespace Router.Host.Plugins.JavaScript;

/// <summary>Jint 只拿到随机 handle；HTTP 响应、请求与取消源始终归宿主所有。</summary>
internal sealed class JsHttpSource(
    HttpResponseMessage response,
    HttpClient? client,
    HttpRequestMessage? request,
    CancellationTokenSource cancellation,
    TimeSpan readIdleTimeout) : IDisposable
{
    private byte[]? _buffer;
    private int _reading;
    private int _disposed;
    public int StatusCode => (int)response.StatusCode;
    public string ContentType => response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
    public TimeSpan ReadIdleTimeout { get; } = readIdleTimeout;
    public Dictionary<string, string[]> Headers => response.Headers.Concat(response.Content.Headers)
        .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key.ToLowerInvariant(), group => group.SelectMany(header => header.Value).ToArray(), StringComparer.OrdinalIgnoreCase);

    public object Metadata(string handle) => new { handle, statusCode = StatusCode, contentType = ContentType, headers = Headers };

    public async Task<Stream> OpenStreamAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _buffer is { } buffer ? new MemoryStream(buffer, writable: false)
            : await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    public async Task<byte[]> ReadBufferAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_buffer is { } cached) return cached;
        if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("HTTP source is already being read.");
        try
        {
            await using var stream = await OpenStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            await foreach (var chunk in ReadRawAsync(stream, cancellationToken))
            {
                if (buffer.Length + chunk.Length > maximumBytes) throw new InvalidOperationException("HTTP response exceeds its buffer limit.");
                await buffer.WriteAsync(chunk, cancellationToken);
            }
            return _buffer = buffer.ToArray();
        }
        finally { Volatile.Write(ref _reading, 0); }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRawAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancellation.Token);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            idle.CancelAfter(ReadIdleTimeout);
            int count;
            try { count = await stream.ReadAsync(buffer, idle.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                throw new TimeoutException("Upstream body read idle timeout.");
            }
            finally { idle.CancelAfter(Timeout.InfiniteTimeSpan); }
            if (count == 0) yield break;
            yield return buffer.AsMemory(0, count).ToArray();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { cancellation.Cancel(); }
        finally
        {
            try { response.Dispose(); }
            finally
            {
                try { request?.Dispose(); }
                finally
                {
                    try { client?.Dispose(); }
                    finally { cancellation.Dispose(); }
                }
            }
        }
    }
}
