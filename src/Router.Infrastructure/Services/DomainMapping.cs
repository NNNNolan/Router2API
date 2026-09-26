using System.Text.Json;
using Router.Contracts.Domain;
using Router.Infrastructure.Persistence;

namespace Router.Infrastructure.Services;

internal static class DomainMapping
{
    public static Account ToDomain(this AccountEntity entity)
        => new()
        {
            Id = entity.Id,
            PluginKey = entity.PluginKey,
            Platform = entity.Platform,
            Credential = CredentialCodec.Decode(entity.CredentialKind, entity.CredentialJson),
            CredentialVersion = entity.CredentialVersion,
            ExpiresAt = entity.ExpiresAtUtc.HasValue
                ? new DateTimeOffset(entity.ExpiresAtUtc.Value, TimeSpan.Zero)
                : null,
            Label = entity.Label,
            Status = new ResourceStatus
            {
                State = Enum.TryParse<ResourceState>(entity.State, out var state)
                    ? state
                    : ResourceState.Active,
                CooldownUntil = entity.CooldownUntilUtc.HasValue
                    ? new DateTimeOffset(entity.CooldownUntilUtc.Value, TimeSpan.Zero)
                    : null,
                DisabledUntil = entity.DisabledUntilUtc.HasValue
                    ? new DateTimeOffset(entity.DisabledUntilUtc.Value, TimeSpan.Zero)
                    : null,
                Reason = entity.Reason,
                LastStatusCode = entity.LastStatusCode,
                ConsecutiveFailures = entity.ConsecutiveFailures,
                InFlight = entity.InFlight,
                Version = entity.Version
            }
        };

    public static AccountEntity ToEntity(this Account account)
    {
        var (kind, json) = CredentialCodec.Encode(account.Credential);
        return new AccountEntity
        {
            Id = account.Id,
            PluginKey = account.PluginKey,
            Platform = account.Platform,
            CredentialKind = kind,
            CredentialJson = json,
            CredentialVersion = account.CredentialVersion,
            State = account.Status.State.ToString(),
            ExpiresAtUtc = account.ExpiresAt?.UtcDateTime,
            CooldownUntilUtc = account.Status.CooldownUntil?.UtcDateTime,
            DisabledUntilUtc = account.Status.DisabledUntil?.UtcDateTime,
            Reason = account.Status.Reason ?? account.Status.CooldownReason,
            LastStatusCode = account.Status.LastStatusCode,
            ConsecutiveFailures = account.Status.ConsecutiveFailures,
            Label = account.Label,
            InFlight = account.Status.InFlight,
            Version = account.Status.Version,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public static ProxyEndpoint ToDomain(this ProxyEndpointEntity entity)
    {
        var proxy = new ProxyEndpoint
        {
            Id = entity.Id,
            SubscriptionId = entity.SubscriptionId,
            Host = entity.Host,
            Port = entity.Port,
            Scheme = Enum.TryParse<ProxyScheme>(entity.Scheme, out var scheme)
                ? scheme
                : ProxyScheme.Http,
            Username = entity.Username,
            Password = entity.Password,
            ConfigurationVersion = entity.ConfigurationVersion,
            LatencyMs = entity.LatencyMs,
            AverageLatencyMs = entity.AverageLatencyMs,
            AverageSpeedBytesPerSecond = entity.AverageSpeedBytesPerSecond,
            CompositeScore = entity.CompositeScore,
            LastProbedAt = entity.LastProbedAtUtc.HasValue
                ? new DateTimeOffset(entity.LastProbedAtUtc.Value, TimeSpan.Zero)
                : null,
            LastProbeSuccessAt = entity.LastProbeSuccessAtUtc.HasValue
                ? new DateTimeOffset(entity.LastProbeSuccessAtUtc.Value, TimeSpan.Zero)
                : null,
            ProbeConsecutiveFailures = entity.ProbeConsecutiveFailures,
            ProbeError = entity.ProbeError,
            ProbeStatus = entity.ProbeStatus,
            ProbeConfigurationVersion = entity.ProbeConfigurationVersion,
            FirstSeenAt = new DateTimeOffset(entity.FirstSeenAtUtc, TimeSpan.Zero),
            LastSeenInSubscriptionAt = entity.LastSeenInSubscriptionAtUtc.HasValue
                ? new DateTimeOffset(entity.LastSeenInSubscriptionAtUtc.Value, TimeSpan.Zero)
                : null,
            Status = new ResourceStatus
            {
                State = Enum.TryParse<ResourceState>(entity.State, out var state)
                    ? state
                    : ResourceState.Active,
                ConsecutiveFailures = entity.ConsecutiveFailures,
                InFlight = entity.InFlight,
                Version = entity.Version
            }
        };

        return proxy;
    }

    public static ProxyEndpointEntity ToEntity(this ProxyEndpoint proxy)
        => new()
        {
            Id = proxy.Id,
            SubscriptionId = proxy.SubscriptionId,
            Host = proxy.Host,
            Port = proxy.Port,
            Scheme = proxy.Scheme.ToString(),
            Username = proxy.Username,
            Password = proxy.Password,
            ConfigurationVersion = proxy.ConfigurationVersion,
            State = proxy.Status.State.ToString(),
            InFlight = proxy.Status.InFlight,
            Version = proxy.Status.Version,
            ConsecutiveFailures = proxy.Status.ConsecutiveFailures,
            LatencyMs = proxy.LatencyMs,
            AverageLatencyMs = proxy.AverageLatencyMs,
            AverageSpeedBytesPerSecond = proxy.AverageSpeedBytesPerSecond,
            CompositeScore = proxy.CompositeScore,
            LastProbedAtUtc = proxy.LastProbedAt?.UtcDateTime,
            LastProbeSuccessAtUtc = proxy.LastProbeSuccessAt?.UtcDateTime,
            ProbeConsecutiveFailures = proxy.ProbeConsecutiveFailures,
            ProbeError = proxy.ProbeError,
            ProbeStatus = proxy.ProbeStatus,
            ProbeConfigurationVersion = proxy.ProbeConfigurationVersion,
            FirstSeenAtUtc = proxy.FirstSeenAt.UtcDateTime,
            LastSeenInSubscriptionAtUtc = proxy.LastSeenInSubscriptionAt?.UtcDateTime
        };

    public static ProxySubscription ToDomain(this ProxySubscriptionEntity entity)
        => new()
        {
            Id = entity.Id,
            Name = entity.Name,
            Url = entity.Url,
            Scheme = Enum.TryParse<ProxyScheme>(entity.Scheme, out var scheme)
                ? scheme
                : ProxyScheme.Http,
            Enabled = entity.Enabled,
            ParserName = entity.ParserName,
            RefreshIntervalMinutes = entity.RefreshIntervalMinutes > 0
                ? entity.RefreshIntervalMinutes
                : 60,
            LastFetchedAt = entity.LastFetchedAtUtc.HasValue
                ? new DateTimeOffset(entity.LastFetchedAtUtc.Value, TimeSpan.Zero)
                : null,
            LastFetchedCount = entity.LastFetchedCount,
            LastError = entity.LastError
        };

    public static ProxySubscriptionEntity ToEntity(this ProxySubscription subscription)
        => new()
        {
            Id = subscription.Id,
            Name = subscription.Name,
            Url = subscription.Url,
            Scheme = subscription.Scheme.ToString(),
            Enabled = subscription.Enabled,
            ParserName = subscription.ParserName,
            RefreshIntervalMinutes = subscription.RefreshIntervalMinutes,
            LastFetchedAtUtc = subscription.LastFetchedAt?.UtcDateTime,
            LastFetchedCount = subscription.LastFetchedCount,
            LastError = subscription.LastError
        };

}

internal static class CredentialCodec
{
    public static (string Kind, string Json) Encode(Credential credential)
        => credential switch
        {
            ApiKeyCredential apiKey => (credential.Kind.ToString(), JsonSerializer.Serialize(new { apiKey.ApiKey })),
            OAuthCredential oauth => (credential.Kind.ToString(), JsonSerializer.Serialize(new
            {
                access_token = oauth.AccessToken,
                refresh_token = oauth.RefreshToken,
                id_token = oauth.IdToken,
                account_id = oauth.AccountId,
                domain = oauth.Domain,
                enterprise_id = oauth.EnterpriseId,
                nickname = oauth.Nickname,
                expires_at = oauth.ExpiresAt
            })),
            BearerTokenCredential bearer => (credential.Kind.ToString(), JsonSerializer.Serialize(new
            {
                token = bearer.Token,
                expires_at = bearer.ExpiresAt
            })),
            BasicAuthCredential basic => (credential.Kind.ToString(), JsonSerializer.Serialize(new
            {
                username = basic.Username,
                password = basic.Password
            })),
            CookieCredential cookie => (credential.Kind.ToString(), JsonSerializer.Serialize(new { cookie.Cookie })),
            CustomCredential custom => (credential.Kind.ToString(), JsonSerializer.Serialize(custom.Fields)),
            _ => (credential.Kind.ToString(), "{}")
        };

    public static Credential Decode(string kind, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = document.RootElement;
            var normalizedKind = Normalize(kind);

            if (normalizedKind is "apikey" or "key" || Read(root, "apiKey", "api_key", "key") is not null)
                return new ApiKeyCredential(Read(root, "apiKey", "api_key", "key") ?? string.Empty);

            if (normalizedKind is "basicauth" or "basic" or "usernamepassword"
                || Read(root, "username") is not null && Read(root, "password") is not null)
                return new BasicAuthCredential(Read(root, "username") ?? string.Empty, Read(root, "password") ?? string.Empty);

            if (normalizedKind is "oauth" or "oauth2" or "tokenbundle"
                || Read(root, "accessToken", "access_token") is not null
                || Read(root, "refreshToken", "refresh_token", "idToken", "id_token", "accountId", "account_id") is not null)
                return new OAuthCredential(
                    Read(root, "accessToken", "access_token") ?? string.Empty,
                    ParseDate(Read(root, "expiresAt", "expires_at")),
                    Read(root, "refreshToken", "refresh_token"),
                    Read(root, "idToken", "id_token"),
                    Read(root, "accountId", "account_id"),
                    Read(root, "domain"),
                    Read(root, "enterpriseId", "enterprise_id"),
                    Read(root, "nickname", "displayName", "display_name"));

            if (normalizedKind is "bearer" or "bearertoken" or "token"
                || Read(root, "token") is not null)
                return new BearerTokenCredential(
                    Read(root, "token") ?? string.Empty,
                    ParseDate(Read(root, "expiresAt", "expires_at")));

            if (normalizedKind is "cookie" || Read(root, "cookie") is not null)
                return new CookieCredential(Read(root, "cookie") ?? string.Empty);

            var fields = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject()
                    .Where(property => property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    .ToDictionary(property => property.Name, property => (string?)property.Value.ToString(), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            return fields.Count > 0
                ? new CustomCredential(fields)
                : new ApiKeyCredential(string.Empty);
        }
        catch (JsonException)
        {
            return new ApiKeyCredential(string.Empty);
        }
    }

    private static string Normalize(string? value)
        => (value ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

    private static string? Read(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Any(name => Normalize(property.Name) == Normalize(name))) continue;
            return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.ToString();
        }

        return null;
    }

    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var date) ? date : null;
}
