using System.Diagnostics;
using Microsoft.Extensions.Options;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Infrastructure.Services;
using Router.Infrastructure.Services.Privacy;
using Router.Host.Privacy;

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
    IOptions<PluginExecutionOptions>? executionOptions = null,
    PrivacyRequestGuard? privacy = null)
{
    public async Task ExecuteAsync(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = usageBuckets; // 保留历史构造签名及 DI 注册兼容，不再在请求完成热路径写入旧聚合表。
        var stopwatch = Stopwatch.StartNew();
        var requestedModel = context.Request.Model;
        var trace = new RequestTrace(context.Request.Endpoint);
        var hasRouted = router.TryResolve(requestedModel, out var routedPlatform, out var routedModel);
        var routedPluginKey = hasRouted ? platforms.Get(routedPlatform)?.PluginKey ?? routedPlatform : null;
        var httpContext = context.Services?.GetService(typeof(IHttpContextAccessor)) as IHttpContextAccessor;
        var isLoopback = httpContext?.HttpContext is { } current && RawConnection.IsLoopback(current);
        PrivacyInspection? inspection = null;
        PrivacyReport? privacyReport = null;
        var started = false;
        var cancelled = false;
        RequestUsageTracker? usageTracker = null;
        string platform;
        string normalizedModel;
        try
        {
            using (var stage = RequestTrace.Measure("privacy"))
            {
                inspection = privacy?.Begin(context.Request, routedPlatform, routedPluginKey, isLoopback);
                if (inspection is not null)
                {
                    try { context.Request = PrivacyRequestGuard.Apply(context.Request, inspection); }
                    catch { stage?.SetResult("failed", 500); throw; }
                    privacyReport = inspection.Report();
                    stage?.SetResult(inspection.Blocked ? "blocked" : inspection.DryRun ? "dry_run" : "completed",
                        inspection.Blocked ? 403 : null);
                    if (inspection.Blocked)
                    {
                        context.Response = new AdapterResponse { StatusCode = 403, Error = "request blocked by privacy guard", ErrorType = "privacy_guard_blocked" };
                        return;
                    }
                }
                else stage?.SetResult("disabled");
            }
            PrivacyForwarding.Clear(context.Request);
            context.Items["RequestedModel"] = requestedModel;
            ModelMapping? mapping;
            using (RequestTrace.Measure("routing"))
                mapping = modelMappings?.Find(requestedModel);
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
            using (RequestTrace.Measure("routing"))
            {
                if (hasRouted)
                {
                    platform = routedPlatform;
                    normalizedModel = routedModel;
                }
                else platform = ResolvePlatform(requestedModel, out normalizedModel);
                context.SelectedPlatform = platform;
                context.PluginKey = platforms.Get(platform)?.PluginKey ?? platform;
                context.Request.Model = normalizedModel;
            }
            metrics.OnRequestStarted(platform);
            started = true;

            var registration = platforms.Get(platform)
                ?? throw new InvalidOperationException($"platform '{platform}' is not registered");
            var relaySource = privacy?.IsWhitelisted(platform, registration.PluginKey) == true;
            if (relaySource)
                PrivacyForwarding.Mark(context.Request,
                    privacy!.IsWhitelisted(platform, null) ? platform : registration.PluginKey);
            PluginInvocationResult result;
            try
            {
                result = await attempts.ExecuteAsync(
                    registration.PluginKey,
                    platform,
                    registration.Terminal,
                    context.Request,
                    context.TraceId,
                    context.TestOverrides,
                    context.CancellationToken);
            }
            finally
            {
                if (relaySource) PrivacyForwarding.Clear(context.Request);
            }
            context.Response = result.Response;
            context.AttemptDetails = result.AttemptDetails ?? [];
            usageTracker = new RequestUsageTracker(context.Request, result.Response);
        }
        catch (PrivacyInspectionException)
        {
            privacyReport = inspection?.Report();
            context.Response = AdapterResponse.ServerError("privacy guard inspection failed");
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
            if (inspection is not null && !inspection.Failed && inspection.RestoreMap.Count > 0 && !inspection.Blocked && context.Response is { } guarded)
            {
                using var stage = RequestTrace.Measure("response.restore");
                try { context.Response = PrivacyResponseRestorer.Restore(guarded, inspection.RestoreMap, context.Request.Endpoint); }
                catch (PrivacyInspectionException)
                {
                    stage?.SetResult("failed", 502);
                    if (guarded.Lifetime is PluginResponseLifetime owner) owner.Fail(false, "privacy response restoration failed");
                    if (guarded.Lifetime is { } cleanup) await cleanup.DisposeAsync();
                    context.Response = new AdapterResponse { StatusCode = 502, Error = "privacy response restoration failed" };
                }
            }
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
                if (inspection is not null)
                    ((PluginResponseLifetime)context.Response.Lifetime!).AddResource(() => { inspection.Dispose(); return ValueTask.CompletedTask; });
            }
            else
            {
                await CompleteAsync(context.Response?.IsSuccess == true, context.Response?.Usage);
                inspection?.Dispose();
            }
            trace.Detach();
        }

        async ValueTask CompleteAsync(bool success, Usage? usage)
        {
            var observation = new RequestObservation(privacyReport, trace.Complete());
            trace.Dispose();
            stopwatch.Stop();
            var usageStarted = Stopwatch.GetTimestamp();
            UsageMeasurement? measurement = null;
            var usageOutcome = "failed";
            try
            {
                measurement = usageTracker?.Finish(success);
                usageOutcome = measurement is null ? "skipped" : measurement.Metadata.Warning is null ? "completed" : "incomplete";
            }
            finally
            {
                logger.LogDebug("usage settlement finished traceId={TraceId} durationMs={DurationMs} outcome={Outcome}",
                    context.TraceId, Stopwatch.GetElapsedTime(usageStarted).TotalMilliseconds, usageOutcome);
            }
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
            var persistenceStarted = Stopwatch.GetTimestamp();
            var persistenceOutcome = "failed";
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
                    await persistentLogs.WriteObservedAsync(log, measurement?.Metadata, observation);
                else
                {
                    await requestLogs.WriteAsync(log);
                }
                persistenceOutcome = "completed";
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "request log persistence failed traceId={TraceId}", context.TraceId);
            }
            finally
            {
                logger.LogDebug("request log persistence finished traceId={TraceId} durationMs={DurationMs} outcome={Outcome}",
                    context.TraceId, Stopwatch.GetElapsedTime(persistenceStarted).TotalMilliseconds, persistenceOutcome);
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
            using var targetStage = RequestTrace.Measure("mapping.target", target.PluginKey, target.Platform, target.Model);
            context.CancellationToken.ThrowIfCancellationRequested();
            var label = $"{target.PluginKey}/{target.Platform}/{target.Model}";
            var registration = platforms.Get(target.Platform);
            if (registration is null || !registration.Enabled || !registration.PluginKey.Equals(target.PluginKey, StringComparison.OrdinalIgnoreCase))
            {
                targetStage?.SetResult("unavailable");
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
                targetStage?.SetResult("cooldown");
                details.Add(new(null, "unbound", null, "Skipped", false,
                    $"{label}: mapping target cooling down until {cooldownUntil:O}", 0));
                context.AttemptDetails = details.ToArray();
                continue;
            }
            context.SelectedPlatform = registration.Name;
            context.PluginKey = registration.PluginKey;
            executedRequest = ModelMappingRequest.Create(context.Request, target.Model);
            var relaySource = privacy?.IsWhitelisted(target.Platform, target.PluginKey) == true;
            if (relaySource)
                PrivacyForwarding.Mark(executedRequest,
                    privacy!.IsWhitelisted(target.Platform, null) ? target.Platform : target.PluginKey);
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
            finally
            {
                if (relaySource) PrivacyForwarding.Clear(executedRequest);
            }
            var targetDetails = result.AttemptDetails ?? [];
            AddDetails(targetDetails, label);
            if (targetDetails.Count == 0)
                details.Add(new(null, "unbound", result.Response.StatusCode, result.Attempt.Outcome.ToString(), false,
                    $"{label}: {result.Attempt.Reason ?? result.Response.Error}", 0));
            context.AttemptDetails = details.ToArray();
            var response = result.Response;
            targetStage?.SetResult(response.IsSuccess ? "completed" : "failed", response.StatusCode);
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
