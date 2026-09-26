using System.Globalization;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;

namespace Router.Host.Web;

/// <summary>优先返回前端构建阶段生成的 Brotli 或 Gzip 静态文件。</summary>
public sealed class PrecompressedStaticFileMiddleware(
    RequestDelegate next,
    IWebHostEnvironment environment)
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private readonly IFileProvider _fileProvider = environment.WebRootFileProvider;
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method is not ("GET" or "HEAD"))
        {
            await _next(context);
            return;
        }

        var original = _fileProvider.GetFileInfo(context.Request.Path);
        var encoding = SelectEncoding(context.Request.Headers.AcceptEncoding.ToString());
        if (!original.Exists || original.IsDirectory || encoding is null)
        {
            await _next(context);
            return;
        }

        var compressed = _fileProvider.GetFileInfo(context.Request.Path + $".{encoding}");
        if (!compressed.Exists || compressed.IsDirectory || compressed.PhysicalPath is null)
        {
            await _next(context);
            return;
        }

        context.Response.Headers["Content-Encoding"] = encoding;
        context.Response.Headers.Vary = "Accept-Encoding";
        context.Response.ContentLength = compressed.Length;
        if (ContentTypes.TryGetContentType(original.Name, out var contentType))
            context.Response.ContentType = contentType;

        if (context.Request.Method == "HEAD") return;
        await context.Response.SendFileAsync(compressed.PhysicalPath);
    }

    private static string? SelectEncoding(string header)
    {
        var brotliQuality = -1.0;
        var gzipQuality = -1.0;
        var wildcardQuality = -1.0;
        foreach (var value in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var name = parts[0];
            var quality = ParseQuality(parts);
            if (name.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                brotliQuality = quality;
                continue;
            }

            if (name.Equals("gzip", StringComparison.OrdinalIgnoreCase))
            {
                gzipQuality = quality;
                continue;
            }

            if (name == "*")
                wildcardQuality = quality;
        }

        if (brotliQuality < 0) brotliQuality = wildcardQuality;
        if (gzipQuality < 0) gzipQuality = wildcardQuality;
        if (brotliQuality > 0 && brotliQuality >= gzipQuality) return "br";
        return gzipQuality > 0 ? "gzip" : null;
    }

    private static double ParseQuality(IReadOnlyList<string> parts)
    {
        foreach (var part in parts.Skip(1))
        {
            if (!part.StartsWith("q=", StringComparison.OrdinalIgnoreCase)) continue;
            return double.TryParse(
                part[2..],
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var quality)
                ? quality
                : 0;
        }

        return 1;
    }
}
