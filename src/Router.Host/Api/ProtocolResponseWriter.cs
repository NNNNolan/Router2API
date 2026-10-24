using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Infrastructure.Services;

namespace Router.Host.Api;

/// <summary>
/// 将宿主的标准适配响应转换为公开端点使用的协议。
/// 插件无需关心调用方使用的是哪个 v1 端点。
/// </summary>
internal static class ProtocolResponseWriter
{
    public static async Task WriteAsync(
        HttpContext context,
        AdapterResponse response,
        TestEndpoint endpoint,
        string model,
        CancellationToken cancellationToken,
        AdapterRequest? request = null)
    {
        var lifetime = response.Lifetime as PluginResponseLifetime;
        if (response.Lifetime is { } owned) context.Response.RegisterForDisposeAsync(owned);
        try
        {
            lifetime?.BeginWrite();
            await WriteCoreAsync(context, response, endpoint, model, cancellationToken, request);
            lifetime?.CompleteWrite();
        }
        catch
        {
            lifetime?.Fail(cancellationToken.IsCancellationRequested,
                cancellationToken.IsCancellationRequested ? "request cancelled" : "response write failed");
            throw;
        }
        finally
        {
            if (response.Lifetime is { } cleanup) await cleanup.DisposeAsync();
        }
    }

    private static async Task WriteCoreAsync(
        HttpContext context,
        AdapterResponse response,
        TestEndpoint endpoint,
        string model,
        CancellationToken cancellationToken,
        AdapterRequest? request = null)
    {
        if (response.IsRawPassthrough)
        {
            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = response.ContentType
                ?? (response.RawStream is null ? "application/json" : "text/event-stream");
            if (response.RawContent is { Length: > 0 } rawContent)
                await context.Response.Body.WriteAsync(rawContent, cancellationToken);
            if (response.RawStream is { } rawStream)
            {
                var scan = RawCompletionScan.For(endpoint);
                var owner = response.Lifetime as PluginResponseLifetime;
                await foreach (var chunk in rawStream.WithCancellation(cancellationToken))
                {
                    await context.Response.Body.WriteAsync(chunk, cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                    // 仅在标记已经写进响应之后记账；扫描只读，不改变透传字节。
                    if (scan is not null && scan.Feed(chunk.Span)) owner?.CompleteRawRelay();
                }
            }

            return;
        }

        if (response.IsStreaming && response.Stream is not null && !response.IsSuccess)
        {
            context.Response.StatusCode = response.StatusCode;
            var error = endpoint == TestEndpoint.AnthropicMessages
                ? ProtocolApiErrors.Anthropic(response)
                : ProtocolApiErrors.OpenAi(response);
            await context.Response.WriteAsJsonAsync(error, cancellationToken);
            return;
        }

        if (endpoint == TestEndpoint.ChatCompletions)
        {
            await OpenAiResponseWriter.WriteAsync(context, response, model, cancellationToken);
            return;
        }

        if (endpoint == TestEndpoint.Completions)
        {
            await WriteLegacyCompletionsAsync(context, response, model, cancellationToken);
            return;
        }

        if (!response.IsStreaming || response.Stream is null)
        {
            await WriteCompletionAsync(context, response, endpoint, model, request, cancellationToken);
            return;
        }

        if (endpoint == TestEndpoint.Responses)
            await WriteResponsesStreamAsync(context, response, model, request, cancellationToken);
        else
            await WriteAnthropicStreamAsync(context, response, model, cancellationToken);
    }

    private static async Task WriteLegacyCompletionsAsync(
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
                await context.Response.WriteAsJsonAsync(
                    ProtocolApiErrors.OpenAi(response),
                    cancellationToken);
                return;
            }

            if (response.Completion is not { } completionResult)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsJsonAsync(
                    ProtocolApiErrors.OpenAi("plugin returned no completion result", StatusCodes.Status502BadGateway),
                    cancellationToken);
                return;
            }

            var completion = ReadCompletion(completionResult, model);
            await context.Response.WriteAsJsonAsync(
                CreateLegacyCompletionBody(completion),
                cancellationToken);
            return;
        }

        PrepareStream(context, response.StatusCode);
        using var writer = new SseJsonWriter(context.Response);
        var id = $"cmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await foreach (var chunk in response.Stream.WithCancellation(cancellationToken))
        {
            if (chunk == default) { await WriteHeartbeatAsync(context, cancellationToken); continue; }
            if (chunk.Error is { } error)
            {
                await writer.WriteAsync(ProtocolApiErrors.OpenAi(error, StatusCodes.Status502BadGateway, chunk.ErrorType), cancellationToken);
                break;
            }
            if (string.IsNullOrEmpty(chunk.Delta)
                && chunk.FinishReason is null
                && chunk.Usage is null)
                continue;

            var payload = new
            {
                id,
                @object = "text_completion",
                created,
                model,
                choices = new[]
                {
                    new
                    {
                        text = chunk.Delta ?? string.Empty,
                        index = 0,
                        logprobs = (object?)null,
                        finish_reason = chunk.FinishReason
                    }
                },
                usage = OpenAiUsage(chunk.Usage)
            };
            await writer.WriteAsync(payload, cancellationToken);
        }

        await WriteDoneAsync(context, cancellationToken);
    }

    private static async Task WriteCompletionAsync(
        HttpContext context,
        AdapterResponse response,
        TestEndpoint endpoint,
        string model,
        AdapterRequest? request,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = response.StatusCode;
        if (!response.IsSuccess)
        {
            await context.Response.WriteAsJsonAsync(
                endpoint == TestEndpoint.AnthropicMessages
                    ? ProtocolApiErrors.Anthropic(response)
                    : ProtocolApiErrors.OpenAi(response),
                cancellationToken);
            return;
        }

        if (response.Completion is not { } completionResult)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            var error = endpoint == TestEndpoint.AnthropicMessages
                ? ProtocolApiErrors.Anthropic(new AdapterResponse
                {
                    StatusCode = StatusCodes.Status502BadGateway,
                    Error = "plugin returned no completion result"
                })
                : ProtocolApiErrors.OpenAi("plugin returned no completion result", StatusCodes.Status502BadGateway);
            await context.Response.WriteAsJsonAsync(error, cancellationToken);
            return;
        }

        var completion = ReadCompletion(completionResult, model);
        byte[] body;
        try
        {
            body = endpoint == TestEndpoint.Responses
                ? OfficialApiResponseSerializer.SerializeResponses(CreateResponsesBody(completion, request))
                : OfficialApiResponseSerializer.SerializeAnthropic(CreateAnthropicBody(completion));
        }
        catch (Exception)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            var error = endpoint == TestEndpoint.Responses
                ? ProtocolApiErrors.OpenAi("plugin returned an invalid Responses result", StatusCodes.Status502BadGateway)
                : ProtocolApiErrors.Anthropic(new AdapterResponse
                {
                    StatusCode = StatusCodes.Status502BadGateway,
                    Error = "plugin returned an invalid Messages result"
                });
            await context.Response.WriteAsJsonAsync(error, cancellationToken);
            return;
        }
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body.WriteAsync(body, cancellationToken);
    }

    private static async Task WriteResponsesStreamAsync(
        HttpContext context,
        AdapterResponse response,
        string model,
        AdapterRequest? request,
        CancellationToken cancellationToken)
    {
        PrepareStream(context, response.StatusCode);
        using var writer = new SseJsonWriter(context.Response);
        var id = $"resp_{Guid.NewGuid():N}";
        var output = new SortedDictionary<int, object>();
        var tools = new SortedDictionary<int, ResponsesToolState>();
        var toolKinds = ReadResponsesToolKinds(request);
        var nextOutputIndex = 0;
        var sequence = 0;
        var text = new StringBuilder();
        var textItemId = $"msg_{Guid.NewGuid():N}";
        int? textOutputIndex = null;
        var reasoning = new StringBuilder();
        var reasoningItemId = $"rs_{Guid.NewGuid():N}";
        int? reasoningOutputIndex = null;
        var reasoningFinished = false;
        var finishReason = "stop";
        Usage? usage = null;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        async Task EmitAsync(string eventName, Dictionary<string, object?> payload)
        {
            payload["type"] = eventName;
            payload["sequence_number"] = sequence++;
            await WriteEventAsync(writer, eventName, payload, cancellationToken);
        }

        Dictionary<string, object?> Response(
            string status,
            object[] items,
            Usage? responseUsage = null,
            object? error = null,
            object? incompleteDetails = null)
            => CreateResponsesEnvelope(
                id,
                model,
                status,
                items,
                createdAt,
                request,
                responseUsage,
                completedAt: status is "completed" or "incomplete"
                    ? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    : null,
                error: error,
                incompleteDetails: incompleteDetails);

        await EmitAsync("response.created", new Dictionary<string, object?>
        {
            ["response"] = Response("in_progress", [])
        });
        await EmitAsync("response.in_progress", new Dictionary<string, object?>
        {
            ["response"] = Response("in_progress", [])
        });

        async Task StartTextAsync()
        {
            if (textOutputIndex is not null) return;
            await FinishReasoningAsync();
            textOutputIndex = nextOutputIndex++;
            await EmitAsync("response.output_item.added", new Dictionary<string, object?>
            {
                ["output_index"] = textOutputIndex.Value,
                ["item"] = new { id = textItemId, type = "message", status = "in_progress", role = "assistant", content = Array.Empty<object>() }
            });
            await EmitAsync("response.content_part.added", new Dictionary<string, object?>
            {
                ["item_id"] = textItemId,
                ["output_index"] = textOutputIndex.Value,
                ["content_index"] = 0,
                ["part"] = new { type = "output_text", text = "", annotations = Array.Empty<object>() }
            });
        }

        async Task StartReasoningAsync()
        {
            if (reasoningFinished)
            {
                reasoning.Clear();
                reasoningItemId = $"rs_{Guid.NewGuid():N}";
                reasoningOutputIndex = null;
                reasoningFinished = false;
            }
            if (reasoningOutputIndex is not null) return;
            reasoningOutputIndex = nextOutputIndex++;
            await EmitAsync("response.output_item.added", new Dictionary<string, object?>
            {
                ["output_index"] = reasoningOutputIndex.Value,
                ["item"] = new { id = reasoningItemId, type = "reasoning", status = "in_progress", summary = Array.Empty<object>() }
            });
            await EmitAsync("response.reasoning_summary_part.added", new Dictionary<string, object?>
            {
                ["item_id"] = reasoningItemId,
                ["output_index"] = reasoningOutputIndex.Value,
                ["summary_index"] = 0,
                ["part"] = new { type = "summary_text", text = "" }
            });
        }

        async Task FinishReasoningAsync()
        {
            if (reasoningOutputIndex is not { } index || reasoningFinished) return;
            reasoningFinished = true;
            var summaryPart = new { type = "summary_text", text = reasoning.ToString() };
            await EmitAsync("response.reasoning_summary_text.done", new Dictionary<string, object?>
            {
                ["item_id"] = reasoningItemId,
                ["output_index"] = index,
                ["summary_index"] = 0,
                ["text"] = reasoning.ToString()
            });
            await EmitAsync("response.reasoning_summary_part.done", new Dictionary<string, object?>
            {
                ["item_id"] = reasoningItemId,
                ["output_index"] = index,
                ["summary_index"] = 0,
                ["part"] = summaryPart
            });
            var reasoningItem = new { id = reasoningItemId, type = "reasoning", status = "completed", summary = new[] { summaryPart } };
            await EmitAsync("response.output_item.done", new Dictionary<string, object?>
            {
                ["output_index"] = index,
                ["item"] = reasoningItem
            });
            output[index] = reasoningItem;
        }

        await foreach (var chunk in response.Stream!.WithCancellation(cancellationToken))
        {
            if (chunk == default) { await WriteHeartbeatAsync(context, cancellationToken); continue; }
            usage = chunk.Usage ?? usage;
            finishReason = chunk.FinishReason ?? finishReason;
            if (chunk.Error is { } error)
            {
                await EmitAsync("response.failed", new Dictionary<string, object?>
                {
                    ["response"] = Response(
                        "failed",
                        output.Values.ToArray(),
                        usage,
                        new { code = chunk.ErrorType ?? "upstream_error", message = error })
                });
                return;
            }

            if (!string.IsNullOrEmpty(chunk.ReasoningDelta))
            {
                await StartReasoningAsync();
                reasoning.Append(chunk.ReasoningDelta);
                await EmitAsync("response.reasoning_summary_text.delta", new Dictionary<string, object?>
                {
                    ["item_id"] = reasoningItemId,
                    ["output_index"] = reasoningOutputIndex!.Value,
                    ["summary_index"] = 0,
                    ["delta"] = chunk.ReasoningDelta
                });
            }

            if (!string.IsNullOrEmpty(chunk.Delta))
            {
                await StartTextAsync();
                text.Append(chunk.Delta);
                await EmitAsync("response.output_text.delta", new Dictionary<string, object?>
                {
                    ["item_id"] = textItemId,
                    ["output_index"] = textOutputIndex!.Value,
                    ["content_index"] = 0,
                    ["delta"] = chunk.Delta
                });
            }

            if (chunk.ToolCalls is not { Count: > 0 }) continue;
            foreach (var delta in chunk.ToolCalls)
            {
                if (!tools.TryGetValue(delta.Index, out var tool))
                {
                    tool = new ResponsesToolState();
                    tools[delta.Index] = tool;
                }
                tool.CallId ??= delta.Id ?? $"call_{delta.Index}";
                tool.Name ??= delta.Name;
                if (!tool.Started && !string.IsNullOrWhiteSpace(tool.Name))
                {
                    tool.Kind = ResolveResponsesToolKind(toolKinds, tool.Name);
                    await FinishReasoningAsync();
                    tool.Started = true;
                    tool.OutputIndex = nextOutputIndex++;
                    tool.ItemId = tool.Kind == "custom"
                        ? CustomToolItemId(tool.CallId ?? $"call_{delta.Index}")
                        : $"fc_{Guid.NewGuid():N}";
                    var item = new Dictionary<string, object?>
                    {
                        ["id"] = tool.ItemId,
                        ["type"] = tool.Kind == "custom" ? "custom_tool_call" : "function_call",
                        ["status"] = "in_progress",
                        ["call_id"] = tool.CallId,
                        ["name"] = tool.Name,
                        [tool.Kind == "custom" ? "input" : "arguments"] = string.Empty
                    };
                    await EmitAsync("response.output_item.added", new Dictionary<string, object?>
                    {
                        ["output_index"] = tool.OutputIndex,
                        ["item"] = item
                    });
                    if (tool.Kind == "function" && tool.Arguments.Length > 0)
                        await EmitAsync("response.function_call_arguments.delta", new Dictionary<string, object?>
                        {
                            ["item_id"] = tool.ItemId,
                            ["output_index"] = tool.OutputIndex,
                            ["delta"] = tool.Arguments.ToString()
                        });
                }
                if (string.IsNullOrEmpty(delta.Arguments)) continue;
                tool.Arguments.Append(delta.Arguments);
                if (tool.Started)
                    await EmitAsync("response.function_call_arguments.delta", new Dictionary<string, object?>
                    {
                        ["item_id"] = tool.ItemId,
                        ["output_index"] = tool.OutputIndex,
                        ["delta"] = delta.Arguments
                    });
            }
        }

        var incompleteReason = ResponsesIncompleteReason(finishReason);
        var outputStatus = incompleteReason is null ? "completed" : "incomplete";
        if (textOutputIndex is { } textIndex)
        {
            var contentPart = new { type = "output_text", text = text.ToString(), annotations = Array.Empty<object>() };
            await EmitAsync("response.output_text.done", new Dictionary<string, object?>
            {
                ["item_id"] = textItemId,
                ["output_index"] = textIndex,
                ["content_index"] = 0,
                ["text"] = text.ToString()
            });
            await EmitAsync("response.content_part.done", new Dictionary<string, object?>
            {
                ["item_id"] = textItemId,
                ["output_index"] = textIndex,
                ["content_index"] = 0,
                ["part"] = contentPart
            });
            var textItem = new { id = textItemId, type = "message", status = outputStatus, role = "assistant", content = new[] { contentPart } };
            await EmitAsync("response.output_item.done", new Dictionary<string, object?>
            {
                ["output_index"] = textIndex,
                ["item"] = textItem
            });
            output[textIndex] = textItem;
        }

        await FinishReasoningAsync();

        foreach (var tool in tools.Values.Where(tool => tool.Started))
        {
            var arguments = tool.Arguments.Length == 0 ? "{}" : tool.Arguments.ToString();
            object item;
            if (tool.Kind == "custom")
            {
                var input = ChatArgumentsToCustomInput(tool.Name, arguments);
                if (input.Length > 0)
                    await EmitAsync("response.custom_tool_call_input.delta", new Dictionary<string, object?>
                    {
                        ["item_id"] = tool.ItemId,
                        ["output_index"] = tool.OutputIndex,
                        ["delta"] = input
                    });
                await EmitAsync("response.custom_tool_call_input.done", new Dictionary<string, object?>
                {
                    ["item_id"] = tool.ItemId,
                    ["output_index"] = tool.OutputIndex,
                    ["input"] = input
                });
                item = new
                {
                    id = tool.ItemId,
                    type = "custom_tool_call",
                    status = outputStatus,
                    call_id = tool.CallId,
                    name = tool.Name,
                    input
                };
            }
            else
            {
                await EmitAsync("response.function_call_arguments.done", new Dictionary<string, object?>
                {
                    ["item_id"] = tool.ItemId,
                    ["output_index"] = tool.OutputIndex,
                    ["arguments"] = arguments
                });
                item = new
                {
                    id = tool.ItemId,
                    type = "function_call",
                    status = outputStatus,
                    arguments,
                    call_id = tool.CallId,
                    name = tool.Name
                };
            }
            await EmitAsync("response.output_item.done", new Dictionary<string, object?>
            {
                ["output_index"] = tool.OutputIndex,
                ["item"] = item
            });
            output[tool.OutputIndex] = item;
        }

        var finalEvent = incompleteReason is null ? "response.completed" : "response.incomplete";
        var finalResponse = Response(
            outputStatus,
            output.Values.ToArray(),
            usage,
            incompleteDetails: incompleteReason is null ? null : new { reason = incompleteReason });
        await EmitAsync(finalEvent, new Dictionary<string, object?> { ["response"] = finalResponse });
    }

    private static async Task WriteAnthropicStreamAsync(
        HttpContext context,
        AdapterResponse response,
        string model,
        CancellationToken cancellationToken)
    {
        PrepareStream(context, response.StatusCode);
        using var writer = new SseJsonWriter(context.Response);
        var id = $"msg_{Guid.NewGuid():N}";
        Usage? usage = null;
        var nextBlockIndex = 0;
        int? textBlockIndex = null;
        int? reasoningBlockIndex = null;
        var finishReason = "stop";
        var tools = new SortedDictionary<int, AnthropicToolState>();

        await WriteEventAsync(writer, "message_start", new
        {
            type = "message_start",
            message = new
            {
                id,
                type = "message",
                role = "assistant",
                container = (object?)null,
                model,
                content = Array.Empty<object>(),
                stop_details = (object?)null,
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new
                {
                    cache_creation = (object?)null,
                    cache_creation_input_tokens = (long?)null,
                    cache_read_input_tokens = (long?)null,
                    inference_geo = (string?)null,
                    input_tokens = 0,
                    output_tokens = 0,
                    output_tokens_details = new { thinking_tokens = 0 },
                    server_tool_use = (object?)null,
                    service_tier = (string?)null
                }
            }
        }, cancellationToken);
        async Task CloseTextBlockAsync()
        {
            if (textBlockIndex is not { } index) return;
            await WriteEventAsync(writer, "content_block_stop", new { type = "content_block_stop", index }, cancellationToken);
            textBlockIndex = null;
        }

        async Task CloseReasoningBlockAsync()
        {
            if (reasoningBlockIndex is not { } index) return;
            await WriteEventAsync(writer, "content_block_stop", new { type = "content_block_stop", index }, cancellationToken);
            reasoningBlockIndex = null;
        }

        async Task StartTextBlockAsync()
        {
            if (textBlockIndex is not null) return;
            await CloseReasoningBlockAsync();
            textBlockIndex = nextBlockIndex++;
            await WriteEventAsync(writer, "content_block_start", new
            {
                type = "content_block_start",
                index = textBlockIndex.Value,
                content_block = new { type = "text", text = string.Empty }
            }, cancellationToken);
        }

        async Task StartReasoningBlockAsync()
        {
            if (reasoningBlockIndex is not null) return;
            await CloseTextBlockAsync();
            reasoningBlockIndex = nextBlockIndex++;
            await WriteEventAsync(writer, "content_block_start", new
            {
                type = "content_block_start",
                index = reasoningBlockIndex.Value,
                content_block = new { type = "thinking", thinking = "", signature = "" }
            }, cancellationToken);
        }

        await foreach (var chunk in response.Stream!.WithCancellation(cancellationToken))
        {
            if (chunk == default) { await WriteHeartbeatAsync(context, cancellationToken); continue; }
            usage = chunk.Usage ?? usage;
            if (chunk.Error is { } error)
            {
                await CloseTextBlockAsync();
                await CloseReasoningBlockAsync();
                await WriteEventAsync(writer, "error", ProtocolApiErrors.Anthropic(new AdapterResponse
                {
                    StatusCode = StatusCodes.Status502BadGateway,
                    Error = error,
                    ErrorType = chunk.ErrorType
                }), cancellationToken);
                return;
            }

            if (!string.IsNullOrEmpty(chunk.Delta))
            {
                await StartTextBlockAsync();
                await WriteEventAsync(writer, "content_block_delta", new
                {
                    type = "content_block_delta",
                    index = textBlockIndex!.Value,
                    delta = new { type = "text_delta", text = chunk.Delta }
                }, cancellationToken);
            }

            if (!string.IsNullOrEmpty(chunk.ReasoningDelta))
            {
                await StartReasoningBlockAsync();
                await WriteEventAsync(writer, "content_block_delta", new
                {
                    type = "content_block_delta",
                    index = reasoningBlockIndex!.Value,
                    delta = new { type = "thinking_delta", thinking = chunk.ReasoningDelta }
                }, cancellationToken);
            }
            if (!string.IsNullOrEmpty(chunk.ReasoningSignature))
            {
                await StartReasoningBlockAsync();
                await WriteEventAsync(writer, "content_block_delta", new
                {
                    type = "content_block_delta",
                    index = reasoningBlockIndex!.Value,
                    delta = new { type = "signature_delta", signature = chunk.ReasoningSignature }
                }, cancellationToken);
            }

            finishReason = chunk.FinishReason ?? finishReason;
            if (chunk.ToolCalls is not { Count: > 0 }) continue;
            foreach (var delta in chunk.ToolCalls)
            {
                if (!tools.TryGetValue(delta.Index, out var tool))
                {
                    tool = new AnthropicToolState();
                    tools[delta.Index] = tool;
                }
                tool.Id ??= delta.Id ?? $"call_{delta.Index}";
                tool.Name ??= delta.Name;
                if (!tool.Started && !string.IsNullOrWhiteSpace(tool.Name))
                {
                    await CloseTextBlockAsync();
                    await CloseReasoningBlockAsync();
                    tool.Started = true;
                    tool.BlockIndex = nextBlockIndex++;
                    await WriteEventAsync(writer, "content_block_start", new
                    {
                        type = "content_block_start",
                        index = tool.BlockIndex,
                        content_block = new { type = "tool_use", id = tool.Id, name = tool.Name, input = new { }, caller = new { type = "direct" } }
                    }, cancellationToken);
                    if (tool.Arguments.Length > 0)
                        await WriteEventAsync(writer, "content_block_delta", new
                        {
                            type = "content_block_delta",
                            index = tool.BlockIndex,
                            delta = new { type = "input_json_delta", partial_json = tool.Arguments.ToString() }
                        }, cancellationToken);
                }
                if (string.IsNullOrEmpty(delta.Arguments)) continue;
                tool.Arguments.Append(delta.Arguments);
                if (tool.Started)
                    await WriteEventAsync(writer, "content_block_delta", new
                    {
                        type = "content_block_delta",
                        index = tool.BlockIndex,
                        delta = new { type = "input_json_delta", partial_json = delta.Arguments }
                    }, cancellationToken);
            }
        }

        await CloseTextBlockAsync();
        await CloseReasoningBlockAsync();
        foreach (var tool in tools.Values.Where(tool => tool.Started))
            await WriteEventAsync(writer, "content_block_stop", new { type = "content_block_stop", index = tool.BlockIndex }, cancellationToken);
        await WriteEventAsync(writer, "message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = MapAnthropicStopReason(finishReason, tools.Values.Any(tool => tool.Started)), stop_sequence = (string?)null },
            usage = new
            {
                input_tokens = usage?.PromptTokens ?? 0,
                output_tokens = usage?.CompletionTokens ?? 0
            }
        }, cancellationToken);
        await WriteEventAsync(writer, "message_stop", new { type = "message_stop" }, cancellationToken);
    }

    internal static async Task WriteHeartbeatAsync(HttpContext context, CancellationToken cancellationToken)
    {
        await context.Response.WriteAsync(": keep-alive\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    private static void PrepareStream(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    /// <summary>
    /// 只读扫描 raw 透传字节，判断对应协议的结束标记是否已经中继给客户端。
    /// 不改动透传内容；调用方在标记之后断开时，宿主据此把请求记为已完成而不是取消。
    /// 没有已知结束标记的协议返回 null，维持原有取消判定。
    /// </summary>
    internal sealed class RawCompletionScan(byte[] marker)
    {
        private readonly byte[] _marker = marker;
        private readonly byte[] _keep = new byte[marker.Length - 1];
        private int _kept;
        private bool _seen;

        /// <summary>按当前端点取协议结束标记；如今只覆盖宿主自己也会生成的 SSE 结束帧。</summary>
        public static RawCompletionScan? For(TestEndpoint endpoint) => endpoint switch
        {
            TestEndpoint.ChatCompletions or TestEndpoint.Completions => new RawCompletionScan("data: [DONE]"u8.ToArray()),
            TestEndpoint.AnthropicMessages => new RawCompletionScan("event: message_stop"u8.ToArray()),
            TestEndpoint.Responses => new RawCompletionScan("event: response.completed"u8.ToArray()),
            _ => null
        };

        /// <summary>喂入一段已成功写出的字节；标记首次出现时返回 true。</summary>
        public bool Feed(ReadOnlySpan<byte> bytes)
        {
            if (_seen) return false;
            // 结束标记是固定的协议字节序列，最多 25 字节；只需复制分块边界，
            // 扫描大数据块时不复制其正文。
            Span<byte> boundary = stackalloc byte[2 * _marker.Length];
            _keep.AsSpan(0, _kept).CopyTo(boundary);
            var prefix = bytes[..Math.Min(bytes.Length, _marker.Length - 1)];
            prefix.CopyTo(boundary[_kept..]);
            if (bytes.IndexOf(_marker) >= 0 || boundary[..(_kept + prefix.Length)].IndexOf(_marker) >= 0)
            {
                _seen = true;
                return true;
            }
            var retained = Math.Min(_kept, Math.Max(0, _keep.Length - bytes.Length));
            _keep.AsSpan(_kept - retained, retained).CopyTo(_keep);
            var suffix = bytes[^Math.Min(bytes.Length, _keep.Length)..];
            suffix.CopyTo(_keep.AsSpan(retained));
            _kept = retained + suffix.Length;
            return false;
        }
    }

    private static string MapAnthropicStopReason(string? finishReason, bool hasToolCalls)
        => finishReason switch
        {
            "length" or "max_tokens" => "max_tokens",
            _ when hasToolCalls => "tool_use",
            "stop_sequence" => "stop_sequence",
            "pause_turn" => "pause_turn",
            "refusal" => "refusal",
            _ => "end_turn"
        };

    private static string? ResponsesIncompleteReason(string? finishReason)
        => finishReason switch
        {
            "length" or "max_tokens" => "max_output_tokens",
            "content_filter" => "content_filter",
            _ => null
        };

    private static ValueTask WriteEventAsync(
        SseJsonWriter writer,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
        => writer.WriteAsync(payload, cancellationToken, eventName);
    private static async Task WriteDoneAsync(HttpContext context, CancellationToken cancellationToken)
    {
        await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    private static CompletionSnapshot ReadCompletion(AdapterCompletion completion, string fallbackModel)
    {
        return new CompletionSnapshot(
            string.IsNullOrWhiteSpace(completion.Model) ? fallbackModel : completion.Model,
            completion.Content ?? string.Empty,
            "assistant",
            NormalizeOpenAiFinishReason(completion.FinishReason, completion.ToolCalls?.Count > 0),
            completion.Usage,
            completion.ToolCalls ?? [],
            completion.ReasoningContent,
            completion.ReasoningSignature);
    }

    private static Dictionary<string, object?> CreateResponsesBody(CompletionSnapshot completion, AdapterRequest? request)
    {
        var output = new List<object>();
        var incompleteReason = ResponsesIncompleteReason(completion.FinishReason);
        var outputStatus = incompleteReason is null ? "completed" : "incomplete";
        var toolKinds = ReadResponsesToolKinds(request);
        if (!string.IsNullOrEmpty(completion.Reasoning))
            output.Add(new
            {
                id = $"rs_{Guid.NewGuid():N}",
                type = "reasoning",
                status = "completed",
                summary = new[] { new { type = "summary_text", text = completion.Reasoning } }
            });
        if (!string.IsNullOrEmpty(completion.Text))
            output.Add(new
            {
                id = $"msg_{Guid.NewGuid():N}",
                type = "message",
                status = outputStatus,
                role = completion.Role,
                content = new[] { new { type = "output_text", text = completion.Text, annotations = Array.Empty<object>() } }
            });
        foreach (var call in completion.ToolCalls)
        {
            var callId = call.Id ?? $"call_{call.Index}";
            if (ResolveResponsesToolKind(toolKinds, call.Name) == "custom")
            {
                output.Add(new
                {
                    id = CustomToolItemId(callId),
                    type = "custom_tool_call",
                    status = outputStatus,
                    call_id = callId,
                    name = call.Name ?? string.Empty,
                    input = ChatArgumentsToCustomInput(call.Name, call.Arguments ?? "{}")
                });
            }
            else
            {
                output.Add(new
                {
                    id = $"fc_{Guid.NewGuid():N}",
                    type = "function_call",
                    status = outputStatus,
                    call_id = callId,
                    name = call.Name ?? string.Empty,
                    arguments = call.Arguments ?? "{}"
                });
            }
        }

        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return CreateResponsesEnvelope(
            $"resp_{Guid.NewGuid():N}",
            completion.Model,
            outputStatus,
            output.ToArray(),
            createdAt,
            request,
            completion.Usage,
            completedAt: createdAt,
            incompleteDetails: incompleteReason is null ? null : new { reason = incompleteReason });
    }

    private static Dictionary<string, object?> CreateResponsesEnvelope(
        string id,
        string model,
        string status,
        object[] output,
        long createdAt,
        AdapterRequest? request,
        Usage? usage,
        long? completedAt = null,
        object? error = null,
        object? incompleteDetails = null)
        => new(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["object"] = "response",
            ["created_at"] = createdAt,
            ["status"] = status,
            ["completed_at"] = completedAt,
            ["access_programs"] = null,
            ["background"] = RequestBoolean(request, "background") ?? false,
            ["conversation"] = RequestOption(request, "conversation"),
            ["error"] = error,
            ["incomplete_details"] = incompleteDetails,
            ["instructions"] = RequestOption(request, "instructions"),
            ["max_output_tokens"] = request?.MaxTokens,
            ["max_tool_calls"] = RequestOption(request, "max_tool_calls"),
            ["model"] = model,
            ["output"] = output,
            ["parallel_tool_calls"] = RequestBoolean(request, "parallel_tool_calls") ?? true,
            ["previous_response_id"] = RequestOption(request, "previous_response_id"),
            ["prompt"] = RequestOption(request, "prompt"),
            ["prompt_cache_key"] = RequestOption(request, "prompt_cache_key"),
            ["prompt_cache_retention"] = RequestOption(request, "prompt_cache_retention"),
            ["reasoning"] = RequestOption(request, "reasoning")
                ?? new { effort = (string?)null, summary = (string?)null },
            ["safety_identifier"] = RequestOption(request, "safety_identifier"),
            ["store"] = RequestBoolean(request, "store") ?? false,
            ["temperature"] = request?.Temperature ?? 1.0,
            ["text"] = RequestOption(request, "text") ?? new { format = new { type = "text" } },
            ["top_logprobs"] = RequestOption(request, "top_logprobs"),
            ["tool_choice"] = RequestOption(request, "tool_choice") ?? "auto",
            ["tools"] = request?.Tools ?? [],
            ["top_p"] = RequestOption(request, "top_p") ?? 1.0,
            ["truncation"] = RequestOption(request, "truncation") ?? "disabled",
            ["usage"] = ResponsesUsage(usage),
            ["user"] = RequestOption(request, "user"),
            ["metadata"] = RequestOption(request, "metadata") ?? new Dictionary<string, string>()
        };

    private static object? RequestOption(AdapterRequest? request, string name)
    {
        if (request is null) return null;
        return request.Extensions.TryGetValue(name, out var value) ? value : null;
    }

    private static bool? RequestBoolean(AdapterRequest? request, string name)
    {
        var value = RequestOption(request, name);
        return value switch
        {
            bool flag => flag,
            JsonElement element when element.ValueKind is JsonValueKind.True or JsonValueKind.False
                => element.GetBoolean(),
            JsonValue jsonValue when jsonValue.TryGetValue<bool>(out var jsonFlag) => jsonFlag,
            _ => null
        };
    }

    private static Dictionary<string, string> ReadResponsesToolKinds(AdapterRequest? request)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request is null) return result;
        foreach (var rawTool in request.Tools)
        {
            try
            {
                var tool = rawTool is JsonElement element
                    ? element
                    : JsonSerializer.SerializeToElement(rawTool);
                AddResponsesToolKinds(tool, result);
            }
            catch (JsonException)
            {
                // 忽略格式错误的扩展工具元数据，仍可使用默认的函数调用结构
                // 返回该工具调用。
            }
        }
        return result;
    }

    private static void AddResponsesToolKinds(JsonElement tool, Dictionary<string, string> result)
    {
        if (tool.ValueKind != JsonValueKind.Object) return;
        var type = tool.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
        if (string.Equals(type, "namespace", StringComparison.OrdinalIgnoreCase))
        {
            if (tool.TryGetProperty("tools", out var nestedTools) && nestedTools.ValueKind == JsonValueKind.Array)
                foreach (var nestedTool in nestedTools.EnumerateArray())
                    AddResponsesToolKinds(nestedTool, result);
            return;
        }

        var source = tool;
        string? resolvedType = null;
        if (tool.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
        {
            source = function;
            resolvedType = string.Equals(type, "custom", StringComparison.OrdinalIgnoreCase)
                ? "custom"
                : "function";
        }
        else if (string.Equals(type, "custom", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(type, "function", StringComparison.OrdinalIgnoreCase))
        {
            resolvedType = type!.ToLowerInvariant();
        }
        if (resolvedType is null
            || !source.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
            return;

        var name = nameElement.GetString();
        if (!string.IsNullOrWhiteSpace(name)) result.TryAdd(name, resolvedType);
    }

    private static string ResolveResponsesToolKind(Dictionary<string, string> toolKinds, string? name)
        => !string.IsNullOrWhiteSpace(name) && toolKinds.TryGetValue(name, out var type)
            ? type
            : string.Equals(name, "exec", StringComparison.Ordinal) ? "custom" : "function";

    private static string CustomToolItemId(string callId)
        => callId.StartsWith("ctc_", StringComparison.Ordinal) ? callId : $"ctc_{callId}";

    private static string ChatArgumentsToCustomInput(string? name, string arguments)
    {
        var trimmed = arguments.Trim();
        if (!string.Equals(name, "exec", StringComparison.Ordinal) || LooksLikeExecJavascript(trimmed))
            return arguments;

        JsonNode? parsed = null;
        try { parsed = JsonNode.Parse(trimmed); }
        catch (JsonException) { }

        if (parsed is JsonValue value && value.TryGetValue<string>(out var nestedText))
        {
            if (LooksLikeExecJavascript(nestedText)) return nestedText;
            try { parsed = JsonNode.Parse(nestedText); }
            catch (JsonException) { }
        }

        if (parsed is JsonObject obj)
        {
            if (obj["command"] is JsonValue commandNode
                && commandNode.TryGetValue<string>(out var command))
            {
                if (LooksLikeExecJavascript(command)) return command;
                return BuildExecToolInput(JsonSerializer.Serialize(new { command }));
            }
            return BuildExecToolInput(obj.ToJsonString());
        }
        return !string.IsNullOrWhiteSpace(trimmed) && parsed is not JsonArray
            ? BuildExecToolInput(JsonSerializer.Serialize(new { command = arguments }))
            : arguments;
    }

    private static bool LooksLikeExecJavascript(string value)
        => !string.IsNullOrWhiteSpace(value)
            && !value.TrimStart().StartsWith('{')
            && !value.TrimStart().StartsWith('[')
            && value.Contains("tools.", StringComparison.Ordinal)
            && (value.Contains("await ", StringComparison.Ordinal) || value.Contains("text(", StringComparison.Ordinal));

    private static string BuildExecToolInput(string argumentJson)
        => $"const result = await tools.shell_command({argumentJson});\ntext(result);\n";

    private static object CreateLegacyCompletionBody(CompletionSnapshot completion)
        => new
        {
            id = $"cmpl-{Guid.NewGuid():N}",
            @object = "text_completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = completion.Model,
            choices = new[]
            {
                new
                {
                    text = completion.Text,
                    index = 0,
                    logprobs = (object?)null,
                    finish_reason = completion.FinishReason
                }
            },
            usage = OpenAiUsage(completion.Usage)
        };

    private static object CreateAnthropicBody(CompletionSnapshot completion)
    {
        var content = new List<object>();
        if (!string.IsNullOrEmpty(completion.Reasoning))
            content.Add(new
            {
                type = "thinking",
                thinking = completion.Reasoning,
                signature = completion.ReasoningSignature ?? string.Empty
            });
        if (!string.IsNullOrEmpty(completion.Text))
            content.Add(new { type = "text", text = completion.Text });
        foreach (var call in completion.ToolCalls)
        {
            var input = ParseToolInput(call.Arguments);
            if (input is null)
            {
                // Messages 的非流式 input 必须是完整对象，不能把截断参数改写成可执行的 {}。
                if (completion.FinishReason is "length" or "max_tokens") continue;
                throw new JsonException("tool input must be a complete JSON object");
            }
            content.Add(new
            {
                type = "tool_use",
                id = call.Id ?? $"call_{call.Index}",
                name = call.Name ?? string.Empty,
                input,
                caller = new { type = "direct" }
            });
        }

        return new
        {
            id = $"msg_{Guid.NewGuid():N}",
            type = "message",
            role = completion.Role,
            container = (object?)null,
            model = completion.Model,
            content,
            stop_details = (object?)null,
            stop_reason = MapAnthropicStopReason(
                completion.FinishReason,
                completion.ToolCalls.Count > 0),
            stop_sequence = (string?)null,
            usage = new
            {
                cache_creation = (object?)null,
                cache_creation_input_tokens = (long?)null,
                cache_read_input_tokens = (long?)null,
                inference_geo = (string?)null,
                input_tokens = completion.Usage?.PromptTokens ?? 0,
                output_tokens = completion.Usage?.CompletionTokens ?? 0,
                output_tokens_details = new { thinking_tokens = 0 },
                server_tool_use = (object?)null,
                service_tier = (string?)null
            }
        };
    }

    internal static string NormalizeOpenAiFinishReason(string? finishReason, bool hasToolCalls)
        => finishReason switch
        {
            "length" or "max_tokens" => "length",
            "content_filter" => "content_filter",
            "tool_calls" or "function_call" when hasToolCalls => "tool_calls",
            "stop" or "end_turn" or "completed" => hasToolCalls ? "tool_calls" : "stop",
            _ => hasToolCalls ? "tool_calls" : "stop"
        };

    private static JsonObject? ParseToolInput(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;
        try
        {
            return JsonNode.Parse(arguments) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object? ResponsesUsage(Usage? usage)
        => usage is null
            ? null
            : new
            {
                input_tokens = usage.PromptTokens,
                input_tokens_details = new { cached_tokens = 0, cache_write_tokens = 0 },
                output_tokens = usage.CompletionTokens,
                output_tokens_details = new { reasoning_tokens = 0 },
                total_tokens = usage.TotalTokens
            };

    private static object? OpenAiUsage(Usage? usage)
        => usage is null
            ? null
            : new
            {
                prompt_tokens = usage.PromptTokens,
                completion_tokens = usage.CompletionTokens,
                total_tokens = usage.TotalTokens
            };

    private sealed class ResponsesToolState
    {
        public int OutputIndex { get; set; }
        public string? ItemId { get; set; }
        public string? CallId { get; set; }
        public string? Name { get; set; }
        public string Kind { get; set; } = "function";
        public StringBuilder Arguments { get; } = new();
        public bool Started { get; set; }
    }

    private sealed class AnthropicToolState
    {
        public int BlockIndex { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
        public bool Started { get; set; }
    }

    private sealed record CompletionSnapshot(
        string Model,
        string Text,
        string Role,
        string FinishReason,
        Usage? Usage,
        IReadOnlyList<AdapterToolCall> ToolCalls,
        string? Reasoning,
        string? ReasoningSignature);
}
