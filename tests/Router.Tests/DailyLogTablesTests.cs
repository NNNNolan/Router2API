using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;
using SqlSugar;

namespace Router.Tests;

[TestClass]
public sealed class DailyLogTablesTests
{
    [TestMethod]
    public async Task DailyWritesRangePagingTaskUpdateAndCleanupUseUtcDays()
    {
        var database = Database();
        var initializer = new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance);
        initializer.Initialize();
        var first = new DateTimeOffset(2026, 10, 30, 23, 59, 59, TimeSpan.Zero);
        var second = first.AddSeconds(1);
        var requests = new RequestLogStore(database);
        foreach (var time in new[] { first, second })
        {
            await requests.WriteAsync(new RequestLog { Platform = "test", Model = "model", CreatedAt = time, Success = true, Usage = new(1, 2, 3) });
            await new ResourceEventLogSink(database).WriteAsync(new ResourceEvent { ResourceId = "test", CreatedAt = time });
            await new PluginLogStore(database, Mock.Of<IOptionsMonitor<PluginLogOptions>>(options => options.CurrentValue == new PluginLogOptions()))
                .WriteAsync(new PluginLog { PluginKey = "test", Message = "test", CreatedAt = time });
        }
        var task = new TaskLog { PluginKey = "test", TaskName = "job", AccountId = "account", Status = "Running", StartedAt = first };
        var tasks = new TaskLogStore(database);
        await tasks.WriteAsync(task);
        await tasks.UpdateAsync(new TaskLog { Id = task.Id, PluginKey = task.PluginKey, TaskName = task.TaskName,
            AccountId = task.AccountId, StartedAt = task.StartedAt, Status = "Success", FinishedAt = second.AddMinutes(1) });
        using (var db = database.CreateClient())
        {
            foreach (var prefix in new[] { "request_logs", "resource_events", "plugin_logs" })
            {
                db.DbMaintenance.IsAnyTable($"{prefix}_20261030", false).Should().BeTrue();
                db.DbMaintenance.IsAnyTable($"{prefix}_20261031", false).Should().BeTrue();
            }
            db.Queryable<TaskLogEntity>().SplitTable().Single().Status.Should().Be("Success");
        }
        (await requests.QueryAsync(new(1, 20, second, second.AddMinutes(1)))).Items.Should().ContainSingle();
        var report = await new AnalyticsService(database).QueryUsageAsync(new(first.AddSeconds(-1), second.AddSeconds(1)));
        report.Requests.Should().Be(2);
        report.Tokens.Should().Be(6);
        (await new ActivityHeatmapService(database).QueryAsync(TimeZoneInfo.Utc, second.AddMinutes(1), 30))
            .Days.Single(day => day.Day == "2026-10-30").Requests.Should().Be(1);
        await new LogRetentionStore(database).DeleteBeforeAsync(second);
        using (var db = database.CreateClient()) db.DbMaintenance.IsAnyTable("request_logs_20261030", false).Should().BeFalse();
        (await requests.QueryAsync(new(1, 20))).Total.Should().Be(1);
        await new LogRetentionStore(database).DeleteBeforeAsync(second.AddDays(1));
        (await requests.QueryAsync(new(1, 20))).Total.Should().Be(0);
        // 全部表被清理后，空查询及随后插入仍然正常，不重建过期表。
        await requests.WriteAsync(new RequestLog { CreatedAt = second.AddDays(2), Success = true });
        (await requests.QueryAsync(new(1, 20))).Total.Should().Be(1);
    }

    [TestMethod]
    public async Task LegacyTablesMigrateWithoutLosingFieldsOrDuplicatingRows()
    {
        var database = Database();
        using (var db = database.CreateClient())
        {
            CreateLegacy<RequestLogEntity>(db, "request_logs");
            db.Insertable(new RequestLogEntity { Id = "old-a", CreatedAtUtc = new(2026, 10, 30), UsageJson = """{"PromptTokens":1,"CompletionTokens":2,"TotalTokens":3}""" })
                .AS("request_logs").ExecuteCommand();
            db.Insertable(new RequestLogEntity { Id = "old-b", CreatedAtUtc = new(2026, 10, 31) })
                .AS("request_logs").ExecuteCommand();
            CreateLegacy<PluginLogEntity>(db, "plugin_logs");
            db.Insertable(new PluginLogEntity { Id = "old-plugin", CreatedAtUtc = new(2026, 10, 30), Message = "kept" }).AS("plugin_logs").ExecuteCommand();
            CreateLegacy<TaskLogEntity>(db, "task_logs");
            db.Insertable(new TaskLogEntity { Id = "old-task", StartedAtUtc = new(2026, 10, 30), AccountId = "account", Status = "Success" }).AS("task_logs").ExecuteCommand();
            CreateLegacy<ResourceEventEntity>(db, "resource_events");
            db.Insertable(new ResourceEventEntity { Id = "old-event", CreatedAtUtc = new(2026, 10, 30) }).AS("resource_events").ExecuteCommand();
        }
        var initializer = new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance);
        initializer.Initialize();
        initializer.Initialize();
        (await new RequestLogStore(database).QueryAsync(new(1, 20))).Total.Should().Be(2);
        using var client = database.CreateClient();
        client.DbMaintenance.IsAnyTable("request_logs", false).Should().BeFalse();
        client.Queryable<PluginLogEntity>().SplitTable().Single().Message.Should().Be("kept");
        client.Queryable<TaskLogEntity>().SplitTable().Single().Status.Should().Be("Success");
        client.Queryable<ResourceEventEntity>().SplitTable().Count().Should().Be(1);
    }

    private static SqlSugarDatabase Database() => new(Options.Create(new DatabaseOptions
    {
        Path = Path.Combine(AppContext.BaseDirectory, "test-data", $"daily-{Guid.NewGuid():N}.db")
    }), NullLogger<SqlSugarDatabase>.Instance);

    private static void CreateLegacy<T>(SqlSugarClient db, string table) where T : class, new()
    {
        db.MappingTables.Add(typeof(T).Name, table);
        db.CodeFirst.InitTables<T>();
        db.MappingTables.RemoveAll(mapping => mapping.EntityName == typeof(T).Name);
    }
}
