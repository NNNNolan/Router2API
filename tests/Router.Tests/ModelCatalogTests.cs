using System.Reflection;
using System.Text.Json;
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Host.Api;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class ModelCatalogTests
{
    [TestMethod]
    public async Task SynchronousPluginCannotBlockTheCallerOrAccumulateWorkersAfterTimeout()
    {
        using var transport = new ProxyTransportFactory();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource<Task<IReadOnlyList<ModelDescriptor>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new PlatformRegistry();
        var calls = 0;
        HttpClient? client = null;
        var terminal = new Mock<IPlatformTerminal>();
        terminal.Setup(value => value.GetModelsAsync(It.IsAny<ModelQueryContext>(), It.IsAny<CancellationToken>()))
            .Returns((ModelQueryContext context, CancellationToken _) =>
            {
                Interlocked.Increment(ref calls);
                client = context.HttpClient;
                entered.TrySetResult();
                // 复现发生在返回 Task 之前的同步数据库/缓存等待，而非普通异步延迟。
                // release.Wait(TimeSpan.FromSeconds(10));
                finished.TrySetResult();
                return Task.FromResult<IReadOnlyList<ModelDescriptor>>([new("late", "Late")]);
            });
        registry.Register(new("blocked", "blocked", "Blocked", terminal.Object));
        registry.Register(new("healthy", "healthy", "Healthy", HealthyTerminal().Object));
        var (clock, timeouts) = ControlledClock();
        var catalog = new ModelCatalog(registry, transport, timeProvider: clock);
        var invoking = Task.Run(() => returned.TrySetResult(catalog.ListAsync()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = await returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // 等健康查询完成后再触发阻塞平台的超时，不能误取消正常请求。
            await WaitForAsync(() => timeouts.Count == 2);
            timeouts.ToArray()[0]();
            (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Single().Id.Should().Be("healthy/model");
            client!.CancelPendingRequests(); // 超时后插件尚未退出，客户端仍由实际调用持有。

            var retry = catalog.RefreshAsync("blocked");
            await WaitForAsync(() => timeouts.Count == 3);
            timeouts.ToArray()[2]();
            Func<Task> wait = () => retry.WaitAsync(TimeSpan.FromSeconds(5));
            await wait.Should().ThrowAsync<TimeoutException>();
            calls.Should().Be(1, "前一次调用未退出时不能因反复刷新而堆积阻塞线程");
        }
        finally
        {
            release.Set();
            await invoking;
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task FailingPluginDoesNotHideHealthyModelsOrQueryDisabledPlugins()
    {
        using var transport = new ProxyTransportFactory();
        var registry = new PlatformRegistry();
        var failed = new Mock<IPlatformTerminal>();
        failed.Setup(value => value.GetModelsAsync(It.IsAny<ModelQueryContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("模型接口不可用"));
        var healthy = HealthyTerminal();
        var disabled = new Mock<IPlatformTerminal>(MockBehavior.Strict);
        registry.Register(new("failed", "failed", "Failed", failed.Object));
        registry.Register(new("healthy", "healthy", "Healthy", healthy.Object));
        registry.Register(new("disabled", "disabled", "Disabled", disabled.Object) { Enabled = false });

        var models = await new ModelCatalog(registry, transport).ListAsync();

        models.Select(model => model.Id).Should().Equal("healthy/model");
        disabled.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task EachPluginHasA45SecondBudgetEvenWhenItIgnoresCancellation()
    {
        using var transport = new ProxyTransportFactory();
        var registry = new PlatformRegistry();
        var pending = new TaskCompletionSource<IReadOnlyList<ModelDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new ConcurrentBag<CancellationToken>();
        foreach (var name in new[] { "slow-one", "slow-two" })
        {
            var terminal = new Mock<IPlatformTerminal>();
            terminal.Setup(value => value.GetModelsAsync(It.IsAny<ModelQueryContext>(), It.IsAny<CancellationToken>()))
                .Returns((ModelQueryContext _, CancellationToken token) => { tokens.Add(token); return pending.Task; });
            registry.Register(new(name, name, name, terminal.Object));
        }
        registry.Register(new("healthy", "healthy", "Healthy", HealthyTerminal().Object));
        var (clock, timeouts) = ControlledClock();
        var catalog = new ModelCatalog(registry, transport, timeProvider: clock);
        try
        {
            var result = catalog.ListAsync();
            await WaitForAsync(() => tokens.Count == 2);
            tokens.Should().HaveCount(2, "慢插件应并行查询，不应串行累计超时时间");
            timeouts.Should().HaveCount(3);
            foreach (var fire in timeouts.Skip(1)) fire(); // 平台按名称排序，健康平台不触发超时。

            (await result.WaitAsync(TimeSpan.FromSeconds(5))).Select(model => model.Id).Should().Equal("healthy/model");
            tokens.Should().OnlyContain(token => token.IsCancellationRequested);
            pending.Task.IsCompleted.Should().BeFalse("忽略取消的插件不能阻止模型广场返回");
        }
        finally { pending.TrySetResult([]); }
    }

    [TestMethod]
    public async Task CallerCancellationIsNotSwallowedAsAPartialSuccess()
    {
        using var transport = new ProxyTransportFactory();
        using var cancellation = new CancellationTokenSource();
        var registry = new PlatformRegistry();
        var terminal = new Mock<IPlatformTerminal>();
        var receivedToken = CancellationToken.None;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.Setup(value => value.GetModelsAsync(It.IsAny<ModelQueryContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (ModelQueryContext _, CancellationToken token) =>
            {
                receivedToken = token;
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Array.Empty<ModelDescriptor>();
            });
        registry.Register(new("slow", "slow", "Slow", terminal.Object));
        var catalog = new ModelCatalog(registry, transport);
        var pending = catalog.ListAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Func<Task> wait = () => pending;
        await wait.Should().ThrowAsync<OperationCanceledException>();
        receivedToken.IsCancellationRequested.Should().BeTrue();
    }

    [TestMethod]
    public async Task TimedOutResultCannotFillTheCacheAfterTheCallerHasReturned()
    {
        using var transport = new ProxyTransportFactory();
        var registry = new PlatformRegistry();
        var late = new TaskCompletionSource<IReadOnlyList<ModelDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new Mock<IPlatformTerminal>();
        terminal.SetupSequence(value => value.GetModelsAsync(It.IsAny<ModelQueryContext>(), It.IsAny<CancellationToken>()))
            .Returns(() => { entered.TrySetResult(); return late.Task; })
            .ReturnsAsync(new[] { new ModelDescriptor("fresh", "Fresh") });
        registry.Register(new("slow", "slow", "Slow", terminal.Object));
        var (clock, timeouts) = ControlledClock();
        var catalog = new ModelCatalog(registry, transport, timeProvider: clock);
        var pending = catalog.ListAsync("slow");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            timeouts.Single()();
            Func<Task> wait = () => pending.WaitAsync(TimeSpan.FromSeconds(5));
            await wait.Should().ThrowAsync<TimeoutException>().WithMessage("*45*");
        }
        finally { late.TrySetResult([new("stale", "Stale")]); }

        (await catalog.ListAsync("slow")).Single().Id.Should().Be("fresh");
        (await catalog.ListAsync("slow")).Single().Id.Should().Be("fresh");
        terminal.Verify(value => value.GetModelsAsync(It.IsAny<ModelQueryContext>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlazaRefreshUsesEachRefreshResultWithoutQueryingPluginsAgain(bool timesOut)
    {
        using var transport = new ProxyTransportFactory();
        var registry = new PlatformRegistry();
        var healthy = HealthyTerminal();
        var failed = new Mock<IPlatformTerminal>();
        var late = new TaskCompletionSource<IReadOnlyList<ModelDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        failed.Setup(value => value.GetModelsAsync(It.IsAny<ModelQueryContext>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                entered.TrySetResult();
                return timesOut ? late.Task : Task.FromException<IReadOnlyList<ModelDescriptor>>(new HttpRequestException("模型接口不可用"));
            });
        registry.Register(new("healthy", "healthy", "Healthy", healthy.Object) { ModelCacheTtl = TimeSpan.Zero });
        registry.Register(new("failed", "failed", "Failed", failed.Object) { ModelCacheTtl = TimeSpan.Zero });
        var metadata = new Mock<IModelMetadataCatalog>();
        metadata.Setup(value => value.GetAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelMetadataSnapshot(DateTimeOffset.MinValue, []));
        var method = typeof(ApiEndpoints).GetMethod("RefreshModelPlazaAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var (clock, timeouts) = ControlledClock();
        var pending = (Task<IResult>)method.Invoke(null,
            [new ModelCatalog(registry, transport, timeProvider: clock), metadata.Object, registry, CancellationToken.None])!;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (timesOut) timeouts.First()(); // 只让 failed 平台超时，不取消健康平台。
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

            var json = JsonSerializer.SerializeToNode(((IValueHttpResult)result).Value, JsonSerializerOptions.Web)!;
            json["models"]!.AsArray().Single()!["id"]!.GetValue<string>().Should().Be("healthy/model");
            healthy.Verify(value => value.GetModelsAsync(It.Is<ModelQueryContext>(context => context.ForceRefresh), It.IsAny<CancellationToken>()), Times.Once);
            healthy.Verify(value => value.GetModelsAsync(It.IsAny<ModelQueryContext>(), It.IsAny<CancellationToken>()), Times.Once);
            failed.Verify(value => value.GetModelsAsync(It.IsAny<ModelQueryContext>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { late.TrySetResult([]); }
    }

    private static Mock<IPlatformTerminal> HealthyTerminal()
    {
        var terminal = new Mock<IPlatformTerminal>();
        terminal.Setup(value => value.GetModelsAsync(It.IsAny<ModelQueryContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ModelDescriptor("model", "Model") });
        return terminal;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static (TimeProvider Clock, ConcurrentQueue<Action> Timeouts) ControlledClock()
    {
        var timeouts = new ConcurrentQueue<Action>();
        var clock = new Mock<TimeProvider>();
        clock.Setup(value => value.CreateTimer(It.IsAny<TimerCallback>(), It.IsAny<object?>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>()))
            .Returns((TimerCallback callback, object? state, TimeSpan due, TimeSpan period) =>
            {
                due.Should().Be(TimeSpan.FromSeconds(45));
                period.Should().Be(Timeout.InfiniteTimeSpan);
                var disposed = false;
                var timer = new Mock<ITimer>();
                timer.Setup(value => value.Dispose()).Callback(() => disposed = true);
                timeouts.Enqueue(() => { if (!disposed) callback(state); });
                return timer.Object;
            });
        return (clock.Object, timeouts);
    }
}
