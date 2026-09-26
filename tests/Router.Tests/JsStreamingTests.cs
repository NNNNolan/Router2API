using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Host.Plugins.JavaScript;

namespace Router.Tests;

[TestClass]
public sealed class JsStreamingTests
{
    private const string DefaultMap = """
        if (frame.data === "[DONE]") { state.done=true; return {state,chunks:[],done:true}; }
        const value=JSON.parse(frame.data);
        for(const chunk of value.chunks) if(chunk.finishReason) state.finish=chunk.finishReason;
        return {state,chunks:value.chunks};
        """;
    private const string DefaultEnd = """
        if(!state.done) return {state,chunks:[{error:"Missing upstream DONE",errorType:"upstream_incomplete_response"}]};
        return {state,chunks:[{finishReason:state.finish||"stop"}]};
        """;

    [TestMethod]
    public async Task MappedStreamPreservesReasoningToolsUsageAndOneFinish()
    {
        var stream = new CountedStream(Encoding.UTF8.GetBytes(Sample()));
        var content = new TrackedContent(stream);
        using var terminal = Terminal(() => content);
        var result = await terminal.InvokeAsync(Context(streaming: true));
        result.Response.IsStreaming.Should().BeTrue();
        content.Disposed.Should().BeFalse();
        stream.Reads.Should().Be(0);
        var chunks = await CollectAsync(result.Response);
        chunks.Any(chunk => chunk == default).Should().BeTrue();
        string.Concat(chunks.Select(chunk => chunk.ReasoningDelta)).Should().Be("think");
        string.Concat(chunks.Select(chunk => chunk.Delta)).Should().Be("answer");
        string.Concat(chunks.SelectMany(chunk => chunk.ToolCalls ?? []).Select(tool => tool.Arguments)).Should().Be("{\"q\":\"x\"}");
        chunks.Last().FinishReason.Should().Be("tool_calls");
        chunks.Count(chunk => chunk.FinishReason is not null).Should().Be(1);
        chunks.Last(chunk => chunk.Usage is not null).Usage.Should().Be(new Usage(2, 3, 5));
        content.Disposed.Should().BeTrue();
    }

    [TestMethod]
    public async Task NonStreamingUsesTheSameMapperAndSignalsBodyPhase()
    {
        var content = new TrackedContent(new CountedStream(Encoding.UTF8.GetBytes(Sample())));
        using var terminal = Terminal(() => content);
        var bodyPhase = false;
        var result = await terminal.InvokeAsync(Context(streaming: false, started: () => bodyPhase = true));
        result.Response.IsSuccess.Should().BeTrue();
        result.Response.Completion!.Content.Should().Be("answer");
        result.Response.Completion.ReasoningContent.Should().Be("think");
        result.Response.Completion.ToolCalls!.Single().Arguments.Should().Be("{\"q\":\"x\"}");
        result.Response.Completion.FinishReason.Should().Be("tool_calls");
        result.Response.Completion.Usage.Should().Be(new Usage(2, 3, 5));
        bodyPhase.Should().BeTrue();
        content.Disposed.Should().BeTrue();
    }

    [TestMethod]
    public async Task EofWithoutProtocolCompletionReturnsError()
    {
        var content = new TrackedContent(new CountedStream(Encoding.UTF8.GetBytes(Data(new StreamChunk("partial")))));
        using var terminal = Terminal(() => content);
        var result = await terminal.InvokeAsync(Context(streaming: true));
        var chunks = await CollectAsync(result.Response);
        chunks.Last().ErrorType.Should().Be("upstream_incomplete_response");
        chunks.Any(chunk => chunk.FinishReason is not null).Should().BeFalse();
        content.Disposed.Should().BeTrue();
    }

    [TestMethod]
    public async Task MapperFailureDoesNotTurnIntoAStop()
    {
        var content = new TrackedContent(new CountedStream(Encoding.UTF8.GetBytes(Sample())));
        using var terminal = Terminal(() => content, map: "throw new Error('bad mapper');");
        var result = await terminal.InvokeAsync(Context(streaming: true));
        var chunks = await CollectAsync(result.Response);
        chunks.Last().ErrorType.Should().Be("plugin_mapping_error");
        chunks.Last().Error.Should().Contain("bad mapper");
        content.Disposed.Should().BeTrue();
    }

    [TestMethod]
    public async Task AsyncMappersAreRejected()
    {
        using var terminal = Terminal(() => new TrackedContent(new CountedStream(Encoding.UTF8.GetBytes(Sample()))),
            map: "return {state,chunks:[]};", asyncMap: true);
        var result = await terminal.InvokeAsync(Context(streaming: true));
        var chunks = await CollectAsync(result.Response);
        chunks.Last().Error.Should().Contain("synchronous");
    }

    [TestMethod]
    public async Task CapturedHostCapabilitiesCannotRunInsideMapper()
    {
        using var terminal = Terminal(() => new TrackedContent(new CountedStream(Encoding.UTF8.GetBytes(Sample()))),
            map: "saved.state.set('forbidden', true); return {state,chunks:[]};");
        var result = await terminal.InvokeAsync(Context(streaming: true));
        var chunks = await CollectAsync(result.Response);
        chunks.Last().ErrorType.Should().Be("plugin_mapping_error");
        chunks.Last().Error.Should().Contain("not allowed");
    }

    [TestMethod]
    public async Task UnconsumedMappedResponseStillClosesItsSource()
    {
        var stream = new CountedStream(Encoding.UTF8.GetBytes(Sample()));
        var content = new TrackedContent(stream);
        using var terminal = Terminal(() => content);
        var result = await terminal.InvokeAsync(Context(streaming: true));
        await result.Response.Lifetime!.DisposeAsync();
        content.Disposed.Should().BeTrue();
        stream.Reads.Should().Be(0);
    }

    [TestMethod]
    public async Task RawStreamPreservesBytesWithoutSseInterpretation()
    {
        var bytes = Encoding.UTF8.GetBytes(": marker\r\nnot JSON\0中");
        var content = new TrackedContent(new CountedStream(bytes));
        using var terminal = Terminal(() => content, raw: true);
        var result = await terminal.InvokeAsync(Context(streaming: true));
        result.Response.IsRawPassthrough.Should().BeTrue();
        using var collected = new MemoryStream();
        await foreach (var chunk in result.Response.RawStream!) await collected.WriteAsync(chunk);
        collected.ToArray().Should().Equal(bytes);
        content.Disposed.Should().BeTrue();
    }

    [TestMethod]
    public async Task CancellationAfterHeadersClosesSourceAndEngine()
    {
        var content = new TrackedContent(new BlockedStream());
        using var terminal = Terminal(() => content);
        using var cancellation = new CancellationTokenSource();
        var result = await terminal.InvokeAsync(Context(streaming: true, token: cancellation.Token));
        cancellation.CancelAfter(40);
        Func<Task> read = async () =>
        {
            await foreach (var _ in result.Response.Stream!.WithCancellation(cancellation.Token)) { }
        };
        await read.Should().ThrowAsync<OperationCanceledException>();
        content.Disposed.Should().BeTrue();
    }

    private static string Data(params StreamChunk[] chunks)
        => "data: " + JsonSerializer.Serialize(new { chunks }, JsPluginCodec.JsonOptions) + "\n\n";

    private static string Sample()
        => ": ping\n\n"
            + Data(new StreamChunk(null, ReasoningDelta: "think"))
            + Data(new StreamChunk("answer"))
            + Data(new StreamChunk(null, ToolCalls: [new ToolCallDelta(0, "call_1", "function", "lookup", "{\"q\":")]))
            + Data(new StreamChunk(null, ToolCalls: [new ToolCallDelta(0, Arguments: "\"x\"}")]))
            + Data(new StreamChunk(null, "tool_calls"))
            + Data(new StreamChunk(null, Usage: new Usage(2, 3, 5)))
            + "data: [DONE]\n\n";

    private static async Task<List<StreamChunk>> CollectAsync(AdapterResponse response)
    {
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in response.Stream!) chunks.Add(chunk);
        return chunks;
    }

    private static JsPlatformTerminal Terminal(Func<HttpContent> content, string map = DefaultMap, bool asyncMap = false, bool raw = false)
    {
        var http = new Mock<IProxyPoolHttpClientFactory>();
        http.Setup(factory => factory.SendAsync(It.IsAny<Func<HttpRequestMessage>>(), It.IsAny<ProxyPoolHttpClientOptions>(),
                It.IsAny<ProxyPoolRetryOptions>(), It.IsAny<HttpCompletionOption>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
        var host = PluginTestHost.Create("js-stream", http.Object);
        var manifest = new JsPluginManifest
        {
            SchemaVersion = 1, Id = "js-stream", Name = "JS stream", Version = "0.1.0", Runtime = "jint",
            HostApi = "1-preview", Format = "esm-bundle", Entry = "server/plugin.mjs",
            Platform = new JsPlatformManifest { Name = "js-stream" },
            Hooks = new JsHooksManifest { Invoke = "invoke" },
            Permissions = new JsPermissions
            {
                Http = new JsHttpPermissions { Origins = ["https://example.com"], Routes = ["pool"] },
                State = ["read", "write"]
            },
            StreamMappers = new() { ["chat"] = new JsStreamMapperManifest { Event = "mapEvent", End = "endStream" } }
        };
        var script = $$$"""
            let saved;
            export async function invoke(ctx) {
              saved=ctx;
              const source=await ctx.http.open({url:"https://example.com/stream",route:"pool"});
              return {response:{kind:"{{{(raw ? "raw" : "mappedStream")}}}",statusCode:200,
                      source:source.handle,mapper:"chat",state:{done:false}},attempt:{outcome:"Healthy"}};
            }
            export {{{(asyncMap ? "async " : "")}}}function mapEvent(frame,state) { {{{map}}} }
            export function endStream(input,state) { {{{DefaultEnd}}} }
            """;
        manifest.Validate("js-stream");
        return new JsPlatformTerminal(new JsPluginPackage(manifest, script, null, "{}"), host);
    }

    private static PluginAttemptContext Context(bool streaming, Action? started = null, CancellationToken token = default) => new()
    {
        PluginKey = "js-stream", PlatformName = "js-stream",
        Request = new AdapterRequest { Model = "test-model", Stream = streaming },
        Account = new Account { Id = "a", PluginKey = "js-stream", Platform = "js-stream", Credential = new ApiKeyCredential("test-key") },
        HttpClient = Mock.Of<IPluginHttpClient>(), CancellationToken = token, NotifyUpstreamResponseStarted = started
    };

    private sealed class CountedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
        }
    }

    private sealed class BlockedStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class TrackedContent(Stream stream) : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => stream.CopyToAsync(destination);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposed = true; stream.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
