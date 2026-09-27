using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.ResponseCompression;
using Router.Contracts.Host;
using Router.Host.Api;
using Router.Host.Configuration;
using Router.Host.Plugins;
using Router.Host.Pipeline;
using Router.Host.Security;
using Router.Host.Services;
using Router.Host.Tracing;
using Router.Host.Web;
using Router.Infrastructure;
using Router.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Configuration.AddEnvironmentVariables("ROUTER2API_");
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddJsonFile(
    Path.Combine(Directory.GetCurrentDirectory(), "Config", "Config.json"),
    optional: true,
    reloadOnChange: true);

var forwardedHeadersConfiguration = builder.Configuration.GetSection("ForwardedHeaders");
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                            | ForwardedHeaders.XForwardedProto
                            | ForwardedHeaders.XForwardedHost;
    // 关键：信任所有代理（Cloudflare 出口 IP 不固定，或者你可以用 Cloudflare IP 段）
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection("Auth"));
builder.Services.AddOptions<LogRetentionOptions>()
    .Bind(builder.Configuration.GetSection("Logging"));
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<AdminAuthService>();
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddSingleton<IConfigFileService, ConfigFileService>();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
    options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
    options.Level = CompressionLevel.Fastest);
builder.Services.AddRouterInfrastructure(builder.Configuration);
builder.Services.AddSingleton<RouterPipeline>();
builder.Services.AddSingleton<PluginCatalog>();
builder.Services.AddSingleton<IPluginCatalog>(sp => sp.GetRequiredService<PluginCatalog>());
builder.Services.AddSingleton<PluginReleaseService>();
builder.Services.AddSingleton<PluginTaskRunner>();
builder.Services.AddSingleton<IPluginTaskInvoker>(sp => sp.GetRequiredService<PluginTaskRunner>());
builder.Services.AddHostedService<PluginScheduledTaskHostedService>();
builder.Services.AddHostedService<LogCleanupHostedService>();
builder.Services.AddHostedService<ModelMetadataWarmupHostedService>();
builder.Services.AddSingleton<ProxySubscriptionRefreshHostedService>();
builder.Services.AddSingleton<IProxySubscriptionRefreshQueue>(sp => sp.GetRequiredService<ProxySubscriptionRefreshHostedService>());
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ProxySubscriptionRefreshHostedService>());
builder.Services.AddCors(options =>
{
    options.AddPolicy("v1", policy => policy
        .AllowAnyOrigin()  // /v1走Bearer Key，不走Cookie，所以可以用*，不用AllowCredentials
        .AllowAnyMethod()  // 必须，覆盖POST/GET/OPTIONS
        .AllowAnyHeader()  // 必须，覆盖Authorization、Content-Type
    );
});
var app = builder.Build();

app.Services.GetRequiredService<IConfigFileService>().EnsureCreated();
app.Services.GetRequiredService<AdminAuthService>().MigratePlaintextPasswords();
app.Services.GetRequiredService<DatabaseInitializer>().Initialize();

var catalog = app.Services.GetRequiredService<PluginCatalog>();
await catalog.ReloadAsync(null);

app.UseForwardedHeaders();
app.UseResponseCompression();
app.UseMiddleware<Router.Host.Web.NormalizeDuplicateContentTypeMiddleware>();
app.UseCors("v1");
app.UseMiddleware<TraceIdMiddleware>();
app.UseMiddleware<Router.Host.Security.DownstreamApiKeyMiddleware>();
app.UseMiddleware<Router.Host.Security.AdminSessionMiddleware>();

var webRoot = app.Environment.WebRootPath;
var hasFrontend = !string.IsNullOrWhiteSpace(webRoot)
    && File.Exists(Path.Combine(webRoot, "index.html"));
if (hasFrontend)
{
    app.UseDefaultFiles();
    app.UseMiddleware<PrecompressedStaticFileMiddleware>();
    app.UseStaticFiles();
}

ApiEndpoints.Map(app);
PluginReleaseEndpoints.Map(app);
catalog.MapEndpoints(app);

if (hasFrontend)
{
    app.MapFallback(async context =>
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(webRoot!, "index.html"));
    });
}

app.Run();

public partial class Program;
