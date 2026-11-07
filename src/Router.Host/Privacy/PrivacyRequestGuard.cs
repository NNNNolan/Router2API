using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Router.Contracts.Domain;
using Router.Host.Api;
using Router.Infrastructure.Services;
using Router.Infrastructure.Services.Privacy;

namespace Router.Host.Privacy;

/// <summary>以原始正文为唯一来源，改写已声明的文本位置后重新解析；插件不能从另一份正文绕过脱敏。</summary>
public sealed class PrivacyRequestGuard : IDisposable
{
    private readonly IDisposable? _subscription;
    private PrivacyPolicy _policy;

    public PrivacyRequestGuard(IOptionsMonitor<PrivacyOptions> options)
    {
        _policy = PrivacyPolicy.Build(options.CurrentValue);
        _subscription = options.OnChange(value => Volatile.Write(ref _policy, PrivacyPolicy.Build(value)));
    }

    /// <summary>配置快照副本，调用方不能改变正在处理的请求策略。</summary>
    public PrivacyOptions Settings => Volatile.Read(ref _policy).Options.Clone();

    /// <summary>语音合成明确不受保护；其他入口使用同一策略快照。</summary>
    public PrivacyInspection? Begin(AdapterRequest request)
    {
        var policy = Volatile.Read(ref _policy);
        return !policy.Options.Enabled || request.Endpoint == "/v1/audio/speech" ? null : new PrivacyInspection(policy);
    }

    /// <summary>用于管理员试用，不读取或保存输入；强制检测，即使正式功能关闭。</summary>
    public PrivacyInspection BeginTest() => new(Volatile.Read(ref _policy));

    /// <summary>返回新请求，保持头、测试控制和插件扩展字段的原有语义。</summary>
    public static AdapterRequest Apply(AdapterRequest request, PrivacyInspection inspection)
    {
        if (request.OriginalBody is not { ValueKind: JsonValueKind.Object } body)
            return ApplyStandard(request, inspection);
        if (inspection.DryRun || !inspection.CanRedact)
        {
            Scan(body, inspection.Inspect, RequestTextMode.Root);
            return request;
        }
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        Scan(body, text =>
        {
            var rewritten = inspection.Inspect(text);
            if (!string.Equals(text, rewritten, StringComparison.Ordinal)) replacements.TryAdd(text, rewritten);
            return rewritten;
        }, RequestTextMode.Root);
        if (inspection.Blocked || replacements.Count == 0) return request;
        var rewritten = Write(body, text => replacements.GetValueOrDefault(text, text), RequestTextMode.Root);
        var endpoint = request.Endpoint switch
        {
            "/v1/completions" => TestEndpoint.Completions,
            "/v1/responses" => TestEndpoint.Responses,
            "/v1/messages" => TestEndpoint.AnthropicMessages,
            _ => TestEndpoint.ChatCompletions
        };
        var result = RequestParser.Parse(rewritten, endpoint);
        foreach (var header in request.RequestHeaders) result.RequestHeaders[header.Key] = header.Value;
        foreach (var header in request.DownstreamRequestHeaders) result.DownstreamRequestHeaders[header.Key] = header.Value.ToArray();
        return result;
    }

    /// <summary>只改写协议文本。未修改的 JsonElement 直接 WriteTo，保留超大整数及未知字段。</summary>
    internal static JsonElement Rewrite(JsonElement body, PrivacyInspection inspection)
        => Write(body, inspection.Inspect, RequestTextMode.Root);

    private static void Scan(JsonElement source, Func<string, string> inspect, RequestTextMode mode)
    {
        if (mode == RequestTextMode.None) return;
        if (source.ValueKind == JsonValueKind.String)
        {
            if (mode != RequestTextMode.Descriptions) inspect(source.GetString()!);
            return;
        }
        if (source.ValueKind == JsonValueKind.Array)
        {
            var childMode = ArrayChildMode(mode);
            foreach (var child in source.EnumerateArray()) Scan(child, inspect, childMode);
            return;
        }
        if (source.ValueKind != JsonValueKind.Object) return;
        foreach (var property in source.EnumerateObject())
        {
            if (property.Name.Equals("arguments", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String
                && (mode == RequestTextMode.ToolFunction || OwnerIsFunctionCall(source, mode)))
            {
                ScanArguments(property.Value.GetString()!, inspect);
                continue;
            }
            Scan(property.Value, inspect, ChildMode(source, property.Name, mode));
        }
    }

    private static void ScanArguments(string text, Func<string, string> inspect)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            Scan(document.RootElement, inspect, RequestTextMode.AllValues);
        }
        catch (JsonException) { inspect(text); }
    }

    private static bool OwnerIsFunctionCall(JsonElement owner, RequestTextMode parentMode)
        => parentMode is RequestTextMode.Message or RequestTextMode.Part
            && owner.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            && type.GetString() == "function_call";

    private static RequestTextMode ArrayChildMode(RequestTextMode mode) => mode switch
    {
        RequestTextMode.Messages => RequestTextMode.Message,
        RequestTextMode.Content => RequestTextMode.Part,
        RequestTextMode.Tools => RequestTextMode.Tool,
        _ => mode
    };

    private enum RequestTextMode { None, Root, Messages, Message, Content, Part, Tools, Tool, ToolFunction, AllValues, Descriptions }

    private static JsonElement Write(JsonElement source, Func<string, string> transform, RequestTextMode mode)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) WriteValue(writer, source, transform, mode);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value, Func<string, string> transform, RequestTextMode mode)
    {
        if (mode == RequestTextMode.None) { value.WriteTo(writer); return; }
        if (value.ValueKind == JsonValueKind.String)
        {
            if (mode == RequestTextMode.Descriptions) { value.WriteTo(writer); return; }
            writer.WriteStringValue(transform(value.GetString()!));
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            var childMode = ArrayChildMode(mode);
            foreach (var child in value.EnumerateArray()) WriteValue(writer, child, transform, childMode);
            writer.WriteEndArray();
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) { value.WriteTo(writer); return; }
        writer.WriteStartObject();
        foreach (var property in value.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (property.Name.Equals("arguments", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String
                && (mode == RequestTextMode.ToolFunction
                    || OwnerIsFunctionCall(value, mode)))
            {
                WriteArguments(writer, property.Value.GetString()!, transform);
                continue;
            }
            WriteValue(writer, property.Value, transform, ChildMode(value, property.Name, mode));
        }
        writer.WriteEndObject();

    }

    private static RequestTextMode ChildMode(JsonElement owner, string name, RequestTextMode mode)
    {
        name = name.ToLowerInvariant();
        var type = owner.TryGetProperty("type", out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        // 工具参数和结果中的嵌套图片也遵守协议内容块的二进制边界。
        if (mode is RequestTextMode.AllValues or RequestTextMode.Message or RequestTextMode.Part
            && type is "image" or "image_url" or "input_image" or "file" or "input_file" or "encrypted_thinking")
            return RequestTextMode.None;
        if (mode == RequestTextMode.AllValues)
            return name is "file_data" or "image_url" or "file_url" or "encrypted_content" or "signature"
                ? RequestTextMode.None : RequestTextMode.AllValues;
        if (mode == RequestTextMode.Descriptions)
            return name == "description" ? RequestTextMode.AllValues : RequestTextMode.Descriptions;
        if (mode == RequestTextMode.Root)
            return name switch
            {
                "messages" => RequestTextMode.Messages,
                "prompt" or "instructions" or "system" => RequestTextMode.Content,
                "input" => RequestTextMode.Messages,
                "tools" or "functions" => RequestTextMode.Tools,
                _ => RequestTextMode.None
            };
        if (mode is RequestTextMode.Message or RequestTextMode.Part)
        {
            return name switch
            {
                "content" => RequestTextMode.Content,
                "text" or "thinking" or "reasoning_content" or "output" or "result" => RequestTextMode.AllValues,
                "tool_calls" => RequestTextMode.Tools,
                "function_call" or "function" => RequestTextMode.ToolFunction,
                "description" => RequestTextMode.AllValues,
                "input" when type == "tool_use" => RequestTextMode.AllValues,
                "arguments" when type == "function_call" => RequestTextMode.ToolFunction,
                _ => RequestTextMode.None
            };
        }
        if (mode == RequestTextMode.Tools) return ChildMode(owner, name, RequestTextMode.Tool);
        if (mode == RequestTextMode.Tool)
            return name switch
            {
                "function" => RequestTextMode.ToolFunction,
                "arguments" => RequestTextMode.AllValues,
                "description" => RequestTextMode.AllValues,
                "parameters" or "input_schema" => RequestTextMode.Descriptions,
                _ => RequestTextMode.None
            };
        if (mode == RequestTextMode.ToolFunction)
            return name switch
            {
                "description" => RequestTextMode.AllValues,
                "parameters" => RequestTextMode.Descriptions,
                "arguments" => RequestTextMode.AllValues,
                _ => RequestTextMode.None
            };
        if (mode == RequestTextMode.Content) return ChildMode(owner, name, RequestTextMode.Part);
        return RequestTextMode.None;
    }

    private static void WriteArguments(Utf8JsonWriter writer, string text, Func<string, string> transform)
    {
        // 只检测 JSON 参数里的值，不能把结构标点、函数名也替换掉。
        JsonElement arguments;
        try { using var document = JsonDocument.Parse(text); arguments = document.RootElement.Clone(); }
        catch (JsonException) { writer.WriteStringValue(transform(text)); return; }
        writer.WriteStringValue(Write(arguments, transform, RequestTextMode.AllValues).GetRawText());
    }

    private static AdapterRequest ApplyStandard(AdapterRequest request, PrivacyInspection inspection)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        ScanStandard(request, text =>
        {
            var rewritten = inspection.Inspect(text);
            if (!string.Equals(text, rewritten, StringComparison.Ordinal)) replacements.TryAdd(text, rewritten);
            return rewritten;
        });
        if (inspection.DryRun || inspection.Blocked || replacements.Count == 0) return request;
        string RewriteText(string text) => replacements.GetValueOrDefault(text, text);

        var result = ModelMappingRequest.Create(request, request.Model);
        result.Messages = request.Messages.Select(message => message with
        {
            Content = RewriteText(message.Content),
            ContentParts = message.ContentParts?.Select(part => part.Kind == AdapterContentPartKind.Text
                ? part with { Text = part.Text is { } text ? RewriteText(text) : null } : part).ToArray(),
            ReasoningContent = message.ReasoningContent is { } reasoning
                ? Write(reasoning, RewriteText, RequestTextMode.AllValues) : null,
            Reasoning = message.Reasoning is { } thinking
                ? Write(thinking, RewriteText, RequestTextMode.AllValues) : null,
            ToolCalls = message.ToolCalls?.Select(call => (object)Write(JsonSerializer.SerializeToElement(call),
                RewriteText, RequestTextMode.Tool)).ToArray()
        }).ToArray();
        result.Tools = request.Tools.Select(tool => (object)Write(JsonSerializer.SerializeToElement(tool),
            RewriteText, RequestTextMode.Tool)).ToArray();
        foreach (var key in new[] { "prompt", "instructions", "system", "input" })
            if (request.Extensions.TryGetValue(key, out var value))
                result.Extensions[key] = Write(JsonSerializer.SerializeToElement(value), RewriteText, RequestTextMode.Content);
        return result;
    }

    private static void ScanStandard(AdapterRequest request, Func<string, string> inspect)
    {
        foreach (var message in request.Messages)
        {
            inspect(message.Content);
            if (message.ContentParts is not null)
                foreach (var part in message.ContentParts)
                    if (part.Kind == AdapterContentPartKind.Text && part.Text is { } text) inspect(text);
            if (message.ReasoningContent is { } reasoning) Scan(reasoning, inspect, RequestTextMode.AllValues);
            if (message.Reasoning is { } thinking) Scan(thinking, inspect, RequestTextMode.AllValues);
            if (message.ToolCalls is not null)
                foreach (var call in message.ToolCalls)
                    Scan(JsonSerializer.SerializeToElement(call), inspect, RequestTextMode.Tool);
        }
        foreach (var tool in request.Tools)
            Scan(JsonSerializer.SerializeToElement(tool), inspect, RequestTextMode.Tool);
        foreach (var key in new[] { "prompt", "instructions", "system", "input" })
            if (request.Extensions.TryGetValue(key, out var value))
                Scan(JsonSerializer.SerializeToElement(value), inspect, RequestTextMode.Content);
    }

    public void Dispose() => _subscription?.Dispose();
}
