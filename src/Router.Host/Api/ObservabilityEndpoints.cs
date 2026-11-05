using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Router.Host.Configuration;
using Router.Host.Privacy;
using Router.Infrastructure.Services;
using Router.Infrastructure.Services.Privacy;

namespace Router.Host.Api;

/// <summary>只映射 /api/admin，认证、同源和 CSRF 边界继续由宿主中间件统一执行。</summary>
internal static class ObservabilityEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/admin/dashboard/activity-heatmap", Heatmap);
        app.MapGet("/api/admin/privacy/settings", (HttpContext context) =>
            Results.Ok(context.RequestServices.GetRequiredService<PrivacyRequestGuard>().Settings));
        app.MapPut("/api/admin/privacy/settings", SavePrivacy);
        app.MapPost("/api/admin/privacy/test", TestPrivacy);
        app.MapGet("/api/admin/privacy/events", (HttpContext context, RequestObservationsService observations) =>
            Query(context, observations, privacy: true));
        app.MapGet("/api/admin/privacy/summary", (HttpContext context, RequestObservationsService observations) =>
            InRange(context, (from, to) => Results.Ok(observations.PrivacySummary(from, to, context.RequestAborted))));
        app.MapGet("/api/admin/traces/requests", (HttpContext context, RequestObservationsService observations) =>
            Query(context, observations, privacy: false));
        app.MapGet("/api/admin/traces/requests/{id}", (string id, RequestObservationsService observations) =>
            id.Length <= 64 && observations.Find(id) is { } request
                ? Results.Ok(request) : Results.NotFound(new { error = "请求记录不存在。" }));
        app.MapGet("/api/admin/traces/topology", (HttpContext context, RequestObservationsService observations) =>
            InRange(context, (from, to) => Results.Ok(observations.Topology(from, to, PluginFilter(context), context.RequestAborted))));
    }

    private static async Task<IResult> Heatmap(HttpContext context, ActivityHeatmapService heatmap, IOptionsMonitor<LogRetentionOptions> logging)
    {
        var name = context.Request.Query["timezone"].ToString();
        TimeZoneInfo zone;
        try { zone = string.IsNullOrWhiteSpace(name) ? TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai") : TimeZoneInfo.FindSystemTimeZoneById(name); }
        catch (TimeZoneNotFoundException) { return Results.BadRequest(new { error = "时区无效。" }); }
        catch (InvalidTimeZoneException) { return Results.BadRequest(new { error = "时区无效。" }); }
        return Results.Ok(await heatmap.QueryAsync(zone, DateTimeOffset.UtcNow, logging.CurrentValue.DataRetentionDays, context.RequestAborted));
    }

    private static async Task<IResult> SavePrivacy(PrivacyOptions body, IConfigFileService config, HttpContext context)
    {
        try
        {
            await config.SaveAsync(new() { Privacy = body }, context.RequestAborted);
            return Results.Ok(context.RequestServices.GetRequiredService<PrivacyRequestGuard>().Settings);
        }
        catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
    }

    private static IResult TestPrivacy(JsonElement body, HttpContext context)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.String || text.GetString()!.Length > 64 * 1024)
            return Results.BadRequest(new { error = "请输入最多 65536 字符的检测文本。" });
        using var inspection = context.RequestServices.GetRequiredService<PrivacyRequestGuard>().BeginTest();
        try
        {
            var transformed = inspection.Inspect(text.GetString()!);
            return Results.Ok(new { report = inspection.Report(), text = transformed });
        }
        catch (PrivacyInspectionException exception)
        {
            return Results.Json(new { error = exception.Message, report = inspection.Report() }, statusCode: 422);
        }
    }

    private static IResult Query(HttpContext context, RequestObservationsService observations, bool privacy)
        => InRange(context, (from, to) =>
        {
            var pageText = context.Request.Query["page"].ToString();
            var sizeText = context.Request.Query["pageSize"].ToString();
            if (pageText.Length > 0 && (!int.TryParse(pageText, out var requestedPage) || requestedPage is < 1 or > 100000)
                || sizeText.Length > 0 && (!int.TryParse(sizeText, out var requestedSize) || requestedSize is not (20 or 50 or 100)))
                return Results.BadRequest(new { error = "分页参数无效。" });
            var page = pageText.Length == 0 ? 1 : int.Parse(pageText, CultureInfo.InvariantCulture);
            var size = sizeText.Length == 0 ? 20 : int.Parse(sizeText, CultureInfo.InvariantCulture);
            var result = observations.Requests(from, to, page, size, PluginFilter(context), privacy, context.RequestAborted);
            return Results.Ok(new { result.Items, result.Page, result.PageSize, result.Total, result.TotalPages });
        });

    private static IResult InRange(HttpContext context, Func<DateTimeOffset, DateTimeOffset, IResult> run)
    {
        var query = context.Request.Query;
        var fromText = query["fromUtc"].ToString();
        var toText = query["toUtc"].ToString();
        var to = DateTimeOffset.UtcNow;
        var from = to.AddHours(-24);
        if (fromText.Length > 0 && !DateTimeOffset.TryParse(fromText, CultureInfo.InvariantCulture, DateTimeStyles.None, out from)
            || toText.Length > 0 && !DateTimeOffset.TryParse(toText, CultureInfo.InvariantCulture, DateTimeStyles.None, out to)
            || to <= from || to - from > TimeSpan.FromDays(3660) || PluginFilter(context)?.Length > 128)
            return Results.BadRequest(new { error = "查询范围或插件参数无效。" });
        context.Response.Headers.CacheControl = "no-store";
        return run(from.ToUniversalTime(), to.ToUniversalTime());
    }

    private static string? PluginFilter(HttpContext context)
        => context.Request.Query["pluginKey"].ToString().Trim() is { Length: > 0 } value ? value : null;
}
