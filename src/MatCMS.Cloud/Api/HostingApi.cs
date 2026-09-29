using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Api;

/// <summary>
/// Hosting over the operator API — everything the Hosting UI does, so an AI agent can do it too:
/// the module switch, container status/actions/logs per instance, and the cloud's own self-update. All
/// through the same services as the UI (<see cref="HostingActionsService"/>, <see cref="CloudUpdaterService"/>).
/// <para>Rights: reading status needs any valid key. Container actions and logs need
/// <see cref="ApiKey.CanManageHosting"/> and honour the key's instance scope (unknown and out-of-scope
/// both answer 404). The cloud-wide actions — switching the module, updating the cloud — need
/// <see cref="ApiKey.CanManageHosting"/> AND an all-instances key: they reach further than any scoped
/// list could.</para>
/// </summary>
public static class HostingApi
{
    private static async Task<(ApiKey? key, IResult? error)> CallerAsync(HttpContext ctx, ApiKeyService keys)
    {
        var key = await keys.AuthenticateAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
        return key is null
            ? (null, Results.Json(new { error = "Ungültiger oder fehlender API-Schlüssel." }, statusCode: StatusCodes.Status401Unauthorized))
            : (key, null);
    }

    private static IResult? RequireHosting(ApiKey key) => key.CanManageHosting ? null
        : Results.Json(new { error = "Dieser Schlüssel darf kein Hosting steuern." }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult? RequireCloudWide(ApiKey key) => key.CanManageHosting && key.AllInstances ? null
        : Results.Json(new { error = "Cloud-weite Hosting-Aktionen brauchen das Hosting-Recht UND einen Schlüssel für alle Instanzen." },
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult NotFoundInstance() =>
        Results.Json(new { error = "Instanz nicht gefunden." }, statusCode: StatusCodes.Status404NotFound);

    private static object ContainerJson(Instance i, DockerHostService.ContainerDetails? d) => new
    {
        hosting = i.Hosting.ToString().ToLowerInvariant(),
        local = HostingActionsService.IsLocal(i),
        cloudManaged = i.CloudManaged,
        containerState = i.ContainerState,
        localPort = i.LocalPort,
        container = d is null ? null : new
        {
            id = d.Id, name = d.Name, image = d.Image, imageId = d.ImageId, state = d.State,
            startedAt = d.StartedAt, restartCount = d.RestartCount, publishedPort = d.PublishedPort,
            cloudManaged = d.CloudManaged, health = d.Health,
        },
    };

    public static void MapHostingApi(this WebApplication app)
    {
        // ---- Module switch ---------------------------------------------------------------------------
        app.MapGet("/api/v1/hosting", async (HttpContext ctx, ApiKeyService keys, CloudContext cloud, DockerHostService docker) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            return Results.Ok(new
            {
                enabled = cloud.Flag(SettingKeys.HostingEnabled),
                dockerConfigured = docker.Configured,
                dockerReachable = await docker.IsReachableAsync(ctx.RequestAborted),
                canManageHosting = key!.CanManageHosting,
            });
        }).RequireRateLimiting("operatorApi");

        app.MapPut("/api/v1/hosting", async (HttpContext ctx, ApiKeyService keys, CloudContext cloud, HostingSwitchDto b) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireCloudWide(key!) is { } g) return g;
            await cloud.SaveAsync(new Dictionary<string, string?> { [SettingKeys.HostingEnabled] = b.Enabled ? "1" : "0" });
            return Results.Ok(new { ok = true, enabled = b.Enabled });
        }).RequireRateLimiting("operatorApi");

        // ---- Container per instance ------------------------------------------------------------------
        app.MapGet("/api/v1/instances/{publicId}/container", async (HttpContext ctx, string publicId, ApiKeyService keys,
            AppDbContext db, HostingActionsService hosting) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var inst = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == publicId);
            if (inst is null || !ApiKeyService.CanAccess(key!, inst)) return NotFoundInstance();
            await hosting.RefreshAsync(inst, ctx.RequestAborted);
            return Results.Ok(ContainerJson(inst, await hosting.DetailsAsync(inst, ctx.RequestAborted)));
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/instances/{publicId}/container/{action}", async (HttpContext ctx, string publicId, string action,
            ApiKeyService keys, AppDbContext db, HostingActionsService hosting) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireHosting(key!) is { } g) return g;
            var inst = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == publicId);
            if (inst is null || !ApiKeyService.CanAccess(key!, inst)) return NotFoundInstance();
            await hosting.RefreshAsync(inst, ctx.RequestAborted);

            HostingActionsService.ActionResult r;
            if (string.Equals(action, "update", StringComparison.OrdinalIgnoreCase))
                r = await hosting.UpdateAsync(inst, ctx.RequestAborted);
            else if (HostingActionsService.ParsePower(action) is { } p)
                r = await hosting.PowerAsync(inst, p, ctx.RequestAborted);
            else
                return Results.Json(new { error = "Unbekannte Aktion. Erlaubt: start, stop, restart, update." }, statusCode: StatusCodes.Status400BadRequest);

            return r.Ok
                ? Results.Ok(new { ok = true, message = r.Message, containerState = inst.ContainerState })
                : Results.Json(new { ok = false, error = r.Message }, statusCode: StatusCodes.Status409Conflict);
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/instances/{publicId}/container/logs", async (HttpContext ctx, string publicId, int? tail,
            ApiKeyService keys, AppDbContext db, HostingActionsService hosting) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            // Logs can carry request paths, stack traces and the like — reading them is an operator matter.
            if (RequireHosting(key!) is { } g) return g;
            var inst = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == publicId);
            if (inst is null || !ApiKeyService.CanAccess(key!, inst)) return NotFoundInstance();
            await hosting.RefreshAsync(inst, ctx.RequestAborted);
            var (ok, text) = await hosting.LogsAsync(inst, tail ?? 200, ctx.RequestAborted);
            return ok ? Results.Ok(new { ok = true, tail = tail ?? 200, logs = text })
                      : Results.Json(new { ok = false, error = text }, statusCode: StatusCodes.Status409Conflict);
        }).RequireRateLimiting("operatorApi");

        // ---- Cloud self-update -----------------------------------------------------------------------
        app.MapGet("/api/v1/cloud/update", async (HttpContext ctx, bool? check, ApiKeyService keys, CloudUpdaterService updater) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var s = await updater.StatusAsync(check ?? true, ctx.RequestAborted);
            return Results.Ok(new
            {
                current = s.Current, latest = s.Latest, updateAvailable = s.UpdateAvailable, checkError = s.CheckError,
                canSelfUpdate = s.CanSelfUpdate, blocker = s.Blocker,
                lastRun = s.LastRun is null ? null : new
                {
                    state = s.LastRun.State, message = s.LastRun.Message, startedAt = s.LastRun.StartedAt,
                    finishedAt = s.LastRun.FinishedAt, fromVersion = s.LastRun.FromVersion, toImage = s.LastRun.ToImage,
                    log = s.LastRun.Log,
                },
            });
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/cloud/update", async (HttpContext ctx, ApiKeyService keys, CloudUpdaterService updater) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireCloudWide(key!) is { } g) return g;
            var r = await updater.StartAsync(ctx.RequestAborted);
            // 202: the swap happens after this response — poll GET /api/v1/cloud/update for lastRun.
            return r.Ok ? Results.Json(new { ok = true, message = r.Message }, statusCode: StatusCodes.Status202Accepted)
                        : Results.Json(new { ok = false, error = r.Message }, statusCode: StatusCodes.Status409Conflict);
        }).RequireRateLimiting("operatorApi");
    }

    public record HostingSwitchDto(bool Enabled);
}
