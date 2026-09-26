using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSugar;
using Router.Infrastructure.Services;

namespace Router.Infrastructure.Persistence;

/// <summary>持有进程级 SqlSugar 作用域。</summary>
public sealed class SqlSugarDatabase
{
    public SqlSugarDatabase(
        IOptions<DatabaseOptions> options,
        ILogger<SqlSugarDatabase> logger,
        RedisKeyValueStore? redis = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var path = options.Value.Path;
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        FactCacheEnabled = redis is { IsConfigured: true };

        var config = new ConnectionConfig
        {
            ConnectionString = $"DataSource={fullPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            MoreSettings = new ConnMoreSettings
            {
                IsAutoRemoveDataCache = true
            }
        };
        if (redis is { IsConfigured: true })
        {
            config.ConfigureExternalServices = new ConfigureExternalServices
            {
                DataInfoCacheService = new SqlSugarRedisCache(redis, logger)
            };
        }

        Scope = new SqlSugarScope(config, db =>
        {
            db.Aop.OnLogExecuting = (sql, parameters) =>
                logger.LogDebug("sql={Sql} parameters={Parameters}", sql, parameters);
        });
    }

    public SqlSugarScope Scope { get; }
    public bool FactCacheEnabled { get; }
}
