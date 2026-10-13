using System.Collections;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Host;
using Router.Contracts.Plugins;
using Router.Host.Configuration;
using Router.Host.Plugins;
using Router.Host.Security;
using Router.Infrastructure.Services;

namespace Router.Tests;

/// <summary>直接执行 Catalog 端点分发与 HTTP 写出，不启动生产宿主或载入提供方插件。</summary>
[TestClass]
public sealed class PluginPublicEndpointTests
{
    private static readonly string[] Methods = ["GET", "POST"];

    /// <summary>匿名页面按 UTF-8 HTML 原文写出，无 JSON 引号，并禁止缓存和嵌入。</summary>
    [TestMethod]
    public async Task AnonymousHtmlIsWrittenVerbatim()
    {
        const string html = "<!doctype html><html lang=\"zh-CN\"><h1>账号授权</h1></html>";
        await using var fixture = new Fixture(PluginAuthPolicy.Anonymous,
            _ => Task.FromResult(new PluginResult { StatusCode = 200, Body = html, ContentType = "text/html; charset=utf-8" }));
        var context = await fixture.CallAsync("GET");
        Assert.AreEqual(200, context.Response.StatusCode);
        Assert.AreEqual("text/html; charset=utf-8", context.Response.ContentType);
        Assert.AreEqual(html, Body(context));
        Assert.AreEqual("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.AreEqual("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
        StringAssert.Contains(context.Response.Headers.ContentSecurityPolicy.ToString(), "frame-ancestors 'none'");
        Assert.AreEqual(0, fixture.Catalog.Get("endpoint-test")!.InFlight);
    }

    /// <summary>匿名写操作不需要管理员 Cookie，但拒绝跨站表单、跨源调用和过大正文。</summary>
    /// <param name="contentType">请求媒体类型。</param>
    /// <param name="origin">请求 Origin。</param>
    /// <param name="site">浏览器 Fetch Metadata。</param>
    /// <param name="size">声明的正文长度。</param>
    /// <param name="expected">预期 HTTP 状态码。</param>
    [TestMethod]
    [DataRow("application/json", null, null, 2, 200)]
    [DataRow("application/json", "https://router.example", "same-origin", 2, 200)]
    [DataRow("application/json", "https://evil.example", null, 2, 403)]
    [DataRow("application/json", null, "cross-site", 2, 403)]
    [DataRow("application/json", null, "same-site", 2, 403)]
    [DataRow("text/plain", null, null, 2, 403)]
    [DataRow("application/x-www-form-urlencoded", null, null, 2, 403)]
    [DataRow("application/json", null, null, 1_048_577, 413)]
    public async Task AnonymousWritesKeepRequestBoundaries(string contentType, string? origin, string? site, int size, int expected)
    {
        var calls = 0;
        await using var fixture = new Fixture(PluginAuthPolicy.Anonymous, context =>
        {
            calls++;
            return Task.FromResult(context.Ok(new { done = false }));
        });
        var result = await fixture.CallAsync("POST", context =>
        {
            context.Request.ContentType = contentType;
            context.Request.ContentLength = size;
            if (origin is not null) context.Request.Headers.Origin = origin;
            if (site is not null) context.Request.Headers["Sec-Fetch-Site"] = site;
        });
        Assert.AreEqual(expected, result.Response.StatusCode);
        Assert.AreEqual(expected == 200 ? 1 : 0, calls);
    }

    /// <summary>除了明确 Anonymous，旧策略仍受管理员认证保护；Internal 永远不公开。</summary>
    /// <param name="policy">已有插件声明策略。</param>
    /// <param name="expected">预期未登录状态码。</param>
    [TestMethod]
    [DataRow(PluginAuthPolicy.AdminSession, 401)]
    [DataRow(PluginAuthPolicy.ApiKey, 401)]
    [DataRow(PluginAuthPolicy.AdminSessionOrApiKey, 401)]
    [DataRow(PluginAuthPolicy.Custom, 401)]
    [DataRow(PluginAuthPolicy.Internal, 404)]
    public async Task OtherPoliciesRemainProtected(PluginAuthPolicy policy, int expected)
    {
        await using var fixture = new Fixture(policy);
        var response = await fixture.CallAsync("GET", context => context.Request.Headers.Authorization = "Bearer test-key");
        Assert.AreEqual(expected, response.Response.StatusCode);
        Assert.AreEqual(PluginAuthPolicy.AdminSession, new PluginEndpointAttribute("GET", "page").Auth);
    }

    /// <summary>管理员写接口仍检查 Cookie/CSRF/Origin，不因支持匿名而失效。</summary>
    [TestMethod]
    public async Task AdminWritesStillRequireCsrfAndSameOrigin()
    {
        await using var fixture = new Fixture(PluginAuthPolicy.AdminSession);
        var missing = await fixture.CallAsync("POST", context => context.Request.Headers.Cookie = fixture.Cookie);
        Assert.AreEqual(403, missing.Response.StatusCode);
        var allowed = await fixture.CallAsync("POST", context =>
        {
            context.Request.Headers.Cookie = fixture.Cookie;
            context.Request.Headers["X-CSRF-Token"] = "test-csrf";
        });
        Assert.AreEqual(200, allowed.Response.StatusCode);
        var crossSite = await fixture.CallAsync("POST", context =>
        {
            context.Request.Headers.Cookie = fixture.Cookie;
            context.Request.Headers["X-CSRF-Token"] = "test-csrf";
            context.Request.Headers.Origin = "https://evil.example";
        });
        Assert.AreEqual(403, crossSite.Response.StatusCode);
    }

    /// <summary>普通 JSON 字符串继续按 JSON 编码，非字符串不得伪装为 HTML。</summary>
    [TestMethod]
    public async Task JsonAndHtmlHaveSeparateSerializationRules()
    {
        await using var json = new Fixture(PluginAuthPolicy.Anonymous,
            context => Task.FromResult(context.Ok("hello")));
        Assert.AreEqual("\"hello\"", Body(await json.CallAsync("GET")));
        await using var invalid = new Fixture(PluginAuthPolicy.Anonymous,
            _ => Task.FromResult(new PluginResult { StatusCode = 200, ContentType = "text/html", Body = new { secret = "do-not-echo" } }));
        var result = await invalid.CallAsync("GET");
        Assert.AreEqual(500, result.Response.StatusCode);
        Assert.IsFalse(Body(result).Contains("do-not-echo", StringComparison.Ordinal));
    }

    private static string Body(HttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();
        private readonly ApiKeyService _apiKeys;
        public PluginCatalog Catalog { get; }
        public string Cookie { get; }

        public Fixture(PluginAuthPolicy policy, Func<PluginHttpContext, Task<PluginResult>>? handler = null)
        {
            var options = new Mock<IOptionsMonitor<AuthOptions>>();
            options.SetupGet(value => value.CurrentValue).Returns(new AuthOptions
            {
                Admin = new AdminOptions { Users = [new AdminUserOptions { Username = "admin", Password = "test-password" }] },
                ApiKey = new ApiKeyOptions { Key = "test-key" }
            });
            var auth = new AdminAuthService(options.Object, new PasswordHasher(), NullLogger<AdminAuthService>.Instance);
            Cookie = $"router_admin_session={auth.Login("admin", "test-password").SessionId}; router_admin_csrf=test-csrf";
            _apiKeys = new ApiKeyService(options.Object);
            Catalog = new PluginCatalog(new PlatformRegistry(), Mock.Of<IModelCatalog>(), Mock.Of<IPluginHostFactory>(),
                new PluginPolicyRegistry(), auth, _apiKeys, Mock.Of<IPluginLogSink>(), NullLogger<PluginCatalog>.Instance,
                Options.Create(new PluginExecutionOptions()));
            handler ??= context => Task.FromResult(context.Ok(new { ok = true }));
            var endpoints = Methods.Select(method =>
                new PluginEndpointDefinition("endpoint-test", "endpoint-test", method, "page", policy, handler)).ToArray();
            var runtimeType = typeof(PluginCatalog).GetNestedType("RuntimePlugin", BindingFlags.NonPublic)!;
            var trackedType = typeof(PluginCatalog).GetNestedType("TrackedTerminal", BindingFlags.NonPublic)!;
            var descriptor = new PluginDescriptor("endpoint-test", "endpoint-test", "1", "Active",
                Path.Combine(Path.GetTempPath(), "Router2API-EndpointTests", Guid.NewGuid().ToString("N")), DateTimeOffset.UtcNow, 0);
            var runtime = Activator.CreateInstance(runtimeType,
                [descriptor, Array.Empty<PlatformRegistration>(), Array.CreateInstance(trackedType, 0), endpoints, null, null])!;
            var plugins = (IDictionary)typeof(PluginCatalog).GetField("_plugins", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Catalog)!;
            plugins.Add("endpoint-test", runtime);
        }

        public async Task<DefaultHttpContext> CallAsync(string method, Action<HttpContext>? configure = null)
        {
            var context = new DefaultHttpContext { RequestServices = _services };
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("router.example");
            context.Request.Method = method;
            context.Request.RouteValues["pluginKey"] = "endpoint-test";
            context.Request.RouteValues["route"] = "page";
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream("{}"u8.ToArray());
            context.Request.ContentLength = 2;
            context.Response.Body = new MemoryStream();
            configure?.Invoke(context);
            var invoke = typeof(PluginCatalog).GetMethod("InvokeEndpointAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var result = await (Task<IResult>)invoke.Invoke(Catalog, [context])!;
            await result.ExecuteAsync(context);
            context.Request.Body.Dispose();
            return context;
        }

        public async ValueTask DisposeAsync()
        {
            await Catalog.DisposeAsync();
            _apiKeys.Dispose();
            await _services.DisposeAsync();
        }
    }
}
