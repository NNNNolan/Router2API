using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Router.Contracts.Host;
using Router.Host.Configuration;
using Router.Host.Security;
using Router.Infrastructure.Persistence;

namespace Router.Tests;

[TestClass]
public sealed class AppearanceConfigurationTests
{
    [TestMethod]
    public async Task ThemePersistsAcrossReloadAndUnrelatedUpdates()
    {
        var path = TestPath();
        using (var services = CreateServices(path))
        {
            var config = services.GetRequiredService<ConfigFileService>();
            config.Get().Appearance.Theme.Should().Be("minimal");
            config.EnsureCreated();
            var saved = await config.SaveAsync(new() { Appearance = new() { Theme = "default" } });
            saved.Appearance.Theme.Should().Be("default");
            (await config.SaveAsync(new() { Database = new() { Path = "test.db" } })).Appearance.Theme.Should().Be("default");
        }
        using var restarted = CreateServices(path);
        var reloaded = restarted.GetRequiredService<ConfigFileService>();
        reloaded.Get().Appearance.Theme.Should().Be("default");
        JsonNode.Parse(File.ReadAllText(path))!["Appearance"]!["Theme"]!.GetValue<string>().Should().Be("default");
        (await reloaded.SaveAsync(new() { Appearance = new() { Theme = "minimal" } })).Appearance.Theme.Should().Be("minimal");
    }

    [TestMethod]
    [DataRow("dark")]
    [DataRow("")]
    [DataRow(null)]
    public async Task UnsupportedThemeDoesNotModifyConfiguration(string? theme)
    {
        var path = TestPath();
        using var services = CreateServices(path);
        var config = services.GetRequiredService<ConfigFileService>();
        config.EnsureCreated();
        var original = File.ReadAllText(path);
        Func<Task> save = () => config.SaveAsync(new() { Appearance = new() { Theme = theme } });
        await save.Should().ThrowAsync<ArgumentException>();
        File.ReadAllText(path).Should().Be(original);
        config.Get().Appearance.Theme.Should().Be("minimal");
    }

    private static string TestPath() => Path.Combine(AppContext.BaseDirectory, "test-data", $"appearance-{Guid.NewGuid():N}.json");

    private static ServiceProvider CreateServices(string path)
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: true, reloadOnChange: false).Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<AuthOptions>(configuration.GetSection("Auth"));
        services.Configure<DatabaseOptions>(configuration.GetSection("Database"));
        services.Configure<RedisOptions>(configuration.GetSection("Redis"));
        services.Configure<LogRetentionOptions>(configuration.GetSection("Logging"));
        services.Configure<PluginLogOptions>(configuration.GetSection("Logging"));
        services.Configure<AppearanceOptions>(configuration.GetSection("Appearance"));
        services.AddSingleton<ApiKeyService>();
        services.AddSingleton(provider => new ConfigFileService(configuration,
            provider.GetRequiredService<IOptionsMonitor<AuthOptions>>(),
            provider.GetRequiredService<IOptionsMonitor<DatabaseOptions>>(),
            provider.GetRequiredService<IOptionsMonitor<RedisOptions>>(),
            provider.GetRequiredService<IOptionsMonitor<LogRetentionOptions>>(),
            provider.GetRequiredService<IOptionsMonitor<PluginLogOptions>>(),
            provider.GetRequiredService<ApiKeyService>(),
            provider.GetRequiredService<ILogger<ConfigFileService>>(),
            provider.GetRequiredService<IOptionsMonitor<AppearanceOptions>>()) { FilePath = path });
        return services.BuildServiceProvider();
    }
}
