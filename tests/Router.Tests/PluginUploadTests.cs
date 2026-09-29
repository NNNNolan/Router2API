using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Plugins.UploadFixture;
using Router.Contracts.Host;
using Router.Host.Api;
using Router.Host.Plugins;
using Router.Host.Security;
using Router.Host.Configuration;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class PluginUploadTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("different-zip-folder/")]
    public async Task UploadsBothJsLayoutsAndReplacesOnlyTheSamePlugin(string prefix)
    {
        await using var fixture = new Fixture();
        using var first = JsZip("upload-js", "1.0", prefix, extra: true);
        var descriptor = await fixture.Service.UploadAsync(first, CancellationToken.None);
        descriptor.PluginKey.Should().Be("upload-js");
        descriptor.State.Should().Be("Active");
        descriptor.Runtime.Should().Be("jint");
        using var other = JsZip("other-js", "1.0", "");
        await fixture.Service.UploadAsync(other, CancellationToken.None);
        var otherSnapshot = fixture.Catalog.Get("other-js")!.DirectoryPath;
        var snapshot = descriptor.DirectoryPath;
        using var second = JsZip("upload-js", "2.0", prefix);
        var replacement = await fixture.Service.UploadAsync(second, CancellationToken.None);
        replacement.Version.Should().Be("2.0");
        replacement.DirectoryPath.Should().NotBe(snapshot);
        fixture.Catalog.Get("other-js")!.DirectoryPath.Should().Be(otherSnapshot);
        File.Exists(Path.Combine(fixture.Root, "upload-js", "obsolete.txt")).Should().BeFalse();
        Directory.EnumerateDirectories(Path.Combine(fixture.Root, ".subscription", ".uploads")).Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidReplacementRestoresFilesAndStateThenValidUploadActivates(bool disabled)
    {
        await using var fixture = new Fixture();
        using var first = JsZip("upload-js", "1.0", "");
        await fixture.Service.UploadAsync(first, CancellationToken.None);
        if (disabled) await fixture.Catalog.SetEnabledAsync("upload-js", false, CancellationToken.None);
        var snapshot = fixture.Catalog.Get("upload-js")!.DirectoryPath;
        using var broken = JsZip("upload-js", "2.0", "", broken: true);
        Func<Task> upload = () => fixture.Service.UploadAsync(broken, CancellationToken.None);
        await upload.Should().ThrowAsync<InvalidOperationException>();
        fixture.Catalog.Get("upload-js")!.State.Should().Be(disabled ? "Disabled" : "Active");
        fixture.Catalog.Get("upload-js")!.DirectoryPath.Should().Be(snapshot);
        (await File.ReadAllTextAsync(Path.Combine(fixture.Root, "upload-js", "plugin.json"))).Should().Contain("1.0");
        using var valid = JsZip("upload-js", "3.0", "");
        (await fixture.Service.UploadAsync(valid, CancellationToken.None)).State.Should().Be("Active");
        File.Exists(Path.Combine(fixture.Root, ".disabled", "upload-js.disabled")).Should().BeFalse();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-the-plugin-id/")]
    public async Task UploadsCSharpWithoutManifestOrDepsAndWithPrivateDlls(string prefix)
    {
        await using var fixture = new Fixture();
        var dll = await File.ReadAllBytesAsync(typeof(UploadFixtureTerminal).Assembly.Location);
        var files = new Dictionary<string, byte[]>
        {
            [prefix + "Plugins.UploadFixture.dll"] = dll,
            [prefix + "native-dependency.dll"] = [0, 1, 2]
        };
        if (prefix.Length > 0)
            files[prefix + "plugin.json"] = Encoding.UTF8.GetBytes("""{"id":"upload-fixture","runtime":"dotnet","description":"with manifest"}""");
        using var zip = Zip(files);
        var result = await fixture.Service.UploadAsync(zip, CancellationToken.None);
        result.PluginKey.Should().Be("upload-fixture");
        result.State.Should().Be("Active");
        result.Runtime.Should().Be("dotnet");
        var manifest = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "upload-fixture", "plugin.json"));
        manifest.Should().Contain("\"assembly\":\"Plugins.UploadFixture.dll\"");
        // 同包重传必须替换并重载，而不是保留旧运行时。
        zip.Position = 0;
        var replaced = await fixture.Service.UploadAsync(zip, CancellationToken.None);
        replaced.DirectoryPath.Should().NotBe(result.DirectoryPath);
    }

    [TestMethod]
    [DataRow("../outside.txt")]
    [DataRow("/absolute.txt")]
    [DataRow("folder/../../outside.txt")]
    [DataRow("C:/outside.txt")]
    [DataRow("folder\\file.txt")]
    [DataRow("file.txt:stream")]
    [DataRow("folder./file.txt")]
    [DataRow("CON.txt")]
    [DataRow("PLUGIN.JSON")]
    public async Task RejectsUnsafeAndCaseCollidingPaths(string path)
    {
        await using var fixture = new Fixture();
        using var zip = Zip(new Dictionary<string, byte[]>
        {
            ["plugin.json"] = Encoding.UTF8.GetBytes("{}"),
            [path] = [1]
        });
        Func<Task> upload = () => fixture.Service.UploadAsync(zip, CancellationToken.None);
        await upload.Should().ThrowAsync<InvalidDataException>();
        fixture.Catalog.All.Should().BeEmpty();
        File.Exists(Path.Combine(fixture.Root, "outside.txt")).Should().BeFalse();
    }

    [TestMethod]
    public async Task RejectsLinksTooManyEntriesAndMultiplePackages()
    {
        await using var fixture = new Fixture();
        using var linked = new MemoryStream();
        using (var zip = new ZipArchive(linked, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("plugin.json");
            entry.ExternalAttributes = 0xA000 << 16;
        }
        linked.Position = 0;
        Func<Task> upload = () => fixture.Service.UploadAsync(linked, CancellationToken.None);
        await upload.Should().ThrowAsync<InvalidDataException>();
        using var many = Zip(Enumerable.Range(0, 2001).ToDictionary(i => i == 0 ? "plugin.json" : i + ".txt", _ => Array.Empty<byte>()));
        upload = () => fixture.Service.UploadAsync(many, CancellationToken.None);
        await upload.Should().ThrowAsync<InvalidDataException>();
        using var multiple = Zip(new Dictionary<string, byte[]> { ["a/plugin.json"] = [1], ["b/plugin.json"] = [1] });
        upload = () => fixture.Service.UploadAsync(multiple, CancellationToken.None);
        await upload.Should().ThrowAsync<InvalidDataException>();
    }

    [TestMethod]
    public async Task RejectsOversizedExtractedEntryBeforeWritingItsBody()
    {
        await using var fixture = new Fixture();
        using var archive = Zip(new Dictionary<string, byte[]> { ["plugin.json"] = [1] });
        var bytes = archive.ToArray();
        // 伪造中央目录中的解压长度，无需实际分配或写出 300 MiB。
        var central = bytes.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
        central.Should().BeGreaterThan(0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24), 300 * 1024 * 1024 + 1);
        using var oversized = new MemoryStream(bytes);
        Func<Task> upload = () => fixture.Service.UploadAsync(oversized, CancellationToken.None);
        await upload.Should().ThrowAsync<InvalidDataException>();
        fixture.Catalog.All.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ReleaseArchiveExtractionStillRequiresItsDeclaredTopLevelDirectory()
    {
        await using var fixture = new Fixture();
        using var bytes = JsZip("upload-js", "1.0", "upload-js/");
        using var zip = new ZipArchive(bytes);
        await PluginReleaseService.ExtractPackageAsync(zip, Path.Combine(fixture.Root, "extracted"), "upload-js/", CancellationToken.None);
        File.Exists(Path.Combine(fixture.Root, "extracted", "plugin.json")).Should().BeTrue();
        using var flat = JsZip("upload-js", "1.0", "");
        using var wrong = new ZipArchive(flat);
        Func<Task> extract = () => PluginReleaseService.ExtractPackageAsync(wrong,
            Path.Combine(fixture.Root, "invalid"), "upload-js/", CancellationToken.None);
        await extract.Should().ThrowAsync<InvalidDataException>();
    }

    [TestMethod]
    public async Task UploadEndpointAcceptsMultipartAndValidatesFileCountAndSize()
    {
        await using var fixture = new Fixture();
        using var zip = JsZip("upload-js", "1.0", "");
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(zip.ToArray()), "file", "arbitrary-name.zip");
        var context = new DefaultHttpContext();
        context.Request.ContentType = multipart.Headers.ContentType!.ToString();
        context.Request.Body = new MemoryStream(await multipart.ReadAsByteArrayAsync());
        context.Request.ContentLength = context.Request.Body.Length;
        var result = await PluginReleaseEndpoints.UploadAsync(context.Request, fixture.Service, CancellationToken.None);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        context.Request.ContentLength = PluginReleaseService.MaxArchiveBytes + 2 * 1024 * 1024;
        result = await PluginReleaseEndpoints.UploadAsync(context.Request, fixture.Service, CancellationToken.None);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(413);
        context.Request.ContentLength = null;
        context.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
        result = await PluginReleaseEndpoints.UploadAsync(context.Request, fixture.Service, CancellationToken.None);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(400);
    }

    [TestMethod]
    public async Task UploadEndpointIsProtectedByAdminSessionAndCsrf()
    {
        var options = new Mock<IOptionsMonitor<AuthOptions>>();
        options.SetupGet(value => value.CurrentValue).Returns(new AuthOptions
        {
            Admin = new AdminOptions { Users = [new AdminUserOptions { Username = "admin", Password = "test-password" }] }
        });
        var auth = new AdminAuthService(options.Object, new PasswordHasher(), NullLogger<AdminAuthService>.Instance);
        var session = auth.Login("admin", "test-password").SessionId!;
        var called = false;
        var middleware = new AdminSessionMiddleware(_ => { called = true; return Task.CompletedTask; }, auth, options.Object);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        foreach (var expected in new[] { 401, 403, 200 })
        {
            called = false;
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Path = "/api/admin/plugins/upload";
            context.Request.Method = "POST";
            context.Response.Body = new MemoryStream();
            if (expected != 401) context.Request.Headers.Cookie = $"router_admin_session={session}; router_admin_csrf=token";
            if (expected == 200) context.Request.Headers["X-CSRF-Token"] = "token";
            await middleware.InvokeAsync(context);
            context.Response.StatusCode.Should().Be(expected);
            called.Should().Be(expected == 200);
        }
    }

    [TestMethod]
    public async Task RejectsSpoofedManifestIdAndMultipleCSharpPlugins()
    {
        await using var fixture = new Fixture();
        var dll = await File.ReadAllBytesAsync(typeof(UploadFixtureTerminal).Assembly.Location);
        using var wrongId = Zip(new Dictionary<string, byte[]>
        {
            ["plugin.json"] = Encoding.UTF8.GetBytes("""{"id":"other","runtime":"dotnet"}"""),
            ["fixture.dll"] = dll
        });
        Func<Task> upload = () => fixture.Service.UploadAsync(wrongId, CancellationToken.None);
        await upload.Should().ThrowAsync<InvalidDataException>();
        using var multiple = Zip(new Dictionary<string, byte[]> { ["one.dll"] = dll, ["two.dll"] = dll });
        upload = () => fixture.Service.UploadAsync(multiple, CancellationToken.None);
        await upload.Should().ThrowAsync<InvalidDataException>();
        fixture.Catalog.All.Should().BeEmpty();
    }

    [TestMethod]
    public async Task RejectsNestedAndMixedRuntimePackages()
    {
        await using var fixture = new Fixture();
        foreach (var nested in new[] { true, false })
        {
            using var zip = JsZip("upload-js", "1.0", "");
            using (var update = new ZipArchive(zip, ZipArchiveMode.Update, leaveOpen: true))
            {
                using var output = update.CreateEntry(nested ? "other/plugin.json" : "fixture.dll").Open();
                output.Write(nested ? Encoding.UTF8.GetBytes("{}")
                    : await File.ReadAllBytesAsync(typeof(UploadFixtureTerminal).Assembly.Location));
            }
            zip.Position = 0;
            Func<Task> upload = () => fixture.Service.UploadAsync(zip, CancellationToken.None);
            await upload.Should().ThrowAsync<InvalidDataException>();
        }
        fixture.Catalog.All.Should().BeEmpty();
    }

    [TestMethod]
    public async Task LocalReplacementClearsOnlyItsReleaseAssociation()
    {
        await using var fixture = new Fixture();
        var subscriptionRoot = Path.Combine(fixture.Root, ".subscription");
        Directory.CreateDirectory(subscriptionRoot);
        var state = Path.Combine(subscriptionRoot, "subscriptions.json");
        await File.WriteAllTextAsync(state, """
            {"repositories":[{"owner":"test","repo":"repo"}],
             "installations":[{"pluginId":"upload-js","owner":"test","repo":"repo","tag":"v1","sha256":"old","description":"old"},
                              {"pluginId":"other","owner":"test","repo":"repo","tag":"v1","sha256":"old","description":"other"}]}
            """);
        using var zip = JsZip("upload-js", "1.0", "");
        await fixture.Service.UploadAsync(zip, CancellationToken.None);
        (await fixture.Service.InstallationsAsync(CancellationToken.None)).Single().PluginId.Should().Be("other");
        (await fixture.Service.RepositoriesAsync(CancellationToken.None)).Should().ContainSingle();
    }

    private static MemoryStream JsZip(string id, string version, string prefix, bool extra = false, bool broken = false)
    {
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, id, name = id, version, runtime = "jint", hostApi = "1", format = "esm-bundle",
            entry = "server/plugin.mjs", platform = new { name = id }, hooks = new { invoke = "invoke" }
        });
        var files = new Dictionary<string, byte[]>
        {
            [prefix + "plugin.json"] = Encoding.UTF8.GetBytes(manifest),
            [prefix + "server/plugin.mjs"] = Encoding.UTF8.GetBytes(broken ? "broken {" :
                "export function invoke(ctx) { return { response: { kind: 'error', statusCode: 400, message: 'fixture' } }; }")
        };
        if (extra) files[prefix + "obsolete.txt"] = [1];
        return Zip(files);
    }

    private static MemoryStream Zip(Dictionary<string, byte[]> files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in files)
            {
                using var output = zip.CreateEntry(name).Open();
                output.Write(bytes);
            }
        stream.Position = 0;
        return stream;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "test-data", "upload-" + Guid.NewGuid().ToString("N"));
        public PluginCatalog Catalog { get; }
        public PluginReleaseService Service { get; }

        public Fixture()
        {
            var hosts = new Mock<IPluginHostFactory>();
            hosts.Setup(host => host.Create(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()))
                .Returns((string key, IReadOnlyList<string> _) => PluginTestHost.Create(key));
            Catalog = new PluginCatalog(new PlatformRegistry(), Mock.Of<IModelCatalog>(), hosts.Object,
                new PluginPolicyRegistry(), null!, null!, Mock.Of<IPluginLogSink>(), NullLogger<PluginCatalog>.Instance,
                Options.Create(new PluginExecutionOptions()));
            typeof(PluginCatalog).GetField("_pluginRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Catalog, Root);
            Service = new PluginReleaseService(Mock.Of<IHttpClientFactory>(), Catalog);
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await Catalog.DisposeAsync();
            // 只清理本测试创建的独立目录；DLL 快照可能被 CLR 延迟释放。
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
