using System.Runtime.CompilerServices;
using Router.Contracts.Domain;

namespace Router.Infrastructure.Services.Privacy;

/// <summary>在当前响应所有者之外做文本变换，不二次读取流、不新增上游请求或成功完成点。</summary>
public static class PrivacyResponseRestorer
{
    public static AdapterResponse Restore(AdapterResponse response, IReadOnlyDictionary<string, string> map, string endpoint)
    {
        if (map.Count == 0) return response;
        var raw = response.IsRawPassthrough ? new PrivacyRawResponseRestorer(map, endpoint) : null;
        return new AdapterResponse
        {
            StatusCode = response.StatusCode, IsStreaming = response.IsStreaming, ErrorType = response.ErrorType,
            Error = response.Error is { } error ? PlaceholderRestorer.Restore(error, map) : null,
            Completion = response.Completion is { } completion ? completion with
            {
                Content = completion.Content is { } content ? PlaceholderRestorer.Restore(content, map) : null,
                ReasoningContent = completion.ReasoningContent is { } reasoning ? PlaceholderRestorer.Restore(reasoning, map) : null,
                ToolCalls = completion.ToolCalls?.Select(call => call with
                {
                    Arguments = call.Arguments is { } arguments ? PlaceholderRestorer.Restore(arguments, map, jsonString: true) : null
                }).ToArray()
            } : null,
            Usage = response.Usage, IsRawPassthrough = response.IsRawPassthrough, ContentType = response.ContentType,
            Lifetime = response.Lifetime,
            Stream = response.Stream is { } stream ? RestoreStream(stream, map) : null,
            RawContent = response.RawContent is { Length: > 0 } bytes && raw is not null ? raw.RestoreContent(bytes, response.ContentType) : response.RawContent,
            RawStream = response.RawStream is { } rawStream && raw is not null ? raw.RestoreStream(rawStream, response.ContentType) : response.RawStream
        };
    }

    private static async IAsyncEnumerable<StreamChunk> RestoreStream(IAsyncEnumerable<StreamChunk> source,
        IReadOnlyDictionary<string, string> map, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = new PlaceholderRestorer(map);
        var reasoning = new PlaceholderRestorer(map);
        var tools = new Dictionary<int, PlaceholderRestorer>();
        await foreach (var chunk in source.WithCancellation(cancellationToken))
        {
            // 结束/错误元数据前输出尾部，不在 finish_reason 或 usage 后偷偷追加内容。
            var restored = chunk with
            {
                Delta = chunk.Delta is { } delta ? text.Push(delta) : null,
                ReasoningDelta = chunk.ReasoningDelta is { } thought ? reasoning.Push(thought) : null,
                ToolCalls = chunk.ToolCalls?.Select(call =>
                {
                    if (!tools.TryGetValue(call.Index, out var restorer))
                        tools.Add(call.Index, restorer = new PlaceholderRestorer(map, jsonString: true));
                    return call with { Arguments = call.Arguments is { } arguments ? restorer.Push(arguments) : null };
                }).ToArray(),
                Error = chunk.Error is { } error ? PlaceholderRestorer.Restore(error, map) : null
            };
            if (chunk.FinishReason is not null || chunk.Error is not null)
            {
                if (restored.Delta?.Length > 0 || restored.ReasoningDelta?.Length > 0 || restored.ToolCalls?.Count > 0)
                    yield return restored with { FinishReason = null, Usage = null, Error = null, ErrorType = null };
                foreach (var tail in Flush()) yield return tail;
                yield return restored with { Delta = null, ReasoningDelta = null, ToolCalls = null };
            }
            else yield return restored;
        }
        foreach (var tail in Flush()) yield return tail;

        IEnumerable<StreamChunk> Flush()
        {
            var content = text.Flush();
            var thought = reasoning.Flush();
            if (content.Length > 0 || thought.Length > 0)
                yield return new(content.Length == 0 ? null : content, ReasoningDelta: thought.Length == 0 ? null : thought);
            foreach (var (index, restorer) in tools)
                if (restorer.Flush() is { Length: > 0 } arguments)
                    yield return new(null, ToolCalls: [new(index, Arguments: arguments)]);
        }
    }
}
