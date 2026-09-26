using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;
using Router.Host.Plugins.JavaScript;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class PluginDecisionTests
{
    [TestMethod]
    public void ExplicitDecisionBypassesAllLegacyPredicates()
    {
        var policy = new PluginProxyPolicyBuilder()
            .RetryWhen(_ => throw new InvalidOperationException("legacy"))
            .CooldownAccountWhen(_ => throw new InvalidOperationException("legacy"))
            .DisableAccountWhen(_ => throw new InvalidOperationException("legacy"))
            .CooldownNodeWhen(_ => throw new InvalidOperationException("legacy")).Build();
        var deadline = DateTimeOffset.UtcNow.AddHours(12);
        var attempt = new PluginAttemptDecision
        {
            FailureKind = PluginFailureKind.Upstream, AccountAction = PluginAccountAction.Cooldown,
            AccountCooldownUntil = deadline, ReasonCode = "credits"
        }.ToResult(429, "unstructured text that the host must not parse");
        var result = PluginAttemptDecisions.Resolve(Failure(attempt), policy, new(null, null, null), false, false);
        result.Decision!.AccountCooldownUntil.Should().Be(deadline);
        result.Decision.Retry.Should().Be(PluginRetryAction.None);
    }

    [TestMethod]
    public void LegacyPredicatesAreMappedOnceAndAccountPolicyKeepsItsPrecedence()
    {
        var retries = 0;
        var nodes = 0;
        var accounts = 0;
        var policy = new PluginProxyPolicyBuilder()
            .RetryWhen(_ => { retries++; return true; })
            .CooldownNodeWhen(_ => { nodes++; return true; })
            .CooldownAccountWhen(_ => throw new InvalidOperationException("overridden")).Build();
        var accountPolicy = new PluginAccountPolicy(null, _ => { accounts++; return true; }, _ => false);
        var result = PluginAttemptDecisions.Resolve(Failure(new PluginAttemptResult(PluginAttemptOutcome.Retry, 429)),
            policy, accountPolicy, false, false);
        result.Decision!.Retry.Should().Be(PluginRetryAction.NextAttempt);
        result.Decision.AccountAction.Should().Be(PluginAccountAction.Cooldown);
        result.Decision.ProxyAction.Should().Be(PluginProxyAction.None, "there is no native proxy fault");
        (retries, nodes, accounts).Should().Be((1, 1, 1));
    }

    [TestMethod]
    public void InventedTransportFlagsAndProxyStatusCannotPenalizeANode()
    {
        var forged = new PluginAttemptResult(PluginAttemptOutcome.CooldownNode, 407, IsTransportFailure: true);
        var result = PluginAttemptDecisions.Resolve(Failure(forged), PluginProxyPolicy.Default, new(null, null, null), false, false);
        result.IsTransportFailure.Should().BeFalse();
        result.Decision!.ProxyAction.Should().Be(PluginProxyAction.None);
        var actual = PluginAttemptDecisions.Resolve(Failure(forged), PluginProxyPolicy.Default, new(null, null, null), true, true);
        actual.IsTransportFailure.Should().BeTrue();
        actual.Decision!.ProxyAction.Should().Be(PluginProxyAction.Cooldown);
    }

    [TestMethod]
    public async Task ExplicitAccountCooldownIsAppliedOnceWithoutLosingItsDeadline()
    {
        using var resilience = new PluginResiliencePipelines();
        var accounts = Accounts();
        var deadline = DateTimeOffset.UtcNow.AddHours(12);
        var terminal = Terminal(_ => Task.FromResult(Failure(new PluginAttemptDecision
        {
            FailureKind = PluginFailureKind.Upstream, AccountAction = PluginAccountAction.Cooldown,
            AccountCooldownUntil = deadline, AccountReason = "credit-exhausted", ReasonCode = "credit"
        }.ToResult(429, "upstream message"))));
        var executor = Executor(accounts, new PluginPolicyRegistry(), resilience);
        await executor.ExecuteAsync("test", "test", terminal, Request(), "trace", Overrides());
        accounts.Verify(value => value.SetCooldownAsync("test", "a", deadline, "credit-exhausted", 429, It.IsAny<CancellationToken>()), Times.Once);
        accounts.Verify(value => value.DisableAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task InvalidDecisionDoesNotApplyAnyPartialResourceAction()
    {
        using var resilience = new PluginResiliencePipelines();
        var accounts = Accounts();
        var terminal = Terminal(_ => Task.FromResult(Failure(new PluginAttemptDecision
        {
            FailureKind = PluginFailureKind.Upstream, AccountAction = PluginAccountAction.Cooldown,
            AccountCooldownUntil = DateTimeOffset.UtcNow.AddDays(365), ProxyAction = PluginProxyAction.Cooldown
        }.ToResult(429))));
        var result = await Executor(accounts, new PluginPolicyRegistry(), resilience)
            .ExecuteAsync("test", "test", terminal, Request(), "trace", Overrides());
        result.Response.IsSuccess.Should().BeFalse();
        accounts.Verify(value => value.SetCooldownAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task SharedAttemptPipelineKeepsPerInvocationBudgetsSeparate()
    {
        using var resilience = new PluginResiliencePipelines();
        var policies = new PluginPolicyRegistry();
        policies.Set("one", new PluginProxyPolicyBuilder().MaxAttempts(1).Build());
        policies.Set("four", new PluginProxyPolicyBuilder().MaxAttempts(4).Build());
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var terminal = Terminal(async context =>
        {
            counts.AddOrUpdate(context.PluginKey, 1, (_, value) => value + 1);
            await Task.Yield();
            return Failure(new PluginAttemptDecision
            {
                FailureKind = PluginFailureKind.Upstream, Retry = PluginRetryAction.NextAttempt
            }.ToResult(503));
        });
        var executor = Executor(Accounts(), policies, resilience);
        var results = await Task.WhenAll(
            executor.ExecuteAsync("one", "one", terminal, Request(), "one", Overrides()),
            executor.ExecuteAsync("four", "four", terminal, Request(), "four", Overrides()));
        counts["one"].Should().Be(1);
        counts["four"].Should().Be(4);
        results.Select(result => result.AttemptDetails!.Count).Should().Equal(1, 4);
    }

    [TestMethod]
    public async Task ReturnedStreamIsNeverReplayedEvenWhenThePluginRequestsRetry()
    {
        using var resilience = new PluginResiliencePipelines();
        var calls = 0;
        var terminal = Terminal(_ =>
        {
            calls++;
            return Task.FromResult(new PluginInvocationResult(new AdapterResponse
            {
                StatusCode = 502, IsStreaming = true, Stream = Body()
            }, new PluginAttemptDecision
            {
                FailureKind = PluginFailureKind.Upstream, Retry = PluginRetryAction.NextAttempt
            }.ToResult(502)));
        });
        var result = await Executor(Accounts(), new PluginPolicyRegistry(), resilience)
            .ExecuteAsync("test", "test", terminal, Request(), "trace", Overrides());
        calls.Should().Be(1);
        result.Attempt.Decision!.Retry.Should().Be(PluginRetryAction.None);
        await result.Response.Lifetime!.DisposeAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task JsProxyPenaltiesRequireAnActualNativeFault(bool transportFails)
    {
        using var resilience = new PluginResiliencePipelines();
        var proxy = new ProxyEndpoint { Id = "node", SubscriptionId = "sub", Host = "localhost", Port = 8080 };
        var proxies = new Mock<IProxyStore>();
        proxies.Setup(value => value.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { proxy });
        var subscriptions = new Mock<IProxySubscriptionService>();
        subscriptions.Setup(value => value.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new ProxySubscription { Id = "sub" } });
        var factory = HttpFactory();
        factory.Setup(value => value.CreateClient(proxy, "upstream")).Returns(() => new HttpClient(new ReplyHandler(transportFails)));
        var reports = new Mock<IProxyPolicyStore>();
        var manifest = new JsPluginManifest
        {
            SchemaVersion = 1, Id = "test", Name = "test", Version = "1", Runtime = "jint", HostApi = "1-preview",
            Entry = "plugin.mjs", Format = "esm-bundle", Platform = new JsPlatformManifest { Name = "test" },
            Hooks = new JsHooksManifest { Invoke = "invoke" },
            Permissions = new JsPermissions { Http = new JsHttpPermissions { Origins = ["https://example.com"], Routes = ["attempt"] } }
        };
        using var terminal = new JsPlatformTerminal(new JsPluginPackage(manifest, """
            export async function invoke(ctx) {
              try { await ctx.http.request({url:"https://example.com",route:"attempt",responseType:"text"}); } catch {}
              return {response:{kind:"error",statusCode:502,message:"invented"},
                attempt:{decision:{failureKind:"Transport",proxyAction:"Cooldown",reasonCode:"transport"}}};
            }
            """, null, "{}"), PluginTestHost.Create("test"));
        var executor = new PluginAttemptExecutor(Accounts().Object, proxies.Object, subscriptions.Object, new ResourceLeaseManager(),
            reports.Object, Mock.Of<ISharedKeyValueStore>(), factory.Object, new PluginPolicyRegistry(), Mock.Of<IModelMetadataCatalog>(),
            Mock.Of<IPluginLogSink>(), NullLogger<PluginAttemptExecutor>.Instance, resilience, Options.Create(new PluginExecutionOptions()));
        var result = await executor.ExecuteAsync("test", "test", terminal, Request(), "trace", Overrides());
        result.Attempt.Decision!.ProxyAction.Should().Be(transportFails ? PluginProxyAction.Cooldown : PluginProxyAction.None);
        result.Attempt.IsTransportFailure.Should().Be(transportFails);
        reports.Verify(value => value.ReportAsync(It.IsAny<string>(), It.IsAny<ProxyEndpoint>(), It.IsAny<PluginAttemptResult>(),
            It.IsAny<CancellationToken>()), transportFails ? Times.Once() : Times.Never());
        proxy.Status.InFlight.Should().Be(0);
    }

    [TestMethod]
    public async Task NativeResponseBodyFailuresRemainObservableAfterHeadersRead()
    {
        var proxy = new ProxyEndpoint { Host = "localhost", Port = 8080 };
        var binding = new PluginProxyBinding(proxy, new HttpClient(new BodyFailureHandler()), Mock.Of<IAsyncDisposable>());
        await using var client = new PluginHttpClient(_ => Task.FromResult<PluginProxyBinding?>(binding), new HttpClient(new ReplyHandler()));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        client.ObservedTransportFailure.Should().BeFalse();
        Func<Task> read = () => response.Content.ReadAsStringAsync();
        await read.Should().ThrowAsync<Exception>();
        client.ObservedTransportFailure.Should().BeTrue();
        client.ObservedProxyFault.Should().BeTrue();
    }

    private static PluginInvocationResult Failure(PluginAttemptResult attempt) => new(new AdapterResponse { StatusCode = 503, Error = "failed" }, attempt);
    private static AdapterRequest Request() => new() { Model = "m" };
    private static TestOverrides Overrides() => new() { AccountId = "a", SkipCooldown = true };
    private static IPlatformTerminal Terminal(Func<PluginAttemptContext, Task<PluginInvocationResult>> invoke)
    {
        var terminal = new Mock<IPlatformTerminal>();
        terminal.Setup(value => value.InvokeAsync(It.IsAny<PluginAttemptContext>())).Returns(invoke);
        return terminal.Object;
    }
    private static Mock<IAccountService> Accounts()
    {
        var accounts = new Mock<IAccountService>();
        accounts.Setup(value => value.GetAsync(It.IsAny<string>(), "a", It.IsAny<CancellationToken>()))
            .Returns((string key, string id, CancellationToken _) => Task.FromResult<Account?>(new Account { Id = id, PluginKey = key, Platform = key }));
        return accounts;
    }
    private static Mock<IProxyHttpClientFactory> HttpFactory()
    {
        var factory = new Mock<IProxyHttpClientFactory>();
        factory.Setup(value => value.CreateDirectClient("upstream")).Returns(() => new HttpClient(new ReplyHandler()));
        return factory;
    }
    private static PluginAttemptExecutor Executor(Mock<IAccountService> accounts, PluginPolicyRegistry policies, PluginResiliencePipelines resilience)
        => new(accounts.Object, Mock.Of<IProxyStore>(), Mock.Of<IProxySubscriptionService>(), new ResourceLeaseManager(),
            Mock.Of<IProxyPolicyStore>(), Mock.Of<ISharedKeyValueStore>(), HttpFactory().Object, policies,
            Mock.Of<IModelMetadataCatalog>(), Mock.Of<IPluginLogSink>(), NullLogger<PluginAttemptExecutor>.Instance,
            resilience, Options.Create(new PluginExecutionOptions()));
    private static async IAsyncEnumerable<StreamChunk> Body()
    {
        await Task.Yield();
        yield return new StreamChunk("body", "stop");
    }
    private sealed class ReplyHandler(bool fail = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => fail ? Task.FromException<HttpResponseMessage>(new HttpRequestException("test transport failure"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
    }
    private sealed class BodyFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new FailingContent() });
    }
    private sealed class FailingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.FromException(new IOException("body connection closed"));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
