using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class PluginCancellationTests : IDisposable
{
    private readonly PluginResiliencePipelines _resilience = new();

    [TestCleanup]
    public void Dispose() => _resilience.Dispose();

    [TestMethod]
    public async Task ClientCancellationKeepsCollectedAttemptsAndWritesLog()
    {
        var account = new Account { Id = "account", PluginKey = "test", Platform = "test" };
        var logs = new List<PluginLog>();
        var sink = new Mock<IPluginLogSink>();
        sink.Setup(value => value.WriteAsync(It.IsAny<PluginLog>(), It.IsAny<CancellationToken>()))
            .Callback<PluginLog, CancellationToken>((log, _) => logs.Add(log))
            .Returns(Task.CompletedTask);
        var executor = Executor(account, sink.Object);

        var attempts = 0;
        var secondAttemptStarted = new TaskCompletionSource();
        var terminal = new Mock<IPlatformTerminal>();
        terminal.Setup(value => value.InvokeAsync(It.IsAny<PluginAttemptContext>()))
            .Returns(async (PluginAttemptContext context) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    return new PluginInvocationResult(
                        new AdapterResponse { StatusCode = 502, Error = "upstream failed" },
                        new PluginAttemptResult(PluginAttemptOutcome.Retry, 502, Reason: "upstream failed")
                        {
                            Decision = new PluginAttemptDecision
                            {
                                FailureKind = PluginFailureKind.Upstream,
                                Retry = PluginRetryAction.NextAttempt,
                                ReasonCode = "test-retry"
                            }
                        });
                }

                secondAttemptStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
                throw new UnreachableException();
            });

        using var cancellation = new CancellationTokenSource();
        var execution = executor.ExecuteAsync("test", "test", terminal.Object, new AdapterRequest { Model = "m" },
            "trace", new TestOverrides { AccountId = account.Id, SkipCooldown = true }, cancellation.Token);

        await secondAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        var act = async () => await execution;
        var thrown = (await act.Should().ThrowAsync<PluginRequestCancelledException>()).Which;
        thrown.AttemptDetails.Should().HaveCount(1, "the attempt collected before the caller cancelled must survive");
        thrown.AttemptDetails[0].StatusCode.Should().Be(502);
        logs.Should().ContainSingle(log => log.EventType == "request.cancelled");
        logs.Single(log => log.EventType == "request.cancelled").TraceId.Should().Be("trace");
    }

    private PluginAttemptExecutor Executor(Account account, IPluginLogSink sink)
    {
        var accounts = new Mock<IAccountService>();
        accounts.Setup(value => value.GetAsync("test", account.Id, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        var factory = new Mock<IProxyHttpClientFactory>();
        factory.Setup(value => value.CreateDirectClient("upstream")).Returns(() => new HttpClient());
        var policies = new PluginPolicyRegistry();
        policies.Set("test", PluginProxyPolicy.Default with
        {
            MaxAttempts = 3,
            AttemptTimeout = TimeSpan.FromSeconds(60),
            TotalTimeout = TimeSpan.FromSeconds(180)
        });
        return new PluginAttemptExecutor(accounts.Object, Mock.Of<IProxyStore>(), Mock.Of<IProxySubscriptionService>(),
            new ResourceLeaseManager(), Mock.Of<IProxyPolicyStore>(), Mock.Of<ISharedKeyValueStore>(), factory.Object,
            policies, Mock.Of<IModelMetadataCatalog>(), sink, NullLogger<PluginAttemptExecutor>.Instance,
            _resilience, Microsoft.Extensions.Options.Options.Create(new PluginExecutionOptions()));
    }
}
