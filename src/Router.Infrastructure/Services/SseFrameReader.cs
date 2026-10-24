using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Router.Infrastructure.Services;

/// <summary>完成分帧但尚未解释提供方 JSON 的 SSE 事件。</summary>
public sealed record SseFrame(string Data, string? Event = null, string? Id = null, int? Retry = null, bool IsComment = false);

/// <summary>有界 UTF-8 SSE 分帧器；支持网络拆包、多行 data、CR/LF 和注释，保留背压。</summary>
public static class SseFrameReader
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

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
        using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new char[4096];
        var parser = new SseFrameParser(maxFrameBytes);

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
                if (parser.Feed(buffer[index]) is { } frame) yield return frame;
            }
        }
    }

}

internal sealed class SseFrameParser(int maxFrameBytes)
{
    private static readonly SseFrame Comment = new("", IsComment: true);
    private readonly ArrayBufferWriter<char> _line = new();
    private readonly StringBuilder _data = new();
    private string? _eventName;
    private string? _lastId;
    private int? _retry;
    private int _frameBytes;
    private bool _hasData;
    private bool _skipLf;
    private bool _firstCharacter = true;

    public bool HasPendingData => _hasData || _line.WrittenCount != 0;

    public SseFrame? Feed(char character)
    {
        if (_firstCharacter)
        {
            _firstCharacter = false;
            if (character == '\ufeff') return null;
        }
        if (_skipLf && character == '\n') { _skipLf = false; return null; }
        _skipLf = false;
        if (character is '\r' or '\n')
        {
            _skipLf = character == '\r';
            return ProcessLine();
        }
        _line.GetSpan(1)[0] = character;
        _line.Advance(1);
        if (_line.WrittenCount > maxFrameBytes) throw new InvalidDataException("SSE line exceeds its size limit.");
        return null;
    }

    private SseFrame? ProcessLine()
    {
        var text = _line.WrittenSpan;
        _line.ResetWrittenCount();
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > maxFrameBytes) throw new InvalidDataException("SSE line exceeds its size limit.");
        if (text.Length == 0)
        {
            var frame = _hasData ? new SseFrame(_data.ToString(0, _data.Length - 1), _eventName, _lastId, _retry) : null;
            _data.Clear();
            _hasData = false;
            _eventName = null;
            _frameBytes = 0;
            return frame;
        }
        if (text[0] == ':') return Comment;
        _frameBytes += bytes + 1;
        if (_frameBytes > maxFrameBytes) throw new InvalidDataException("SSE frame exceeds its size limit.");
        var colon = text.IndexOf(':');
        var field = colon < 0 ? text : text[..colon];
        var value = colon < 0 ? [] : text[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..];
        switch (field)
        {
            case "data": _data.Append(value).Append('\n'); _hasData = true; break;
            case "event": _eventName = value.Length == 0 ? null : value.ToString(); break;
            case "id" when !value.Contains('\0'): _lastId = value.ToString(); break;
            case "retry" when IsAsciiDigits(value)
                && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds):
                _retry = milliseconds;
                break;
        }
        return null;
    }

    private static bool IsAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (character is < '0' or > '9') return false;
        return true;
    }
}
