using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class ProxyPoolHttpClientFactoryTests : IDisposable
{
    private readonly ProxyTransportFactory _transport = new();
    private readonly PluginResiliencePipelines _resilience = new();

    [TestCleanup]
    public void Dispose()
    {
        _transport.Dispose();
        _resilience.Dispose();
    }
    [TestMethod]
    public async Task EmptyPoolDoesNotSilentlyFallBackToDirect()
    {
        var pool = CreatePool([]);
        var act = () => pool.CreateClientAsync();
        await act.Should().ThrowAsync<ProxyPoolUnavailableException>();
    }

    [TestMethod]
    public async Task FiltersSubscriptionsAndUnavailableNodes()
    {
        await using var server = new TestServer("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
        var selected = Node(server.Port);
        var pool = CreatePool([
            Node(UnusedPort(), subscription: "other"),
            Node(UnusedPort(), state: ResourceState.Disabled),
            selected
        ]);
        using var response = await pool.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "http://upstream.invalid/check"),
            new ProxyPoolHttpClientOptions { SubscriptionIds = ["enabled"] });

        (await response.Content.ReadAsStringAsync()).Should().Be("ok");
        (await server.Requests).Single().Should().StartWith("GET http://upstream.invalid/check");
    }

    [TestMethod]
    [DataRow(401)]
    [DataRow(407)]
    [DataRow(429)]
    [DataRow(500)]
    [DataRow(503)]
    public async Task HttpErrorIsReturnedWithoutRetryOrResourcePenalty(int status)
    {
        await using var server = new TestServer($"HTTP/1.1 {status} Error\r\nContent-Length: 7\r\n\r\nlimited");
        var pool = CreatePool([Node(server.Port)]);
        var requests = 0;
        using var response = await pool.SendAsync(
            () =>
            {
                requests++;
                return new HttpRequestMessage(HttpMethod.Get, "http://upstream.invalid/");
            },
            retry: new ProxyPoolRetryOptions { MaxRetries = 3, Delay = TimeSpan.Zero });

        ((int)response.StatusCode).Should().Be(status);
        (await response.Content.ReadAsStringAsync()).Should().Be("limited");
        requests.Should().Be(1);
    }

    [TestMethod]
    public async Task TransportRetryCreatesANewRequestAndSelectsTheUpdatedPool()
    {
        await using var server = new TestServer("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
        var nodes = new Mock<IProxyStore>();
        var failedNode = Node(UnusedPort());
        nodes.SetupSequence(store => store.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { failedNode })
            .ReturnsAsync(new[] { failedNode, Node(server.Port) });
        var pool = new ProxyPoolHttpClientFactory(nodes.Object, Subscriptions(), _transport, _resilience);
        var requests = new List<HttpRequestMessage>();
        using var response = await pool.SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "http://upstream.invalid/");
                requests.Add(request);
                return request;
            },
            retry: new ProxyPoolRetryOptions { MaxRetries = 1, Delay = TimeSpan.Zero });

        (await response.Content.ReadAsStringAsync()).Should().Be("ok");
        requests.Should().HaveCount(2);
        requests[0].Should().NotBeSameAs(requests[1]);
    }

    [TestMethod]
    public async Task PostIsNotRetriedUnlessReplayIsExplicitlyAllowed()
    {
        var pool = CreatePool([Node(UnusedPort())]);
        var requests = 0;
        var act = () => pool.SendAsync(
            () =>
            {
                requests++;
                return new HttpRequestMessage(HttpMethod.Post, "http://upstream.invalid/");
            },
            retry: new ProxyPoolRetryOptions { MaxRetries = 2, Delay = TimeSpan.Zero });

        await act.Should().ThrowAsync<HttpRequestException>();
        requests.Should().Be(1);
    }

    [TestMethod]
    public async Task ExplicitlyAllowedPostReplayUsesANewRequest()
    {
        await using var server = new TestServer("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
        var nodes = new Mock<IProxyStore>();
        nodes.SetupSequence(store => store.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Node(UnusedPort()) })
            .ReturnsAsync(new[] { Node(server.Port) });
        var pool = new ProxyPoolHttpClientFactory(nodes.Object, Subscriptions(), _transport, _resilience);
        var requests = 0;
        using var response = await pool.SendAsync(
            () =>
            {
                requests++;
                return new HttpRequestMessage(HttpMethod.Post, "http://upstream.invalid/");
            },
            retry: new ProxyPoolRetryOptions { MaxRetries = 1, Delay = TimeSpan.Zero, AllowUnsafeMethods = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        requests.Should().Be(2);
    }

    [TestMethod]
    public async Task CallerCancellationStopsRetryBackoff()
    {
        var pool = CreatePool([Node(UnusedPort())]);
        using var cancellation = new CancellationTokenSource();
        var requests = 0;
        var act = () => pool.SendAsync(
            () =>
            {
                requests++;
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
                return new HttpRequestMessage(HttpMethod.Get, "http://upstream.invalid/");
            },
            retry: new ProxyPoolRetryOptions { MaxRetries = 3, Delay = TimeSpan.FromSeconds(5) },
            cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        requests.Should().Be(1);
    }

    [TestMethod]
    public async Task DirectFallbackRequiresAnExplicitOption()
    {
        await using var server = new TestServer("HTTP/1.1 200 OK\r\nContent-Length: 6\r\n\r\ndirect");
        var pool = CreatePool([]);
        using var response = await pool.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/"),
            new ProxyPoolHttpClientOptions { AllowDirectFallback = true });

        (await response.Content.ReadAsStringAsync()).Should().Be("direct");
    }

    [TestMethod]
    public async Task DefaultTransportDoesNotFollowRedirects()
    {
        await using var server = new TestServer(
            "HTTP/1.1 302 Found\r\nLocation: http://upstream.invalid/another\r\nContent-Length: 0\r\n\r\n");
        var pool = CreatePool([Node(server.Port)]);
        using var response = await pool.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "http://upstream.invalid/"),
            new ProxyPoolHttpClientOptions { RequestTimeout = TimeSpan.FromSeconds(2) });

        response.StatusCode.Should().Be(HttpStatusCode.Found);
    }

    [TestMethod]
    public async Task SharedHandlerDoesNotShareCookiesBetweenClients()
    {
        await using var server = new TestServer(
            "HTTP/1.1 200 OK\r\nSet-Cookie: session=secret; Path=/\r\nContent-Length: 0\r\n\r\n",
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        var pool = CreatePool([Node(server.Port)]);
        using (var first = await pool.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "http://upstream.invalid/")))
            await first.Content.ReadAsByteArrayAsync();
        using (var second = await pool.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "http://upstream.invalid/")))
            await second.Content.ReadAsByteArrayAsync();

        (await server.Requests)[1].Should().NotContain("Cookie:");
    }

    [TestMethod]
    public async Task ResponseOwnsRequestUntilTheBodyIsConsumedAndDisposed()
    {
        await using var server = new TestServer(
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 4\r\n\r\nbody");
        var pool = CreatePool([Node(server.Port)]);
        var content = new TrackedContent();
        var response = await pool.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "http://upstream.invalid/")
        {
            Content = content
        });
        content.DisposeCount.Should().Be(0);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        await using (var stream = await response.Content.ReadAsStreamAsync())
        {
            var bytes = new byte[4];
            await stream.ReadExactlyAsync(bytes);
            Encoding.UTF8.GetString(bytes).Should().Be("body");
        }
        response.Dispose();
        response.Dispose();
        content.DisposeCount.Should().Be(1);
    }

    private static ProxyEndpoint Node(int port, string subscription = "enabled", ResourceState state = ResourceState.Active)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            SubscriptionId = subscription,
            Host = "127.0.0.1",
            Port = port,
            Status = new ResourceStatus { State = state }
        };

    private static IProxySubscriptionService Subscriptions()
    {
        var subscriptions = new Mock<IProxySubscriptionService>();
        subscriptions.Setup(store => store.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProxySubscription { Id = "enabled" }, new ProxySubscription { Id = "other" } });
        return subscriptions.Object;
    }

    private ProxyPoolHttpClientFactory CreatePool(ProxyEndpoint[] proxies)
    {
        var nodes = new Mock<IProxyStore>();
        nodes.Setup(store => store.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(proxies);
        return new ProxyPoolHttpClientFactory(nodes.Object, Subscriptions(), _transport, _resilience);
    }

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class TrackedContent : HttpContent
    {
        public int DisposeCount { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
        protected override void Dispose(bool disposing) { if (disposing) DisposeCount++; base.Dispose(disposing); }
    }

    private sealed class TestServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(10));
        public int Port { get; }
        public Task<string[]> Requests { get; }

        public TestServer(params string[] responses)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Requests = ServeAsync(responses);
        }

        private async Task<string[]> ServeAsync(string[] responses)
        {
            var requests = new List<string>();
            foreach (var response in responses)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var headers = new StringBuilder();
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line) headers.AppendLine(line);
                requests.Add(headers.ToString());
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
            }
            return requests.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await Requests; }
            catch (OperationCanceledException) { }
            finally { _stop.Dispose(); }
        }
    }
}
