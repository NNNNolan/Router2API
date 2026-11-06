using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class DatabaseInitializerTests
{
    [TestMethod]
    public void LogPathDefaultsBesideBusinessDatabaseAndSamePathIsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"router-paths-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "router.db");
        new DatabaseOptions { Path = path }.GetLogPath().Should().Be(Path.Combine(directory, "router.logs.db"));
        var options = Options.Create(new DatabaseOptions { Path = path, LogPath = path });
        Action createBusiness = () => GC.KeepAlive(new SqlSugarDatabase(options, NullLogger<SqlSugarDatabase>.Instance));
        Action createLogs = () => GC.KeepAlive(SqlSugarDatabase.CreateLogs(options, NullLogger<SqlSugarDatabase>.Instance));
        createBusiness.Should().Throw<InvalidOperationException>();
        createLogs.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void DatabasePathsWithConnectionStringSeparatorsRemainValidFilenames()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"router-semicolon-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "business;name.db");
        var database = new SqlSugarDatabase(Options.Create(new DatabaseOptions { Path = path }), NullLogger<SqlSugarDatabase>.Instance);
        using (var db = database.CreateClient()) db.DbMaintenance.CreateDatabase();
        File.Exists(path).Should().BeTrue();
    }

    [TestMethod]
    public async Task DependencyInjectionRoutesLogServicesToKeyedDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"router-di-db-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Path"] = Path.Combine(directory, "business.db")
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouterInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        var business = provider.GetRequiredService<SqlSugarDatabase>();
        var logs = provider.GetRequiredKeyedService<SqlSugarDatabase>(SqlSugarDatabase.LogsKey);
        business.FilePath.Should().Be(Path.Combine(directory, "business.db"));
        logs.FilePath.Should().Be(Path.Combine(directory, "business.logs.db"));
        provider.GetRequiredService<DatabaseInitializer>().Initialize();

        var sink = provider.GetRequiredService<RequestLogStore>();
        var createdAt = DateTimeOffset.UtcNow;
        var request = new RequestLog { Id = "split-routing", TraceId = "split-routing", Platform = "test", Model = "model",
            CreatedAt = createdAt, Success = true, Usage = new(2, 3, 5) };
        await sink.WriteAsync(request);
        await provider.GetRequiredService<IUsageBucketStore>().RecordAsync(request);
        await provider.GetRequiredService<ILogSink<ResourceEvent>>().WriteAsync(new ResourceEvent
        {
            Id = "split-resource", ResourceType = "account", ResourceId = "a", CreatedAt = createdAt
        });
        await provider.GetRequiredService<IPluginLogStore>().WriteAsync(new PluginLog
        {
            Id = "split-plugin", PluginKey = "test", Level = "Information", EventType = "test", Message = "split", CreatedAt = createdAt
        });
        await provider.GetRequiredService<ITaskLogStore>().WriteAsync(new TaskLog
        {
            Id = "split-task", PluginKey = "test", TaskName = "test", Status = "Success", StartedAt = createdAt
        });
        var analytics = await provider.GetRequiredService<AnalyticsService>().QueryUsageAsync(new(createdAt.AddSeconds(-1), createdAt.AddMinutes(1)));
        analytics.Requests.Should().Be(1);
        var dayStart = new DateTimeOffset(createdAt.UtcDateTime.Date, TimeSpan.Zero);
        var dashboard = await provider.GetRequiredService<DashboardMetricsService>().QueryAsync(dayStart, dayStart.AddDays(1), createdAt, 0);
        dashboard.TotalRequests.Should().Be(1);
        provider.GetRequiredService<RequestObservationsService>().Find("split-routing").Should().NotBeNull();
        using var businessDb = business.CreateClient();
        using var logDb = logs.CreateClient();
        businessDb.DbMaintenance.IsAnyTable($"request_logs_{DateTime.UtcNow:yyyyMMdd}", false).Should().BeFalse();
        foreach (var prefix in new[] { "request_logs", "resource_events", "plugin_logs", "task_logs" })
        {
            var table = $"{prefix}_{DateTime.UtcNow:yyyyMMdd}";
            businessDb.DbMaintenance.IsAnyTable(table, false).Should().BeFalse();
            logDb.DbMaintenance.IsAnyTable(table, false).Should().BeTrue();
        }
        businessDb.DbMaintenance.IsAnyTable("usage_buckets", false).Should().BeFalse();
        logDb.Queryable<UsageBucketEntity>().Count().Should().Be(1);
        logDb.Queryable<ResourceEventEntity>().SplitTable().Count().Should().Be(1);
        logDb.Queryable<PluginLogEntity>().SplitTable().Count().Should().Be(1);
        logDb.Queryable<TaskLogEntity>().SplitTable().Count().Should().Be(1);
    }

    [TestMethod]
    public void CodeFirstInitializationIsIdempotent()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"router2api-{Guid.NewGuid():N}.db");
        var options = Options.Create(new DatabaseOptions { Path = path });
        var database = new SqlSugarDatabase(
            options,
            NullLogger<SqlSugarDatabase>.Instance);
        var logs = SqlSugarDatabase.CreateLogs(options, NullLogger<SqlSugarDatabase>.Instance);

        try
        {
            var initializer = new DatabaseInitializer(database, logs, NullLogger<DatabaseInitializer>.Instance);
            initializer.Initialize();
            initializer.Initialize();

            File.Exists(path).Should().BeTrue();
            using var db = database.CreateClient();
            db.Queryable<AccountEntity>().Count().Should().Be(0);
            var todayTable = $"request_logs_{DateTime.UtcNow:yyyyMMdd}";
            db.DbMaintenance.IsAnyTable(todayTable, false).Should().BeFalse();
            using var logDb = logs.CreateClient();
            logDb.DbMaintenance.IsAnyTable(todayTable, false).Should().BeTrue();
            logDb.Queryable<ResourceEventEntity>().SplitTable().Count().Should().Be(0);
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // SqlSugar 可能会一直持有 SQLite 句柄，直到进程级连接池被回收。
            }
        }
    }

    [TestMethod]
    public async Task AccountRefreshPersistsThePluginReturnedCredential()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"router2api-{Guid.NewGuid():N}.db");
        var options = Options.Create(new DatabaseOptions { Path = path });
        var database = new SqlSugarDatabase(
            options,
            NullLogger<SqlSugarDatabase>.Instance);
        var logs = SqlSugarDatabase.CreateLogs(options, NullLogger<SqlSugarDatabase>.Instance);

        try
        {
            new DatabaseInitializer(database, logs, NullLogger<DatabaseInitializer>.Instance).Initialize();
            var state = new Mock<ISharedKeyValueStore>();
            state.SetupGet(value => value.IsConfigured).Returns(true);
            var accounts = new AccountService(database, state.Object);
            await accounts.SaveAsync(new Account
            {
                Id = "account-refresh",
                PluginKey = "myai",
                Platform = "myai",
                Credential = new ApiKeyCredential("old-key")
            });

            await accounts.RefreshAsync(
                "myai",
                "account-refresh",
                (account, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    account.Credential = new ApiKeyCredential("new-key");
                    return Task.FromResult(account);
                });

            var saved = await accounts.GetAsync("myai", "account-refresh");
            saved.Should().NotBeNull();
            saved!.Credential.Should().Be(new ApiKeyCredential("new-key"));

            Func<Task> changeIdentity = () => accounts.RefreshAsync("myai", "account-refresh", (account, _) =>
            {
                account.Platform = "foreign-platform";
                return Task.FromResult(account);
            });
            await changeIdentity.Should().ThrowAsync<InvalidOperationException>();
            (await accounts.GetAsync("myai", "account-refresh"))!.Platform.Should().Be("myai");

            state.Invocations.Clear();
            saved.Status.CooldownUntil = DateTimeOffset.UtcNow.AddHours(1);
            await accounts.SaveAsync(saved);
            state.Verify(value => value.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            saved.Status.CooldownUntil = null;
            await accounts.SaveAsync(saved);
            state.Verify(value => value.RemoveAsync("account:cooldown:myai:account-refresh", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // SqlSugar 可能会一直持有 SQLite 句柄，直到进程级连接池被回收。
            }
        }
    }
}
