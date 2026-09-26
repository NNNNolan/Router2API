using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Router.Infrastructure.Persistence;
using Router.Host.Security;
using Router.Contracts.Host;

namespace Router.Host.Configuration;

public interface IConfigFileService
{
    string FilePath { get; }
    ConfigSnapshot Get();
    void EnsureCreated();
    Task<ConfigSnapshot> SaveAsync(ConfigUpdateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 管理启动目录下的 Config/Config.json。
/// 配置源顺序由 Program 保证：Config.json > User Secrets > ROUTER2API_ 环境变量。
/// </summary>
public sealed class ConfigFileService(
    IConfiguration configuration,
    IOptionsMonitor<AuthOptions> auth,
    IOptionsMonitor<DatabaseOptions> database,
    IOptionsMonitor<RedisOptions> redis,
    IOptionsMonitor<LogRetentionOptions> retention,
    IOptionsMonitor<PluginLogOptions> pluginLogs,
    ApiKeyService apiKeys,
    ILogger<ConfigFileService> logger) : IConfigFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string FilePath { get; } = Path.Combine(
        Directory.GetCurrentDirectory(),
        "Config",
        "Config.json");

    public ConfigSnapshot Get()
        => new(
            FilePath,
            File.Exists(FilePath),
            File.Exists(FilePath) ? "Config.json（最高优先级）" : "用户机密 > ROUTER2API_ 环境变量",
            new ConfigAuthSnapshot(
                auth.CurrentValue.Admin.Enabled,
                auth.CurrentValue.Admin.SessionLifetimeHours,
                auth.CurrentValue.Admin.Users.Count,
                auth.CurrentValue.ApiKey.Enabled,
                apiKeys.Masked(),
                !string.IsNullOrWhiteSpace(apiKeys.Current)),
            new ConfigDatabaseSnapshot(database.CurrentValue.Path),
            new ConfigRedisSnapshot(redis.CurrentValue.ConnectionString, redis.CurrentValue.InstanceName),
            new ConfigLoggingSnapshot(
                retention.CurrentValue.DataRetentionDays,
                retention.CurrentValue.CleanupCron,
                pluginLogs.CurrentValue.MinimumPluginLogLevel));

    public void EnsureCreated()
    {
        if (File.Exists(FilePath)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        WriteJson(BuildInitialDocument());
        ReloadConfiguration();
        logger.LogInformation("created configuration file at {ConfigPath}; precedence is Config.json > user secrets > environment", FilePath);
    }

    public Task<ConfigSnapshot> SaveAsync(
        ConfigUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Auth is not null)
        {
            if (request.Auth.SessionLifetimeHours is < 1 or > 168)
                throw new ArgumentOutOfRangeException(nameof(request), "管理员会话时长必须在 1 到 168 小时之间。");
            if (request.Auth.ApiKey?.Key?.Length > 256)
                throw new ArgumentOutOfRangeException(nameof(request), "API Key 长度不能超过 256 个字符。");
        }

        if (request.Logging is not null)
        {
            if (request.Logging.DataRetentionDays is < 1 or > 3650)
                throw new ArgumentOutOfRangeException(nameof(request), "日志保留天数必须在 1 到 3650 天之间。");
            if (string.IsNullOrWhiteSpace(request.Logging.CleanupCron))
                throw new ArgumentException("日志清理 Cron 不能为空。", nameof(request));
            if (!PluginLogOptions.IsSupportedLevel(request.Logging.MinimumPluginLogLevel))
                throw new ArgumentException("插件日志最低等级只能是 Debug、Information、Warning 或 Error。", nameof(request));
        }

        var root = ReadDocument();
        if (request.Auth is { } authUpdate)
        {
            var authNode = GetOrCreateObject(root, "Auth");
            var adminNode = GetOrCreateObject(authNode, "Admin");
            if (authUpdate.Enabled is { } adminEnabled) adminNode["Enabled"] = adminEnabled;
            if (authUpdate.SessionLifetimeHours is { } sessionHours) adminNode["SessionLifetimeHours"] = sessionHours;

            if (authUpdate.ApiKey is { } apiKeyUpdate)
            {
                var apiKeyNode = GetOrCreateObject(authNode, "ApiKey");
                if (apiKeyUpdate.Enabled is { } apiKeyEnabled) apiKeyNode["Enabled"] = apiKeyEnabled;
                if (!string.IsNullOrWhiteSpace(apiKeyUpdate.Key)) apiKeyNode["Key"] = apiKeyUpdate.Key.Trim();
            }
        }

        if (request.Database is { } databaseUpdate && !string.IsNullOrWhiteSpace(databaseUpdate.Path))
            GetOrCreateObject(root, "Database")["Path"] = databaseUpdate.Path.Trim();

        if (request.Redis is { } redisUpdate)
        {
            var redisNode = GetOrCreateObject(root, "Redis");
            if (!string.IsNullOrWhiteSpace(redisUpdate.ConnectionString))
                redisNode["ConnectionString"] = redisUpdate.ConnectionString.Trim();
            if (redisUpdate.InstanceName is not null)
                redisNode["InstanceName"] = redisUpdate.InstanceName.Trim();
        }

        if (request.Logging is { } loggingUpdate)
        {
            var loggingNode = GetOrCreateObject(root, "Logging");
            loggingNode["DataRetentionDays"] = loggingUpdate.DataRetentionDays;
            loggingNode["CleanupCron"] = loggingUpdate.CleanupCron.Trim();
            loggingNode["MinimumPluginLogLevel"] = loggingUpdate.MinimumPluginLogLevel;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        WriteJson(root);
        ReloadConfiguration();
        return Task.FromResult(Get());
    }

    private JsonObject BuildInitialDocument()
        => new()
        {
            ["Database"] = new JsonObject { ["Path"] = database.CurrentValue.Path },
            ["Redis"] = new JsonObject
            {
                ["ConnectionString"] = redis.CurrentValue.ConnectionString,
                ["InstanceName"] = redis.CurrentValue.InstanceName
            },
            ["Auth"] = new JsonObject
            {
                ["Admin"] = new JsonObject
                {
                    ["Enabled"] = auth.CurrentValue.Admin.Enabled,
                    ["SessionLifetimeHours"] = auth.CurrentValue.Admin.SessionLifetimeHours
                },
                ["ApiKey"] = new JsonObject { ["Enabled"] = auth.CurrentValue.ApiKey.Enabled }
            },
            ["Logging"] = new JsonObject
            {
                ["DataRetentionDays"] = retention.CurrentValue.DataRetentionDays,
                ["CleanupCron"] = retention.CurrentValue.CleanupCron,
                ["MinimumPluginLogLevel"] = pluginLogs.CurrentValue.MinimumPluginLogLevel
            }
        };

    private JsonObject ReadDocument()
    {
        if (!File.Exists(FilePath)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("配置文件格式无效：" + FilePath, exception);
        }
    }

    private void WriteJson(JsonObject root)
    {
        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporaryPath, root.ToJsonString(JsonOptions));
        File.Move(temporaryPath, FilePath, true);
    }

    private void ReloadConfiguration()
    {
        if (configuration is IConfigurationRoot root)
            root.Reload();
    }

    private static JsonObject GetOrCreateObject(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonObject existing) return existing;
        var created = new JsonObject();
        root[propertyName] = created;
        return created;
    }
}

public sealed record ConfigSnapshot(
    string ConfigPath,
    bool FileExists,
    string Source,
    ConfigAuthSnapshot Auth,
    ConfigDatabaseSnapshot Database,
    ConfigRedisSnapshot Redis,
    ConfigLoggingSnapshot Logging);

public sealed record ConfigAuthSnapshot(
    bool AdminEnabled,
    int SessionLifetimeHours,
    int AdministratorCount,
    bool ApiKeyEnabled,
    string ApiKeyPreview,
    bool ApiKeyConfigured);

public sealed record ConfigDatabaseSnapshot(string Path);

public sealed record ConfigRedisSnapshot(string ConnectionString, string InstanceName);

public sealed record ConfigLoggingSnapshot(int DataRetentionDays, string CleanupCron, string MinimumPluginLogLevel);

public sealed class ConfigUpdateRequest
{
    public ConfigAuthUpdate? Auth { get; init; }
    public ConfigDatabaseUpdate? Database { get; init; }
    public ConfigRedisUpdate? Redis { get; init; }
    public ConfigLoggingUpdate? Logging { get; init; }
}

public sealed class ConfigAuthUpdate
{
    public bool? Enabled { get; init; }
    public int? SessionLifetimeHours { get; init; }
    public ConfigApiKeyUpdate? ApiKey { get; init; }
}

public sealed class ConfigApiKeyUpdate
{
    public bool? Enabled { get; init; }
    public string? Key { get; init; }
}

public sealed class ConfigDatabaseUpdate
{
    public string? Path { get; init; }
}

public sealed class ConfigRedisUpdate
{
    public string? ConnectionString { get; init; }
    public string? InstanceName { get; init; }
}

public sealed class ConfigLoggingUpdate
{
    public int DataRetentionDays { get; init; } = 30;
    public string CleanupCron { get; init; } = "0 0 3 * * *";
    public string MinimumPluginLogLevel { get; init; } = "Information";
}
