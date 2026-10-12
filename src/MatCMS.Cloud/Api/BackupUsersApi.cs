using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Api;

/// <summary>
/// A backup WITH the site's user accounts — for moving a site to another server, where the ordinary backup
/// (deliberately without users) would leave the new site with nobody who can sign in. Same request/upload/restore
/// cycle as <c>…/backups/request</c>; the instance just puts its users into this one file. Password hashes leave
/// the site, so this needs <see cref="Models.ApiKey.CanRestore"/> — the right that may overwrite a site anyway.
/// On the receiving site the import stays add-only for existing accounts (only an untouched default "admin" is
/// replaced). MCP <c>request_backup_with_users</c> is the same call.
/// </summary>
public static class BackupUsersApi
{
    public static void MapBackupUsersApi(this WebApplication app)
    {
        app.MapPost("/api/v1/instances/{publicId}/backups/request-users", async (
            HttpContext ctx, string publicId, ApiKeyService keys, InstanceService instances, AppDbContext db) =>
        {
            var key = await keys.AuthenticateAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
            if (key is null) return Results.Json(new { error = "Ungültiger oder fehlender API-Schlüssel." }, statusCode: StatusCodes.Status401Unauthorized);
            var instance = await FindAsync(db, key, publicId, ctx.RequestAborted);
            if (instance is null) return Results.Json(new { error = "Instanz nicht gefunden." }, statusCode: StatusCodes.Status404NotFound);
            if (!key.CanRestore)
                return Results.Json(new { error = "Dieser Schlüssel darf keine Benutzerdaten anfordern (Recht „Wiederherstellen“ fehlt)." }, statusCode: StatusCodes.Status403Forbidden);
            var (requestId, error) = await RequestAsync(instances, db, instance, "Backup mit Benutzern über die API angefordert.", ctx.RequestAborted);
            return error is not null
                ? Results.Json(new { error }, statusCode: StatusCodes.Status409Conflict)
                : Results.Ok(new { requestId });
        }).RequireRateLimiting("operatorApi");
    }

    /// <summary>Shared with the MCP tool. Older instances (no <c>IncludeUsers</c> in their contract) would answer with
    /// an ordinary backup — that is caught on the receiving side by simply finding no users in it.</summary>
    public static async Task<(int RequestId, string? Error)> RequestAsync(InstanceService instances, AppDbContext db, Models.Instance instance, string note, CancellationToken ct)
    {
        if (instance.Status != Models.InstanceStatus.Approved) return (0, "Instanz ist nicht freigegeben.");
        // Marked BEFORE the request is raised, so the one save inside RequestBackupAsync stores both — a heartbeat in
        // between must never see the new request without its flag and answer it with a backup lacking the users.
        instance.BackupUsersRequestId = instance.BackupRequestId + 1;
        await instances.RequestBackupAsync(instance, ct);
        instances.Log(instance, Models.InstanceEventKind.BackupRequested, note);
        await db.SaveChangesAsync(ct);
        return (instance.BackupRequestId, null);
    }

    /// <summary>The instance by public id if this key may reach it — null for both "unknown" and "out of scope", so a
    /// scoped key cannot probe for instances.</summary>
    public static async Task<Models.Instance?> FindAsync(AppDbContext db, Models.ApiKey key, string publicId, CancellationToken ct)
    {
        var instance = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == publicId, ct);
        return instance is not null && ApiKeyService.CanAccess(key, instance) ? instance : null;
    }
}
