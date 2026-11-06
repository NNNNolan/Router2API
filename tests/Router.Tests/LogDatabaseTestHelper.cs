using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Router.Infrastructure.Persistence;

namespace Router.Tests;

internal static class LogDatabaseTestHelper
{
    public static SqlSugarDatabase For(SqlSugarDatabase business)
    {
        var options = Options.Create(new DatabaseOptions { Path = business.FilePath });
        return SqlSugarDatabase.CreateLogs(options, NullLogger<SqlSugarDatabase>.Instance);
    }

    public static DatabaseInitializer Initializer(SqlSugarDatabase business)
        => new(business, For(business), NullLogger<DatabaseInitializer>.Instance);
}
