using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Host.Services;

/// <summary>受宿主生命周期管理的手动日志迁移 job。</summary>
public sealed class LogMigrationJobService : BackgroundService
{
    private readonly SqlSugarDatabase _businessDatabase;
    private readonly SqlSugarDatabase _logsDatabase;
    private readonly IOptionsMonitor<DatabaseOptions> _options;
    private readonly LogMaintenanceGate _gate;
    private readonly DashboardMetricsService _dashboard;
    private readonly ILogger<LogMigrationJobService> _logger;
    private readonly Channel<bool> _queue = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly object _sync = new();
    private LogMigrationSnapshot _snapshot;
    private bool _inventoryLoaded;

    public LogMigrationJobService(
        SqlSugarDatabase businessDatabase,
        [FromKeyedServices(SqlSugarDatabase.LogsKey)] SqlSugarDatabase logsDatabase,
        IOptionsMonitor<DatabaseOptions> options,
        LogMaintenanceGate gate,
        DashboardMetricsService dashboard,
        ILogger<LogMigrationJobService> logger)
    {
        _businessDatabase = businessDatabase;
        _logsDatabase = logsDatabase;
        _options = options;
        _gate = gate;
        _dashboard = dashboard;
        _logger = logger;
        _snapshot = CreateIdle(0, 0);
    }

    public LogMigrationSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            if (!_inventoryLoaded && _snapshot.Status == "idle")
            {
                try
                {
                    var inventory = LogDatabaseMigration.Scan(_logsDatabase, _businessDatabase.FilePath);
                    _inventoryLoaded = true;
                    _snapshot = CreateIdle(inventory.Rows, inventory.Tables.Count);
                }
                catch (LogMigrationException ex)
                {
                    _logger.LogWarning(ex, "Could not inspect legacy log tables.");
                    _inventoryLoaded = true;
                    _snapshot = _snapshot with { Status = "failed", Phase = "failed", Error = ex.Message, FinishedAt = DateTimeOffset.UtcNow };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not inspect legacy log tables.");
                    _inventoryLoaded = true;
                    _snapshot = _snapshot with { Status = "failed", Phase = "failed", Error = "无法读取旧日志表清单，请检查数据库状态后重试。", FinishedAt = DateTimeOffset.UtcNow };
                }
            }
            return WithRestartRequired(_snapshot);
        }
    }

    public StartResult TryStart()
    {
        lock (_sync)
        {
            var current = WithRestartRequired(_snapshot);
            if (current.RestartRequired) return new StartResult(false, true, current with { Error = "数据库路径已变更，请重启宿主后再迁移。" });
            if (current.Status == "running") return new StartResult(false, false, current);

            _inventoryLoaded = false;
            _snapshot = new LogMigrationSnapshot("running", "scanning", _businessDatabase.FilePath, _logsDatabase.FilePath,
                false, 0, 0, 0, 0, 0, null, 0, null, DateTimeOffset.UtcNow, null);
            if (!_queue.Writer.TryWrite(true))
            {
                _snapshot = _snapshot with { Status = "failed", Phase = "failed", Error = "迁移任务队列不可用。", FinishedAt = DateTimeOffset.UtcNow };
                return new StartResult(false, false, _snapshot);
            }
            return new StartResult(true, false, _snapshot);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await RunMigrationAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RunMigrationAsync(CancellationToken stoppingToken)
    {
        await _gate.Semaphore.WaitAsync(stoppingToken);
        try
        {
            var inventory = await Task.Run(() => LogDatabaseMigration.Scan(_logsDatabase, _businessDatabase.FilePath), stoppingToken);
            lock (_sync)
            {
                _inventoryLoaded = true;
                _snapshot = _snapshot with { TotalRows = inventory.Rows, TotalTables = inventory.Tables.Count };
            }
            if (inventory.Tables.Count == 0)
            {
                lock (_sync) _snapshot = CreateIdle(0, 0);
                return;
            }

            var result = await LogDatabaseMigration.MigrateAsync(_logsDatabase, _businessDatabase.FilePath, inventory,
                progress => UpdateProgress(progress), stoppingToken);
            _dashboard.InvalidateHistory();
            lock (_sync)
            {
                _snapshot = _snapshot with
                {
                    Status = "completed", Phase = "completed", CopiedRows = result.CopiedRows,
                    VerifiedRows = result.VerifiedRows, DeletedTables = result.DeletedTables,
                    CurrentTable = null, ProgressPercent = 100, Error = null, FinishedAt = DateTimeOffset.UtcNow
                };
                _inventoryLoaded = false;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _dashboard.InvalidateHistory();
            lock (_sync) _snapshot = _snapshot with
            {
                Status = "failed", Phase = "failed", Error = "宿主停止时迁移已中断；可重启后重新执行。",
                FinishedAt = DateTimeOffset.UtcNow
            };
        }
        catch (LogMigrationException ex)
        {
            _dashboard.InvalidateHistory();
            _logger.LogWarning(ex, "Manual log migration stopped at table {Table}.", _snapshot.CurrentTable);
            lock (_sync) _snapshot = _snapshot with
            {
                Status = "failed", Phase = "failed", Error = _snapshot.CurrentTable is null
                    ? ex.Message
                    : $"表 {_snapshot.CurrentTable}：{ex.Message} 剩余来源表可安全重试。",
                FinishedAt = DateTimeOffset.UtcNow
            };
            lock (_sync) _inventoryLoaded = false;
        }
        catch (Exception ex)
        {
            _dashboard.InvalidateHistory();
            _logger.LogError(ex, "Manual log migration failed at table {Table}.", _snapshot.CurrentTable);
            lock (_sync) _snapshot = _snapshot with
            {
                Status = "failed", Phase = "failed", Error = _snapshot.CurrentTable is null
                    ? "迁移失败，剩余来源表可安全重试。"
                    : $"表 {_snapshot.CurrentTable} 迁移失败，剩余来源表可安全重试。",
                FinishedAt = DateTimeOffset.UtcNow
            };
            lock (_sync) _inventoryLoaded = false;
        }
        finally { _gate.Semaphore.Release(); }
    }

    private void UpdateProgress(LogDatabaseMigration.MigrationProgress progress)
    {
        lock (_sync)
        {
            var totalUnits = Math.Max(1L, 2 * _snapshot.TotalRows + _snapshot.TotalTables);
            var done = _snapshot.Status == "completed" ? totalUnits : progress.CopiedRows + progress.VerifiedRows + progress.DeletedTables;
            var percent = (int)Math.Clamp(done * 100 / totalUnits, 0, 99);
            _snapshot = _snapshot with
            {
                Phase = progress.Phase, CopiedRows = progress.CopiedRows, VerifiedRows = progress.VerifiedRows,
                DeletedTables = progress.DeletedTables, CurrentTable = progress.Table, ProgressPercent = percent
            };
        }
    }

    private LogMigrationSnapshot WithRestartRequired(LogMigrationSnapshot snapshot)
    {
        var configuredBusiness = Path.GetFullPath(_options.CurrentValue.Path);
        var configuredLogs = _options.CurrentValue.GetLogPath();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return snapshot with { RestartRequired = !string.Equals(configuredBusiness, _businessDatabase.FilePath, comparison)
            || !string.Equals(configuredLogs, _logsDatabase.FilePath, comparison) };
    }

    private LogMigrationSnapshot CreateIdle(long rows, int tables)
        => new("idle", "idle", _businessDatabase.FilePath, _logsDatabase.FilePath, false, rows, 0, 0,
            tables, 0, null, 0, null, null, null);
}

public sealed record StartResult(bool Started, bool RestartRequired, LogMigrationSnapshot Snapshot);

public sealed record LogMigrationSnapshot(
    string Status,
    string Phase,
    string SourcePath,
    string TargetPath,
    bool RestartRequired,
    long TotalRows,
    long CopiedRows,
    long VerifiedRows,
    int TotalTables,
    int DeletedTables,
    string? CurrentTable,
    int ProgressPercent,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);
