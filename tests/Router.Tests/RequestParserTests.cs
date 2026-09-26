using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Router.Contracts.Domain;
using Router.Host.Api;

namespace Router.Tests;

[TestClass]
public sealed class RequestParserTests
{
    [TestMethod]
    public void ChatRequestPreservesLegacyPromptToolsAndToolChoice()
    {
        using var document = JsonDocument.Parse("""
            {
              "model": "opencode/mimo-v2.6-flash-free",
              "prompt": "自检工具调用",
              "functions": [
                {
                  "name": "probe",
                  "description": "probe the environment",
                  "parameters": { "type": "object", "properties": {} }
                }
              ],
              "function_call": "auto"
            }
            """);

        var request = RequestParser.Parse(document.RootElement);

        request.Messages.Should().ContainSingle().Which.Content.Should().Be("自检工具调用");
        request.Tools.Should().ContainSingle();
        request.Extensions.Should().ContainKey("tool_choice");
        ((string)request.Extensions["tool_choice"]!).Should().Be("auto");
    }

    [TestMethod]
    public void ChatHistoryPreservesToolCallsAndToolCallId()
    {
        using var document = JsonDocument.Parse("""
            {
              "model": "opencode/mimo-v2.6-flash-free",
              "messages": [
                {
                  "role": "assistant",
                  "content": null,
                  "tool_calls": [
                    {
                      "id": "call_probe",
                      "type": "function",
                      "function": { "name": "probe", "arguments": "{}" }
                    }
                  ]
                },
                {
                  "role": "tool",
                  "tool_call_id": "call_probe",
                  "content": "ok"
                }
              ]
            }
            """);

        var request = RequestParser.Parse(document.RootElement);
        var assistant = request.Messages[0];
        var tool = request.Messages[1];

        assistant.Content.Should().BeEmpty();
        assistant.ToolCalls.Should().ContainSingle();
        tool.ToolCallId.Should().Be("call_probe");
        tool.Content.Should().Be("ok");
    }

    [TestMethod]
    public void LegacyCompletionsEndpointIsKeptSeparateFromChatCompletions()
    {
        using var document = JsonDocument.Parse("""
            { "endpoint": "completions", "model": "myai-fast", "prompt": "hello" }
            """);

        RequestParser.TryParseEndpoint(document.RootElement, out var endpoint).Should().BeTrue();
        endpoint.Should().Be(TestEndpoint.Completions);

        var request = RequestParser.Parse(document.RootElement, endpoint);
        request.Messages.Should().ContainSingle().Which.Content.Should().Be("hello");
    }

    [TestMethod]
    public void UnknownProviderParametersRemainAvailableToThePlugin()
    {
        using var document = JsonDocument.Parse("""
            {
              "model": "opencode/mimo-v2.6-flash-free",
              "messages": [{ "role": "user", "content": "hello" }],
              "max_completion_tokens": 321,
              "prompt_cache_key": "conversation-1",
              "include": ["reasoning.encrypted_content"],
              "metadata": { "session_id": "conversation-1" },
              "endpoint": "chat-completions"
            }
            """);

        var request = RequestParser.Parse(document.RootElement);

        request.MaxTokens.Should().Be(321);
        request.Extensions.Should().ContainKey("prompt_cache_key");
        request.Extensions.Should().ContainKey("include");
        request.Extensions.Should().ContainKey("metadata");
        request.Extensions.Should().NotContainKey("endpoint");
    }

    [TestMethod]
    public void ChatImagesKeepImageOnlyMessagesAndPartOrder()
    {
        using var document = JsonDocument.Parse("""
            {
              "model": "workbuddy/deepseek-v4.1-flash",
              "messages": [
                {
                  "role": "user",
                  "content": [{ "type": "image_url", "image_url": { "url": "https://example.com/one.png" } }]
                },
                {
                  "role": "user",
                  "content": [
                    { "type": "text", "text": "before" },
                    { "type": "image_url", "image_url": { "url": "data:image/png;base64,AA==", "detail": "high" } },
                    { "type": "text", "text": "after" }
                  ]
                }
              ]
            }
            """);

        var messages = RequestParser.Parse(document.RootElement).Messages;

        messages.Should().HaveCount(2);
        messages[0].Content.Should().BeEmpty();
        var imageOnly = JsonSerializer.SerializeToElement(AdapterContentFormatter.ToChatContent(messages[0]));
        imageOnly[0].GetProperty("image_url").GetProperty("url").GetString()
            .Should().Be("https://example.com/one.png");

        messages[1].Content.Should().Be("beforeafter");
        var mixed = JsonSerializer.SerializeToElement(AdapterContentFormatter.ToChatContent(messages[1]));
        mixed.EnumerateArray().Select(part => part.GetProperty("type").GetString())
            .Should().Equal("text", "image_url", "text");
        mixed[1].GetProperty("image_url").GetProperty("detail").GetString().Should().Be("high");
    }

    [TestMethod]
    public void ResponsesImageConvertsToChatAndAnthropicContent()
    {
        using var document = JsonDocument.Parse("""
            {
              "model": "workbuddy/deepseek-v4.1-flash",
              "input": [{
                "role": "user",
                "content": [
                  { "type": "input_text", "text": "describe" },
                  { "type": "input_image", "image_url": "data:image/png;base64,AA==" }
                ]
              }]
            }
            """);

        var message = RequestParser.Parse(document.RootElement, TestEndpoint.Responses).Messages.Single();
        var chat = JsonSerializer.SerializeToElement(AdapterContentFormatter.ToChatContent(message));
        var anthropic = JsonSerializer.SerializeToElement(AdapterContentFormatter.ToAnthropicContent(message));

        chat[1].GetProperty("image_url").GetProperty("url").GetString()
            .Should().Be("data:image/png;base64,AA==");
        anthropic[1].GetProperty("source").GetProperty("type").GetString().Should().Be("base64");
        anthropic[1].GetProperty("source").GetProperty("media_type").GetString().Should().Be("image/png");
        anthropic[1].GetProperty("source").GetProperty("data").GetString().Should().Be("AA==");
    }

    [TestMethod]
    public void AnthropicImageConvertsToResponsesAndCrossProtocolFileIdIsRejected()
    {
        using var anthropicDocument = JsonDocument.Parse("""
            {
              "model": "opencode/vision-model",
              "messages": [{
                "role": "user",
                "content": [{
                  "type": "image",
                  "source": { "type": "base64", "media_type": "image/jpeg", "data": "AA==" }
                }]
              }]
            }
            """);
        var imageMessage = RequestParser.Parse(anthropicDocument.RootElement, TestEndpoint.AnthropicMessages)
            .Messages.Single();
        var responses = JsonSerializer.SerializeToElement(
            AdapterContentFormatter.ToResponsesContent(imageMessage, isAssistant: false));

        responses[0].GetProperty("type").GetString().Should().Be("input_image");
        responses[0].GetProperty("image_url").GetString().Should().Be("data:image/jpeg;base64,AA==");

        using var responsesDocument = JsonDocument.Parse("""
            {
              "model": "opencode/vision-model",
              "input": [{
                "role": "user",
                "content": [{ "type": "input_image", "file_id": "file_provider_specific" }]
              }]
            }
            """);
        var fileRequest = RequestParser.Parse(responsesDocument.RootElement, TestEndpoint.Responses);
        var fileMessage = fileRequest.Messages.Single();
        fileMessage.ContentParts.Should().ContainSingle().Which.FileId.Should().Be("file_provider_specific");
        fileRequest.OriginalBody.Should().NotBeNull();
        fileRequest.OriginalBody!.Value.GetRawText().Should().Contain("file_provider_specific");
        var convertToChat = () => AdapterContentFormatter.ToChatContent(fileMessage);
        convertToChat.Should().Throw<NotSupportedException>();
        var convertToResponses = () => AdapterContentFormatter.ToResponsesContent(fileMessage, isAssistant: false);
        convertToResponses.Should().Throw<NotSupportedException>();
    }

    [TestMethod]
    public void ChatPdfFileKeepsAttachmentOrderAcrossProtocols()
    {
        using var document = JsonDocument.Parse("""
            {
              "model": "workbuddy/deepseek-v4.1-flash",
              "messages": [{
                "role": "user",
                "content": [
                  { "type": "text", "text": "before" },
                  { "type": "file", "file": { "filename": "report.pdf", "file_data": "data:application/pdf;base64,JVBERg==" } },
                  { "type": "text", "text": "after" }
                ]
              }]
            }
            """);

        var message = RequestParser.Parse(document.RootElement).Messages.Single();
        message.Content.Should().Be("beforeafter");
        message.ContentParts.Should().HaveCount(3);
        var chat = JsonSerializer.SerializeToElement(AdapterContentFormatter.ToChatContent(message));
        var responses = JsonSerializer.SerializeToElement(
            AdapterContentFormatter.ToResponsesContent(message, isAssistant: false));
        var anthropic = JsonSerializer.SerializeToElement(AdapterContentFormatter.ToAnthropicContent(message));

        chat.EnumerateArray().Select(part => part.GetProperty("type").GetString())
            .Should().Equal("text", "file", "text");
        chat[1].GetProperty("file").GetProperty("file_data").GetString()
            .Should().Be("data:application/pdf;base64,JVBERg==");
        responses[1].GetProperty("type").GetString().Should().Be("input_file");
        anthropic[1].GetProperty("source").GetProperty("media_type").GetString()
            .Should().Be("application/pdf");
    }

    [TestMethod]
    public void ResponsesFileUrlAndFileIdAreHandledExplicitly()
    {
        using var document = JsonDocument.Parse("""
            {
              "model": "opencode/model",
              "input": [
                { "role": "user", "content": [{ "type": "input_file", "filename": "report.pdf", "file_url": "https://example.com/report.pdf" }] },
                { "role": "user", "content": [{ "type": "input_file", "file_id": "file_provider_specific" }] }
              ]
            }
            """);

        var messages = RequestParser.Parse(document.RootElement, TestEndpoint.Responses).Messages;
        messages.Should().HaveCount(2);
        var anthropic = JsonSerializer.SerializeToElement(AdapterContentFormatter.ToAnthropicContent(messages[0]));
        anthropic[0].GetProperty("source").GetProperty("url").GetString()
            .Should().Be("https://example.com/report.pdf");
        ((Action)(() => AdapterContentFormatter.ToChatContent(messages[0])))
            .Should().Throw<NotSupportedException>().WithMessage("*file_url*");
        ((Action)(() => AdapterContentFormatter.ToAnthropicContent(messages[1])))
            .Should().Throw<NotSupportedException>().WithMessage("*file ID*");
    }

    [TestMethod]
    public void InlineTextFilesAreReadableAndUnsupportedBinaryFilesFail()
    {
        using var document = JsonDocument.Parse("""
            {
              "model": "workbuddy/deepseek-v4.1-flash",
              "input": [
                { "role": "user", "content": [{ "type": "input_file", "filename": "notes.txt", "file_data": "data:text/plain;base64,aGVsbG8=" }] },
                { "role": "user", "content": [{ "type": "input_file", "filename": "report.docx", "file_data": "data:application/vnd.openxmlformats-officedocument.wordprocessingml.document;base64,AA==" }] }
              ]
            }
            """);

        var messages = RequestParser.Parse(document.RootElement, TestEndpoint.Responses).Messages;
        var chat = JsonSerializer.SerializeToElement(AdapterContentFormatter.ToChatContent(messages[0]));
        chat[0].GetProperty("text").GetString().Should().Be("File: notes.txt\nhello");
        ((Action)(() => AdapterContentFormatter.ToChatContent(messages[1])))
            .Should().Throw<NotSupportedException>().WithMessage("*Responses-native*");

        using var anthropicDocument = JsonDocument.Parse("""
            {
              "model": "opencode/model",
              "messages": [{ "role": "user", "content": [{
                "type": "document",
                "title": "memo.txt",
                "source": { "type": "text", "media_type": "text/plain", "data": "hello" }
              }] }]
            }
            """);
        var plainText = RequestParser.Parse(anthropicDocument.RootElement, TestEndpoint.AnthropicMessages)
            .Messages.Single();
        var converted = JsonSerializer.SerializeToElement(AdapterContentFormatter.ToChatContent(plainText));
        converted[0].GetProperty("text").GetString().Should().Be("File: memo.txt\nhello");
    }
}
