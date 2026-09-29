using Router.Host.Plugins;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Router.Host.Api;

public static class PluginReleaseEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/admin/plugins/upload", UploadAsync)
            .WithMetadata(new RequestSizeLimitAttribute(PluginReleaseService.MaxArchiveBytes + 1024 * 1024));
        app.MapGet("/api/admin/plugin-repositories", async (PluginReleaseService service, CancellationToken token)
            => await Execute(() => service.RepositoriesAsync(token)));
        app.MapPost("/api/admin/plugin-repositories", async (RepositoryRequest request, PluginReleaseService service, CancellationToken token)
            => await Execute(() => service.AddRepositoryAsync(request.Owner, request.Repo, token)));
        app.MapGet("/api/admin/plugin-repositories/{owner}/{repo}/releases", async
            (string owner, string repo, PluginReleaseService service, CancellationToken token)
            => await Execute(() => service.ReleasesAsync(owner, repo, token)));
        app.MapGet("/api/admin/plugin-repositories/{owner}/{repo}/releases/{tag}", async
            (string owner, string repo, string tag, PluginReleaseService service, CancellationToken token)
            => await Execute(() => service.IndexAsync(owner, repo, tag, token)));
        app.MapPost("/api/admin/plugin-repositories/install", async
            (InstallRequest request, PluginReleaseService service, CancellationToken token)
            => await Execute(async () =>
            {
                await service.InstallAsync(request.Owner, request.Repo, request.Tag, request.PluginIds ?? [], token);
                return new { installed = request.PluginIds };
            }));
        app.MapGet("/api/admin/plugin-updates", async (PluginReleaseService service, CancellationToken token)
            => await Execute(() => service.UpdatesAsync(token)));
        app.MapGet("/api/admin/plugin-installations", async (PluginReleaseService service, CancellationToken token)
            => await Execute(() => service.InstallationsAsync(token)));
        app.MapDelete("/api/admin/plugins/{pluginKey}", async
            (string pluginKey, PluginCatalog catalog, PluginReleaseService service, CancellationToken token) =>
            {
                try
                {
                    if (!await catalog.RemovePackageAsync(pluginKey, token)) return Results.NotFound();
                    await service.ForgetAsync(pluginKey, token);
                    return Results.NoContent();
                }
                catch (InvalidOperationException exception)
                { return Results.Conflict(new { error = exception.Message }); }
            });
    }

    internal static async Task<IResult> UploadAsync(HttpRequest request, PluginReleaseService service, CancellationToken token)
    {
        const long maxRequestBytes = PluginReleaseService.MaxArchiveBytes + 1024 * 1024;
        if (request.ContentLength > maxRequestBytes)
            return Results.Json(new { error = "Plugin ZIP must not exceed 100 MiB." }, statusCode: 413);
        var limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = maxRequestBytes;
        if (!request.HasFormContentType || !request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Upload one ZIP using multipart/form-data field 'file'." });
        return await Execute(async () =>
        {
            var form = await request.ReadFormAsync(new FormOptions
            {
                MultipartBodyLengthLimit = PluginReleaseService.MaxArchiveBytes,
                ValueCountLimit = 4
            }, token);
            if (form.Files.Count != 1 || form.Files[0].Name != "file")
                throw new ArgumentException("Upload exactly one plugin ZIP in field 'file'.");
            var file = form.Files[0];
            if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.Length <= 0
                || file.Length > PluginReleaseService.MaxArchiveBytes)
                throw new ArgumentException("Select a non-empty ZIP no larger than 100 MiB.");
            await using var input = file.OpenReadStream();
            return await service.UploadAsync(input, token);
        });
    }

    private static async Task<IResult> Execute<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        catch (HttpRequestException exception)
        { return Results.Json(new { error = "GitHub request failed: " + exception.Message }, statusCode: 502); }
    }

    public sealed record RepositoryRequest(string Owner, string Repo);
    public sealed record InstallRequest(string Owner, string Repo, string Tag, string[]? PluginIds);
}
