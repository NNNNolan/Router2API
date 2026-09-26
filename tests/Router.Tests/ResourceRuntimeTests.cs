using FluentAssertions;
using Router.Contracts.Domain;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class ResourceRuntimeTests
{
    [TestMethod]
    public async Task LeaseIsReleasedExactlyOnce()
    {
        var manager = new ResourceLeaseManager();
        var account = new Account { Id = "account-1", PluginKey = "myai", Platform = "myai" };

        var result = await manager.AcquireAsync(
            [account],
            _ => true,
            TimeSpan.FromMilliseconds(100));

        result.Should().NotBeNull();
        account.Status.InFlight.Should().Be(1);

        await result!.Value.Lease.DisposeAsync();
        await result.Value.Lease.DisposeAsync();

        account.Status.InFlight.Should().Be(0);
    }

    [TestMethod]
    public async Task ConcurrentLeasesKeepTheInflightCounterBalanced()
    {
        var manager = new ResourceLeaseManager();
        var account = new Account { Id = "account-100", PluginKey = "myai", Platform = "myai" };
        var leases = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => manager.AcquireAsync(
            [account],
            _ => true,
            TimeSpan.FromSeconds(1))));

        leases.Should().AllSatisfy(item => item.Should().NotBeNull());
        account.Status.InFlight.Should().Be(100);

        foreach (var lease in leases)
            await lease!.Value.Lease.DisposeAsync();

        account.Status.InFlight.Should().Be(0);
    }

    [TestMethod]
    public void ResourceAvailabilityHonorsGlobalStateAndCooldown()
    {
        var proxy = new ProxyEndpoint
        {
            Id = "proxy-1",
            Host = "127.0.0.1",
            Port = 8080
        };
        var now = DateTimeOffset.UtcNow;

        ResourceAvailability.IsAvailable(proxy, now).Should().BeTrue();
        proxy.Status.CooldownUntil = now.AddMinutes(1);
        ResourceAvailability.IsAvailable(proxy, now).Should().BeFalse();
        ResourceAvailability.IsAvailable(proxy, now.AddMinutes(2)).Should().BeTrue();

        proxy.Status.State = ResourceState.Disabled;
        ResourceAvailability.IsAvailable(proxy, now.AddMinutes(2)).Should().BeFalse();
    }

    [TestMethod]
    public async Task ExclusiveActionsDoNotOverlapAndPendingStateClears()
    {
        var manager = new ResourceLeaseManager();
        var current = 0;
        var maximum = 0;
        var maximumLock = new object();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => manager.WithExclusiveAsync(
            "proxy-exclusive",
            async cancellationToken =>
            {
                var active = Interlocked.Increment(ref current);
                lock (maximumLock)
                {
                    if (active > maximum)
                        maximum = active;
                }
                await Task.Delay(20, cancellationToken);
                Interlocked.Decrement(ref current);
                return true;
            })));

        maximum.Should().Be(1);

        var proxy = new ProxyEndpoint { Id = "proxy-exclusive", Host = "localhost", Port = 8080 };
        var lease = await manager.AcquireAsync([proxy], _ => true, TimeSpan.FromMilliseconds(100));
        lease.Should().NotBeNull();
        await lease!.Value.Lease.DisposeAsync();
    }

    [TestMethod]
    public void CompoundHttpClientKeySeparatesProxiesAndVersions()
    {
        var first = new ProxyClientKey("upstream", "proxy-1", 1, ProxyScheme.Http);
        var same = new ProxyClientKey("upstream", "proxy-1", 1, ProxyScheme.Http);
        var otherProxy = new ProxyClientKey("upstream", "proxy-2", 1, ProxyScheme.Http);
        var changedVersion = new ProxyClientKey("upstream", "proxy-1", 2, ProxyScheme.Http);
        var changedTransport = new ProxyClientKey("upstream", "proxy-1", 1, ProxyScheme.Socks5);

        same.Should().Be(first);
        otherProxy.Should().NotBe(first);
        changedVersion.Should().NotBe(first);
        changedTransport.Should().NotBe(first);
    }
}
