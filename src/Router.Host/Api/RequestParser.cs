using System.Text.Json;
using Router.Contracts.Domain;

namespace Router.Host.Api;

internal static class RequestParser
{
    private static readonly HashSet<string> StructuralProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "model",
        "messages",
        "prompt",
        "input",
        "stream",
        "max_tokens",
        "max_completion_tokens",
        "max_output_tokens",
        "temperature",
        "tools",
        "functions",
        "tool_choice",
        "function_call",
        "parallel_tool_calls",
        "reasoning_effort",
        "reasoning",
        "thinking",
        "output_config",
        "endpoint",
        "overrides",
        "models"
    };

    public static bool TryParseEndpoint(JsonElement body, out TestEndpoint endpoint)
    {
        endpoint = TestEndpoint.ChatCompletions;
        if (!body.TryGetProperty("endpoint", out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;

        if (value.ValueKind != JsonValueKind.String) return false;
        var normalized = (value.GetString() ?? string.Empty)
            .Replace("/", string.Empty)
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .Replace(".", string.Empty)
            .ToLowerInvariant();
        endpoint = normalized switch
        {
            "chat" or "chatcompletions" => TestEndpoint.ChatCompletions,
            "completions" or "legacycompletions" => TestEndpoint.Completions,
            "responses" => TestEndpoint.Responses,
            "anthropicmessages" or "messages" => TestEndpoint.AnthropicMessages,
            _ => TestEndpoint.Unknown
        };
        return endpoint != TestEndpoint.Unknown;
    }

    public static AdapterRequest Parse(
        JsonElement body,
        TestEndpoint endpoint = TestEndpoint.ChatCompletions)
    {
        var request = new AdapterRequest
        {
            Model = TryGetProperty(body, "model", out var model) ? model.GetString() ?? string.Empty : string.Empty,
            Endpoint = endpoint switch
            {
                TestEndpoint.ChatCompletions => "/v1/chat/completions",
                TestEndpoint.Completions => "/v1/completions",
                TestEndpoint.Responses => "/v1/responses",
                TestEndpoint.AnthropicMessages => "/v1/messages",
                _ => "/v1/chat/completions"
            },
            OriginalBody = body.ValueKind == JsonValueKind.Object ? body.Clone() : null,
            Stream = TryGetProperty(body, "stream", out var stream) && stream.ValueKind == JsonValueKind.True,
            MaxTokens = ReadInt32(body, "max_tokens")
                ?? ReadInt32(body, "max_completion_tokens")
                ?? (endpoint == TestEndpoint.Responses ? ReadInt32(body, "max_output_tokens") : null),
            Temperature = TryGetProperty(body, "temperature", out var temperature) && temperature.TryGetDouble(out var temp)
                ? temp
                : null
        };

        request.Messages = endpoint switch
        {
            TestEndpoint.Responses => ReadResponsesMessages(body),
            TestEndpoint.AnthropicMessages => ReadAnthropicMessages(body),
            _ => ReadChatMessages(body)
        };

        request.Tools = ReadTools(body);
        CaptureToolOptions(body, request);
        CaptureReasoning(body, request);
        CaptureUnmappedProperties(body, request);

        return request;
    }

    /// <summary>
    /// 保留协议适配器未定义的请求字段，避免宿主为某个插件维护字段白名单。
    /// 插件可以按自己的上游协议解释这些扩展；测试控制字段不会进入扩展。
    /// </summary>
    private static void CaptureUnmappedProperties(JsonElement body, AdapterRequest request)
    {
        if (body.ValueKind != JsonValueKind.Object) return;

        foreach (var property in body.EnumerateObject())
            if (!StructuralProperties.Contains(property.Name))
                request.Extensions[property.Name] = property.Value.Clone();
    }

    private static List<AdapterMessage> ReadChatMessages(JsonElement body)
    {
        var messages = ReadMessages(body, "messages");
        return messages.Count > 0 ? messages : ReadLegacyPrompt(body);
    }

    private static List<AdapterMessage> ReadLegacyPrompt(JsonElement body)
    {
        if (!TryGetProperty(body, "prompt", out var prompt)) return [];

        var messages = new List<AdapterMessage>();
        if (prompt.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prompt.EnumerateArray())
                AddMessage(messages, "user", item, null);
        }
        else
        {
            AddMessage(messages, "user", prompt, null);
        }

        return messages;
    }

    private static List<object> ReadTools(JsonElement body)
    {
        var tools = new List<object>();
        if (TryGetProperty(body, "tools", out var rawTools)
            && rawTools.ValueKind == JsonValueKind.Array)
        {
            tools.AddRange(rawTools.EnumerateArray().Select(item => (object)item.Clone()));
        }

        // OpenAI 的旧 functions 字段统一转换成 Chat Completions tools。
        if (tools.Count == 0
            && TryGetProperty(body, "functions", out var functions)
            && functions.ValueKind == JsonValueKind.Array)
        {
            tools.AddRange(functions.EnumerateArray().Select(function => (object)new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = function.Clone()
            }));
        }

        return tools;
    }

    private static void CaptureToolOptions(JsonElement body, AdapterRequest request)
    {
        if (TryGetProperty(body, "tool_choice", out var toolChoice)
            && toolChoice.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            request.Extensions["tool_choice"] = toolChoice.Clone();
        }
        else if (TryGetProperty(body, "function_call", out var functionCall)
                 && functionCall.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            request.Extensions["tool_choice"] = NormalizeFunctionCallChoice(functionCall);
        }

        if (TryGetProperty(body, "parallel_tool_calls", out var parallelToolCalls)
            && parallelToolCalls.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            request.Extensions["parallel_tool_calls"] = parallelToolCalls.Clone();
        }
    }

    private static object NormalizeFunctionCallChoice(JsonElement functionCall)
    {
        if (functionCall.ValueKind == JsonValueKind.String)
            return functionCall.GetString() ?? "auto";

        if (functionCall.ValueKind == JsonValueKind.Object
            && TryGetProperty(functionCall, "name", out var name)
            && name.ValueKind == JsonValueKind.String)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = name.GetString()
                }
            };
        }

        return functionCall.Clone();
    }

    private static void CaptureReasoning(JsonElement body, AdapterRequest request)
    {
        if (TryGetProperty(body, "reasoning_effort", out var effort)
            && effort.ValueKind == JsonValueKind.String)
            request.Extensions["reasoning_effort"] = effort.GetString();

        foreach (var propertyName in new[] { "reasoning", "thinking" })
        {
            if (!TryGetProperty(body, propertyName, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            request.Extensions[propertyName] = value.Clone();
            if (value.ValueKind == JsonValueKind.Object
                && TryGetProperty(value, "effort", out var nestedEffort)
                && nestedEffort.ValueKind == JsonValueKind.String
                && !request.Extensions.ContainsKey("reasoning_effort"))
                request.Extensions["reasoning_effort"] = nestedEffort.GetString();
        }

        if (TryGetProperty(body, "output_config", out var outputConfig)
            && outputConfig.ValueKind == JsonValueKind.Object)
        {
            request.Extensions["output_config"] = outputConfig.Clone();
            if (!request.Extensions.ContainsKey("reasoning_effort")
                && TryGetProperty(outputConfig, "effort", out var outputEffort)
                && outputEffort.ValueKind == JsonValueKind.String)
                request.Extensions["reasoning_effort"] = outputEffort.GetString();
        }
    }

    private static List<AdapterMessage> ReadResponsesMessages(JsonElement body)
    {
        var messages = new List<AdapterMessage>();
        if (TryGetProperty(body, "instructions", out var instructions))
            AddMessage(messages, "system", instructions, null);

        if (!TryGetProperty(body, "input", out var input)) return messages;
        if (input.ValueKind == JsonValueKind.String)
        {
            AddMessage(messages, "user", input, null);
            return messages;
        }

        if (input.ValueKind == JsonValueKind.Array)
            foreach (var item in input.EnumerateArray())
            {
                if (TryReadResponsesToolMessage(messages, item)) continue;

                var role = item.ValueKind == JsonValueKind.Object
                    && TryGetProperty(item, "role", out var roleElement)
                    ? roleElement.GetString() ?? "user"
                    : "user";
                AddMessage(messages, role, item, null);
            }

        return messages;
    }

    private static List<AdapterMessage> ReadAnthropicMessages(JsonElement body)
    {
        var messages = new List<AdapterMessage>();
        if (TryGetProperty(body, "system", out var system))
            AddMessage(messages, "system", system, null);
        if (body.TryGetProperty("messages", out var rawMessages)
            && rawMessages.ValueKind == JsonValueKind.Array)
        {
            foreach (var rawMessage in rawMessages.EnumerateArray())
                AddAnthropicMessage(messages, rawMessage);
        }

        return messages;
    }

    private static void AddAnthropicMessage(List<AdapterMessage> messages, JsonElement rawMessage)
    {
        var role = TryGetProperty(rawMessage, "role", out var roleElement)
            ? roleElement.GetString() ?? "user"
            : "user";
        var name = TryGetProperty(rawMessage, "name", out var nameElement)
            ? nameElement.GetString()
            : null;
        if (!TryGetProperty(rawMessage, "content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            AddMessage(messages, role, rawMessage, name);
            return;
        }

        var toolCalls = ReadToolCalls(rawMessage);
        var text = new System.Text.StringBuilder();
        var parts = new List<AdapterContentPart>();

        void FlushSegment(IReadOnlyList<object>? calls = null)
        {
            var hasAttachment = parts.Any(part => part.Kind != AdapterContentPartKind.Text);
            if (text.Length > 0 || hasAttachment || calls is { Count: > 0 })
                messages.Add(new AdapterMessage(
                    role,
                    text.ToString(),
                    name,
                    calls)
                {
                    ContentParts = hasAttachment ? parts.ToArray() : null
                });
            text.Clear();
            parts.Clear();
        }

        foreach (var block in content.EnumerateArray())
        {
            if (TryGetProperty(block, "type", out var type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "tool_result", StringComparison.OrdinalIgnoreCase))
            {
                FlushSegment();

                var id = TryGetProperty(block, "tool_use_id", out var idElement)
                    ? idElement.GetString()
                    : null;
                var hasResultContent = TryGetProperty(block, "content", out var resultContent);
                var result = hasResultContent ? ReadContent(resultContent) : string.Empty;
                var resultParts = hasResultContent ? ReadContentParts(resultContent) : null;
                if (!string.IsNullOrWhiteSpace(id) || !string.IsNullOrEmpty(result) || resultParts is { Count: > 0 })
                    messages.Add(new AdapterMessage("tool", result, ToolCallId: id) { ContentParts = resultParts });
                continue;
            }

            if (ReadContentPart(block) is not { } part) continue;
            parts.Add(part);
            if (part.Kind == AdapterContentPartKind.Text) text.Append(part.Text);
        }

        FlushSegment(toolCalls);
    }

    private static List<AdapterMessage> ReadMessages(JsonElement body, string propertyName)
    {
        if (!body.TryGetProperty(propertyName, out var messages)
            || messages.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<AdapterMessage>();
        foreach (var message in messages.EnumerateArray())
        {
            var role = TryGetProperty(message, "role", out var roleElement)
                ? roleElement.GetString() ?? "user"
                : "user";
            var name = TryGetProperty(message, "name", out var nameElement)
                ? nameElement.GetString()
                : null;
            AddMessage(result, role, message, name);
        }

        return result;
    }

    private static void AddMessage(
        List<AdapterMessage> messages,
        string role,
        JsonElement value,
        string? name,
        IReadOnlyList<object>? toolCalls = null,
        string? toolCallId = null)
    {
        var contentSource = value;
        if (value.ValueKind == JsonValueKind.Object && TryGetProperty(value, "content", out var contentElement))
            contentSource = contentElement;
        else if (value.ValueKind == JsonValueKind.Object && TryGetProperty(value, "text", out var textElement))
            contentSource = textElement;
        var content = ReadContent(contentSource);
        var contentParts = ReadContentParts(contentSource);
        toolCalls ??= ReadToolCalls(value);
        toolCallId ??= ReadToolCallId(value);
        var reasoningContent = ReadOptionalProperty(value, "reasoning_content");
        var reasoning = ReadOptionalProperty(value, "reasoning");
        if (!string.IsNullOrWhiteSpace(content)
            || toolCalls is { Count: > 0 }
            || !string.IsNullOrWhiteSpace(toolCallId)
            || reasoningContent.HasValue
            || reasoning.HasValue
            || contentParts is { Count: > 0 })
        {
            messages.Add(new AdapterMessage(role, content, name, toolCalls, toolCallId, reasoningContent, reasoning)
            {
                ContentParts = contentParts
            });
        }
    }

    private static JsonElement? ReadOptionalProperty(JsonElement value, string propertyName)
        => TryGetProperty(value, propertyName, out var property) ? property.Clone() : null;

    private static bool TryReadResponsesToolMessage(List<AdapterMessage> messages, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !TryGetProperty(item, "type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
            return false;

        var type = typeElement.GetString();
        if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "custom_tool_call", StringComparison.OrdinalIgnoreCase))
        {
            var isCustom = string.Equals(type, "custom_tool_call", StringComparison.OrdinalIgnoreCase);
            var name = TryGetProperty(item, "name", out var nameElement)
                ? nameElement.GetString()
                : null;
            var arguments = isCustom
                ? ReadCustomToolInputArguments(item)
                : TryGetProperty(item, "arguments", out var argumentsElement)
                    ? argumentsElement.GetString() ?? argumentsElement.GetRawText()
                    : "{}";
            var id = TryGetProperty(item, "call_id", out var callIdElement)
                ? callIdElement.GetString()
                : null;
            var toolCall = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["arguments"] = arguments
                }
            };
            messages.Add(new AdapterMessage("assistant", string.Empty, null, [toolCall], id));
            return true;
        }

        if (string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "custom_tool_call_output", StringComparison.OrdinalIgnoreCase))
        {
            var hasOutput = TryGetProperty(item, "output", out var outputElement);
            var output = hasOutput ? ReadContent(outputElement) : string.Empty;
            var outputParts = hasOutput ? ReadContentParts(outputElement) : null;
            var id = TryGetProperty(item, "call_id", out var callIdElement)
                ? callIdElement.GetString()
                : null;
            messages.Add(new AdapterMessage("tool", output, null, null, id) { ContentParts = outputParts });
            return true;
        }

        return false;
    }

    private static string ReadCustomToolInputArguments(JsonElement item)
    {
        if (!TryGetProperty(item, "input", out var input)
            || input.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return "{}";
        if (input.ValueKind != JsonValueKind.String)
            return JsonSerializer.Serialize(input);

        var value = input.GetString() ?? string.Empty;
        const string marker = "tools.shell_command(";
        var markerIndex = value.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var openIndex = markerIndex + marker.Length - 1;
            var endIndex = value.IndexOf(");", openIndex + 1, StringComparison.Ordinal);
            if (endIndex >= 0
                && TrySerializeJsonContainer(value[(openIndex + 1)..endIndex].Trim()) is { } commandArguments)
                return commandArguments;
        }

        if (TrySerializeJsonContainer(value.Trim()) is { } jsonArguments)
            return jsonArguments;
        try
        {
            using var parsed = JsonDocument.Parse(value.Trim());
            if (parsed.RootElement.ValueKind == JsonValueKind.String
                && TrySerializeJsonContainer(parsed.RootElement.GetString() ?? string.Empty) is { } nestedArguments)
                return nestedArguments;
        }
        catch (JsonException) { }

        return JsonSerializer.Serialize(new { command = value });
    }

    private static string? TrySerializeJsonContainer(string value)
    {
        try
        {
            using var parsed = JsonDocument.Parse(value);
            return parsed.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? JsonSerializer.Serialize(parsed.RootElement)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<object>? ReadToolCalls(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;

        if (TryGetProperty(value, "tool_calls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array)
        {
            var calls = toolCalls.EnumerateArray().Select(item => (object)item.Clone()).ToArray();
            return calls.Length == 0 ? null : calls;
        }

        if (TryGetProperty(value, "function_call", out var functionCall)
            && functionCall.ValueKind == JsonValueKind.Object)
        {
            var id = TryGetProperty(functionCall, "id", out var idElement)
                ? idElement.GetString()
                : null;
            return
            [
                new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = functionCall.Clone()
                }
            ];
        }

        if (TryGetProperty(value, "content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            var calls = new List<object>();
            foreach (var block in content.EnumerateArray())
            {
                if (!TryGetProperty(block, "type", out var type)
                    || !string.Equals(type.GetString(), "tool_use", StringComparison.OrdinalIgnoreCase))
                    continue;

                var id = TryGetProperty(block, "id", out var idElement) ? idElement.GetString() : null;
                var name = TryGetProperty(block, "name", out var nameElement) ? nameElement.GetString() : null;
                var input = TryGetProperty(block, "input", out var inputElement)
                    ? inputElement.GetRawText()
                    : "{}";
                calls.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["arguments"] = input
                    }
                });
            }

            return calls.Count == 0 ? null : calls;
        }

        return null;
    }

    private static string? ReadToolCallId(JsonElement value)
    {
        foreach (var propertyName in new[] { "tool_call_id", "tool_use_id" })
            if (TryGetProperty(value, propertyName, out var property)
                && property.ValueKind == JsonValueKind.String)
                return property.GetString();

        if (value.ValueKind == JsonValueKind.Object
            && TryGetProperty(value, "content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
                if (TryGetProperty(block, "type", out var type)
                    && string.Equals(type.GetString(), "tool_result", StringComparison.OrdinalIgnoreCase)
                    && TryGetProperty(block, "tool_use_id", out var id))
                    return id.GetString();
        }

        return null;
    }

    private static List<AdapterContentPart>? ReadContentParts(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.Object)
            return ReadContentPart(content) is { Kind: not AdapterContentPartKind.Text } attachment
                ? [attachment]
                : null;
        if (content.ValueKind != JsonValueKind.Array) return null;

        var parts = new List<AdapterContentPart>();
        foreach (var block in content.EnumerateArray())
            if (ReadContentPart(block) is { } part) parts.Add(part);
        return parts.Any(part => part.Kind != AdapterContentPartKind.Text) ? parts : null;
    }

    private static AdapterContentPart? ReadContentPart(JsonElement block)
    {
        if (block.ValueKind == JsonValueKind.String)
            return new AdapterContentPart(AdapterContentPartKind.Text, Text: block.GetString());
        if (block.ValueKind != JsonValueKind.Object) return null;

        var type = ReadStringProperty(block, "type");
        if (type is "image_url" or "input_image" or "image")
        {
            var image = TryGetProperty(block, "image_url", out var imageUrl) ? imageUrl : default;
            var source = TryGetProperty(block, "source", out var imageSource) ? imageSource : default;
            var url = image.ValueKind == JsonValueKind.String
                ? image.GetString()
                : ReadStringProperty(image, "url") ?? ReadStringProperty(block, "url");
            var detail = ReadStringProperty(image, "detail") ?? ReadStringProperty(block, "detail");
            var fileId = ReadStringProperty(block, "file_id");
            if (type == "image")
            {
                var sourceType = ReadStringProperty(source, "type");
                url = sourceType switch
                {
                    "url" => ReadStringProperty(source, "url"),
                    "base64" when ReadStringProperty(source, "media_type") is { } mediaType
                        && ReadStringProperty(source, "data") is { } data
                        => $"data:{mediaType};base64,{data}",
                    _ => null
                };
                fileId = ReadStringProperty(source, "file_id") ?? fileId;
            }

            return new AdapterContentPart(AdapterContentPartKind.Image, ImageUrl: url, FileId: fileId, Detail: detail);
        }

        if (type is "file" or "input_file" or "document")
        {
            var file = TryGetProperty(block, "file", out var chatFile) ? chatFile : default;
            var source = TryGetProperty(block, "source", out var documentSource) ? documentSource : default;
            var sourceType = ReadStringProperty(source, "type");
            var mediaType = ReadStringProperty(source, "media_type");
            var data = sourceType == "base64" ? ReadStringProperty(source, "data") : null;
            return new AdapterContentPart(
                AdapterContentPartKind.File,
                FileId: ReadStringProperty(file, "file_id")
                    ?? ReadStringProperty(block, "file_id")
                    ?? (sourceType == "file" ? ReadStringProperty(source, "file_id") : null),
                Detail: ReadStringProperty(block, "detail"),
                FileName: ReadStringProperty(file, "filename")
                    ?? ReadStringProperty(block, "filename")
                    ?? ReadStringProperty(block, "title"),
                FileData: ReadStringProperty(file, "file_data")
                    ?? ReadStringProperty(block, "file_data")
                    ?? (data is not null && mediaType is not null ? $"data:{mediaType};base64,{data}" : null)
                    ?? (sourceType == "text" && mediaType == "text/plain"
                        && ReadStringProperty(source, "data") is { } plainText
                        ? $"data:text/plain;base64,{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText))}"
                        : null),
                FileUrl: ReadStringProperty(block, "file_url")
                    ?? (sourceType == "url" ? ReadStringProperty(source, "url") : null),
                MediaType: mediaType ?? (type == "document" && sourceType == "url" ? "application/pdf" : null));
        }

        var text = ReadContentBlock(block);
        return string.IsNullOrEmpty(text) ? null : new AdapterContentPart(AdapterContentPartKind.Text, Text: text);
    }

    private static string? ReadStringProperty(JsonElement value, string propertyName)
        => TryGetProperty(value, propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string ReadContent(JsonElement content)
    {
        if (content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return string.Empty;

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            var text = string.Concat(content.EnumerateArray().Select(ReadContentBlock));
            if (text.Length > 0 || ReadContentParts(content) is { Count: > 0 }) return text;
        }

        return content.ValueKind == JsonValueKind.Object
            ? string.Empty
            : content.GetRawText();
    }

    private static string ReadContentBlock(JsonElement block)
    {
        if (block.ValueKind == JsonValueKind.String)
            return block.GetString() ?? string.Empty;

        if (block.ValueKind != JsonValueKind.Object) return string.Empty;
        if (TryGetProperty(block, "text", out var text))
            return text.ValueKind == JsonValueKind.String ? text.GetString() ?? string.Empty : string.Empty;

        if (TryGetProperty(block, "type", out var type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "tool_result", StringComparison.OrdinalIgnoreCase)
            && TryGetProperty(block, "content", out var toolResultContent))
            return ReadContent(toolResultContent);

        return string.Empty;
    }

    private static int? ReadInt32(JsonElement body, string propertyName)
        => TryGetProperty(body, propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;

    private static bool TryGetProperty(JsonElement value, string propertyName, out JsonElement property)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(propertyName, out property))
            return true;

        if (value.ValueKind == JsonValueKind.Object)
            foreach (var item in value.EnumerateObject())
                if (item.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = item.Value;
                    return true;
                }

        property = default;
        return false;
    }
}

internal enum TestEndpoint
{
    Unknown,
    ChatCompletions,
    Completions,
    Responses,
    AnthropicMessages
}
