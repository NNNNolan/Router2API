using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;

namespace Router.Infrastructure.Services;

/// <summary>基于 SqlSugar 的资源审计接收器。</summary>
public sealed class ResourceEventLogSink(SqlSugarDatabase database) : ILogSink<Router.Contracts.Host.ResourceEvent>
{
    public Task WriteAsync(Router.Contracts.Host.ResourceEvent item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        database.Scope.Insertable(new ResourceEventEntity
        {
            Id = item.Id,
            ResourceType = item.ResourceType,
            ResourceId = item.ResourceId,
            PluginKey = item.PluginKey,
            EventType = item.EventType,
            Error = item.Error,
            CreatedAtUtc = item.CreatedAt.UtcDateTime
        }).ExecuteCommand();
        return Task.CompletedTask;
    }
}

/// <summary>持久化请求日志并提供最近审计记录。</summary>
public sealed class RequestLogStore(SqlSugarDatabase database) : ILogSink<RequestLog>, IRequestLogStore
{
    public Task WriteAsync(RequestLog item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        database.Scope.Insertable(new RequestLogEntity
        {
            Id = item.Id,
            TraceId = item.TraceId,
            Platform = item.Platform,
            Model = item.Model,
            Success = item.Success,
            StatusCode = item.StatusCode,
            DurationMs = item.DurationMs,
            TtfbMs = item.TtfbMs ?? 0,
            UsageJson = item.Usage is null ? null : JsonSerializer.Serialize(item.Usage),
            AttemptDetailsJson = item.AttemptDetails.Count == 0
                ? null
                : JsonSerializer.Serialize(item.AttemptDetails),
            CreatedAtUtc = item.CreatedAt.UtcDateTime
        }).ExecuteCommand();
        return Task.CompletedTask;
    }

    public Task<PagedResult<RequestLog>> QueryAsync(
        RequestLogQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = Math.Max(1, query.Page);
        var pageSize = query.PageSize is 20 or 50 or 100 ? query.PageSize : 20;
        var source = database.Scope.Queryable<RequestLogEntity>();
        if (query.FromUtc is { } from)
            source = source.Where(item => item.CreatedAtUtc >= from.UtcDateTime);
        if (query.ToUtc is { } to)
            source = source.Where(item => item.CreatedAtUtc < to.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(query.Platform))
            source = source.Where(item => item.Platform == query.Platform);
        if (!string.IsNullOrWhiteSpace(query.Model))
            source = source.Where(item => item.Model == query.Model);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            source = source.Where(item => item.TraceId.Contains(keyword) || item.Model!.Contains(keyword));
        }

        var total = source.Count();
        var result = source.OrderByDescending(item => item.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList()
            .Select(item => new RequestLog
            {
                Id = item.Id,
                TraceId = item.TraceId,
                Platform = item.Platform,
                Model = item.Model,
                Success = item.Success,
                StatusCode = item.StatusCode,
                DurationMs = item.DurationMs,
                TtfbMs = item.TtfbMs,
                CreatedAt = new DateTimeOffset(item.CreatedAtUtc, TimeSpan.Zero),
                Usage = item.UsageJson is null ? null : JsonSerializer.Deserialize<Usage>(item.UsageJson),
                AttemptDetails = DeserializeAttemptDetails(item.AttemptDetailsJson)
            })
            .ToArray();
        return Task.FromResult(new PagedResult<RequestLog>(result, page, pageSize, total));
    }

    private static List<RequestAttemptDetail> DeserializeAttemptDetails(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<RequestAttemptDetail>>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

public sealed class TaskLogStore(SqlSugarDatabase database) : ITaskLogStore
{
    public Task WriteAsync(TaskLog log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        cancellationToken.ThrowIfCancellationRequested();
        database.Scope.Insertable(new TaskLogEntity
        {
            Id = log.Id,
            PluginKey = log.PluginKey,
            Platform = log.Platform,
            TaskName = log.TaskName,
            AccountId = log.AccountId,
            Status = log.Status,
            Message = log.Message,
            Error = log.Error,
            DetailsJson = log.DetailsJson,
            DurationMs = log.DurationMs,
            StartedAtUtc = log.StartedAt.UtcDateTime,
            FinishedAtUtc = log.FinishedAt?.UtcDateTime
        }).ExecuteCommand();
        return Task.CompletedTask;
    }

    public Task UpdateAsync(TaskLog log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        cancellationToken.ThrowIfCancellationRequested();
        database.Scope.Updateable<TaskLogEntity>()
            .SetColumns(entity => new TaskLogEntity
            {
                PluginKey = log.PluginKey,
                Platform = log.Platform,
                TaskName = log.TaskName,
                AccountId = log.AccountId,
                Status = log.Status,
                Message = log.Message,
                Error = log.Error,
                DetailsJson = log.DetailsJson,
                DurationMs = log.DurationMs,
                StartedAtUtc = log.StartedAt.UtcDateTime,
                FinishedAtUtc = log.FinishedAt.HasValue ? log.FinishedAt.Value.UtcDateTime : null
            })
            .Where(entity => entity.Id == log.Id)
            .ExecuteCommand();
        return Task.CompletedTask;
    }

    public Task<PagedResult<TaskLog>> QueryAsync(
        TaskLogQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = Math.Max(1, query.Page);
        var pageSize = query.PageSize is 20 or 50 or 100 ? query.PageSize : 20;
        var source = database.Scope.Queryable<TaskLogEntity>()
            .Where(item => item.AccountId != null);
        if (query.FromUtc is { } from) source = source.Where(item => item.StartedAtUtc >= from.UtcDateTime);
        if (query.ToUtc is { } to) source = source.Where(item => item.StartedAtUtc < to.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(query.PluginKey)) source = source.Where(item => item.PluginKey == query.PluginKey);
        if (!string.IsNullOrWhiteSpace(query.Platform)) source = source.Where(item => item.Platform == query.Platform);
        if (!string.IsNullOrWhiteSpace(query.TaskName)) source = source.Where(item => item.TaskName == query.TaskName);
        if (!string.IsNullOrWhiteSpace(query.Status)) source = source.Where(item => item.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            source = source.Where(item => item.PluginKey.Contains(keyword)
                || item.TaskName.Contains(keyword)
                || item.AccountId!.Contains(keyword)
                || item.Message!.Contains(keyword));
        }

        var total = source.Count();
        var items = source.OrderByDescending(item => item.StartedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList()
            .Select(item => new TaskLog
            {
                Id = item.Id,
                PluginKey = item.PluginKey,
                Platform = item.Platform,
                TaskName = item.TaskName,
                AccountId = item.AccountId,
                Status = item.Status,
                Message = item.Message,
                Error = item.Error,
                DetailsJson = item.DetailsJson,
                DurationMs = item.DurationMs,
                StartedAt = new DateTimeOffset(item.StartedAtUtc, TimeSpan.Zero),
                FinishedAt = item.FinishedAtUtc is { } finished
                    ? new DateTimeOffset(finished, TimeSpan.Zero)
                    : null
            })
            .ToArray();
        return Task.FromResult(new PagedResult<TaskLog>(items, page, pageSize, total));
    }
}

/// <summary>持久化插件执行明细，并供管理端按插件、事件和时间查询。</summary>
public sealed class PluginLogStore(
    SqlSugarDatabase database,
    IOptionsMonitor<PluginLogOptions> options) : IPluginLogStore
{
    public Task WriteAsync(PluginLog log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ShouldPersist(log.Level, options.CurrentValue.MinimumPluginLogLevel))
            return Task.CompletedTask;
        database.Scope.Insertable(new PluginLogEntity
        {
            Id = log.Id,
            PluginKey = Limit(log.PluginKey, 128) ?? string.Empty,
            Platform = Limit(log.Platform, 128),
            Level = Limit(log.Level, 32) ?? "Information",
            EventType = Limit(log.EventType, 128) ?? "plugin",
            Message = Limit(Sanitize(log.Message), 512) ?? string.Empty,
            TraceId = Limit(log.TraceId, 64),
            TaskName = Limit(log.TaskName, 128),
            AccountId = Limit(log.AccountId, 64),
            Model = Limit(log.Model, 256),
            StatusCode = log.StatusCode,
            DurationMs = log.DurationMs,
            DetailsJson = Limit(Sanitize(log.DetailsJson), 16_384),
            CreatedAtUtc = log.CreatedAt.UtcDateTime
        }).ExecuteCommand();
        return Task.CompletedTask;
    }

    private static bool ShouldPersist(string? level, string? minimumLevel)
        => LevelValue(level) >= LevelValue(minimumLevel);

    private static int LevelValue(string? level)
        => level?.Trim().ToLowerInvariant() switch
        {
            "trace" => 0,
            "debug" => 1,
            "information" or "info" => 2,
            "warning" or "warn" => 3,
            "error" => 4,
            "critical" => 5,
            _ => 2
        };

    public Task<PagedResult<PluginLog>> QueryAsync(
        PluginLogQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = Math.Max(1, query.Page);
        var pageSize = query.PageSize is 20 or 50 or 100 ? query.PageSize : 20;
        var source = database.Scope.Queryable<PluginLogEntity>();
        if (query.FromUtc is { } from) source = source.Where(item => item.CreatedAtUtc >= from.UtcDateTime);
        if (query.ToUtc is { } to) source = source.Where(item => item.CreatedAtUtc < to.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(query.PluginKey)) source = source.Where(item => item.PluginKey == query.PluginKey);
        if (!string.IsNullOrWhiteSpace(query.Platform)) source = source.Where(item => item.Platform == query.Platform);
        if (!string.IsNullOrWhiteSpace(query.TaskName)) source = source.Where(item => item.TaskName == query.TaskName);
        if (!string.IsNullOrWhiteSpace(query.Level)) source = source.Where(item => item.Level == query.Level);
        if (!string.IsNullOrWhiteSpace(query.EventType)) source = source.Where(item => item.EventType == query.EventType);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            source = source.Where(item => item.Message.Contains(keyword)
                || item.PluginKey.Contains(keyword)
                || item.EventType.Contains(keyword)
                || item.TraceId!.Contains(keyword));
        }

        var total = source.Count();
        var items = source.OrderByDescending(item => item.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList()
            .Select(item => new PluginLog
            {
                Id = item.Id,
                PluginKey = item.PluginKey,
                Platform = item.Platform,
                Level = item.Level,
                EventType = item.EventType,
                Message = item.Message,
                TraceId = item.TraceId,
                TaskName = item.TaskName,
                AccountId = item.AccountId,
                Model = item.Model,
                StatusCode = item.StatusCode,
                DurationMs = item.DurationMs,
                DetailsJson = item.DetailsJson,
                CreatedAt = new DateTimeOffset(item.CreatedAtUtc, TimeSpan.Zero)
            })
            .ToArray();
        return Task.FromResult(new PagedResult<PluginLog>(items, page, pageSize, total));
    }

    private static string? Limit(string? value, int maxLength)
        => value is null ? null : value.Length <= maxLength ? value : value[..maxLength];

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var sanitized = Regex.Replace(
            value,
            @"(?i)(authorization|api[-_]?key|access[-_]?token|refresh[-_]?token|id[-_]?token|cookie|password)(\s*[:=]\s*)([^,\s}\]]+)",
            "$1$2[REDACTED]");
        return Regex.Replace(
            sanitized,
            @"(?i)(bearer\s+)[A-Za-z0-9._~+/=-]+",
            "$1[REDACTED]");
    }
}

/// <summary>统一清理请求、资源、任务、插件和用量日志。</summary>
public sealed class LogRetentionStore(SqlSugarDatabase database) : ILogRetentionStore
{
    public Task<int> DeleteBeforeAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cutoff = cutoffUtc.UtcDateTime;
        var deleted = 0;
        deleted += database.Scope.Deleteable<RequestLogEntity>()
            .Where(item => item.CreatedAtUtc < cutoff)
            .ExecuteCommand();
        deleted += database.Scope.Deleteable<ResourceEventEntity>()
            .Where(item => item.CreatedAtUtc < cutoff)
            .ExecuteCommand();
        deleted += database.Scope.Deleteable<TaskLogEntity>()
            .Where(item => item.StartedAtUtc < cutoff)
            .ExecuteCommand();
        deleted += database.Scope.Deleteable<PluginLogEntity>()
            .Where(item => item.CreatedAtUtc < cutoff)
            .ExecuteCommand();
        deleted += database.Scope.Deleteable<UsageBucketEntity>()
            .Where(item => item.Day < cutoff.Date)
            .ExecuteCommand();
        return Task.FromResult(deleted);
    }
}
