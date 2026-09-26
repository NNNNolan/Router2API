using System.Text.Json;
using CSRedis;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Router.Infrastructure.Persistence;

namespace Router.Infrastructure.Services;

/// <summary>
/// SqlSugar 的 Redis 二级缓存适配器。只使用独立的事实缓存命名空间，
/// 不参与插件节点策略、账号冷却或任务锁。
/// </summary>
public sealed class SqlSugarRedisCache(
    RedisKeyValueStore redis,
    ILogger<SqlSugarDatabase> logger) : ICacheService
{
    private const string Namespace = "sqlsugar:v1:";

    public void Add<T>(string key, T value)
        => Add(key, value, 300);

    public void Add<T>(string key, T value, int cacheDurationInSeconds)
    {
        if (value is null) return;
        try
        {
            redis.RequireClient().Set(
                PhysicalKey(key),
                value is string text ? text : JsonSerializer.Serialize(value),
                TimeSpan.FromSeconds(Math.Max(1, cacheDurationInSeconds)));
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "sqlsugar redis cache write failed key={Key}", key);
        }
    }

    public bool ContainsKey<T>(string key)
    {
        try { return redis.RequireClient().Exists(PhysicalKey(key)); }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "sqlsugar redis cache lookup failed key={Key}", key);
            return false;
        }
    }

    public T? Get<T>(string key)
    {
        try
        {
            var value = redis.RequireClient().Get<string>(PhysicalKey(key));
            if (string.IsNullOrWhiteSpace(value)) return default;
            if (typeof(T) == typeof(string)) return (T)(object)value;
            return JsonSerializer.Deserialize<T>(value);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "sqlsugar redis cache read failed key={Key}", key);
            return default;
        }
    }

    public IEnumerable<string> GetAllKey<T>()
    {
        try
        {
            var keys = new List<string>();
            var cursor = 0L;
            var pattern = redis.Key($"{Namespace}*");
            do
            {
                var scan = redis.RequireClient().Scan(cursor, pattern, 200);
                keys.AddRange(scan.Items);
                cursor = scan.Cursor;
            }
            while (cursor != 0);

            return keys;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "sqlsugar redis cache key scan failed");
            return [];
        }
    }

    public void Remove<T>(string key)
    {
        try { redis.RequireClient().Del(PhysicalKey(key)); }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "sqlsugar redis cache remove failed key={Key}", key);
        }
    }

    public T GetOrCreate<T>(string cacheKey, Func<T> create, int cacheDurationInSeconds)
    {
        if (ContainsKey<T>(cacheKey)) return Get<T>(cacheKey)!;

        var created = create();
        if (created is not null) Add(cacheKey, created, cacheDurationInSeconds);
        return created;
    }

    private string PhysicalKey(string key)
        => key.StartsWith(redis.Key(Namespace), StringComparison.Ordinal)
            ? key
            : redis.Key($"{Namespace}{key}");
}
