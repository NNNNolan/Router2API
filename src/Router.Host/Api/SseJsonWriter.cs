using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace Router.Host.Api;

/// <summary>每条响应复用一个 JSON writer，直接写入 UTF-8 响应缓冲区。</summary>
internal sealed class SseJsonWriter(HttpResponse response) : IDisposable
{
    private readonly PipeWriter _output = response.BodyWriter;
    private readonly Utf8JsonWriter _json = new(response.BodyWriter);

    public Utf8JsonWriter StartEvent(string? eventName = null)
    {
        if (eventName is not null)
        {
            _output.Write("event: "u8);
            var length = Encoding.UTF8.GetBytes(eventName, _output.GetSpan(Encoding.UTF8.GetMaxByteCount(eventName.Length)));
            _output.Advance(length);
            _output.Write("\n"u8);
        }
        _output.Write("data: "u8);
        _json.Reset(_output);
        return _json;
    }

    public ValueTask FlushEventAsync(CancellationToken cancellationToken)
    {
        _json.Flush();
        _output.Write("\n\n"u8);
        var pending = _output.FlushAsync(cancellationToken);
        if (!pending.IsCompletedSuccessfully) return AwaitFlushAsync(pending, cancellationToken);
        CheckFlush(pending.Result, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private static async ValueTask AwaitFlushAsync(ValueTask<FlushResult> pending, CancellationToken cancellationToken)
        => CheckFlush(await pending, cancellationToken);

    private static void CheckFlush(FlushResult result, CancellationToken cancellationToken)
    {
        if (result.IsCanceled) throw new OperationCanceledException(cancellationToken);
    }

    public ValueTask WriteAsync(object payload, CancellationToken cancellationToken, string? eventName = null)
    {
        var json = StartEvent(eventName);
        JsonSerializer.Serialize(json, payload, payload.GetType());
        return FlushEventAsync(cancellationToken);
    }

    public void Dispose() => _json.Dispose();
}
