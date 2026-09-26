using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Router.Host.Configuration;

namespace Router.Host.Security;

/// <summary>管理员会话服务。</summary>
public sealed class AdminAuthService(
    IOptionsMonitor<AuthOptions> options,
    PasswordHasher hasher,
    ILogger<AdminAuthService> logger)
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FailureWindow> _failures = new(StringComparer.OrdinalIgnoreCase);

    public void MigratePlaintextPasswords()
    {
        foreach (var user in options.CurrentValue.Admin.Users)
        {
            if (string.IsNullOrWhiteSpace(user.Password) || !string.IsNullOrWhiteSpace(user.PasswordHash)) continue;
            user.PasswordHash = hasher.Hash(user.Password);
            user.Password = null;
            logger.LogInformation("migrated plaintext administrator password to an in-memory PBKDF2 hash for {Username}", user.Username);
        }
    }

    public LoginResult Login(string username, string password)
    {
        var normalizedUsername = username.Trim();
        if (IsBlocked(normalizedUsername))
            return new LoginResult(false, null, "too many login attempts", null);

        var user = options.CurrentValue.Admin.Users.FirstOrDefault(x =>
            string.Equals(x.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));

        if (user is null || !Verify(user, password))
        {
            RegisterFailure(normalizedUsername);
            logger.LogWarning("admin login failed for {Username}", normalizedUsername);
            return new LoginResult(false, null, "invalid credentials", null);
        }

        _failures.TryRemove(normalizedUsername, out _);
        var sessionId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var principal = new AdminPrincipal(
            user.Username,
            user.DisplayName,
            user.Roles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var expiresAt = DateTimeOffset.UtcNow.AddHours(options.CurrentValue.Admin.SessionLifetimeHours);
        _sessions[sessionId] = new Session(principal, expiresAt);
        logger.LogInformation("admin login succeeded for {Username}", normalizedUsername);
        return new LoginResult(true, sessionId, null, principal);
    }

    public void Logout(string sessionId) => _sessions.TryRemove(sessionId, out _);

    public bool ChangePassword(AdminPrincipal principal, string currentPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8) return false;
        var user = options.CurrentValue.Admin.Users.FirstOrDefault(item =>
            string.Equals(item.Username, principal.Username, StringComparison.OrdinalIgnoreCase));
        if (user is null || !Verify(user, currentPassword)) return false;

        user.PasswordHash = hasher.Hash(newPassword);
        user.Password = null;
        return true;
    }

    public AdminPrincipal? Validate(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return null;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(sessionId, out _);
            return null;
        }

        return session.Principal;
    }

    private bool Verify(AdminUserOptions user, string password)
    {
        if (!string.IsNullOrWhiteSpace(user.PasswordHash))
            return hasher.Verify(password, user.PasswordHash);

        if (!string.IsNullOrEmpty(user.Password))
        {
            user.PasswordHash = hasher.Hash(user.Password);
            user.Password = null;
            return hasher.Verify(password, user.PasswordHash);
        }

        return false;
    }

    private bool IsBlocked(string username)
        => _failures.TryGetValue(username, out var window)
            && window.FirstFailureAt.AddMinutes(1) > DateTimeOffset.UtcNow
            && window.Count >= 5;

    private void RegisterFailure(string username)
    {
        _failures.AddOrUpdate(
            username,
            _ => new FailureWindow(DateTimeOffset.UtcNow, 1),
            (_, current) => current.FirstFailureAt.AddMinutes(1) <= DateTimeOffset.UtcNow
                ? new FailureWindow(DateTimeOffset.UtcNow, 1)
                : current with { Count = current.Count + 1 });
    }

    private sealed record Session(AdminPrincipal Principal, DateTimeOffset ExpiresAt);
    private sealed record FailureWindow(DateTimeOffset FirstFailureAt, int Count);
}

/// <summary>管理员登录结果。</summary>
public sealed record LoginResult(
    bool Success,
    string? SessionId,
    string? Error,
    AdminPrincipal? Principal);

/// <summary>已认证的管理员身份。</summary>
public sealed record AdminPrincipal(
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles);
