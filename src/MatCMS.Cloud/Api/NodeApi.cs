using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using MatCMS.Cloud.Services.Nodes;

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
    public sealed record EdgeGuardDto(bool? Enabled, bool? Rotate);

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
            NodeService nodes, MigrationService migrations) =>
        {
            var node = await nodes.AuthenticateAsync(publicId, ctx.Request.Headers[NodeProtocol.TokenHeader].ToString(), ctx.RequestAborted);
            if (node is null || node.Revoked) return Results.Unauthorized();
            var ok = await migrations.IsRunningTransferAsync(transferId, node.Id, asSource: true);
            if (!ok) return Results.NotFound();
            // A site's data volume dwarfs Kestrel's default body cap.
            var size = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) size.MaxRequestBodySize = null;
            var bytes = await new FileNodeTransfer().UploadAsync(transferId, ctx.Request.Body, ctx.RequestAborted);
            return Results.Ok(new { bytes });
        }).RequireRateLimiting("nodeApi").DisableAntiforgery();

        app.MapGet("/api/nodes/{publicId}/transfers/{transferId}", async (HttpContext ctx, string publicId, string transferId,
            NodeService nodes, MigrationService migrations) =>
        {
            var node = await nodes.AuthenticateAsync(publicId, ctx.Request.Headers[NodeProtocol.TokenHeader].ToString(), ctx.RequestAborted);
            if (node is null || node.Revoked) return Results.Unauthorized();
            var ok = await migrations.IsRunningTransferAsync(transferId, node.Id, asSource: false);
            if (!ok) return Results.NotFound();
            var path = FileNodeTransfer.PathFor(transferId);
            if (!File.Exists(path)) return Results.NotFound();
            return Results.File(path, "application/x-tar");
        }).RequireRateLimiting("nodeApi");

        // ---- Operator REST ----------------------------------------------------------------------------------
        app.MapGet("/api/v1/nodes", async (HttpContext ctx, ApiKeyService keys, NodeService nodes, DockerHostService docker,
            Services.Proxy.ProxyService proxy) =>
        {
            var (_, error) = await CallerAsync(ctx, keys, cloudWide: false);
            if (error is not null) return error;
            var counts = await nodes.InstanceCountsAsync();
            var list = await nodes.ListAsync();
            return Results.Ok(new
            {
                // "Dieser Host" — the cloud's own daemon, configured by the cloud-wide settings (/api/v1/hosting/proxy).
                local = new
                {
                    id = "local", dockerConfigured = docker.Configured, dockerReachable = await docker.IsReachableAsync(ctx.RequestAborted),
                    instances = await nodes.CountLocalInstancesAsync(), proxy = proxy.PublicConfig(),
                },
                nodes = list.Select(n => NodeService.PublicJson(n, counts.GetValueOrDefault(n.Id), withInventory: false)),
            });
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/nodes", async (HttpContext ctx, ApiKeyService keys, NodeService nodes, CreateNodeDto b) =>
        {
            var (_, error) = await CallerAsync(ctx, keys, cloudWide: true);
            if (error is not null) return error;
            var (node, token, err) = await nodes.CreateAsync(b.Name, ctx.RequestAborted);
            if (node is null) return Results.Json(new { error = err }, statusCode: StatusCodes.Status409Conflict);
            // The token exists in plain text only in this response.
            return Results.Ok(new { id = node.PublicId, name = node.Name, token, command = nodes.AgentCommand(node, token!), compose = nodes.AgentCompose(node, token!), composeAgent = nodes.AgentComposeAgentOnly(node, token!) });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/nodes/{id}", async (HttpContext ctx, string id, ApiKeyService keys, NodeService nodes, VersionService version) =>
        {
            var (_, error) = await CallerAsync(ctx, keys, cloudWide: false);
            if (error is not null) return error;
            var n = await nodes.FindByPublicIdAsync(id, track: false);
            if (n is null) return NotFound();
            return Results.Ok(NodeService.PublicJson(n, await nodes.CountInstancesAsync(n.Id), withInventory: true, version.Current));
        }).RequireRateLimiting("operatorApi");

        app.MapPut("/api/v1/nodes/{id}", async (HttpContext ctx, string id, ApiKeyService keys, NodeService nodes,
            NodeService.NodeInput b) =>
        {
            var (_, error) = await CallerAsync(ctx, keys, cloudWide: true);
            if (error is not null) return error;
            var n = await nodes.FindByPublicIdAsync(id);
            if (n is null) return NotFound();
            var err = await nodes.UpdateAsync(n, b, ctx.RequestAborted);
            if (err is not null) return Results.Json(new { error = err }, statusCode: StatusCodes.Status409Conflict);
            return Results.Ok(NodeService.PublicJson(n, await nodes.CountInstancesAsync(n.Id), withInventory: false));
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/nodes/{id}/{action}", async (HttpContext ctx, string id, string action, ApiKeyService keys,
            NodeService nodes, Services.Proxy.ProxyService proxy) =>
        {
            // test is an ordinary hosting action; the rest change the node itself.
            var cloudWide = action != "test-proxy";
            var (_, error) = await CallerAsync(ctx, keys, cloudWide);
            if (error is not null) return error;
            var n = await nodes.FindByPublicIdAsync(id);
            if (n is null) return NotFound();
            switch (action)
            {
                case "revoke": await nodes.SetRevokedAsync(n, true, ctx.RequestAborted); return Results.Ok(new { ok = true, revoked = true });
                case "activate": await nodes.SetRevokedAsync(n, false, ctx.RequestAborted); return Results.Ok(new { ok = true, revoked = false });
                case "token":
                    var token = await nodes.RotateTokenAsync(n, ctx.RequestAborted);
                    return Results.Ok(new { ok = true, token, command = nodes.AgentCommand(n, token), compose = nodes.AgentCompose(n, token), composeAgent = nodes.AgentComposeAgentOnly(n, token) });
                case "update-agent":
                    var u = await nodes.UpdateAgentAsync(n, ctx.RequestAborted);
                    return u.Ok ? Results.Ok(new { ok = true, message = u.Message })
                                : Results.Json(new { ok = false, error = u.Message }, statusCode: StatusCodes.Status409Conflict);
                case "test-proxy":
                    var r = await proxy.TestAsync(n, ctx.RequestAborted);
                    return Results.Ok(new { ok = r.Ok, provider = r.Kind, message = r.Message });
                default:
                    return Results.Json(new { error = "Unbekannte Aktion. Erlaubt: revoke, activate, token, update-agent, test-proxy." }, statusCode: StatusCodes.Status400BadRequest);
            }
        }).RequireRateLimiting("operatorApi");

        // "Nur über den Edge erreichbar": closes the node's automatic host addresses to everybody but the edge (a secret header the edge
        // sends), switches that off, rotates the secret, or — with no enabled — applies the current state once more (after a node was
        // unreachable half-way). The answer lists what could not be done; the secret never leaves the cloud.
        app.MapPut("/api/v1/nodes/{id}/edge-guard", async (HttpContext ctx, string id, EdgeGuardDto b, ApiKeyService keys,
            NodeService nodes, Services.Proxy.ProxyService proxy) =>
        {
            var (_, error) = await CallerAsync(ctx, keys, cloudWide: true);
            if (error is not null) return error;
            var n = await nodes.FindByPublicIdAsync(id);
            if (n is null) return NotFound();
            var r = await proxy.SetEdgeGuardAsync(n, b.Enabled ?? n.EdgeGuard, b.Rotate ?? false, ctx.RequestAborted);
            return Results.Json(new { ok = r.Ok, message = r.Message, notes = r.Notes, edgeGuard = n.EdgeGuard },
                statusCode: r.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/nodes/{id}", async (HttpContext ctx, string id, ApiKeyService keys, NodeService nodes) =>
        {
            var (_, error) = await CallerAsync(ctx, keys, cloudWide: true);
            if (error is not null) return error;
            var n = await nodes.FindByPublicIdAsync(id);
            if (n is null) return NotFound();
            await nodes.DeleteAsync(n, ctx.RequestAborted);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/nodes/{id}/jobs", async (HttpContext ctx, string id, int? take, ApiKeyService keys, NodeService nodes) =>
        {
            var (_, error) = await CallerAsync(ctx, keys, cloudWide: false);
            if (error is not null) return error;
            var n = await nodes.FindByPublicIdAsync(id, track: false);
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
