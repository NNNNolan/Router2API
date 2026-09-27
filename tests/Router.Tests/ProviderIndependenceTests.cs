using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Host.Api;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

/// <summary>防止宿主重新引入某个提供方的默认值或权限例外。</summary>
[TestClass]
public sealed class ProviderIndependenceTests
{
    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"pluginKey\":\"missing\"}")]
    [DataRow("{\"pluginKey\":123}")]
    public async Task ProxyTestRequiresAnExplicitInstalledPlugin(string json)
    {
        var proxies = new Mock<IProxyStore>(MockBehavior.Strict);
        var result = await ApiEndpoints.TestProxyAsync("proxy", proxies.Object, Mock.Of<IProxyPolicyStore>(),
            new PlatformRegistry(), JsonDocument.Parse(json).RootElement, CancellationToken.None);
        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(400);
        proxies.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ProxyTestUsesRegisteredIdentityInsteadOfAnyBuiltInProvider()
    {
        var registry = new PlatformRegistry();
        registry.Register(new PlatformRegistration("custom-platform", "custom-package", "Custom", Mock.Of<IPlatformTerminal>()));
        var proxy = new ProxyEndpoint { Id = "proxy", Host = "proxy.example", Port = 8080 };
        var proxies = new Mock<IProxyStore>();
        proxies.Setup(value => value.GetAsync("proxy", It.IsAny<CancellationToken>())).ReturnsAsync(proxy);
        var policy = new Mock<IProxyPolicyStore>();
        var result = await ApiEndpoints.TestProxyAsync("proxy", proxies.Object, policy.Object, registry,
            JsonSerializer.SerializeToElement(new { pluginKey = " CUSTOM-PACKAGE " }), CancellationToken.None);
        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(200);
        policy.Verify(value => value.GetAsync("custom-package", proxy, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("opencode")]
    [DataRow("unrelated-provider")]
    public async Task EveryPluginGetsTheSameLogRedactionAndLengthLimit(string pluginKey)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N") + ".db");
        var database = new SqlSugarDatabase(Options.Create(new DatabaseOptions { Path = path }), NullLogger<SqlSugarDatabase>.Instance);
        try
        {
            new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance).Initialize();
            var options = new Mock<IOptionsMonitor<PluginLogOptions>>();
            options.SetupGet(value => value.CurrentValue).Returns(new PluginLogOptions());
            var store = new PluginLogStore(database, options.Object);
            await store.WriteAsync(new PluginLog
            {
                PluginKey = pluginKey, EventType = "upstream.forbidden", StatusCode = 403,
                Message = "test", DetailsJson = "Bearer unit-secret " + new string('x', 20000)
            });
            using var db = database.CreateClient();
            var saved = db.Queryable<PluginLogEntity>().Single();
            saved.DetailsJson.Should().NotContain("unit-secret");
            saved.DetailsJson!.Length.Should().BeLessThanOrEqualTo(16384);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { /* SQLite 连接池可能延后释放。 */ }
        }
    }
}
