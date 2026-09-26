using System.Text;
using System.Globalization;
using Router.Contracts.Host;

namespace Router.Infrastructure.Services;

internal sealed class PluginMemoryState : IPluginStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Value, DateTimeOffset ExpiresAt)> _values = new(StringComparer.Ordinal);
    public bool IsAvailable => true;

    public Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
    {
        PluginStateLimits.ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RemoveExpired();
            return Task.FromResult(_values.TryGetValue(key, out var entry) ? entry.Value : null);
        }
    }

    public Task<bool> SetStringAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        PluginStateLimits.Validate(key, value, ttl, 64 * 1024, TimeSpan.FromDays(1));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RemoveExpired();
            if (_values.Count >= 256 && !_values.ContainsKey(key))
                throw new InvalidOperationException("Plugin local state entry quota exceeded.");
            _values[key] = (value, DateTimeOffset.UtcNow.Add(ttl));
            return Task.FromResult(true);
        }
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        PluginStateLimits.ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _values.Remove(key);
        return Task.CompletedTask;
    }

    public Task<bool> PutIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
        => CompareExchangeAsync(key, null, value, ttl, cancellationToken);

    public Task<bool> CompareExchangeAsync(string key, string? expected, string? value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        PluginStateLimits.Validate(key, value ?? "", ttl, 64 * 1024, TimeSpan.FromDays(1));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RemoveExpired();
            var found = _values.TryGetValue(key, out var entry);
            if (expected != (found ? entry.Value : null)) return Task.FromResult(false);
            if (value is null) _values.Remove(key);
            else
            {
                if (!found && _values.Count >= 256) throw new InvalidOperationException("Plugin local state entry quota exceeded.");
                _values[key] = (value, DateTimeOffset.UtcNow.Add(ttl));
            }
            return Task.FromResult(true);
        }
    }

    public Task<long> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        PluginStateLimits.Validate(key, "", ttl, 64 * 1024, TimeSpan.FromDays(1));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RemoveExpired();
            var found = _values.TryGetValue(key, out var entry);
            if (!found && _values.Count >= 256) throw new InvalidOperationException("Plugin local state entry quota exceeded.");
            var current = found ? long.Parse(entry.Value, CultureInfo.InvariantCulture) : 0;
            var value = checked(current + delta);
            _values[key] = (value.ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow.Add(ttl));
            return Task.FromResult(value);
        }
    }

    public Task<DateTimeOffset?> GetExpiryAsync(string key, CancellationToken cancellationToken = default)
    {
        PluginStateLimits.ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RemoveExpired();
            return Task.FromResult<DateTimeOffset?>(_values.TryGetValue(key, out var entry) ? entry.ExpiresAt : null);
        }
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _values.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            _values.Remove(key);
    }
}

internal sealed class PluginSharedState(string pluginKey, ISharedKeyValueStore store) : IPluginStateStore
{
    private readonly string _prefix = $"plugin-state:{Uri.EscapeDataString(pluginKey.ToLowerInvariant())}:";
    public bool IsAvailable => store.IsConfigured;
    public Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
        => store.GetStringAsync(Key(key), cancellationToken);
    public Task<bool> SetStringAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        PluginStateLimits.Validate(key, value, ttl, 4 * 1024 * 1024, TimeSpan.FromDays(30));
        return store.SetStringAsync(Key(key), value, ttl, cancellationToken);
    }
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => store.RemoveAsync(Key(key), cancellationToken);
    public Task<bool> PutIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        PluginStateLimits.Validate(key, value, ttl, 4 * 1024 * 1024, TimeSpan.FromDays(30));
        return store.PutIfAbsentAsync(Key(key), value, ttl, cancellationToken);
    }
    public Task<long> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        PluginStateLimits.Validate(key, "", ttl, 4 * 1024 * 1024, TimeSpan.FromDays(30));
        return store.IncrementAsync(Key(key), delta, ttl, cancellationToken);
    }
    public Task<bool> CompareExchangeAsync(string key, string? expected, string? value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        PluginStateLimits.Validate(key, value ?? "", ttl, 4 * 1024 * 1024, TimeSpan.FromDays(30));
        return store.CompareExchangeAsync(Key(key), expected, value, ttl, cancellationToken);
    }
    public Task<DateTimeOffset?> GetExpiryAsync(string key, CancellationToken cancellationToken = default)
        => store.GetExpiryAsync(Key(key), cancellationToken);
    private string Key(string key)
    {
        PluginStateLimits.ValidateKey(key);
        return _prefix + key;
    }
}

internal static class PluginStateLimits
{
    public static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl))
            throw new ArgumentException("Plugin state keys must be 1–128 non-control characters.", nameof(key));
    }
    public static void Validate(string key, string value, TimeSpan ttl, int maxBytes, TimeSpan maxTtl)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);
        if (ttl <= TimeSpan.Zero || ttl > maxTtl || Encoding.UTF8.GetByteCount(value) > maxBytes)
            throw new ArgumentException("Plugin state value or TTL exceeds its quota.", nameof(value));
    }
}
