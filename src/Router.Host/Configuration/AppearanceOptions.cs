namespace Router.Host.Configuration;

/// <summary>宿主管理界面的外观设置，独立于插件协议和认证配置。</summary>
public sealed class AppearanceOptions
{
    /// <summary>minimal 为默认黑白极简主题；default 保留原有蓝色主题标识，兼容已保存的配置。</summary>
    public string Theme { get; set; } = "minimal";

    /// <summary>检查主题标识是否受支持。</summary>
    /// <param name="theme">外部配置或请求中的主题标识。</param>
    /// <returns>仅允许已实现的主题。</returns>
    public static bool IsSupported(string? theme) => theme is "default" or "minimal";
}
