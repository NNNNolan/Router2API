using FluentAssertions;
using Router.Host.Plugins.JavaScript;

namespace Router.Tests;

[TestClass]
public sealed class JsHttpSourceTests
{
    [TestMethod]
    public async Task ConcurrentRawResponsesKeepBytesStableUntilTheirConsumersAdvance()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async worker =>
        {
            var bytes = Enumerable.Range(0, 70000).Select(index => (byte)(index + worker)).ToArray();
            using var source = Source();
            using var stream = new MemoryStream(bytes);
            var offset = 0;
            await foreach (var chunk in source.ReadRawAsync(stream, default))
            {
                await Task.Yield(); // 等待下游写入期间，借用的数据块必须保持完整。
                chunk.Span.SequenceEqual(bytes.AsSpan(offset, chunk.Length)).Should().BeTrue();
                offset += chunk.Length;
            }
            offset.Should().Be(bytes.Length);
        }));
    }

    [TestMethod]
    public async Task RawIdleTimeoutAndCallerCancellationAreDistinct()
    {
        using var source = Source();
        using var stream = new BlockedStream();
        Func<Task> idle = async () => { await foreach (var _ in source.ReadRawAsync(stream, default)) { } };
        await idle.Should().ThrowAsync<TimeoutException>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        Func<Task> cancelled = async () => { await foreach (var _ in source.ReadRawAsync(stream, cancellation.Token)) { } };
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    private static JsHttpSource Source() => new(new HttpResponseMessage { Content = new ByteArrayContent([]) },
        null, null, new CancellationTokenSource(), TimeSpan.FromMilliseconds(50));

    private sealed class BlockedStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
