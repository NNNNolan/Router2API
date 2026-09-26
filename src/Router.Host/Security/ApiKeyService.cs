using Microsoft.Extensions.Options;
using Router.Host.Configuration;

namespace Router.Host.Security;

/// <summary>保存当前下游 API Key。</summary>
public sealed class ApiKeyService : IDisposable
{
    private string _key;
    private readonly IDisposable? _optionsSubscription;

    public ApiKeyService(IOptionsMonitor<AuthOptions> options)
    {
        _key = options.CurrentValue.ApiKey.Key ?? string.Empty;
        _optionsSubscription = options.OnChange(value => Volatile.Write(ref _key, value.ApiKey.Key ?? string.Empty));
    }

    public string Current => Volatile.Read(ref _key);

    public string Rotate()
    {
        var key = $"sk-router-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
        Volatile.Write(ref _key, key);
        return key;
    }

    public string Masked()
    {
        var key = Current;
        return key.Length <= 8 ? "********" : $"{key[..4]}...{key[^4..]}";
    }

    public void Dispose() => _optionsSubscription?.Dispose();
}
