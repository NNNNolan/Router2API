using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Host;
using Router.Host.Api;
using Router.Host.Configuration;
using Router.Host.Plugins;
using Router.Host.Pipeline;
using Router.Host.Security;
using Router.Host.Services;
using Router.Infrastructure;
using Router.Infrastructure.Persistence;
using SqlSugar;

namespace Router.Tests;

[TestClass]
public sealed class LogDatabaseMigrationTests
{
    [TestMethod]
    public async Task AdminEndpointRequiresAuthAndCsrfQueuesOneManagedJobWithCachedProgress()
    {
        var path = Path.Combine(Path.GetTempPath(), $"log-migration-api-{Guid.NewGuid():N}.db");
        var password = Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Path"] = path,
            ["Redis:ConnectionString"] = ""
        });
        builder.Services.Configure<AuthOptions>(options => options.Admin.Users.Add(new AdminUserOptions { Username = "admin", Password = password }));
        builder.Services.AddSingleton<PasswordHasher>();
        builder.Services.AddSingleton<AdminAuthService>();
        builder.Services.AddSingleton<ApiKeyService>();
        builder.Services.AddSingleton(Mock.Of<IConfigFileService>());
        builder.Services.AddSingleton(Mock.Of<IProxySubscriptionRefreshQueue>());
        builder.Services.AddSingleton(Mock.Of<IPluginCatalog>());
        builder.Services.AddRouterInfrastructure(builder.Configuration);
        builder.Services.AddSingleton<RouterPipeline>();
        builder.Services.AddLogMigration();
        await using var app = builder.Build();
        app.Services.GetRequiredService<DatabaseInitializer>().Initialize();
        var business = app.Services.GetRequiredService<SqlSugarDatabase>();
        using (var db = business.CreateClient())
        {
            db.Ado.ExecuteCommand("CREATE TABLE request_logs_invalid_day (Id TEXT)");
        }
        app.UseMiddleware<AdminSessionMiddleware>();
        ApiEndpoints.Map(app);
        await app.StartAsync();
        using var client = app.GetTestClient();
        (await client.GetAsync("/api/admin/log-migration")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var login = await client.PostAsJsonAsync("/api/admin/login", new { username = "admin", password });
        login.EnsureSuccessStatusCode();
        var cookies = login.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';')[0]).ToArray();
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
        (await client.PostAsync("/api/admin/log-migration", content: null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Add("X-CSRF-Token", cookies.Single(cookie => cookie.StartsWith("router_admin_csrf=", StringComparison.Ordinal)).Split('=')[1]);

        var scanFailureResponse = await client.GetAsync("/api/admin/log-migration");
        var scanFailure = await scanFailureResponse.Content.ReadFromJsonAsync<LogMigrationSnapshot>();
        scanFailure!.Status.Should().Be("failed");
        scanFailure.Phase.Should().Be("failed");
        scanFailure.Error.Should().Contain("UTC 日期").And.NotContain(path);
        using (var db = business.CreateClient())
        {
            db.Ado.ExecuteCommand("DROP TABLE request_logs_invalid_day");
            CreateLegacy<RequestLogEntity>(db, "request_logs");
            db.Insertable(new RequestLogEntity { Id = "api-old", TraceId = "trace", CreatedAtUtc = new(2026, 11, 5), UsageJson = "{\"tokens\":3}" }).AS("request_logs").ExecuteCommand();
        }

        var gate = app.Services.GetRequiredService<LogMaintenanceGate>();
        gate.Semaphore.Wait();
        try
        {
            var started = await client.PostAsync("/api/admin/log-migration", content: null);
            started.StatusCode.Should().Be(HttpStatusCode.Accepted);
            var duplicate = await client.PostAsync("/api/admin/log-migration", content: null);
            duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var snapshot = await client.GetFromJsonAsync<LogMigrationSnapshot>("/api/admin/log-migration");
            snapshot!.Status.Should().Be("running");
            snapshot.SourcePath.Should().Be(business.FilePath);
            snapshot.TotalRows.Should().Be(0); // The managed job has not scanned while waiting for the maintenance gate.
            var status = await client.GetAsync("/api/admin/log-migration");
            status.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        finally { gate.Semaphore.Release(); }

        LogMigrationSnapshot? completed = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(20);
            completed = await client.GetFromJsonAsync<LogMigrationSnapshot>("/api/admin/log-migration");
            if (completed?.Status == "completed" || completed?.Status == "failed") break;
        }
        completed!.Status.Should().Be("completed", completed.Error);
        completed.TotalRows.Should().Be(1);
        completed.CopiedRows.Should().Be(1);
        completed.VerifiedRows.Should().Be(1);
        completed.DeletedTables.Should().Be(1);
        completed.ProgressPercent.Should().Be(100);
        using var logs = app.Services.GetRequiredKeyedService<SqlSugarDatabase>(SqlSugarDatabase.LogsKey).CreateClient();
        logs.Queryable<RequestLogEntity>().SplitTable().Single().Id.Should().Be("api-old");

        builder.Configuration["Database:Path"] = Path.Combine(Path.GetTempPath(), $"restarted-business-{Guid.NewGuid():N}.db");
        ((IConfigurationRoot)builder.Configuration).Reload();
        var drift = await client.GetFromJsonAsync<LogMigrationSnapshot>("/api/admin/log-migration");
        drift!.RestartRequired.Should().BeTrue();
        drift.SourcePath.Should().Be(business.FilePath);
        drift.TargetPath.Should().Be(app.Services.GetRequiredKeyedService<SqlSugarDatabase>(SqlSugarDatabase.LogsKey).FilePath);
        var rejected = await client.PostAsync("/api/admin/log-migration", content: null);
        rejected.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await rejected.Content.ReadFromJsonAsync<LogMigrationSnapshot>())!.RestartRequired.Should().BeTrue();
    }

    [TestMethod]
    public async Task MigratesMixedLegacyAndDailyTablesInBatchesAndPreservesFields()
    {
        var (business, logs) = Databases();
        new DatabaseInitializer(business, logs, NullLogger<DatabaseInitializer>.Instance).Initialize();
        var day = new DateTime(2026, 11, 5, 23, 59, 0, DateTimeKind.Utc);
        using (var db = business.CreateClient())
        {
            CreateLegacy<RequestLogEntity>(db, "request_logs");
            var requests = Enumerable.Range(0, 1001).Select(index => new RequestLogEntity
            {
                Id = $"req-{index:D4}", TraceId = $"trace-{index}", Platform = index % 2 == 0 ? "p" : null,
                Model = "m", Success = true, StatusCode = 200, DurationMs = 1, TtfbMs = null,
                UsageJson = index == 0 ? "{\"PromptTokens\":9007199254740993}" : null,
                UsageMetadataJson = null, AttemptDetailsJson = "[]", PrivacyJson = "{\"redacted\":true}",
                TraceJson = "{\"attempt\":1}", Endpoint = "/v1/responses", CreatedAtUtc = day
            }).ToArray();
            db.Insertable(requests).AS("request_logs").ExecuteCommand();
            CreateLegacy<RequestLogEntity>(db, "request_logs_20261106");
            db.Ado.ExecuteCommand("INSERT INTO request_logs_20261106 SELECT * FROM request_logs WHERE Id='req-0000'");

            CreateLegacy<ResourceEventEntity>(db, "resource_events");
            db.Insertable(new ResourceEventEntity { Id = "event-1", ResourceType = "account", ResourceId = "a", PluginKey = "p", EventType = "error", Error = null, CreatedAtUtc = day }).AS("resource_events").ExecuteCommand();
            CreateLegacy<TaskLogEntity>(db, "task_logs");
            db.Insertable(new TaskLogEntity { Id = "task-1", PluginKey = "p", TaskName = "refresh", Status = "Failed", DurationMs = 2, StartedAtUtc = day, FinishedAtUtc = null, DetailsJson = "{\"n\":9007199254740993}" }).AS("task_logs").ExecuteCommand();
            CreateLegacy<PluginLogEntity>(db, "plugin_logs");
            db.Insertable(new PluginLogEntity { Id = "plugin-1", PluginKey = "p", Level = "Warning", EventType = "event", Message = "m", TraceId = null, CreatedAtUtc = day, DetailsJson = "{\"x\":1}" }).AS("plugin_logs").ExecuteCommand();
            CreateLegacy<UsageBucketEntity>(db, "usage_buckets");
            db.Insertable(new UsageBucketEntity { Id = "bucket-1", Day = day.Date, Platform = "p", Requests = 3, PromptTokens = 9007199254740993, CompletionTokens = 7 }).AS("usage_buckets").ExecuteCommand();
        }

        var inventory = LogDatabaseMigration.Scan(logs, business.FilePath);
        inventory.Rows.Should().Be(1006);
        var progress = new List<LogDatabaseMigration.MigrationProgress>();
        var result = await LogDatabaseMigration.MigrateAsync(logs, business.FilePath, inventory, progress.Add, CancellationToken.None);
        result.DeletedTables.Should().Be(6);
        result.VerifiedRows.Should().Be(1006);
        result.CopiedRows.Should().Be(1006);
        progress.Should().Contain(item => item.Phase == "deleting");

        using (var db = logs.CreateClient())
        {
            db.Ado.GetInt("SELECT COUNT(*) FROM request_logs_20261105").Should().Be(1001);
            db.Ado.GetInt("SELECT COUNT(*) FROM resource_events_20261105").Should().Be(1);
            db.Ado.GetInt("SELECT COUNT(*) FROM task_logs_20261105").Should().Be(1);
            db.Ado.GetInt("SELECT COUNT(*) FROM plugin_logs_20261105").Should().Be(1);
            db.Queryable<UsageBucketEntity>().Single().PromptTokens.Should().Be(9007199254740993);
            var request = db.Queryable<RequestLogEntity>().SplitTable().Where(row => row.Id == "req-0000").Single();
            request.UsageJson.Should().Be("{\"PromptTokens\":9007199254740993}");
            request.TraceJson.Should().Be("{\"attempt\":1}");
            request.Platform.Should().Be("p");
            request.TtfbMs.Should().BeNull();
            db.Queryable<TaskLogEntity>().SplitTable().Single().FinishedAtUtc.Should().BeNull();
            db.Queryable<PluginLogEntity>().SplitTable().Single().DetailsJson.Should().Be("{\"x\":1}");
        }
        using var businessDb = business.CreateClient();
        businessDb.DbMaintenance.IsAnyTable("accounts", false).Should().BeTrue();
        foreach (var source in inventory.Tables) businessDb.DbMaintenance.IsAnyTable(source, false).Should().BeFalse();
    }

    [TestMethod]
    public async Task RejectsUnknownColumnsAndDifferentPrimaryKeyContentWithoutDeletingSource()
    {
        var (business, logs) = Databases();
        new DatabaseInitializer(business, logs, NullLogger<DatabaseInitializer>.Instance).Initialize();
        using (var db = business.CreateClient())
        {
            CreateLegacy<RequestLogEntity>(db, "request_logs");
            db.Ado.ExecuteCommand("ALTER TABLE request_logs ADD COLUMN Secret TEXT");
            db.Insertable(new RequestLogEntity { Id = "bad", CreatedAtUtc = new(2026, 11, 5), TraceId = "trace" }).AS("request_logs").ExecuteCommand();
        }
        Action scan = () => LogDatabaseMigration.Scan(logs, business.FilePath);
        scan.Should().Throw<LogMigrationException>().WithMessage("*未知字段*");
        using (var db = business.CreateClient()) db.DbMaintenance.IsAnyTable("request_logs", false).Should().BeTrue();

        // A valid source that conflicts with an existing target row must fail and retain the source.
        using (var db = business.CreateClient())
        {
            db.Ado.ExecuteCommand("ALTER TABLE request_logs DROP COLUMN Secret");
            db.Insertable(new RequestLogEntity { Id = "conflict", TraceId = "source", CreatedAtUtc = new(2026, 11, 5) }).AS("request_logs").ExecuteCommand();
        }
        using (var db = logs.CreateClient())
            db.Insertable(new RequestLogEntity { Id = "conflict", TraceId = "target", CreatedAtUtc = new(2026, 11, 5) }).SplitTable().ExecuteCommand();
        var inventory = LogDatabaseMigration.Scan(logs, business.FilePath);
        Func<Task> migrate = () => LogDatabaseMigration.MigrateAsync(logs, business.FilePath, inventory, _ => { }, CancellationToken.None);
        await migrate.Should().ThrowAsync<LogMigrationException>().WithMessage("*冲突*");
        using var sourceDb = business.CreateClient();
        sourceDb.Ado.GetInt("SELECT COUNT(*) FROM request_logs WHERE Id='conflict'").Should().Be(1);
    }

    [TestMethod]
    public async Task RechecksSourceSchemaInsideDeleteTransactionBeforeDroppingTable()
    {
        var (business, logs) = Databases();
        new DatabaseInitializer(business, logs, NullLogger<DatabaseInitializer>.Instance).Initialize();
        using (var db = business.CreateClient())
        {
            CreateLegacy<RequestLogEntity>(db, "request_logs");
            db.Insertable(new RequestLogEntity { Id = "schema-race", TraceId = "trace", CreatedAtUtc = new(2026, 11, 5) }).AS("request_logs").ExecuteCommand();
        }
        var inventory = LogDatabaseMigration.Scan(logs, business.FilePath);
        var altered = false;
        Func<Task> migrate = () => LogDatabaseMigration.MigrateAsync(logs, business.FilePath, inventory, progress =>
        {
            if (progress.Phase != "verifying" || progress.VerifiedRows == 0 || altered) return;
            altered = true;
            using var db = business.CreateClient();
            db.Ado.ExecuteCommand("ALTER TABLE request_logs ADD COLUMN AddedDuringMigration TEXT");
        }, CancellationToken.None);
        await migrate.Should().ThrowAsync<LogMigrationException>().WithMessage("*未知字段*");
        using var source = business.CreateClient();
        source.DbMaintenance.IsAnyTable("request_logs", false).Should().BeTrue();
        source.Ado.GetInt("SELECT COUNT(*) FROM request_logs").Should().Be(1);
    }

    [TestMethod]
    public void RejectsMalformedReservedDailyTableNames()
    {
        var (business, logs) = Databases();
        new DatabaseInitializer(business, logs, NullLogger<DatabaseInitializer>.Instance).Initialize();
        using (var db = business.CreateClient()) db.Ado.ExecuteCommand("CREATE TABLE request_logs_20261340 (Id TEXT)");
        Action scan = () => LogDatabaseMigration.Scan(logs, business.FilePath);
        scan.Should().Throw<LogMigrationException>().WithMessage("*UTC 日期*");
    }

    private static (SqlSugarDatabase Business, SqlSugarDatabase Logs) Databases()
    {
        var path = Path.Combine(Path.GetTempPath(), $"log-migration-{Guid.NewGuid():N}.db");
        var options = Options.Create(new DatabaseOptions { Path = path });
        return (new SqlSugarDatabase(options, NullLogger<SqlSugarDatabase>.Instance), SqlSugarDatabase.CreateLogs(options, NullLogger<SqlSugarDatabase>.Instance));
    }

    private static void CreateLegacy<T>(SqlSugarClient db, string table) where T : class, new()
    {
        db.MappingTables.Add(typeof(T).Name, table);
        db.CodeFirst.InitTables<T>();
        db.MappingTables.RemoveAll(mapping => mapping.EntityName == typeof(T).Name);
    }
}
