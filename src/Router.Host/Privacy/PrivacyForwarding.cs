using Router.Contracts.Domain;

namespace Router.Host.Privacy;

/// <summary>
/// 宿主注入的来源插件头只在回环请求上有效，并会在进入下一个插件前移除，
/// 让转发插件可以把“跳过 block”的身份带到自己的回环调用。
/// </summary>
internal static class PrivacyForwarding
{
    internal const string SourcePluginHeader = "x-router2api-source-plugin";

    internal static string? ReadSourcePlugin(AdapterRequest request)
        => request.RequestHeaders.TryGetValue(SourcePluginHeader, out var value) ? value.Trim() : null;

    internal static void Mark(AdapterRequest request, string pluginKey)
        => request.RequestHeaders[SourcePluginHeader] = pluginKey;

    internal static void Clear(AdapterRequest request)
    {
        request.RequestHeaders.Remove(SourcePluginHeader);
        request.DownstreamRequestHeaders.Remove(SourcePluginHeader);
    }
}
