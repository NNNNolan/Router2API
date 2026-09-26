using System.Text;
using FluentAssertions;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class SseFrameReaderTests
{
    [TestMethod]
    public async Task FramesHandleUtf8SplitsCrLfCommentsAndMultilineData()
    {
        await using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(
            "\ufeff: keepalive\r\nid: first\r\nevent: chunk\r\ndata: {\"value\":\r\ndata: \"中😀\"}\r\nretry: 123\r\n\r\ndata:\n\n"));
        var frames = new List<SseFrame>();
        await foreach (var frame in SseFrameReader.ReadAsync(stream)) frames.Add(frame);
        frames.Should().HaveCount(3);
        frames[0].IsComment.Should().BeTrue();
        frames[1].Data.Should().Be("{\"value\":\n\"中😀\"}");
        frames[1].Event.Should().Be("chunk");
        frames[1].Id.Should().Be("first");
        frames[1].Retry.Should().Be(123);
        frames[2].Data.Should().BeEmpty();
        frames[2].Id.Should().Be("first");
        stream.CanRead.Should().BeTrue();
    }

    [TestMethod]
    public async Task UnterminatedEofIsNotDispatchedAsACompleteEvent()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: [DONE]\n"));
        var count = 0;
        await foreach (var _ in SseFrameReader.ReadAsync(stream)) count++;
        count.Should().Be(0);
    }

    [TestMethod]
    public async Task OversizedPartialLineAndMultilineFrameAreRejected()
    {
        foreach (var input in new[] { "data: " + new string('x', 100), "data: 1234567890\ndata: 1234567890\ndata: 1234567890\n\n" })
        {
            await using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(input));
            Func<Task> read = async () => { await foreach (var _ in SseFrameReader.ReadAsync(stream, maxFrameBytes: 32)) { } };
            await read.Should().ThrowAsync<InvalidDataException>();
        }
    }

    [TestMethod]
    public async Task IdleReadTimesOutButCallerCancellationRemainsCancellation()
    {
        await using var stream = new NeverReadableStream();
        Func<Task> idle = async () =>
        {
            await foreach (var _ in SseFrameReader.ReadAsync(stream, idleTimeout: TimeSpan.FromMilliseconds(30))) { }
        };
        await idle.Should().ThrowAsync<TimeoutException>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        Func<Task> cancelled = async () =>
        {
            await foreach (var _ in SseFrameReader.ReadAsync(stream, cancellationToken: cancellation.Token)) { }
        };
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }

    private sealed class NeverReadableStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
