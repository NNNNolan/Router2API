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

        var id = $"chatcmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await foreach (var chunk in response.Stream.WithCancellation(cancellationToken))
        {
            if (chunk == default) { await ProtocolResponseWriter.WriteHeartbeatAsync(context, cancellationToken); continue; }
            if (chunk.Error is { } error)
            {
                await context.Response.WriteAsync(
                    $"data: {JsonSerializer.Serialize(ProtocolApiErrors.OpenAi(error, StatusCodes.Status502BadGateway, chunk.ErrorType))}\n\n",
                    cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                break;
            }

            var usageOnly = chunk.Usage is not null
                && chunk.Delta is null
                && chunk.FinishReason is null
                && chunk.Role is null
                && chunk.ToolCalls is not { Count: > 0 }
                && chunk.ReasoningDelta is null
                && chunk.ReasoningSignature is null;
            object[] choices = usageOnly
                ? []
                :
                [
                    new
                    {
                        index = 0,
                        delta = new
                        {
                            role = chunk.Role,
                            content = chunk.Delta,
                            tool_calls = chunk.ToolCalls?.Select(toolCall => new
                            {
                                index = toolCall.Index,
                                id = toolCall.Id,
                                type = "function",
                                function = new
                                {
                                    name = toolCall.Name,
                                    arguments = toolCall.Arguments ?? string.Empty
                                }
                            }),
                            reasoning_content = chunk.ReasoningDelta,
                            reasoning_signature = chunk.ReasoningSignature
                        },
                        finish_reason = chunk.FinishReason is null
                            ? null
                            : ProtocolResponseWriter.NormalizeOpenAiFinishReason(
                                chunk.FinishReason,
                                chunk.ToolCalls is { Count: > 0 })
                    }
                ];

            var payload = new
            {
                id,
                @object = "chat.completion.chunk",
                created,
                model,
                choices,
                usage = chunk.Usage is null ? null : new
                {
                    prompt_tokens = chunk.Usage.PromptTokens,
                    completion_tokens = chunk.Usage.CompletionTokens,
                    total_tokens = chunk.Usage.TotalTokens
                }
            };

            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }

        await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
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
