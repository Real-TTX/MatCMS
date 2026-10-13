using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;

namespace MatCMS.Cloud.Api;

/// <summary>
/// The notification matrix over the operator API — what the Benachrichtigungen page edits, through the same
/// <see cref="NotificationService"/>. Rights: it decides who hears about the whole fleet, so reading needs an
/// all-instances key, and writing additionally <see cref="ApiKey.CanManageProfiles"/> (the right for the cloud's
/// rolled-out configuration).
/// </summary>
public static class NotificationApi
{
    public sealed record MatrixDto(List<NotifyRow> Rows);

    /// <summary>The matrix plus what its keys mean, so a client (or an AI agent) can edit it without guessing.</summary>
    public static async Task<object> DescribeAsync(NotificationService notify)
    {
        var m = notify.Load();
        var users = await notify.UsersAsync();
        return new
        {
            events = NotifyEvents.All,
            fleetOnlyEvents = NotifyEvents.FleetOnly,
            rowKinds = new
            {
                groups = new[] { NotificationService.GroupAdmins, NotificationService.GroupOperators },
                user = "u:<userId>", email = "e:<address>",
                note = "Operators (group or single) only receive events of their assigned instances and never fleet-only events.",
            },
            users,
            rows = m.Rows,
        };
    }

    public static void MapNotificationApi(this WebApplication app)
    {
        app.MapGet("/api/v1/notifications", async (HttpContext ctx, ApiKeyService keys, NotificationService notify) =>
        {
            var key = await keys.AuthenticateAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
            if (key is null) return Results.Json(new { error = "Ungültiger oder fehlender API-Schlüssel." }, statusCode: StatusCodes.Status401Unauthorized);
            if (!key.AllInstances) return Results.Json(new { error = "Benachrichtigungen betreffen die ganze Cloud — nur mit einem Schlüssel für alle Instanzen." }, statusCode: StatusCodes.Status403Forbidden);
            return Results.Ok(await DescribeAsync(notify));
        }).RequireRateLimiting("operatorApi");

        app.MapPut("/api/v1/notifications", async (HttpContext ctx, ApiKeyService keys, NotificationService notify, MatrixDto b) =>
        {
            var key = await keys.AuthenticateAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
            if (key is null) return Results.Json(new { error = "Ungültiger oder fehlender API-Schlüssel." }, statusCode: StatusCodes.Status401Unauthorized);
            if (!key.AllInstances || !key.CanManageProfiles)
                return Results.Json(new { error = "Benachrichtigungen ändern braucht einen Schlüssel für alle Instanzen mit dem Recht „Profile verwalten“." }, statusCode: StatusCodes.Status403Forbidden);
            // Full replace, normalised (unknown events/keys dropped, fleet events never on the Operator group).
            await notify.SaveAsync(new NotifyMatrix { Rows = b.Rows ?? new() });
            return Results.Ok(await DescribeAsync(notify));
        }).RequireRateLimiting("operatorApi");
    }
}
