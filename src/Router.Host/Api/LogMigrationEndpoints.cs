using Router.Host.Services;

namespace Router.Host.Api;

public static class LogMigrationEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/admin/log-migration", (HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] LogMigrationJobService migration) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(migration.GetSnapshot());
        });
        app.MapPost("/api/admin/log-migration", ([Microsoft.AspNetCore.Mvc.FromServices] LogMigrationJobService migration) =>
        {
            var result = migration.TryStart();
            if (result.Started) return Results.Accepted(value: result.Snapshot);
            return Results.Conflict(result.Snapshot);
        });
    }
}
