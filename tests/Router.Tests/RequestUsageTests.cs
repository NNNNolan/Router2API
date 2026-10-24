using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Host.Api;
using Router.Host.Pipeline;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class RequestUsageTests
{
    [TestMethod]
    public void ExplicitZeroUsageWinsOverRawResponseAndEstimation()
    {
        var tracker = new RequestUsageTracker(Request(), Raw("""{"usage":{"prompt_tokens":10,"completion_tokens":20,"total_tokens":30}}""", new Usage(0, 0, 0)));
        var result = tracker.Finish(true);
        result.Usage.Should().Be(new Usage(0, 0, 0));
        result.Metadata.Source.Should().Be("plugin");
        result.Metadata.IsPartial.Should().BeFalse();
        tracker.Finish(false).Should().BeSameAs(result);
    }

    [TestMethod]
    [DataRow("""{"choices":[{"message":{"content":"answer"}}],"usage":{"prompt_tokens":9,"completion_tokens":4,"total_tokens":13}}""")]
    [DataRow("""{"output":[],"usage":{"input_tokens":9,"output_tokens":4,"total_tokens":13}}""")]
    [DataRow("""{"content":[],"usage":{"input_tokens":2,"cache_read_input_tokens":3,"cache_creation_input_tokens":4,"output_tokens":4}}""")]
    [DataRow("""{"usageMetadata":{"promptTokenCount":9,"candidatesTokenCount":3,"thoughtsTokenCount":1,"totalTokenCount":13}}""")]
    public void RawProtocolsPreserveProviderUsage(string json)
    {
        var result = new RequestUsageTracker(Request(), Raw(json)).Finish(true);
        result.Usage.Should().Be(new Usage(9, 4, 13));
        result.Metadata.Source.Should().Be("response");
        result.Metadata.IsPartial.Should().BeFalse();
    }

    [TestMethod]
    public void AnthropicSnapshotsMergeFieldsAndDoNotAddCumulativeOutput()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "event: message_start\r\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":10,\"output_tokens\":1}}}\r\n\r\n"
            + "data: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"中😀\"}}\n\n"
            + "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":3}}\n\n"
            + "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":7}}\n\n"
            + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        foreach (var size in new[] { 1, 7, bytes.Length })
        {
            var tracker = new RequestUsageTracker(Request(), new AdapterResponse { StatusCode = 200, RawStream = EmptyRaw(), IsRawPassthrough = true });
            for (var offset = 0; offset < bytes.Length; offset += size)
                tracker.ObserveRaw(bytes.AsSpan(offset, Math.Min(size, bytes.Length - offset)));
            var result = tracker.Finish(true);
            result.Usage.Should().Be(new Usage(10, 7, 17));
            result.Metadata.IsPartial.Should().BeFalse();
        }
    }

    [TestMethod]
    public void ResponsesCompletionEventIsReadWithoutRecountingItsFullOutput()
    {
        var tracker = new RequestUsageTracker(Request(), new AdapterResponse { StatusCode = 200, RawStream = EmptyRaw(), IsRawPassthrough = true });
        tracker.ObserveRaw(Encoding.UTF8.GetBytes(
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"answer\"}\n\n"
            + "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":5,\"output_tokens\":6,\"total_tokens\":11},\"output\":[]}}\n\n"));
        tracker.Finish(true).Usage.Should().Be(new Usage(5, 6, 11));
    }

    [TestMethod]
    [DataRow("{\"usage\":{\"input_tokens\":2,\"cache_read_input_tokens\":3,\"output_tokens\":1}}", "{\"usage\":{\"input_tokens\":2,\"cache_creation_input_tokens\":4,\"output_tokens\":4}}")]
    [DataRow("{\"usageMetadata\":{\"promptTokenCount\":9,\"candidatesTokenCount\":3}}", "{\"usageMetadata\":{\"thoughtsTokenCount\":1}}")]
    public void IndependentlyReportedFieldsSurviveRepeatedAndPartialSnapshots(string first, string last)
    {
        var tracker = RawTracker();
        foreach (var json in new[] { first, last, last })
            tracker.ObserveRaw(Encoding.UTF8.GetBytes($"data: {json}\n\n"));
        var result = tracker.Finish(true);
        result.Usage.Should().Be(new Usage(9, 4, 13));
        result.Metadata.Source.Should().Be("response");
        result.Metadata.IsPartial.Should().BeFalse();
    }

    [TestMethod]
    public void ExplicitTotalsAndZeroSnapshotsAreNotRecomputedOrAdded()
    {
        var tracker = RawTracker();
        tracker.ObserveRaw("data: {\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4,\"total_tokens\":99}}\n\n"u8);
        tracker.ObserveRaw("data: {\"usage\":{\"completion_tokens\":0}}\n\n"u8);
        tracker.Finish(true).Usage.Should().Be(new Usage(3, 0, 99));
        var totalOnly = new RequestUsageTracker(Request(), Raw("{\"usage\":{\"total_tokens\":99}}")).Finish(true);
        totalOnly.Usage!.TotalTokens.Should().Be(99);
        totalOnly.Metadata.Source.Should().Be("mixed");
        totalOnly.Metadata.TotalSource.Should().Be("response");
    }

    [TestMethod]
    [DataRow("{\"choices\":[{\"message\":{\"content\":\"answer\",\"reasoning_content\":\"think\",\"tool_calls\":[{\"function\":{\"name\":\"weather\",\"arguments\":\"{city:Shanghai}\"}}]}}]}")]
    [DataRow("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"answer\"}]},{\"type\":\"reasoning\",\"summary\":[{\"text\":\"think\"}]},{\"type\":\"function_call\",\"name\":\"weather\",\"arguments\":\"{city:Shanghai}\"}]}")]
    [DataRow("{\"content\":[{\"type\":\"text\",\"text\":\"answer\"},{\"type\":\"thinking\",\"thinking\":\"think\"},{\"type\":\"tool_use\",\"name\":\"weather\",\"input\":{\"city\":\"Shanghai\"}}]}")]
    [DataRow("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"answer\"},{\"thought\":true,\"text\":\"think\"},{\"functionCall\":{\"name\":\"weather\",\"args\":{\"city\":\"Shanghai\"}}}]}}]}")]
    public void RawTextReasoningAndToolArgumentsAreEstimated(string json)
    {
        var result = new RequestUsageTracker(Request(), Raw(json)).Finish(true);
        var baseline = new RequestUsageTracker(Request(), Raw("{\"choices\":[{\"text\":\"answer\"}]}")).Finish(true);
        result.Usage!.CompletionTokens.Should().BeGreaterThan(baseline.Usage!.CompletionTokens);
        result.Metadata.Should().Match<UsageMetadata>(value => value.Source == "estimated" && !value.IsPartial && value.Algorithm == "unicode-v1");
    }

    [TestMethod]
    public void ResponsesFinalSnapshotDoesNotDuplicateEstimatedDeltas()
    {
        var tracker = RawTracker();
        tracker.ObserveRaw("data: {\"type\":\"response.output_text.delta\",\"delta\":\"answer\"}\n\n"u8);
        tracker.ObserveRaw("data: {\"type\":\"response.completed\",\"response\":{\"output\":[{\"content\":[{\"text\":\"answer\"}]}]}}\n\n"u8);
        var estimate = new UnicodeTokenEstimate();
        estimate.Add("answer");
        tracker.Finish(true).Usage!.CompletionTokens.Should().Be(estimate.Count);
    }

    [TestMethod]
    public void InvalidAndTruncatedRawObservationIsVisibleWithoutChangingTheStream()
    {
        foreach (var bytes in new[] { "data: invalid\n\n"u8.ToArray(), "data: {}"u8.ToArray(), new byte[] { 0xff }, "data: \""u8.ToArray().Concat(new byte[] { 0xf0 }).ToArray() })
        {
            var tracker = RawTracker();
            tracker.ObserveRaw(bytes);
            var result = tracker.Finish(true);
            result.Metadata.IsPartial.Should().BeTrue();
            result.Metadata.Warning.Should().NotBeNullOrEmpty();
        }
    }

    [TestMethod]
    public void TextFallbackUsesLocalTokenizerAndCountsToolsAndReasoning()
    {
        var request = new AdapterRequest { Model = "gpt-4o", Messages = [new("user", "Hello world")] };
        var basic = new RequestUsageTracker(request,
            new AdapterResponse { StatusCode = 200, Completion = new("gpt-4o", "Hello world") }).Finish(true);
        basic.Usage.Should().Be(new Usage(9, 2, 11));
        basic.Metadata.Source.Should().Be("estimated");
        basic.Metadata.Algorithm.Should().Be("tiktoken-o200k_base-unicode-v1");
        var extended = new RequestUsageTracker(request,
            new AdapterResponse { StatusCode = 200, Completion = new("gpt-4o", "Hello world", ReasoningContent: "think more", ToolCalls: [new(0, "id", "weather", """{"city":"上海"}""")]) }).Finish(true);
        extended.Usage!.CompletionTokens.Should().BeGreaterThan(basic.Usage!.CompletionTokens);
        request.Tools = [new { type = "function", function = new { name = "weather" } }];
        var withTools = new RequestUsageTracker(request, new AdapterResponse { StatusCode = 200, Completion = new("gpt-4o", "") }).Finish(true);
        withTools.Usage!.PromptTokens.Should().BeGreaterThan(basic.Usage.PromptTokens);
    }

    [TestMethod]
    public void IncrementalEstimateIsIndependentOfChunkBoundariesIncludingSurrogates()
    {
        const string text = "Hello 世界 😀 {\"city\":\"上海\"}\n";
        var whole = new UnicodeTokenEstimate();
        whole.Add(text);
        foreach (var size in new[] { 1, 2, 7 })
        {
            var split = new UnicodeTokenEstimate();
            for (var offset = 0; offset < text.Length; offset += size)
                split.Add(text.AsSpan(offset, Math.Min(size, text.Length - offset)));
            split.Count.Should().Be(whole.Count);
        }
    }

    [TestMethod]
    public void MissingFieldsAreEstimatedButZeroAndInvalidFieldsAreDistinct()
    {
        var mixed = new RequestUsageTracker(Request(), Raw("""{"choices":[{"text":"answer"}],"usage":{"prompt_tokens":0}}""")).Finish(true);
        mixed.Usage!.PromptTokens.Should().Be(0);
        mixed.Usage.CompletionTokens.Should().BeGreaterThan(0);
        mixed.Metadata.Source.Should().Be("mixed");
        mixed.Metadata.InputSource.Should().Be("response");
        var invalid = new RequestUsageTracker(Request(), Raw("""{"choices":[{"text":"answer"}],"usage":{"prompt_tokens":"bad","completion_tokens":-1}}""")).Finish(true);
        invalid.Metadata.Warning.Should().NotBeNullOrEmpty();
        invalid.Metadata.IsPartial.Should().BeTrue();
        invalid.Usage!.PromptTokens.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void FailedCallsHaveNoInventedUsageAndUnsupportedMediaIsPartial()
    {
        new RequestUsageTracker(Request(), AdapterResponse.ServerError("failed")).Finish(false).Usage.Should().BeNull();
        var request = new AdapterRequest
        {
            Model = "unknown",
            Messages = [new("user", "") { ContentParts = [new(AdapterContentPartKind.Image, ImageUrl: "https://example.invalid/image")] }]
        };
        var result = new RequestUsageTracker(request, new AdapterResponse { StatusCode = 200, Completion = new("unknown", "answer") }).Finish(true);
        result.Metadata.IsPartial.Should().BeTrue();
        var tracker = new RequestUsageTracker(Request(), new AdapterResponse { StatusCode = 200, Stream = EmptyChunks() });
        tracker.Observe(new("partial"));
        tracker.Finish(false).Metadata.IsPartial.Should().BeTrue();
        foreach (var json in new[] { "{\"output\":[{\"type\":\"message\",\"content\":[{\"text\":\"answer\"}]},{\"type\":\"image_generation_call\",\"result\":\"base64\"}]}", "{\"choices\":[{\"message\":{\"content\":\"answer\",\"audio\":{\"data\":\"base64\"}}}]}" })
            new RequestUsageTracker(Request(), Raw(json)).Finish(true).Metadata.IsPartial.Should().BeTrue();
    }

    [TestMethod]
    public async Task RawObservationPreservesBytesAndCompletesExactlyOnce()
    {
        var bytes = Encoding.UTF8.GetBytes("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3,\"total_tokens\":5}}\n\ndata: [DONE]\n\n");
        var response = PluginResponseLifetime.Ensure(new AdapterResponse
        {
            StatusCode = 200, IsRawPassthrough = true, IsStreaming = true, RawStream = RawChunks(bytes)
        });
        var tracker = new RequestUsageTracker(Request(), response);
        var owner = (PluginResponseLifetime)response.Lifetime!;
        owner.TrackUsage(tracker);
        var completed = 0;
        owner.OnCompleted(completion =>
        {
            completed++;
            completion.Success.Should().BeTrue();
            tracker.Finish(completion.Success).Usage.Should().Be(new Usage(2, 3, 5));
            return ValueTask.CompletedTask;
        });
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await ProtocolResponseWriter.WriteAsync(context, response, TestEndpoint.ChatCompletions, "unknown", CancellationToken.None);
        await owner.DisposeAsync();
        body.ToArray().Should().Equal(bytes);
        completed.Should().Be(1);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RawUsageFollowsDeliveredMarkerWhenTheCallerDisconnects(bool delivered)
    {
        var bytes = Encoding.UTF8.GetBytes("data: {\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3,\"total_tokens\":5}}\n\n" + (delivered ? "data: [DONE]\n\n" : ""));
        using var cancellation = new CancellationTokenSource();
        var response = PluginResponseLifetime.Ensure(new AdapterResponse { StatusCode = 200, IsRawPassthrough = true, IsStreaming = true, RawStream = RawThenCancel(bytes, cancellation) });
        var tracker = new RequestUsageTracker(Request(), response);
        var owner = (PluginResponseLifetime)response.Lifetime!;
        owner.TrackUsage(tracker);
        var completed = 0;
        owner.OnCompleted(outcome =>
        {
            completed++;
            outcome.Success.Should().Be(delivered);
            outcome.Cancelled.Should().Be(!delivered);
            var result = tracker.Finish(outcome.Success);
            result.Usage.Should().Be(new Usage(2, 3, 5));
            result.Metadata.IsPartial.Should().Be(!delivered);
            return ValueTask.CompletedTask;
        });
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        Func<Task> write = () => ProtocolResponseWriter.WriteAsync(context, response, TestEndpoint.ChatCompletions, "model", cancellation.Token);
        await write.Should().ThrowAsync<OperationCanceledException>();
        await owner.DisposeAsync();
        body.ToArray().Should().Equal(bytes);
        completed.Should().Be(1);
    }

    [TestMethod]
    public async Task MetadataAndAnalyticsRoundTripWithoutChangingPluginContracts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"router-usage-{Guid.NewGuid():N}.db");
        var database = new SqlSugarDatabase(Options.Create(new DatabaseOptions { Path = path }), NullLogger<SqlSugarDatabase>.Instance);
        new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance).Initialize();
        var store = new RequestLogStore(database);
        var timestamp = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        await store.WriteAsync(new RequestLog { Id = "one", Platform = "test", Model = "test/model", Success = true, Usage = new(2, 3, 5), CreatedAt = timestamp },
            new UsageMetadata("response", "response", "response", false, null));
        await store.WriteAsync(new RequestLog { Id = "two", Platform = "test", Model = "test/model", Success = false, Usage = new(4, 5, 9), CreatedAt = timestamp.AddMinutes(1) },
            new UsageMetadata("estimated", "estimated", "estimated", true, "v1"));
        await store.WriteAsync(new RequestLog { Id = "boundary", Platform = "other", Model = "other/model", Usage = new(100, 100, 200), CreatedAt = timestamp.AddHours(1) });
        var report = await new AnalyticsService(database).QueryUsageAsync(new(timestamp, timestamp.AddHours(1)));
        report.Requests.Should().Be(2);
        report.InputTokens.Should().Be(6);
        report.OutputTokens.Should().Be(8);
        report.Tokens.Should().Be(14);
        report.EstimatedRequests.Should().Be(1);
        report.PartialRequests.Should().Be(1);
        report.Buckets.Single().Tokens.Should().Be(14);
        report.Models.Single().Tokens.Should().Be(14);
        report.Platforms.Single().Name.Should().Be("test");
        var page = await store.QueryAdminAsync(new RequestLogQuery(1, 20, timestamp, timestamp.AddHours(1)));
        var json = System.Text.Json.JsonSerializer.Serialize(page);
        json.Should().Contain("usageMetadata").And.Contain("estimated");
        var filtered = await new AnalyticsService(database).QueryUsageAsync(new(timestamp, timestamp.AddHours(2), Model: "other/model"));
        filtered.Tokens.Should().Be(200);
    }

    [TestMethod]
    public async Task ExistingDatabaseRetainsOldLogsAndAddsNullableMetadataIdempotently()
    {
        var database = TestDatabase();
        var initializer = new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance);
        initializer.Initialize();
        var timestamp = DateTimeOffset.UtcNow;
        var store = new RequestLogStore(database);
        await store.WriteAsync(new RequestLog { Id = "old", CreatedAt = timestamp, Usage = new(2, 3, 5) });
        using (var db = database.CreateClient()) db.Ado.ExecuteCommand("ALTER TABLE request_logs DROP COLUMN UsageMetadataJson");
        initializer.Initialize();
        initializer.Initialize();
        var page = await store.QueryAdminAsync(new(1, 20));
        var item = JsonSerializer.SerializeToElement(page.Items.Single());
        item.GetProperty("usageMetadata").ValueKind.Should().Be(JsonValueKind.Null);
        var report = await new AnalyticsService(database).QueryUsageAsync(new(timestamp.AddSeconds(-1), timestamp.AddSeconds(1)));
        report.Tokens.Should().Be(5);
        report.UnknownUsageRequests.Should().Be(1);
    }

    [TestMethod]
    [DataRow("complete")]
    [DataRow("upstream")]
    [DataRow("cancel")]
    [DataRow("write")]
    public async Task PipelinePersistsTheSameUsageOnceAtTheRealWriterCompletion(string outcome)
    {
        var database = TestDatabase();
        new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance).Initialize();
        var store = new RequestLogStore(database);
        var platforms = new PlatformRegistry();
        var terminal = Mock.Of<IPlatformTerminal>();
        platforms.Register(new PlatformRegistration("test", "test", "Test", terminal));
        using var cancellation = new CancellationTokenSource();
        var response = PluginResponseLifetime.Ensure(new AdapterResponse { StatusCode = 200, IsStreaming = true, Stream = OutcomeChunks(outcome, cancellation) });
        var executor = new Mock<IPluginAttemptExecutor>();
        executor.Setup(value => value.ExecuteAsync("test", "test", terminal, It.IsAny<AdapterRequest>(), It.IsAny<string>(), It.IsAny<TestOverrides>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PluginInvocationResult(response, new PluginAttemptResult(PluginAttemptOutcome.Healthy, 200)));
        var metrics = new RealtimeMetrics();
        var pipeline = new RouterPipeline(new ModelRouter(platforms), platforms, executor.Object, metrics, store, Mock.Of<IUsageBucketStore>(), NullLogger<RouterPipeline>.Instance, new ModelMappingHealth());
        var request = new RequestContext { Request = new AdapterRequest { Model = "test/model", Stream = true }, CancellationToken = cancellation.Token };
        await pipeline.ExecuteAsync(request);
        (await store.QueryAsync(new(1, 20))).Total.Should().Be(0);
        var context = new DefaultHttpContext();
        await using MemoryStream body = outcome == "write" ? new FailOnDoneStream() : new MemoryStream();
        context.Response.Body = body;
        Func<Task> write = () => ProtocolResponseWriter.WriteAsync(context, request.Response!, TestEndpoint.ChatCompletions, "model", cancellation.Token);
        if (outcome == "write") await write.Should().ThrowAsync<IOException>();
        else if (outcome == "cancel") await write.Should().ThrowAsync<OperationCanceledException>();
        else await write();
        await Task.WhenAll(response.Lifetime!.DisposeAsync().AsTask(), response.Lifetime.DisposeAsync().AsTask());
        var page = await store.QueryAsync(new(1, 20));
        page.Total.Should().Be(1);
        var log = page.Items.Single();
        log.Success.Should().Be(outcome == "complete");
        log.Cancelled.Should().Be(outcome == "cancel");
        log.Usage.Should().Be(new Usage(2, 3, 5));
        var admin = await store.QueryAdminAsync(new(1, 20));
        var metadata = JsonSerializer.SerializeToElement(admin.Items.Single()).GetProperty("usageMetadata").Deserialize<UsageMetadata>()!;
        metadata.Source.Should().Be("plugin");
        metadata.IsPartial.Should().Be(outcome != "complete");
        var report = await new AnalyticsService(database).QueryUsageAsync(new(log.CreatedAt.AddSeconds(-1), log.CreatedAt.AddSeconds(1)));
        report.Tokens.Should().Be(log.Usage!.TotalTokens);
        report.Requests.Should().Be(1);
        metrics.Snapshot().ActiveConnections.Should().Be(0);
        metrics.Snapshot().ActiveStreaming.Should().Be(0);
    }

    private static SqlSugarDatabase TestDatabase() => new(
        Options.Create(new DatabaseOptions { Path = Path.Combine(Path.GetTempPath(), $"router-usage-{Guid.NewGuid():N}.db") }),
        NullLogger<SqlSugarDatabase>.Instance);

    [TestMethod]
    public async Task PipelineDoesNotRestoreInvalidUsageWhenTheTrackerReturnsUnknown()
    {
        var platforms = new PlatformRegistry();
        var terminal = Mock.Of<IPlatformTerminal>();
        platforms.Register(new PlatformRegistration("test", "test", "Test", terminal));
        var executor = new Mock<IPluginAttemptExecutor>();
        executor.Setup(value => value.ExecuteAsync("test", "test", terminal, It.IsAny<AdapterRequest>(), It.IsAny<string>(), It.IsAny<TestOverrides>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PluginInvocationResult(new AdapterResponse { StatusCode = 502, Usage = new(-1, -1, -2) }, new PluginAttemptResult(PluginAttemptOutcome.Healthy, 502)));
        var logs = new Mock<ILogSink<RequestLog>>();
        var pipeline = new RouterPipeline(new ModelRouter(platforms), platforms, executor.Object, new RealtimeMetrics(), logs.Object, Mock.Of<IUsageBucketStore>(), NullLogger<RouterPipeline>.Instance, new ModelMappingHealth());
        await pipeline.ExecuteAsync(new RequestContext { Request = new AdapterRequest { Model = "test/model" } });
        logs.Verify(value => value.WriteAsync(It.Is<RequestLog>(log => !log.Success && log.Usage == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static RequestUsageTracker RawTracker() => new(Request(), new AdapterResponse { StatusCode = 200, IsRawPassthrough = true, RawStream = EmptyRaw() });

    private static async IAsyncEnumerable<StreamChunk> OutcomeChunks(string outcome, CancellationTokenSource cancellation, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield return new("answer", Usage: new(2, 3, 5));
        if (outcome == "upstream") throw new IOException("upstream failed");
        if (outcome == "cancel") cancellation.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new(null, FinishReason: "stop");
    }

    private sealed class FailOnDoneStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => Encoding.UTF8.GetString(buffer.Span).Contains("[DONE]", StringComparison.Ordinal)
                ? ValueTask.FromException(new IOException("final marker write failed"))
                : base.WriteAsync(buffer, cancellationToken);
    }

    private static AdapterRequest Request() => new() { Model = "unknown", Messages = [new("user", "hello")] };
    private static AdapterResponse Raw(string json, Usage? usage = null) => new()
    {
        StatusCode = 200, IsRawPassthrough = true, RawContent = Encoding.UTF8.GetBytes(json), Usage = usage
    };

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> EmptyRaw() { await Task.CompletedTask; yield break; }
    private static async IAsyncEnumerable<StreamChunk> EmptyChunks() { await Task.CompletedTask; yield break; }
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> RawChunks(byte[] bytes)
    {
        await Task.CompletedTask;
        foreach (var value in bytes) yield return new byte[] { value };
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> RawThenCancel(byte[] bytes, CancellationTokenSource cancellation)
    {
        await Task.CompletedTask;
        yield return bytes;
        cancellation.Cancel();
        throw new OperationCanceledException(cancellation.Token);
    }
}
