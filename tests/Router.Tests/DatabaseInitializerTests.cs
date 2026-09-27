using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class DatabaseInitializerTests
{
    [TestMethod]
    public void CodeFirstInitializationIsIdempotent()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"router2api-{Guid.NewGuid():N}.db");
        var database = new SqlSugarDatabase(
            Options.Create(new DatabaseOptions { Path = path }),
            NullLogger<SqlSugarDatabase>.Instance);

        try
        {
            var initializer = new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance);
            initializer.Initialize();
            initializer.Initialize();

            File.Exists(path).Should().BeTrue();
            using var db = database.CreateClient();
            db.Queryable<AccountEntity>().Count().Should().Be(0);
            db.Queryable<ResourceEventEntity>().Count().Should().Be(0);
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
        var database = new SqlSugarDatabase(
            Options.Create(new DatabaseOptions { Path = path }),
            NullLogger<SqlSugarDatabase>.Instance);

        try
        {
            new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance).Initialize();
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
