using Router.Infrastructure.Persistence;

namespace Router.Infrastructure.Services;

/// <summary>动态站点的持久化明确授权；不使用通配符绕过 JS 网络权限。</summary>
public sealed class PluginHttpOriginStore(SqlSugarDatabase database)
{
    public Task<IReadOnlyList<string>> ListAsync(string pluginKey, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(database.Scope.Queryable<PluginHttpOriginEntity>()
            .Where(row => row.PluginKey == pluginKey).ToList().Select(row => row.Origin).ToArray());
    }

    public Task ApproveAsync(string pluginKey, string origin, CancellationToken token = default)
    {
        origin = Normalize(origin);
        token.ThrowIfCancellationRequested();
        // A stable compound primary key makes concurrent approvals idempotent.
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pluginKey + "\0" + origin)));
        database.Scope.Ado.ExecuteCommand(
            "INSERT OR IGNORE INTO plugin_http_origins (Id, PluginKey, Origin) VALUES (@Id, @PluginKey, @Origin)",
            new { Id = id, PluginKey = pluginKey, Origin = origin });
        return Task.CompletedTask;
    }

    public Task RevokeAsync(string pluginKey, string origin, CancellationToken token = default)
    {
        origin = Normalize(origin);
        token.ThrowIfCancellationRequested();
        database.Scope.Deleteable<PluginHttpOriginEntity>().Where(row => row.PluginKey == pluginKey && row.Origin == origin).ExecuteCommand();
        return Task.CompletedTask;
    }

    public static string Normalize(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.Host.Contains('*') || origin.Length > 2048 || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Expected an exact HTTP(S) origin.", nameof(origin));
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
}
