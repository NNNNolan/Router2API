using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;
using Router.Contracts.Domain;

namespace Router.Infrastructure.Services;

/// <summary>宿主统计的来源与完整性，不扩展公开插件契约。</summary>
public sealed record UsageMetadata(string Source, string InputSource, string OutputSource, bool IsPartial, string? Algorithm, string? Warning = null, string? TotalSource = null);

/// <summary>一次调用的最终用量；日志、聚合和实时指标共用此结果。</summary>
public sealed record UsageMeasurement(Usage? Usage, UsageMetadata Metadata);

/// <summary>只观察当前响应，不重读流、不保留完整输出；在原生命周期完成点结算。</summary>
public sealed class RequestUsageTracker
{
    private static readonly Lazy<TiktokenTokenizer> Cl100k = new(() => TiktokenTokenizer.CreateForEncoding("cl100k_base"));
    private static readonly Lazy<TiktokenTokenizer> O200k = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));
    private readonly AdapterRequest _request;
    private readonly bool _eligible;
    private readonly bool _streaming;
    private readonly string? _encoding;
    private readonly UnicodeTokenEstimate _output = new();
    private readonly HashSet<string> _toolNames = new(StringComparer.Ordinal);
    private readonly Decoder? _decoder;
    private readonly SseFrameParser? _parser;
    private readonly char[]? _characters;
    private Usage? _explicit;
    private int? _input;
    private int? _completion;
    private int? _total;
    private int? _cachedRead;
    private int? _cachedWrite;
    private int? _thoughts;
    private bool _partial;
    private bool _rawDisabled;
    private bool _sawOutput;
    private long _nonStreamingTokens;
    private string? _warning;
    private UsageMeasurement? _result;

    public RequestUsageTracker(AdapterRequest request, AdapterResponse response)
    {
        _request = request;
        _eligible = response.IsSuccess;
        _streaming = PluginResponseLifetime.HasStream(response);
        _encoding = ResolveEncoding(request.Model);
        ObserveUsage(response.Usage);
        if (response.Completion is { } completion)
        {
            _sawOutput = true;
            Add(completion.Content);
            Add(completion.ReasoningContent);
            foreach (var tool in completion.ToolCalls ?? [])
            {
                AddToolName(tool.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), tool.Name);
                Add(tool.Arguments);
            }
        }
        if (response.IsRawPassthrough && _explicit is null)
        {
            if (response.RawContent is { Length: > 0 } content)
                ObserveJson(content);
            if (response.RawStream is not null)
            {
                if (response.ContentType is null || response.ContentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    _decoder = new UTF8Encoding(false, true).GetDecoder();
                    _parser = new SseFrameParser(1024 * 1024);
                    _characters = new char[4096];
                }
                else SetWarning("Raw response is not a supported SSE stream.");
            }
        }
    }

    public void Observe(StreamChunk chunk)
    {
        ObserveUsage(chunk.Usage);
        if (chunk == default) return;
        _sawOutput |= chunk.Delta is not null || chunk.ReasoningDelta is not null || chunk.ToolCalls is not null || chunk.FinishReason is not null;
        Add(chunk.Delta);
        Add(chunk.ReasoningDelta);
        foreach (var tool in chunk.ToolCalls ?? [])
        {
            AddToolName(tool.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), tool.Name);
            Add(tool.Arguments);
        }
    }

    public void ObserveRaw(ReadOnlySpan<byte> bytes)
    {
        if (_decoder is null || _rawDisabled || _explicit is not null) return;
        try
        {
            while (!bytes.IsEmpty)
            {
                _decoder.Convert(bytes, _characters, false, out var used, out var written, out _);
                bytes = bytes[used..];
                for (var index = 0; index < written; index++)
                    if (_parser!.Feed(_characters![index]) is { IsComment: false } frame && frame.Data != "[DONE]" && frame.Data.Length > 0)
                        ObserveJson(Encoding.UTF8.GetBytes(frame.Data));
            }
        }
        catch (Exception exception) when (exception is DecoderFallbackException or InvalidDataException)
        {
            _rawDisabled = true;
            SetWarning(exception is DecoderFallbackException ? "Invalid UTF-8 in usage stream." : "Usage SSE frame exceeds 1 MiB.");
        }
    }

    public UsageMeasurement Finish(bool complete)
    {
        if (_result is not null) return _result;
        if (_decoder is not null && !_rawDisabled)
        {
            try { _decoder.GetChars(ReadOnlySpan<byte>.Empty, _characters, true); }
            catch (DecoderFallbackException) { SetWarning("Truncated UTF-8 in usage stream."); }
            if (_parser!.HasPendingData) SetWarning("Unterminated usage SSE frame.");
        }
        if (_explicit is { } explicitUsage)
            return _result = new(explicitUsage, new("plugin", "plugin", "plugin", !complete, null, _warning, "plugin"));
        if (!_eligible && _input is null && _completion is null && _total is null)
            return _result = new(null, new("unknown", "unknown", "unknown", true, null, _warning));

        var reportedInput = _input.HasValue ? SumCounts(_input.Value, _cachedRead ?? 0, _cachedWrite ?? 0) : null;
        var reportedOutput = _completion.HasValue ? SumCounts(_completion.Value, _thoughts ?? 0) : null;
        var input = reportedInput ?? CountRequest();
        var output = reportedOutput ?? (_streaming ? _output.Count : (int)Math.Min(_nonStreamingTokens, int.MaxValue));
        var inputSource = reportedInput.HasValue ? "response" : "estimated";
        var outputSource = reportedOutput.HasValue ? "response" : "estimated";
        var totalSource = _total.HasValue ? "response" : inputSource == outputSource ? inputSource : "mixed";
        var source = inputSource == outputSource && inputSource == totalSource ? inputSource : "mixed";
        var total = _total ?? (int)Math.Min((long)input + output, int.MaxValue);
        var partial = !complete || _warning is not null || ((!reportedInput.HasValue || !reportedOutput.HasValue) && (_partial || !_sawOutput));
        return _result = new(new Usage(input, output, total),
            new(source, inputSource, outputSource, partial,
                source is "response" ? null : _encoding is null ? "unicode-v1" : $"tiktoken-{_encoding}-unicode-v1", _warning, totalSource));
    }

    private void ObserveUsage(Usage? usage)
    {
        if (usage is null) return;
        if (usage.PromptTokens < 0 || usage.CompletionTokens < 0 || usage.TotalTokens < 0)
        {
            SetWarning("Plugin returned invalid negative usage.");
            return;
        }
        _explicit = usage;
    }

    private void ObserveJson(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            ReadUsage(root);
            if (Property(root, "message") is { ValueKind: JsonValueKind.Object } message) ReadUsage(message);
            if (Property(root, "response") is { ValueKind: JsonValueKind.Object } response) ReadUsage(response);
            ReadOutput(root);
        }
        catch (JsonException) { SetWarning("Response contains an invalid usage JSON event."); }
    }

    private void ReadUsage(JsonElement root)
    {
        var usage = Property(root, "usage");
        if (usage is { ValueKind: JsonValueKind.Object } value)
        {
            // 各字段都是累计快照，可能分阶段出现；缓存明细不能随下一帧缺省而丢失。
            _input = ReadCount(value, "prompt_tokens") ?? ReadCount(value, "input_tokens") ?? _input;
            _cachedRead = ReadCount(value, "cache_read_input_tokens") ?? _cachedRead;
            _cachedWrite = ReadCount(value, "cache_creation_input_tokens") ?? _cachedWrite;
            _completion = ReadCount(value, "completion_tokens") ?? ReadCount(value, "output_tokens") ?? _completion;
            _total = ReadCount(value, "total_tokens") ?? _total;
        }
        if (Property(root, "usageMetadata") is { ValueKind: JsonValueKind.Object } gemini)
        {
            _input = ReadCount(gemini, "promptTokenCount") ?? _input;
            _completion = ReadCount(gemini, "candidatesTokenCount") ?? _completion;
            _thoughts = ReadCount(gemini, "thoughtsTokenCount") ?? _thoughts;
            _total = ReadCount(gemini, "totalTokenCount") ?? _total;
        }
    }

    private int? ReadCount(JsonElement value, string name)
    {
        var field = Property(value, name);
        if (field is null || field.Value.ValueKind == JsonValueKind.Null) return null;
        if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out var count) && count >= 0) return count;
        SetWarning("Response contains an invalid token count.");
        return null;
    }

    private int? SumCounts(params int[] values)
    {
        var sum = values.Sum(value => (long)value);
        if (sum <= int.MaxValue) return (int)sum;
        SetWarning("Response token count exceeds Int32.");
        return null;
    }

    private void ReadOutput(JsonElement root)
    {
        if (Property(root, "choices") is { ValueKind: JsonValueKind.Array } choices)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                var payload = Property(choice, "delta") ?? Property(choice, "message");
                if (payload is { } content) ReadMessage(content);
                if (Property(choice, "text") is { ValueKind: JsonValueKind.String } text) { Add(text.GetString()); _sawOutput = true; }
                if (Property(choice, "finish_reason") is { ValueKind: JsonValueKind.String }) _sawOutput = true;
            }
        }
        var type = Property(root, "type") is { ValueKind: JsonValueKind.String } typeValue ? typeValue.GetString() : null;
        if (type is "response.output_text.delta" or "response.reasoning_summary_text.delta" or "response.reasoning_text.delta" or "response.function_call_arguments.delta")
        {
            AddString(root, "delta");
            _sawOutput = true;
        }
        if (type is "content_block_delta" && Property(root, "delta") is { } delta)
        {
            AddString(delta, "text");
            AddString(delta, "thinking");
            AddString(delta, "partial_json");
            _sawOutput = true;
        }
        if (type is "content_block_start" && Property(root, "content_block") is { } block)
        {
            AddString(block, "name");
            ReadContent(block);
        }
        if (type is "response.output_item.added" && Property(root, "item") is { } added)
            AddString(added, "name");
        if (type is "response.completed" or "message_stop") _sawOutput = true;
        if (!_streaming)
        {
            ReadMessage(root);
            if (Property(root, "output") is { ValueKind: JsonValueKind.Array } output)
                foreach (var item in output.EnumerateArray())
                {
                    ReadMessage(item);
                    ReadContent(item);
                    AddString(item, "name");
                    AddString(item, "arguments");
                    if (Property(item, "summary") is { ValueKind: JsonValueKind.Array } summary)
                        foreach (var part in summary.EnumerateArray()) ReadContent(part);
                }
        }
        if (Property(root, "candidates") is { ValueKind: JsonValueKind.Array } candidates)
            foreach (var candidate in candidates.EnumerateArray())
                if (Property(candidate, "content") is { } content && Property(content, "parts") is { ValueKind: JsonValueKind.Array } parts)
                    foreach (var part in parts.EnumerateArray()) ReadContent(part);
    }

    private void ReadMessage(JsonElement message)
    {
        if (Property(message, "audio") is { ValueKind: not JsonValueKind.Null }) _partial = true;
        if (Property(message, "content") is { } content)
        {
            _sawOutput = true;
            if (content.ValueKind == JsonValueKind.String) Add(content.GetString());
            else if (content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray()) ReadContent(part);
        }
        AddString(message, "reasoning_content");
        if (Property(message, "tool_calls") is { ValueKind: JsonValueKind.Array } tools)
        {
            _sawOutput = true;
            var position = 0;
            foreach (var tool in tools.EnumerateArray())
            {
                if (Property(tool, "function") is { } function)
                {
                    var index = Property(tool, "index")?.ToString() ?? position.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    AddToolName(index, Property(function, "name") is { ValueKind: JsonValueKind.String } name ? name.GetString() : null);
                    AddString(function, "arguments");
                }
                position++;
            }
        }
    }

    private void ReadContent(JsonElement part)
    {
        AddString(part, "text");
        AddString(part, "thinking");
        if (Property(part, "type") is { ValueKind: JsonValueKind.String } type
            && type.GetString() is "image" or "image_url" or "audio" or "video" or "file" or "image_generation_call" or "output_audio") _partial = true;
        if (Property(part, "functionCall") is { } function)
        {
            AddString(function, "name");
            if (Property(function, "args") is { } arguments) Add(arguments.GetRawText());
        }
        if (Property(part, "type") is { ValueKind: JsonValueKind.String } kind && kind.GetString() is "tool_use" or "server_tool_use")
        {
            // 流式 start 的 input 通常只是占位 {}，参数随后由 partial_json 上报。
            if (!_streaming)
            {
                AddString(part, "name");
                if (Property(part, "input") is { } input) Add(input.GetRawText());
            }
        }
        if (Property(part, "inlineData") is not null || Property(part, "fileData") is not null) _partial = true;
        _sawOutput = true;
    }

    private void AddString(JsonElement root, string name)
    {
        if (Property(root, name) is { ValueKind: JsonValueKind.String } value) Add(value.GetString());
    }

    private void Add(string? text)
    {
        if (text is null || _explicit is not null) return;
        if (_streaming) _output.Add(text);
        else _nonStreamingTokens += CountText(text);
    }

    private void AddToolName(string index, string? name)
    {
        if (name is null) return;
        if (_toolNames.Count >= 1024) { SetWarning("Too many tool names for complete usage estimation."); return; }
        if (_toolNames.Add(index + ":" + name)) Add(name);
    }

    private int CountRequest()
    {
        long tokens = 3;
        foreach (var message in _request.Messages)
        {
            tokens += 3 + CountText(message.Role) + CountText(message.Name);
            if (message.ContentParts is { Count: > 0 } parts)
            {
                foreach (var part in parts)
                    if (part.Kind == AdapterContentPartKind.Text) tokens += CountText(part.Text);
                    else _partial = true;
            }
            else tokens += CountText(message.Content);
            if (message.ToolCalls is { } calls) tokens += CountText(JsonSerializer.Serialize(calls));
            if ((message.ReasoningContent ?? message.Reasoning) is { } reasoning) tokens += CountText(reasoning.ToString());
        }
        if (_request.Tools.Count > 0) tokens += CountText(JsonSerializer.Serialize(_request.Tools));
        if (_request.Messages.Count == 0 && _request.OriginalBody is { } body)
        {
            if (Property(body, "input") is { } input) tokens += CountText(input.ToString());
            else _partial = true;
        }
        return (int)Math.Min(tokens, int.MaxValue);
    }

    private int CountText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (_encoding is not null)
            return (_encoding == "cl100k_base" ? Cl100k.Value : O200k.Value).CountTokens(text);
        var estimate = new UnicodeTokenEstimate();
        estimate.Add(text);
        return estimate.Count;
    }

    private static string? ResolveEncoding(string value)
    {
        var model = value.Split('/').Last().ToLowerInvariant();
        if (model is "gpt-4" or "gpt-3.5-turbo" || model.StartsWith("gpt-4-", StringComparison.Ordinal)
            || model.StartsWith("gpt-3.5-turbo-", StringComparison.Ordinal)) return "cl100k_base";
        foreach (var family in new[] { "gpt-4o", "chatgpt-4o", "gpt-4.1", "gpt-4.5", "gpt-5", "o1", "o3", "o4" })
            if (model == family || model.StartsWith(family + "-", StringComparison.Ordinal)) return "o200k_base";
        return null;
    }

    private void SetWarning(string message)
    {
        _partial = true;
        _warning ??= message;
    }

    private static JsonElement? Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : null;
}

internal sealed class UnicodeTokenEstimate
{
    private long _quarters;
    private char _pendingSurrogate;
    public int Count => (int)Math.Min((_quarters + (_pendingSurrogate == '\0' ? 0 : 8) + 3) / 4, int.MaxValue);

    public void Add(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (_pendingSurrogate != '\0')
            {
                _quarters += 8;
                _pendingSurrogate = '\0';
                if (char.IsLowSurrogate(character)) continue;
            }
            if (char.IsHighSurrogate(character)) { _pendingSurrogate = character; continue; }
            _quarters += character switch
            {
                >= '\u2e80' and <= '\u9fff' or >= '\uac00' and <= '\ud7af' => 6,
                <= '\u007f' => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) ? 1 : 2,
                _ => 4
            };
        }
    }
}
