using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Router.Contracts.Domain;
using Router.Host.Api;

namespace Router.Tests;

[TestClass]
public sealed class ProtocolConversionOutputTests
{
    [TestMethod]
    [DataRow("tool_calls", "tool_calls")]
    [DataRow("function_call", "tool_calls")]
    [DataRow("stop", "tool_calls")]
    [DataRow("end_turn", "tool_calls")]
    [DataRow("completed", "tool_calls")]
    [DataRow("length", "length")]
    [DataRow("max_tokens", "length")]
    [DataRow("content_filter", "content_filter")]
    public async Task ChatStreamRemembersEarlierToolsWithoutReplayingDeltas(string finishReason, string expected)
    {
        var result = await WriteAsync(TestEndpoint.ChatCompletions, Streaming(
            new StreamChunk(null, Role: "assistant", ToolCalls: [new ToolCallDelta(0, "call_lookup", "function", "lookup", "{\"q\":")]),
            new StreamChunk(null, ToolCalls: [new ToolCallDelta(0, Arguments: "\"value\"}")]),
            new StreamChunk(null, FinishReason: finishReason),
            new StreamChunk(null, Usage: new Usage(2, 3, 5))));

        result.Status.Should().Be(200, "wire response: {0}", result.Body);
        result.ContentType.Should().Be("text/event-stream");
        result.Body.Should().EndWith("data: [DONE]\n\n");
        var events = ReadEvents(result.Body);
        events.Should().HaveCount(4);
        var deltas = events.Take(3).Select(e => e.Data["choices"]![0]!["delta"]!).ToArray();
        deltas[0]["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>().Should().Be("{\"q\":");
        deltas[1]["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>().Should().Be("\"value\"}");
        deltas[2]["tool_calls"].Should().BeNull("the finish chunk must not replay tool deltas");
        events[2].Data["choices"]![0]!["finish_reason"]!.GetValue<string>().Should().Be(expected);
        events[3].Data["choices"]!.AsArray().Should().BeEmpty();
        events[3].Data["usage"]!["total_tokens"]!.GetValue<int>().Should().Be(5);
    }

    [TestMethod]
    public async Task ChatStreamWithoutToolsKeepsStopFinishReason()
    {
        var result = await WriteAsync(TestEndpoint.ChatCompletions, Streaming(
            new StreamChunk("hello"), new StreamChunk(null, FinishReason: "stop")));

        ReadEvents(result.Body).Last().Data["choices"]![0]!["finish_reason"]!.GetValue<string>().Should().Be("stop");
    }

    [TestMethod]
    [DataRow("length")]
    [DataRow("max_tokens")]
    public async Task AnthropicStreamRetainsPartialToolJsonAndTokenLimit(string finishReason)
    {
        var result = await WriteAsync(TestEndpoint.AnthropicMessages, Streaming(
            new StreamChunk(null, ToolCalls: [new ToolCallDelta(0, "call_lookup", "function", "lookup", "{\"q\":")]),
            new StreamChunk(null, ToolCalls: [new ToolCallDelta(0, Arguments: "\"unfinished")]),
            new StreamChunk(null, FinishReason: finishReason, Usage: new Usage(2, 3, 5))));

        var events = ReadEvents(result.Body);
        var start = events.Single(e => e.Name == "content_block_start").Data["content_block"]!;
        start["type"]!.GetValue<string>().Should().Be("tool_use");
        start["caller"]!["type"]!.GetValue<string>().Should().Be("direct");
        start["input"]!.AsObject().Count.Should().Be(0, "Anthropic streaming starts tool input with its standard empty placeholder");
        var partialJson = string.Concat(events.Where(e => e.Name == "content_block_delta")
            .Select(e => e.Data["delta"]!["partial_json"]!.GetValue<string>()));
        partialJson.Should().Be("{\"q\":\"unfinished", "truncated arguments must remain partial rather than becoming executable empty arguments");
        events.Count(e => e.Name == "content_block_stop").Should().Be(1);
        var finalDelta = events.Single(e => e.Name == "message_delta").Data;
        finalDelta["delta"]!["stop_reason"]!.GetValue<string>().Should().Be("max_tokens");
        finalDelta["usage"]!["output_tokens"]!.GetValue<int>().Should().Be(3);
        events.Last().Name.Should().Be("message_stop");
    }

    [TestMethod]
    [DataRow("length", "{\"q\":")]
    [DataRow("max_tokens", "{\"q\":")]
    [DataRow("length", "")]
    [DataRow("length", null)]
    [DataRow("length", "[]")]
    [DataRow("length", "null")]
    public async Task AnthropicNonstreamOmitsIncompleteToolsAndRetainsValidTools(string finishReason, string? arguments)
    {
        var result = await WriteAsync(TestEndpoint.AnthropicMessages, Completed(
            finishReason,
            "partial answer",
            [new AdapterToolCall(0, "call_incomplete", "lookup", arguments),
             new AdapterToolCall(1, "call_valid", "lookup", "{\"q\":\"value\"}")]));

        result.Status.Should().Be(200, "wire response: {0}", result.Body);
        var root = JsonNode.Parse(result.Body)!;
        root["stop_reason"]!.GetValue<string>().Should().Be("max_tokens");
        var content = root["content"]!.AsArray();
        content.Should().HaveCount(2);
        content[0]!["text"]!.GetValue<string>().Should().Be("partial answer");
        content[1]!["id"]!.GetValue<string>().Should().Be("call_valid");
        content[1]!["caller"]!["type"]!.GetValue<string>().Should().Be("direct");
        content[1]!["input"]!["q"]!.GetValue<string>().Should().Be("value");
    }

    [TestMethod]
    public async Task AnthropicNonstreamWithOnlyTruncatedToolDoesNotCreateAnExecutableCall()
    {
        var result = await WriteAsync(TestEndpoint.AnthropicMessages, Completed(
            "length", null, [new AdapterToolCall(0, "call_incomplete", "lookup", "{\"q\":")]));

        result.Status.Should().Be(200, "wire response: {0}", result.Body);
        var root = JsonNode.Parse(result.Body)!;
        root["stop_reason"]!.GetValue<string>().Should().Be("max_tokens");
        root["content"]!.AsArray().Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"q\":\"value\"}")]
    public async Task AnthropicValidToolsKeepToolUseStopReasonInBothModes(string arguments)
    {
        var result = await WriteAsync(TestEndpoint.AnthropicMessages, Completed(
            "stop", null, [new AdapterToolCall(0, "call_lookup", "lookup", arguments)]));
        result.Status.Should().Be(200, "wire response: {0}", result.Body);
        var root = JsonNode.Parse(result.Body)!;
        root["stop_reason"]!.GetValue<string>().Should().Be("tool_use");
        root["content"]![0]!["input"]!.ToJsonString().Should().Be(arguments);
        root["content"]![0]!["caller"]!["type"]!.GetValue<string>().Should().Be("direct");

        var stream = await WriteAsync(TestEndpoint.AnthropicMessages, Streaming(
            new StreamChunk(null, ToolCalls: [new ToolCallDelta(0, "call_lookup", "function", "lookup", arguments)]),
            new StreamChunk(null, FinishReason: "stop")));
        ReadEvents(stream.Body).Single(e => e.Name == "message_delta").Data["delta"]!["stop_reason"]!
            .GetValue<string>().Should().Be("tool_use");
    }

    [TestMethod]
    [DataRow("{\"q\":")]
    [DataRow("[]")]
    [DataRow(null)]
    public async Task AnthropicNontruncatedMalformedToolReturnsProtocolError(string? arguments)
    {
        var result = await WriteAsync(TestEndpoint.AnthropicMessages, Completed(
            "tool_calls", null, [new AdapterToolCall(0, "call_invalid", "lookup", arguments)]));

        result.Status.Should().Be(502);
        var root = JsonNode.Parse(result.Body)!;
        root["type"]!.GetValue<string>().Should().Be("error");
        root["error"]!["type"]!.GetValue<string>().Should().Be("api_error");
        root["content"].Should().BeNull();
    }

    [TestMethod]
    [DataRow("stop", "completed", null)]
    [DataRow("completed", "completed", null)]
    [DataRow("tool_calls", "completed", null)]
    [DataRow("length", "incomplete", "max_output_tokens")]
    [DataRow("max_tokens", "incomplete", "max_output_tokens")]
    [DataRow("content_filter", "incomplete", "content_filter")]
    public async Task ResponsesStreamAndNonstreamAgreeOnTerminalState(string finishReason, string status, string? reason)
    {
        foreach (var withTool in new[] { false, true })
        {
            var tools = withTool ? new[] { new AdapterToolCall(0, "call_lookup", "lookup", "{\"q\":") } : null;
            var completed = await WriteAsync(TestEndpoint.Responses, Completed(finishReason, "partial answer", tools));
            completed.Status.Should().Be(200);
            var root = JsonNode.Parse(completed.Body)!;
            AssertResponsesState(root, status, reason);

            var streamed = await WriteAsync(TestEndpoint.Responses, Streaming(
                new StreamChunk("partial answer"),
                withTool ? new StreamChunk(null, ToolCalls: [new ToolCallDelta(0, "call_lookup", "function", "lookup", "{\"q\":")]) : default,
                new StreamChunk(null, FinishReason: finishReason),
                new StreamChunk(null, Usage: new Usage(2, 3, 5))));
            var events = ReadEvents(streamed.Body);
            events.First().Name.Should().Be("response.created");
            events.Last().Name.Should().Be($"response.{status}");
            var streamedRoot = events.Last().Data["response"]!;
            AssertResponsesState(streamedRoot, status, reason);
            streamedRoot["usage"]!["total_tokens"]!.GetValue<int>().Should().Be(5);
            foreach (var item in events.Where(e => e.Name == "response.output_item.done"))
                item.Data["item"]!["status"]!.GetValue<string>().Should().Be(status);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyStreamEmitsErrorAndStopsReading(bool emitTextFirst)
    {
        var disposed = false;
        async IAsyncEnumerable<StreamChunk> Chunks()
        {
            await Task.CompletedTask;
            try
            {
                if (emitTextFirst) yield return new StreamChunk("before error");
                yield return new StreamChunk(null, Error: "upstream limited", ErrorType: "rate_limit_error");
                throw new InvalidOperationException("writer advanced past the error chunk");
            }
            finally
            {
                disposed = true;
            }
        }

        var result = await WriteAsync(TestEndpoint.Completions, new AdapterResponse
        {
            StatusCode = 200, IsStreaming = true, Stream = Chunks()
        });

        disposed.Should().BeTrue();
        result.Status.Should().Be(200, "wire response: {0}", result.Body);
        result.ContentType.Should().Be("text/event-stream");
        result.Body.Should().EndWith("data: [DONE]\n\n");
        var events = ReadEvents(result.Body);
        events.Should().HaveCount(emitTextFirst ? 2 : 1);
        var error = events.Last().Data;
        error["error"]!["message"]!.GetValue<string>().Should().Be("upstream limited");
        error["error"]!["type"]!.GetValue<string>().Should().Be("rate_limit_error");
        error["choices"].Should().BeNull();
        if (emitTextFirst)
            events[0].Data["choices"]![0]!["text"]!.GetValue<string>().Should().Be("before error");
    }

    private static void AssertResponsesState(JsonNode root, string status, string? reason)
    {
        root["status"]!.GetValue<string>().Should().Be(status);
        if (reason is null) root["incomplete_details"].Should().BeNull();
        else root["incomplete_details"]!["reason"]!.GetValue<string>().Should().Be(reason);
        foreach (var item in root["output"]!.AsArray())
            item!["status"]!.GetValue<string>().Should().Be(status);
    }

    private static AdapterResponse Completed(string finishReason, string? text, IReadOnlyList<AdapterToolCall>? tools = null)
        => new()
        {
            StatusCode = 200,
            Completion = new AdapterCompletion("model", text, finishReason, new Usage(2, 3, 5), tools)
        };

    private static AdapterResponse Streaming(params StreamChunk[] chunks)
        => new() { StatusCode = 200, IsStreaming = true, Stream = Chunks(chunks) };

    private static async IAsyncEnumerable<StreamChunk> Chunks(StreamChunk[] chunks)
    {
        await Task.CompletedTask;
        foreach (var chunk in chunks) yield return chunk;
    }

    private static async Task<(int Status, string? ContentType, string Body)> WriteAsync(TestEndpoint endpoint, AdapterResponse response)
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await ProtocolResponseWriter.WriteAsync(context, response, endpoint, "fallback", CancellationToken.None);
        return (context.Response.StatusCode, context.Response.ContentType, Encoding.UTF8.GetString(body.ToArray()));
    }

    private static List<(string? Name, JsonNode Data)> ReadEvents(string body)
    {
        var result = new List<(string? Name, JsonNode Data)>();
        foreach (var frame in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = frame.Split('\n');
            var data = lines.SingleOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal));
            if (data is null || data == "data: [DONE]") continue;
            var name = lines.SingleOrDefault(line => line.StartsWith("event: ", StringComparison.Ordinal));
            result.Add((name?[7..], JsonNode.Parse(data[6..])!));
        }
        return result;
    }
}
