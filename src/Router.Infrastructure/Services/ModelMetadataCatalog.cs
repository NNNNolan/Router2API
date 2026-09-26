using System.Text.Json;
using Microsoft.Extensions.Logging;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Infrastructure.Services;

/// <summary>读取公共模型能力元数据并缓存四小时，不承担任何提供方的协议发现。</summary>
public sealed class ModelMetadataCatalog(
    IHttpClientFactory httpClientFactory,
    ISharedKeyValueStore cache,
    ILogger<ModelMetadataCatalog> logger) : IModelMetadataCatalog, IDisposable
{
    private const string CacheKey = "router2api:model-metadata:models-dev:v1";
    private const string SourceUrl = "https://models.dev/api.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(4);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private ModelMetadataSnapshot? _memory;

    public async Task<ModelMetadataSnapshot> GetAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && IsFresh(_memory))
            return _memory!;

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && IsFresh(_memory))
                return _memory!;

            var previous = _memory;
            var cached = await ReadCacheAsync(cancellationToken);
            if (cached is not null && (previous is null || cached.UpdatedAt > previous.UpdatedAt))
                previous = cached;
            if (previous is not null)
                _memory = previous;

            if (!forceRefresh && IsFresh(previous))
                return previous!;

            try
            {
                var snapshot = await FetchAsync(cancellationToken);

                _memory = snapshot;
                await WriteCacheAsync(snapshot, cancellationToken);
                return snapshot;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (previous is not null)
                {
                    logger.LogWarning(exception, "model metadata refresh failed; using the last cached snapshot");
                    return previous;
                }

                throw;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<ModelMetadata?> FindAsync(
        string platform,
        string model,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(false, cancellationToken);
        var fullId = $"{platform.Trim()}/{model.Trim()}";
        return snapshot.Models.FirstOrDefault(item =>
                   item.Id.Equals(fullId, StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Models.FirstOrDefault(item =>
                item.Id.EndsWith('/' + model.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ModelMetadataSnapshot?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        if (!cache.IsConfigured) return null;

        try
        {
            var value = await cache.GetStringAsync(CacheKey, cancellationToken);
            return string.IsNullOrWhiteSpace(value)
                ? null
                : JsonSerializer.Deserialize<ModelMetadataSnapshot>(value, JsonOptions);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "failed to read model metadata cache");
            return null;
        }
    }

    private async Task WriteCacheAsync(
        ModelMetadataSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!cache.IsConfigured) return;

        try
        {
            var value = JsonSerializer.Serialize(snapshot, JsonOptions);
            await cache.SetStringAsync(CacheKey, value, CacheTtl, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "failed to write model metadata cache");
        }
    }

    // 宿主只读取提供方无关的能力目录；额外站点、SDK 包名/端点推断由插件自己实现。
    private async Task<ModelMetadataSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("models-dev");
        using var response = await client.GetAsync(SourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return new ModelMetadataSnapshot(DateTimeOffset.UtcNow, Parse(document.RootElement));
    }
    private static ModelMetadata[] Parse(JsonElement root)
    {
        var providers = root;
        if (TryGetProperty(root, "providers", out var providerContainer)
            && providerContainer.ValueKind == JsonValueKind.Object)
            providers = providerContainer;

        if (providers.ValueKind != JsonValueKind.Object)
            throw new JsonException("models.dev response must contain a provider object");

        var models = new List<ModelMetadata>();
        foreach (var provider in providers.EnumerateObject())
        {
            if (provider.Value.ValueKind != JsonValueKind.Object
                || !TryGetProperty(provider.Value, "models", out var providerModels)
                || providerModels.ValueKind != JsonValueKind.Object)
                continue;

            var platform = ReadString(provider.Value, "id") ?? provider.Name;
            var platformName = ReadString(provider.Value, "name") ?? platform;
            foreach (var model in providerModels.EnumerateObject())
            {
                if (model.Value.ValueKind != JsonValueKind.Object) continue;
                var modelId = ReadString(model.Value, "id") ?? model.Name;
                if (string.IsNullOrWhiteSpace(modelId)) continue;
                if (string.Equals(ReadString(model.Value, "status"), "deprecated", StringComparison.OrdinalIgnoreCase))
                    continue;

                var id = modelId.Contains('/')
                    ? modelId
                    : $"{platform}/{modelId}";
                var reasoning = ParseReasoning(model.Value);
                models.Add(new ModelMetadata(
                    id,
                    platform,
                    platformName,
                    ReadString(model.Value, "name") ?? model.Name,
                    ReadLimit(model.Value, "context"),
                    ReadLimit(model.Value, "input"),
                    ReadLimit(model.Value, "output"),
                    reasoning.Supported,
                    reasoning.Levels,
                    reasoning.TokenLimit));
            }
        }

        return models
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model.PlatformName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (bool Supported, IReadOnlyList<string>? Levels, int? TokenLimit) ParseReasoning(JsonElement model)
    {
        var supported = TryGetProperty(model, "reasoning", out var reasoning)
            && (reasoning.ValueKind == JsonValueKind.True
                || (reasoning.ValueKind == JsonValueKind.Object
                    && (!TryGetProperty(reasoning, "supported", out var supportedValue)
                        || supportedValue.ValueKind == JsonValueKind.True)));
        var levels = new List<string>();
        var tokenLimit = (int?)null;

        if (TryGetProperty(model, "reasoning_options", out var options))
            ParseReasoningOptions(options, levels, ref tokenLimit);
        if (reasoning.ValueKind == JsonValueKind.Object
            && TryGetProperty(reasoning, "options", out var nestedOptions))
            ParseReasoningOptions(nestedOptions, levels, ref tokenLimit);

        if (levels.Count > 0 || tokenLimit is not null)
            supported = true;

        return (
            supported,
            levels.Count == 0 ? null : levels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            tokenLimit);
    }

    private static void ParseReasoningOptions(
        JsonElement options,
        List<string> levels,
        ref int? tokenLimit)
    {
        if (options.ValueKind != JsonValueKind.Array) return;
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind == JsonValueKind.String)
            {
                levels.Add(option.GetString()!);
                continue;
            }

            if (option.ValueKind != JsonValueKind.Object) continue;
            var type = ReadString(option, "type");
            if (string.Equals(type, "effort", StringComparison.OrdinalIgnoreCase)
                && TryGetProperty(option, "values", out var values)
                && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in values.EnumerateArray())
                    if (value.ValueKind == JsonValueKind.String)
                        levels.Add(value.GetString()!);
            }

            if (string.Equals(type, "budget_tokens", StringComparison.OrdinalIgnoreCase))
            {
                var max = ReadNumber(option, "max");
                if (max is > 0)
                    tokenLimit = (int)Math.Min(max.Value, int.MaxValue);
            }
        }
    }

    private static bool IsFresh(ModelMetadataSnapshot? snapshot)
        => snapshot is not null && snapshot.UpdatedAt + CacheTtl > DateTimeOffset.UtcNow;

    private static int ReadLimit(JsonElement model, string propertyName)
    {
        if (!TryGetProperty(model, "limit", out var limits)
            || limits.ValueKind != JsonValueKind.Object)
            return 0;
        var value = ReadNumber(limits, propertyName);
        return value is > 0 ? (int)Math.Min(value.Value, int.MaxValue) : 0;
    }

    private static long? ReadNumber(JsonElement value, string propertyName)
    {
        if (!TryGetProperty(value, propertyName, out var property)) return null;
        if (property.TryGetInt64(out var integer)) return integer;
        return property.TryGetDouble(out var number) && number is >= 0 and <= long.MaxValue
            ? (long)number
            : null;
    }

    private static string? ReadString(JsonElement value, string propertyName)
        => TryGetProperty(value, propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetProperty(JsonElement value, string propertyName, out JsonElement property)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(propertyName, out property))
            return true;

        if (value.ValueKind == JsonValueKind.Object)
            foreach (var item in value.EnumerateObject())
                if (item.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = item.Value;
                    return true;
                }

        property = default;
        return false;
    }

    public void Dispose() => _refreshGate.Dispose();
}
