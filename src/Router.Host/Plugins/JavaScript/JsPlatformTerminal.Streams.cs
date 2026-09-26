using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Infrastructure.Services;
using static Router.Host.Plugins.JavaScript.JsPluginCodec;

namespace Router.Host.Plugins.JavaScript;

internal sealed partial class JsPlatformTerminal
{
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadJsRawAsync(
        JsInvocation invocation,
        JsHttpSource source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, invocation.Token);
        await using var stream = await source.OpenStreamAsync(linked.Token);
        await foreach (var chunk in source.ReadRawAsync(stream, linked.Token)) yield return chunk;
    }

    private static async IAsyncEnumerable<StreamChunk> ReadMappedStreamAsync(
        JsInvocation invocation,
        JsHttpSource source,
        JsStreamMapperManifest mapper,
        JsonNode? state,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, invocation.Token);
        await using var stream = await source.OpenStreamAsync(linked.Token);
        var endReason = "eof";
        string? finish = null;
        await foreach (var frame in SseFrameReader.ReadAsync(stream, idleTimeout: source.ReadIdleTimeout, cancellationToken: linked.Token))
        {
            if (frame.IsComment) { yield return default; continue; }
            var batch = MapBatch(invocation, mapper.Event, frame, state);
            state = batch.State;
            foreach (var chunk in batch.Chunks)
            {
                finish = chunk.FinishReason ?? finish;
                var output = chunk with { FinishReason = null };
                if (output != default) yield return output;
                if (chunk.Error is not null) yield break;
            }
            if (batch.Done) { endReason = "protocol"; break; }
        }

        var ending = MapBatch(invocation, mapper.End, new { reason = endReason }, state);
        foreach (var chunk in ending.Chunks)
        {
            finish = chunk.FinishReason ?? finish;
            var output = chunk with { FinishReason = null };
            if (output != default) yield return output;
            if (chunk.Error is not null) yield break;
        }
        if (finish is null)
            yield return new StreamChunk(null, Error: "Upstream stream ended without a finish reason.", ErrorType: "upstream_incomplete_response");
        else
            yield return new StreamChunk(null, finish);
    }

    private static MappedBatch MapBatch(JsInvocation invocation, string handler, object frame, JsonNode? state)
    {
        try
        {
            var mapped = invocation.Map(handler, frame, state);
            if (!mapped.ContainsKey("state") || mapped["chunks"] is not JsonArray)
                throw new InvalidOperationException("A mapper must return state and chunks.");
            var nextState = mapped["state"];
            mapped.Remove("state");
            if (Encoding.UTF8.GetByteCount(nextState?.ToJsonString(JsonOptions) ?? "null") > 512 * 1024)
                throw new InvalidOperationException("Stream state exceeds its size limit.");
            var chunks = mapped["chunks"]!.Deserialize<StreamChunk[]>(JsonOptions)!;
            if (chunks.Length > 128) throw new InvalidOperationException("Too many chunks from one mapper event.");
            foreach (var chunk in chunks)
            {
                ValidateUsage(chunk.Usage);
                if (chunk.ToolCalls is { } tools && (tools.Count > 128 || tools.Any(tool => tool.Index < 0 || tool.Index > 1023)))
                    throw new InvalidOperationException("Invalid tool call indices.");
            }
            return new MappedBatch(nextState, chunks, mapped["done"]?.GetValue<bool>() == true);
        }
        catch (Exception exception)
        {
            invocation.Context.Token.ThrowIfCancellationRequested();
            return new MappedBatch(null,
                [new StreamChunk(null, Error: SafeError(exception, invocation.Context.Attempt?.Account), ErrorType: "plugin_mapping_error")], true);
        }
    }

    private static async Task<AdapterCompletion> AggregateAsync(
        IAsyncEnumerable<StreamChunk> chunks, string model, CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var signature = new StringBuilder();
        var tools = new SortedDictionary<int, AggregatedTool>();
        Usage? usage = null;
        string? finish = null;
        var bytes = 0;
        void Append(StringBuilder builder, string? value)
        {
            if (value is null) return;
            bytes += Encoding.UTF8.GetByteCount(value);
            if (bytes > 32 * 1024 * 1024) throw new InvalidOperationException("Aggregated completion exceeds its size limit.");
            builder.Append(value);
        }
        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            if (chunk.Error is { } error) throw new InvalidOperationException(error);
            Append(content, chunk.Delta);
            Append(reasoning, chunk.ReasoningDelta);
            Append(signature, chunk.ReasoningSignature);
            usage = chunk.Usage ?? usage;
            finish = chunk.FinishReason ?? finish;
            foreach (var call in chunk.ToolCalls ?? [])
            {
                if (!tools.TryGetValue(call.Index, out var tool)) tools[call.Index] = tool = new AggregatedTool();
                tool.Id = call.Id ?? tool.Id;
                tool.Name = call.Name ?? tool.Name;
                Append(tool.Arguments, call.Arguments);
            }
        }
        if (finish is null) throw new InvalidOperationException("Missing completion finish reason.");
        return new AdapterCompletion(model, content.Length == 0 ? null : content.ToString(), finish, usage,
            tools.Select(pair => new AdapterToolCall(pair.Key, pair.Value.Id, pair.Value.Name, pair.Value.Arguments.ToString())).ToArray(),
            reasoning.Length == 0 ? null : reasoning.ToString(), signature.Length == 0 ? null : signature.ToString());
    }

    private static async IAsyncEnumerable<StreamChunk> CompletionChunks(AdapterCompletion completion)
    {
        await Task.CompletedTask;
        yield return new StreamChunk(null, Role: "assistant");
        if (!string.IsNullOrEmpty(completion.ReasoningContent)) yield return new StreamChunk(null, ReasoningDelta: completion.ReasoningContent);
        if (!string.IsNullOrEmpty(completion.ReasoningSignature)) yield return new StreamChunk(null, ReasoningSignature: completion.ReasoningSignature);
        if (completion.Content is { } content) yield return new StreamChunk(content);
        if (completion.ToolCalls is { Count: > 0 } calls)
            yield return new StreamChunk(null, ToolCalls: calls.Select(call => new ToolCallDelta(call.Index, call.Id, "function", call.Name, call.Arguments)).ToArray());
        if (completion.Usage is { } usage) yield return new StreamChunk(null, Usage: usage);
        yield return new StreamChunk(null, completion.FinishReason);
    }

    private static void ValidateCompletion(AdapterCompletion completion)
    {
        ValidateUsage(completion.Usage);
        if (string.IsNullOrEmpty(completion.FinishReason)) throw new InvalidOperationException("A completion needs a finish reason.");
    }

    private static void ValidateUsage(Usage? usage)
    {
        if (usage is not null && (usage.PromptTokens < 0 || usage.CompletionTokens < 0 || usage.TotalTokens < 0))
            throw new InvalidOperationException("Usage cannot be negative.");
    }

    private sealed record MappedBatch(JsonNode? State, StreamChunk[] Chunks, bool Done);
    private sealed class AggregatedTool
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
