using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Router.Contracts.Domain;
using Router.Host.Api;
using Router.Infrastructure.Services;
using Router.Infrastructure.Services.Privacy;

namespace Router.Tests;

[TestClass]
public sealed class PrivacyResponseTests
{
    private static readonly Dictionary<string, string> Map = new()
        { ["[R2A_test_0]"] = "person@example.com", ["[R2A_test_1]"] = "quote \" slash \\ newline\n😀" };

    [TestMethod]
    public void PlaceholderCanSplitAtEveryPositionWithoutDelayOfOrdinaryText()
    {
        const string key = "[R2A_test_0]";
        for (var position = 0; position <= key.Length; position++)
        {
            var restorer = new PlaceholderRestorer(Map);
            (restorer.Push("hello " + key[..position]) + restorer.Push(key[position..] + " world") + restorer.Flush())
                .Should().Be("hello person@example.com world");
        }
        new PlaceholderRestorer(Map).Push("ordinary text").Should().Be("ordinary text");
    }

    [TestMethod]
    [DataRow("/v1/chat/completions")]
    [DataRow("/v1/completions")]
    [DataRow("/v1/responses")]
    [DataRow("/v1/messages")]
    public async Task StandardStreamRestoresBeforeCompletionAndKeepsUsage(string endpoint)
    {
        var response = PluginResponseLifetime.Ensure(new AdapterResponse { StatusCode = 200, IsStreaming = true, Stream = Chunks() });
        PluginStreamCompletion? outcome = null;
        ((PluginResponseLifetime)response.Lifetime!).OnCompleted(value => { outcome = value; return ValueTask.CompletedTask; });
        var restored = PrivacyResponseRestorer.Restore(response, Map, endpoint);
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        await ProtocolResponseWriter.WriteAsync(context, restored, PrivacyGuardTests.Endpoint(endpoint), "demo", default);
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        body.Should().Contain("person@example.com").And.NotContain("[R2A_test_0]");
        outcome!.Success.Should().BeTrue();
        outcome.Usage.Should().Be(new Usage(1, 2, 3));
    }

    [TestMethod]
    public async Task IndependentToolsEscapeRestoredJsonArguments()
    {
        var response = PrivacyResponseRestorer.Restore(new AdapterResponse { StatusCode = 200, IsStreaming = true, Stream = Tools() }, Map, "/v1/chat/completions");
        var calls = new Dictionary<int, StringBuilder>();
        await foreach (var chunk in response.Stream!)
            foreach (var call in chunk.ToolCalls ?? [])
            {
                if (!calls.TryGetValue(call.Index, out var text)) calls[call.Index] = text = new();
                text.Append(call.Arguments);
            }
        using var first = JsonDocument.Parse(calls[0].ToString());
        using var second = JsonDocument.Parse(calls[1].ToString());
        first.RootElement.GetProperty("value").GetString().Should().Be(Map["[R2A_test_1]"]);
        second.RootElement.GetProperty("email").GetString().Should().Be(Map["[R2A_test_0]"]);
    }

    [TestMethod]
    [DataRow("/v1/chat/completions", """{"choices":[{"index":0,"message":{"content":"[R2A_test_0]"},"finish_reason":"stop"}],"usage":{"total_tokens":3}}""")]
    [DataRow("/v1/completions", """{"choices":[{"index":0,"text":"[R2A_test_0]","finish_reason":"stop"}]}""")]
    [DataRow("/v1/responses", """{"output":[{"type":"message","content":[{"type":"output_text","text":"[R2A_test_0]"}]}]}""")]
    [DataRow("/v1/messages", """{"content":[{"type":"text","text":"[R2A_test_0]"},{"type":"thinking","thinking":"[R2A_test_0]","signature":"unchanged"}]}""")]
    public void RawJsonOnlyChangesProtocolText(string endpoint, string source)
    {
        var response = PrivacyResponseRestorer.Restore(new AdapterResponse { StatusCode = 200, IsRawPassthrough = true,
            RawContent = Encoding.UTF8.GetBytes(source), ContentType = "application/json" }, Map, endpoint);
        var body = Encoding.UTF8.GetString(response.RawContent!);
        body.Should().Contain("person@example.com").And.NotContain("[R2A_test_0]");
        if (endpoint == "/v1/messages") body.Should().Contain("unchanged");
    }

    [TestMethod]
    [DataRow("/v1/chat/completions", "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"[R2A_\"}}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"test_0]\"}}]}\n\ndata: [DONE]\n\n")]
    [DataRow("/v1/completions", "data: {\"choices\":[{\"index\":0,\"text\":\"[R2A_\"}]}\n\ndata: {\"choices\":[{\"index\":0,\"text\":\"test_0]\"}]}\n\ndata: [DONE]\n\n")]
    [DataRow("/v1/responses", "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"[R2A_\"}\n\nevent: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"test_0]\"}\n\nevent: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n")]
    [DataRow("/v1/messages", "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"[R2A_\"}}\n\nevent: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"test_0]\"}}\n\nevent: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n")]
    public async Task RawSseRestoresAcrossByteAndEventBoundaries(string endpoint, string source)
    {
        var response = PrivacyResponseRestorer.Restore(new AdapterResponse { StatusCode = 200, IsRawPassthrough = true,
            RawStream = Bytes(source), ContentType = "text/event-stream" }, Map, endpoint);
        var output = new StringBuilder();
        await foreach (var chunk in response.RawStream!) output.Append(Encoding.UTF8.GetString(chunk.Span));
        output.ToString().Should().Contain("person@example.com").And.NotContain("[R2A_");
        if (source.Contains("[DONE]", StringComparison.Ordinal)) output.ToString().Should().EndWith("data: [DONE]\n\n");
    }

    [TestMethod]
    public async Task MalformedRawStreamFailsAndLifecycleDoesNotReportSuccess()
    {
        var source = PluginResponseLifetime.Ensure(new AdapterResponse { StatusCode = 200, IsRawPassthrough = true,
            RawStream = Bytes("data: not-json\n\n"), ContentType = "text/event-stream" });
        PluginStreamCompletion? result = null;
        ((PluginResponseLifetime)source.Lifetime!).OnCompleted(value => { result = value; return ValueTask.CompletedTask; });
        var restored = PrivacyResponseRestorer.Restore(source, Map, "/v1/chat/completions");
        var context = new DefaultHttpContext(); context.Response.Body = new MemoryStream();
        Func<Task> write = () => ProtocolResponseWriter.WriteAsync(context, restored, TestEndpoint.ChatCompletions, "demo", default);
        await write.Should().ThrowAsync<PrivacyInspectionException>();
        result!.Success.Should().BeFalse();
    }

    private static async IAsyncEnumerable<StreamChunk> Chunks()
    {
        await Task.Yield();
        yield return new("hello [R2A_");
        yield return new("test_0]", FinishReason: "stop", Usage: new(1, 2, 3));
    }
    private static async IAsyncEnumerable<StreamChunk> Tools()
    {
        await Task.Yield();
        yield return new(null, ToolCalls: [new(0, Arguments: "{\"value\":\"[R2A_"), new(1, Arguments: "{\"email\":\"[R2A_test_")]);
        yield return new(null, ToolCalls: [new(0, Arguments: "test_1]\"}"), new(1, Arguments: "0]\"}")], FinishReason: "tool_calls");
    }
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Bytes(string text)
    {
        await Task.Yield();
        foreach (var value in Encoding.UTF8.GetBytes(text)) yield return new byte[] { value };
    }
}
