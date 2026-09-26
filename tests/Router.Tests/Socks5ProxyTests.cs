using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Router.Contracts.Domain;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class Socks5ProxyTests
{
    [TestMethod]
    public async Task HttpClientUsesSocks5ConnectCallback()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = AcceptAndRespondAsync(listener);
        var proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var transport = new ProxyTransportFactory();
        var factory = new ProxyHttpClientFactory(transport);

        try
        {
            using var client = factory.CreateClient(new ProxyEndpoint
            {
                Host = "127.0.0.1",
                Port = proxyPort,
                Scheme = ProxyScheme.Socks5
            }, "upstream");

            using var response = await client.GetAsync("http://example.test/health");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Be("ok");
            await serverTask;
        }
        finally
        {
            factory.Dispose();
            listener.Stop();
        }
    }

    private static async Task AcceptAndRespondAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();

        (await ReadExactlyAsync(stream, 3)).Should().Equal(5, 1, 0);
        await stream.WriteAsync(new byte[] { 5, 0 });

        var connectRequest = await ReadExactlyAsync(stream, 4);
        connectRequest.Should().Equal(5, 1, 0, 3);
        var hostLength = (await ReadExactlyAsync(stream, 1))[0];
        var host = Encoding.ASCII.GetString(await ReadExactlyAsync(stream, hostLength));
        host.Should().Be("example.test");
        var portBytes = await ReadExactlyAsync(stream, 2);
        ((portBytes[0] << 8) | portBytes[1]).Should().Be(80);

        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 });
        await stream.FlushAsync();

        var request = new byte[4096];
        (await stream.ReadAsync(request)).Should().BeGreaterThan(0);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        await stream.FlushAsync();
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length)
    {
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes.AsMemory());
        return bytes;
    }
}
