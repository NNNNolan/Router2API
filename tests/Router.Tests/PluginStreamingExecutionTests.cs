using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;
using Router.Host.Plugins;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class PluginStreamingExecutionTests : IDisposable
{
    private readonly PluginResiliencePipelines _resilience = new();
    [TestCleanup]
    public void Dispose() => _resilience.Dispose();
    [TestMethod]
    public async Task ClientLeaseAndCancellationLiveUntilTheStreamEnds()
    {
        var account = Account();
        var handler = new TrackingHandler();
        var setup = Executor(account, handler, shortDeadlines: true);
        PluginAttemptContext? captured = null;
        var terminal = new Mock<IPlatformTerminal>();
        terminal.Setup(value => value.InvokeAsync(It.IsAny<PluginAttemptContext>()))
            .Returns(async (PluginAttemptContext context) =>
            {
                captured = context;
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/headers");
                await context.HttpClient.SendAsync(request, false, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken);
                return new PluginInvocationResult(new AdapterResponse
                {
                    StatusCode = 200, IsStreaming = true, Stream = SendAgain(context)
                }, new PluginAttemptResult(PluginAttemptOutcome.Healthy, 200));
            });
        var result = await setup.ExecuteAsync("test", "test", terminal.Object, new AdapterRequest { Model = "m", Stream = true },
            "trace", new TestOverrides { AccountId = account.Id, SkipCooldown = true });
        account.Status.InFlight.Should().Be(1);
        handler.Disposed.Should().BeFalse();
        await Task.Delay(150);
        captured!.CancellationToken.IsCancellationRequested.Should().BeFalse("header/attempt timers must not cut off the body");
        await foreach (var _ in result.Response.Stream!) { }
        handler.Requests.Should().Be(2);
        handler.Disposed.Should().BeTrue();
        account.Status.InFlight.Should().Be(0);
        handler.Contents.Should().AllSatisfy(content => content.Disposed.Should().BeTrue());
    }

    [TestMethod]
    public async Task CancelAfterHeadersStillReachesTheOriginalAttemptToken()
    {
        var account = Account();
        var handler = new TrackingHandler();
        var setup = Executor(account, handler);
        PluginAttemptContext? captured = null;
        var terminal = new Mock<IPlatformTerminal>();
        terminal.Setup(value => value.InvokeAsync(It.IsAny<PluginAttemptContext>()))
            .Returns(async (PluginAttemptContext context) =>
            {
                captured = context;
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
                await context.HttpClient.SendAsync(request, false, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken);
                return new PluginInvocationResult(new AdapterResponse { StatusCode = 200, IsStreaming = true, Stream = EmptyBody() },
                    new PluginAttemptResult(PluginAttemptOutcome.Healthy, 200));
            });
        using var cancellation = new CancellationTokenSource();
        var result = await setup.ExecuteAsync("test", "test", terminal.Object, new AdapterRequest { Model = "m", Stream = true },
            "trace", new TestOverrides { AccountId = account.Id, SkipCooldown = true }, cancellation.Token);
        cancellation.Cancel();
        captured!.CancellationToken.IsCancellationRequested.Should().BeTrue();
        await result.Response.Lifetime!.DisposeAsync();
        account.Status.InFlight.Should().Be(0);
        handler.Contents.Single().Disposed.Should().BeTrue("even an unenumerated response is owned by the native client");
    }

    [TestMethod]
    public async Task MixedCaseSseHeadersAlsoSwitchTheDeadlineForNonStreamingAggregation()
    {
        var account = Account();
        var handler = new TrackingHandler { MediaType = "Text/Event-Stream" };
        var terminal = new Mock<IPlatformTerminal>();
        terminal.Setup(value => value.InvokeAsync(It.IsAny<PluginAttemptContext>())).Returns(async (PluginAttemptContext context) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
            await context.HttpClient.SendAsync(request, false, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken);
            await Task.Delay(150, context.CancellationToken);
            return new PluginInvocationResult(new AdapterResponse { StatusCode = 200, Completion = new AdapterCompletion("m", "ok") },
                new PluginAttemptResult(PluginAttemptOutcome.Healthy));
        });
        var result = await Executor(account, handler, shortDeadlines: true).ExecuteAsync("test", "test", terminal.Object,
            new AdapterRequest { Model = "m" }, "trace", new TestOverrides { AccountId = account.Id, SkipCooldown = true });
        result.Response.IsSuccess.Should().BeTrue();
        handler.Disposed.Should().BeTrue();
        account.Status.InFlight.Should().Be(0);
    }

    [TestMethod]
    public async Task TrackedTerminalDoesNotDrainWhileItsResponseIsUnconsumed()
    {
        var inner = new Mock<IPlatformTerminal>();
        inner.Setup(value => value.InvokeAsync(It.IsAny<PluginAttemptContext>()))
            .ReturnsAsync(new PluginInvocationResult(
                new AdapterResponse { StatusCode = 200, IsStreaming = true, Stream = EmptyBody() },
                new PluginAttemptResult(PluginAttemptOutcome.Healthy, 200)));
        var type = typeof(PluginCatalog).GetNestedType("TrackedTerminal", BindingFlags.NonPublic)!;
        var tracked = (IPlatformTerminal)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [inner.Object, Array.Empty<ScheduledTaskRegistration>()], culture: null)!;
        var result = await tracked.InvokeAsync(new PluginAttemptContext
        {
            PluginKey = "test", PlatformName = "test", Account = Account(), Request = new AdapterRequest(),
            HttpClient = Mock.Of<IPluginHttpClient>(), CancellationToken = CancellationToken.None
        });
        ((int)type.GetProperty("InFlight")!.GetValue(tracked)!).Should().Be(1);
        var wait = (Task)type.GetMethod("WaitForDrainAsync")!.Invoke(tracked, [TimeSpan.FromMilliseconds(30), CancellationToken.None])!;
        Func<Task> draining = () => wait;
        await draining.Should().ThrowAsync<OperationCanceledException>();
        await result.Response.Lifetime!.DisposeAsync();
        ((int)type.GetProperty("InFlight")!.GetValue(tracked)!).Should().Be(0);
        await (Task)type.GetMethod("WaitForDrainAsync")!.Invoke(tracked, [TimeSpan.FromMilliseconds(30), CancellationToken.None])!;
    }

    private PluginAttemptExecutor Executor(Account account, TrackingHandler handler, bool shortDeadlines = false)
    {
        var accounts = new Mock<IAccountService>();
        accounts.Setup(value => value.GetAsync("test", account.Id, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        var factory = new Mock<IProxyHttpClientFactory>();
        factory.Setup(value => value.CreateDirectClient("upstream")).Returns(() => new HttpClient(handler));
        var policies = new PluginPolicyRegistry();
        policies.Set("test", PluginProxyPolicy.Default with
        {
            MaxAttempts = 1,
            AttemptTimeout = shortDeadlines ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(60),
            TotalTimeout = shortDeadlines ? TimeSpan.FromMilliseconds(80) : TimeSpan.FromSeconds(180)
        });
        return new PluginAttemptExecutor(accounts.Object, Mock.Of<IProxyStore>(), Mock.Of<IProxySubscriptionService>(),
            new ResourceLeaseManager(), Mock.Of<IProxyPolicyStore>(), Mock.Of<ISharedKeyValueStore>(), factory.Object,
            policies, Mock.Of<IModelMetadataCatalog>(), Mock.Of<IPluginLogSink>(), NullLogger<PluginAttemptExecutor>.Instance,
            _resilience, Microsoft.Extensions.Options.Options.Create(new PluginExecutionOptions()));
    }

    private static Account Account() => new() { Id = "account", PluginKey = "test", Platform = "test" };

    private static async IAsyncEnumerable<StreamChunk> SendAgain(
        PluginAttemptContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/body");
        using var response = await context.HttpClient.SendAsync(request, false, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        yield return new StreamChunk("ok");
        yield return new StreamChunk(null, "stop");
    }

    private static async IAsyncEnumerable<StreamChunk> EmptyBody()
    {
        await Task.CompletedTask;
        yield return new StreamChunk("body");
        yield return new StreamChunk(null, "stop");
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        public int Requests { get; private set; }
        public List<TrackingContent> Contents { get; } = [];
        public string MediaType { get; init; } = "text/event-stream";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            var content = new TrackingContent();
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(MediaType);
            Contents.Add(content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }

    private sealed class TrackingContent : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
}
