using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Router.Host.Configuration;

namespace Router.Host.Security;

/// <summary>为受保护的管理请求附加管理员身份。</summary>
public sealed class AdminSessionMiddleware(
    RequestDelegate next,
    AdminAuthService auth,
    IOptionsMonitor<AuthOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var isAdminApi = context.Request.Path.StartsWithSegments("/api/admin");
        if (!isAdminApi)
        {
            await next(context);
            return;
        }

        if (IsUnsafeMethod(context.Request.Method) && !HasValidOrigin(context))
        {
            await Results.Json(new { error = "origin validation failed" }, statusCode: 403).ExecuteAsync(context);
            return;
        }

        var bypass = context.Request.Path.StartsWithSegments("/api/admin/login")
            || context.Request.Path.StartsWithSegments("/api/admin/health")
            || context.Request.Path.StartsWithSegments("/api/admin/setup");

        if (bypass || !options.CurrentValue.Admin.Enabled)
        {
            await next(context);
            return;
        }

        var cookie = context.Request.Cookies["router_admin_session"];
        var principal = cookie is null ? null : auth.Validate(cookie);
        if (principal is null)
        {
            await Results.Json(new { error = "admin authentication required" }, statusCode: 401)
                .ExecuteAsync(context);
            return;
        }

        if (!principal.Roles.Contains("admin", StringComparer.OrdinalIgnoreCase))
        {
            await Results.Json(new { error = "admin role required" }, statusCode: 403).ExecuteAsync(context);
            return;
        }

        if (IsUnsafeMethod(context.Request.Method) && !HasValidCsrfToken(context))
        {
            await Results.Json(new { error = "csrf validation failed" }, statusCode: 403).ExecuteAsync(context);
            return;
        }

        context.Items[typeof(AdminPrincipal)] = principal;
        await next(context);
    }

    private static bool IsUnsafeMethod(string method)
        => HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    private static bool HasValidOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin)) return true;
        var expected = $"{context.Request.Scheme}://{context.Request.Host}";
        return string.Equals(origin.TrimEnd('/'), expected.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasValidCsrfToken(HttpContext context)
    {
        var cookie = context.Request.Cookies["router_admin_csrf"];
        var header = context.Request.Headers["X-CSRF-Token"].ToString();
        return !string.IsNullOrWhiteSpace(cookie)
            && !string.IsNullOrWhiteSpace(header)
            && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(cookie),
                System.Text.Encoding.UTF8.GetBytes(header));
    }
}

/// <summary>校验下游模型 API 使用的全局 Bearer Key。</summary>
public sealed class DownstreamApiKeyMiddleware(
    RequestDelegate next,
    IOptionsMonitor<AuthOptions> options,
    ApiKeyService apiKeys,
    AdminAuthService adminAuth)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/v1")
            || !options.CurrentValue.ApiKey.Enabled)
        {
            await next(context);
            return;
        }

        // The admin test console may read the model catalog with its existing session.
        // Other /v1 requests still require the downstream Bearer key.
        if (IsAdminModelCatalogRequest(context, adminAuth))
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        var expected = apiKeys.Current;
        var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : context.Request.Headers["x-api-key"].ToString().Trim();

        if (string.IsNullOrWhiteSpace(expected)
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expected),
                System.Text.Encoding.UTF8.GetBytes(supplied)))
        {
            await Results.Json(new { error = "invalid api key" }, statusCode: 401)
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static bool IsAdminModelCatalogRequest(HttpContext context, AdminAuthService adminAuth)
    {
        if (!HttpMethods.IsGet(context.Request.Method)
            || !string.Equals(context.Request.Path.Value, "/v1/models", StringComparison.OrdinalIgnoreCase))
            return false;

        var sessionId = context.Request.Cookies["router_admin_session"];
        var principal = string.IsNullOrWhiteSpace(sessionId) ? null : adminAuth.Validate(sessionId);
        return principal?.Roles.Contains("admin", StringComparer.OrdinalIgnoreCase) == true;
    }
}
