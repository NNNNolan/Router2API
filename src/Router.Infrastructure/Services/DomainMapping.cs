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
            RefreshIntervalSeconds = entity.RefreshIntervalSeconds is > 0
                ? entity.RefreshIntervalSeconds.Value
                : (long)(entity.RefreshIntervalMinutes > 0 ? entity.RefreshIntervalMinutes : 60) * 60,
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
            RefreshIntervalSeconds = subscription.RefreshIntervalSeconds,
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
            var values = ReadFields(root);

            if (NameEquals(kind, "apikey") || NameEquals(kind, "key") || IsPresent(values.ApiKey))
                return new ApiKeyCredential(Text(values.ApiKey) ?? string.Empty);

            if (NameEquals(kind, "basicauth") || NameEquals(kind, "basic") || NameEquals(kind, "usernamepassword")
                || IsPresent(values.Username) && IsPresent(values.Password))
                return new BasicAuthCredential(Text(values.Username) ?? string.Empty, Text(values.Password) ?? string.Empty);

            if (NameEquals(kind, "oauth") || NameEquals(kind, "oauth2") || NameEquals(kind, "tokenbundle")
                || IsPresent(values.AccessToken) || IsPresent(values.OAuthHint))
                return new OAuthCredential(
                    Text(values.AccessToken) ?? string.Empty,
                    ParseDate(Text(values.ExpiresAt)),
                    Text(values.RefreshToken),
                    Text(values.IdToken),
                    Text(values.AccountId),
                    Text(values.Domain),
                    Text(values.EnterpriseId),
                    Text(values.Nickname));

            if (NameEquals(kind, "bearer") || NameEquals(kind, "bearertoken") || NameEquals(kind, "token")
                || IsPresent(values.Token))
                return new BearerTokenCredential(Text(values.Token) ?? string.Empty, ParseDate(Text(values.ExpiresAt)));

            if (NameEquals(kind, "cookie") || IsPresent(values.Cookie))
                return new CookieCredential(Text(values.Cookie) ?? string.Empty);
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

    // Compare legacy spellings without creating normalized strings.
    internal static bool NameEquals(ReadOnlySpan<char> value, ReadOnlySpan<char> expected)
    {
        var index = 0;
        var expectedIndex = 0;
        while (true)
        {
            while (index < value.Length && value[index] is '_' or '-') index++;
            while (expectedIndex < expected.Length && expected[expectedIndex] is '_' or '-') expectedIndex++;
            if (index == value.Length || expectedIndex == expected.Length)
                return index == value.Length && expectedIndex == expected.Length;
            if (char.ToLowerInvariant(value[index++]) != char.ToLowerInvariant(expected[expectedIndex++])) return false;
        }
    }

    private static CredentialFields ReadFields(JsonElement root)
    {
        var fields = new CredentialFields();
        if (root.ValueKind != JsonValueKind.Object) return fields;
        foreach (var property in root.EnumerateObject())
        {
            var name = property.Name;
            switch (name)
            {
                case var _ when NameEquals(name, "apiKey") || NameEquals(name, "key"): SetFirst(ref fields.ApiKey, property.Value); break;
                case var _ when NameEquals(name, "username"): SetFirst(ref fields.Username, property.Value); break;
                case var _ when NameEquals(name, "password"): SetFirst(ref fields.Password, property.Value); break;
                case var _ when NameEquals(name, "accessToken"): SetFirst(ref fields.AccessToken, property.Value); break;
                case var _ when NameEquals(name, "refreshToken"):
                    SetFirst(ref fields.RefreshToken, property.Value); SetFirst(ref fields.OAuthHint, property.Value); break;
                case var _ when NameEquals(name, "idToken"):
                    SetFirst(ref fields.IdToken, property.Value); SetFirst(ref fields.OAuthHint, property.Value); break;
                case var _ when NameEquals(name, "accountId"):
                    SetFirst(ref fields.AccountId, property.Value); SetFirst(ref fields.OAuthHint, property.Value); break;
                case var _ when NameEquals(name, "domain"): SetFirst(ref fields.Domain, property.Value); break;
                case var _ when NameEquals(name, "enterpriseId"): SetFirst(ref fields.EnterpriseId, property.Value); break;
                case var _ when NameEquals(name, "nickname") || NameEquals(name, "displayName"): SetFirst(ref fields.Nickname, property.Value); break;
                case var _ when NameEquals(name, "expiresAt"): SetFirst(ref fields.ExpiresAt, property.Value); break;
                case var _ when NameEquals(name, "token"): SetFirst(ref fields.Token, property.Value); break;
                case var _ when NameEquals(name, "cookie"): SetFirst(ref fields.Cookie, property.Value); break;
            }
        }
        return fields;
    }

    private static void SetFirst(ref JsonElement field, JsonElement value)
    {
        // First matching property wins, including an explicit null.
        if (field.ValueKind == JsonValueKind.Undefined) field = value;
    }

    private static string? Text(JsonElement value)
        => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : value.ToString();

    private static bool IsPresent(JsonElement value)
        => value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);

    private struct CredentialFields
    {
        public JsonElement ApiKey;
        public JsonElement Username;
        public JsonElement Password;
        public JsonElement AccessToken;
        public JsonElement RefreshToken;
        public JsonElement IdToken;
        public JsonElement AccountId;
        public JsonElement Domain;
        public JsonElement EnterpriseId;
        public JsonElement Nickname;
        public JsonElement ExpiresAt;
        public JsonElement Token;
        public JsonElement Cookie;
        public JsonElement OAuthHint;
    }
    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var date) ? date : null;
}
