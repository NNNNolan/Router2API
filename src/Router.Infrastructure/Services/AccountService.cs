using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;
using SqlSugar;

namespace Router.Infrastructure.Services;

/// <summary>基于 SqlSugar 的账号事实服务。账号不进入二级缓存。</summary>
public sealed class AccountService(
    SqlSugarDatabase database,
    ISharedKeyValueStore shortTermState) : IAccountService
{
    public Task<Account?> GetAsync(
        string pluginKey,
        string id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var entity = db.Queryable<AccountEntity>()
            .First(x => x.Id == id && x.PluginKey == pluginKey);
        return Task.FromResult(entity?.ToDomain());
    }

    public Task<Account?> GetAnyAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var entity = db.Queryable<AccountEntity>().First(x => x.Id == id);
        return Task.FromResult(entity?.ToDomain());
    }

    public Task<IReadOnlyList<Account>> ListAsync(
        string pluginKey,
        string? platform = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var query = db.Queryable<AccountEntity>()
            .Where(x => x.PluginKey == pluginKey);
        if (!string.IsNullOrWhiteSpace(platform))
            query = query.Where(x => x.Platform == platform);

        var result = query.OrderBy(x => x.Id)
            .ToList()
            .Select(x => x.ToDomain())
            .ToArray();
        return Task.FromResult<IReadOnlyList<Account>>(result);
    }

    public Task<PagedResult<Account>> QueryAsync(
        AccountQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = Math.Max(1, query.Page);
        var pageSize = NormalizePageSize(query.PageSize);
        using var db = database.CreateClient();
        var source = db.Queryable<AccountEntity>();
        if (!string.IsNullOrWhiteSpace(query.PluginKey))
            source = source.Where(x => x.PluginKey == query.PluginKey);
        if (!string.IsNullOrWhiteSpace(query.Platform))
            source = source.Where(x => x.Platform == query.Platform);
        if (query.State is { } state)
            source = source.Where(x => x.State == state.ToString());
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            source = source.Where(x => x.Id.Contains(keyword) || x.Label!.Contains(keyword));
        }

        var total = source.Count();
        var items = source.OrderByDescending(x => x.UpdatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList()
            .Select(x => x.ToDomain())
            .ToArray();
        return Task.FromResult(new PagedResult<Account>(items, page, pageSize, total));
    }

    public async Task<Account> SaveAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (string.IsNullOrWhiteSpace(account.PluginKey))
            throw new ArgumentException("account PluginKey is required", nameof(account));
        cancellationToken.ThrowIfCancellationRequested();

        var entity = account.ToEntity();
        using var db = database.CreateClient();
        var existing = db.Queryable<AccountEntity>().First(x => x.Id == entity.Id);
        if (existing is not null
            && !string.Equals(existing.PluginKey, entity.PluginKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"account '{entity.Id}' belongs to another plugin");
        entity.CredentialVersion = existing is null ? 1
            : existing.CredentialVersion + (entity.CredentialKind != existing.CredentialKind || entity.CredentialJson != existing.CredentialJson ? 1 : 0);
        if (existing is not null)
        {
            var expected = existing.CredentialVersion;
            var updated = db.Updateable(entity)
                .Where(row => row.Id == entity.Id && row.PluginKey == entity.PluginKey && row.CredentialVersion == expected)
                .ExecuteCommand();
            if (updated == 0) throw new InvalidOperationException("Account credentials changed concurrently; read the account again.");
        }
        else
            db.Insertable(entity).ExecuteCommand();
        account.CredentialVersion = entity.CredentialVersion;

        // Re-enabling/re-authorizing an account must also reconcile its fast cooldown marker.
        // Plugins must not know or manipulate host Redis keys.
        if (account.Status.CooldownUntil is null && shortTermState.IsConfigured)
        {
            try { await shortTermState.RemoveAsync(AccountCooldownKey(account.PluginKey, account.Id), cancellationToken); }
            catch when (!cancellationToken.IsCancellationRequested) { /* Durable account state remains authoritative. */ }
        }
        return account;
    }

    public async Task<Account> RefreshAsync(
        string pluginKey,
        string id,
        Func<Account, CancellationToken, Task<Account>> refresh,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        var current = await GetAsync(pluginKey, id, cancellationToken)
            ?? throw new KeyNotFoundException($"account '{id}' was not found");
        var platform = current.Platform;
        var refreshed = await refresh(current, cancellationToken);
        if (!string.Equals(refreshed.Id, id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(refreshed.PluginKey, pluginKey, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(refreshed.Platform, platform, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("account refresh cannot change account identity or platform");
        return await SaveAsync(refreshed, cancellationToken);
    }

    public async Task<Account> PatchAsync(Account account, IReadOnlyList<string> fields, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(fields);
        var columns = fields.SelectMany(field => field switch
        {
            "label" => new[] { nameof(AccountEntity.Label) },
            "expiresAt" => [nameof(AccountEntity.ExpiresAtUtc)],
            "credential" => [nameof(AccountEntity.CredentialKind), nameof(AccountEntity.CredentialJson), nameof(AccountEntity.CredentialVersion)],
            "status.state" => [nameof(AccountEntity.State)],
            "status.cooldownUntil" => [nameof(AccountEntity.CooldownUntilUtc)],
            "status.disabledUntil" => [nameof(AccountEntity.DisabledUntilUtc)],
            "status.reason" => [nameof(AccountEntity.Reason)],
            "status.lastStatusCode" => [nameof(AccountEntity.LastStatusCode)],
            "status.consecutiveFailures" => [nameof(AccountEntity.ConsecutiveFailures)],
            _ => throw new ArgumentException("Unsupported account field.", nameof(fields))
        }).Append(nameof(AccountEntity.UpdatedAtUtc)).Distinct().ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var entity = account.ToEntity();
        var updatesCredential = fields.Contains("credential", StringComparer.Ordinal);
        var expected = account.CredentialVersion;
        if (updatesCredential) entity.CredentialVersion = checked(expected + 1);
        using var db = database.CreateClient();
        var query = db.Updateable(entity).UpdateColumns(columns)
            .Where(row => row.Id == account.Id && row.PluginKey == account.PluginKey && row.Platform == account.Platform);
        if (updatesCredential) query = query.Where(row => row.CredentialVersion == expected);
        if (query.ExecuteCommand() == 0) throw new InvalidOperationException("Account was removed or its credential version changed.");
        if (fields.Contains("status.cooldownUntil", StringComparer.Ordinal) && account.Status.CooldownUntil is null && shortTermState.IsConfigured)
        {
            try { await shortTermState.RemoveAsync(AccountCooldownKey(account.PluginKey, account.Id), cancellationToken); }
            catch when (!cancellationToken.IsCancellationRequested) { }
        }
        return await GetAsync(account.PluginKey, account.Id, cancellationToken) ?? throw new KeyNotFoundException("Account was removed.");
    }

    public async Task DeleteAsync(string pluginKey, string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        db.Deleteable<AccountEntity>()
            .Where(x => x.Id == id && x.PluginKey == pluginKey)
            .ExecuteCommand();
        await shortTermState.RemoveAsync(AccountCooldownKey(pluginKey, id), cancellationToken);
    }

    public async Task SetCooldownAsync(
        string pluginKey,
        string id,
        DateTimeOffset until,
        string reason,
        int? statusCode = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ttl = until - DateTimeOffset.UtcNow;
        if (ttl <= TimeSpan.Zero) return;

        // The fast marker is best effort; the database update remains the durable source of truth.
        try
        {
            await shortTermState.SetExpiryIfLaterAsync(
                AccountCooldownKey(pluginKey, id),
                until,
                until.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested) { }

        var untilUtc = until.UtcDateTime;
        var updatedAtUtc = DateTime.UtcNow;
        using var db = database.CreateClient();
        var updated = db.Updateable<AccountEntity>()
            .SetColumns(account => new AccountEntity
            {
                CooldownUntilUtc = SqlFunc.IIF(
                    account.CooldownUntilUtc == null || account.CooldownUntilUtc < untilUtc,
                    untilUtc,
                    account.CooldownUntilUtc),
                Reason = SqlFunc.IIF(
                    account.CooldownUntilUtc == null || account.CooldownUntilUtc < untilUtc,
                    reason,
                    account.Reason),
                LastStatusCode = SqlFunc.IIF(
                    account.CooldownUntilUtc == null || account.CooldownUntilUtc < untilUtc,
                    statusCode,
                    account.LastStatusCode),
                ConsecutiveFailures = account.ConsecutiveFailures + 1,
                UpdatedAtUtc = updatedAtUtc
            })
            .Where(account => account.Id == id && account.PluginKey == pluginKey)
            .ExecuteCommand();
        if (updated == 0)
            throw new KeyNotFoundException($"account '{id}' was not found");
    }

    public async Task<bool> ClearCooldownAsync(
        string pluginKey,
        string id,
        string expectedReason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedReason))
            throw new ArgumentException("expected cooldown reason is required", nameof(expectedReason));
        cancellationToken.ThrowIfCancellationRequested();

        if (shortTermState.IsConfigured)
            await shortTermState.RemoveAsync(AccountCooldownKey(pluginKey, id), cancellationToken);

        using var db = database.CreateClient();
        var updated = db.Updateable<AccountEntity>()
            .SetColumns(account => new AccountEntity
            {
                CooldownUntilUtc = null,
                Reason = null,
                LastStatusCode = null,
                ConsecutiveFailures = 0,
                UpdatedAtUtc = DateTime.UtcNow
            })
            .Where(account => account.Id == id
                && account.PluginKey == pluginKey
                && account.State == ResourceState.Active.ToString()
                && account.Reason == expectedReason)
            .ExecuteCommand();

        return updated > 0;
    }

    public Task DisableAsync(
        string pluginKey,
        string id,
        string reason,
        int? statusCode = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var updated = db.Updateable<AccountEntity>()
            .SetColumns(row => new AccountEntity
            {
                State = ResourceState.Disabled.ToString(), DisabledUntilUtc = null, Reason = reason,
                LastStatusCode = statusCode, ConsecutiveFailures = row.ConsecutiveFailures + 1, UpdatedAtUtc = DateTime.UtcNow
            })
            .Where(row => row.Id == id && row.PluginKey == pluginKey).ExecuteCommand();
        if (updated == 0) throw new KeyNotFoundException($"account '{id}' was not found");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Account?> CompareExchangeCredentialAsync(string pluginKey, string id, long expectedVersion,
        Credential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        cancellationToken.ThrowIfCancellationRequested();
        var (kind, json) = CredentialCodec.Encode(credential);
        using var db = database.CreateClient();
        var current = db.Queryable<AccountEntity>().First(row => row.Id == id && row.PluginKey == pluginKey);
        if (current is null || current.CredentialVersion != expectedVersion) return null;
        var expiresAt = credential switch
        {
            OAuthCredential oauth => oauth.ExpiresAt?.UtcDateTime ?? current.ExpiresAtUtc,
            BearerTokenCredential bearer => bearer.ExpiresAt?.UtcDateTime ?? current.ExpiresAtUtc,
            _ => current.ExpiresAtUtc
        };
        var label = credential is OAuthCredential { Nickname: { } nickname } ? nickname : current.Label;
        var changed = db.Updateable<AccountEntity>().SetColumns(row => new AccountEntity
        {
            CredentialKind = kind, CredentialJson = json, CredentialVersion = row.CredentialVersion + 1,
            ExpiresAtUtc = expiresAt, Label = label, UpdatedAtUtc = DateTime.UtcNow
        }).Where(row => row.Id == id && row.PluginKey == pluginKey && row.CredentialVersion == expectedVersion).ExecuteCommand();
        return changed == 0 ? null : await GetAsync(pluginKey, id, cancellationToken);
    }

    public static string AccountCooldownKey(string pluginKey, string accountId)
        => $"account:cooldown:{pluginKey}:{accountId}";

    private static int NormalizePageSize(int pageSize)
        => pageSize is 20 or 50 or 100 ? pageSize : 20;
}
