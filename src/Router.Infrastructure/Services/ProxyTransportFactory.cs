using System.Net;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Infrastructure.Services;

/// <summary>
/// 所有插件 HTTP 路径共用的物理连接池。只负责连接，不选择节点、账号或重试。
/// 客户端租约保证旧节点配置淘汰时不会截断正在读取的响应。
/// </summary>
public sealed class ProxyTransportFactory : IDisposable
{
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly Dictionary<TransportKey, Entry> _entries = [];
    private bool _disposed;
    internal int CachedHandlerCount { get { lock (_gate) return _entries.Count; } }

    /// <summary>为已选节点或显式直连创建客户端；调用方负责释放。</summary>
    /// <param name="proxy">已选节点，null 明确表示直连。</param>
    /// <param name="options">有限的传输选项，不包含重试或客户端名称。</param>
    /// <returns>持有连接池租约的客户端。</returns>
    public HttpClient CreateClient(ProxyEndpoint? proxy, PluginHttpClientOptions? options = null)
    {
        options ??= new PluginHttpClientOptions();
        ValidateTimeout(options.RequestTimeout);
        if (proxy is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(proxy.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(proxy.Host);
            if (proxy.Port is < 1 or > 65535 || !Enum.IsDefined(proxy.Scheme))
                throw new ArgumentException("Invalid proxy transport settings.", nameof(proxy));
        }
        // Copy connection settings: a mutable ProxyEndpoint must not change a SOCKS callback after publication.
        var key = new TransportKey(proxy?.Id, proxy?.ConfigurationVersion ?? 0, proxy?.Scheme ?? ProxyScheme.Http,
            proxy?.Host, proxy?.Port ?? 0, proxy?.Username, proxy?.Password, options.AllowAutoRedirect);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = DateTimeOffset.UtcNow;
            // ponytail: sweep on acquisition, not on a timer; an idle host retains at most its last working set.
            foreach (var pair in _entries.Where(pair => pair.Value.Clients == 0
                && (now - pair.Value.LastUsed >= IdleLifetime
                    || pair.Key.ProxyId == key.ProxyId && pair.Key != key
                        && pair.Key.AllowAutoRedirect == key.AllowAutoRedirect)).ToArray())
            {
                pair.Value.Dispose();
                _entries.Remove(pair.Key);
            }
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(CreateHandler(key));
                _entries.Add(key, entry);
            }
            entry.Clients++;
            return new HttpClient(new ClientLeaseHandler(entry.Sender, () => Release(entry)), disposeHandler: true)
            {
                Timeout = options.RequestTimeout
            };
        }
    }

    internal static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout != Timeout.InfiniteTimeSpan && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Request timeout must be positive or infinite.");
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            entry.Clients--;
            entry.LastUsed = DateTimeOffset.UtcNow;
        }
    }

    private static SocketsHttpHandler CreateHandler(TransportKey key)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = IdleLifetime,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = key.AllowAutoRedirect,
            UseCookies = false,
            UseProxy = false
        };
        if (key.ProxyId is null) return handler;

        var proxy = new ProxyEndpoint
        {
            Id = key.ProxyId, ConfigurationVersion = key.Version, Scheme = key.Scheme,
            Host = key.Host!, Port = key.Port, Username = key.Username, Password = key.Password
        };
        if (proxy.Scheme == ProxyScheme.Socks5)
        {
            handler.ConnectCallback = (context, token) => Socks5Connector.ConnectAsync(proxy, context.DnsEndPoint, token);
        }
        else
        {
            var webProxy = new WebProxy(proxy.ToUri());
            if (!string.IsNullOrWhiteSpace(proxy.Username))
                webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
            handler.Proxy = webProxy;
            handler.UseProxy = true;
        }
        return handler;
    }

    /// <summary>仅由宿主在关闭时释放全部连接池。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _entries.Values) entry.Dispose();
            _entries.Clear();
        }
    }

    // Private: keys contain proxy credentials and must never be logged or projected to plugins.
    private readonly record struct TransportKey(string? ProxyId, long Version, ProxyScheme Scheme,
        string? Host, int Port, string? Username, string? Password, bool AllowAutoRedirect);

    private sealed class Entry(SocketsHttpHandler handler) : IDisposable
    {
        public HttpMessageInvoker Sender { get; } = new(handler, disposeHandler: true);
        public int Clients { get; set; }
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
        public void Dispose() => Sender.Dispose();
    }

    private sealed class ClientLeaseHandler(HttpMessageInvoker sender, Action release) : HttpMessageHandler
    {
        private int _disposed;
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => sender.Send(request, cancellationToken);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => sender.SendAsync(request, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) release();
            base.Dispose(disposing);
        }
    }
}
