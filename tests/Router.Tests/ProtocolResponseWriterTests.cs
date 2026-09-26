using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Router.Contracts.Domain;
using Router.Host.Api;
using Router.Host.Tracing;

namespace Router.Tests;

[TestClass]
public sealed class ProtocolResponseWriterTests
{
    [TestMethod]
    public async Task LegacyCompletionUsesTextCompletionShape()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var response = new AdapterResponse
        {
            StatusCode = 200,
            Completion = new AdapterCompletion(
                "myai-fast",
                "hello",
                "stop",
                new Usage(1, 2, 3))
        };

        await ProtocolResponseWriter.WriteAsync(
            context,
            response,
            TestEndpoint.Completions,
            "fallback-model",
            CancellationToken.None);

        body.Position = 0;
        var root = JsonNode.Parse(await new StreamReader(body, Encoding.UTF8).ReadToEndAsync())!;
        root["object"]!.GetValue<string>().Should().Be("text_completion");
        root["model"]!.GetValue<string>().Should().Be("myai-fast");
        root["choices"]![0]! ["text"]!.GetValue<string>().Should().Be("hello");
        root["usage"]!["total_tokens"]!.GetValue<int>().Should().Be(3);
    }

    [TestMethod]
    public void TraceIdIsCreatedAtHttpIngressAndCanBeReused()
    {
        var context = new DefaultHttpContext();

        var first = TraceIdContext.GetOrCreate(context);
        var second = TraceIdContext.GetOrCreate(context);

        first.Should().NotBeNullOrWhiteSpace();
        first.Should().Be(second);
        first.Should().MatchRegex("^[A-Za-z0-9._:-]+$");
    }
}
