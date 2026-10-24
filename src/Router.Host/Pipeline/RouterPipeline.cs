using System.Diagnostics;
using Microsoft.Extensions.Options;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Infrastructure.Services;

namespace Router.Host.Pipeline;

/// <summary>解析模型并把资源选择、代理和重试交给宿主执行器。</summary>
public sealed class RouterPipeline(
    IModelRouter router,
    IPlatformRegistry platforms,
    IPluginAttemptExecutor attempts,
    IRealtimeMetrics metrics,
    ILogSink<RequestLog> requestLogs,
    IUsageBucketStore usageBuckets,
    ILogger<RouterPipeline> logger,
    ModelMappingHealth mappingHealth,
    ModelMappingStore? modelMappings = null,
    PluginAttemptExecutor? mappedAttempts = null,
    IOptions<PluginExecutionOptions>? executionOptions = null)
{
    public async Task ExecuteAsync(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stopwatch = Stopwatch.StartNew();
        var requestedModel = context.Request.Model;
        var started = false;
        var cancelled = false;
        RequestUsageTracker? usageTracker = null;
        string platform;
        string normalizedModel;
        try
        {
            context.Items["RequestedModel"] = requestedModel;
            var mapping = modelMappings?.Find(requestedModel);
            if (mapping is not null)
            {
                if (!mapping.Enabled)
                {
                    context.Response = new AdapterResponse { StatusCode = 404, Error = "model mapping is disabled", ErrorType = "invalid_request_error" };
                    return;
                }
                metrics.OnRequestStarted(mapping.Targets.FirstOrDefault(target => target.Enabled)?.Platform ?? "unknown");
                started = true;
                var executedRequest = await ExecuteMappingAsync(context, mapping);
                usageTracker = new RequestUsageTracker(executedRequest, context.Response!);
                return;
            }
            platform = ResolvePlatform(requestedModel, out normalizedModel);
            context.SelectedPlatform = platform;
            context.PluginKey = platforms.Get(platform)?.PluginKey ?? platform;
            context.Request.Model = normalizedModel;
            metrics.OnRequestStarted(platform);
            started = true;

            var registration = platforms.Get(platform)
                ?? throw new InvalidOperationException($"platform '{platform}' is not registered");
            var result = await attempts.ExecuteAsync(
                registration.PluginKey,
                platform,
                registration.Terminal,
                context.Request,
                context.TraceId,
                context.TestOverrides,
                context.CancellationToken);
            context.Response = result.Response;
            context.AttemptDetails = result.AttemptDetails ?? [];
            usageTracker = new RequestUsageTracker(context.Request, result.Response);
        }
        catch (OperationCanceledException exception) when (context.CancellationToken.IsCancellationRequested)
        {
            cancelled = true;
            if (exception is PluginRequestCancelledException interrupted)
                context.AttemptDetails = interrupted.AttemptDetails;
            context.Response = AdapterResponse.ServerError("request cancelled");
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "router pipeline failed traceId={TraceId}", context.TraceId);
            context.Response = AdapterResponse.ServerError(exception.Message);
            platform = context.SelectedPlatform ?? "unknown";
        }
        finally
        {
            if (context.Response is { } response && PluginResponseLifetime.HasStream(response))
            {
                context.Response = PluginResponseLifetime.Ensure(response);
                if (usageTracker is not null) ((PluginResponseLifetime)context.Response.Lifetime!).TrackUsage(usageTracker);
                metrics.OnStreamStarted(context.SelectedPlatform ?? "unknown");
                ((PluginResponseLifetime)context.Response.Lifetime!).OnCompleted(async completion =>
                {
                    metrics.OnStreamEnded(context.SelectedPlatform ?? "unknown");
                    cancelled = completion.Cancelled;
                    await CompleteAsync(completion.Success, completion.Usage);
                });
            }
            else
            {
                await CompleteAsync(context.Response?.IsSuccess == true, context.Response?.Usage);
            }
        }

        async ValueTask CompleteAsync(bool success, Usage? usage)
        {
            stopwatch.Stop();
            var measurement = usageTracker?.Finish(success);
            usage = measurement is null ? usage : measurement.Usage;
            if (measurement?.Metadata.Warning is { } warning)
                logger.LogWarning("usage observation incomplete traceId={TraceId} reason={Reason}", context.TraceId, warning);
            var resolvedPlatform = context.SelectedPlatform ?? "unknown";
            if (!started) metrics.OnRequestStarted(resolvedPlatform);
            context.Items["DurationMs"] = (int)stopwatch.ElapsedMilliseconds;
            metrics.OnRequestCompleted(
                resolvedPlatform,
                (int)stopwatch.ElapsedMilliseconds,
                success,
                usage);
            try
            {
                var log = new RequestLog
                {
                    TraceId = context.TraceId,
                    Platform = resolvedPlatform,
                    Model = requestedModel,
                    Success = success,
                    Cancelled = cancelled,
                    StatusCode = context.Response?.StatusCode ?? 500,
                    DurationMs = (int)stopwatch.ElapsedMilliseconds,
                    Usage = usage,
                    AttemptDetails = context.AttemptDetails
                };
                if (requestLogs is RequestLogStore persistentLogs)
                    await persistentLogs.WriteAsync(log, measurement?.Metadata);
                else
                    await requestLogs.WriteAsync(log);
                try { await usageBuckets.RecordAsync(log); }
                catch (Exception exception)
                {
                    logger.LogError(exception, "usage bucket persistence failed traceId={TraceId}", context.TraceId);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "request log persistence failed traceId={TraceId}", context.TraceId);
            }
        }
    }

    private async Task<AdapterRequest> ExecuteMappingAsync(RequestContext context, ModelMapping mapping)
    {
        if (mappedAttempts is null) throw new InvalidOperationException("Mapped model executor is not configured.");
        var stopwatch = Stopwatch.StartNew();
        var budget = (executionOptions?.Value ?? new PluginExecutionOptions()).TotalSetupTimeout;
        var details = new List<RequestAttemptDetail>();
        var executedRequest = context.Request;
        foreach (var target in mapping.Targets.Where(target => target.Enabled))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var label = $"{target.PluginKey}/{target.Platform}/{target.Model}";
            var registration = platforms.Get(target.Platform);
            if (registration is null || !registration.Enabled || !registration.PluginKey.Equals(target.PluginKey, StringComparison.OrdinalIgnoreCase))
            {
                details.Add(new(null, "unbound", null, "Skipped", false, $"{label}: target unavailable", 0));
                context.AttemptDetails = details.ToArray();
                continue;
            }
            var remaining = budget - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                context.Response = new AdapterResponse { StatusCode = 504, Error = "model mapping setup timed out" };
                break;
            }
            var invocation = mappingHealth.TryBegin(mapping.Id, target, out var cooldownUntil);
            if (invocation is null)
            {
                details.Add(new(null, "unbound", null, "Skipped", false,
                    $"{label}: mapping target cooling down until {cooldownUntil:O}", 0));
                context.AttemptDetails = details.ToArray();
                continue;
            }
            context.SelectedPlatform = registration.Name;
            context.PluginKey = registration.PluginKey;
            executedRequest = ModelMappingRequest.Create(context.Request, target.Model);
            PluginInvocationResult result;
            try
            {
                result = await mappedAttempts.ExecuteMappedAsync(
                    registration.PluginKey, registration.Name, registration.Terminal, executedRequest, context.TraceId,
                    context.TestOverrides, target.RetryCount, remaining, context.CancellationToken);
            }
            catch (PluginRequestCancelledException exception)
            {
                AddDetails(exception.AttemptDetails, label);
                throw new PluginRequestCancelledException(exception, details.ToArray(), context.CancellationToken);
            }
            catch (Exception) when (!context.CancellationToken.IsCancellationRequested)
            {
                CompleteMappingTarget(invocation, mapping, target, false);
                throw;
            }
            var targetDetails = result.AttemptDetails ?? [];
            AddDetails(targetDetails, label);
            if (targetDetails.Count == 0)
                details.Add(new(null, "unbound", result.Response.StatusCode, result.Attempt.Outcome.ToString(), false,
                    $"{label}: {result.Attempt.Reason ?? result.Response.Error}", 0));
            context.AttemptDetails = details.ToArray();
            var response = result.Response;
            var hasStream = PluginResponseLifetime.HasStream(response);
            var clientInputError = response.StatusCode is 400 or 413 or 415 or 422;
            context.Response = response;
            if (!clientInputError)
            {
                if (hasStream)
                {
                    context.Response = PluginResponseLifetime.Ensure(response);
                    ((PluginResponseLifetime)context.Response.Lifetime!).OnCompleted(completion =>
                    {
                        if (completion.Success || !completion.Cancelled && !context.CancellationToken.IsCancellationRequested)
                            CompleteMappingTarget(invocation, mapping, target, completion.Success);
                        return ValueTask.CompletedTask;
                    });
                }
                else if (!context.CancellationToken.IsCancellationRequested)
                    CompleteMappingTarget(invocation, mapping, target, response.IsSuccess);
            }
            if (response.IsSuccess || hasStream || clientInputError
                || (result.Attempt.Decision?.Retry != PluginRetryAction.NextAttempt && result.Attempt.Decision?.ReasonCode != "no-account"))
                return executedRequest;

            // 失败响应在切换前释放；已经返回的流永远不会进入这一分支。
            if (result.Response.Lifetime is { } lifetime) await lifetime.DisposeAsync();
            if (stopwatch.Elapsed >= budget)
            {
                context.Response = new AdapterResponse { StatusCode = 504, Error = "model mapping setup timed out" };
                break;
            }
        }
        context.Response ??= new AdapterResponse { StatusCode = 503, Error = "no available target for model mapping" };
        return executedRequest;

        void AddDetails(IReadOnlyList<RequestAttemptDetail> attemptsForTarget, string label)
            => details.AddRange(attemptsForTarget.Select(detail => detail with { Reason = $"{label}: {detail.Reason ?? detail.Outcome}" }));
    }

    private void CompleteMappingTarget(ModelMappingHealth.Invocation invocation, ModelMapping mapping, ModelMappingTarget target, bool success)
    {
        if (invocation.Complete(success) is { } until)
            logger.LogWarning("model mapping target cooled down mapping={Mapping} pluginKey={PluginKey} platform={Platform} model={Model} until={CooldownUntil}",
                mapping.Name, target.PluginKey, target.Platform, target.Model, until);
    }

    private string ResolvePlatform(string model, out string normalizedModel)
    {
        if (router.TryResolve(model, out var platform, out normalizedModel)) return platform;
        var enabledPlatforms = platforms.All.Where(item => item.Enabled).ToArray();
        if (enabledPlatforms.Length == 1)
        {
            normalizedModel = model;
            return enabledPlatforms[0].Name;
        }

        throw new InvalidOperationException("model must use the platform/model format");
    }
}
