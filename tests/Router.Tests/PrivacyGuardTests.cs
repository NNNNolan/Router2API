using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Domain;
using Router.Host.Api;
using Router.Host.Privacy;
using Router.Infrastructure.Services.Privacy;

namespace Router.Tests;

[TestClass]
public sealed class PrivacyGuardTests
{
    [TestMethod]
    public void DefaultIsOffAndEachRequestKeepsItsOwnPolicyAndMap()
    {
        Action<PrivacyOptions, string?>? changed = null;
        var monitor = new Mock<IOptionsMonitor<PrivacyOptions>>();
        monitor.SetupGet(value => value.CurrentValue).Returns(new PrivacyOptions());
        monitor.Setup(value => value.OnChange(It.IsAny<Action<PrivacyOptions, string?>>()))
            .Callback<Action<PrivacyOptions, string?>>(callback => changed = callback).Returns(Mock.Of<IDisposable>());
        using var guard = new PrivacyRequestGuard(monitor.Object);
        var request = new AdapterRequest();
        guard.Begin(request).Should().BeNull();
        changed!(Settings("redact"), null);
        using var first = guard.Begin(request)!;
        var placeholder = first.Inspect("first@example.com");
        changed(Settings("block"), null);
        using var second = guard.Begin(request)!;
        second.Inspect("second@example.com");
        second.Blocked.Should().BeTrue();
        first.Blocked.Should().BeFalse();
        first.RestoreMap[placeholder].Should().Be("first@example.com");
        first.Inspect("another@example.com").Should().StartWith("[R2A_");
        first.Dispose();
        first.RestoreMap.Should().BeEmpty();
        guard.Begin(new AdapterRequest { Endpoint = "/v1/audio/speech" }).Should().BeNull();
    }

    [TestMethod]
    [DataRow("/v1/chat/completions", """{"model":"demo/model","messages":[{"role":"user","content":"person@example.com"}]}""")]
    [DataRow("/v1/completions", """{"model":"demo/model","prompt":["person@example.com"]}""")]
    [DataRow("/v1/responses", """{"model":"demo/model","instructions":"person@example.com","input":[{"type":"function_call","name":"person@example.com","arguments":"{\"email\":\"person@example.com\"}"}]}""")]
    [DataRow("/v1/messages", """{"model":"demo/model","system":[{"type":"text","text":"person@example.com"}],"messages":[{"role":"assistant","content":[{"type":"tool_use","name":"person@example.com","input":{"email":"person@example.com"}}]}]}""")]
    public void ProtocolTextIsRewrittenAndOriginalSourceIsConsistent(string endpoint, string json)
    {
        using var document = JsonDocument.Parse(json);
        var request = RequestParser.Parse(document.RootElement, Endpoint(endpoint));
        using var inspection = new PrivacyInspection(PrivacyPolicy.Build(Settings("redact")));
        var rewritten = PrivacyRequestGuard.Apply(request, inspection);
        rewritten.OriginalBody!.Value.GetRawText().Should().Contain("[R2A_");
        rewritten.Model.Should().Be(request.Model);
        inspection.RestoreMap.Values.Should().Contain("person@example.com");
        rewritten.Messages.Select(message => message.Content).Should().NotContain("person@example.com");
        request.OriginalBody!.Value.GetRawText().Should().Contain("person@example.com");
    }

    [TestMethod]
    public void ToolsPrecisionAndBinaryBoundariesRemainIntact()
    {
        const string json = """
            {"model":"person@example.com","messages":[{"role":"assistant","name":"person@example.com","content":[
                {"type":"text","text":"person@example.com"},
                {"type":"image_url","image_url":{"url":"https://person@example.com/image"}},
                {"type":"file","file":{"file_data":"person@example.com"}}
            ],"tool_calls":[{"id":"person@example.com","type":"function","function":{"name":"person@example.com","arguments":"{\"email\":\"person@example.com\",\"n\":9007199254740993}"}}]}],
            "tools":[{"type":"function","function":{"name":"person@example.com","description":"person@example.com",
                "parameters":{"type":"object","properties":{"email":{"type":"string","description":"person@example.com"}}}}}],
            "metadata":{"email":"person@example.com","n":9007199254740993}}
            """;
        using var document = JsonDocument.Parse(json);
        var request = RequestParser.Parse(document.RootElement);
        using var inspection = new PrivacyInspection(PrivacyPolicy.Build(Settings("redact")));
        var rewritten = PrivacyRequestGuard.Apply(request, inspection);
        var body = rewritten.OriginalBody!.Value;
        body.GetProperty("model").GetString().Should().Be("person@example.com");
        body.GetProperty("metadata").GetProperty("n").GetRawText().Should().Be("9007199254740993");
        body.GetProperty("metadata").GetProperty("email").GetString().Should().Be("person@example.com");
        var message = body.GetProperty("messages")[0];
        message.GetProperty("content")[0].GetProperty("text").GetString().Should().StartWith("[R2A_");
        message.GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString().Should().Contain("person@example.com");
        message.GetProperty("content")[2].GetProperty("file").GetProperty("file_data").GetString().Should().Be("person@example.com");
        var function = message.GetProperty("tool_calls")[0].GetProperty("function");
        function.GetProperty("name").GetString().Should().Be("person@example.com");
        function.GetProperty("arguments").GetString().Should().Contain("9007199254740993").And.NotContain("person@example.com");
        body.GetProperty("tools")[0].GetProperty("function").GetProperty("description").GetString().Should().StartWith("[R2A_");
        JsonSerializer.Serialize(inspection.Report()).Should().NotContain("person@example.com").And.NotContain("[R2A_");
    }

    [TestMethod]
    public void DryRunPreservesBodyAndCustomActionTakesPrecedence()
    {
        var settings = Settings("block", true);
        settings.CategoryActions["email"] = "warn";
        settings.CustomRules.Add(new("custom-rule", "测试", "CONFIDENTIAL", Action: "redact"));
        using var dry = new PrivacyInspection(PrivacyPolicy.Build(settings));
        dry.Inspect("person@example.com CONFIDENTIAL").Should().Be("person@example.com CONFIDENTIAL");
        dry.Report().Blocked.Should().BeFalse();
        dry.RestoreMap.Should().BeEmpty();
        dry.Report().Hits.Single(hit => hit.Category == "email").Action.Should().Be("warn");
        dry.Report().Hits.Single(hit => hit.Category == "custom").Action.Should().Be("redact");
        var invalid = Settings("warn");
        invalid.CustomRules.Add(new("bad", "bad", "["));
        Action compile = () => PrivacyPolicy.Build(invalid);
        compile.Should().Throw<ArgumentException>();
        invalid.CustomRules[0] = new("bad", "bad", ".*");
        compile.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void DryRunScansProtocolTextWithoutChangingTheOriginalBody()
    {
        const string json = """{"model":"demo/model","messages":[{"role":"user","content":"person@example.com"}]}""";
        using var document = JsonDocument.Parse(json);
        var request = RequestParser.Parse(document.RootElement);
        using var inspection = new PrivacyInspection(PrivacyPolicy.Build(Settings("redact", dryRun: true)));

        var result = PrivacyRequestGuard.Apply(request, inspection);

        result.Should().BeSameAs(request);
        result.OriginalBody!.Value.GetRawText().Should().Be(json);
        inspection.Report().Hits.Should().ContainSingle(hit => hit.Category == "email");
        inspection.RestoreMap.Should().BeEmpty();
    }

    [TestMethod]
    public void RedactPolicyWithNoMatchesKeepsTheOriginalRequest()
    {
        const string json = """{"model":"demo/model","messages":[{"role":"user","content":"ordinary text"}]}""";
        using var document = JsonDocument.Parse(json);
        var request = RequestParser.Parse(document.RootElement);
        using var inspection = new PrivacyInspection(PrivacyPolicy.Build(Settings("redact")));

        var result = PrivacyRequestGuard.Apply(request, inspection);

        result.Should().BeSameAs(request);
        result.OriginalBody!.Value.GetRawText().Should().Be(json);
        inspection.Report().Hits.Should().BeEmpty();
    }

    [TestMethod]
    public void RedactScanCountsEachOccurrenceOnceBeforeWriting()
    {
        const string json = """{"model":"demo/model","messages":[{"role":"user","content":"person@example.com person@example.com"}]}""";
        using var document = JsonDocument.Parse(json);
        var request = RequestParser.Parse(document.RootElement);
        using var inspection = new PrivacyInspection(PrivacyPolicy.Build(Settings("redact")));

        var result = PrivacyRequestGuard.Apply(request, inspection);

        inspection.Report().Hits.Single(hit => hit.Category == "email").Count.Should().Be(2);
        result.OriginalBody!.Value.GetProperty("messages")[0].GetProperty("content").GetString()
            .Should().NotContain("person@example.com");
    }

    [TestMethod]
    public void StandardRequestDryRunScansWithoutCreatingARewrittenRequest()
    {
        var request = new AdapterRequest { Messages = [new AdapterMessage("user", "person@example.com")] };
        using var inspection = new PrivacyInspection(PrivacyPolicy.Build(Settings("redact", dryRun: true)));

        var result = PrivacyRequestGuard.Apply(request, inspection);

        result.Should().BeSameAs(request);
        inspection.Report().Hits.Should().ContainSingle(hit => hit.Category == "email" && hit.Count == 1);
        inspection.RestoreMap.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("api_key", "sk-unit0123456789abcdefghijklmnop")]
    [DataRow("aws_key", "AKIA0123456789ABCDEF")]
    [DataRow("jwt", "eyJabcdefghijk.abcdefghijkl.abcdefghijkl")]
    [DataRow("private_key", "-----BEGIN PRIVATE KEY-----\nunit\n-----END PRIVATE KEY-----")]
    [DataRow("email", "person@example.com")]
    [DataRow("phone", "13800138000")]
    [DataRow("intranet", "192.168.1.10")]
    [DataRow("credit_card", "4111111111111111")]
    [DataRow("national_id", "110105199001011234")]
    [DataRow("credential", "password=unit-secret")]
    public void BuiltinCategoriesDetect(string category, string sample)
    {
        using var inspection = new PrivacyInspection(PrivacyPolicy.Build(Settings("warn")));
        inspection.Inspect(sample);
        inspection.Report().Hits.Should().Contain(hit => hit.Category == category);
    }

    [TestMethod]
    public void MatchBudgetFailsClosedWithoutPersistingOriginals()
    {
        using var inspection = new PrivacyInspection(PrivacyPolicy.Build(Settings("redact")));
        Action scan = () => inspection.Inspect(string.Join(" ", Enumerable.Repeat("person@example.com", 1025)));
        scan.Should().Throw<PrivacyInspectionException>();
        inspection.Report().Error.Should().NotBeNull();
        JsonSerializer.Serialize(inspection.Report()).Should().NotContain("person@example.com");
    }

    internal static PrivacyOptions Settings(string action, bool dryRun = false) => new() { Enabled = true, DryRun = dryRun, DefaultAction = action };
    internal static TestEndpoint Endpoint(string value) => value switch
    {
        "/v1/completions" => TestEndpoint.Completions, "/v1/responses" => TestEndpoint.Responses,
        "/v1/messages" => TestEndpoint.AnthropicMessages, _ => TestEndpoint.ChatCompletions
    };
}
