using System.Text;

namespace Router.Contracts.Domain;

internal static class AdapterFileFormatter
{
    internal static object ToChatFile(AdapterContentPart part)
    {
        RejectFileId(part, "Chat Completions");
        if (IsPdf(part))
        {
            if (string.IsNullOrWhiteSpace(part.FileData))
                throw new NotSupportedException("Chat Completions PDF input requires file_data; file_url is only supported by Responses");
            var (mediaType, _) = RequireBase64DataUrl(part.FileData, "PDF");
            if (!mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Chat Completions PDF input requires application/pdf file_data");
            return new Dictionary<string, object?>
            {
                ["type"] = "file",
                ["file"] = new Dictionary<string, object?>
                {
                    ["filename"] = part.FileName ?? "document.pdf",
                    ["file_data"] = part.FileData
                }
            };
        }

        return ToTextFile(part);
    }

    internal static object ToResponsesFile(AdapterContentPart part)
    {
        RejectFileId(part, "Responses");
        var file = new Dictionary<string, object?> { ["type"] = "input_file" };
        if (!string.IsNullOrWhiteSpace(part.FileData)) file["file_data"] = part.FileData;
        else if (!string.IsNullOrWhiteSpace(part.FileUrl)) file["file_url"] = part.FileUrl;
        else throw new NotSupportedException("file content is missing file data or URL");
        if (!string.IsNullOrWhiteSpace(part.FileName)) file["filename"] = part.FileName;
        if (!string.IsNullOrWhiteSpace(part.Detail)) file["detail"] = part.Detail;
        return file;
    }

    internal static object ToAnthropicFile(AdapterContentPart part)
    {
        RejectFileId(part, "Anthropic Messages");
        if (IsPdf(part))
        {
            Dictionary<string, object?> source;
            if (!string.IsNullOrWhiteSpace(part.FileData))
            {
                var (mediaType, data) = RequireBase64DataUrl(part.FileData, "PDF");
                if (!mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Anthropic PDF document requires application/pdf file_data");
                source = new Dictionary<string, object?>
                {
                    ["type"] = "base64",
                    ["media_type"] = "application/pdf",
                    ["data"] = data
                };
            }
            else if (!string.IsNullOrWhiteSpace(part.FileUrl))
            {
                source = new Dictionary<string, object?>
                {
                    ["type"] = "url",
                    ["url"] = part.FileUrl
                };
            }
            else throw new NotSupportedException("PDF content is missing file data or URL");

            var document = new Dictionary<string, object?>
            {
                ["type"] = "document",
                ["source"] = source
            };
            if (!string.IsNullOrWhiteSpace(part.FileName)) document["title"] = part.FileName;
            return document;
        }

        return ToTextFile(part);
    }

    private static Dictionary<string, object?> ToTextFile(AdapterContentPart part)
    {
        if (!IsTextFile(part))
            throw new NotSupportedException("this file type cannot be converted to Chat or Anthropic Messages; use a Responses-native model or convert the file to PDF/text");
        if (string.IsNullOrWhiteSpace(part.FileData))
            throw new NotSupportedException("text file conversion requires inline file_data; file_url requires a Responses-native model");

        var (_, encoded) = RequireBase64DataUrl(part.FileData, "text file");
        const int maxTextBytes = 1024 * 1024;
        if (encoded.Length > maxTextBytes * 4 / 3 + 4)
            throw new NotSupportedException("text file is too large to inline as a message (limit: 1 MiB)");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new NotSupportedException("text file_data is not valid base64"); }
        if (bytes.Length > maxTextBytes)
            throw new NotSupportedException("text file is too large to inline as a message (limit: 1 MiB)");
        string content;
        try { content = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new NotSupportedException("text file_data is not valid UTF-8"); }
        return new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["text"] = $"File: {part.FileName ?? "attachment"}\n{content}"
        };
    }

    private static (string MediaType, string Base64) RequireBase64DataUrl(string value, string kind)
    {
        var separator = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || separator <= 5 || separator + 8 >= value.Length)
            throw new NotSupportedException($"{kind} content requires a base64 data URL");
        return (value[5..separator], value[(separator + 8)..]);
    }

    private static bool IsPdf(AdapterContentPart part)
    {
        var mediaType = GetMediaType(part);
        if (!string.IsNullOrWhiteSpace(mediaType))
            return mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
        return part.FileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true
            || part.FileUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextFile(AdapterContentPart part)
    {
        var mediaType = GetMediaType(part);
        if (mediaType is not null)
            return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/yaml", StringComparison.OrdinalIgnoreCase);
        var extension = Path.GetExtension(part.FileName ?? string.Empty).ToLowerInvariant();
        return extension is ".txt" or ".md" or ".csv" or ".tsv" or ".json" or ".html" or ".htm"
            or ".xml" or ".yaml" or ".yml" or ".log" or ".cs" or ".py" or ".js"
            or ".ts" or ".go" or ".java" or ".sql" or ".sh" or ".ps1";
    }

    private static string? GetMediaType(AdapterContentPart part)
    {
        if (!string.IsNullOrWhiteSpace(part.MediaType)) return part.MediaType;
        if (part.FileData?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var separator = part.FileData.IndexOf(';');
            if (separator > 5) return part.FileData[5..separator];
        }
        return null;
    }

    private static void RejectFileId(AdapterContentPart part, string protocol)
    {
        if (!string.IsNullOrWhiteSpace(part.FileId))
            throw new NotSupportedException($"file ID cannot be converted to {protocol}; use inline base64 file_data or a supported URL");
    }
}
