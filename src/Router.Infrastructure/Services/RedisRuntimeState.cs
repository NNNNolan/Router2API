using CSRedis;
using Microsoft.Extensions.Options;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;

namespace Router.Infrastructure.Services;

/// <summary>CSRedisCore 运行态封装。这里不保存业务事实，只保存带 TTL 的短期状态。</summary>
public sealed class RedisKeyValueStore : ISharedKeyValueStore, IDisposable
{
    private const string SetIfLaterScript = """
        local requested = tonumber(ARGV[1])
        local existing = tonumber(redis.call('GET', KEYS[1]) or '0')
        if existing >= requested then return 0 end
        redis.call('SET', KEYS[1], ARGV[2], 'PXAT', requested)
        return 1
        """;
    private const string TryAcquireScript = """
        local result = redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2])
        if result then return 1 end
        return 0
        """;

    private readonly CSRedisClient? _client;
    private readonly string _prefix;

    public RedisKeyValueStore(IOptions<RedisOptions> options)
    {
        var value = options.Value;
        _prefix = value.InstanceName ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(value.ConnectionString)
            && !string.Equals(value.ConnectionString, "disabled", StringComparison.OrdinalIgnoreCase))
            _client = new CSRedisClient(value.ConnectionString);
    }

    public bool IsConfigured => _client is not null;

    public async Task<string?> GetStringAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await RequireClient().GetAsync(Key(key));
    }

    public async Task<bool> SetStringAsync(
        string key,
        string value,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ttl <= TimeSpan.Zero) return false;
        return await RequireClient().SetAsync(Key(key), value, ttl);
    }

    public async Task<DateTimeOffset?> GetExpiryAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = RequireClient();
        var ttl = await client.PTtlAsync(Key(key));
        return ttl switch
        {
            > 0 => DateTimeOffset.UtcNow.AddMilliseconds(ttl),
            _ => null
        };
    }

    public async Task<bool> SetExpiryAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ttl <= TimeSpan.Zero) return false;
        return await RequireClient().SetAsync(Key(key), "1", ttl);
    }

    public async Task<bool> SetExpiryIfLaterAsync(
        string key,
        DateTimeOffset until,
        string value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (until <= DateTimeOffset.UtcNow) return false;

        var result = await RequireClient().EvalAsync(
            SetIfLaterScript,
            Key(key),
            until.ToUnixTimeMilliseconds(),
            value);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public async Task<bool> TryAcquireAsync(
        string key,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ttl <= TimeSpan.Zero) return false;
        var result = await RequireClient().EvalAsync(
            TryAcquireScript,
            Key(key),
            "1",
            Math.Max(1, (long)ttl.TotalMilliseconds));
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RequireClient().DelAsync(Key(key));
    }

    public async Task<bool> PutIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await RequireClient().EvalAsync(
            "return redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2]) and 1 or 0",
            Key(key), value, Math.Max(1, (long)ttl.TotalMilliseconds));
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public async Task<long> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Return the stored string: Lua numbers lose precision above 2^53.
        var result = await RequireClient().EvalAsync(
            "redis.call('INCRBY', KEYS[1], ARGV[1]); redis.call('PEXPIRE', KEYS[1], ARGV[2]); return redis.call('GET', KEYS[1])",
            Key(key), delta.ToString(System.Globalization.CultureInfo.InvariantCulture), Math.Max(1, (long)ttl.TotalMilliseconds));
        return long.Parse(Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture)!, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<bool> CompareExchangeAsync(string key, string? expected, string? value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string script = """
            local current = redis.call('GET', KEYS[1])
            if ARGV[1] == 'missing' then
              if current then return 0 end
            elseif not current or current ~= ARGV[2] then return 0 end
            if ARGV[3] == 'delete' then redis.call('DEL', KEYS[1])
            else redis.call('SET', KEYS[1], ARGV[4], 'PX', ARGV[5]) end
            return 1
            """;
        var result = await RequireClient().EvalAsync(script, Key(key), expected is null ? "missing" : "value", expected ?? "",
            value is null ? "delete" : "value", value ?? "", Math.Max(1, (long)ttl.TotalMilliseconds));
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    internal CSRedisClient RequireClient()
        => _client ?? throw new InvalidOperationException("Redis runtime state is not configured");

    internal string Key(string key) => $"{_prefix}{key}";

    public void Dispose() => _client?.Dispose();
}

/// <summary>按插件和节点配置版本存储 Redis 冷却策略；Redis 不可用时调用方必须失败关闭。</summary>
public sealed class RedisProxyPolicyStore(RedisKeyValueStore redis) : IProxyPolicyStore
{
    private const string ReportScript = """
        local now = tonumber(ARGV[1])
        local current = tonumber(redis.call('HGET', KEYS[1], 'cooldownUntil') or '0')
        local failures = redis.call('HINCRBY', KEYS[1], 'failureCount', 1)
        local seconds = math.min(1800, 2 ^ math.min(8, failures))
        local requested = now + seconds * 1000
        local ttl = seconds * 1000
        local currentTtl = redis.call('PTTL', KEYS[1])
        if current > requested and currentTtl > ttl then ttl = currentTtl end
        if current > requested then requested = current end
        redis.call('HSET', KEYS[1],
            'state', 'Cooldown',
            'cooldownUntil', requested,
            'reason', ARGV[2],
            'lastStatusCode', ARGV[3],
            'lastFailureAt', ARGV[4])
        redis.call('PEXPIRE', KEYS[1], ttl)
        return failures
        """;

    public async Task<ProxyPolicySnapshot?> GetAsync(
        string pluginKey,
        ProxyEndpoint proxy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fields = await redis.RequireClient().HGetAllAsync(
            redis.Key(Key(pluginKey, proxy)));
        if (fields.Count == 0)
            return new ProxyPolicySnapshot(ProxyPolicyState.Available, null, 0, null, null, null);

        if (!Enum.TryParse<ProxyPolicyState>(Get(fields, "state"), out var state)
            || !long.TryParse(Get(fields, "cooldownUntil"), out var cooldownMilliseconds)
            || !int.TryParse(Get(fields, "failureCount"), out var failureCount)
            || !long.TryParse(Get(fields, "lastFailureAt"), out var failureMilliseconds))
            throw new InvalidOperationException("Redis proxy policy state is invalid");

        int? statusCode = int.TryParse(Get(fields, "lastStatusCode"), out var parsedStatusCode)
            ? parsedStatusCode
            : null;
        return new ProxyPolicySnapshot(
            state,
            DateTimeOffset.FromUnixTimeMilliseconds(cooldownMilliseconds),
            failureCount,
            Get(fields, "reason"),
            statusCode,
            DateTimeOffset.FromUnixTimeMilliseconds(failureMilliseconds));
    }

    public async Task ReportAsync(
        string pluginKey,
        ProxyEndpoint proxy,
        PluginAttemptResult result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = redis.RequireClient();
        var key = redis.Key(Key(pluginKey, proxy));
        if (result.Outcome is PluginAttemptOutcome.Healthy or PluginAttemptOutcome.NoPenalty)
        {
            await client.DelAsync(key);
            return;
        }

        if (result.Outcome != PluginAttemptOutcome.CooldownNode)
            return;

        var now = DateTimeOffset.UtcNow;
        await client.EvalAsync(
            ReportScript,
            key,
            now.ToUnixTimeMilliseconds(),
            result.Reason ?? (result.IsTransportFailure ? "transport-failure" : "plugin-rule"),
            result.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            now.ToUnixTimeMilliseconds());
    }

    private static string? Get(Dictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var value) ? value : null;

    private static string Key(string pluginKey, ProxyEndpoint proxy)
        => $"proxy-policy:{pluginKey}:{proxy.Id}:{proxy.ConfigurationVersion}";
}
