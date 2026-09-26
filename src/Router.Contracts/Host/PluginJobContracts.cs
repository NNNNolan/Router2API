using System.Text.Json;

namespace Router.Contracts.Host;

/// <summary>后台任务状态；完成记录按插件版本有界保留，不跨宿主重启恢复。</summary>
public enum PluginJobState
{
    /// <summary>等待执行配额。</summary>
    Queued,
    /// <summary>执行中。</summary>
    Running,
    /// <summary>正常完成。</summary>
    Completed,
    /// <summary>失败。</summary>
    Failed,
    /// <summary>已取消或达到执行截止时间。</summary>
    Cancelled
}

/// <summary>任务入队选项。</summary>
public sealed record PluginJobOptions
{
    /// <summary>同任务/平台下的活跃任务去重键；相同键返回原任务。</summary>
    public string? Key { get; init; }
    /// <summary>所属声明平台；同名任务有多个平台时必须指定。</summary>
    public string? Platform { get; init; }
}

/// <summary>插件可见的任务状态，不包含宿主对象或执行堆栈。</summary>
public sealed record PluginJobSnapshot(string Id, string Name, string Platform, string? Key, PluginJobState State,
    DateTimeOffset QueuedAt, DateTimeOffset? StartedAt = null, DateTimeOffset? FinishedAt = null,
    JsonElement? Progress = null, JsonElement? Result = null, string? Error = null);

/// <summary>本插件版本的后台任务能力。任务输入和结果均为有界 JSON。</summary>
public interface IPluginJobs
{
    /// <summary>启动已声明任务，返回后任务不依赖当前 HTTP 请求令牌；令牌只取消本次入队。</summary>
    Task<PluginJobSnapshot> StartAsync(string name, JsonElement? input = null, PluginJobOptions? options = null,
        CancellationToken cancellationToken = default);
    /// <summary>查询本版本任务；其他插件/版本的任务不可见。</summary>
    PluginJobSnapshot? Get(string id);
    /// <summary>列出本版本仍保留的任务。</summary>
    IReadOnlyList<PluginJobSnapshot> List();
    /// <summary>请求取消本版本任务，不伪造任务已经退出。</summary>
    Task<bool> CancelAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>等待任务实际结束；取消等待不取消该任务。</summary>
    Task<PluginJobSnapshot?> WaitAsync(string id, CancellationToken cancellationToken = default);
}
