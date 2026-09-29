using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using MatCMS.Cloud.Services.Nodes;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Api;

/// <summary>
/// Nodes (Hosting increment 4): the agents' heartbeat, and the operator REST that manages them — everything the
/// Nodes pages do, through the same <see cref="NodeService"/> (API-first).
/// <para>Rights: every node endpoint needs <see cref="ApiKey.CanManageHosting"/> — nodes are infrastructure, not
/// something a site key needs to see. Creating, changing, revoking and deleting a node additionally need an
/// all-instances key: a node can host any site.</para>
/// </summary>
public static class NodeApi
{
    private static async Task<(ApiKey? key, IResult? error)> CallerAsync(HttpContext ctx, ApiKeyService keys, bool cloudWide)
    {
        var key = await keys.AuthenticateAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
        if (key is null)
            return (null, Results.Json(new { error = "Ungültiger oder fehlender API-Schlüssel." }, statusCode: StatusCodes.Status401Unauthorized));
        ctx.Items[NodeService.ApiKeyItem] = key;
        if (!key.CanManageHosting)
            return (null, Results.Json(new { error = "Dieser Schlüssel darf kein Hosting steuern." }, statusCode: StatusCodes.Status403Forbidden));
        if (cloudWide && !key.AllInstances)
            return (null, Results.Json(new { error = "Nodes verwalten braucht das Hosting-Recht UND einen Schlüssel für alle Instanzen." },
                statusCode: StatusCodes.Status403Forbidden));
        return (key, null);
    }

    private static IResult NotFound() => Results.Json(new { error = "Node nicht gefunden." }, statusCode: StatusCodes.Status404NotFound);

    public sealed record CreateNodeDto(string Name);

    public static void MapNodeApi(this WebApplication app)
    {
        // ---- The agent's heartbeat (long poll) ------------------------------------------------------------
        app.MapPost("/api/nodes/{publicId}/heartbeat", async (HttpContext ctx, string publicId, NodeHeartbeatRequest req, NodeService nodes) =>
        {
            var node = await nodes.AuthenticateAsync(publicId, ctx.Request.Headers[NodeProtocol.TokenHeader].ToString(), ctx.RequestAborted);
            if (node is null) return Results.Unauthorized();
            // Revoked: turned away, so the agent stops asking (it retries slowly) instead of receiving jobs.
            if (node.Revoked) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(await nodes.HeartbeatAsync(node, req, ctx.RequestAborted));
        }).RequireRateLimiting("nodeApi").DisableAntiforgery();

        // ---- Data transfer of a move (increment 5) ----------------------------------------------------------
        // Still outbound only: the SOURCE agent uploads, the TARGET agent downloads. Each is allowed exactly for
        // the one running move it takes part in, and only in its own role — a node cannot read another site's data.
        app.MapPut("/api/nodes/{publicId}/transfers/{transferId}", async (HttpContext ctx, string publicId, string transferId,
            NodeService nodes, AppDbContext db) =>
        {
            var node = await nodes.AuthenticateAsync(publicId, ctx.Request.Headers[NodeProtocol.TokenHeader].ToString(), ctx.RequestAborted);
            if (node is null || node.Revoked) return Results.Unauthorized();
            var ok = await db.InstanceMigrations.AnyAsync(m => m.TransferId == transferId && m.State == "running" && m.FromNodeId == node.Id);
            if (!ok) return Results.NotFound();
            // A site's data volume dwarfs Kestrel's default body cap.
            var size = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) size.MaxRequestBodySize = null;
            var bytes = await new FileNodeTransfer().UploadAsync(transferId, ctx.Request.Body, ctx.RequestAborted);
            return Results.Ok(new { bytes });
        }).RequireRateLimiting("nodeApi").DisableAntiforgery();

        app.MapGet("/api/nodes/{publicId}/transfers/{transferId}", async (HttpContext ctx, string publicId, string transferId,
            NodeService nodes, AppDbContext db) =>
        {
            var node = await nodes.AuthenticateAsync(publicId, ctx.Request.Headers[NodeProtocol.TokenHeader].ToString(), ctx.RequestAborted);
            if (node is null || node.Revoked) return Results.Unauthorized();
            var ok = await db.InstanceMigrations.AnyAsync(m => m.TransferId == transferId && m.State == "running" && m.ToNodeId == node.Id);
            if (!ok) return Results.NotFound();
            var path = FileNodeTransfer.PathFor(transferId);
            if (!File.Exists(path)) return Results.NotFound();
            return Results.File(path, "application/x-tar");
        }).RequireRateLimiting("nodeApi");

        // ---- Operator REST ----------------------------------------------------------------------------------
        app.MapGet("/api/v1/nodes", async (HttpContext ctx, ApiKeyService keys, AppDbContext db, DockerHostService docker,
            Services.Proxy.ProxyService proxy) =>
        {
            var (_, error) = await CallerAsync(ctx, keys, cloudWide: false);
            if (error is not null) return error;
            var counts = await db.Instances.Where(i => i.NodeId != null).GroupBy(i => i.NodeId).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
            var list = await db.Nodes.AsNoTracking().OrderBy(n => n.Name).ToListAsync();
            return Results.Ok(new
            {
                // "Dieser Host" — the cloud's own daemon, configured by the cloud-wide settings (/api/v1/hosting/proxy).
                local = new
                {
                    id = "local", dockerConfigured = docker.Configured, dockerReachable = await docker.IsReachableAsync(ctx.RequestAborted),
                    instances = await db.Instances.CountAsync(i => i.Hosting == InstanceHosting.Local), proxy = proxy.PublicConfig(),
                },
                nodes = list.Select(n => NodeService.PublicJson(n, counts.FirstOrDefault(c => c.Key == n.Id)?.N ?? 0, withInventory: false)),
            });
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/nodes", async (HttpContext ctx, ApiKeyService keys, NodeService nodes, CreateNodeDto b) =>
        {
            var (_, error) = await CallerAsync(ctx, keys, cloudWide: true);
            if (error is not null) return error;
            var (node, token, err) = await nodes.CreateAsync(b.Name, ctx.RequestAborted);
            if (node is null) return Results.Json(new { error = err }, statusCode: StatusCodes.Status409Conflict);
            // The token exists in plain text only in this response.
            return Results.Ok(new { id = node.PublicId, name = node.Name, token, command = nodes.AgentCommand(node, token!) });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/nodes/{id}", async (HttpContext ctx, string id, ApiKeyService keys, AppDbContext db) =>
        {
            var (_, error) = await CallerAsync(ctx, keys, cloudWide: false);
            if (error is not null) return error;
            var n = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(x => x.PublicId == id);
            if (n is null) return NotFound();
            return Results.Ok(NodeService.PublicJson(n, await db.Instances.CountAsync(i => i.NodeId == n.Id), withInventory: true));
        }).RequireRateLimiting("operatorApi");

        app.MapPut("/api/v1/nodes/{id}", async (HttpContext ctx, string id, ApiKeyService keys, AppDbContext db, NodeService nodes,
            NodeService.NodeInput b) =>
        {
            var (_, error) = await CallerAsync(ctx, keys, cloudWide: true);
            if (error is not null) return error;
            var n = await db.Nodes.FirstOrDefaultAsync(x => x.PublicId == id);
            if (n is null) return NotFound();
            var err = await nodes.UpdateAsync(n, b, ctx.RequestAborted);
            if (err is not null) return Results.Json(new { error = err }, statusCode: StatusCodes.Status409Conflict);
            return Results.Ok(NodeService.PublicJson(n, await db.Instances.CountAsync(i => i.NodeId == n.Id), withInventory: false));
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/nodes/{id}/{action}", async (HttpContext ctx, string id, string action, ApiKeyService keys,
            AppDbContext db, NodeService nodes, Services.Proxy.ProxyService proxy) =>
        {
            // test is an ordinary hosting action; the rest change the node itself.
            var cloudWide = action != "test-proxy";
            var (_, error) = await CallerAsync(ctx, keys, cloudWide);
            if (error is not null) return error;
            var n = await db.Nodes.FirstOrDefaultAsync(x => x.PublicId == id);
            if (n is null) return NotFound();
            switch (action)
            {
                case "revoke": await nodes.SetRevokedAsync(n, true, ctx.RequestAborted); return Results.Ok(new { ok = true, revoked = true });
                case "activate": await nodes.SetRevokedAsync(n, false, ctx.RequestAborted); return Results.Ok(new { ok = true, revoked = false });
                case "token":
                    var token = await nodes.RotateTokenAsync(n, ctx.RequestAborted);
                    return Results.Ok(new { ok = true, token, command = nodes.AgentCommand(n, token) });
                case "test-proxy":
                    var r = await proxy.TestAsync(n, ctx.RequestAborted);
                    return Results.Ok(new { ok = r.Ok, provider = r.Kind, message = r.Message });
                default:
                    return Results.Json(new { error = "Unbekannte Aktion. Erlaubt: revoke, activate, token, test-proxy." }, statusCode: StatusCodes.Status400BadRequest);
            }
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/nodes/{id}", async (HttpContext ctx, string id, ApiKeyService keys, AppDbContext db, NodeService nodes) =>
        {
            var (_, error) = await CallerAsync(ctx, keys, cloudWide: true);
            if (error is not null) return error;
            var n = await db.Nodes.FirstOrDefaultAsync(x => x.PublicId == id);
            if (n is null) return NotFound();
            await nodes.DeleteAsync(n, ctx.RequestAborted);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/nodes/{id}/jobs", async (HttpContext ctx, string id, int? take, ApiKeyService keys, AppDbContext db, NodeService nodes) =>
        {
            var (_, error) = await CallerAsync(ctx, keys, cloudWide: false);
            if (error is not null) return error;
            var n = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(x => x.PublicId == id);
            if (n is null) return NotFound();
            var jobs = await nodes.JobsAsync(n.Id, take ?? 50, ctx.RequestAborted);
            return Results.Ok(jobs.Select(JobJson));
        }).RequireRateLimiting("operatorApi");
    }

    /// <summary>A job for the API — payload and result are left out (the proxy payload carries the Matcad key,
    /// results can be whole log files).</summary>
    public static object JobJson(NodeJob j) => new
    {
        id = j.Id, kind = j.Kind, state = j.State.ToString().ToLowerInvariant(), message = j.Message,
        requestedBy = j.RequestedBy, createdAt = j.CreatedAt, startedAt = j.StartedAt, finishedAt = j.FinishedAt,
    };
}
