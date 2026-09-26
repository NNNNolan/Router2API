namespace Router.Host.Configuration;

/// <summary>宿主数据日志保留和清理计划。</summary>
public sealed class LogRetentionOptions
{
    public int DataRetentionDays { get; set; } = 30;
    public string CleanupCron { get; set; } = "0 0 3 * * *";
}
