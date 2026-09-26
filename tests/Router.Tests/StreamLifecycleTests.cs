using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Host.Api;
using Router.Host.Pipeline;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class StreamLifecycleTests
{
    [TestMethod]
    public async Task NaturalCompletionReleasesOnceAndRecordsLastUsage()
    {
        var response = Response(Chunks());
        var lifetime = (PluginResponseLifetime)response.Lifetime!;
        var released = 0;
        PluginStreamCompletion? finished = null;
        lifetime.AddResource(() => { released++; return ValueTask.CompletedTask; });
        lifetime.OnCompleted(value => { finished = value; return ValueTask.CompletedTask; });
        await foreach (var _ in response.Stream!) { }
        await lifetime.DisposeAsync();
        released.Should().Be(1);
        finished!.Success.Should().BeTrue();
        finished.Usage.Should().Be(new Usage(2, 3, 5));
    }

    [TestMethod]
    public async Task EarlyBreakIsNotReportedAsSuccess()
    {
        var response = Response(Chunks());
        PluginStreamCompletion? finished = null;
        ((PluginResponseLifetime)response.Lifetime!).OnCompleted(value => { finished = value; return ValueTask.CompletedTask; });
        await foreach (var _ in response.Stream!) break;
        finished!.Success.Should().BeFalse();
        finished.Error.Should().Contain("not fully consumed");
    }

    [TestMethod]
    public async Task UnconsumedResponseStillReleasesExplicitOwnership()
    {
        var response = Response(Chunks());
        var released = false;
        ((PluginResponseLifetime)response.Lifetime!).AddResource(() => { released = true; return ValueTask.CompletedTask; });
        await response.Lifetime!.DisposeAsync();
        released.Should().BeTrue();
    }

    [TestMethod]
    public async Task ConcurrentDisposeWaitsForTheSameCleanup()
    {
        var response = Response(Chunks());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = 0;
        ((PluginResponseLifetime)response.Lifetime!).AddResource(async () =>
        {
            releases++;
            entered.SetResult();
            await finish.Task;
        });
        var first = response.Lifetime!.DisposeAsync().AsTask();
        await entered.Task;
        var second = response.Lifetime.DisposeAsync().AsTask();
        second.IsCompleted.Should().BeFalse();
        finish.SetResult();
        await Task.WhenAll(first, second);
        releases.Should().Be(1);
    }

    [TestMethod]
    public async Task SourceFailureBecomesAStreamErrorNotSilentSuccess()
    {
        var response = Response(Broken());
        var chunks = new List<StreamChunk>();
        PluginStreamCompletion? finished = null;
        ((PluginResponseLifetime)response.Lifetime!).OnCompleted(value => { finished = value; return ValueTask.CompletedTask; });
        await foreach (var chunk in response.Stream!) chunks.Add(chunk);
        chunks.Last().ErrorType.Should().Be("upstream_stream_error");
        finished!.Success.Should().BeFalse();
    }

    [TestMethod]
    public async Task WriterOwnsTheFinalDoneFrameAndReleasesOnWriteFailure()
    {
        var response = Response(Chunks());
        var context = new DefaultHttpContext();
        await using var body = new FailOnDoneStream();
        context.Response.Body = body;
        var released = 0;
        PluginStreamCompletion? finished = null;
        var lifetime = (PluginResponseLifetime)response.Lifetime!;
        lifetime.AddResource(() => { released++; return ValueTask.CompletedTask; });
        lifetime.OnCompleted(value => { finished = value; return ValueTask.CompletedTask; });
        Func<Task> write = () => ProtocolResponseWriter.WriteAsync(context, response, TestEndpoint.ChatCompletions, "test", CancellationToken.None);
        await write.Should().ThrowAsync<IOException>();
        released.Should().Be(1);
        finished!.Success.Should().BeFalse();
        finished.Error.Should().Be("response write failed");
    }

    [TestMethod]
    public async Task HttpErrorBranchReleasesAStreamWithoutEnumeratingIt()
    {
        var response = PluginResponseLifetime.Ensure(new AdapterResponse
        {
            StatusCode = 502, IsStreaming = true, Stream = Chunks(), Error = "upstream failed"
        });
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var released = false;
        ((PluginResponseLifetime)response.Lifetime!).AddResource(() => { released = true; return ValueTask.CompletedTask; });
        await ProtocolResponseWriter.WriteAsync(context, response, TestEndpoint.ChatCompletions, "test", CancellationToken.None);
        released.Should().BeTrue();
        context.Response.StatusCode.Should().Be(502);
    }

    [TestMethod]
    public async Task CleanupFailureDoesNotSkipRemainingResources()
    {
        var response = Response(Chunks());
        var lifetime = (PluginResponseLifetime)response.Lifetime!;
        var second = false;
        lifetime.AddResource(() => throw new IOException("cleanup failed"));
        lifetime.AddResource(() => { second = true; return ValueTask.CompletedTask; });
        Func<Task> dispose = () => lifetime.DisposeAsync().AsTask();
        await dispose.Should().ThrowAsync<AggregateException>();
        second.Should().BeTrue();
    }

    [TestMethod]
    public async Task PipelineDefersMetricsAndUsageUntilTheStreamIsFinished()
    {
        var platforms = new PlatformRegistry();
        var terminal = Mock.Of<IPlatformTerminal>();
        platforms.Register(new PlatformRegistration("test", "test", "Test", terminal));
        var executor = new Mock<IPluginAttemptExecutor>();
        executor.Setup(value => value.ExecuteAsync("test", "test", terminal, It.IsAny<AdapterRequest>(),
                It.IsAny<string>(), It.IsAny<TestOverrides>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PluginInvocationResult(Response(Chunks()), new PluginAttemptResult(PluginAttemptOutcome.Healthy, 200)));
        var metrics = new RealtimeMetrics();
        var logs = new Mock<ILogSink<RequestLog>>();
        var pipeline = new RouterPipeline(new ModelRouter(platforms), platforms, executor.Object, metrics,
            logs.Object, Mock.Of<IUsageBucketStore>(), NullLogger<RouterPipeline>.Instance);
        var context = new RequestContext { Request = new AdapterRequest { Model = "test/model", Stream = true } };

        await pipeline.ExecuteAsync(context);
        metrics.Snapshot().ActiveConnections.Should().Be(1);
        metrics.Snapshot().ActiveStreaming.Should().Be(1);
        metrics.Snapshot().RequestsToday.Should().Be(0);
        await foreach (var _ in context.Response!.Stream!) { }
        metrics.Snapshot().ActiveConnections.Should().Be(0);
        metrics.Snapshot().ActiveStreaming.Should().Be(0);
        metrics.Snapshot().TokensToday.Should().Be(5);
        logs.Verify(value => value.WriteAsync(It.Is<RequestLog>(log => log.Success && log.Usage!.TotalTokens == 5),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("ChatCompletions", "reasoning_content", "[DONE]")]
    [DataRow("Responses", "response.reasoning_summary_text.delta", "response.completed")]
    [DataRow("AnthropicMessages", "thinking_delta", "message_stop")]
    public async Task StandardWritersKeepHeartbeatReasoningAndFinalMarker(string endpointName, string reasoningMarker, string finalMarker)
    {
        var endpoint = Enum.Parse<TestEndpoint>(endpointName);
        var response = Response(WithHeartbeat());
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await ProtocolResponseWriter.WriteAsync(context, response, endpoint, "test", CancellationToken.None);
        var text = Encoding.UTF8.GetString(body.ToArray());
        text.Should().Contain(": keep-alive").And.Contain(reasoningMarker).And.Contain(finalMarker);
        if (endpoint == TestEndpoint.ChatCompletions) text.Split("[DONE]").Should().HaveCount(2);
    }

    private static AdapterResponse Response(IAsyncEnumerable<StreamChunk> stream)
        => PluginResponseLifetime.Ensure(new AdapterResponse { StatusCode = 200, IsStreaming = true, Stream = stream });

    private static async IAsyncEnumerable<StreamChunk> Chunks([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield return new StreamChunk("answer");
        yield return new StreamChunk(null, Usage: new Usage(2, 3, 5));
        yield return new StreamChunk(null, "stop");
    }

    private static async IAsyncEnumerable<StreamChunk> Broken()
    {
        await Task.CompletedTask;
        yield return new StreamChunk("partial");
        throw new IOException("not for downstream");
    }

    private static async IAsyncEnumerable<StreamChunk> WithHeartbeat()
    {
        yield return default;
        yield return new StreamChunk(null, ReasoningDelta: "think");
        await foreach (var chunk in Chunks()) yield return chunk;
    }

    private sealed class FailOnDoneStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Encoding.UTF8.GetString(buffer.Span).Contains("[DONE]", StringComparison.Ordinal))
                throw new IOException("client disappeared at the final frame");
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
