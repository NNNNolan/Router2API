using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Router.Infrastructure.Services.Privacy;

/// <summary>只变换已识别协议的正文/推理/工具参数，SSE 按事件解析并保持结束标记；不替换任意字节。</summary>
internal sealed class PrivacyRawResponseRestorer(IReadOnlyDictionary<string, string> map, string endpoint)
{
    private const int FrameLimit = 4 * 1024 * 1024;
    private const int ChannelLimit = 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Dictionary<string, Channel> _channels = new(StringComparer.Ordinal);

    public byte[] RestoreContent(byte[] bytes, string? contentType)
    {
        if (contentType is not null && !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            throw InvalidResponse();
        try
        {
            var body = JsonNode.Parse(bytes) as JsonObject ?? throw InvalidResponse();
            RestoreCompletion(body);
            return JsonSerializer.SerializeToUtf8Bytes(body);
        }
        catch (JsonException) { throw InvalidResponse(); }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> RestoreStream(IAsyncEnumerable<ReadOnlyMemory<byte>> source,
        string? contentType, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (contentType is not null && !contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw InvalidResponse();
        var reader = new FrameReader();
        try
        {
            await foreach (var chunk in source.WithCancellation(cancellationToken))
                foreach (var frame in reader.Feed(chunk, false))
                    foreach (var restored in RestoreFrame(frame))
                        yield return Utf8.GetBytes(restored);
            foreach (var frame in reader.Feed(ReadOnlyMemory<byte>.Empty, true))
                foreach (var restored in RestoreFrame(frame))
                    yield return Utf8.GetBytes(restored);
            if (_channels.Values.Any(channel => channel.Restorer.Flush().Length > 0))
                throw InvalidResponse(); // 未遇到协议结束点，不把截断流伪装为完整还原。
        }
        finally { _channels.Clear(); }
    }

    private string[] RestoreFrame(string frame)
    {
        var lines = frame.Split('\n');
        var data = string.Join("\n", lines.Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..]));
        if (data.Length == 0) return [frame];
        if (data == "[DONE]")
        {
            if (endpoint is not ("/v1/chat/completions" or "/v1/completions")) throw InvalidResponse();
            return Flush(_ => true).Select(Render).Append(frame).ToArray();
        }
        JsonObject body;
        try { body = JsonNode.Parse(data) as JsonObject ?? throw InvalidResponse(); }
        catch (JsonException) { throw InvalidResponse(); }
        var eventName = lines.FirstOrDefault(line => line.StartsWith("event:", StringComparison.Ordinal))?[6..].Trim();
        var before = new List<JsonObject>();
        if (endpoint == "/v1/messages") RestoreAnthropicEvent(body, eventName, before);
        else if (endpoint == "/v1/responses") RestoreResponsesEvent(body, eventName, before);
        else RestoreChat(body, streaming: true);
        var header = lines.Where(line => line.Length > 0 && !line.StartsWith("data:", StringComparison.Ordinal));
        var current = string.Join("\n", header.Append("data: " + body.ToJsonString())) + "\n\n";
        return before.Select(Render).Append(current).ToArray();
    }

    private static string Render(JsonObject body)
    {
        var type = Text(body["type"]);
        return (type is null ? "" : $"event: {type}\n") + "data: " + body.ToJsonString() + "\n\n";
    }

    private void RestoreCompletion(JsonObject body)
    {
        if (body["error"] is not null) { RestoreError(body["error"]); return; }
        if (endpoint == "/v1/messages")
        {
            if (body["content"] is not JsonArray content) throw InvalidResponse();
            RestoreAnthropicContent(content);
        }
        else if (endpoint == "/v1/responses")
        {
            if (body["output"] is not JsonArray output) throw InvalidResponse();
            foreach (var item in output.OfType<JsonObject>()) RestoreResponseItem(item);
        }
        else RestoreChat(body, streaming: false);
    }

    private void RestoreChat(JsonObject body, bool streaming)
    {
        if (body["error"] is not null) { RestoreError(body["error"]); return; }
        if (body["choices"] is not JsonArray choices) throw InvalidResponse();
        foreach (var choice in choices.OfType<JsonObject>())
        {
            var index = Integer(choice["index"]);
            var prefix = $"chat:{index}:";
            var final = !streaming || choice["finish_reason"] is not null;
            if (choice.ContainsKey("text"))
            {
                Change(choice, "text", prefix + "text", final, false,
                    tail => new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["index"] = index, ["text"] = tail }) });
                continue;
            }
            if (endpoint == "/v1/completions")
            {
                Change(choice, "text", prefix + "text", final, false,
                    tail => new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["index"] = index, ["text"] = tail }) });
                continue;
            }
            var message = choice[streaming ? "delta" : "message"] as JsonObject;
            if (message is null)
            {
                if (streaming && final)
                {
                    message = new JsonObject();
                    choice["delta"] = message;
                }
                else if (streaming) continue;
                else throw InvalidResponse();
            }
            JsonObject Tail(string field, string tail) => new()
            {
                ["choices"] = new JsonArray(new JsonObject { ["index"] = index, ["delta"] = new JsonObject { [field] = tail } })
            };
            Change(message, "content", prefix + "text", final, false, tail => Tail("content", tail));
            Change(message, "reasoning_content", prefix + "reasoning", final, false, tail => Tail("reasoning_content", tail));
            Change(message, "reasoning", prefix + "reasoning2", final, false, tail => Tail("reasoning", tail));
            if (message["content"] is JsonArray parts)
                foreach (var part in parts.OfType<JsonObject>())
                    RestoreField(part, "text");
            if (message["tool_calls"] is JsonArray calls)
                foreach (var call in calls.OfType<JsonObject>())
                    if (call["function"] is JsonObject function)
                    {
                        var toolIndex = Integer(call["index"]);
                        Change(function, "arguments", prefix + $"tool:{toolIndex}", final, true, tail => new JsonObject
                        {
                            ["choices"] = new JsonArray(new JsonObject
                            {
                                ["index"] = index, ["delta"] = new JsonObject
                                {
                                    ["tool_calls"] = new JsonArray(new JsonObject
                                    { ["index"] = toolIndex, ["function"] = new JsonObject { ["arguments"] = tail } })
                                }
                            })
                        });
                    }
            // finish_reason 可以单独到达：在同一 choice 的结束事件内补尾部，不能把它放到结束之后。
            if (final)
                foreach (var tail in Flush(key => key.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    var delta = (JsonObject)tail["choices"]![0]!["delta"]!;
                    foreach (var property in delta)
                    {
                        if (property.Key == "tool_calls")
                        {
                            message["tool_calls"] ??= new JsonArray();
                            foreach (var call in (JsonArray)property.Value!) ((JsonArray)message["tool_calls"]!).Add(call!.DeepClone());
                        }
                        else
                            message[property.Key] = (Text(message[property.Key]) ?? "") + Text(property.Value);
                    }
                }
        }
    }

    private void RestoreAnthropicEvent(JsonObject body, string? eventName, List<JsonObject> before)
    {
        var type = Text(body["type"]) ?? eventName;
        var index = Integer(body["index"]);
        var prefix = $"anthropic:{index}:";
        if (type == "content_block_delta" && body["delta"] is JsonObject delta)
        {
            var (field, json) = Text(delta["type"]) switch
            {
                "text_delta" => ("text", false),
                "thinking_delta" => ("thinking", false),
                "input_json_delta" => ("partial_json", true),
                "signature_delta" => ("", false),
                _ => throw InvalidResponse()
            };
            if (field.Length > 0)
                Change(delta, field, prefix + field, false, json, tail => new JsonObject
                {
                    ["type"] = "content_block_delta", ["index"] = index,
                    ["delta"] = new JsonObject { ["type"] = Text(delta["type"]), [field] = tail }
                });
        }
        else if (type == "content_block_start" && body["content_block"] is JsonObject block)
            RestoreAnthropicContent(new JsonArray(block.DeepClone()), original: block);
        else if (type == "content_block_delta" || type == "content_block_start")
            throw InvalidResponse();
        else if (type == "content_block_stop")
            before.AddRange(Flush(key => key.StartsWith(prefix, StringComparison.Ordinal)));
        else if (type is "message_stop" or "error")
        {
            before.AddRange(Flush(_ => true));
            if (type == "error") RestoreError(body["error"]);
        }
        else if (type == "message_start" && body["message"]?["content"] is JsonArray content)
            RestoreAnthropicContent(content);
        else if (type is not ("message_start" or "message_delta" or "ping" or "content_block_stop"))
            throw InvalidResponse();
    }

    private void RestoreAnthropicContent(JsonArray content, JsonObject? original = null)
    {
        foreach (var value in content.OfType<JsonObject>())
        {
            var block = original ?? value;
            switch (Text(block["type"]))
            {
                case "text": RestoreField(block, "text"); break;
                case "thinking": RestoreField(block, "thinking"); break;
                case "tool_use": RestoreValues(block["input"]); break;
            }
        }
    }

    private void RestoreResponsesEvent(JsonObject body, string? eventName, List<JsonObject> before)
    {
        var type = Text(body["type"]) ?? eventName ?? throw InvalidResponse();
        var output = Integer(body["output_index"]);
        var content = Integer(body["content_index"] ?? body["summary_index"]);
        var prefix = $"responses:{output}:{content}:";
        if (type is "response.output_text.delta" or "response.reasoning_summary_text.delta"
            or "response.reasoning_text.delta" or "response.function_call_arguments.delta")
        {
            var json = type == "response.function_call_arguments.delta";
            var channel = prefix + type;
            Change(body, "delta", channel, false, json, tail =>
            {
                var copy = (JsonObject)body.DeepClone();
                copy["delta"] = tail;
                return copy;
            });
        }
        else if (type.EndsWith(".done", StringComparison.Ordinal))
        {
            before.AddRange(Flush(key => key.StartsWith(prefix, StringComparison.Ordinal)));
            RestoreField(body, "text");
            RestoreField(body, "arguments", json: true);
            if (body["item"] is JsonObject item) RestoreResponseItem(item);
        }
        else if (type is "response.completed" or "response.failed" or "response.incomplete" or "error")
        {
            before.AddRange(Flush(_ => true));
            if (body["response"] is JsonObject response && response["output"] is JsonArray items)
                foreach (var item in items.OfType<JsonObject>()) RestoreResponseItem(item);
            RestoreError(body["error"]);
        }
        else if (body["item"] is JsonObject item) RestoreResponseItem(item);
        else if (type.EndsWith(".delta", StringComparison.Ordinal) || type.Contains("text", StringComparison.Ordinal)
            || type.Contains("arguments", StringComparison.Ordinal))
            throw InvalidResponse();
    }

    private void RestoreResponseItem(JsonObject item)
    {
        switch (Text(item["type"]))
        {
            case "message":
                if (item["content"] is JsonArray parts)
                    foreach (var part in parts.OfType<JsonObject>()) RestoreField(part, "text");
                break;
            case "reasoning":
                if (item["summary"] is JsonArray summary)
                    foreach (var part in summary.OfType<JsonObject>()) RestoreField(part, "text");
                break;
            case "function_call": RestoreField(item, "arguments", true); break;
            case "custom_tool_call": RestoreField(item, "input"); break;
        }
    }

    private void Change(JsonObject owner, string field, string key, bool final, bool json, Func<string, JsonObject> tail)
    {
        var value = Text(owner[field]);
        if (value is null && !_channels.ContainsKey(key)) return;
        if (!_channels.TryGetValue(key, out var channel))
        {
            if (_channels.Count >= ChannelLimit) throw InvalidResponse();
            _channels.Add(key, channel = new(new PlaceholderRestorer(map, json), tail));
        }
        var restored = channel.Restorer.Push(value ?? "");
        if (final) { restored += channel.Restorer.Flush(); _channels.Remove(key); }
        if (value is not null || restored.Length > 0) owner[field] = restored;
    }

    private IEnumerable<JsonObject> Flush(Func<string, bool> predicate)
    {
        foreach (var key in _channels.Keys.Where(predicate).ToArray())
        {
            var channel = _channels[key];
            _channels.Remove(key);
            var tail = channel.Restorer.Flush();
            if (tail.Length > 0) yield return channel.Tail(tail);
        }
    }

    private void RestoreField(JsonObject owner, string field, bool json = false)
    {
        if (Text(owner[field]) is { } text) owner[field] = PlaceholderRestorer.Restore(text, map, json);
    }

    private void RestoreValues(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
                if (Text(obj[key]) is { } text) obj[key] = PlaceholderRestorer.Restore(text, map);
                else RestoreValues(obj[key]);
        else if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
                if (Text(array[index]) is { } text) array[index] = PlaceholderRestorer.Restore(text, map);
                else RestoreValues(array[index]);
    }

    private void RestoreError(JsonNode? node)
    {
        if (node is JsonObject error) RestoreField(error, "message");
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static int Integer(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
    private static PrivacyInspectionException InvalidResponse() => new("插件返回了无法安全还原的隐私响应。");
    private sealed record Channel(PlaceholderRestorer Restorer, Func<string, JsonObject> Tail);

    /// <summary>增量 UTF-8 和 SSE 分帧；原始字节分片不能切坏 Unicode 或被当作事件边界。</summary>
    private sealed class FrameReader
    {
        private readonly Decoder _decoder = Utf8.GetDecoder();
        private readonly StringBuilder _line = new();
        private readonly StringBuilder _frame = new();
        private bool _afterCr;

        public List<string> Feed(ReadOnlyMemory<byte> bytes, bool flush)
        {
            var frames = new List<string>();
            var chars = new char[Utf8.GetMaxCharCount(bytes.Length)];
            int count;
            try { count = _decoder.GetChars(bytes.Span, chars, flush); }
            catch (DecoderFallbackException) { throw InvalidResponse(); }
            for (var index = 0; index < count; index++)
            {
                var character = chars[index];
                if (_afterCr && character == '\n') { _afterCr = false; continue; }
                _afterCr = character == '\r';
                if (character is '\r' or '\n')
                {
                    _frame.Append(_line).Append('\n');
                    if (_line.Length == 0) { frames.Add(_frame.ToString()); _frame.Clear(); }
                    _line.Clear();
                }
                else _line.Append(character);
                if (_line.Length + _frame.Length > FrameLimit) throw InvalidResponse();
            }
            if (flush && (_line.Length > 0 || _frame.Length > 0)) throw InvalidResponse();
            return frames;
        }
    }
}
