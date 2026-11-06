using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class ProxyProbeConcurrencyTests
{
    [TestMethod]
    public async Task ProbeForOldConfigurationDoesNotOverwriteNewConnectionOrProbeState()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proxy = await fixture.AddProxyAsync("probe-update");
        var updated = new ProxyEndpoint
        {
            Id = proxy.Id,
            SubscriptionId = proxy.SubscriptionId,
            Host = "new.proxy.test",
            Port = proxy.Port,
            Scheme = proxy.Scheme,
            Username = proxy.Username,
            Password = proxy.Password,
            ConfigurationVersion = proxy.ConfigurationVersion + 1,
            ProbeStatus = "Pending",
            ProbeConfigurationVersion = proxy.ConfigurationVersion + 1
        };
        await fixture.Store.UpdateSubscriptionConnectionAsync(updated, proxy.ConfigurationVersion);
        using var http = new CallbackProxyHttpFactory(_ => Task.CompletedTask);
        var probe = fixture.CreateProbe(http);

        var result = await probe.ProbeAsync(proxy);

        result.Status.Should().Be("Superseded");
        var current = await fixture.Store.GetAsync(proxy.Id);
        current.Should().NotBeNull();
        current!.Host.Should().Be("new.proxy.test");
        current.ConfigurationVersion.Should().Be(2);
        current.ProbeStatus.Should().Be("Pending");
        current.LastProbeAt.Should().BeNull();
    }

    [TestMethod]
    public async Task ProbeForDeletedProxyDoesNotReinsertIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proxy = await fixture.AddProxyAsync("probe-delete");
        using var http = new CallbackProxyHttpFactory(token => fixture.Store.RemoveAsync(proxy.Id, token));
        var probe = fixture.CreateProbe(http);

        var result = await probe.ProbeAsync(proxy);

        result.Status.Should().Be("Superseded");
        (await fixture.Store.GetAsync(proxy.Id)).Should().BeNull();
    }

    private sealed class Fixture(SqlSugarDatabase database, string path)
    {
        public ProxyStore Store { get; } = new(database);

        public ProxyProbeService CreateProbe(IProxyHttpClientFactory http)
        {
            var state = new Mock<ISharedKeyValueStore>();
            state.SetupGet(item => item.IsConfigured).Returns(true);
            state.Setup(item => item.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            state.Setup(item => item.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            return new ProxyProbeService(http, Store, state.Object, NullLogger<ProxyProbeService>.Instance);
        }

        public async Task<ProxyEndpoint> AddProxyAsync(string id)
        {
            var proxy = new ProxyEndpoint
            {
                Id = id,
                SubscriptionId = "probe-subscription",
                Host = "old.proxy.test",
                Port = 8080,
                ProbeStatus = "Pending",
                ProbeConfigurationVersion = 1
            };
            await Store.SaveAsync(proxy);
            return proxy;
        }

        public static Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "test-data");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"router2api-{Guid.NewGuid():N}.db");
            var database = new SqlSugarDatabase(
                Options.Create(new DatabaseOptions { Path = path }),
                NullLogger<SqlSugarDatabase>.Instance);
            LogDatabaseTestHelper.Initializer(database).Initialize();
            return Task.FromResult(new Fixture(database, path));
        }

        public ValueTask DisposeAsync()
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CallbackProxyHttpFactory(Func<CancellationToken, Task> beforeResponse) : IProxyHttpClientFactory
    {
        public HttpClient CreateDirectClient(string clientName = "direct") => throw new NotSupportedException();
        public HttpClient CreateClient(ProxyEndpoint proxy, string clientName = "proxy")
            => new(new CallbackHandler(beforeResponse));
        public void Dispose() { }
    }

    private sealed class CallbackHandler(Func<CancellationToken, Task> beforeResponse) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await beforeResponse(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[65536]) };
        }
    }
}
