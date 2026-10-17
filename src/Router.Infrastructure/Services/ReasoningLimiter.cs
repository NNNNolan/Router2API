using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;

namespace Router.Infrastructure.Services;

/// <summary>把客户端声明的推理参数限制在模型能力目录已探测到的范围内。</summary>
internal static class ReasoningLimiter
{
    private static readonly string[] NestedEffortKeys = ["reasoning", "thinking", "output_config"];

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

        foreach (var key in NestedEffortKeys)
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

    internal static string ClosestSupportedLevel(string requested, IReadOnlyList<string> levels)
    {
        var normalized = NormalizeEffort(requested);
        for (var index = 0; index < levels.Count; index++)
            if (NormalizeEffort(levels[index]).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return levels[index];

        var requestedRank = KnownRank(normalized);
        if (requestedRank < 0) return levels[^1];

        var bestIndex = -1;
        var bestDistance = int.MaxValue;
        var bestRank = int.MaxValue;
        for (var index = 0; index < levels.Count; index++)
        {
            var rank = KnownRank(NormalizeEffort(levels[index]));
            if (rank < 0) continue;
            var distance = Math.Abs(rank - requestedRank);
            // 距离相同时优先选择较低的推理等级，其次保留原始顺序。
            if (distance < bestDistance || distance == bestDistance && rank <= requestedRank && bestRank > requestedRank)
            {
                bestIndex = index;
                bestRank = rank;
                bestDistance = distance;
            }
        }
        return bestIndex < 0 ? levels[^1] : levels[bestIndex];
    }

    private static int KnownRank(ReadOnlySpan<char> value)
    {
        value = value.Trim();
        if (value.Equals("minimal", StringComparison.OrdinalIgnoreCase)) return 0;
        if (value.Equals("low", StringComparison.OrdinalIgnoreCase)) return 1;
        if (value.Equals("medium", StringComparison.OrdinalIgnoreCase)) return 2;
        if (value.Equals("high", StringComparison.OrdinalIgnoreCase)) return 3;
        if (value.Equals("xhigh", StringComparison.OrdinalIgnoreCase)) return 4;
        if (value.Equals("max", StringComparison.OrdinalIgnoreCase)) return 5;
        return -1;
    }

    private static ReadOnlySpan<char> NormalizeEffort(ReadOnlySpan<char> value)
    {
        value = value.Trim();
        if (value.Equals("very_low", StringComparison.OrdinalIgnoreCase) || value.Equals("very-low", StringComparison.OrdinalIgnoreCase)) return "low";
        if (value.Equals("normal", StringComparison.OrdinalIgnoreCase)) return "medium";
        if (value.Equals("very_high", StringComparison.OrdinalIgnoreCase) || value.Equals("very-high", StringComparison.OrdinalIgnoreCase)
            || value.Equals("extra-high", StringComparison.OrdinalIgnoreCase)) return "xhigh";
        return value.Equals("ultra", StringComparison.OrdinalIgnoreCase) ? "max" : value;
    }

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
