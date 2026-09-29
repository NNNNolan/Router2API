using SqlSugar;

namespace Router.Infrastructure.Persistence;

[SugarTable("plugin_http_origins")]
public sealed class PluginHttpOriginEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string Id { get; set; } = "";
    [SugarColumn(Length = 128)]
    public string PluginKey { get; set; } = "";
    [SugarColumn(Length = 2048)]
    public string Origin { get; set; } = "";
}

[SugarTable("accounts")]
public sealed class AccountEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string Id { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string PluginKey { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string Platform { get; set; } = string.Empty;

    [SugarColumn(Length = 32)]
    public string CredentialKind { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "text")]
    public string CredentialJson { get; set; } = "{}";
    [SugarColumn(DefaultValue = "0")]
    public long CredentialVersion { get; set; }

    [SugarColumn(Length = 32)]
    public string State { get; set; } = "Active";

    [SugarColumn(IsNullable = true)]
    public DateTime? ExpiresAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? CooldownUntilUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? DisabledUntilUtc { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? Reason { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? LastStatusCode { get; set; }

    public int ConsecutiveFailures { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? Label { get; set; }

    public int InFlight { get; set; }
    public long Version { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

[SugarTable("proxy_subscriptions")]
public sealed class ProxySubscriptionEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string Id { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "text")]
    public string Url { get; set; } = string.Empty;

    [SugarColumn(Length = 16)]
    public string Scheme { get; set; } = "Http";

    public bool Enabled { get; set; } = true;

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? ParserName { get; set; }

    public int RefreshIntervalMinutes { get; set; } = 60;

    [SugarColumn(IsNullable = true)]
    public long? RefreshIntervalSeconds { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LastFetchedAtUtc { get; set; }
    public int LastFetchedCount { get; set; }

    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? LastError { get; set; }
}

[SugarTable("proxy_endpoints")]
public sealed class ProxyEndpointEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string Id { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string SubscriptionId { get; set; } = string.Empty;

    [SugarColumn(Length = 256)]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    [SugarColumn(Length = 16)]
    public string Scheme { get; set; } = "Http";

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? Username { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? Password { get; set; }

    public long ConfigurationVersion { get; set; } = 1;
    public string State { get; set; } = "Active";
    public int InFlight { get; set; }
    public long Version { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int LatencyMs { get; set; }
    public int AverageLatencyMs { get; set; }
    public double AverageSpeedBytesPerSecond { get; set; }
    public double CompositeScore { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? LastProbedAtUtc { get; set; }
    [SugarColumn(IsNullable = true)]
    public DateTime? LastProbeSuccessAtUtc { get; set; }
    public int ProbeConsecutiveFailures { get; set; }
    [SugarColumn(Length = 32)]
    public string ProbeStatus { get; set; } = "Unknown";
    [SugarColumn(Length = 512, IsNullable = true)]
    public string? ProbeError { get; set; }
    public long ProbeConfigurationVersion { get; set; }
    public DateTime FirstSeenAtUtc { get; set; } = DateTime.UtcNow;
    [SugarColumn(IsNullable = true)]
    public DateTime? LastSeenInSubscriptionAtUtc { get; set; }
}

[SugarTable("request_logs")]
public sealed class RequestLogEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string Id { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string TraceId { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? Platform { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? Model { get; set; }

    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public int DurationMs { get; set; }
    [SugarColumn(IsNullable = true)]
    public int? TtfbMs { get; set; }

    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? UsageJson { get; set; }

    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? AttemptDetailsJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

[SugarTable("usage_buckets")]
[SugarIndex("ux_usage_bucket_day_platform", nameof(Day), OrderByType.Asc, nameof(Platform), OrderByType.Asc, IsUnique = true)]
public sealed class UsageBucketEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string Id { get; set; } = string.Empty;

    public DateTime Day { get; set; }

    [SugarColumn(Length = 128)]
    public string Platform { get; set; } = string.Empty;

    public long Requests { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
}

[SugarTable("resource_events")]
public sealed class ResourceEventEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string Id { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string ResourceType { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string ResourceId { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string PluginKey { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string EventType { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

[SugarTable("task_logs")]
public sealed class TaskLogEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string Id { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string PluginKey { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? Platform { get; set; }

    [SugarColumn(Length = 128)]
    public string TaskName { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? AccountId { get; set; }

    [SugarColumn(Length = 32)]
    public string Status { get; set; } = string.Empty;

    [SugarColumn(Length = 512, IsNullable = true)]
    public string? Message { get; set; }

    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? Error { get; set; }

    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? DetailsJson { get; set; }

    public int DurationMs { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    [SugarColumn(IsNullable = true)]
    public DateTime? FinishedAtUtc { get; set; }
}

[SugarTable("plugin_logs")]
public sealed class PluginLogEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string Id { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string PluginKey { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? Platform { get; set; }

    [SugarColumn(Length = 32)]
    public string Level { get; set; } = "Information";

    [SugarColumn(Length = 128)]
    public string EventType { get; set; } = string.Empty;

    [SugarColumn(Length = 512)]
    public string Message { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? TraceId { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? TaskName { get; set; }

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? AccountId { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? Model { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? StatusCode { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? DurationMs { get; set; }

    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? DetailsJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
