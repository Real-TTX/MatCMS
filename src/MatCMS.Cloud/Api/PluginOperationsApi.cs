using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;

namespace MatCMS.Cloud.Api;

/// <summary>
/// The REST twin of the MCP tools <c>list_plugin_operations</c> / <c>run_plugin_operation</c>: a site's described plugin operations, listed and
/// run through <see cref="PluginOperationService"/> (same checks). The run answers with the content op's id; its outcome is
/// <c>GET /api/v1/instances/{id}/content-ops/{opId}</c>.
/// </summary>
public static class PluginOperationsApi
{
    private static IResult Error(string message, int status) => Results.Json(new { error = message }, statusCode: status);

    private static async Task<(ApiKey? key, Instance? instance, IResult? fail)> ResolveAsync(HttpContext ctx, string publicId, ApiKeyService keys, InstanceService instances)
    {
        var key = await keys.AuthenticateAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
        if (key is null) return (null, null, Error("Ungültiger oder fehlender API-Schlüssel.", StatusCodes.Status401Unauthorized));
        var instance = await instances.FindForKeyAsync(publicId, key, track: false, ct: ctx.RequestAborted);
        if (instance is null) return (key, null, Error("Instanz nicht gefunden.", StatusCodes.Status404NotFound));
        if (instance.Status != InstanceStatus.Approved) return (key, null, Error("Instanz ist nicht freigegeben.", StatusCodes.Status409Conflict));
        return (key, instance, null);
    }

    public static void MapPluginOperationsApi(this WebApplication app)
    {
        app.MapGet("/api/v1/instances/{publicId}/plugin-operations", async (HttpContext ctx, string publicId, ApiKeyService keys, InstanceService instances) =>
        {
            var (key, instance, fail) = await ResolveAsync(ctx, publicId, keys, instances);
            return fail ?? Results.Ok(PluginOperationService.ListDto(instance!, key!));
        }).RequireRateLimiting("operatorApi");

        // The body IS the arguments object (like a backup upload is the raw ZIP): no wrapper to get wrong.
        app.MapPost("/api/v1/instances/{publicId}/plugin-operations/{name}", async (HttpContext ctx, string publicId, string name, ApiKeyService keys,
            InstanceService instances, PluginOperationService ops) =>
        {
            var (key, instance, fail) = await ResolveAsync(ctx, publicId, keys, instances);
            if (fail is not null) return fail;
            string body;
            using (var reader = new StreamReader(ctx.Request.Body)) body = await reader.ReadToEndAsync(ctx.RequestAborted);
            var r = await ops.RunAsync(instance!, key!, name, body, ctx.RequestAborted, by: "api:" + key!.Name);
            if (!r.Ok) return Error(r.Error!, r.Error!.Contains("Wiederherstellen-Recht") ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest);
            return Results.Accepted($"/api/v1/instances/{instance!.PublicId}/content-ops/{r.Op!.Id}",
                new { ok = true, opId = r.Op.Id, queued = true, name = r.Info!.Name, writes = r.Info.Writes });
        }).RequireRateLimiting("operatorApi");

        // The op's state — the REST counterpart of the MCP tool get_content_op (which had no REST twin until plugin operations had one).
        app.MapGet("/api/v1/instances/{publicId}/content-ops/{opId:int}", async (HttpContext ctx, string publicId, int opId, ApiKeyService keys, InstanceService instances) =>
        {
            var (key, instance, fail) = await ResolveAsync(ctx, publicId, keys, instances);
            if (fail is not null) return fail;
            var op = await instances.FindContentOpAsync(instance!.Id, opId, ctx.RequestAborted);
            if (op is null) return Error("Vorgang nicht gefunden.", StatusCodes.Status404NotFound);
            // The same rule as get_content_op: a result goes out without the restore right only for reads anybody who may read the site
            // sees anyway — never for a write's answer or a personal-data read (an order list holds customers' names and addresses).
            var mayRead = key!.CanRestore || PluginOperationService.ResultReadableWithoutRestore(instance, op.Kind);
            return Results.Ok(new
            {
                opId = op.Id, kind = op.Kind, done = op.DoneAt != null, outcome = op.Outcome, detail = mayRead ? op.Detail : null,
                result = !mayRead || op.ResultJson is null ? null : (object?)System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(op.ResultJson),
                resultWithheld = !mayRead && op.ResultJson is not null,
                createdAt = op.CreatedAt, doneAt = op.DoneAt,
            });
        }).RequireRateLimiting("operatorApi");
    }
}
