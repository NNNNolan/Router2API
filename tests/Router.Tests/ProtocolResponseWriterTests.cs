using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Router.Contracts.Domain;
using Router.Host.Api;
using Router.Host.Tracing;

namespace Router.Tests;

[TestClass]
public sealed class ProtocolResponseWriterTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SseWriterWaitsForBackpressureAndPropagatesCancellation(bool cancel)
    {
        var context = new DefaultHttpContext();
        await using var stream = new DelayedWriteStream();
        context.Response.Body = stream;
        using var writer = new SseJsonWriter(context.Response);
        using var cancellation = new CancellationTokenSource();
        var pending = writer.WriteAsync(new { value = 1 }, cancellation.Token);
        pending.IsCompleted.Should().BeFalse();
        if (cancel)
        {
            cancellation.Cancel();
            Func<Task> wait = async () => await pending;
            await wait.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            stream.Release.TrySetResult();
            await pending;
            Encoding.UTF8.GetString(stream.ToArray()).Should().Be("data: {\"value\":1}\n\n");
        }
    }

    [TestMethod]
    public void RawMarkersCanCrossEveryBoundaryWithoutFalsePrefixMatches()
    {
        foreach (var (endpoint, marker) in new[]
        {
            (TestEndpoint.ChatCompletions, "data: [DONE]"),
            (TestEndpoint.Responses, "event: response.completed"),
            (TestEndpoint.AnthropicMessages, "event: message_stop")
        })
        {
            var bytes = Encoding.UTF8.GetBytes(marker);
            for (var split = 1; split < bytes.Length; split++)
            {
                var scan = ProtocolResponseWriter.RawCompletionScan.For(endpoint)!;
                scan.Feed("event: wrong\ndata: [DONX]\n"u8).Should().BeFalse();
                scan.Feed(bytes.AsSpan(0, split)).Should().BeFalse();
                scan.Feed(bytes.AsSpan(split)).Should().BeTrue();
                scan.Feed(bytes).Should().BeFalse("completion is signalled once");
            }
            var fragmented = ProtocolResponseWriter.RawCompletionScan.For(endpoint)!;
            for (var index = 0; index < bytes.Length; index++)
                fragmented.Feed(bytes.AsSpan(index, 1)).Should().Be(index == bytes.Length - 1);
        }
    }

    [TestMethod]
    public async Task ChatSseKeepsUnicodeToolDeltasNullsAndUsageOnlyFrames()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await OpenAiResponseWriter.WriteAsync(context, new AdapterResponse { StatusCode = 200, IsStreaming = true, Stream = ChatChunks() }, "model", default);
        var events = Encoding.UTF8.GetString(body.ToArray()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        events.Should().HaveCount(3);
        var first = JsonNode.Parse(events[0][6..])!;
        first["choices"]![0]!["delta"]!["content"]!.GetValue<string>().Should().Be("中😀");
        first["choices"]![0]!["delta"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>().Should().Be("{\"q\":");
        first["usage"].Should().BeNull();
        var usage = JsonNode.Parse(events[1][6..])!;
        usage["choices"]!.AsArray().Should().BeEmpty();
        usage["usage"]!["total_tokens"]!.GetValue<int>().Should().Be(3);
        events[2].Should().Be("data: [DONE]");
    }

    private static async IAsyncEnumerable<StreamChunk> ChatChunks()
    {
        await Task.CompletedTask;
        yield return new StreamChunk("中😀", ToolCalls: [new ToolCallDelta(0, "call", "function", "lookup", "{\"q\":")]);
        yield return new StreamChunk(null, Usage: new Usage(1, 2, 3));
    }

    private sealed class DelayedWriteStream : MemoryStream
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    [TestMethod]
    public async Task LegacyCompletionUsesTextCompletionShape()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var response = new AdapterResponse
        {
            StatusCode = 200,
            Completion = new AdapterCompletion(
                "myai-fast",
                "hello",
                "stop",
                new Usage(1, 2, 3))
        };

        await ProtocolResponseWriter.WriteAsync(
            context,
            response,
            TestEndpoint.Completions,
            "fallback-model",
            CancellationToken.None);

        body.Position = 0;
        var root = JsonNode.Parse(await new StreamReader(body, Encoding.UTF8).ReadToEndAsync())!;
        root["object"]!.GetValue<string>().Should().Be("text_completion");
        root["model"]!.GetValue<string>().Should().Be("myai-fast");
        root["choices"]![0]! ["text"]!.GetValue<string>().Should().Be("hello");
        root["usage"]!["total_tokens"]!.GetValue<int>().Should().Be(3);
    }

    [TestMethod]
    public void TraceIdIsCreatedAtHttpIngressAndCanBeReused()
    {
        var context = new DefaultHttpContext();

        var first = TraceIdContext.GetOrCreate(context);
        var second = TraceIdContext.GetOrCreate(context);

        first.Should().NotBeNullOrWhiteSpace();
        first.Should().Be(second);
        first.Should().MatchRegex("^[A-Za-z0-9._:-]+$");
    }
}
