using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Host.Plugins.JavaScript;
using Router.Infrastructure;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class PluginServicesTests
{
    [TestMethod]
    [DataRow("00:02:00", true)]
    [DataRow("00:00:00", false)]
    public void InfrastructureRegistrationBindsAndValidatesExecutionOptions(string timeout, bool valid)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Plugins:Execution:ResponseTimeout"] = timeout,
            ["Redis:ConnectionString"] = "disabled"
        }).Build();
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddRouterInfrastructure(configuration);
        using var provider = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var options = provider.GetRequiredService<IOptions<PluginExecutionOptions>>();
        if (valid)
        {
            options.Value.ResponseTimeout.Should().Be(TimeSpan.FromMinutes(2));
            provider.GetRequiredService<PluginResiliencePipelines>().Should().NotBeNull();
            provider.GetRequiredService<ProxyTransportFactory>().Should().NotBeNull();
        }
        else
        {
            Action read = () => _ = options.Value;
            read.Should().Throw<OptionsValidationException>();
        }
    }

    [TestMethod]
    public async Task AccountLogModelAndTaskCapabilitiesCannotChangeTheirPluginScope()
    {
        using var fixture = new HostFixture();
        var services = fixture.Create("alpha").Services;
        Func<Task> save = () => services.Accounts.SaveAsync(new Account { PluginKey = "beta", Platform = "beta" });
        await save.Should().ThrowAsync<UnauthorizedAccessException>();
        Func<Task> log = () => services.Log.WriteAsync(new PluginLog { PluginKey = "beta" });
        await log.Should().ThrowAsync<UnauthorizedAccessException>();
        Func<Task> model = () => services.Models.ListAsync("beta");
        await model.Should().ThrowAsync<UnauthorizedAccessException>();
        Action invalidateAll = () => services.Models.Invalidate(null!);
        invalidateAll.Should().Throw<UnauthorizedAccessException>();
        fixture.Models.Verify(value => value.Invalidate(It.IsAny<string>()), Times.Never);
        Func<Task> task = () => services.Tasks.RunAsync("check", "beta");
        await task.Should().ThrowAsync<UnauthorizedAccessException>();

        await services.Accounts.GetAsync("possibly-foreign-id");
        await services.Tasks.RunAsync("check", "alpha");
        fixture.Accounts.Verify(value => value.GetAsync("alpha", "possibly-foreign-id", It.IsAny<CancellationToken>()), Times.Once);
        fixture.Accounts.VerifyNoOtherCalls();
        fixture.Tasks.Verify(value => value.RunAsync("alpha", "check", "alpha", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RefreshCannotMutateIdentityInPlace()
    {
        using var fixture = new HostFixture();
        fixture.Accounts.Setup(value => value.RefreshAsync("alpha", "a",
                It.IsAny<Func<Account, CancellationToken, Task<Account>>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, Func<Account, CancellationToken, Task<Account>> update, CancellationToken token) =>
                update(new Account { Id = "a", PluginKey = "alpha", Platform = "alpha" }, token));
        var services = fixture.Create("alpha").Services;
        Func<Task> refresh = () => services.Accounts.RefreshAsync("a", (account, _) =>
        {
            account.Platform = "beta";
            return Task.FromResult(account);
        });
        await refresh.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task SharedStateCannotReachHostKeysAndLocalStateIsGenerationScoped()
    {
        using var fixture = new HostFixture();
        var first = fixture.Create("alpha").Services;
        var nextGeneration = fixture.Create("alpha").Services;
        var otherPlugin = fixture.Create("beta").Services;
        await first.State.Local.SetStringAsync("same", "local", TimeSpan.FromMinutes(1));
        (await nextGeneration.State.Local.GetStringAsync("same")).Should().BeNull();
        (await otherPlugin.State.Local.GetStringAsync("same")).Should().BeNull();
        await first.State.Shared.SetStringAsync("account:cooldown:beta:a", "value", TimeSpan.FromMinutes(1));
        fixture.State.Verify(value => value.SetStringAsync(
            "plugin-state:alpha:account:cooldown:beta:a", "value", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        await otherPlugin.State.Shared.RemoveAsync("account:cooldown:beta:a");
        fixture.State.Verify(value => value.RemoveAsync("plugin-state:beta:account:cooldown:beta:a", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task LocalStateEnforcesEntryValueAndTtlBudgets()
    {
        var state = new PluginMemoryState();
        for (var index = 0; index < 256; index++)
            await state.SetStringAsync(index.ToString(System.Globalization.CultureInfo.InvariantCulture), "x", TimeSpan.FromMinutes(1));
        Func<Task> overCount = () => state.SetStringAsync("extra", "x", TimeSpan.FromMinutes(1));
        await overCount.Should().ThrowAsync<InvalidOperationException>();
        await state.SetStringAsync("0", "updated", TimeSpan.FromMinutes(1));
        Func<Task> overBytes = () => state.SetStringAsync("0", new string('中', 32 * 1024), TimeSpan.FromMinutes(1));
        await overBytes.Should().ThrowAsync<ArgumentException>();
        Func<Task> overTtl = () => state.SetStringAsync("0", "x", TimeSpan.FromDays(2));
        await overTtl.Should().ThrowAsync<ArgumentException>();
        await state.RemoveAsync("0");
        await state.SetStringAsync("extra", "x", TimeSpan.FromMilliseconds(30));
        await Task.Delay(60);
        (await state.GetStringAsync("extra")).Should().BeNull();
    }

    [TestMethod]
    public async Task JsAndCSharpUseTheSameStateModelAndTaskCapabilities()
    {
        using var fixture = new HostFixture();
        var host = fixture.Create("alpha");
        await host.Services.State.Local.SetStringAsync("counter", "7", TimeSpan.FromMinutes(1));
        fixture.Models.Setup(value => value.ListAsync("alpha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ModelDescriptor("test", "Test") });
        fixture.Tasks.Setup(value => value.RunAsync("alpha", "check", "alpha", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var manifest = new JsPluginManifest
        {
            SchemaVersion = 1, Id = "alpha", Name = "alpha", Version = "1", Runtime = "jint", HostApi = "1-preview",
            Entry = "plugin.mjs", Format = "esm-bundle", Platform = new JsPlatformManifest { Name = "alpha" },
            Hooks = new JsHooksManifest { Invoke = "invoke" },
            Endpoints = [new JsEndpointManifest { Path = "status", Handler = "status" }],
            Tasks = [new JsTaskManifest { Name = "check", Handler = "check", Cron = "0 0 * * * *" }],
            Permissions = new JsPermissions { State = ["read", "write"], Models = ["read"], Tasks = ["run"] }
        };
        manifest.Validate("alpha");
        using var terminal = new JsPlatformTerminal(new JsPluginPackage(manifest, """
            export function invoke() {}
            export function check() {}
            export async function status(ctx) {
              const next=(await ctx.state.get("counter"))+1;
              await ctx.state.set("counter",next);
              return ctx.json(200,{next,models:await ctx.models.list(),ran:await ctx.tasks.run("check")});
            }
            """, null, "{}"), host);
        await terminal.ValidateAsync(CancellationToken.None);
        var result = await terminal.InvokeEndpointAsync("status", new PluginHttpContext());
        result.StatusCode.Should().Be(200);
        var json = JsonSerializer.SerializeToNode(result.Body)!;
        json["next"]!.GetValue<int>().Should().Be(8);
        json["ran"]!.GetValue<bool>().Should().BeTrue();
        (await host.Services.State.Local.GetStringAsync("counter")).Should().Be("8");
        fixture.Tasks.Verify(value => value.RunAsync("alpha", "check", "alpha", It.IsAny<CancellationToken>()), Times.Once);
        fixture.Provider.Verify(value => value.GetService(It.IsAny<Type>()), Times.Once, "only the host's deferred task binding is resolved");
    }

    [TestMethod]
    public async Task LegacyServiceLocatorStillWorksForExistingDlls()
    {
        using var fixture = new HostFixture();
        var host = fixture.Create("alpha");
        (await host.GetServiceAsync<ISharedKeyValueStore>()).Should().BeSameAs(fixture.State.Object);
    }

    private sealed class HostFixture : IDisposable
    {
        private readonly ProxyTransportFactory _transport = new();
        public Mock<IAccountService> Accounts { get; } = new();
        public Mock<ISharedKeyValueStore> State { get; } = new();
        public Mock<IModelCatalog> Models { get; } = new();
        public Mock<IPluginTaskInvoker> Tasks { get; } = new();
        public Mock<IServiceProvider> Provider { get; } = new();
        public IPluginHost Create(string key)
        {
            Provider.Setup(value => value.GetService(typeof(IPluginTaskInvoker))).Returns(Tasks.Object);
            Provider.Setup(value => value.GetService(typeof(ISharedKeyValueStore))).Returns(State.Object);
            return new PluginHostFactory(Models.Object, Accounts.Object, Mock.Of<ILogSink<ResourceEvent>>(),
                Mock.Of<IPluginLogSink>(), Mock.Of<IModelMetadataCatalog>(), _transport,
                Mock.Of<IProxyPoolHttpClientFactory>(), State.Object, Mock.Of<ITaskLogStore>(),
                Options.Create(new PluginExecutionOptions()), Provider.Object).Create(key, [key]);
        }
        public void Dispose() => _transport.Dispose();
    }
}
