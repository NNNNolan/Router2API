using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Infrastructure.Services;

/// <summary>旧连接池键契约，保留供已编译调用方使用。物理连接缓存由 ProxyTransportFactory 管理。</summary>
public readonly record struct ProxyClientKey(
    string ClientName,
    string ProxyId,
    long ProxyConfigVersion,
    ProxyScheme TransportProfile);

/// <summary>宿主使用的动态代理 HttpClient 工厂。</summary>
public sealed class ProxyHttpClientFactory(ProxyTransportFactory transport) : IProxyHttpClientFactory
{
    private readonly ProxyTransportFactory _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    // Attempt clients also serve permission-checked JS. Redirects would bypass its origin allowlist.
    private static readonly PluginHttpClientOptions LegacyOptions = new();

    public HttpClient CreateDirectClient(string clientName = "direct")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        return _transport.CreateClient(null, LegacyOptions);
    }

    public HttpClient CreateClient(ProxyEndpoint proxy, string clientName = "proxy")
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        return _transport.CreateClient(proxy, LegacyOptions);
    }

    /// <summary>兼容旧接口；共享 transport 属于宿主，插件不能通过此适配器关闭它。</summary>
    public void Dispose() { }
}

internal static class Socks5Connector
{
    public static async ValueTask<Stream> ConnectAsync(
        ProxyEndpoint proxy,
        DnsEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(proxy.Host, proxy.Port, cancellationToken);
            client.NoDelay = true;
            var stream = client.GetStream();
            await NegotiateAsync(stream, proxy, endpoint, cancellationToken);
            return stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task NegotiateAsync(
        Stream stream,
        ProxyEndpoint proxy,
        DnsEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        var hasCredentials = !string.IsNullOrWhiteSpace(proxy.Username)
            || !string.IsNullOrWhiteSpace(proxy.Password);
        var methods = hasCredentials ? new byte[] { 0, 2 } : [0];
        var greeting = new byte[2 + methods.Length];
        greeting[0] = 5;
        greeting[1] = (byte)methods.Length;
        methods.CopyTo(greeting, 2);
        await WriteAsync(stream, greeting, cancellationToken);

        var selection = await ReadAsync(stream, 2, cancellationToken);
        if (selection[0] != 5)
            throw new HttpRequestException("SOCKS5 proxy returned an invalid protocol version");
        if (selection[1] == 2)
        {
            if (!hasCredentials)
                throw new HttpRequestException("SOCKS5 proxy requires username/password authentication");
            await AuthenticateAsync(stream, proxy, cancellationToken);
        }
        else if (selection[1] != 0)
        {
            throw new HttpRequestException($"SOCKS5 proxy rejected authentication method {selection[1]}");
        }

        var address = EncodeAddress(endpoint.Host);
        var request = new byte[address.Length + 5];
        request[0] = 5;
        request[1] = 1;
        request[2] = 0;
        request[3] = address[0];
        Buffer.BlockCopy(address, 1, request, 4, address.Length - 1);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4 + address.Length - 1), checked((ushort)endpoint.Port));
        await WriteAsync(stream, request, cancellationToken);

        var response = await ReadAsync(stream, 4, cancellationToken);
        if (response[0] != 5)
            throw new HttpRequestException("SOCKS5 proxy returned an invalid connect response");
        if (response[1] != 0)
            throw new HttpRequestException($"SOCKS5 proxy connection failed with code {response[1]}");

        var boundAddressLength = response[3] switch
        {
            1 => 4,
            3 => (await ReadAsync(stream, 1, cancellationToken))[0],
            4 => 16,
            _ => throw new HttpRequestException("SOCKS5 proxy returned an invalid address type")
        };
        await ReadAsync(stream, boundAddressLength + 2, cancellationToken);
    }

    private static async Task AuthenticateAsync(
        Stream stream,
        ProxyEndpoint proxy,
        CancellationToken cancellationToken)
    {
        var username = Encoding.UTF8.GetBytes(proxy.Username ?? string.Empty);
        var password = Encoding.UTF8.GetBytes(proxy.Password ?? string.Empty);
        if (username.Length > byte.MaxValue || password.Length > byte.MaxValue)
            throw new HttpRequestException("SOCKS5 username/password is too long");

        var request = new byte[3 + username.Length + password.Length];
        request[0] = 1;
        request[1] = (byte)username.Length;
        username.CopyTo(request, 2);
        request[2 + username.Length] = (byte)password.Length;
        password.CopyTo(request, 3 + username.Length);
        await WriteAsync(stream, request, cancellationToken);

        var response = await ReadAsync(stream, 2, cancellationToken);
        if (response[0] != 1 || response[1] != 0)
            throw new HttpRequestException("SOCKS5 proxy username/password authentication failed");
    }

    private static byte[] EncodeAddress(string host)
    {
        if (IPAddress.TryParse(host, out var ipAddress))
            return [ipAddress.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4, .. ipAddress.GetAddressBytes()];

        var asciiHost = new IdnMapping().GetAscii(host);
        var hostBytes = Encoding.ASCII.GetBytes(asciiHost);
        if (hostBytes.Length is 0 or > byte.MaxValue)
            throw new HttpRequestException("SOCKS5 target host is invalid");
        return [3, (byte)hostBytes.Length, .. hostBytes];
    }

    private static async ValueTask WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask<byte[]> ReadAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes.AsMemory(), cancellationToken);
        return bytes;
    }
}
