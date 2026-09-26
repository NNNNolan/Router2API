using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using static Router.Host.Plugins.JavaScript.JsPluginCodec;

namespace Router.Host.Plugins.JavaScript;

/// <summary>账号的 JSON 边界；所有身份由绑定宿主验证，凭据只在显式授权时投影。</summary>
internal sealed class JsAccountCapabilities(JsPluginManifest manifest, IPluginServices host,
    Func<string?, JsCallContext, object?, Task<JsonNode?>> invoke)
{
    public async Task<object?> DispatchAsync(string operation, JsonObject input, JsInvocation invocation, CancellationToken token)
    {
        var id = input["id"]?.GetValue<string>() ?? "";
        switch (operation)
        {
            case "accounts.list":
                Require("read");
                var includeCredentials = input["includeCredentials"]?.GetValue<bool>() == true;
                if (includeCredentials) Require("readCredentials");
                return (await host.Accounts.ListAsync(input["platform"]?.GetValue<string>() ?? manifest.Platform.Name, token))
                    .Select(account =>
                    {
                        if (includeCredentials) invocation.RememberCredential(account.Credential);
                        return AccountMetadata(account, includeCredentials);
                    }).ToArray();
            case "accounts.get":
                Require("read");
                var found = await host.Accounts.GetAsync(ValidateId(id), token);
                return found is null ? null : AccountMetadata(found);
            case "accounts.readCredentials":
                Require("readCredentials");
                var source = await GetAsync(id, token);
                invocation.RememberCredential(source.Credential);
                return new { accountId = source.Id, version = source.CredentialVersion.ToString(CultureInfo.InvariantCulture), credential = CredentialJson(source.Credential) };
            case "accounts.save":
                Require("write");
                return await SaveAsync(input, invocation, token);
            case "accounts.delete":
                Require("write");
                await host.Accounts.DeleteAsync(ValidateId(id), token);
                return null;
            case "accounts.compareExchangeCredential":
                Require("write");
                if (!long.TryParse(input["expectedVersion"]?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
                    throw new InvalidOperationException("expectedVersion must be a credential version string.");
                var credential = ParseCredential(input["credential"]);
                invocation.RememberCredential(credential);
                var saved = await host.Accounts.CompareExchangeCredentialAsync(ValidateId(id), version, credential, token);
                return saved is null ? null : AccountMetadata(saved);
            case "accounts.refresh":
                Require("refresh");
                if (invocation.Context.Kind == JsCallKind.Callback || manifest.Hooks.RefreshCredential is not { } handler)
                    throw new InvalidOperationException("Declare refreshCredential; nested credential refresh is not allowed.");
                var refreshed = await host.Accounts.RefreshCredentialAsync(ValidateId(id), async (account, refreshToken) =>
                {
                    invocation.RememberCredential(account.Credential);
                    var value = await invoke(handler, new JsCallContext(JsCallKind.Callback, TimeSpan.FromSeconds(55),
                        CredentialAccount: account, Token: refreshToken), AccountMetadata(account, includeCredential: true));
                    var updated = ParseCredential(value);
                    invocation.RememberCredential(updated);
                    return updated;
                }, token);
                invocation.RememberCredential(refreshed.Credential);
                return AccountMetadata(refreshed, includeCredential: true);
            case "accounts.setCooldown":
                Require("setCooldown");
                var until = input["until"]?.GetValue<DateTimeOffset>() ?? throw new InvalidOperationException("Cooldown deadline is required.");
                if (until > DateTimeOffset.UtcNow.AddDays(30)) throw new InvalidOperationException("Cooldown must be within 30 days.");
                await host.Accounts.SetCooldownAsync(ValidateId(id), until, Reason(input), StatusCode(input), token);
                return null;
            case "accounts.clearCooldown":
                Require("setCooldown");
                return await host.Accounts.ClearCooldownAsync(ValidateId(id), input["expectedReason"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("expectedReason is required."), token);
            case "accounts.disable":
                Require("disable");
                await host.Accounts.DisableAsync(ValidateId(id), Reason(input), StatusCode(input), token);
                return null;
            default:
                throw new UnauthorizedAccessException("Unknown account capability.");
        }
    }

    private async Task<object> SaveAsync(JsonObject input, JsInvocation invocation, CancellationToken token)
    {
        var patch = input.Deserialize<AccountInput>(AttemptJsonOptions) ?? throw new InvalidOperationException("Invalid account input.");
        var id = patch.Id is null ? Guid.NewGuid().ToString("N") : ValidateId(patch.Id);
        var existing = await host.Accounts.GetAsync(id, token);
        var platform = patch.Platform ?? existing?.Platform ?? manifest.Platform.Name;
        var credential = patch.Credential is null ? existing?.Credential : ParseCredential(patch.Credential, platform);
        if (credential is null) throw new InvalidOperationException("New accounts require a credential.");
        invocation.RememberCredential(credential);
        var account = new Account
        {
            Id = id, PluginKey = host.PluginKey, Platform = platform,
            Credential = credential, CredentialVersion = existing?.CredentialVersion ?? 0,
            Label = input.ContainsKey("label") ? patch.Label : existing?.Label,
            ExpiresAt = input.ContainsKey("expiresAt") ? patch.ExpiresAt
                : patch.Credential is not null ? CredentialExpiry(credential) ?? existing?.ExpiresAt : existing?.ExpiresAt,
            Status = existing is null ? new ResourceStatus() : CloneStatus(existing.Status)
        };
        if (existing is not null && account.Platform != existing.Platform)
            throw new InvalidOperationException("Account platform cannot be changed.");
        if (patch.Status is { } status)
        {
            _ = status.Deserialize<StatusInput>(AttemptJsonOptions); // Reject host-only fields such as InFlight.
            if (status.ContainsKey("state") || status.ContainsKey("disabledUntil")) Require("disable");
            if (status.Any(pair => pair.Key is not ("state" or "disabledUntil"))) Require("setCooldown");
            if (status["state"] is { } state)
            {
                account.Status.State = state.Deserialize<ResourceState>(JsonOptions);
                if (!Enum.IsDefined(account.Status.State)) throw new InvalidOperationException("Invalid resource state.");
            }
            if (status.ContainsKey("reason")) account.Status.Reason = status["reason"]?.GetValue<string>();
            if (status.ContainsKey("cooldownUntil")) account.Status.CooldownUntil = status["cooldownUntil"]?.GetValue<DateTimeOffset>();
            if (status.ContainsKey("disabledUntil")) account.Status.DisabledUntil = status["disabledUntil"]?.GetValue<DateTimeOffset>();
            if (status.ContainsKey("lastStatusCode")) account.Status.LastStatusCode = status["lastStatusCode"]?.GetValue<int>();
            if (status.ContainsKey("consecutiveFailures")) account.Status.ConsecutiveFailures = status["consecutiveFailures"]!.GetValue<int>();
            if (account.Status.ConsecutiveFailures < 0 || account.Status.Reason?.Length > 2000
                || account.Status.CooldownUntil > DateTimeOffset.UtcNow.AddDays(30))
                throw new InvalidOperationException("Invalid account status.");
        }
        if (account.Label?.Length > 128 || !AllowedPlatform(account.Platform)) throw new InvalidOperationException("Invalid account label or platform.");
        if (existing is null) return AccountMetadata(await host.Accounts.SaveAsync(account, token));
        var fields = input.Select(pair => pair.Key).Where(key => key is "label" or "expiresAt" or "credential").ToList();
        if (patch.Credential is not null && CredentialExpiry(credential) is not null && !fields.Contains("expiresAt", StringComparer.Ordinal)) fields.Add("expiresAt");
        if (patch.Status is not null) fields.AddRange(patch.Status.Select(pair => "status." + pair.Key));
        return AccountMetadata(await host.Accounts.PatchAsync(account, fields, token));
    }

    public Credential ParseCredential(JsonNode? value, string? platform = null)
    {
        if (value is null || Encoding.UTF8.GetByteCount(value.ToJsonString()) > 64 * 1024)
            throw new InvalidOperationException("Missing or oversized credential.");
        var kind = value["kind"]?.GetValue<string>() ?? "";
        var kinds = platform is not null && manifest.PackageCredentialKinds?.TryGetValue(platform, out var declared) == true
            ? declared : manifest.Platform.CredentialKinds;
        if (!kinds.Contains(kind, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Credential kind is not declared by the platform.");
        Credential credential = kind switch
        {
            "ApiKey" => value.Deserialize<ApiKeyCredential>(AttemptJsonOptions)!,
            "OAuth" => value.Deserialize<OAuthCredential>(AttemptJsonOptions)!,
            "Custom" => value.Deserialize<CustomCredential>(AttemptJsonOptions)!,
            "BearerToken" => value.Deserialize<BearerTokenCredential>(AttemptJsonOptions)!,
            "BasicAuth" => value.Deserialize<BasicAuthCredential>(AttemptJsonOptions)!,
            "Cookie" => value.Deserialize<CookieCredential>(AttemptJsonOptions)!,
            _ => throw new InvalidOperationException("Unknown credential kind.")
        };
        if (credential is CustomCredential { Fields: null }) throw new InvalidOperationException("Custom credentials need fields.");
        return credential;
    }

    private async Task<Account> GetAsync(string id, CancellationToken token)
        => await host.Accounts.GetAsync(ValidateId(id), token) ?? throw new KeyNotFoundException("Account not found.");
    private void Require(string permission)
    {
        if (!manifest.Permissions.Accounts.Contains(permission, StringComparer.Ordinal))
            throw new UnauthorizedAccessException($"Account capability '{permission}' is not permitted.");
    }
    private bool AllowedPlatform(string platform) => (manifest.PackagePlatforms ?? [manifest.Platform.Name]).Contains(platform, StringComparer.Ordinal);
    private static string ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(char.IsControl))
            throw new InvalidOperationException("Account id must be 1–64 non-control characters.");
        return id;
    }
    private static string Reason(JsonObject input)
    {
        var reason = input["reason"]?.GetValue<string>() ?? "";
        return reason.Length is > 0 and <= 2000 ? reason : throw new InvalidOperationException("Invalid account action reason.");
    }
    private static int? StatusCode(JsonObject input)
    {
        var status = input["statusCode"]?.GetValue<int>();
        return status is null or >= 100 and <= 599 ? status : throw new InvalidOperationException("Invalid HTTP status.");
    }
    private static DateTimeOffset? CredentialExpiry(Credential credential) => credential switch
    {
        OAuthCredential oauth => oauth.ExpiresAt, BearerTokenCredential bearer => bearer.ExpiresAt, _ => null
    };
    private static ResourceStatus CloneStatus(ResourceStatus status) => new()
    {
        State = status.State, CooldownUntil = status.CooldownUntil, DisabledUntil = status.DisabledUntil,
        Reason = status.Reason, LastStatusCode = status.LastStatusCode, ConsecutiveFailures = status.ConsecutiveFailures,
        InFlight = status.InFlight, Version = status.Version
    };
    private sealed record AccountInput(string? Id, string? Platform, string? Label, DateTimeOffset? ExpiresAt,
        JsonNode? Credential, JsonObject? Status);
    private sealed record StatusInput(ResourceState? State, DateTimeOffset? CooldownUntil, DateTimeOffset? DisabledUntil,
        string? Reason, int? LastStatusCode, int? ConsecutiveFailures);
}
