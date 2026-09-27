using Router.Host.Plugins;

namespace Router.Host.Api;

public static class PluginReleaseEndpoints
{
    public static void Map(WebApplication app)
    {
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
