using Router.Infrastructure.Services;

namespace Router.Host.Api;

/// <summary>沿用管理员会话与 CSRF 的模型映射管理入口。</summary>
internal static class ModelMappingEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/admin/model-mappings", (ModelMappingStore store) => Results.Ok(store.List()));
        app.MapPost("/api/admin/model-mappings", (ModelMapping body, ModelMappingStore store) => Save(body with { Id = "" }, store));
        app.MapPut("/api/admin/model-mappings/{id}", (string id, ModelMapping body, ModelMappingStore store) => Save(body with { Id = id }, store));
        app.MapDelete("/api/admin/model-mappings/{id}", (string id, ModelMappingStore store)
            => store.Delete(id) ? Results.NoContent() : Results.NotFound(new { error = "映射不存在。" }));
    }

    private static IResult Save(ModelMapping mapping, ModelMappingStore store)
    {
        try { return Results.Ok(store.Save(mapping)); }
        catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
    }
}
