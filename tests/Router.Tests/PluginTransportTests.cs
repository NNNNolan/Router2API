using FluentAssertions;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class PluginTransportTests
{
    [TestMethod]
    public async Task LegacyAndPoolClientsShareTheSamePhysicalTransport()
    {
        using var transport = new ProxyTransportFactory();
        using var resilience = new PluginResiliencePipelines();
        var proxy = Node(1);
        var proxies = new Mock<IProxyStore>();
        proxies.Setup(value => value.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { proxy });
        var subscriptions = new Mock<IProxySubscriptionService>();
        subscriptions.Setup(value => value.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new ProxySubscription { Id = "sub" } });
        var legacy = new ProxyHttpClientFactory(transport);
        var pool = new ProxyPoolHttpClientFactory(proxies.Object, subscriptions.Object, transport, resilience);
        using var first = legacy.CreateClient(proxy, "old-name");
        using var second = legacy.CreateClient(proxy, "different-name");
        using var third = await pool.CreateClientAsync();
        transport.CachedHandlerCount.Should().Be(1, "client names are not connection profiles");
        legacy.Dispose();
        using var fourth = await pool.CreateClientAsync();
        transport.CachedHandlerCount.Should().Be(1, "disposing a legacy adapter cannot close a host singleton");
    }

    [TestMethod]
    public void OldConfigurationsAreEvictedOnlyAfterTheirClientLeasesEnd()
    {
        using var transport = new ProxyTransportFactory();
        var old = transport.CreateClient(Node(1));
        using var current = transport.CreateClient(Node(2));
        transport.CachedHandlerCount.Should().Be(2);
        old.Dispose();
        using var another = transport.CreateClient(Node(2));
        transport.CachedHandlerCount.Should().Be(1);
    }

    [TestMethod]
    public void RedirectOptionsAndConnectionSettingsAreExplicitCacheKeys()
    {
        using var transport = new ProxyTransportFactory();
        using var first = transport.CreateClient(Node(1));
        using var redirect = transport.CreateClient(Node(1), new PluginHttpClientOptions { AllowAutoRedirect = true });
        using var changedHostWithoutVersionBump = transport.CreateClient(new ProxyEndpoint
        {
            Id = "node", SubscriptionId = "sub", ConfigurationVersion = 1, Host = "other.local", Port = 8080
        });
        transport.CachedHandlerCount.Should().Be(3);
        using var direct = transport.CreateClient(null);
        transport.CachedHandlerCount.Should().Be(4, "an explicit direct route is distinct from every node");
    }

    [TestMethod]
    public async Task SharedTransportProfileDoesNotLeakRetryLimitsBetweenParallelSends()
    {
        using var transport = new ProxyTransportFactory();
        using var resilience = new PluginResiliencePipelines();
        // Port 0 is not selected, so explicitly direct requests use a closed loopback port instead.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var nodes = new Mock<IProxyStore>();
        nodes.Setup(value => value.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ProxyEndpoint>());
        var subscriptions = new Mock<IProxySubscriptionService>();
        subscriptions.Setup(value => value.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ProxySubscription>());
        var pool = new ProxyPoolHttpClientFactory(nodes.Object, subscriptions.Object, transport, resilience);
        async Task<int> SendAsync(int retries)
        {
            var sent = 0;
            try
            {
                using var response = await pool.SendAsync(() =>
                {
                    sent++;
                    return new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/");
                }, new ProxyPoolHttpClientOptions { AllowDirectFallback = true },
                    new ProxyPoolRetryOptions { MaxRetries = retries, Delay = TimeSpan.Zero });
            }
            catch (HttpRequestException) { }
            return sent;
        }
        var counts = await Task.WhenAll(SendAsync(1), SendAsync(3));
        counts.Should().Equal(2, 4);
    }

    private static ProxyEndpoint Node(long version) => new()
    {
        Id = "node", SubscriptionId = "sub", ConfigurationVersion = version, Host = "localhost", Port = 8080
    };
}
