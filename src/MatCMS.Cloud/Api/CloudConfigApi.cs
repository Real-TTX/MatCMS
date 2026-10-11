using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;

namespace MatCMS.Cloud.Api;

/// <summary>
/// The cloud's own configuration over the API: its settings (Allgemein, SMTP, KI, Backup-Vorgaben — what the
/// Einstellungen page edits) and backups of the whole configuration (download here, upload on another cloud,
/// restore). Cloud-wide by nature, so every call needs a key for ALL instances with the profile and store rights;
/// restoring users or the hosting block additionally needs every other right too — they are the most powerful parts.
/// </summary>
public static class CloudConfigApi
{
    private static async Task<(ApiKey? key, IResult? error)> CallerAsync(HttpContext ctx, ApiKeyService keys)
    {
        var key = await keys.AuthenticateAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
        return key is null
            ? (null, Results.Json(new { error = "Ungültiger oder fehlender API-Schlüssel." }, statusCode: StatusCodes.Status401Unauthorized))
            : (key, null);
    }

    /// <summary>Cloud configuration is cloud-wide: all instances, profiles AND store.</summary>
    private static IResult? RequireConfig(ApiKey key) => key.AllInstances && key.CanManageProfiles && key.CanManageStore ? null
        : Results.Json(new { error = "Die Cloud-Konfiguration braucht einen Schlüssel für alle Instanzen mit den Rechten „Profile“ und „Store“." },
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Users and the server block can lock people out or move the cloud's addresses — only a key with every right.</summary>
    private static IResult? RequireFull(ApiKey key, IEnumerable<string> sections) =>
        sections.Any(s => s is "users" or "hosting") && !(key.CanRestore && key.CanManageHosting)
            ? Results.Json(new { error = "Benutzer und Server/Hosting wiederherstellen braucht zusätzlich die Rechte „Wiederherstellen“ und „Hosting“." },
                statusCode: StatusCodes.Status403Forbidden)
            : null;

    private static object InfoJson(CloudConfigBackupService.Info i) => new
    {
        id = i.Id, createdAt = i.CreatedAt, sizeBytes = i.Size, source = i.Source, version = i.Version, sections = i.Sections, hasSecrets = i.HasSecrets, note = i.Note,
    };

    public sealed record SettingsDto(Dictionary<string, string?>? Values, List<string>? Clear);
    public sealed record CreateDto(List<string>? Sections, string? Passphrase, string? Note);
    public sealed record RestoreDto(List<string>? Sections, string? Passphrase, bool? DryRun);

    public static void MapCloudConfigApi(this WebApplication app)
    {
        // ---- Settings (Einstellungen: Allgemein, SMTP, KI, Backup-Vorgaben) ---------------------------------------------
        app.MapGet("/api/v1/cloud/settings", async (HttpContext ctx, ApiKeyService keys, CloudConfigBackupService svc) =>
        {
            var (key, err) = await CallerAsync(ctx, keys);
            if (err is not null) return err;
            if (RequireConfig(key!) is { } d) return d;
            return Results.Ok(svc.GetSettings());
        }).RequireRateLimiting("operatorApi");

        app.MapPut("/api/v1/cloud/settings", async (HttpContext ctx, SettingsDto b, ApiKeyService keys, CloudConfigBackupService svc) =>
        {
            var (key, err) = await CallerAsync(ctx, keys);
            if (err is not null) return err;
            if (RequireConfig(key!) is { } d) return d;
            var (ok, error, changed) = await svc.SetSettingsAsync(b.Values, b.Clear);
            return ok ? Results.Ok(new { ok = true, changed }) : Results.Json(new { ok = false, error }, statusCode: StatusCodes.Status400BadRequest);
        }).RequireRateLimiting("operatorApi");

        // ---- Configuration backups --------------------------------------------------------------------------------------
        app.MapGet("/api/v1/cloud/config-backups", async (HttpContext ctx, ApiKeyService keys, CloudConfigBackupService svc) =>
        {
            var (key, err) = await CallerAsync(ctx, keys);
            if (err is not null) return err;
            if (RequireConfig(key!) is { } d) return d;
            return Results.Ok(new { sections = CloudConfigBackupService.AllSections, defaultRestore = CloudConfigBackupService.DefaultRestoreSections, backups = svc.List().Select(InfoJson) });
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/cloud/config-backups", async (HttpContext ctx, CreateDto b, ApiKeyService keys, CloudConfigBackupService svc) =>
        {
            var (key, err) = await CallerAsync(ctx, keys);
            if (err is not null) return err;
            if (RequireConfig(key!) is { } d) return d;
            var secs = CloudConfigBackupService.Normalise(b.Sections, CloudConfigBackupService.AllSections);
            if (RequireFull(key!, secs) is { } f) return f;
            var (info, error) = await svc.CreateAsync(secs, b.Passphrase, b.Note, ctx.RequestAborted);
            return info is null ? Results.Json(new { ok = false, error }, statusCode: StatusCodes.Status400BadRequest) : Results.Ok(new { ok = true, backup = InfoJson(info) });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/cloud/config-backups/{id}/download", async (HttpContext ctx, string id, ApiKeyService keys, CloudConfigBackupService svc) =>
        {
            var (key, err) = await CallerAsync(ctx, keys);
            if (err is not null) return err;
            if (RequireConfig(key!) is { } d) return d;
            return svc.FilePath(id) is { } p ? Results.File(p, "application/zip", id) : Results.Json(new { error = "Sicherung nicht gefunden." }, statusCode: StatusCodes.Status404NotFound);
        }).RequireRateLimiting("operatorApi");

        // Raw zip as the body (as downloaded from another cloud); name optionally in X-MatCMS-Backup-Name.
        app.MapPost("/api/v1/cloud/config-backups/upload", async (HttpContext ctx, ApiKeyService keys, CloudConfigBackupService svc) =>
        {
            var (key, err) = await CallerAsync(ctx, keys);
            if (err is not null) return err;
            if (RequireConfig(key!) is { } d) return d;
            // Plugin bundles ride along — the default request limit (30 MB) would cut a large store off.
            var sizeFeature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = 256L * 1024 * 1024;
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            if (ms.Length == 0) return Results.Json(new { ok = false, error = "Kein ZIP im Body." }, statusCode: StatusCodes.Status400BadRequest);
            var (info, error) = svc.Upload(ms.ToArray(), ctx.Request.Headers["X-MatCMS-Backup-Name"].ToString());
            return info is null ? Results.Json(new { ok = false, error }, statusCode: StatusCodes.Status400BadRequest) : Results.Ok(new { ok = true, backup = InfoJson(info) });
        }).RequireRateLimiting("operatorApi").DisableAntiforgery();

        app.MapPost("/api/v1/cloud/config-backups/{id}/restore", async (HttpContext ctx, string id, RestoreDto b, ApiKeyService keys, CloudConfigBackupService svc) =>
        {
            var (key, err) = await CallerAsync(ctx, keys);
            if (err is not null) return err;
            if (RequireConfig(key!) is { } d) return d;
            var secs = CloudConfigBackupService.Normalise(b.Sections, CloudConfigBackupService.DefaultRestoreSections);
            if (RequireFull(key!, secs) is { } f) return f;
            var r = await svc.RestoreAsync(id, secs, b.Passphrase, b.DryRun ?? false, ctx.RequestAborted);
            return r.Ok ? Results.Ok(new { ok = true, dryRun = r.DryRun, report = r.Lines })
                        : Results.Json(new { ok = false, error = string.Join(" ", r.Lines) }, statusCode: StatusCodes.Status409Conflict);
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/cloud/config-backups/{id}", async (HttpContext ctx, string id, ApiKeyService keys, CloudConfigBackupService svc) =>
        {
            var (key, err) = await CallerAsync(ctx, keys);
            if (err is not null) return err;
            if (RequireConfig(key!) is { } d) return d;
            return svc.Delete(id) ? Results.Ok(new { ok = true }) : Results.Json(new { error = "Sicherung nicht gefunden." }, statusCode: StatusCodes.Status404NotFound);
        }).RequireRateLimiting("operatorApi");
    }
}
