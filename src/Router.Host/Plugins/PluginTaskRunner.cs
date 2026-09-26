using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Router.Host.Plugins;

/// <summary>统一的定时/手动插件任务执行器，所有入口共用任务锁和插件日志。</summary>
public sealed class PluginTaskRunner(
    IPlatformRegistry platforms,
    IPluginLogSink pluginLogs,
    ISharedKeyValueStore runtimeState,
    ILogger<PluginTaskRunner> logger) : IPluginTaskInvoker
{
    public async Task<bool> RunAsync(
        string pluginKey,
        string taskName,
        string? platformName = null,
        CancellationToken cancellationToken = default)
    {
        var registration = platforms.All.FirstOrDefault(item =>
            item.Enabled
            && item.PluginKey.Equals(pluginKey, StringComparison.OrdinalIgnoreCase)
            && (platformName is null || item.Name.Equals(platformName, StringComparison.OrdinalIgnoreCase)));
        if (registration?.Terminal is not IPluginScheduledTaskProvider provider)
            return false;

        var task = provider.ScheduledTasks.FirstOrDefault(item =>
            item.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase));
        return task is not null && await RunAsync(registration, task, cancellationToken);
    }

    public async Task<bool> RunAsync(
        PlatformRegistration registration,
        ScheduledTaskRegistration task,
        CancellationToken cancellationToken = default)
    {
        var lockKey = $"task-lock:{registration.PluginKey}:{registration.Name}:{task.Name}";
        bool acquired;
        try
        {
            acquired = await runtimeState.TryAcquireAsync(
                lockKey,
                TimeSpan.FromMinutes(30),
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "plugin task lock is unavailable platform={Platform} plugin={PluginKey} task={TaskName}",
                registration.Name,
                registration.PluginKey,
                task.Name);
            return false;
        }

        if (!acquired) return false;

        var started = DateTimeOffset.UtcNow;
        var status = "Success";
        string? error = null;
        try
        {
            await TryWritePluginLogAsync(new PluginLog
            {
                PluginKey = registration.PluginKey,
                Platform = registration.Name,
                EventType = "task.started",
                Message = "插件定时任务开始",
                Level = "Information",
                TaskName = task.Name,
                CreatedAt = started
            });
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "plugin task start log persistence failed platform={Platform} plugin={PluginKey} task={TaskName}",
                registration.Name,
                registration.PluginKey,
                task.Name);
        }

        try
        {
            await task.ExecuteAsync(new PluginScheduledTaskContext(
                registration.Name,
                registration.PluginKey,
                cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = "Cancelled";
            error = "task cancelled";
        }
        catch (Exception exception)
        {
            status = "Failed";
            error = exception.Message;
            logger.LogError(
                exception,
                "plugin task failed platform={Platform} plugin={PluginKey} task={TaskName}",
                registration.Name,
                registration.PluginKey,
                task.Name);
        }

        try
        {
            await TryWritePluginLogAsync(new PluginLog
            {
                PluginKey = registration.PluginKey,
                Platform = registration.Name,
                EventType = "task.completed",
                Message = error ?? "插件定时任务完成",
                Level = status == "Success" ? "Information" : status == "Cancelled" ? "Warning" : "Error",
                TaskName = task.Name,
                DurationMs = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { status, error })
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "plugin task completion log persistence failed plugin={PluginKey} task={TaskName}", registration.PluginKey, task.Name);
        }
        try { await runtimeState.RemoveAsync(lockKey, CancellationToken.None); }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "plugin task lock release failed platform={Platform} plugin={PluginKey} task={TaskName}",
                registration.Name,
                registration.PluginKey,
                task.Name);
        }
        return status == "Success";
    }

    private async Task TryWritePluginLogAsync(PluginLog log)
    {
        try
        {
            await pluginLogs.WriteAsync(log, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "plugin detail task log persistence failed pluginKey={PluginKey} task={TaskName}",
                log.PluginKey,
                log.TaskName);
        }
    }
}
