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
using Router.Host.Api;
using Router.Host.Configuration;
using Router.Host.Pipeline;
using Router.Host.Plugins;
using Router.Host.Privacy;
using Router.Host.Security;
using Router.Host.Services;
using Router.Host.Tracing;
using Router.Infrastructure;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;
using Router.Infrastructure.Services.Privacy;

namespace Router.Tests;

[TestClass]
public sealed class ObservabilityIntegrationTests
{
    [TestMethod]
    public async Task AdminAuthGuardAllProtocolsAndQueriesShareOnePersistentSource()
    {
        await using var host = await Host.Create();
        using var client = host.App.GetTestClient();
        foreach (var path in new[] { "/api/admin/privacy/settings", "/api/admin/privacy/events", "/api/admin/traces/topology", "/api/admin/traces/requests" })
            (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await host.Login(client);
        using var untrusted = await client.PutAsJsonAsync("/api/admin/privacy/settings", PrivacyGuardTests.Settings("redact"));
        untrusted.StatusCode.Should().Be(HttpStatusCode.Forbidden, "管理员写接口仍要求 CSRF");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", host.Csrf);
        using var saved = await client.PutAsJsonAsync("/api/admin/privacy/settings", PrivacyGuardTests.Settings("redact"));
        saved.EnsureSuccessStatusCode();
        (await saved.Content.ReadFromJsonAsync<PrivacyOptions>())!.DryRun.Should().BeFalse();
        client.DefaultRequestHeaders.Authorization = new("Bearer", host.ApiKey);
        client.DefaultRequestHeaders.Add("X-Trace-Id", "same-external-id");
        foreach (var (endpoint, body) in new[]
        {
            ("/v1/chat/completions", """{"model":"demo/model","messages":[{"role":"user","content":"person@example.com"}]}"""),
            ("/v1/completions", """{"model":"demo/model","prompt":"person@example.com"}"""),
            ("/v1/responses", """{"model":"demo/model","input":"person@example.com"}"""),
            ("/v1/messages", """{"model":"demo/model","max_tokens":100,"messages":[{"role":"user","content":"person@example.com"}]}""")
        })
        {
            using var request = JsonDocument.Parse(body);
            using var response = await client.PostAsJsonAsync(endpoint, request.RootElement);
            response.EnsureSuccessStatusCode();
            (await response.Content.ReadAsStringAsync()).Should().Contain("person@example.com").And.NotContain("[R2A_");
            host.Received.Last().Should().Contain("[R2A_").And.NotContain("person@example.com");
        }
        using var block = await client.PutAsJsonAsync("/api/admin/privacy/settings", PrivacyGuardTests.Settings("block"));
        block.EnsureSuccessStatusCode();
        using var blocked = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "demo/model", messages = new[] { new { role = "user", content = "person@example.com" } } });
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        host.Received.Should().HaveCount(4, "阻断发生在插件、选号和重试之前");
        var summary = await client.GetFromJsonAsync<PrivacySummary>("/api/admin/privacy/summary");
        summary!.Inspected.Should().Be(5);
        summary.Blocked.Should().Be(1);
        summary.Redactions.Should().Be(4);
        var rows = await client.GetFromJsonAsync<PagedResult<ObservedRequest>>("/api/admin/traces/requests");
        rows!.Items.Should().HaveCount(5);
        rows.Items.Select(row => row.Id).Distinct().Should().HaveCount(5);
        rows.Items.Select(row => row.TraceId).Distinct().Should().ContainSingle().Which.Should().Be("same-external-id");
        var blockedRow = rows.Items.Single(row => row.StatusCode == 403);
        blockedRow.Trace!.Stages.Should().ContainSingle(stage => stage.Name == "privacy" && stage.Outcome == "blocked");
        blockedRow.Trace.Stages.Should().NotContain(stage => stage.Name == "plugin.attempt");
        using var events = await client.GetAsync("/api/admin/privacy/events");
        (await events.Content.ReadAsStringAsync()).Should().NotContain("person@example.com");
        foreach (var path in new[] { "/api/admin/traces/requests", "/api/admin/privacy/events" })
        {
            var filtered = await client.GetFromJsonAsync<PagedResult<ObservedRequest>>(path + "?pluginKey=demo&pageSize=20");
            filtered!.Total.Should().Be(4, "只有实际进入 demo 插件的请求匹配筛选，护栏阻断请求不匹配");
            filtered.Items.Should().HaveCount(4);
            filtered.Items.Should().OnlyContain(row => row.StatusCode == 200);
            var empty = await client.GetFromJsonAsync<PagedResult<ObservedRequest>>(path + "?pluginKey=" + Uri.EscapeDataString("demo' OR 1=1 --"));
            empty!.Total.Should().Be(0, "插件筛选必须绑定参数，不能改变 SQL 条件");
            empty.Items.Should().BeEmpty();
        }
        var topology = await client.GetFromJsonAsync<TraceTopology>("/api/admin/traces/topology");
        topology!.Links.Should().HaveCount(4);
        topology.Links.Should().OnlyContain(link => link.PluginKey == "demo" && link.Attempts == 1);
        (await client.GetAsync("/api/admin/traces/requests?fromUtc=not-a-date")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/admin/traces/requests?pageSize=100000")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var test = await client.PostAsJsonAsync("/api/admin/privacy/test", new { text = "person@example.com" });
        test.EnsureSuccessStatusCode();
        host.Received.Should().HaveCount(4);
        var reloaded = new ConfigurationBuilder().AddJsonFile(host.ConfigPath).Build();
        reloaded.GetSection("Privacy").Get<PrivacyOptions>()!.DefaultAction.Should().Be("block");
    }

    [TestMethod]
    public async Task MappedRequestsKeepBothPluginStagesAndIndependentRequestIds()
    {
        await using var host = await Host.Create();
        var platforms = host.App.Services.GetRequiredService<IPlatformRegistry>();
        var first = new Mock<IPlatformTerminal>();
        first.Setup(value => value.InvokeAsync(It.IsAny<PluginAttemptContext>())).ReturnsAsync(new PluginInvocationResult(
            new AdapterResponse { StatusCode = 503, Error = "retry next mapping target" },
            new PluginAttemptDecision { FailureKind = PluginFailureKind.Upstream, Retry = PluginRetryAction.NextAttempt }.ToResult(503)));
        platforms.Register(new("first", "first", "first", first.Object));
        var store = host.App.Services.GetRequiredService<ModelMappingStore>();
        store.Save(new("", "alias", true, [
            new ModelMappingTarget("first", "first", "model", true, 1, 0, 0),
            new ModelMappingTarget("demo", "demo", "model", true, 1, 0, 0)
        ]));
        using var client = host.App.GetTestClient();
        await host.Login(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", host.ApiKey);
        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "alias", messages = new[] { new { role = "user", content = "hello" } } });
        response.EnsureSuccessStatusCode();
        var rows = await client.GetFromJsonAsync<PagedResult<ObservedRequest>>("/api/admin/traces/requests");
        var row = rows!.Items.Single();
        row.Trace!.Stages.Where(stage => stage.Name == "plugin.attempt").Select(stage => stage.PluginKey).Should().Equal("first", "demo");
        var filtered = await client.GetFromJsonAsync<PagedResult<ObservedRequest>>("/api/admin/traces/requests?pluginKey=first");
        filtered!.Items.Should().ContainSingle().Which.Id.Should().Be(row.Id, "最终平台为 demo 也要保留失败的 first 尝试");
        var topology = await client.GetFromJsonAsync<TraceTopology>("/api/admin/traces/topology?pluginKey=first");
        topology!.Clients.Single().Requests.Should().Be(1);
        topology.Links.Single().Attempts.Should().Be(1);
        var detail = await client.GetFromJsonAsync<ObservedRequest>("/api/admin/traces/requests/" + row.Id);
        detail!.Id.Should().Be(row.Id);
    }

    [TestMethod]
    public async Task WhitelistedPluginSkipsBlockButKeepsRedactionAndSourceMarker()
    {
        await using var host = await Host.Create(values =>
        {
            values["Privacy:Enabled"] = "true";
            values["Privacy:DryRun"] = "false";
            values["Privacy:DefaultAction"] = "block";
            values["Privacy:CategoryActions:email"] = "redact";
            values["Privacy:WhitelistPlugins:0"] = "Purge";
        });
        var guard = host.App.Services.GetRequiredService<PrivacyRequestGuard>();
        guard.Settings.Enabled.Should().BeTrue();
        guard.Settings.DefaultAction.Should().Be("block");
        guard.Settings.WhitelistPlugins.Should().ContainSingle().Which.Should().Be("Purge");
        var terminal = new Mock<IPlatformTerminal>();
        string? sourceMarker = null;
        string? content = null;
        var invocations = 0;
        terminal.Setup(value => value.InvokeAsync(It.IsAny<PluginAttemptContext>())).Returns((PluginAttemptContext context) =>
        {
            invocations++;
            sourceMarker = context.Request.RequestHeaders.GetValueOrDefault("x-router2api-source-plugin");
            content = context.Request.Messages.Count > 0 ? context.Request.Messages[0].Content : null;
            return Task.FromResult(new PluginInvocationResult(new AdapterResponse
            {
                StatusCode = 200,
                Completion = new(context.Request.Model, content)
            }, new PluginAttemptResult(PluginAttemptOutcome.Healthy)));
        });
        host.App.Services.GetRequiredService<IPlatformRegistry>()
            .Register(new("Purge", "power.purge", "Purge", terminal.Object));
        using var client = host.App.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", host.ApiKey);

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "Purge/alias",
            messages = new[] { new { role = "user", content = "person@example.com" } }
        });
        response.EnsureSuccessStatusCode();
        sourceMarker.Should().Be("Purge");
        content.Should().NotBeNull().And.Contain("[R2A_").And.NotContain("person@example.com");
        (await response.Content.ReadAsStringAsync()).Should().Contain("person@example.com", "响应仍按还原映射恢复原文");

        using var skippedBlock = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "Purge/alias",
            messages = new[] { new { role = "user", content = "13800138000" } }
        });
        skippedBlock.EnsureSuccessStatusCode();
        content.Should().Contain("13800138000", "白名单跳过 block，不再拦截手机号");
        invocations.Should().Be(2);

        using var blocked = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "demo/model",
            messages = new[] { new { role = "user", content = "13800138000" } }
        });
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden, "未命中白名单的平台仍执行 block");
        invocations.Should().Be(2);
    }

    private sealed class Host(WebApplication app, string apiKey, string password, string configPath) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public string ApiKey { get; } = apiKey;
        public string ConfigPath { get; } = configPath;
        public string Csrf { get; private set; } = "";
        public List<string> Received { get; } = [];

        public async Task Login(HttpClient client)
        {
            using var response = await client.PostAsJsonAsync("/api/admin/login", new { username = "test-admin", password });
            response.EnsureSuccessStatusCode();
            var cookies = response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]).ToArray();
            Csrf = cookies.Single(value => value.StartsWith("router_admin_csrf=", StringComparison.Ordinal)).Split('=')[1];
            client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
        }

        public static async Task<Host> Create(Action<Dictionary<string, string?>>? configure = null)
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "test-data", "observations-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "Config.json");
            var password = Guid.NewGuid().ToString("N");
            var apiKey = Guid.NewGuid().ToString("N");
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            builder.Configuration.Sources.Clear();
            var settings = new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(directory, "test.db"),
                ["Redis:ConnectionString"] = "",
                ["Auth:Admin:Users:0:Username"] = "test-admin",
                ["Auth:Admin:Users:0:Password"] = password,
                ["Auth:ApiKey:Key"] = apiKey
            };
            configure?.Invoke(settings);
            builder.Configuration.AddInMemoryCollection(settings).AddJsonFile(path, optional: true, reloadOnChange: false);
            builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
            builder.Services.Configure<AppearanceOptions>(builder.Configuration.GetSection("Appearance"));
            builder.Services.Configure<LogRetentionOptions>(builder.Configuration.GetSection("Logging"));
            builder.Services.Configure<PrivacyOptions>(builder.Configuration.GetSection("Privacy"));
            builder.Services.AddRouterInfrastructure(builder.Configuration);
            builder.Services.AddLogMigration();
            builder.Services.AddSingleton<PasswordHasher>();
            builder.Services.AddSingleton<AdminAuthService>();
            builder.Services.AddSingleton<ApiKeyService>();
            builder.Services.AddSingleton<PrivacyRequestGuard>();
            builder.Services.AddSingleton<IConfigFileService>(services => new ConfigFileService(builder.Configuration,
                services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AuthOptions>>(),
                services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<DatabaseOptions>>(),
                services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RedisOptions>>(),
                services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<LogRetentionOptions>>(),
                services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<PluginLogOptions>>(),
                services.GetRequiredService<ApiKeyService>(), services.GetRequiredService<ILogger<ConfigFileService>>(),
                services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AppearanceOptions>>(),
                services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<PrivacyOptions>>()) { FilePath = path });
            builder.Services.AddSingleton<RouterPipeline>();
            builder.Services.AddSingleton(Mock.Of<IPluginCatalog>());
            builder.Services.AddSingleton(Mock.Of<IProxySubscriptionRefreshQueue>());
            builder.Services.AddSingleton(Mock.Of<ISharedKeyValueStore>());
            builder.Services.AddSingleton(Mock.Of<IModelMetadataCatalog>());
            var accounts = new Mock<IAccountService>();
            accounts.Setup(value => value.QueryAsync(It.IsAny<AccountQuery>(), It.IsAny<CancellationToken>()))
                .Returns((AccountQuery query, CancellationToken _) => Task.FromResult(new PagedResult<Account>(
                    [new() { Id = "account", Platform = query.Platform!, PluginKey = query.PluginKey! }], 1, 100, 1)));
            builder.Services.AddSingleton(accounts.Object);
            builder.Services.AddCors(options => options.AddPolicy("v1", policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
            var app = builder.Build();
            var host = new Host(app, apiKey, password, path);
            app.Services.GetRequiredService<IConfigFileService>().EnsureCreated();
            app.Services.GetRequiredService<DatabaseInitializer>().Initialize();
            var terminal = new Mock<IPlatformTerminal>();
            terminal.Setup(value => value.InvokeAsync(It.IsAny<PluginAttemptContext>())).Returns((PluginAttemptContext context) =>
            {
                host.Received.Add(context.Request.OriginalBody?.GetRawText() ?? JsonSerializer.Serialize(context.Request.Messages));
                return Task.FromResult(new PluginInvocationResult(new AdapterResponse { StatusCode = 200,
                    Completion = new(context.Request.Model, context.Request.Messages.Count > 0 ? context.Request.Messages[0].Content : "hello", Usage: new(1, 2, 3)) },
                    new PluginAttemptResult(PluginAttemptOutcome.Healthy)));
            });
            app.Services.GetRequiredService<IPlatformRegistry>().Register(new("demo", "demo", "demo", terminal.Object));
            app.UseCors("v1");
            app.UseMiddleware<TraceIdMiddleware>();
            app.UseMiddleware<DownstreamApiKeyMiddleware>();
            app.UseMiddleware<AdminSessionMiddleware>();
            ApiEndpoints.Map(app);
            await app.StartAsync();
            return host;
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}
