using System.Text.Json;
using FluentAssertions;
using Router.Host.Api;
using Router.Host.Privacy;
using Router.Infrastructure.Services.Privacy;

namespace Router.Tests;

[TestClass]
public sealed class PrivacyImageTests
{
    // 合法 Base64 字符串，包含会命中银行卡和手机号规则的编码片段；检测器不解码图片。
    private const string ImageData = "iVBORw0KGgoAAAAA4111111111111111AAAA13800138000A";

    [TestMethod]
    [DataRow("warn")]
    [DataRow("block")]
    [DataRow("redact")]
    public void InlineImageDataUrisDoNotTriggerPrivacyActions(string action)
    {
        var settings = PrivacyGuardTests.Settings(action);
        settings.CustomRules.Add(new("image-digits", "测试数字", "4111111111111111", Action: action));
        using var inspection = new PrivacyInspection(PrivacyPolicy.Build(settings));
        foreach (var text in new[]
        {
            "data:image/png;base64," + ImageData,
            "![图片](data:image/jpeg;base64," + ImageData + ")",
            "<img src=\"DATA:IMAGE/WEBP;BASE64," + ImageData + "\">",
            "图片：data:image/svg+xml;charset=utf-8;base64," + ImageData
        })
            inspection.Inspect(text).Should().Be(text);

        inspection.Report().Hits.Should().BeEmpty();
        inspection.Blocked.Should().BeFalse();
        inspection.RestoreMap.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("warn", false)]
    [DataRow("block", false)]
    [DataRow("redact", false)]
    [DataRow("block", true)]
    [DataRow("redact", true)]
    public void ImagesDoNotHideSurroundingSensitiveText(string action, bool dryRun)
    {
        var image = "data:image/png;base64," + ImageData;
        var text = $"4111111111111111 ![正面]({image}) person@example.com ![背面]({image}) 5555555555554444";
        using var inspection = new PrivacyInspection(PrivacyPolicy.Build(PrivacyGuardTests.Settings(action, dryRun)));

        var result = inspection.Inspect(text);

        result.Should().Contain(image);
        var report = inspection.Report();
        report.Hits.Should().HaveCount(2);
        report.Hits.Should().ContainSingle(hit => hit.Category == "credit_card" && hit.Count == 2);
        report.Hits.Should().ContainSingle(hit => hit.Category == "email" && hit.Count == 1);
        inspection.Blocked.Should().Be(action == "block" && !dryRun);
        if (action == "redact" && !dryRun)
        {
            inspection.RestoreMap.Values.Should().BeEquivalentTo("4111111111111111", "person@example.com", "5555555555554444");
            result.Should().StartWith("[R2A_").And.EndWith("]").And.NotContain("person@example.com");
        }
        else
        {
            result.Should().Be(text);
            inspection.RestoreMap.Should().BeEmpty();
        }
    }

    [TestMethod]
    [DataRow("/v1/chat/completions", """{"model":"demo/model","messages":[{"role":"user","content":[{"type":"text","text":"person@example.com"},{"type":"image_url","image_url":{"url":"data:image/png;base64,IMAGE"}}]}]}""")]
    [DataRow("/v1/messages", """{"model":"demo/model","messages":[{"role":"user","content":[{"type":"text","text":"person@example.com"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"IMAGE"}}]}]}""")]
    [DataRow("/v1/responses", """{"model":"demo/model","input":[{"type":"function_call_output","call_id":"call","output":[{"type":"text","text":"person@example.com"},{"type":"image","data":"IMAGE","mimeType":"image/png"}]}]}""")]
    [DataRow("/v1/messages", """{"model":"demo/model","messages":[{"role":"assistant","content":[{"type":"tool_use","name":"echo","input":{"text":"person@example.com","image":{"type":"image","source":{"type":"base64","media_type":"image/png","data":"IMAGE"}}}}]}]}""")]
    [DataRow("/v1/chat/completions", """{"model":"demo/model","messages":[{"role":"user","content":"图片 data:image/png;base64,IMAGE; person@example.com"}]}""")]
    [DataRow("/v1/chat/completions", """{"model":"demo/model","messages":[{"role":"assistant","tool_calls":[{"id":"call","type":"function","function":{"name":"echo","arguments":"{\"image\":{\"type\":\"image\",\"data\":\"IMAGE\",\"mimeType\":\"image/png\"},\"text\":\"person@example.com\"}"}}]}]}""")]
    public void ProtocolAndToolImagesKeepTheirEncodedData(string endpoint, string json)
    {
        using var document = JsonDocument.Parse(json.Replace("IMAGE", ImageData, StringComparison.Ordinal));
        var request = RequestParser.Parse(document.RootElement, PrivacyGuardTests.Endpoint(endpoint));
        using var inspection = new PrivacyInspection(PrivacyPolicy.Build(PrivacyGuardTests.Settings("redact")));

        var result = PrivacyRequestGuard.Apply(request, inspection);

        result.OriginalBody!.Value.GetRawText().Should().Contain(ImageData).And.NotContain("person@example.com");
        inspection.Report().Hits.Should().ContainSingle(hit => hit.Category == "email" && hit.Count == 1);
        inspection.RestoreMap.Values.Should().ContainSingle().Which.Should().Be("person@example.com");
    }

    [TestMethod]
    public void InlineImageIsNotScannedAgainstTheMatchBudget()
    {
        var text = "data:image/png;base64," + string.Concat(Enumerable.Repeat(ImageData, 1025));
        using var inspection = new PrivacyInspection(PrivacyPolicy.Build(PrivacyGuardTests.Settings("block")));

        inspection.Inspect(text).Should().Be(text);

        inspection.Report().Hits.Should().BeEmpty();
        inspection.Blocked.Should().BeFalse();
    }

    [TestMethod]
    public void InlineImageStillCountsTowardsTheTextBudget()
    {
        var text = "data:image/png;base64," + new string('A', 16 * 1024 * 1024);
        using var inspection = new PrivacyInspection(PrivacyPolicy.Build(PrivacyGuardTests.Settings("block")));

        Action inspect = () => inspection.Inspect(text);

        inspect.Should().Throw<PrivacyInspectionException>();
        inspection.Report().Error.Should().NotBeNull();
    }
}
