using System.Diagnostics;
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
    ILogger<RouterPipeline> logger)
{
    public async Task ExecuteAsync(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stopwatch = Stopwatch.StartNew();
        var requestedModel = context.Request.Model;
        var started = false;
        string platform;
        string normalizedModel;
        try
        {
            platform = ResolvePlatform(requestedModel, out normalizedModel);
            context.SelectedPlatform = platform;
            context.PluginKey = platforms.Get(platform)?.PluginKey ?? platform;
            context.Request.Model = normalizedModel;
            context.Items["RequestedModel"] = requestedModel;
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
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
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
                metrics.OnStreamStarted(context.SelectedPlatform ?? "unknown");
                ((PluginResponseLifetime)context.Response.Lifetime!).OnCompleted(async completion =>
                {
                    metrics.OnStreamEnded(context.SelectedPlatform ?? "unknown");
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
                    StatusCode = context.Response?.StatusCode ?? 500,
                    DurationMs = (int)stopwatch.ElapsedMilliseconds,
                    Usage = usage,
                    AttemptDetails = context.AttemptDetails
                };
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
