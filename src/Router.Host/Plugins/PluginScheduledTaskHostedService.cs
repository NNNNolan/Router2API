using System.Collections.Concurrent;
using Router.Contracts.Host;
using Router.Contracts.Plugins;
using Router.Host.Services;

namespace Router.Host.Plugins;

/// <summary>在宿主进程中运行插件声明的定时任务。</summary>
public sealed class PluginScheduledTaskHostedService(
    IPlatformRegistry platforms,
    PluginTaskRunner runner,
    ILogger<PluginScheduledTaskHostedService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTimeOffset.UtcNow;
                var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var registration in platforms.All.Where(item => item.Enabled))
                {
                    if (registration.Terminal is not IPluginScheduledTaskProvider provider)
                        continue;

                    IReadOnlyList<ScheduledTaskRegistration> tasks;
                    try
                    {
                        tasks = provider.ScheduledTasks;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(
                            exception,
                            "failed to enumerate plugin scheduled tasks platform={Platform} plugin={PluginKey}",
                            registration.Name,
                            registration.PluginKey);
                        continue;
                    }

                    foreach (var task in tasks)
                    {
                        if (string.IsNullOrWhiteSpace(task.Name)
                            || string.IsNullOrWhiteSpace(task.Cron))
                            continue;

                        var key = $"{registration.PluginKey}:{registration.Name}:{task.Name}";
                        activeKeys.Add(key);
                        DateTimeOffset due;
                        try
                        {
                            // A task should first run at its next Cron occurrence, not as soon as
                            // the host starts. Scheduling all tasks immediately creates a burst
                            // of database and network work during startup.
                            due = _nextRuns.GetOrAdd(key, _ => CronSchedule.GetNext(task.Cron, now));
                        }
                        catch (FormatException exception)
                        {
                            _running.TryRemove(key, out _);
                            _nextRuns[key] = now.AddMinutes(1);
                            logger.LogError(
                                exception,
                                "invalid plugin scheduled task cron platform={Platform} plugin={PluginKey} task={TaskName} cron={Cron}",
                                registration.Name,
                                registration.PluginKey,
                                task.Name,
                                task.Cron);
                            continue;
                        }

                        if (due > now || !_running.TryAdd(key, 0))
                            continue;

                        try
                        {
                            _nextRuns[key] = CronSchedule.GetNext(task.Cron, now);
                        }
                        catch (FormatException exception)
                        {
                            _running.TryRemove(key, out _);
                            _nextRuns[key] = now.AddMinutes(1);
                            logger.LogError(
                                exception,
                                "invalid plugin scheduled task cron platform={Platform} plugin={PluginKey} task={TaskName} cron={Cron}",
                                registration.Name,
                                registration.PluginKey,
                                task.Name,
                                task.Cron);
                            continue;
                        }

                        _ = RunTaskAsync(key, registration, task, stoppingToken);
                    }
                }

                foreach (var key in _nextRuns.Keys)
                {
                    if (!activeKeys.Contains(key))
                        _nextRuns.TryRemove(key, out _);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 宿主正常关闭。
        }
    }

    private async Task RunTaskAsync(
        string key,
        PlatformRegistration registration,
        ScheduledTaskRegistration task,
        CancellationToken stoppingToken)
    {
        try
        {
            logger.LogDebug(
                "running plugin scheduled task platform={Platform} plugin={PluginKey} task={TaskName}",
                registration.Name,
                registration.PluginKey,
                task.Name);

            await runner.RunAsync(registration, task, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 宿主正常关闭。
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "plugin scheduled task failed platform={Platform} plugin={PluginKey} task={TaskName}",
                registration.Name,
                registration.PluginKey,
                task.Name);
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }

}
