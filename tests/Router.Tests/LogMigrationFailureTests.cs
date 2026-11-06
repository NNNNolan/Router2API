using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;
using SqlSugar;

namespace Router.Tests;

[TestClass]
public sealed class LogMigrationFailureTests
{
    [TestMethod]
    public async Task CancelledBatchCanBeRescannedAndRetriedWithoutDuplicates()
    {
        var (business, logs) = Databases();
        Initialize(business, logs);
        using (var db = business.CreateClient())
        {
            CreateLegacy<RequestLogEntity>(db, "request_logs");
            db.Insertable(Enumerable.Range(0, 1501).Select(index => new RequestLogEntity
            {
                Id = $"cancel-{index:D4}", TraceId = $"trace-{index:D4}", CreatedAtUtc = new(2026, 11, 5), Success = true
            }).ToArray()).AS("request_logs").ExecuteCommand();
        }

        using var cancellation = new CancellationTokenSource();
        var inventory = LogDatabaseMigration.Scan(logs, business.FilePath);
        Func<Task> migrate = () => LogDatabaseMigration.MigrateAsync(logs, business.FilePath, inventory, progress =>
        {
            if (progress.Phase == "copying" && progress.CopiedRows >= 500) cancellation.Cancel();
        }, cancellation.Token);
        await migrate.Should().ThrowAsync<OperationCanceledException>();
        using (var source = business.CreateClient()) source.Ado.GetInt("SELECT COUNT(*) FROM request_logs").Should().Be(1501);

        var retry = LogDatabaseMigration.Scan(logs, business.FilePath);
        var result = await LogDatabaseMigration.MigrateAsync(logs, business.FilePath, retry, _ => { }, CancellationToken.None);
        result.VerifiedRows.Should().Be(1501);
        using var target = logs.CreateClient();
        target.Ado.GetInt("SELECT COUNT(*) FROM request_logs_20261105").Should().Be(1501);
    }

    [TestMethod]
    public async Task MissingTargetRowDuringVerificationRetainsSource()
    {
        var (business, logs) = Databases();
        Initialize(business, logs);
        CreateRequestSource(business, "missing-target", 1);
        var inventory = LogDatabaseMigration.Scan(logs, business.FilePath);
        Func<Task> migrate = () => LogDatabaseMigration.MigrateAsync(logs, business.FilePath, inventory, progress =>
        {
            if (progress.Phase != "verifying" || progress.VerifiedRows != 0) return;
            using var target = logs.CreateClient();
            target.Ado.ExecuteCommand("DELETE FROM request_logs_20261105 WHERE Id='missing-target-0000'");
        }, CancellationToken.None);
        await migrate.Should().ThrowAsync<LogMigrationException>().WithMessage("*未完整保存*");
        using var source = business.CreateClient();
        source.Ado.GetInt("SELECT COUNT(*) FROM request_logs").Should().Be(1);
    }

    [TestMethod]
    public async Task CorruptTargetRowDuringVerificationRetainsSource()
    {
        var (business, logs) = Databases();
        Initialize(business, logs);
        CreateRequestSource(business, "corrupt-target", 1);
        var inventory = LogDatabaseMigration.Scan(logs, business.FilePath);
        Func<Task> migrate = () => LogDatabaseMigration.MigrateAsync(logs, business.FilePath, inventory, progress =>
        {
            if (progress.Phase != "verifying" || progress.VerifiedRows != 0) return;
            using var target = logs.CreateClient();
            target.Ado.ExecuteCommand("UPDATE request_logs_20261105 SET TraceId='corrupted' WHERE Id='corrupt-target-0000'");
        }, CancellationToken.None);
        await migrate.Should().ThrowAsync<LogMigrationException>().WithMessage("*未完整保存*");
        using var source = business.CreateClient();
        source.Ado.GetInt("SELECT COUNT(*) FROM request_logs").Should().Be(1);
    }

    [TestMethod]
    public async Task RetryAfterOneVerifiedSourceTableWasDroppedPreservesAllTargetRows()
    {
        var (business, logs) = Databases();
        Initialize(business, logs);
        using (var db = business.CreateClient())
        {
            CreateLegacy<RequestLogEntity>(db, "request_logs");
            db.Insertable(new RequestLogEntity { Id = "request", TraceId = "trace", CreatedAtUtc = new(2026, 11, 5), Success = true })
                .AS("request_logs").ExecuteCommand();
            CreateLegacy<TaskLogEntity>(db, "task_logs");
            db.Insertable(new TaskLogEntity { Id = "task", PluginKey = "p", TaskName = "t", Status = "Success", StartedAtUtc = new(2026, 11, 5) })
                .AS("task_logs").ExecuteCommand();
        }

        var inventory = LogDatabaseMigration.Scan(logs, business.FilePath);
        Func<Task> interrupted = () => LogDatabaseMigration.MigrateAsync(logs, business.FilePath, inventory, progress =>
        {
            if (progress.Phase == "deleting" && progress.DeletedTables == 1)
                throw new InvalidOperationException("injected interruption after committed source deletion");
        }, CancellationToken.None);
        await interrupted.Should().ThrowAsync<InvalidOperationException>();
        var retry = LogDatabaseMigration.Scan(logs, business.FilePath);
        await LogDatabaseMigration.MigrateAsync(logs, business.FilePath, retry, _ => { }, CancellationToken.None);
        using var target = logs.CreateClient();
        target.Ado.GetInt("SELECT COUNT(*) FROM request_logs_20261105 WHERE Id='request'").Should().Be(1);
        target.Ado.GetInt("SELECT COUNT(*) FROM task_logs_20261105 WHERE Id='task'").Should().Be(1);
    }

    [TestMethod]
    public async Task SourceChangeBeforeDeletionStopsMigrationAndLeavesSourceTable()
    {
        var (business, logs) = Databases();
        Initialize(business, logs);
        CreateRequestSource(business, "original", 1);
        var inventory = LogDatabaseMigration.Scan(logs, business.FilePath);
        var changed = false;
        Func<Task> migrate = () => LogDatabaseMigration.MigrateAsync(logs, business.FilePath, inventory, progress =>
        {
            if (progress.Phase != "verifying" || progress.VerifiedRows == 0 || changed) return;
            changed = true;
            using var source = business.CreateClient();
            source.Insertable(new RequestLogEntity { Id = "late", TraceId = "late", CreatedAtUtc = new(2026, 11, 5), Success = true })
                .AS("request_logs").ExecuteCommand();
        }, CancellationToken.None);
        await migrate.Should().ThrowAsync<LogMigrationException>();
        using var sourceDb = business.CreateClient();
        sourceDb.Ado.GetInt("SELECT COUNT(*) FROM request_logs").Should().Be(2);
    }

    [TestMethod]
    public async Task OptionalLegacyColumnsAreFilledWithNullAndInvalidIdsOrDatesAreRejected()
    {
        var (business, logs) = Databases();
        Initialize(business, logs);
        using (var db = business.CreateClient())
        {
            db.Ado.ExecuteCommand("CREATE TABLE request_logs (Id TEXT PRIMARY KEY, TraceId TEXT, Success INTEGER, StatusCode INTEGER, DurationMs INTEGER, CreatedAtUtc TEXT)");
            db.Ado.ExecuteCommand("INSERT INTO request_logs VALUES ('valid','trace',1,200,1,'2026-11-05 00:00:00')");
        }
        var inventory = LogDatabaseMigration.Scan(logs, business.FilePath);
        await LogDatabaseMigration.MigrateAsync(logs, business.FilePath, inventory, _ => { }, CancellationToken.None);
        using (var target = logs.CreateClient())
            target.Queryable<RequestLogEntity>().SplitTable().Single().UsageJson.Should().BeNull();

        using (var db = business.CreateClient())
        {
            db.Ado.ExecuteCommand("CREATE TABLE request_logs (Id TEXT, TraceId TEXT, Success INTEGER, StatusCode INTEGER, DurationMs INTEGER, CreatedAtUtc TEXT)");
            db.Ado.ExecuteCommand("INSERT INTO request_logs VALUES ('','trace',1,200,1,'2026-11-05 00:00:00')");
        }
        var invalidIdInventory = LogDatabaseMigration.Scan(logs, business.FilePath);
        Func<Task> invalidId = () => LogDatabaseMigration.MigrateAsync(logs, business.FilePath, invalidIdInventory, _ => { }, CancellationToken.None);
        await invalidId.Should().ThrowAsync<LogMigrationException>().WithMessage("*ID*");
        using (var db = business.CreateClient())
        {
            db.Ado.ExecuteCommand("DROP TABLE request_logs");
            db.Ado.ExecuteCommand("CREATE TABLE request_logs (Id TEXT, TraceId TEXT, Success INTEGER, StatusCode INTEGER, DurationMs INTEGER, CreatedAtUtc TEXT)");
            db.Ado.ExecuteCommand("INSERT INTO request_logs VALUES ('bad','trace',1,200,1,'not-a-date')");
        }
        var invalidDateInventory = LogDatabaseMigration.Scan(logs, business.FilePath);
        Func<Task> invalidDate = () => LogDatabaseMigration.MigrateAsync(logs, business.FilePath, invalidDateInventory, _ => { }, CancellationToken.None);
        await invalidDate.Should().ThrowAsync<LogMigrationException>().WithMessage("*UTC 日期*");
    }

    [TestMethod]
    public async Task NewLogWritesCanContinueWhileLegacyBatchesCopy()
    {
        var (business, logs) = Databases();
        Initialize(business, logs);
        CreateRequestSource(business, "legacy", 1001);
        var inventory = LogDatabaseMigration.Scan(logs, business.FilePath);
        var wrote = false;
        await LogDatabaseMigration.MigrateAsync(logs, business.FilePath, inventory, progress =>
        {
            if (progress.Phase != "copying" || wrote) return;
            wrote = true;
            new RequestLogStore(logs).WriteAsync(new RequestLog
            {
                Id = "live-write", TraceId = "live-write", Platform = "live", CreatedAt = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero), Success = true
            }).GetAwaiter().GetResult();
        }, CancellationToken.None);
        using var target = logs.CreateClient();
        target.Ado.GetInt("SELECT COUNT(*) FROM request_logs_20261105").Should().Be(1002);
    }

    [TestMethod]
    public async Task LegacySingleTableSplitsRowsByUtcMidnight()
    {
        var (business, logs) = Databases();
        Initialize(business, logs);
        using (var source = business.CreateClient())
        {
            CreateLegacy<RequestLogEntity>(source, "request_logs");
            source.Insertable(new[]
            {
                new RequestLogEntity { Id = "before-midnight", TraceId = "before", CreatedAtUtc = new(2026, 11, 5, 23, 59, 59, DateTimeKind.Utc), Success = true },
                new RequestLogEntity { Id = "after-midnight", TraceId = "after", CreatedAtUtc = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc), Success = true }
            }).AS("request_logs").ExecuteCommand();
        }
        var inventory = LogDatabaseMigration.Scan(logs, business.FilePath);
        await LogDatabaseMigration.MigrateAsync(logs, business.FilePath, inventory, _ => { }, CancellationToken.None);
        using var target = logs.CreateClient();
        target.Ado.GetInt("SELECT COUNT(*) FROM request_logs_20261105 WHERE Id='before-midnight'").Should().Be(1);
        target.Ado.GetInt("SELECT COUNT(*) FROM request_logs_20261106 WHERE Id='after-midnight'").Should().Be(1);
    }

    [TestMethod]
    public async Task RetentionWaitsForTheSharedMigrationGateAndHonorsCancellation()
    {
        var (business, logs) = Databases();
        Initialize(business, logs);
        var gate = new LogMaintenanceGate();
        var retention = new LogRetentionStore(logs, gate);
        Task<int>? waiting = null;
        await gate.Semaphore.WaitAsync();
        try
        {
            waiting = retention.DeleteBeforeAsync(DateTimeOffset.UtcNow.AddDays(-1));
            waiting.IsCompleted.Should().BeFalse();
            using var cancellation = new CancellationTokenSource();
            var cancelled = retention.DeleteBeforeAsync(DateTimeOffset.UtcNow, cancellation.Token);
            cancellation.Cancel();
            Func<Task> awaitCancelled = () => cancelled;
            await awaitCancelled.Should().ThrowAsync<OperationCanceledException>();
        }
        finally { gate.Semaphore.Release(); }
        await waiting!;
    }

    private static (SqlSugarDatabase Business, SqlSugarDatabase Logs) Databases()
    {
        var options = Options.Create(new DatabaseOptions { Path = Path.Combine(Path.GetTempPath(), $"migration-failure-{Guid.NewGuid():N}.db") });
        return (new SqlSugarDatabase(options, NullLogger<SqlSugarDatabase>.Instance),
            SqlSugarDatabase.CreateLogs(options, NullLogger<SqlSugarDatabase>.Instance));
    }

    private static void Initialize(SqlSugarDatabase business, SqlSugarDatabase logs)
        => new DatabaseInitializer(business, logs, NullLogger<DatabaseInitializer>.Instance).Initialize();

    private static void CreateRequestSource(SqlSugarDatabase business, string prefix, int count)
    {
        using var db = business.CreateClient();
        CreateLegacy<RequestLogEntity>(db, "request_logs");
        db.Insertable(Enumerable.Range(0, count).Select(index => new RequestLogEntity
        {
            Id = $"{prefix}-{index:D4}", TraceId = $"trace-{index:D4}", CreatedAtUtc = new(2026, 11, 5), Success = true
        }).ToArray()).AS("request_logs").ExecuteCommand();
    }

    private static void CreateLegacy<T>(SqlSugarClient db, string table) where T : class, new()
    {
        db.MappingTables.Add(typeof(T).Name, table);
        db.CodeFirst.InitTables<T>();
        db.MappingTables.RemoveAll(mapping => mapping.EntityName == typeof(T).Name);
    }
}
