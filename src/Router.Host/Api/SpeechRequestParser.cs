using System.Text.Json;
using Router.Contracts.Domain;

namespace Router.Host.Api;

/// <summary>
/// 把 OpenAI 兼容的 <c>POST /v1/audio/speech</c> 请求体解析成统一的 <see cref="AdapterRequest"/>。
/// <para>
/// 语音合成与聊天共用同一条插件管线：<c>model</c> 仍按 <c>平台/模型</c> 解析（也支持模型映射），
/// <c>input</c> 既作为消息正文交给插件，也保留在 <see cref="AdapterRequest.OriginalBody"/> 里；
/// <c>voice</c>、<c>response_format</c>、<c>speed</c> 等非结构字段原样保留，供插件按自己的上游协议解释。
/// </para>
/// </summary>
internal static class SpeechRequestParser
{
    /// <summary>与 OpenAI 一致的单词输入上限（字符数）。更长的文本应改用聊天端点，由插件自行分段。</summary>
    internal const int MaxInputLength = 4096;

    /// <summary>不属于插件扩展的结构字段，不进入 <see cref="AdapterRequest.Extensions"/>。</summary>
    private static readonly HashSet<string> StructuralProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "model",
        "input",
        "text",
        "prompt",
        "stream"
    };

    /// <summary>解析请求体。</summary>
    /// <param name="body">HTTP 请求体。</param>
    /// <param name="request">解析结果；失败时为 null。</param>
    /// <param name="error">失败原因；成功时为 null。</param>
    /// <returns>请求体合法时为 true。</returns>
    public static bool TryParse(JsonElement body, out AdapterRequest? request, out string? error)
    {
        request = null;
        error = null;

        if (body.ValueKind != JsonValueKind.Object)
        {
            error = "request body must be a JSON object";
            return false;
        }

        var model = ReadString(body, "model")?.Trim() ?? string.Empty;
        if (model.Length == 0)
        {
            error = "model is required, for example 'doubaotts/taozi'";
            return false;
        }

        if (TryGetProperty(body, "stream", out var stream) && stream.ValueKind == JsonValueKind.True)
        {
            error = "streaming speech responses are not supported; omit 'stream' to receive one audio file";
            return false;
        }

        var input = ReadString(body, "input")
            ?? ReadString(body, "text")
            ?? ReadString(body, "prompt")
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "input is required and must contain the text to synthesize";
            return false;
        }

        if (input.Length > MaxInputLength)
        {
            error = $"input must be at most {MaxInputLength} characters; use the chat endpoint for longer text";
            return false;
        }

        var parsed = new AdapterRequest
        {
            Model = model,
            Endpoint = "/v1/audio/speech",
            OriginalBody = body.Clone(),
            Messages = [new AdapterMessage("user", input)]
        };
        CapturePluginProperties(body, parsed);

        request = parsed;
        return true;
    }

    /// <summary>把 <c>voice</c>、<c>response_format</c>、<c>speed</c> 等插件字段放进扩展表，插件无需重复解析正文。</summary>
    private static void CapturePluginProperties(JsonElement body, AdapterRequest request)
    {
        foreach (var property in body.EnumerateObject())
        {
            if (StructuralProperties.Contains(property.Name))
            {
                continue;
            }

            request.Extensions[property.Name] = property.Value.Clone();
        }
    }

    private static string? ReadString(JsonElement body, string name)
        => TryGetProperty(body, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetProperty(JsonElement body, string name, out JsonElement value)
    {
        foreach (var property in body.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
