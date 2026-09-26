using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Router.Infrastructure.Services;

/// <summary>完成分帧但尚未解释提供方 JSON 的 SSE 事件。</summary>
public sealed record SseFrame(string Data, string? Event = null, string? Id = null, int? Retry = null, bool IsComment = false);

/// <summary>有界 UTF-8 SSE 分帧器；支持网络拆包、多行 data、CR/LF 和注释，保留背压。</summary>
public static class SseFrameReader
{
    /// <summary>逐帧读取，不关闭输入流；未以空行结束的 EOF 数据不伪装成完整事件。</summary>
    /// <param name="stream">上游响应流。</param>
    /// <param name="maxFrameBytes">帧及单行的最大 UTF-8 字节数。</param>
    /// <param name="idleTimeout">等待新字节的空闲上限，不计下游消费和 mapper 时间。</param>
    /// <param name="cancellationToken">整个流的取消令牌。</param>
    public static async IAsyncEnumerable<SseFrame> ReadAsync(
        Stream stream,
        int maxFrameBytes = 1024 * 1024,
        TimeSpan? idleTimeout = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameBytes);
        var idleLimit = idleTimeout ?? TimeSpan.FromSeconds(60);
        if (idleLimit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new char[4096];
        var line = new StringBuilder();
        var data = new StringBuilder();
        string? eventName = null;
        string? lastId = null;
        int? retry = null;
        var frameBytes = 0;
        var hasData = false;
        var skipLf = false;
        var firstCharacter = true;

        SseFrame? ProcessLine()
        {
            var text = line.ToString();
            line.Clear();
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes > maxFrameBytes) throw new InvalidDataException("SSE line exceeds its size limit.");
            if (text.Length == 0)
            {
                var frame = hasData
                    ? new SseFrame(data.ToString(0, data.Length - 1), eventName, lastId, retry)
                    : null;
                data.Clear();
                hasData = false;
                eventName = null;
                frameBytes = 0;
                return frame;
            }
            if (text[0] == ':') return new SseFrame("", IsComment: true);
            frameBytes += bytes + 1;
            if (frameBytes > maxFrameBytes) throw new InvalidDataException("SSE frame exceeds its size limit.");
            var colon = text.IndexOf(':');
            var field = colon < 0 ? text : text[..colon];
            var value = colon < 0 ? "" : text[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "data":
                    data.Append(value).Append('\n');
                    hasData = true;
                    break;
                case "event":
                    eventName = value.Length == 0 ? null : value;
                    break;
                case "id" when !value.Contains('\0'):
                    lastId = value;
                    break;
                case "retry" when value.All(character => character is >= '0' and <= '9')
                    && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds):
                    retry = milliseconds;
                    break;
            }
            return null;
        }

        while (true)
        {
            idle.CancelAfter(idleLimit);
            int count;
            try { count = await reader.ReadAsync(buffer.AsMemory(), idle.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Upstream SSE read idle timeout.");
            }
            finally { idle.CancelAfter(Timeout.InfiniteTimeSpan); }
            if (count == 0) yield break;
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (firstCharacter)
                {
                    firstCharacter = false;
                    if (character == '\ufeff') continue;
                }
                if (skipLf && character == '\n') { skipLf = false; continue; }
                skipLf = false;
                if (character is '\r' or '\n')
                {
                    skipLf = character == '\r';
                    if (ProcessLine() is { } frame) yield return frame;
                }
                else
                {
                    line.Append(character);
                    // UTF-8 needs at least one byte per UTF-16 code unit, so this also bounds partial lines.
                    if (line.Length > maxFrameBytes) throw new InvalidDataException("SSE line exceeds its size limit.");
                }
            }
        }
    }
}
