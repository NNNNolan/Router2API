using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;

namespace Router.Infrastructure.Services;

/// <summary>把客户端声明的推理参数限制在模型能力目录已探测到的范围内。</summary>
internal static class ReasoningLimiter
{
    public static bool HasReasoning(AdapterRequest request)
        => request.Extensions.ContainsKey("reasoning_effort")
            || request.Extensions.ContainsKey("effort")
            || request.Extensions.ContainsKey("reasoning")
            || request.Extensions.ContainsKey("thinking")
            || request.Extensions.ContainsKey("output_config");

    public static bool Clamp(AdapterRequest request, ModelMetadata metadata)
    {
        var changed = false;
        var levels = metadata.ReasoningLevels;
        var requested = ReadRequestedEffort(request.Extensions);
        if (!string.IsNullOrWhiteSpace(requested)
            && !IsReasoningDisabled(requested)
            && !IsReasoningAutomatic(requested)
            && levels is { Count: > 0 })
        {
            var supported = ClosestSupportedLevel(requested, levels);
            if (!requested.Equals(supported, StringComparison.OrdinalIgnoreCase))
            {
                request.Extensions["reasoning_effort"] = supported;
                if (request.Extensions.ContainsKey("effort"))
                    request.Extensions["effort"] = supported;
                UpdateNestedEffort(request.Extensions, "reasoning", supported);
                UpdateNestedEffort(request.Extensions, "thinking", supported);
                UpdateNestedEffort(request.Extensions, "output_config", supported);
                changed = true;
            }
        }

        if (metadata.ReasoningTokenLimit is { } tokenLimit)
            changed |= ClampBudget(request.Extensions, "thinking", tokenLimit);

        return changed;
    }

    private static string? ReadRequestedEffort(Dictionary<string, object?> extensions)
    {
        var effort = ReadString(extensions, "reasoning_effort")
            ?? ReadString(extensions, "effort");
        if (!string.IsNullOrWhiteSpace(effort)) return effort;

        foreach (var key in new[] { "reasoning", "thinking", "output_config" })
        {
            if (!extensions.TryGetValue(key, out var raw) || raw is null) continue;
            var node = ToObject(raw);
            if (node?["effort"] is JsonValue effortValue
                && effortValue.TryGetValue<string>(out var nestedEffort)
                && !string.IsNullOrWhiteSpace(nestedEffort))
                return nestedEffort;
        }

        return null;
    }

    private static string ClosestSupportedLevel(string requested, IReadOnlyList<string> levels)
    {
        var normalized = NormalizeEffort(requested);
        foreach (var level in levels)
            if (NormalizeEffort(level).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return level;

        var requestedRank = KnownRank(normalized);
        if (requestedRank < 0) return levels[^1];

        return levels
            .Select((level, index) => (Level: level, Index: index, Rank: KnownRank(NormalizeEffort(level))))
            .Where(candidate => candidate.Rank >= 0)
            .OrderBy(candidate => Math.Abs(candidate.Rank - requestedRank))
            .ThenBy(candidate => candidate.Rank > requestedRank)
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Level)
            .FirstOrDefault() ?? levels[^1];
    }

    private static int KnownRank(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "minimal" => 0,
            "low" => 1,
            "medium" => 2,
            "high" => 3,
            "xhigh" => 4,
            "max" => 5,
            _ => -1
        };

    private static string NormalizeEffort(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "very_low" or "very-low" => "low",
            "normal" => "medium",
            "very_high" or "very-high" or "extra-high" => "xhigh",
            "ultra" => "max",
            var normalized => normalized
        };

    private static bool IsReasoningDisabled(string effort)
        => effort.Equals("none", StringComparison.OrdinalIgnoreCase)
            || effort.Equals("disabled", StringComparison.OrdinalIgnoreCase)
            || effort.Equals("off", StringComparison.OrdinalIgnoreCase);

    private static bool IsReasoningAutomatic(string effort)
        => effort.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || effort.Equals("default", StringComparison.OrdinalIgnoreCase);

    private static void UpdateNestedEffort(
        Dictionary<string, object?> extensions,
        string key,
        string value)
    {
        if (!extensions.TryGetValue(key, out var raw) || raw is null) return;
        var node = ToObject(raw);
        if (node is null) return;
        if (node.ContainsKey("effort")) node["effort"] = value;
        extensions[key] = node;
    }

    private static bool ClampBudget(
        Dictionary<string, object?> extensions,
        string key,
        int maximum)
    {
        if (!extensions.TryGetValue(key, out var raw) || raw is null) return false;
        var node = ToObject(raw);
        if (node is null || node["budget_tokens"] is not JsonValue budget) return false;

        if (!budget.TryGetValue<int>(out var current) || current <= maximum)
            return false;

        node["budget_tokens"] = maximum;
        extensions[key] = node;
        return true;
    }

    private static string? ReadString(
        Dictionary<string, object?> extensions,
        string key)
    {
        if (!extensions.TryGetValue(key, out var raw) || raw is null) return null;
        if (raw is string value) return value;
        if (raw is JsonElement element && element.ValueKind == JsonValueKind.String)
            return element.GetString();
        if (raw is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var jsonText))
            return jsonText;
        return null;
    }

    private static JsonObject? ToObject(object raw)
    {
        try
        {
            return raw switch
            {
                JsonObject objectValue => objectValue,
                JsonElement element when element.ValueKind == JsonValueKind.Object
                    => JsonNode.Parse(element.GetRawText()) as JsonObject,
                _ => JsonNode.Parse(JsonSerializer.Serialize(raw)) as JsonObject
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
