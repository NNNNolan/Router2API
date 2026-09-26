namespace Router.Host.Tracing;

/// <summary>为每个 HTTP 请求创建并传播 Router2API 的关联 TraceId。</summary>
public sealed class TraceIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = TraceIdContext.GetOrCreate(context);
        context.Response.OnStarting(static state =>
        {
            var (response, value) = ((HttpResponse Response, string TraceId))state;
            response.Headers[TraceIdContext.HeaderName] = value;
            response.Headers[TraceIdContext.NewApiHeaderName] = value;
            return Task.CompletedTask;
        }, (context.Response, traceId));

        await next(context);
    }
}

/// <summary>读取或创建当前 HTTP 请求的 TraceId。</summary>
public static class TraceIdContext
{
    public const string HeaderName = "X-Trace-Id";
    public const string NewApiHeaderName = "X-Oneapi-Request-Id";

    private const string ItemKey = "Router2API.TraceId";
    private const int MaxLength = 128;

    public static string GetOrCreate(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items[ItemKey] is string existing && IsSafe(existing))
            return existing;

        var supplied = context.Request.Headers[HeaderName].ToString();
        if (!IsSafe(supplied))
            supplied = context.Request.Headers[NewApiHeaderName].ToString();

        var traceId = IsSafe(supplied) ? supplied : Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = traceId;
        return traceId;
    }

    private static bool IsSafe(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxLength
            && value.All(static character => char.IsLetterOrDigit(character)
                || character is '-' or '_' or ':' or '.');
}
