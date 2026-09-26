using Microsoft.Extensions.Options;
using Router.Contracts.Host;
using Router.Host.Configuration;

namespace Router.Host.Services;

/// <summary>按配置的 Cron 清理宿主数据日志。</summary>
public sealed class LogCleanupHostedService(
    ILogRetentionStore store,
    IOptionsMonitor<LogRetentionOptions> options,
    ILogger<LogCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        var schedule = string.Empty;
        var nextRun = DateTimeOffset.MaxValue;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var current = options.CurrentValue;
                if (!string.Equals(schedule, current.CleanupCron, StringComparison.Ordinal))
                {
                    schedule = current.CleanupCron;
                    try
                    {
                        nextRun = CronSchedule.GetNext(schedule, DateTimeOffset.UtcNow);
                    }
                    catch (FormatException exception)
                    {
                        nextRun = DateTimeOffset.UtcNow.AddMinutes(1);
                        logger.LogError(exception, "invalid log cleanup cron {Cron}", schedule);
                    }
                }

                var now = DateTimeOffset.UtcNow;
                if (now < nextRun) continue;

                var retentionDays = Math.Clamp(current.DataRetentionDays, 1, 3650);
                try
                {
                    var deleted = await store.DeleteBeforeAsync(now.AddDays(-retentionDays), stoppingToken);
                    logger.LogInformation(
                        "log cleanup completed deleted={Deleted} retentionDays={RetentionDays} cron={Cron}",
                        deleted,
                        retentionDays,
                        schedule);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "log cleanup failed retentionDays={RetentionDays}", retentionDays);
                }

                try
                {
                    nextRun = CronSchedule.GetNext(schedule, DateTimeOffset.UtcNow);
                }
                catch (FormatException exception)
                {
                    nextRun = DateTimeOffset.UtcNow.AddMinutes(1);
                    logger.LogError(exception, "invalid log cleanup cron {Cron}", schedule);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 宿主正常关闭。
        }
    }
}
