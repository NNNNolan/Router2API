using Microsoft.AspNetCore.Http;

namespace Router.Host.Web;

/// <summary>Collapses repeated identical Content-Type values before endpoint body binding.</summary>
public sealed class NormalizeDuplicateContentTypeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var contentType = context.Request.ContentType;
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var values = contentType.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length > 1
                && values.All(value => value.Equals(values[0], StringComparison.OrdinalIgnoreCase)))
                context.Request.ContentType = values[0];
        }

        await next(context);
    }
}
