using System;
using System.Text.Json;
using System.ClientModel.Primitives;
using Anthropic.Models.Messages;
using OpenAI.Chat;
using OpenAI.Responses;
using Router.Contracts.Domain;

#pragma warning disable OPENAI001

namespace Router.Host.Api;

/// <summary>Serializes host-owned API response shapes and checks them with the official SDK models.</summary>
internal static class OfficialApiResponseSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] SerializeOpenAiChatCompletion(object response)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        var completion = ModelReaderWriter.Read<ChatCompletion>(BinaryData.FromBytes(json))
            ?? throw new JsonException("OpenAI Chat Completions response was empty");
        if (string.IsNullOrWhiteSpace(completion.Id) || string.IsNullOrWhiteSpace(completion.Model))
            throw new JsonException("OpenAI Chat Completions response is missing required fields");
        _ = completion.FinishReason;
        return json;
    }

    public static byte[] SerializeResponses(object response)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        var result = ModelReaderWriter.Read<ResponseResult>(BinaryData.FromBytes(json))
            ?? throw new JsonException("OpenAI Responses result was empty");
        if (string.IsNullOrWhiteSpace(result.Id)
            || string.IsNullOrWhiteSpace(result.Model)
            || result.Status is null
            || result.OutputItems is null)
            throw new JsonException("OpenAI Responses result is missing required fields");
        return json;
    }

    public static byte[] SerializeAnthropic(object response)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        (JsonSerializer.Deserialize<Message>(json, JsonOptions)
            ?? throw new JsonException("Anthropic Messages result was empty"))
            .Validate();
        return json;
    }
}

/// <summary>Maps internal plugin errors to the error envelope expected by each public protocol.</summary>
internal static class ProtocolApiErrors
{
    public static object OpenAi(AdapterResponse response)
        => OpenAi(response.Error ?? "upstream request failed", response.StatusCode, response.ErrorType);

    public static object OpenAi(string message, int statusCode, string? errorType = null)
        => new
        {
            error = new
            {
                message,
                type = ResolveOpenAiErrorType(errorType, statusCode),
                param = (string?)null,
                code = (string?)null
            }
        };

    public static object Anthropic(AdapterResponse response)
    {
        var statusCode = response.StatusCode;
        var type = ResolveAnthropicErrorType(response.ErrorType, statusCode);
        return new
        {
            type = "error",
            error = new
            {
                type,
                message = response.Error ?? "upstream request failed"
            }
        };
    }

    private static string OpenAiErrorType(int statusCode)
        => statusCode switch
        {
            400 or 422 => "invalid_request_error",
            401 => "authentication_error",
            403 => "permission_error",
            404 => "not_found_error",
            429 => "rate_limit_error",
            >= 500 => "server_error",
            _ => "api_error"
        };

    private static string ResolveOpenAiErrorType(string? errorType, int statusCode)
        => errorType is "invalid_request_error" or "authentication_error" or "permission_error"
            or "not_found_error" or "rate_limit_error" or "server_error" or "api_error"
                ? errorType
                : OpenAiErrorType(statusCode);

    private static string AnthropicErrorType(int statusCode)
        => statusCode switch
        {
            400 or 422 => "invalid_request_error",
            401 => "authentication_error",
            403 => "permission_error",
            404 => "not_found_error",
            429 => "rate_limit_error",
            529 => "overloaded_error",
            _ => "api_error"
        };

    private static string ResolveAnthropicErrorType(string? errorType, int statusCode)
        => errorType is "invalid_request_error" or "authentication_error" or "permission_error"
            or "not_found_error" or "rate_limit_error" or "overloaded_error" or "api_error"
                ? errorType
                : AnthropicErrorType(statusCode);
}
