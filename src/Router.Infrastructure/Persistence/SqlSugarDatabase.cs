using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSugar;
using Router.Infrastructure.Services;

namespace Router.Infrastructure.Persistence;

/// <summary>复用 SqlSugar 配置，为每次数据库操作创建独立客户端。</summary>
public sealed class SqlSugarDatabase
{
    public const string LogsKey = "logs";
    private readonly string _connectionString;
    private readonly ILogger<SqlSugarDatabase> _logger;
    private readonly SqlSugarRedisCache? _cache;
    private readonly Action<SqlSugarClient> _configureClient;
    private readonly Action<string, SugarParameter[]> _logExecuting;

    public SqlSugarDatabase(IOptions<DatabaseOptions> options, ILogger<SqlSugarDatabase> logger, RedisKeyValueStore? redis = null)
        : this(logger, ResolveBusinessPath(options), redis) { }

    private SqlSugarDatabase(ILogger<SqlSugarDatabase> logger, string path, RedisKeyValueStore? redis)
    {
        ArgumentNullException.ThrowIfNull(logger);
        FilePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var connection = new DbConnectionStringBuilder { ["Data Source"] = FilePath };
        _connectionString = connection.ConnectionString;
        _logger = logger;
        _configureClient = ConfigureClient;
        _logExecuting = LogExecuting;
        if (redis is { IsConfigured: true })
            _cache = new SqlSugarRedisCache(redis, logger);
    }

    public string FilePath { get; }

    public static SqlSugarDatabase CreateLogs(IOptions<DatabaseOptions> options, ILogger<SqlSugarDatabase> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        var businessPath = Path.GetFullPath(settings.Path);
        var logsPath = settings.GetLogPath();
        EnsureDistinctPaths(businessPath, logsPath);
        return new(logger, logsPath, null);
    }

    private static string ResolveBusinessPath(IOptions<DatabaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        var path = Path.GetFullPath(settings.Path);
        EnsureDistinctPaths(path, settings.GetLogPath());
        return path;
    }

    private static void EnsureDistinctPaths(string businessPath, string logsPath)
    {
        if (string.Equals(businessPath, logsPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Business and log database paths must be different.");
    }

    /// <summary>创建独立客户端，避免并发任务共享 AsyncLocal 中的连接并提前关闭 reader。</summary>
    public SqlSugarClient CreateClient()
    {
        var config = new ConnectionConfig
        {
            ConnectionString = _connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            MoreSettings = new ConnMoreSettings { IsAutoRemoveDataCache = true }
        };
        if (_cache is not null)
            config.ConfigureExternalServices = new ConfigureExternalServices { DataInfoCacheService = _cache };
        return new SqlSugarClient(config, _configureClient);
    }

    private void ConfigureClient(SqlSugarClient db) => db.Aop.OnLogExecuting = _logExecuting;

    private void LogExecuting(string sql, SugarParameter[] parameters)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("sql={Sql} parameters={Parameters}", sql, parameters);
    }

    public bool FactCacheEnabled => _cache is not null;
}
