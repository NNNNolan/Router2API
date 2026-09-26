using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;
using Router.Host.Plugins;
using Router.Host.Plugins.JavaScript;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class JsCapabilityParityTests
{
    [TestMethod]
    public async Task AccountCrudCredentialCasAndStatusChangesUseRealScopedStorage()
    {
        await using var fixture = new Fixture();
        var terminal = fixture.Create("""
            export async function endpoint(ctx) {
              const a=await ctx.accounts.save({id:"a",label:"original",credential:{kind:"OAuth",accessToken:"old-secret"}});
              const original=await ctx.accounts.readCredentials(a.id);
              await ctx.accounts.disable(a.id,"disabled by test",401);
              const changed=await ctx.accounts.compareExchangeCredential(a.id,original.version,{kind:"OAuth",accessToken:"new-secret"});
              const conflict=await ctx.accounts.compareExchangeCredential(a.id,original.version,{kind:"OAuth",accessToken:"stale-secret"});
              const renamed=await ctx.accounts.save({id:a.id,label:"renamed"});
              const read=await ctx.accounts.readCredentials(a.id);
              await ctx.log.write({message:"new-secret",details:{token:read.credential.accessToken}});
              const list=await ctx.accounts.list();
              await ctx.accounts.delete(a.id);
              return ctx.json(200,{a,changed,conflict,renamed,count:list.length,deleted:await ctx.accounts.get(a.id)});
            }
            """);
        var result = await fixture.Endpoint(terminal);
        result["changed"]!["status"]!["state"]!.GetValue<string>().Should().Be("Disabled");
        result["renamed"]!["label"]!.GetValue<string>().Should().Be("renamed");
        result["renamed"]!["status"]!["state"]!.GetValue<string>().Should().Be("Disabled");
        result["conflict"].Should().BeNull();
        result["deleted"].Should().BeNull();
        result.ToJsonString().Should().NotContain("old-secret").And.NotContain("new-secret");
        fixture.Logs.Verify(sink => sink.WriteAsync(It.Is<PluginLog>(log => log.Message == "[redacted]"
            && log.DetailsJson!.Contains("[redacted]", StringComparison.Ordinal)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ForeignAccountsAndMissingPermissionsCannotBeBypassed()
    {
        await using var fixture = new Fixture();
        await fixture.Accounts.SaveAsync(new Account { Id = "foreign", PluginKey = "other", Platform = "other", Credential = new ApiKeyCredential("hidden") });
        var terminal = fixture.Create("""
            export async function endpoint(ctx) {
              const invisible=await ctx.accounts.get("foreign");
              try { await ctx.accounts.save({id:"foreign",credential:{kind:"ApiKey",apiKey:"overwrite"}}); }
              catch(e) { return ctx.json(200,{invisible,rejected:true}); }
              throw new Error("cross-plugin write unexpectedly succeeded");
            }
            """);
        var result = await fixture.Endpoint(terminal);
        result["invisible"].Should().BeNull();
        result["rejected"]!.GetValue<bool>().Should().BeTrue();
        ((ApiKeyCredential)(await fixture.Accounts.GetAsync("other", "foreign"))!.Credential).ApiKey.Should().Be("hidden");

        var restricted = fixture.Create("""
            export async function endpoint(ctx) { await ctx.accounts.list(); return ctx.json(200,{}); }
            """, Fixture.Manifest() with { Permissions = new JsPermissions() });
        var denied = await restricted.InvokeEndpointAsync("endpoint", new PluginHttpContext());
        denied.StatusCode.Should().Be(502);
    }

    [TestMethod]
    public async Task CredentialRefreshUsesAFreshCallbackEngineAndDoesNotClobberCooldown()
    {
        await using var fixture = new Fixture();
        await fixture.Accounts.SaveAsync(new Account { Id = "a", PluginKey = "api", Platform = "api", Credential = new OAuthCredential("old-secret") });
        var until = DateTimeOffset.UtcNow.AddHours(2);
        await fixture.Accounts.SetCooldownAsync("api", "a", until, "quota");
        var terminal = fixture.Create("""
            export async function refresh(ctx, account) {
              await ctx.delay(10);
              return {...account.credential,accessToken:account.credential.accessToken+"-next"};
            }
            export async function endpoint(ctx) {
              const account=await ctx.accounts.refresh("a");
              return ctx.json(200,{version:account.credentialVersion,cooldown:account.status.cooldownUntil});
            }
            """, Fixture.Manifest() with { Hooks = new JsHooksManifest { Invoke = "invoke", RefreshCredential = "refresh" } });
        var results = await Task.WhenAll(fixture.Endpoint(terminal), fixture.Endpoint(terminal));
        results.Select(result => result["version"]!.GetValue<string>()).Should().BeEquivalentTo("2", "3");
        var saved = await fixture.Accounts.GetAsync("api", "a");
        saved!.Status.CooldownUntil.Should().Be(until);
        ((OAuthCredential)saved.Credential).AccessToken.Should().Be("old-secret-next-next");
    }

    [TestMethod]
    public async Task LocalAndSharedAtomicStateAndUtilitiesAreAvailableToRealJint()
    {
        await using var fixture = new Fixture();
        var terminal = fixture.Create("""
            export async function endpoint(ctx) {
              const once=await ctx.state.shared.putIfAbsent("cache",{a:1},{ttlSeconds:90});
              const twice=await ctx.state.shared.putIfAbsent("cache",{a:2},{ttlSeconds:90});
              const swapped=await ctx.state.shared.compareExchange("cache",{a:1},{a:3},{ttlSeconds:90});
              const count=await ctx.state.increment("counter","9007199254740993");
              await ctx.state.setString("raw","not-json");
              return ctx.json(200,{once,twice,swapped,count,raw:await ctx.state.getString("raw"),
                shared:await ctx.state.shared.get("cache"),expires:await ctx.state.shared.expiry("cache"),
                hash:ctx.crypto.sha256("abc"),decimal:ctx.decimal.add("0.1","0.2"),
                text:ctx.encoding.fromBase64(ctx.encoding.toBase64("中文")),
                url:ctx.url.resolve("https://example.com/base/","../next"),uuid:ctx.crypto.randomUUID()});
            }
            """);
        var value = await fixture.Endpoint(terminal);
        value["once"]!.GetValue<bool>().Should().BeTrue();
        value["twice"]!.GetValue<bool>().Should().BeFalse();
        value["swapped"]!.GetValue<bool>().Should().BeTrue();
        value["count"]!.GetValue<string>().Should().Be("9007199254740993");
        value["raw"]!.GetValue<string>().Should().Be("not-json");
        value["shared"]!["a"]!.GetValue<int>().Should().Be(3);
        value["expires"].Should().NotBeNull();
        value["hash"]!.GetValue<string>().Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        value["decimal"]!.GetValue<string>().Should().Be("0.3");
        value["text"]!.GetValue<string>().Should().Be("中文");
        Guid.TryParse(value["uuid"]!.GetValue<string>(), out _).Should().BeTrue();
        value["url"]!.GetValue<string>().Should().Be("https://example.com/next");
    }

    [TestMethod]
    public async Task JsBatchSelectionRespectsNativeFiltersAndSupportsExpiryThenWeight()
    {
        await using var fixture = new Fixture();
        foreach (var id in new[] { "later", "sooner", "disabled", "cooling" })
            await fixture.Accounts.SaveAsync(new Account { Id = id, PluginKey = "api", Platform = "api", Credential = new ApiKeyCredential("key") });
        await fixture.Accounts.DisableAsync("api", "disabled", "test");
        await fixture.Accounts.SetCooldownAsync("api", "cooling", DateTimeOffset.UtcNow.AddHours(1), "test");
        await fixture.Host.Services.State.Local.SetStringAsync("deadlines", """{"later":"2099-01-02T00:00:00Z","sooner":"2099-01-01T00:00:00Z"}""", TimeSpan.FromMinutes(1));
        var manifest = Fixture.Manifest() with { Hooks = new JsHooksManifest { Invoke = "invoke", SelectAccounts = "select" } };
        var terminal = fixture.Create("""
            export async function select(ctx,input) {
              if(input.candidates.some(a=>a.id==="disabled"||a.id==="cooling"))throw new Error("native hard filters were bypassed");
              const deadlines=await ctx.state.get("deadlines");
              return input.candidates.map(a=>({accountId:a.id,weight:a.id==="later"?100:0,preferredExpiry:deadlines[a.id]}));
            }
            export function invoke(ctx) { return ctx.reply.completion({model:ctx.request.model,content:ctx.account.id}); }
            """, manifest);
        var result = await fixture.Execute(terminal, new AdapterRequest { Model = "model" });
        result.Response.Completion!.Content.Should().Be("sooner");

        var bad = fixture.Create("""
            export function select(ctx,input) { return [{accountId:"foreign",weight:999}]; }
            """, manifest);
        (await fixture.Execute(bad, new AdapterRequest { Model = "model" })).Response.IsSuccess.Should().BeFalse();
    }

    [TestMethod]
    public async Task DetachedJobsExposeProgressDeduplicateAndCancelWithoutLeakingAnEngine()
    {
        await using var fixture = new Fixture();
        var manifest = Fixture.Manifest() with { Jobs = [new JsJobManifest { Name = "slow", Handler = "slow", TimeoutSeconds = 600 }] };
        var terminal = fixture.Create("""
            export async function slow(ctx,input) {
              await ctx.jobs.progress({accountId:input.id});
              await ctx.delay(300000);
              return {done:true};
            }
            export async function endpoint(ctx) {
              return ctx.json(202,await ctx.jobs.start("slow",{id:"a"},{key:"same"}));
            }
            """, manifest);
        var first = await fixture.Endpoint(terminal);
        var second = await fixture.Endpoint(terminal);
        var id = first["id"]!.GetValue<string>();
        second["id"]!.GetValue<string>().Should().Be(id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (fixture.Jobs.Get(id)!.Progress is null) await Task.Delay(10, timeout.Token);
        fixture.Jobs.Get(id)!.Progress!.Value.GetProperty("accountId").GetString().Should().Be("a");
        await fixture.Jobs.CancelAsync(id);
        (await fixture.Jobs.WaitAsync(id, timeout.Token))!.State.Should().Be(PluginJobState.Cancelled);
        fixture.Jobs.InFlight.Should().Be(0);
    }

    [TestMethod]
    public async Task OriginalJsonPatchPreservesUnknownFieldsAndLargeIntegersAndRawBytes()
    {
        await using var fixture = new Fixture();
        string? sent = null;
        fixture.Send = async (request, token) =>
        {
            sent = await request.Content!.ReadAsStringAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 255, 1, 128]) };
        };
        await fixture.Accounts.SaveAsync(new Account { Id = "a", PluginKey = "api", Platform = "api", Credential = new ApiKeyCredential("key") });
        var terminal = fixture.Create("""
            export async function invoke(ctx) {
              const source=await ctx.http.open({url:"https://example.com",route:"direct",method:"POST",
                originalJson:{source:ctx.request.originalBodyRef,remove:["overrides"],set:{model:ctx.request.model}}});
              return ctx.reply.raw(source);
            }
            """);
        using var body = JsonDocument.Parse("""{"model":"api/model","overrides":{},"huge":9223372036854775807,"nested":{"exact":9007199254740993},"newField":true}""");
        var result = await fixture.Execute(terminal, new AdapterRequest { Model = "model", OriginalBody = body.RootElement.Clone() });
        result.Response.RawContent.Should().Equal(0, 255, 1, 128);
        sent.Should().Contain("9223372036854775807").And.Contain("9007199254740993").And.NotContain("overrides");
        JsonNode.Parse(sent!)!["model"]!.GetValue<string>().Should().Be("model");
    }

    [TestMethod]
    public async Task FixedPoolClientUsesOneSelectionAndBinaryAndFormBodiesAreSupported()
    {
        await using var fixture = new Fixture();
        var count = 0;
        fixture.Send = async (request, token) =>
        {
            Interlocked.Increment(ref count);
            if (request.RequestUri!.AbsolutePath == "/binary")
                (await request.Content!.ReadAsByteArrayAsync(token)).Should().Equal(0, 255, 1);
            else
                (await request.Content!.ReadAsStringAsync(token)).Should().Contain("name=a+b");
            return Fixture.Json(new { ok = true });
        };
        var terminal = fixture.Create("""
            export async function endpoint(ctx) {
              const client=await ctx.http.createClient({route:"pool"});
              try {
                await client.request({url:"https://example.com/binary",method:"POST",bodyBase64:"AP8B"});
                await client.request({url:"https://example.com/form",method:"POST",form:{name:"a b"}});
              } finally { await client.close(); }
              return ctx.json(200,{ok:true});
            }
            """);
        (await fixture.Endpoint(terminal))["ok"]!.GetValue<bool>().Should().BeTrue();
        count.Should().Be(2);
        fixture.Pool.Verify(value => value.CreateClientAsync(It.IsAny<ProxyPoolHttpClientOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Pool.Verify(value => value.SendAsync(It.IsAny<Func<HttpRequestMessage>>(), It.IsAny<ProxyPoolHttpClientOptions>(),
            It.IsAny<ProxyPoolRetryOptions>(), It.IsAny<HttpCompletionOption>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task DynamicOriginsRequireAnAdminEndpointAndRedirectsRecheckAuthorization()
    {
        await using var fixture = new Fixture();
        var manifest = Fixture.Manifest() with
        {
            Permissions = Fixture.Manifest().Permissions with { Http = new JsHttpPermissions { Origins = ["https://example.com"], Routes = ["direct"], ManageOrigins = true } }
        };
        var terminal = fixture.Create("""
            export async function endpoint(ctx) {
              await ctx.http.approveOrigin("https://new.example");
              const result=await ctx.http.request({url:"https://new.example/status",route:"direct"});
              return ctx.json(200,result.body);
            }
            export async function invoke(ctx) {
              await ctx.http.approveOrigin("https://not-admin.example");
              return ctx.reply.completion({model:"m",content:"unexpected"});
            }
            """, manifest);
        fixture.Send = (_, _) => Task.FromResult(Fixture.Json(new { approved = true }));
        (await fixture.Endpoint(terminal))["approved"]!.GetValue<bool>().Should().BeTrue();
        (await fixture.Native.Http.GetApprovedOriginsAsync()).Should().Contain("https://new.example");
        await fixture.Accounts.SaveAsync(new Account { Id = "a", PluginKey = "api", Platform = "api", Credential = new ApiKeyCredential("key") });
        (await fixture.Execute(terminal, new AdapterRequest { Model = "m" })).Response.IsSuccess.Should().BeFalse();

        fixture.Send = (_, _) =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://not-approved.example/");
            return Task.FromResult(redirect);
        };
        var redirects = fixture.Create("""
            export async function endpoint(ctx) {
              await ctx.http.request({url:"https://example.com",route:"direct",followRedirects:true});
              return ctx.json(200,{unexpected:true});
            }
            """, manifest);
        (await redirects.InvokeEndpointAsync("endpoint", new PluginHttpContext())).StatusCode.Should().Be(502);
    }

    [TestMethod]
    public async Task NativeCredentialCasAndFieldPatchPreserveUnrelatedConcurrentState()
    {
        await using var fixture = new Fixture();
        var account = await fixture.Accounts.SaveAsync(new Account { Id = "a", PluginKey = "api", Platform = "api", Credential = new ApiKeyCredential("old") });
        var until = DateTimeOffset.UtcNow.AddHours(1);
        await fixture.Accounts.SetCooldownAsync("api", "a", until, "quota");
        account.Label = "renamed using stale snapshot";
        var patched = await fixture.Accounts.PatchAsync(account, ["label"]);
        patched.Status.CooldownUntil.Should().Be(until);
        var results = await Task.WhenAll(
            fixture.Accounts.CompareExchangeCredentialAsync("api", "a", account.CredentialVersion, new ApiKeyCredential("one")),
            fixture.Accounts.CompareExchangeCredentialAsync("api", "a", account.CredentialVersion, new ApiKeyCredential("two")));
        results.Count(value => value is not null).Should().Be(1);
        (await fixture.Accounts.GetAsync("api", "a"))!.Status.CooldownUntil.Should().Be(until);
    }

    [TestMethod]
    public async Task MultiPlatformLoaderBindsIndependentPoliciesPagesAndLongTasksAndDrainsJobs()
    {
        var boundary = Path.Combine(Path.GetTempPath(), "Router2API-JsParity");
        var root = Path.Combine(boundary, Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "multi");
        Directory.CreateDirectory(source);
        var manifest = new JsPluginManifest
        {
            SchemaVersion = 1, Id = "multi", Name = "Multi", Version = "1", Runtime = "jint", HostApi = "1", Format = "esm-bundle", Entry = "plugin.mjs",
            Platforms =
            [
                new JsPlatformManifest { Name = "one", Hooks = new JsHooksManifest { Invoke = "one", GetModels = "models", GetMainPage = "page" },
                    Policy = new JsPolicyManifest { MaxAttempts = 2 } },
                new JsPlatformManifest { Name = "two", Hooks = new JsHooksManifest { Invoke = "two" },
                    Policy = new JsPolicyManifest { MaxAttempts = 4 } }
            ],
            Endpoints = [new JsEndpointManifest { Path = "status", Platform = "two", Handler = "endpoint" }],
            Tasks = [new JsTaskManifest { Name = "long-task", Platform = "two", Handler = "job", Cron = "0 0 * * * *", TimeoutSeconds = 1800 }],
            Permissions = new JsPermissions { Jobs = ["start", "read", "cancel"], State = ["read", "write"] }
        };
        await File.WriteAllTextAsync(Path.Combine(source, "plugin.json"), JsonSerializer.Serialize(manifest, JsPluginCodec.JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(source, "plugin.mjs"), """
            export function one(ctx){return ctx.reply.completion({model:"m",content:"one"});}
            export function two(ctx){return ctx.reply.completion({model:"m",content:"two"});}
            export function models(){return [{id:"m",displayName:"Model"}];}
            export function page(ctx){return {title:"Dynamic "+ctx.platform,html:"<p>Own page</p>",version:"2"};}
            export function endpoint(ctx){return ctx.json(200,{platform:ctx.platform});}
            export async function job(ctx){await ctx.state.set("job-running",true);await ctx.delay(300000);}
            """);
        var host = PluginTestHost.Create("multi");
        await using var jobs = new PluginJobManager("multi", host.Services.Log);
        Mock.Get(host.Services).SetupGet(value => value.Jobs).Returns(jobs);
        var hosts = new Mock<IPluginHostFactory>();
        hosts.Setup(value => value.Create("multi", It.IsAny<IReadOnlyList<string>>())).Returns(host);
        try
        {
            var loader = new JintPackageLoader(hosts.Object);
            await using var loaded = await loader.LoadAsync("multi", source, Path.Combine(root, "staged"), CancellationToken.None);
            var executions = 0;
            Mock.Get(host.Services.Tasks).Setup(value => value.RunAsync("long-task", "two", It.IsAny<CancellationToken>()))
                .Returns(async (string _, string? _, CancellationToken token) =>
                {
                    Interlocked.Increment(ref executions);
                    await loaded.Platforms[1].Tasks.Single().ExecuteAsync(new PluginScheduledTaskContext("two", "multi", token));
                    return true;
                });
            loaded.Platforms.Select(platform => platform.Name).Should().Equal("one", "two");
            loaded.MainPage!.Title.Should().Be("Dynamic one");
            (await loaded.Platforms[0].Terminal.GetModelsAsync(new ModelQueryContext(), CancellationToken.None)).Single().Id.Should().Be("m");
            var status = await loaded.Endpoints.Single().InvokeAsync(new PluginHttpContext());
            JsonSerializer.SerializeToNode(status.Body)!["platform"]!.GetValue<string>().Should().Be("two");
            var registry = new PluginPolicyRegistry();
            foreach (var platform in loaded.Platforms)
                registry.Set("multi", platform.Name, platform.Configuration.BuiltProxyPolicy, platform.Configuration.BuiltAccountPolicy);
            registry.GetSnapshot("MULTI", "ONE").Attempts.MaxAttempts.Should().Be(2);
            registry.GetSnapshot("multi", "two").Attempts.MaxAttempts.Should().Be(4);
            await loaded.StartAsync(CancellationToken.None);
            var snapshot = await jobs.StartAsync("long-task", options: new PluginJobOptions { Platform = "two" });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await host.Services.State.Local.GetStringAsync("job-running", timeout.Token) is null) await Task.Delay(10, timeout.Token);
            loaded.JobInFlight.Should().Be(1);
            executions.Should().Be(1, "manual Cron jobs must go through the native task invoker");
            var finished = jobs.WaitAsync(snapshot.Id, timeout.Token);
            await loaded.DisposeAsync();
            (await finished)!.State.Should().Be(PluginJobState.Cancelled);
            hosts.Verify(value => value.Create("multi", It.Is<IReadOnlyList<string>>(names => names.Count == 2)), Times.Once);
        }
        finally
        {
            var target = Path.GetFullPath(root);
            if (target.StartsWith(Path.GetFullPath(boundary) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(target, recursive: true);
        }
    }

    [TestMethod]
    public async Task SelectionCannotPerformNetworkIoEvenWhenThePluginHasHttpPermission()
    {
        await using var fixture = new Fixture();
        await fixture.Accounts.SaveAsync(new Account { Id = "a", PluginKey = "api", Platform = "api", Credential = new ApiKeyCredential("key") });
        var requests = 0;
        fixture.Send = (_, _) => { requests++; return Task.FromResult(Fixture.Json(new { ok = true })); };
        var terminal = fixture.Create("""
            export async function select(ctx,input) {
              await ctx.http.request({url:"https://example.com",route:"direct"});
              return input.candidates.map(a=>({accountId:a.id}));
            }
            """, Fixture.Manifest() with { Hooks = new JsHooksManifest { Invoke = "invoke", SelectAccounts = "select" } });
        (await fixture.Execute(terminal, new AdapterRequest { Model = "m" })).Response.IsSuccess.Should().BeFalse();
        requests.Should().Be(0);
    }

    [TestMethod]
    public async Task MetadataWritesDoNotGrantImplicitDisablePermission()
    {
        await using var fixture = new Fixture();
        await fixture.Accounts.SaveAsync(new Account { Id = "a", PluginKey = "api", Platform = "api", Credential = new ApiKeyCredential("key") });
        var manifest = Fixture.Manifest() with { Permissions = Fixture.Manifest().Permissions with { Accounts = ["read", "write"] } };
        var terminal = fixture.Create("""
            export async function endpoint(ctx) { await ctx.accounts.save({id:"a",status:{state:"Disabled"}});return ctx.json(200,{}); }
            """, manifest);
        (await terminal.InvokeEndpointAsync("endpoint", new PluginHttpContext())).StatusCode.Should().Be(502);
        (await fixture.Accounts.GetAsync("api", "a"))!.Status.State.Should().Be(ResourceState.Active);
    }

    [TestMethod]
    public async Task BulkCredentialsRemainOptInAndRequireTheSeparatePermission()
    {
        await using var fixture = new Fixture();
        await fixture.Accounts.SaveAsync(new Account
        {
            Id = "bulk", PluginKey = "api", Platform = "api", Credential = new ApiKeyCredential("bulk-secret")
        });
        const string script = """
            export async function endpoint(ctx) {
              const ordinary = await ctx.accounts.list();
              try {
                const complete = await ctx.accounts.list({includeCredentials:true});
                await ctx.log.write({message:complete[0].credential.apiKey});
                return ctx.json(200,{ordinarySecret:ordinary[0].credential,read:complete[0].credential.kind});
              } catch(error) { return ctx.json(200,{ordinarySecret:ordinary[0].credential,code:error.code}); }
            }
            """;
        var restricted = fixture.Create(script, Fixture.Manifest() with
        {
            Permissions = Fixture.Manifest().Permissions with { Accounts = ["read"] }
        });
        var denied = await fixture.Endpoint(restricted);
        denied["ordinarySecret"].Should().BeNull();
        denied["code"]!.GetValue<string>().Should().Be("host.denied");
        var granted = await fixture.Endpoint(fixture.Create(script));
        granted["ordinarySecret"].Should().BeNull();
        granted["read"]!.GetValue<string>().Should().Be("ApiKey");
        fixture.Logs.Verify(value => value.WriteAsync(
            It.Is<PluginLog>(log => log.Message == "[redacted]"), It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _databasePath = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N") + "-js-api.db");
        private readonly SqlSugarDatabase _database;
        private readonly ProxyTransportFactory _transport = new();
        private readonly HttpMessageHandler _handler;
        private readonly List<JsPlatformTerminal> _terminals = [];
        public AccountService Accounts { get; }
        public IPluginServices Native { get; }
        public IPluginHost Host { get; }
        public PluginJobManager Jobs => (PluginJobManager)Native.Jobs;
        public Mock<IProxyPoolHttpClientFactory> Pool { get; } = new();
        public Mock<IPluginLogSink> Logs { get; } = new();
        public ISharedKeyValueStore Shared { get; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send { get; set; }
            = (_, _) => Task.FromException<HttpResponseMessage>(new InvalidOperationException("Unexpected HTTP call."));

        public Fixture()
        {
            var memory = new PluginMemoryState();
            var shared = new Mock<ISharedKeyValueStore>();
            shared.SetupGet(value => value.IsConfigured).Returns(true);
            shared.Setup(value => value.GetStringAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string key, CancellationToken token) => memory.GetStringAsync(key, token));
            shared.Setup(value => value.SetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns((string key, string value, TimeSpan ttl, CancellationToken token) => memory.SetStringAsync(key, value, ttl, token));
            shared.Setup(value => value.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string key, CancellationToken token) => memory.RemoveAsync(key, token));
            shared.Setup(value => value.GetExpiryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string key, CancellationToken token) => memory.GetExpiryAsync(key, token));
            shared.Setup(value => value.PutIfAbsentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns((string key, string value, TimeSpan ttl, CancellationToken token) => memory.PutIfAbsentAsync(key, value, ttl, token));
            shared.Setup(value => value.CompareExchangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns((string key, string? expected, string? value, TimeSpan ttl, CancellationToken token) => memory.CompareExchangeAsync(key, expected, value, ttl, token));
            shared.Setup(value => value.IncrementAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns((string key, long delta, TimeSpan ttl, CancellationToken token) => memory.IncrementAsync(key, delta, ttl, token));
            Shared = shared.Object;
            _database = new SqlSugarDatabase(Options.Create(new DatabaseOptions { Path = _databasePath }), NullLogger<SqlSugarDatabase>.Instance);
            new DatabaseInitializer(_database, NullLogger<DatabaseInitializer>.Instance).Initialize();
            Accounts = new AccountService(_database, Shared);
            Native = new PluginServices("api", ["api"], Accounts, Mock.Of<IModelCatalog>(), Mock.Of<IModelMetadataCatalog>(),
                _transport, Pool.Object, Shared, () => Mock.Of<IPluginTaskInvoker>(), Mock.Of<ITaskLogStore>(), Logs.Object,
                new PluginExecutionOptions(), new ResourceLeaseManager(), new PluginHttpOriginStore(_database));
            _handler = new CallbackHandler((request, token) => Send(request, token));
            Pool.Setup(value => value.CreateClientAsync(It.IsAny<ProxyPoolHttpClientOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new HttpClient(_handler, disposeHandler: false));
            var http = new Mock<IPluginHttpServices>();
            http.SetupGet(value => value.Pool).Returns(Pool.Object);
            http.Setup(value => value.CreateDirectClient(It.IsAny<PluginHttpClientOptions>())).Returns(() => new HttpClient(_handler, disposeHandler: false));
            http.Setup(value => value.GetApprovedOriginsAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken token) => Native.Http.GetApprovedOriginsAsync(token));
            http.Setup(value => value.ApproveOriginAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string origin, CancellationToken token) => Native.Http.ApproveOriginAsync(origin, token));
            http.Setup(value => value.RevokeOriginAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string origin, CancellationToken token) => Native.Http.RevokeOriginAsync(origin, token));
            var services = new Mock<IPluginServices>();
            services.SetupGet(value => value.PluginKey).Returns("api");
            services.SetupGet(value => value.Accounts).Returns(Native.Accounts);
            services.SetupGet(value => value.Http).Returns(http.Object);
            services.SetupGet(value => value.State).Returns(Native.State);
            services.SetupGet(value => value.Models).Returns(Native.Models);
            services.SetupGet(value => value.Tasks).Returns(Native.Tasks);
            services.SetupGet(value => value.Jobs).Returns(Native.Jobs);
            services.SetupGet(value => value.Log).Returns(Native.Log);
            services.SetupGet(value => value.Execution).Returns(Native.Execution);
            var host = new Mock<IPluginHost>();
            host.SetupGet(value => value.Services).Returns(services.Object);
            host.SetupGet(value => value.PluginKey).Returns("api");
            Host = host.Object;
        }

        public static JsPluginManifest Manifest() => new()
        {
            SchemaVersion = 1, Id = "api", Name = "API", Version = "1", Runtime = "jint", HostApi = "1", Entry = "plugin.mjs", Format = "esm-bundle",
            Platform = new JsPlatformManifest { Name = "api", CredentialKinds = ["ApiKey", "OAuth", "Custom", "BearerToken", "BasicAuth", "Cookie"] },
            Hooks = new JsHooksManifest { Invoke = "invoke" },
            Permissions = new JsPermissions
            {
                Accounts = ["read", "readCredentials", "write", "refresh", "disable", "setCooldown"],
                State = ["read", "write"], SharedState = ["read", "write"], Jobs = ["start", "read", "cancel"],
                Models = ["read", "refresh", "invalidate"], Tasks = ["run", "writeLog"],
                Crypto = ["random", "hash", "hmac", "encoding", "decimal"],
                Http = new JsHttpPermissions { Origins = ["https://example.com"], Routes = ["direct", "pool", "attempt"] }
            }
        };

        public JsPlatformTerminal Create(string script, JsPluginManifest? manifest = null)
        {
            manifest ??= Manifest();
            manifest.Validate("api");
            if (!script.Contains("function invoke(", StringComparison.Ordinal))
                script += "\nexport function invoke(ctx){return ctx.reply.completion({model:'m',content:'ok'});}";
            var terminal = new JsPlatformTerminal(new JsPluginPackage(manifest, script, null, "{}"), Host);
            _terminals.Add(terminal);
            var builder = new PluginBuilder();
            terminal.Configure(builder);
            if (builder.Jobs.Count > 0) Jobs.Register(builder.Jobs);
            return terminal;
        }

        public async Task<JsonNode> Endpoint(JsPlatformTerminal terminal)
        {
            var result = await terminal.InvokeEndpointAsync("endpoint", new PluginHttpContext());
            result.StatusCode.Should().BeOneOf(200, 202);
            return JsonSerializer.SerializeToNode(result.Body)!;
        }

        public async Task<PluginInvocationResult> Execute(JsPlatformTerminal terminal, AdapterRequest request)
        {
            using var resilience = new PluginResiliencePipelines();
            var builder = new PluginBuilder();
            terminal.Configure(builder);
            var policies = new PluginPolicyRegistry();
            policies.Set("api", builder.BuiltProxyPolicy, builder.BuiltAccountPolicy);
            var http = new Mock<IProxyHttpClientFactory>();
            http.Setup(value => value.CreateDirectClient("upstream")).Returns(() => new HttpClient(_handler, disposeHandler: false));
            var executor = new PluginAttemptExecutor(Accounts, Mock.Of<IProxyStore>(), Mock.Of<IProxySubscriptionService>(), new ResourceLeaseManager(),
                Mock.Of<IProxyPolicyStore>(), Shared, http.Object, policies, Mock.Of<IModelMetadataCatalog>(), Logs.Object,
                NullLogger<PluginAttemptExecutor>.Instance, resilience, Options.Create(new PluginExecutionOptions()));
            return await executor.ExecuteAsync("api", "api", terminal, request, "parity", null);
        }

        public async ValueTask DisposeAsync()
        {
            await Jobs.DisposeAsync();
            foreach (var terminal in _terminals) await terminal.DisposeAsync();
            _handler.Dispose();
            _transport.Dispose();
            _database.Scope.Dispose();
            try { File.Delete(_databasePath); } catch (IOException) { }
        }

        public static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        private sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => send(request, cancellationToken);
        }
    }
}
