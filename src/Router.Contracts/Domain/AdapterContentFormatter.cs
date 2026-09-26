namespace Router.Contracts.Domain;

/// <summary>将统一消息内容转换为上游 Chat、Responses 或 Anthropic Messages 内容块。</summary>
public static class AdapterContentFormatter
{
    /// <summary>生成 Chat Completions 的字符串或有序内容块。</summary>
    public static object ToChatContent(AdapterMessage message)
    {
        if (message.ContentParts is not { Count: > 0 } parts) return message.Content;

        var result = new List<object>(parts.Count);
        foreach (var part in parts)
        {
            if (part.Kind == AdapterContentPartKind.Text)
            {
                result.Add(new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = part.Text ?? string.Empty
                });
                continue;
            }

            if (part.Kind == AdapterContentPartKind.File)
            {
                result.Add(AdapterFileFormatter.ToChatFile(part));
                continue;
            }

            var imageUrl = RequireImageUrl(part, "Chat Completions");
            var image = new Dictionary<string, object?> { ["url"] = imageUrl };
            if (!string.IsNullOrWhiteSpace(part.Detail)) image["detail"] = part.Detail;
            result.Add(new Dictionary<string, object?>
            {
                ["type"] = "image_url",
                ["image_url"] = image
            });
        }

        return result;
    }

    /// <summary>生成 Responses 输入消息的有序内容块。</summary>
    public static List<object> ToResponsesContent(AdapterMessage message, bool isAssistant)
    {
        if (message.ContentParts is not { Count: > 0 } parts)
            return string.IsNullOrWhiteSpace(message.Content)
                ? []
                : [TextBlock(isAssistant ? "output_text" : "input_text", message.Content)];

        var result = new List<object>(parts.Count);
        foreach (var part in parts)
        {
            if (part.Kind == AdapterContentPartKind.Text)
            {
                result.Add(TextBlock(isAssistant ? "output_text" : "input_text", part.Text ?? string.Empty));
                continue;
            }

            if (part.Kind == AdapterContentPartKind.File)
            {
                result.Add(AdapterFileFormatter.ToResponsesFile(part));
                continue;
            }

            var image = new Dictionary<string, object?> { ["type"] = "input_image" };
            if (!string.IsNullOrWhiteSpace(part.ImageUrl)) image["image_url"] = part.ImageUrl;
            else if (!string.IsNullOrWhiteSpace(part.FileId))
                throw new NotSupportedException("image file ID cannot be converted to Responses; use an image URL or base64 data URL");
            else throw new NotSupportedException("image content is missing an image URL or file ID");
            if (!string.IsNullOrWhiteSpace(part.Detail)) image["detail"] = part.Detail;
            result.Add(image);
        }

        return result;
    }

    /// <summary>生成 Anthropic Messages 的有序文本、图片和文件块。</summary>
    public static List<object> ToAnthropicContent(AdapterMessage message)
    {
        if (message.ContentParts is not { Count: > 0 } parts)
            return string.IsNullOrEmpty(message.Content)
                ? []
                : [TextBlock("text", message.Content)];

        var result = new List<object>(parts.Count);
        foreach (var part in parts)
        {
            if (part.Kind == AdapterContentPartKind.Text)
            {
                result.Add(TextBlock("text", part.Text ?? string.Empty));
                continue;
            }

            if (part.Kind == AdapterContentPartKind.File)
            {
                result.Add(AdapterFileFormatter.ToAnthropicFile(part));
                continue;
            }

            var imageUrl = RequireImageUrl(part, "Anthropic Messages");
            Dictionary<string, object?> source;
            if (imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var separator = imageUrl.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
                if (separator <= 5 || separator + 8 >= imageUrl.Length)
                    throw new NotSupportedException("Anthropic image content requires a base64 data URL");
                source = new Dictionary<string, object?>
                {
                    ["type"] = "base64",
                    ["media_type"] = imageUrl[5..separator],
                    ["data"] = imageUrl[(separator + 8)..]
                };
            }
            else
            {
                source = new Dictionary<string, object?>
                {
                    ["type"] = "url",
                    ["url"] = imageUrl
                };
            }

            result.Add(new Dictionary<string, object?>
            {
                ["type"] = "image",
                ["source"] = source
            });
        }

        return result;
    }

    private static Dictionary<string, object?> TextBlock(string type, string text)
        => new() { ["type"] = type, ["text"] = text };

    private static string RequireImageUrl(AdapterContentPart part, string protocol)
    {
        if (!string.IsNullOrWhiteSpace(part.ImageUrl)) return part.ImageUrl;
        if (!string.IsNullOrWhiteSpace(part.FileId))
            throw new NotSupportedException($"image file ID cannot be converted to {protocol}; use an image URL or base64 data URL");
        throw new NotSupportedException("image content is missing an image URL or base64 data URL");
    }
}
