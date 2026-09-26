namespace Router.Host.Configuration;

/// <summary>认证配置。</summary>
public sealed class AuthOptions
{
    public AdminOptions Admin { get; set; } = new();
    public ApiKeyOptions ApiKey { get; set; } = new();
}

/// <summary>管理员账号配置。</summary>
public sealed class AdminOptions
{
    public bool Enabled { get; set; } = true;
    public int SessionLifetimeHours { get; set; } = 12;
    public List<AdminUserOptions> Users { get; set; } = [];
}

/// <summary>已配置的管理员。</summary>
public sealed class AdminUserOptions
{
    public string Username { get; set; } = string.Empty;
    public string? Password { get; set; }
    public string? PasswordHash { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = ["admin"];
}

/// <summary>下游 API Key 配置。</summary>
public sealed class ApiKeyOptions
{
    public bool Enabled { get; set; } = true;
    public string Key { get; set; } = string.Empty;
}
