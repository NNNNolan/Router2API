using System.Buffers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Router.Contracts.Domain;
using Router.Host.Api;
using Router.Host.Plugins.JavaScript;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
[TestCategory("Performance")]
public sealed class AllocationBudgetTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void PureComparisonsMetricsAndChatEncodingAllocateZeroAfterWarmup()
    {
        var metrics = new RealtimeMetrics();
        var policies = new PluginPolicyRegistry();
        policies.Set("Plugin", "Platform", Router.Contracts.Plugins.PluginProxyPolicy.Default with { MaxAttempts = 7 });
        var scan = ProtocolResponseWriter.RawCompletionScan.For(TestEndpoint.ChatCompletions)!;
        var resource = new Account { PluginKey = "probe", Platform = "probe" };
        var now = DateTimeOffset.UtcNow;
        using var json = new Utf8JsonWriter(new ArrayBufferWriter<byte>());
        var buffer = new ArrayBufferWriter<byte>(4096);
        var chunk = new StreamChunk("中😀", ToolCalls: [new ToolCallDelta(0, "call", "function", "lookup", "{}")], Usage: new Usage(1, 2, 3));
        string[] levels = ["LOW", "HIGH", "MAX"];
        var bytes = Measure(1000, () =>
        {
            if (!CredentialCodec.NameEquals("A-C_C_ESS_TOKEN", "accessToken") || !ResourceAvailability.IsAvailable(resource, now))
                throw new InvalidOperationException();
            metrics.OnRequestStarted("probe");
            metrics.OnRequestCompleted("probe", 1, true, chunk.Usage);
            if (policies.GetSnapshot("PLUGIN", "PLATFORM").Attempts.MaxAttempts != 7) throw new InvalidOperationException();
            if (scan.Feed("partial data"u8)) throw new InvalidOperationException();
            if (ReasoningLimiter.ClosestSupportedLevel(" NORMAL ", levels) != "LOW") throw new InvalidOperationException();
            buffer.Clear();
            json.Reset(buffer);
            OpenAiResponseWriter.WriteChunk(json, chunk, "chat-id", 1, "probe");
            json.Flush();
        });
        TestContext.WriteLine($"Pure helpers + counters + chat JSON: {bytes} B/op");
        bytes.Should().Be(0);
    }

    [TestMethod]
    public void OAuthDecodeHasABoundedAllocationBudget()
    {
        var credential = new OAuthCredential("access", new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), "refresh", "id", "account", "example.invalid", "enterprise", "nickname");
        var encoded = CredentialCodec.Encode(credential);
        Credential? result = null;
        var bytes = Measure(1000, () => result = CredentialCodec.Decode(encoded.Kind, encoded.Json));
        result.Should().Be(credential);
        TestContext.WriteLine($"OAuth decode: {bytes} B/op");
        bytes.Should().BeLessThanOrEqualTo(2048);
    }

    [TestMethod]
    public void RawAllocationDoesNotGrowWithTheNumberOfChunks()
    {
        using var source = new JsHttpSource(new HttpResponseMessage { Content = new ByteArrayContent([]) },
            null, null, new CancellationTokenSource(), TimeSpan.FromSeconds(60));
        using var shortStream = new MemoryStream(new byte[16 * 1024]);
        using var longStream = new MemoryStream(new byte[1024 * 1024]);
        var shortBytes = Measure(30, () => ConsumeRaw(source, shortStream));
        var longBytes = Measure(30, () => ConsumeRaw(source, longStream));
        TestContext.WriteLine($"Raw 16 KiB / 1 MiB: {shortBytes} / {longBytes} B/stream");
        longBytes.Should().BeLessThanOrEqualTo(shortBytes + 128, "chunks must borrow one buffer instead of allocating an array per read");
    }

    [TestMethod]
    public void SseFramesStayWithinTheirAllocationBudget()
    {
        using var small = SseStream(64);
        using var large = SseStream(1024);
        var smallBytes = Measure(10, () => ConsumeSse(small));
        var largeBytes = Measure(10, () => ConsumeSse(large));
        var perFrame = (largeBytes - smallBytes) / 960.0;
        TestContext.WriteLine($"SSE: {perFrame:F1} B/frame, {largeBytes} B/1024-frame stream");
        perFrame.Should().BeLessThanOrEqualTo(112, "only the frame object and its data string should grow per event");
    }

    [TestMethod]
    public void ChatSseWritesReuseTheirUtf8Buffer()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = Stream.Null;
        using var writer = new SseJsonWriter(context.Response);
        var chunk = new StreamChunk("delta");
        var bytes = Measure(1000, () =>
        {
            OpenAiResponseWriter.WriteChunk(writer.StartEvent(), chunk, "chat-id", 1, "probe");
            Complete(writer.FlushEventAsync(default));
        });
        TestContext.WriteLine($"Chat SSE encode + flush to Stream.Null: {bytes} B/frame");
        bytes.Should().Be(0);
    }

    private static MemoryStream SseStream(int frames)
        => new(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("data: {\"x\":1}\n\n", frames))));

    private static void ConsumeSse(MemoryStream stream)
    {
        stream.Position = 0;
        var iterator = SseFrameReader.ReadAsync(stream).GetAsyncEnumerator();
        try
        {
            while (true)
            {
                var next = iterator.MoveNextAsync();
                if (!next.IsCompleted) throw new InvalidOperationException("Allocation probe requires synchronous memory reads.");
                if (!next.Result) break;
            }
        }
        finally { Complete(iterator.DisposeAsync()); }
    }

    private static void ConsumeRaw(JsHttpSource source, MemoryStream stream)
    {
        stream.Position = 0;
        var iterator = source.ReadRawAsync(stream, default).GetAsyncEnumerator();
        var scan = ProtocolResponseWriter.RawCompletionScan.For(TestEndpoint.ChatCompletions)!;
        var bytes = 0;
        try
        {
            while (true)
            {
                var next = iterator.MoveNextAsync();
                if (!next.IsCompleted) throw new InvalidOperationException("Allocation probe requires synchronous memory reads.");
                if (!next.Result) break;
                bytes += iterator.Current.Length;
                _ = scan.Feed(iterator.Current.Span);
            }
        }
        finally { Complete(iterator.DisposeAsync()); }
        if (bytes != stream.Length) throw new InvalidOperationException("Incomplete raw read.");
    }

    private static void Complete(ValueTask task)
    {
        if (!task.IsCompleted) throw new InvalidOperationException("Allocation probe requires synchronous completion.");
        task.GetAwaiter().GetResult();
    }

    private static double Measure(int iterations, Action action)
    {
        for (var index = 0; index < 32; index++) action();
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++) action();
        return (GC.GetAllocatedBytesForCurrentThread() - start) / (double)iterations;
    }
}
