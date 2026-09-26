using System.Net;

namespace Router.Infrastructure.Services;

/// <summary>响应头返回后仍由宿主记录真实读取故障；JSON/脚本抛出的异常不构成节点故障证据。</summary>
internal sealed class ObservedHttpContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly Action _onFailure;

    public ObservedHttpContent(HttpContent inner, Action onFailure)
    {
        _inner = inner;
        _onFailure = onFailure;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        try { await _inner.CopyToAsync(stream, context, cancellationToken); }
        catch (Exception exception) when (IsTransportFailure(exception)) { _onFailure(); throw; }
    }

    protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        try { return new ObservedStream(await _inner.ReadAsStreamAsync(cancellationToken), _onFailure); }
        catch (Exception exception) when (IsTransportFailure(exception)) { _onFailure(); throw; }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _inner.Headers.ContentLength ?? 0;
        return _inner.Headers.ContentLength.HasValue;
    }

    protected override void Dispose(bool disposing)
    {
        try { if (disposing) _inner.Dispose(); }
        finally { base.Dispose(disposing); }
    }

    private static bool IsTransportFailure(Exception exception)
        => exception is HttpRequestException or IOException or OperationCanceledException;

    private sealed class ObservedStream(Stream inner, Action onFailure) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            try { return inner.Read(buffer); }
            catch (Exception exception) when (IsTransportFailure(exception)) { onFailure(); throw; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { return await inner.ReadAsync(buffer, cancellationToken); }
            catch (Exception exception) when (IsTransportFailure(exception)) { onFailure(); throw; }
        }
        protected override void Dispose(bool disposing)
        {
            try { if (disposing) inner.Dispose(); }
            finally { base.Dispose(disposing); }
        }
    }
}
