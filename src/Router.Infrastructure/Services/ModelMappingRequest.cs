using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;

namespace Router.Infrastructure.Services;

/// <summary>为映射目标创建独立的可变参数与原始正文，保留其他 JSON 字段的数字精度。</summary>
public static class ModelMappingRequest
{
    /// <summary>创建目标请求；不修改调用方请求，原始正文只替换顶层 model。</summary>
    /// <param name="source">调用方请求。</param>
    /// <param name="model">插件内模型 ID。</param>
    /// <returns>供一个映射目标使用的请求。</returns>
    public static AdapterRequest Create(AdapterRequest source, string model)
    {
        var request = new AdapterRequest
        {
            Model = model, Endpoint = source.Endpoint, Stream = source.Stream,
            MaxTokens = source.MaxTokens, Temperature = source.Temperature,
            Messages = source.Messages.Select(message => message with
            {
                ToolCalls = message.ToolCalls?.Select(value => CloneValue(value)!).ToArray(),
                ContentParts = message.ContentParts?.ToArray()
            }).ToArray(),
            Tools = source.Tools.Select(value => CloneValue(value)!).ToArray()
        };
        foreach (var pair in source.Extensions)
            request.Extensions[pair.Key] = CloneValue(pair.Value);
        foreach (var pair in source.RequestHeaders) request.RequestHeaders[pair.Key] = pair.Value;
        foreach (var pair in source.DownstreamRequestHeaders) request.DownstreamRequestHeaders[pair.Key] = pair.Value.ToArray();
        if (source.OriginalBody is { ValueKind: JsonValueKind.Object } body)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in body.EnumerateObject())
                    if (!property.Name.Equals("model", StringComparison.OrdinalIgnoreCase)) property.WriteTo(writer);
                writer.WriteString("model", model);
                writer.WriteEndObject();
            }
            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            request.OriginalBody = document.RootElement.Clone();
        }
        return request;
    }

    private static object? CloneValue(object? value) => value is JsonNode node ? node.DeepClone()
        : value is null or JsonElement or string or ValueType ? value : JsonSerializer.SerializeToElement(value);
}
