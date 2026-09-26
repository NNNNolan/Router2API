using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;
using Router.Host.Plugins;
using Router.Host.Plugins.JavaScript;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class JsPluginTests
{
    private const string Completion = """
        return {response:{kind:"completion",statusCode:200,completion:{model:ctx.request.model,content:"ok"}},
                attempt:{outcome:"Healthy",statusCode:200}};
        """;

    [TestMethod]
    public async Task ConcurrentInvocationsUseIsolatedEngines()
    {
        using var terminal = CreateTerminal("""
            let count=0;
            export async function invoke(ctx) {
              count++; await ctx.delay(5);
              return {response:{kind:"completion",statusCode:200,completion:{model:ctx.request.model,content:String(count)}},
                      attempt:{outcome:"Healthy"}};
            }
            """);
        await terminal.ValidateAsync(CancellationToken.None);
        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(index => terminal.InvokeAsync(Attempt("model-" + index))));

        results.Should().AllSatisfy(result => result.Response.Completion!.Content.Should().HaveLength(1).And.Be("1"));
        results.Select(result => result.Response.Completion!.Model).Should()
            .Equal(Enumerable.Range(0, 12).Select(index => "model-" + index));
    }

    [TestMethod]
    public async Task TaskUsesPoolFactoryAndStateSurvivesTheNextEngine()
    {
        var pool = new Mock<IProxyPoolHttpClientFactory>();
        pool.Setup(factory => factory.SendAsync(
                It.IsAny<Func<HttpRequestMessage>>(), It.IsAny<ProxyPoolHttpClientOptions>(),
                It.IsAny<ProxyPoolRetryOptions>(), It.IsAny<HttpCompletionOption>(), It.IsAny<CancellationToken>()))
            .Returns((Func<HttpRequestMessage> create, ProxyPoolHttpClientOptions? options, ProxyPoolRetryOptions? retry,
                HttpCompletionOption completion, CancellationToken token) =>
            {
                using var request = create();
                request.RequestUri!.AbsoluteUri.Should().Be("https://example.com/status");
                options!.AllowDirectFallback.Should().BeFalse();
                options.AllowAutoRedirect.Should().BeFalse();
                retry!.MaxRetries.Should().Be(2);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"healthy":true}""", Encoding.UTF8, "application/json")
                });
            });
        var manifest = Manifest() with
        {
            Tasks = [new JsTaskManifest { Name = "pool-check", Handler = "check", Cron = "0 0 * * * *" }],
            Endpoints = [new JsEndpointManifest { Path = "status", Handler = "status" }]
        };
        using var terminal = CreateTerminal($$$"""
            export async function invoke(ctx) { {{{Completion}}} }
            export async function check(ctx) {
              const r=await ctx.http.request({url:"https://example.com/status",route:"pool",retry:{maxRetries:2}});
              await ctx.state.set("last", r.body, {ttlSeconds:60});
            }
            export async function status(ctx) { return ctx.json(200, await ctx.state.get("last")); }
            """, manifest, Host(pool));
        await terminal.ValidateAsync(CancellationToken.None);

        await terminal.ScheduledTasks.Single().ExecuteAsync(new PluginScheduledTaskContext("js-test", "js-test", CancellationToken.None));
        var status = await terminal.InvokeEndpointAsync("status", new PluginHttpContext());

        status.StatusCode.Should().Be(200);
        JsonSerializer.SerializeToNode(status.Body)!["healthy"]!.GetValue<bool>().Should().BeTrue();
        pool.Verify(factory => factory.SendAsync(It.IsAny<Func<HttpRequestMessage>>(), It.IsAny<ProxyPoolHttpClientOptions>(),
            It.IsAny<ProxyPoolRetryOptions>(), It.IsAny<HttpCompletionOption>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UnlistedOriginCannotReachTheFactory()
    {
        var pool = new Mock<IProxyPoolHttpClientFactory>();
        using var terminal = CreateTerminal($$$"""
            export async function invoke(ctx) {
              await ctx.http.request({url:"https://untrusted.invalid/secret",route:"pool"});
              {{{Completion}}}
            }
            """, host: Host(pool));

        var result = await terminal.InvokeAsync(Attempt());

        result.Response.StatusCode.Should().Be(502);
        result.Attempt.Outcome.Should().Be(PluginAttemptOutcome.NoPenalty);
        pool.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task TransportRetryCannotMultiplyTheModelAttemptBudget()
    {
        var pool = new Mock<IProxyPoolHttpClientFactory>();
        using var terminal = CreateTerminal($$$"""
            export async function invoke(ctx) {
              await ctx.http.request({url:"https://example.com/status",route:"pool",retry:{maxRetries:2}});
              {{{Completion}}}
            }
            """, Manifest() with { Policy = new JsPolicyManifest { MaxAttempts = 2 } }, Host(pool));
        var result = await terminal.InvokeAsync(Attempt());
        result.Response.StatusCode.Should().Be(502);
        result.Response.Error.Should().Contain("one retry owner");
        pool.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ExplicitDecisionsAreValidatedAndUnknownFieldsAreRejected()
    {
        using var terminal = CreateTerminal("""
            export function invoke(ctx) {
              return {response:{kind:"error",statusCode:429,message:"limited"},
                attempt:{statusCode:429,decision:{failureKind:"Upstream",retry:"NextAttempt",reasonCode:"quota"}}};
            }
            """);
        var result = await terminal.InvokeAsync(Attempt());
        result.Attempt.Decision!.Retry.Should().Be(PluginRetryAction.NextAttempt);
        using var invalid = CreateTerminal("""
            export function invoke(ctx) {
              return {response:{kind:"error",statusCode:429,message:"limited"},attempt:{decision:{retryTypo:"NextAttempt"}}};
            }
            """);
        (await invalid.InvokeAsync(Attempt())).Attempt.Decision!.FailureKind.Should().Be(PluginFailureKind.Plugin);
    }

    [TestMethod]
    public async Task CallerCancellationReachesPendingHostHttp()
    {
        var cancelled = false;
        var pool = new Mock<IProxyPoolHttpClientFactory>();
        pool.Setup(factory => factory.SendAsync(
                It.IsAny<Func<HttpRequestMessage>>(), It.IsAny<ProxyPoolHttpClientOptions>(),
                It.IsAny<ProxyPoolRetryOptions>(), It.IsAny<HttpCompletionOption>(), It.IsAny<CancellationToken>()))
            .Returns(async (Func<HttpRequestMessage> create, ProxyPoolHttpClientOptions? options, ProxyPoolRetryOptions? retry,
                HttpCompletionOption completion, CancellationToken token) =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), token); }
                catch (OperationCanceledException) { cancelled = true; throw; }
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            });
        using var terminal = CreateTerminal($$$"""
            export async function invoke(ctx) {
              await ctx.http.request({url:"https://example.com/status",route:"pool"});
              {{{Completion}}}
            }
            """, host: Host(pool));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var act = () => terminal.InvokeAsync(Attempt(token: cancellation.Token));

        await act.Should().ThrowAsync<OperationCanceledException>();
        cancelled.Should().BeTrue();
    }

    [TestMethod]
    public async Task ScriptErrorsCannotExposeCurrentCredential()
    {
        using var terminal = CreateTerminal("""
            export async function invoke(ctx) {
              const credential=await ctx.accounts.currentCredential();
              throw new Error("bad token: " + credential.apiKey);
            }
            """, Manifest() with { Permissions = Manifest().Permissions with { ReadCurrentCredential = true } });

        var result = await terminal.InvokeAsync(Attempt());

        result.Response.StatusCode.Should().Be(502);
        result.Response.Error.Should().Contain("[redacted]").And.NotContain("test-secret-key");
        result.Attempt.IsTransportFailure.Should().BeFalse();
    }

    [TestMethod]
    public async Task DetachedHostCallsAreCancelledBeforeTheEngineIsReleased()
    {
        var cancelled = false;
        var pool = new Mock<IProxyPoolHttpClientFactory>();
        pool.Setup(factory => factory.SendAsync(
                It.IsAny<Func<HttpRequestMessage>>(), It.IsAny<ProxyPoolHttpClientOptions>(),
                It.IsAny<ProxyPoolRetryOptions>(), It.IsAny<HttpCompletionOption>(), It.IsAny<CancellationToken>()))
            .Returns(async (Func<HttpRequestMessage> create, ProxyPoolHttpClientOptions? options, ProxyPoolRetryOptions? retry,
                HttpCompletionOption completion, CancellationToken token) =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), token); }
                catch (OperationCanceledException) { cancelled = true; throw; }
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            });
        using var terminal = CreateTerminal($$$"""
            export async function invoke(ctx) {
              ctx.http.request({url:"https://example.com/status",route:"pool"});
              {{{Completion}}}
            }
            """, host: Host(pool));

        var result = await terminal.InvokeAsync(Attempt());

        result.Response.StatusCode.Should().Be(502);
        cancelled.Should().BeTrue();
    }

    [TestMethod]
    public async Task MissingDeclaredExportIsRejectedAtLoad()
    {
        using var terminal = CreateTerminal("export const invoke = 42;");
        var validate = () => terminal.ValidateAsync(CancellationToken.None);
        await validate.Should().ThrowAsync<Exception>().WithMessage("*Missing JS export*");
    }

    [TestMethod]
    public async Task StatementBudgetStopsScriptLoopsWithoutPenalizingResources()
    {
        using var terminal = CreateTerminal("""
            export async function invoke(ctx) { let i=0; while(true) { i++; } }
            """);
        var result = await terminal.InvokeAsync(Attempt());
        result.Response.StatusCode.Should().Be(502);
        result.Attempt.Outcome.Should().Be(PluginAttemptOutcome.NoPenalty);
    }

    [TestMethod]
    public async Task BufferedCompletionsCanBeExposedAsAStandardStream()
    {
        var manifest = Manifest() with { Hooks = new JsHooksManifest { Invoke = "invoke", GetModels = "models" } };
        using var terminal = CreateTerminal($$$"""
            export async function invoke(ctx) { {{{Completion}}} }
            export function models(ctx) { return [{id:"echo",displayName:"Echo",supportsStreaming:true}]; }
            """, manifest);
        var models = await terminal.GetModelsAsync(new ModelQueryContext(), CancellationToken.None);
        models.Single().SupportsStreaming.Should().BeTrue();
        var context = Attempt();
        context.Request.Stream = true;
        var result = await terminal.InvokeAsync(context);
        result.Response.StatusCode.Should().Be(200);
        result.Response.IsStreaming.Should().BeTrue();
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in result.Response.Stream!) chunks.Add(chunk);
        chunks.Single(chunk => chunk.Delta is not null).Delta.Should().Be("ok");
    }

    [TestMethod]
    public async Task PackageRejectsPathTraversalAndUnsupportedCapabilities()
    {
        await using var package = await TestPackage.CreateAsync();
        var bad = Manifest() with { Entry = "../outside.mjs" };
        await File.WriteAllTextAsync(Path.Combine(package.Root, "plugin.json"), JsonSerializer.Serialize(bad, JsPluginCodec.JsonOptions));
        var escape = () => JsPluginPackage.ReadAsync(package.Root, "js-test", CancellationToken.None);
        await escape.Should().ThrowAsync<InvalidOperationException>();

        var json = JsonSerializer.SerializeToNode(Manifest(), JsPluginCodec.JsonOptions)!.AsObject();
        json["unsupportedHostCapability"] = new JsonObject();
        await File.WriteAllTextAsync(Path.Combine(package.Root, "plugin.json"), json.ToJsonString());
        var unsupported = () => JsPluginPackage.ReadAsync(package.Root, "js-test", CancellationToken.None);
        await unsupported.Should().ThrowAsync<JsonException>();
    }

    [TestMethod]
    public async Task CatalogLoadsJsWithoutAnAssemblyAndKeepsOldVersionOnFailure()
    {
        await using var package = await TestPackage.CreateAsync();
        var host = Host(new Mock<IProxyPoolHttpClientFactory>());
        var hostFactory = new Mock<IPluginHostFactory>();
        hostFactory.Setup(factory => factory.Create("js-test")).Returns(host);
        hostFactory.Setup(factory => factory.Create("js-test", It.IsAny<IReadOnlyList<string>>())).Returns(host);
        var platforms = new PlatformRegistry();
        await using var catalog = new PluginCatalog(platforms, Mock.Of<IModelCatalog>(), hostFactory.Object,
            new PluginPolicyRegistry(), null!, null!, Mock.Of<IPluginLogSink>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PluginCatalog>.Instance,
            Microsoft.Extensions.Options.Options.Create(new PluginExecutionOptions()));
        var load = typeof(PluginCatalog).GetMethod("LoadDirectoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)load.Invoke(catalog, ["js-test", package.Root, CancellationToken.None])!;
        var first = catalog.Get("js-test")!;
        first.State.Should().Be("Active");
        first.Runtime.Should().Be("jint");
        catalog.GetMainPage("js-test")!.Html.Should().Contain("JS test page");
        var result = await platforms.Get("js-test")!.Terminal.InvokeAsync(Attempt());
        result.Response.Completion!.Content.Should().Be("ok");

        await File.WriteAllTextAsync(Path.Combine(package.Root, "server/plugin.mjs"), "broken Javascript {");
        await (Task)load.Invoke(catalog, ["js-test", package.Root, CancellationToken.None])!;
        catalog.Get("js-test")!.DirectoryPath.Should().Be(first.DirectoryPath);
        catalog.Get("js-test")!.State.Should().Be("Active");
    }

    [TestMethod]
    public async Task ShippedExamplePassesRealJintValidationWithoutNetwork()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Router2API.slnx")))
            directory = directory.Parent;
        Assert.IsNotNull(directory);
        var package = await JsPluginPackage.ReadAsync(
            Path.Combine(directory.FullName, "tests/Router.Tests/Fixtures/js-proxy-demo"), "js-proxy-demo", CancellationToken.None);
        using var terminal = new JsPlatformTerminal(package, PluginTestHost.Create("js-proxy-demo"));
        await terminal.ValidateAsync(CancellationToken.None);
        var models = await terminal.GetModelsAsync(new ModelQueryContext(), CancellationToken.None);
        models.Single().Id.Should().Be("echo");
        terminal.ScheduledTasks.Single().Name.Should().Be("js-proxy-demo-check");
    }

    private static JsPluginManifest Manifest() => new()
    {
        SchemaVersion = 1, Id = "js-test", Name = "JS test", Version = "0.1.0", Runtime = "jint",
        HostApi = "1-preview", Format = "esm-bundle", Entry = "server/plugin.mjs",
        Platform = new JsPlatformManifest { Name = "js-test", DisplayName = "JS test" },
        Hooks = new JsHooksManifest { Invoke = "invoke" },
        Permissions = new JsPermissions
        {
            Http = new JsHttpPermissions { Origins = ["https://example.com"], Routes = ["pool"] },
            State = ["read", "write"]
        }
    };

    private static IPluginHost Host(Mock<IProxyPoolHttpClientFactory> pool)
        => PluginTestHost.Create("js-test", pool.Object);

    private static JsPlatformTerminal CreateTerminal(string source, JsPluginManifest? manifest = null, IPluginHost? host = null)
    {
        manifest ??= Manifest();
        manifest.Validate("js-test");
        return new JsPlatformTerminal(new JsPluginPackage(manifest, source, null, "{}"),
            host ?? Host(new Mock<IProxyPoolHttpClientFactory>()));
    }

    private static PluginAttemptContext Attempt(string model = "echo", CancellationToken token = default) => new()
    {
        PluginKey = "js-test", PlatformName = "js-test", TraceId = "test-js-trace",
        Request = new AdapterRequest { Model = model, Messages = [new AdapterMessage("user", "hello")] },
        Account = new Account { Id = "test-account", PluginKey = "js-test", Platform = "js-test", Credential = new ApiKeyCredential("test-secret-key") },
        HttpClient = Mock.Of<IPluginHttpClient>(), CancellationToken = token
    };

    private sealed class TestPackage : IAsyncDisposable
    {
        private readonly string _ownedRoot = Path.Combine(Path.GetTempPath(), "Router2API-JsTests", Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(_ownedRoot, "js-test");
        public static async Task<TestPackage> CreateAsync()
        {
            var package = new TestPackage();
            Directory.CreateDirectory(Path.Combine(package.Root, "server"));
            Directory.CreateDirectory(Path.Combine(package.Root, "ui"));
            var manifest = Manifest() with { Page = new JsPageManifest { Title = "JS test", Entry = "ui/index.html" } };
            await File.WriteAllTextAsync(Path.Combine(package.Root, "plugin.json"), JsonSerializer.Serialize(manifest, JsPluginCodec.JsonOptions));
            await File.WriteAllTextAsync(Path.Combine(package.Root, "server/plugin.mjs"), $"export async function invoke(ctx) {{ {Completion} }}");
            await File.WriteAllTextAsync(Path.Combine(package.Root, "ui/index.html"), "<p>JS test page</p>");
            return package;
        }
        public ValueTask DisposeAsync()
        {
            var boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Router2API-JsTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_ownedRoot).StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid test cleanup path.");
            if (Directory.Exists(_ownedRoot)) Directory.Delete(_ownedRoot, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
