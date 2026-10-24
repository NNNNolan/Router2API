using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;
using Router.Host.Api;
using Router.Host.Pipeline;
using Router.Host.Plugins.JavaScript;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class ModelMappingTests : IDisposable
{
    private readonly PluginResiliencePipelines _resilience = new();
    private readonly PlatformRegistry _platforms = new();
    private readonly PluginPolicyRegistry _policies = new();
    private readonly Mock<IAccountService> _accounts = new();
    private readonly List<RequestLog> _logs = [];
    private readonly RealtimeMetrics _metrics = new();
    private readonly SqlSugarDatabase _database;
    private readonly ModelMappingStore _store;
    private readonly PluginAttemptExecutor _executor;
    private readonly IOptions<PluginExecutionOptions> _options;
    private readonly Mock<TimeProvider> _clock = new();
    private readonly ModelMappingHealth _health;
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public ModelMappingTests()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-data", $"mapping-{Guid.NewGuid():N}.db");
        _database = new SqlSugarDatabase(Options.Create(new DatabaseOptions { Path = path }), NullLogger<SqlSugarDatabase>.Instance);
        new DatabaseInitializer(_database, NullLogger<DatabaseInitializer>.Instance).Initialize();
        _clock.Setup(clock => clock.GetUtcNow()).Returns(() => _now);
        _health = new ModelMappingHealth(_clock.Object);
        _store = new ModelMappingStore(_database, _health);
        _options = Options.Create(new PluginExecutionOptions { TotalSetupTimeout = TimeSpan.FromSeconds(2) });
        _accounts.Setup(value => value.GetAsync(It.IsAny<string>(), "account", It.IsAny<CancellationToken>()))
            .Returns((string plugin, string id, CancellationToken _) => Task.FromResult<Account?>(new Account { Id = id, PluginKey = plugin, Platform = plugin }));
        var http = new Mock<IProxyHttpClientFactory>();
        http.Setup(value => value.CreateDirectClient("upstream")).Returns(() => new HttpClient());
        _executor = new PluginAttemptExecutor(_accounts.Object, Mock.Of<IProxyStore>(), Mock.Of<IProxySubscriptionService>(),
            new ResourceLeaseManager(), Mock.Of<IProxyPolicyStore>(), Mock.Of<ISharedKeyValueStore>(), http.Object, _policies,
            Mock.Of<IModelMetadataCatalog>(), Mock.Of<IPluginLogSink>(), NullLogger<PluginAttemptExecutor>.Instance, _resilience, _options);
    }

    [TestCleanup]
    public void Dispose() => _resilience.Dispose();

    [TestMethod]
    public void StorePersistsOrderedTargetsAndSupportsRenameDisableDelete()
    {
        var saved = Save([Target("a", 2), Target("b", null)]);
        var reloaded = new ModelMappingStore(_database, _health);
        reloaded.Find("DS4").Should().BeEquivalentTo(saved, options => options.WithStrictOrdering());
        var updated = reloaded.Save(saved with { Name = "renamed", Enabled = false, Targets = saved.Targets.Reverse().ToArray() });
        reloaded.Find("ds4").Should().BeNull();
        reloaded.Find("RENAMED").Should().BeEquivalentTo(updated, options => options.WithStrictOrdering());
        Action duplicate = () => reloaded.Save(updated with { Id = "", Name = "Renamed" });
        duplicate.Should().Throw<InvalidOperationException>();
        reloaded.Delete(updated.Id).Should().BeTrue();
        reloaded.List().Should().BeEmpty();
    }

    [TestMethod]
    public void StoreNormalizesLegacyZeroAttemptCountsOnSaveAndRead()
    {
        var saved = Save([Target("a", 0)]);
        saved.Targets[0].RetryCount.Should().Be(1);
        var legacyTargets = JsonSerializer.Serialize(new[] { Target("a", 0) });
        using var db = _database.CreateClient();
        db.Updateable<ModelMappingEntity>().SetColumns(row => row.TargetsJson == legacyTargets)
            .Where(row => row.Id == saved.Id).ExecuteCommand();
        _store.Find(saved.Name)!.Targets[0].RetryCount.Should().Be(1);
        _store.List().Single().Targets[0].RetryCount.Should().Be(1);
    }

    [TestMethod]
    public void StoreRejectsInvalidNamesBudgetsDuplicateAndEmptyTargets()
    {
        var mapping = new ModelMapping("", "ds4", true, [Target("a", 0)]);
        Action badName = () => _store.Save(mapping with { Name = "a/model" });
        Action badBudget = () => _store.Save(mapping with { Targets = [Target("a", 36)] });
        Action duplicates = () => _store.Save(mapping with { Targets = [Target("a", 0), Target("A", 1)] });
        Action empty = () => _store.Save(mapping with { Targets = [] });
        Action disabledTargets = () => _store.Save(mapping with { Targets = [Target("a", 0) with { Enabled = false }] });
        badName.Should().Throw<ArgumentException>();
        badBudget.Should().Throw<ArgumentException>();
        duplicates.Should().Throw<ArgumentException>();
        empty.Should().Throw<ArgumentException>();
        disabledTargets.Should().Throw<ArgumentException>();
        _store.List().Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("ChatCompletions")]
    [DataRow("Completions")]
    [DataRow("Responses")]
    [DataRow("AnthropicMessages")]
    public async Task TargetsRunInOrderWithOverriddenBudgetsAndIndependentRequests(string endpointName)
    {
        var endpoint = Enum.Parse<TestEndpoint>(endpointName);
        var calls = new List<string>();
        Register("a", context =>
        {
            calls.Add($"a/{context.Request.Model}");
            context.Request.Extensions["reasoning_effort"] = "low";
            return Task.FromResult(Failure());
        });
        Register("b", context =>
        {
            calls.Add($"b/{context.Request.Model}");
            context.Request.Extensions["reasoning_effort"]?.ToString().Should().Be("high");
            context.Request.OriginalBody!.Value.GetProperty("model").GetString().Should().Be("real-b");
            context.Request.OriginalBody.Value.GetProperty("large").GetRawText().Should().Be("9007199254740993");
            context.Request.OriginalBody.Value.GetProperty("input").GetString().Should().Be("hello");
            context.Request.Endpoint.Should().Be(endpoint switch
            {
                TestEndpoint.Completions => "/v1/completions", TestEndpoint.Responses => "/v1/responses",
                TestEndpoint.AnthropicMessages => "/v1/messages", _ => "/v1/chat/completions"
            });
            return Task.FromResult(Success());
        });
        Save([Target("a", 3), Target("b", 1)]);
        using var body = JsonDocument.Parse("""{"model":"ds4","messages":[{"role":"user","content":"hello"}],"input":"hello","prompt":"hello","max_tokens":32,"reasoning_effort":"high","large":9007199254740993}""");
        var context = Context(RequestParser.Parse(body.RootElement, endpoint));
        await Pipeline().ExecuteAsync(context);
        calls.Should().Equal("a/real-a", "a/real-a", "a/real-a", "b/real-b");
        context.Response!.IsSuccess.Should().BeTrue();
        context.Request.Model.Should().Be("ds4");
        context.Request.Extensions["reasoning_effort"]?.ToString().Should().Be("high");
        context.SelectedPlatform.Should().Be("b");
        context.AttemptDetails.Should().HaveCount(4);
        context.AttemptDetails[0].Reason.Should().StartWith("a/a/real-a:");
        context.AttemptDetails[3].Reason.Should().StartWith("b/b/real-b:");
        _policies.Get("a").MaxAttempts.Should().Be(4, "overrides must not mutate the plugin policy");
        _logs.Should().ContainSingle(log => log.Model == "ds4" && log.Platform == "b" && log.Success);
        _metrics.Snapshot().ActiveConnections.Should().Be(0);
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(1, 1)]
    [DataRow(2, 2)]
    [DataRow(3, 3)]
    [DataRow(35, 35)]
    [DataRow(null, 4)]
    public async Task MappingAttemptBudgetIncludesTheFirstRequest(int? attemptCount, int expectedCalls)
    {
        var calls = 0;
        Register("a", _ => { calls++; return Task.FromResult(Failure()); });
        Save([Target("a", attemptCount)]);
        await Pipeline().ExecuteAsync(Context());
        calls.Should().Be(expectedCalls);
    }

    [TestMethod]
    public async Task UnavailableAndAccountlessTargetsAreSkippedButDisabledMappingDoesNotFallBack()
    {
        var calls = new List<string>();
        Register("a", _ => throw new InvalidOperationException("No account should reach this terminal"));
        _accounts.Setup(value => value.GetAsync("a", "account", It.IsAny<CancellationToken>())).ReturnsAsync((Account?)null);
        Register("b", _ => { calls.Add("b"); return Task.FromResult(Success()); });
        var mapping = Save([Target("missing", 0), Target("a", 0), Target("b", 0)]);
        var context = Context();
        await Pipeline().ExecuteAsync(context);
        context.Response!.IsSuccess.Should().BeTrue();
        context.AttemptDetails.Should().HaveCount(3);
        calls.Should().Equal("b");
        _store.Save(mapping with { Enabled = false });
        context = Context();
        await Pipeline().ExecuteAsync(context);
        context.Response!.StatusCode.Should().Be(404);
        calls.Should().Equal("b");
        context = Context(new AdapterRequest { Model = "b/real-b" });
        await Pipeline().ExecuteAsync(context);
        context.Response!.IsSuccess.Should().BeTrue();
        calls.Should().Equal("b", "b");
    }

    [TestMethod]
    [DataRow(400, true)]
    [DataRow(413, true)]
    [DataRow(503, false)]
    public async Task ClientErrorsAndNonReplayableFailuresDoNotSwitchTargets(int status, bool retry)
    {
        var secondCalls = 0;
        Register("a", _ => Task.FromResult(Failure(status, retry)));
        Register("b", _ => { secondCalls++; return Task.FromResult(Success()); });
        Save([Target("a", 0), Target("b", 0)]);
        var context = Context();
        await Pipeline().ExecuteAsync(context);
        context.Response!.StatusCode.Should().Be(status);
        secondCalls.Should().Be(0);
    }

    [TestMethod]
    public async Task ReturnedStreamNeverSwitchesAndLogsItsActualWriteFailure()
    {
        var secondCalls = 0;
        Register("a", _ => Task.FromResult(new PluginInvocationResult(new AdapterResponse
        {
            StatusCode = 200, IsStreaming = true, Stream = BrokenBody()
        }, new PluginAttemptResult(PluginAttemptOutcome.Healthy))));
        Register("b", _ => { secondCalls++; return Task.FromResult(Success()); });
        Save([Target("a", 3), Target("b", 0)]);
        var context = Context();
        await Pipeline().ExecuteAsync(context);
        _logs.Should().BeEmpty("stream logs must wait for actual consumption");
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        await ProtocolResponseWriter.WriteAsync(http, context.Response!, TestEndpoint.ChatCompletions, "ds4", CancellationToken.None);
        System.Text.Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray()).Should().Contain("Upstream stream failed.");
        secondCalls.Should().Be(0);
        _logs.Should().ContainSingle(log => !log.Success);
        _metrics.Snapshot().ActiveConnections.Should().Be(0);
    }

    [TestMethod]
    public async Task CallerCancellationPreservesAttemptsAcrossTargets()
    {
        using var cancellation = new CancellationTokenSource();
        Register("a", _ => Task.FromResult(Failure()));
        Register("b", context => { cancellation.Cancel(); context.CancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Success()); });
        Save([Target("a", 0), Target("b", 0)]);
        var request = Context();
        request.CancellationToken = cancellation.Token;
        Func<Task> execute = () => Pipeline().ExecuteAsync(request);
        await execute.Should().ThrowAsync<OperationCanceledException>();
        request.AttemptDetails.Should().ContainSingle();
        request.AttemptDetails[0].Reason.Should().StartWith("a/a/real-a:");
        _logs.Should().ContainSingle(log => log.Cancelled && !log.Success);
        _metrics.Snapshot().ActiveConnections.Should().Be(0);
    }

    [TestMethod]
    public async Task AllTargetsShareTheSetupDeadline()
    {
        Register("a", async context => { await Task.Delay(1100, context.CancellationToken); return Failure(); });
        Register("b", async context => { await Task.Delay(1100, context.CancellationToken); return Success(); });
        Save([Target("a", 0), Target("b", 0)]);
        var request = Context();
        await Pipeline().ExecuteAsync(request);
        request.SelectedPlatform.Should().Be("b");
        request.Response!.StatusCode.Should().Be(504, "B must not get a fresh two-second setup budget");
    }

    [TestMethod]
    [DataRow(1, 2, false)]
    [DataRow(4, 0, true)]
    [DataRow(4, 1, true)]
    public async Task JsTransportReplayGuardUsesEffectiveMappingBudget(int manifestAttempts, int attemptCount, bool allowed)
    {
        var pool = new Mock<IProxyPoolHttpClientFactory>();
        pool.Setup(value => value.SendAsync(It.IsAny<Func<HttpRequestMessage>>(), It.IsAny<ProxyPoolHttpClientOptions>(),
                It.IsAny<ProxyPoolRetryOptions>(), It.IsAny<HttpCompletionOption>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("ok") });
        var manifest = new JsPluginManifest
        {
            SchemaVersion = 1, Id = "a", Name = "a", Version = "1", Runtime = "jint", HostApi = "1", Entry = "plugin.mjs", Format = "esm-bundle",
            Platform = new JsPlatformManifest { Name = "a" }, Hooks = new JsHooksManifest { Invoke = "invoke" },
            Policy = new JsPolicyManifest { MaxAttempts = manifestAttempts },
            Permissions = new JsPermissions { Http = new JsHttpPermissions { Origins = ["https://example.com"], Routes = ["pool"] } }
        };
        using var terminal = new JsPlatformTerminal(new JsPluginPackage(manifest, """
            export async function invoke(ctx) {
              await ctx.http.request({url:'https://example.com',route:'pool',responseType:'text',retry:{maxRetries:1}});
              return {response:{kind:'completion',completion:{model:'real-a',content:'ok',finishReason:'stop'}},attempt:{outcome:'Healthy'}};
            }
            """, null, "{}"), PluginTestHost.Create("a", pool.Object));
        _platforms.Register(new("a", "a", "a", terminal));
        _policies.Set("a", new PluginProxyPolicyBuilder().MaxAttempts(manifestAttempts).Build());
        Save([Target("a", attemptCount)]);
        var request = Context();
        await Pipeline().ExecuteAsync(request);
        request.Response!.IsSuccess.Should().Be(allowed);
        pool.Verify(value => value.SendAsync(It.IsAny<Func<HttpRequestMessage>>(), It.IsAny<ProxyPoolHttpClientOptions>(),
                It.IsAny<ProxyPoolRetryOptions>(), It.IsAny<HttpCompletionOption>(), It.IsAny<CancellationToken>()), allowed ? Times.Once() : Times.Never());
    }

    [TestMethod]
    public async Task ConcurrentAliasesOfTheSamePluginKeepTheirBudgetsSeparate()
    {
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        Register("a", async context =>
        {
            counts.AddOrUpdate(context.TraceId, 1, (_, count) => count + 1);
            await Task.Yield();
            return Failure();
        });
        Save([Target("a", 0)]);
        _store.Save(new("", "long", true, [Target("a", 3)]));
        var shortRequest = Context();
        var longRequest = Context(new AdapterRequest { Model = "long" });
        await Task.WhenAll(Pipeline().ExecuteAsync(shortRequest), Pipeline().ExecuteAsync(longRequest));
        counts[shortRequest.TraceId].Should().Be(1);
        counts[longRequest.TraceId].Should().Be(3);
        _policies.Get("a").MaxAttempts.Should().Be(4);
    }

    [TestMethod]
    public async Task AcceptedStreamOutlivesSetupBudgetAndKeepsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken streamToken = default;
        Register("a", context =>
        {
            streamToken = context.CancellationToken;
            return Task.FromResult(new PluginInvocationResult(new AdapterResponse { StatusCode = 200, IsStreaming = true, Stream = BrokenBody() },
                new PluginAttemptResult(PluginAttemptOutcome.Healthy)));
        });
        Save([Target("a", 0)]);
        var request = Context();
        request.CancellationToken = cancellation.Token;
        await Pipeline().ExecuteAsync(request);
        await Task.Delay(2100, CancellationToken.None);
        streamToken.IsCancellationRequested.Should().BeFalse("the setup deadline ends when the response stream is accepted");
        cancellation.Cancel();
        streamToken.IsCancellationRequested.Should().BeTrue();
        await request.Response!.Lifetime!.DisposeAsync();
        _metrics.Snapshot().ActiveConnections.Should().Be(0);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task ThreeFailedRequestsCoolOnlyTheTargetAndResumeAtFiveMinutes(int attemptCount)
    {
        var firstCalls = 0;
        var secondCalls = 0;
        Register("a", _ => { firstCalls++; return Task.FromResult(Failure()); });
        Register("b", _ => { secondCalls++; return Task.FromResult(Success()); });
        Save([Target("a", attemptCount), Target("b", 1)]);
        for (var index = 1; index <= 3; index++)
        {
            await Pipeline().ExecuteAsync(Context());
            firstCalls.Should().Be(index * attemptCount, "the first attempt belongs to the configured total budget");
        }
        var cooling = Context();
        await Pipeline().ExecuteAsync(cooling);
        firstCalls.Should().Be(3 * attemptCount);
        secondCalls.Should().Be(4);
        cooling.Response!.IsSuccess.Should().BeTrue();
        cooling.AttemptDetails[0].Outcome.Should().Be("Skipped");
        cooling.AttemptDetails[0].Reason.Should().Contain("cooling down until");
        _now += TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1);
        await Pipeline().ExecuteAsync(Context());
        firstCalls.Should().Be(3 * attemptCount);
        _now += TimeSpan.FromMilliseconds(1);
        await Pipeline().ExecuteAsync(Context());
        await Pipeline().ExecuteAsync(Context());
        firstCalls.Should().Be(5 * attemptCount, "cooldown expiry starts a fresh failure streak");
    }

    [TestMethod]
    public async Task SuccessfulRequestResetsTheFailureStreak()
    {
        var calls = 0;
        Register("a", _ => Task.FromResult(++calls == 3 ? Success() : Failure()));
        Save([Target("a", 0)]);
        for (var index = 0; index < 6; index++) await Pipeline().ExecuteAsync(Context());
        calls.Should().Be(6, "two failures, success, then three failures are needed to cool down");
        var cooling = Context();
        await Pipeline().ExecuteAsync(cooling);
        calls.Should().Be(6);
        cooling.Response!.StatusCode.Should().Be(503);
        cooling.Response.Error.Should().Be("no available target for model mapping");
        await Pipeline().ExecuteAsync(Context(new AdapterRequest { Model = "a/real-a" }));
        calls.Should().Be(10, "explicit platform routing keeps the plugin's four attempts and does not use mapping cooldowns");
    }

    [TestMethod]
    public async Task ParameterErrorsAndCallerCancellationDoNotCountOrResetTheStreak()
    {
        var status = 503;
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        Register("a", context =>
        {
            calls++;
            if (status == 0)
            {
                cancellation.Cancel();
                context.CancellationToken.ThrowIfCancellationRequested();
            }
            return Task.FromResult(Failure(status));
        });
        Save([Target("a", 0)]);
        for (var index = 0; index < 2; index++) await Pipeline().ExecuteAsync(Context());
        foreach (var clientStatus in new[] { 400, 413, 415, 422 })
        {
            status = clientStatus;
            var clientError = Context();
            await Pipeline().ExecuteAsync(clientError);
            clientError.Response!.StatusCode.Should().Be(clientStatus);
        }
        status = 0;
        var cancelled = Context();
        cancelled.CancellationToken = cancellation.Token;
        Func<Task> execute = () => Pipeline().ExecuteAsync(cancelled);
        await execute.Should().ThrowAsync<OperationCanceledException>();
        status = 503;
        await Pipeline().ExecuteAsync(Context());
        var cooling = Context();
        await Pipeline().ExecuteAsync(cooling);
        calls.Should().Be(8);
        cooling.AttemptDetails.Should().ContainSingle(detail => detail.Outcome == "Skipped");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task StreamCompletionDeterminesWhetherTheFailureStreakResets(bool success, bool raw)
    {
        var streaming = false;
        var firstCalls = 0;
        Register("a", _ =>
        {
            firstCalls++;
            return Task.FromResult(streaming ? new PluginInvocationResult(new AdapterResponse
            {
                StatusCode = 200, IsStreaming = true, IsRawPassthrough = raw,
                Stream = raw ? null : success ? CompletedBody() : BrokenBody(), RawStream = raw ? RawBody(success) : null
            }, new PluginAttemptResult(PluginAttemptOutcome.Healthy)) : Failure());
        });
        Register("b", _ => Task.FromResult(Success()));
        Save([Target("a", 0), Target("b", 0)]);
        for (var index = 0; index < 2; index++) await Pipeline().ExecuteAsync(Context());
        streaming = true;
        var stream = Context();
        await Pipeline().ExecuteAsync(stream);
        firstCalls.Should().Be(3);
        stream.SelectedPlatform.Should().Be("a");
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        Func<Task> write = () => ProtocolResponseWriter.WriteAsync(http, stream.Response!, TestEndpoint.ChatCompletions, "ds4", CancellationToken.None);
        if (raw && !success) await write.Should().ThrowAsync<InvalidOperationException>();
        else await write();
        streaming = false;
        if (success)
            for (var index = 0; index < 3; index++) await Pipeline().ExecuteAsync(Context());
        var cooling = Context();
        await Pipeline().ExecuteAsync(cooling);
        firstCalls.Should().Be(success ? 6 : 3, "HTTP 200 alone must not reset the streak before the stream finishes");
        cooling.SelectedPlatform.Should().Be("b");
        cooling.AttemptDetails[0].Outcome.Should().Be("Skipped");
    }

    [TestMethod]
    public async Task OtherModelsOfTheSamePluginAndOtherAliasesAreIndependent()
    {
        var failedModelCalls = 0;
        var otherModelCalls = 0;
        Register("a", context =>
        {
            if (context.Request.Model == "real-a") { failedModelCalls++; return Task.FromResult(Failure()); }
            otherModelCalls++;
            return Task.FromResult(Success());
        });
        Save([Target("a", 0), Target("a", 0) with { Model = "other-model" }]);
        _store.Save(new("", "other-alias", true, [Target("a", 0)]));
        for (var index = 0; index < 4; index++) await Pipeline().ExecuteAsync(Context());
        failedModelCalls.Should().Be(3);
        otherModelCalls.Should().Be(4);
        await Pipeline().ExecuteAsync(Context(new AdapterRequest { Model = "other-alias" }));
        failedModelCalls.Should().Be(4);
    }

    [TestMethod]
    public async Task CallerCancellingAnUnconsumedStreamDoesNotCoolTheTarget()
    {
        var streaming = false;
        var calls = 0;
        Register("a", _ =>
        {
            calls++;
            return Task.FromResult(streaming ? new PluginInvocationResult(new AdapterResponse
            {
                StatusCode = 200, IsStreaming = true, Stream = CompletedBody()
            }, new PluginAttemptResult(PluginAttemptOutcome.Healthy)) : Failure());
        });
        Save([Target("a", 0)]);
        for (var index = 0; index < 2; index++) await Pipeline().ExecuteAsync(Context());
        streaming = true;
        using var cancellation = new CancellationTokenSource();
        var stream = Context();
        stream.CancellationToken = cancellation.Token;
        await Pipeline().ExecuteAsync(stream);
        cancellation.Cancel();
        await stream.Response!.Lifetime!.DisposeAsync();
        streaming = false;
        await Pipeline().ExecuteAsync(Context());
        await Pipeline().ExecuteAsync(Context());
        calls.Should().Be(4, "cancellation leaves two failures, then the next real failure triggers cooldown");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConcurrentFailuresAndOldCompletionsCannotExtendOrClearCooldown(bool oldSuccess)
    {
        var mapping = Save([Target("a", 0)]);
        var admitted = Enumerable.Range(0, 5).Select(_ => Begin(mapping)).ToArray();
        var results = await Task.WhenAll(admitted.Take(3).Select(invocation => Task.Run(() => invocation.Complete(false))));
        results.Count(result => result is not null).Should().Be(1, "the third completed failure triggers one cooldown");
        var until = _now.AddMinutes(5);
        _now += TimeSpan.FromMinutes(1);
        admitted[3].Complete(true).Should().BeNull();
        _health.TryBegin(mapping.Id, mapping.Targets[0], out var deadline).Should().BeNull();
        deadline.Should().Be(until, "a success from an already admitted request cannot reopen the target early");
        _now = until;
        Begin(mapping).Complete(false).Should().BeNull();
        admitted[4].Complete(oldSuccess).Should().BeNull();
        Begin(mapping).Complete(false).Should().BeNull();
        Begin(mapping).Complete(false).Should().Be(_now.AddMinutes(5), "old completions cannot change the new failure streak");
    }

    [TestMethod]
    public void EditingRemovedTargetsAndDeletingMappingsDiscardOldResults()
    {
        var mapping = Save([Target("a", 0), Target("b", 0)]);
        for (var index = 0; index < 3; index++) Begin(mapping, mapping.Targets[1]).Complete(false);
        for (var index = 0; index < 2; index++) Begin(mapping).Complete(false);
        var removed = Begin(mapping);
        var reduced = _store.Save(mapping with { Name = "renamed", Targets = [mapping.Targets[1]] });
        removed.Complete(false).Should().BeNull();
        _health.TryBegin(reduced.Id, reduced.Targets[0], out _).Should().BeNull("renaming does not reset retained targets");
        mapping = _store.Save(reduced with { Targets = mapping.Targets });
        for (var index = 0; index < 2; index++) Begin(mapping).Complete(false).Should().BeNull();
        var deleted = Begin(mapping);
        _store.Delete(mapping.Id).Should().BeTrue();
        deleted.Complete(false).Should().BeNull();
    }

    private ModelMappingHealth.Invocation Begin(ModelMapping mapping, ModelMappingTarget? target = null)
    {
        var invocation = _health.TryBegin(mapping.Id, target ?? mapping.Targets[0], out _);
        invocation.Should().NotBeNull();
        return invocation!;
    }

    private static async IAsyncEnumerable<StreamChunk> CompletedBody()
    {
        yield return new("ok");
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> RawBody(bool success)
    {
        yield return System.Text.Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n");
        await Task.Yield();
        if (!success) throw new InvalidOperationException("upstream raw body broke");
        yield return System.Text.Encoding.UTF8.GetBytes("data: [DONE]\n\n");
    }

    private ModelMapping Save(ModelMappingTarget[] targets) => _store.Save(new("", "ds4", true, targets));
    private static ModelMappingTarget Target(string plugin, int? attemptCount) => new(plugin, plugin, $"real-{plugin}", RetryCount: attemptCount);
    private static RequestContext Context(AdapterRequest? request = null) => new()
    {
        Request = request ?? new AdapterRequest { Model = "ds4" }, TestOverrides = new TestOverrides { AccountId = "account", SkipCooldown = true }
    };
    private void Register(string plugin, Func<PluginAttemptContext, Task<PluginInvocationResult>> invoke)
    {
        var terminal = new Mock<IPlatformTerminal>();
        terminal.Setup(value => value.InvokeAsync(It.IsAny<PluginAttemptContext>())).Returns(invoke);
        _platforms.Register(new(plugin, plugin, plugin, terminal.Object));
        _policies.Set(plugin, new PluginProxyPolicyBuilder().MaxAttempts(4).Build());
    }
    private RouterPipeline Pipeline()
    {
        var logs = new Mock<ILogSink<RequestLog>>();
        logs.Setup(value => value.WriteAsync(It.IsAny<RequestLog>(), It.IsAny<CancellationToken>()))
            .Returns((RequestLog log, CancellationToken _) => { lock (_logs) _logs.Add(log); return Task.CompletedTask; });
        return new(new ModelRouter(_platforms), _platforms, _executor, _metrics, logs.Object,
            Mock.Of<IUsageBucketStore>(), NullLogger<RouterPipeline>.Instance, _health, _store, _executor, _options);
    }
    private static PluginInvocationResult Failure(int status = 503, bool retry = true)
        => new(new AdapterResponse { StatusCode = status, Error = "failed" }, new PluginAttemptDecision
        {
            FailureKind = PluginFailureKind.Upstream, Retry = retry ? PluginRetryAction.NextAttempt : PluginRetryAction.None, ReasonCode = "test"
        }.ToResult(status));
    private static PluginInvocationResult Success()
        => new(new AdapterResponse { StatusCode = 200, Completion = new("real", "ok", Usage: new(1, 1, 2)) }, new PluginAttemptResult(PluginAttemptOutcome.Healthy));
    private static async IAsyncEnumerable<StreamChunk> BrokenBody()
    {
        yield return new("partial");
        await Task.Yield();
        throw new InvalidOperationException("upstream body broke");
    }
}
