using System.Net;

namespace Router.Host.Privacy;

/// <summary>
/// 在转发头中间件之前记录真实连接是否来自回环地址，避免外部
/// X-Forwarded-For 伪造影响白名单转发判断。
/// </summary>
internal static class RawConnection
{
    private const string LoopbackItem = "Router2API.RawLoopback";

    internal static void Capture(HttpContext context)
        => context.Items[LoopbackItem] = context.Connection.RemoteIpAddress is { } address
            && IPAddress.IsLoopback(address);

    internal static bool IsLoopback(HttpContext context)
        => context.Items.TryGetValue(LoopbackItem, out var value) && value is bool loopback
            ? loopback
            : context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
}
