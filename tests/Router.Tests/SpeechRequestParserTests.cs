using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Router.Contracts.Domain;
using Router.Host.Api;

namespace Router.Tests;

[TestClass]
public sealed class SpeechRequestParserTests
{
    [TestMethod]
    public void ParsesOpenAiSpeechBodyIntoAudioRequest()
    {
        AdapterRequest request;
        using (var document = JsonDocument.Parse("""
            {
              "model": "doubaotts/taozi",
              "input": "你好，我是豆包。",
              "voice": "tianmei",
              "response_format": "mp3",
              "speed": 1.2,
              "instructions": "用轻快的语气",
              "metadata": { "trace": 1 }
            }
            """))
        {
            SpeechRequestParser.TryParse(document.RootElement, out var parsed, out var error).Should().BeTrue();
            error.Should().BeNull();
            request = parsed!;
        }

        request.Model.Should().Be("doubaotts/taozi");
        request.Endpoint.Should().Be("/v1/audio/speech");
        request.Messages.Should().ContainSingle().Which.Content.Should().Be("你好，我是豆包。");
        request.Stream.Should().BeFalse();

        // 正文与扩展都必须脱离原 JsonDocument 生命周期。
        request.OriginalBody!.Value.GetProperty("voice").GetString().Should().Be("tianmei");
        request.Extensions.Keys.Should().Contain(["voice", "response_format", "speed", "instructions", "metadata"]);
        request.Extensions.Keys.Should().NotContain(["model", "input"]);
        ((JsonElement)request.Extensions["speed"]!).GetDouble().Should().Be(1.2);
    }

    [TestMethod]
    public void AcceptsTextAliasWhenInputIsMissing()
    {
        using var document = JsonDocument.Parse("""{"model":"doubaotts/taozi","text":"朗读这段"}""");

        SpeechRequestParser.TryParse(document.RootElement, out var request, out var error).Should().BeTrue();

        error.Should().BeNull();
        request!.Messages.Should().ContainSingle().Which.Content.Should().Be("朗读这段");
    }

    [TestMethod]
    public void KeepsModelNameUnchangedSoMappingsStillResolve()
    {
        using var document = JsonDocument.Parse("""{"model":"  tts  ","input":"hi"}""");

        SpeechRequestParser.TryParse(document.RootElement, out var request, out _).Should().BeTrue();

        request!.Model.Should().Be("tts");
    }

    [TestMethod]
    public void RejectsMissingModel()
    {
        using var document = JsonDocument.Parse("""{"input":"hi"}""");

        SpeechRequestParser.TryParse(document.RootElement, out var request, out var error).Should().BeFalse();

        request.Should().BeNull();
        error.Should().Contain("model");
    }

    [TestMethod]
    public void RejectsMissingOrBlankInput()
    {
        foreach (var body in new[] { """{"model":"doubaotts/taozi"}""", """{"model":"doubaotts/taozi","input":"   "}""" })
        {
            using var document = JsonDocument.Parse(body);

            SpeechRequestParser.TryParse(document.RootElement, out var request, out var error).Should().BeFalse();

            request.Should().BeNull();
            error.Should().Contain("input");
        }
    }

    [TestMethod]
    public void RejectsNonObjectBody()
    {
        foreach (var body in new[] { "[]", "\"text\"", "42", "null" })
        {
            using var document = JsonDocument.Parse(body);

            SpeechRequestParser.TryParse(document.RootElement, out var request, out var error).Should().BeFalse();

            request.Should().BeNull();
            error.Should().Contain("JSON object");
        }
    }

    [TestMethod]
    public void RejectsStreamingSpeech()
    {
        using var document = JsonDocument.Parse("""{"model":"doubaotts/taozi","input":"hi","stream":true}""");

        SpeechRequestParser.TryParse(document.RootElement, out var request, out var error).Should().BeFalse();

        request.Should().BeNull();
        error.Should().Contain("streaming");
    }

    [TestMethod]
    public void EnforcesOpenAiInputLengthLimit()
    {
        using var atLimitDocument = JsonDocument.Parse(SerializeInput(new string('a', SpeechRequestParser.MaxInputLength)));
        SpeechRequestParser.TryParse(atLimitDocument.RootElement, out var accepted, out var acceptedError).Should().BeTrue();
        acceptedError.Should().BeNull();
        accepted!.Messages.Should().ContainSingle().Which.Content.Should().HaveLength(SpeechRequestParser.MaxInputLength);

        using var overLimitDocument = JsonDocument.Parse(SerializeInput(new string('a', SpeechRequestParser.MaxInputLength + 1)));
        SpeechRequestParser.TryParse(overLimitDocument.RootElement, out var rejected, out var rejectedError).Should().BeFalse();
        rejected.Should().BeNull();
        rejectedError.Should().Contain(SpeechRequestParser.MaxInputLength.ToString(CultureInfo.InvariantCulture));

        static string SerializeInput(string input)
            => JsonSerializer.Serialize(new { model = "doubaotts/taozi", input });
    }

    [TestMethod]
    public void PropertyNamesAreCaseInsensitive()
    {
        using var document = JsonDocument.Parse("""{"MODEL":"doubaotts/taozi","Input":"hi"}""");

        SpeechRequestParser.TryParse(document.RootElement, out var request, out _).Should().BeTrue();

        request!.Model.Should().Be("doubaotts/taozi");
        request.Messages.Should().ContainSingle().Which.Content.Should().Be("hi");
    }

    [TestMethod]
    public void NonStringVoiceIsKeptForPluginToReject()
    {
        using var document = JsonDocument.Parse("""{"model":"doubaotts/taozi","input":"hi","voice":123}""");

        SpeechRequestParser.TryParse(document.RootElement, out var request, out _).Should().BeTrue();

        ((JsonElement)request!.Extensions["voice"]!).ValueKind.Should().Be(JsonValueKind.Number);
    }
}
