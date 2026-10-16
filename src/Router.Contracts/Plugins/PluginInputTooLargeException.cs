namespace Router.Contracts.Plugins;

/// <summary>
/// 宿主送给插件的输入超过桥接上限。这是调用方请求过大的问题，
/// 不是插件或上游故障，调用方应缩短上下文后重试。
/// </summary>
public sealed class PluginInputTooLargeException(int limitBytes, string message) : Exception(message)
{
    /// <summary>触发本异常的上限字节数。</summary>
    public int LimitBytes { get; } = limitBytes;

    /// <summary>上限的 MiB 数值，用于面向调用方的说明。</summary>
    public int LimitMegabytes => LimitBytes / (1024 * 1024);

    /// <summary>可直接回给调用方（或写入日志）的中文说明。</summary>
    public string UserMessage => $"请求体超过宿主 JS 插件的 {LimitMegabytes} MiB 上限，请缩短上下文后重试。";
}
