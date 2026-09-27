using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSugar;
using Router.Infrastructure.Services;

namespace Router.Infrastructure.Persistence;

/// <summary>复用 SqlSugar 配置，为每次数据库操作创建独立客户端。</summary>
public sealed class SqlSugarDatabase
{
    private readonly string _connectionString;
    private readonly ILogger<SqlSugarDatabase> _logger;
    private readonly SqlSugarRedisCache? _cache;

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
        _connectionString = $"DataSource={fullPath}";
        _logger = logger;
        if (redis is { IsConfigured: true })
            _cache = new SqlSugarRedisCache(redis, logger);
    }

    /// <summary>创建独立客户端，避免并发任务共享 AsyncLocal 中的连接并提前关闭 reader。</summary>
    /// <returns>由调用方通过 using 释放的客户端。</returns>
    public SqlSugarClient CreateClient()
    {
        var config = new ConnectionConfig
        {
            ConnectionString = _connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            MoreSettings = new ConnMoreSettings
            {
                IsAutoRemoveDataCache = true
            }
        };
        if (_cache is not null)
        {
            config.ConfigureExternalServices = new ConfigureExternalServices
            {
                DataInfoCacheService = _cache
            };
        }

        return new SqlSugarClient(config, db =>
        {
            db.Aop.OnLogExecuting = (sql, parameters) =>
                _logger.LogDebug("sql={Sql} parameters={Parameters}", sql, parameters);
        });
    }

    public bool FactCacheEnabled => _cache is not null;
}
