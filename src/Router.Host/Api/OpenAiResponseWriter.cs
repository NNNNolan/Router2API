using System.Text.Json;
using Router.Contracts.Domain;

namespace Router.Host.Api;

internal static class OpenAiResponseWriter
{
    public static async Task WriteAsync(
        HttpContext context,
        AdapterResponse response,
        string model,
        CancellationToken cancellationToken)
    {
        if (!response.IsStreaming || response.Stream is null)
        {
            context.Response.StatusCode = response.StatusCode;
            if (!response.IsSuccess)
            {
                await context.Response.WriteAsJsonAsync(ProtocolApiErrors.OpenAi(response), cancellationToken);
                return;
            }

            if (response.Completion is not { } completion)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsJsonAsync(
                    ProtocolApiErrors.OpenAi("plugin returned no completion result", StatusCodes.Status502BadGateway),
                    cancellationToken);
                return;
            }

            byte[] body;
            try
            {
                body = OfficialApiResponseSerializer.SerializeOpenAiChatCompletion(
                    CreateChatCompletionBody(completion, model));
            }
            catch (Exception)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsJsonAsync(
                    ProtocolApiErrors.OpenAi("plugin returned an invalid Chat Completions result", StatusCodes.Status502BadGateway),
                    cancellationToken);
                return;
            }
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.Body.WriteAsync(body, cancellationToken);
            return;
        }

        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        using var writer = new SseJsonWriter(context.Response);
        var id = $"chatcmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await foreach (var chunk in response.Stream.WithCancellation(cancellationToken))
        {
            if (chunk == default) { await ProtocolResponseWriter.WriteHeartbeatAsync(context, cancellationToken); continue; }
            if (chunk.Error is { } error)
            {
                await writer.WriteAsync(ProtocolApiErrors.OpenAi(error, StatusCodes.Status502BadGateway, chunk.ErrorType), cancellationToken);
                break;
            }

            WriteChunk(writer.StartEvent(), chunk, id, created, model);
            await writer.FlushEventAsync(cancellationToken);
        }

        await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    internal static void WriteChunk(Utf8JsonWriter json, StreamChunk chunk, string id, long created, string model)
    {
        var usageOnly = chunk.Usage is not null
            && chunk.Delta is null && chunk.FinishReason is null && chunk.Role is null
            && chunk.ToolCalls is not { Count: > 0 } && chunk.ReasoningDelta is null && chunk.ReasoningSignature is null;
        json.WriteStartObject();
        json.WriteString("id", id);
        json.WriteString("object", "chat.completion.chunk");
        json.WriteNumber("created", created);
        json.WriteString("model", model);
        json.WriteStartArray("choices");
        if (!usageOnly)
        {
            json.WriteStartObject();
            json.WriteNumber("index", 0);
            json.WriteStartObject("delta");
            json.WriteString("role", chunk.Role);
            json.WriteString("content", chunk.Delta);
            json.WritePropertyName("tool_calls");
            if (chunk.ToolCalls is not { } tools) json.WriteNullValue();
            else
            {
                json.WriteStartArray();
                for (var index = 0; index < tools.Count; index++)
                {
                    var tool = tools[index];
                    json.WriteStartObject();
                    json.WriteNumber("index", tool.Index);
                    json.WriteString("id", tool.Id);
                    json.WriteString("type", "function");
                    json.WriteStartObject("function");
                    json.WriteString("name", tool.Name);
                    json.WriteString("arguments", tool.Arguments ?? string.Empty);
                    json.WriteEndObject();
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }
            json.WriteString("reasoning_content", chunk.ReasoningDelta);
            json.WriteString("reasoning_signature", chunk.ReasoningSignature);
            json.WriteEndObject();
            json.WriteString("finish_reason", chunk.FinishReason is null ? null
                : ProtocolResponseWriter.NormalizeOpenAiFinishReason(chunk.FinishReason, chunk.ToolCalls is { Count: > 0 }));
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WritePropertyName("usage");
        if (chunk.Usage is not { } usage) json.WriteNullValue();
        else
        {
            json.WriteStartObject();
            json.WriteNumber("prompt_tokens", usage.PromptTokens);
            json.WriteNumber("completion_tokens", usage.CompletionTokens);
            json.WriteNumber("total_tokens", usage.TotalTokens);
            json.WriteEndObject();
        }
        json.WriteEndObject();
    }
    private static object CreateChatCompletionBody(AdapterCompletion completion, string fallbackModel)
    {
        var toolCalls = completion.ToolCalls ?? [];
        var message = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = "assistant",
            ["content"] = completion.Content
        };
        if (toolCalls.Count > 0)
        {
            message["tool_calls"] = toolCalls.Select((call, index) => new
            {
                id = string.IsNullOrWhiteSpace(call.Id) ? $"call_{index}" : call.Id,
                type = "function",
                function = new
                {
                    name = call.Name ?? string.Empty,
                    arguments = call.Arguments ?? "{}"
                }
            }).ToArray();
        }
        if (!string.IsNullOrEmpty(completion.ReasoningContent))
            message["reasoning_content"] = completion.ReasoningContent;
        if (!string.IsNullOrEmpty(completion.ReasoningSignature))
            message["reasoning_signature"] = completion.ReasoningSignature;

        return new
        {
            id = $"chatcmpl-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = string.IsNullOrWhiteSpace(completion.Model) ? fallbackModel : completion.Model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message,
                    finish_reason = ProtocolResponseWriter.NormalizeOpenAiFinishReason(
                        completion.FinishReason,
                        toolCalls.Count > 0)
                }
            },
            usage = completion.Usage is null ? null : new
            {
                prompt_tokens = completion.Usage.PromptTokens,
                completion_tokens = completion.Usage.CompletionTokens,
                total_tokens = completion.Usage.TotalTokens
            }
        };
    }
}
