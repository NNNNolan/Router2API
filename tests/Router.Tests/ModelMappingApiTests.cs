using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;
using Router.Host.Api;
using Router.Host.Configuration;
using Router.Host.Pipeline;
using Router.Host.Plugins;
using Router.Host.Security;
using Router.Infrastructure;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class ModelMappingApiTests
{
    [TestMethod]
    public async Task AdminCrudAuthorizationCatalogAndAllConversationEndpointsWorkTogether()
    {
        // 使用独立 TestServer 和数据库，不启动 Program，不读取本机配置、插件或 User Secrets。
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Path"] = Path.Combine(AppContext.BaseDirectory, "test-data", $"mapping-api-{Guid.NewGuid():N}.db"),
            ["Redis:ConnectionString"] = ""
        });
        var password = Guid.NewGuid().ToString("N");
        var apiKey = Guid.NewGuid().ToString("N");
        builder.Services.Configure<AuthOptions>(options =>
        {
            options.Admin.Users.Add(new() { Username = "mapping-admin", Password = password });
            options.ApiKey.Key = apiKey;
        });
        builder.Services.AddSingleton<PasswordHasher>();
        builder.Services.AddSingleton<AdminAuthService>();
        builder.Services.AddSingleton<ApiKeyService>();
        builder.Services.AddSingleton(Mock.Of<IConfigFileService>());
        builder.Services.AddSingleton(Mock.Of<IPluginCatalog>());
        builder.Services.AddSingleton(Mock.Of<IProxySubscriptionRefreshQueue>());
        builder.Services.AddRouterInfrastructure(builder.Configuration);
        builder.Services.AddSingleton<RouterPipeline>();
        builder.Services.AddSingleton(Mock.Of<IModelMetadataCatalog>());
        builder.Services.AddSingleton(Mock.Of<ISharedKeyValueStore>());
        var catalog = new Mock<IModelCatalog>();
        catalog.Setup(value => value.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new("a/real-a", "real-a")]);
        builder.Services.AddSingleton(catalog.Object);
        var accounts = new Mock<IAccountService>();
        accounts.Setup(value => value.QueryAsync(It.IsAny<AccountQuery>(), It.IsAny<CancellationToken>()))
            .Returns((AccountQuery query, CancellationToken _) => Task.FromResult(new PagedResult<Account>(
                [new Account { Id = "test-account", PluginKey = query.PluginKey!, Platform = query.Platform! }], 1, 100, 1)));
        builder.Services.AddSingleton(accounts.Object);
        builder.Services.AddCors(options => options.AddPolicy("v1", policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        await using var app = builder.Build();
        app.Services.GetRequiredService<DatabaseInitializer>().Initialize();
        var calls = new List<string>();
        var terminal = new Mock<IPlatformTerminal>();
        terminal.Setup(value => value.InvokeAsync(It.IsAny<PluginAttemptContext>())).Returns((PluginAttemptContext context) =>
        {
            calls.Add(context.PlatformName);
            return Task.FromResult(context.PlatformName == "a"
                ? new PluginInvocationResult(new AdapterResponse { StatusCode = 503, Error = "try next" }, new PluginAttemptDecision
                {
                    FailureKind = PluginFailureKind.Upstream, Retry = PluginRetryAction.NextAttempt, ReasonCode = "test"
                }.ToResult(503))
                : new PluginInvocationResult(new AdapterResponse { StatusCode = 200, Completion = new("real-b", "ok", Usage: new(1, 1, 2)) },
                    new PluginAttemptResult(PluginAttemptOutcome.Healthy)));
        });
        var platforms = app.Services.GetRequiredService<IPlatformRegistry>();
        platforms.Register(new("a", "a", "a", terminal.Object));
        platforms.Register(new("b", "b", "b", terminal.Object));
        var policies = app.Services.GetRequiredService<IPluginPolicyRegistry>();
        policies.Set("a", new PluginProxyPolicyBuilder().MaxAttempts(5).Build());
        app.UseCors("v1");
        app.UseMiddleware<DownstreamApiKeyMiddleware>();
        app.UseMiddleware<AdminSessionMiddleware>();
        ApiEndpoints.Map(app);
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var appearance = await client.GetAsync("/api/appearance");
        appearance.EnsureSuccessStatusCode();
        appearance.Headers.CacheControl!.NoStore.Should().BeTrue();
        var publicSettings = await appearance.Content.ReadFromJsonAsync<JsonElement>();
        publicSettings.EnumerateObject().Should().ContainSingle();
        publicSettings.GetProperty("theme").GetString().Should().Be("minimal");
        (await client.GetAsync("/api/admin/model-mappings")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var login = await client.PostAsJsonAsync("/api/admin/login", new { username = "mapping-admin", password });
        login.EnsureSuccessStatusCode();
        var cookies = login.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';')[0]).ToArray();
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
        var mapping = new ModelMapping("", "ds4", true, [new("a", "a", "real-a", RetryCount: 2), new("b", "b", "real-b", RetryCount: 1)]);
        (await client.PostAsJsonAsync("/api/admin/model-mappings", mapping)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Add("X-CSRF-Token", cookies.Single(cookie => cookie.StartsWith("router_admin_csrf=", StringComparison.Ordinal)).Split('=')[1]);
        client.DefaultRequestHeaders.Add("Origin", "https://foreign.example");
        (await client.PostAsJsonAsync("/api/admin/model-mappings", mapping)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Remove("Origin");
        var created = await client.PostAsJsonAsync("/api/admin/model-mappings", mapping);
        created.EnsureSuccessStatusCode();
        var saved = (await created.Content.ReadFromJsonAsync<ModelMapping>())!;
        saved.Targets[0].FailureThreshold.Should().Be(3);
        saved.Targets[0].CooldownSeconds.Should().Be(300);
        (await client.PostAsJsonAsync("/api/admin/model-mappings", mapping)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync("/api/admin/model-mappings", mapping with { Name = "a/model" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/admin/model-mappings", mapping with { Name = "invalid-cooldown", Targets = [mapping.Targets[0] with { CooldownSeconds = -1 }] })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetFromJsonAsync<ModelMapping[]>("/api/admin/model-mappings"))!.Should().ContainSingle();
        var models = await client.GetFromJsonAsync<JsonElement>("/v1/models");
        models.GetProperty("data").EnumerateArray().Select(model => model.GetProperty("id").GetString()).Should().Contain("ds4");
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        var requestNumber = 0;
        foreach (var endpoint in new[] { "/v1/chat/completions", "/v1/completions", "/v1/responses", "/v1/messages" })
        {
            calls.Clear();
            var response = await client.PostAsJsonAsync(endpoint, new
            {
                model = "ds4", messages = new[] { new { role = "user", content = "hello" } }, input = "hello", prompt = "hello", max_tokens = 32
            });
            response.EnsureSuccessStatusCode();
            (await response.Content.ReadAsStringAsync()).Should().Contain("ok");
            string[] expectedCalls = requestNumber++ < 3 ? ["a", "a", "b"] : ["b"];
            calls.Should().Equal(expectedCalls, "the fourth endpoint shares the target cooldown after three failed requests");
        }
        (await client.PutAsJsonAsync($"/api/admin/model-mappings/{saved.Id}", saved with { Enabled = false })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/v1/chat/completions", new { model = "ds4", messages = Array.Empty<object>() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"/api/admin/model-mappings/{saved.Id}")).EnsureSuccessStatusCode();
        (await client.GetFromJsonAsync<ModelMapping[]>("/api/admin/model-mappings"))!.Should().BeEmpty();
    }
}
