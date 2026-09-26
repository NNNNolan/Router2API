using FluentAssertions;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;
using Router.Host.Plugins;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class PluginLoadingTests
{
    [TestMethod]
    public async Task FailedStartStopsEnteredHooksAndDisposesUnstartedTerminalsOnce()
    {
        var first = new LifecycleTerminal();
        var failed = new LifecycleTerminal { FailStart = true };
        var unstarted = new LifecycleTerminal();
        var released = 0;
        var platforms = new[] { first, failed, unstarted }.Select((terminal, index) => new LoadedPlatform(
            "p" + index, "test", "Test", null, terminal, PluginTestHost.Create("test"), new PluginBuilder(), [], TimeSpan.Zero)).ToArray();
        var package = new LoadedPlugin("test", "1", "test-runtime", platforms, [], null, () => released++);
        Func<Task> start = () => package.StartAsync(CancellationToken.None);
        await start.Should().ThrowAsync<InvalidOperationException>();
        await package.DisposeAsync();
        await package.DisposeAsync();
        first.Stops.Should().Be(1);
        failed.Stops.Should().Be(1);
        unstarted.Stops.Should().Be(0);
        unstarted.Disposals.Should().Be(1);
        first.Disposals.Should().Be(0, "legacy StopAsync owns cleanup and must not be followed by a duplicate Dispose");
        released.Should().Be(1);
    }

    [TestMethod]
    public async Task ModelCacheHonorsPackageTtlAndDisposesDiscoveryClients()
    {
        using var transport = new ProxyTransportFactory();
        var registry = new PlatformRegistry();
        var terminal = new Mock<IPlatformTerminal>();
        var calls = 0;
        HttpClient? captured = null;
        terminal.Setup(value => value.GetModelsAsync(It.IsAny<ModelQueryContext>(), It.IsAny<CancellationToken>()))
            .Returns((ModelQueryContext context, CancellationToken _) =>
            {
                calls++;
                captured = context.HttpClient;
                return Task.FromResult<IReadOnlyList<ModelDescriptor>>([new("m", "Model")]);
            });
        registry.Register(new PlatformRegistration("test", "test", "Test", terminal.Object) { ModelCacheTtl = TimeSpan.FromMinutes(1) });
        var models = new ModelCatalog(registry, transport);
        await models.ListAsync("test");
        await models.ListAsync("test");
        calls.Should().Be(1);
        Func<Task> useDisposedClient = () => captured!.GetAsync("http://localhost/");
        await useDisposedClient.Should().ThrowAsync<ObjectDisposedException>();
        models.Invalidate("test");
        await models.ListAsync("test");
        calls.Should().Be(2);
        registry.Register(registry.Get("test")! with { ModelCacheTtl = TimeSpan.Zero });
        await models.ListAsync("test");
        await models.ListAsync("test");
        calls.Should().Be(4);
    }

    [TestMethod]
    public async Task InvalidatedOrOldGenerationModelFetchCannotOverwriteANewCache()
    {
        using var transport = new ProxyTransportFactory();
        var registry = new PlatformRegistry();
        var old = new Mock<IPlatformTerminal>();
        var oldResult = new TaskCompletionSource<IReadOnlyList<ModelDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        old.Setup(value => value.GetModelsAsync(It.IsAny<ModelQueryContext>(), It.IsAny<CancellationToken>())).Returns(oldResult.Task);
        var current = new Mock<IPlatformTerminal>();
        current.Setup(value => value.GetModelsAsync(It.IsAny<ModelQueryContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ModelDescriptor("new", "New") });
        registry.Register(new PlatformRegistration("test", "test", "Test", old.Object));
        var models = new ModelCatalog(registry, transport);
        var pending = models.ListAsync("test");
        models.Invalidate("test");
        registry.Register(new PlatformRegistration("test", "test", "Test", current.Object));
        (await models.ListAsync("test")).Single().Id.Should().Be("new");
        oldResult.SetResult([new("old", "Old")]);
        await pending;
        (await models.ListAsync("test")).Single().Id.Should().Be("new");
        current.Verify(value => value.GetModelsAsync(It.IsAny<ModelQueryContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class LifecycleTerminal : IPlatformTerminal, IPluginModule, IDisposable
    {
        public bool FailStart { get; init; }
        public int Stops { get; private set; }
        public int Disposals { get; private set; }
        public void Configure(IPluginBuilder builder) { }
        public ValueTask StartAsync(PluginStartContext context, CancellationToken cancellationToken)
            => FailStart ? ValueTask.FromException(new InvalidOperationException("start failed")) : ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken cancellationToken) { Stops++; return ValueTask.CompletedTask; }
        public void Dispose() => Disposals++;
        public Task<PluginInvocationResult> InvokeAsync(PluginAttemptContext context) => throw new NotSupportedException();
        public Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(ModelQueryContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CredentialValidationResult> ValidateCredentialAsync(Credential credential, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
