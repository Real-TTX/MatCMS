using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;

namespace MatCMS.Cloud.Api;

/// <summary>
/// The dashboard's lists over the operator API — what needs attention, and the apply history — through the same
/// services as the dashboard and its pages (<see cref="AttentionService"/>, <see cref="SyncHistoryService"/>); MCP
/// <c>get_attention</c> / <c>list_sync_runs</c> return the same shapes. Any key, scoped to its instances; the fleet
/// lines (nodes, the cloud itself) only for an all-instances key.
/// </summary>
public static class OverviewApi
{
    public static IReadOnlySet<int>? Allowed(ApiKey key) => key.AllInstances ? null : key.Instances.Select(s => s.InstanceId).ToHashSet();

    public static object AttentionDto(List<AttentionService.Item> items) => new
    {
        count = items.Count,
        items = items.Select(x => new { level = x.Level, kind = x.Kind, title = x.Title, text = x.Text, instanceId = x.InstanceId, url = x.Url }),
    };

    public static object SyncsDto(List<InstanceSyncRun> runs) => new
    {
        runs = runs.Select(r => new
        {
            ranAt = r.RanAt, instanceId = r.Instance?.PublicId, instance = r.Instance?.Name, revision = r.Revision,
            outcome = SyncHistoryService.Outcome(r), error = r.Error, installed = r.Installed, updated = r.Updated, failed = r.Failed,
        }),
    };

    public static void MapOverviewApi(this WebApplication app)
    {
        app.MapGet("/api/v1/attention", async (HttpContext ctx, ApiKeyService keys, AttentionService attention) =>
        {
            var key = await keys.AuthenticateAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
            if (key is null) return Results.Json(new { error = "Ungültiger oder fehlender API-Schlüssel." }, statusCode: StatusCodes.Status401Unauthorized);
            return Results.Ok(AttentionDto(await attention.BuildAsync(Allowed(key), fleet: key.AllInstances, ctx.RequestAborted)));
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/syncs", async (HttpContext ctx, ApiKeyService keys, SyncHistoryService history, int? take) =>
        {
            var key = await keys.AuthenticateAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
            if (key is null) return Results.Json(new { error = "Ungültiger oder fehlender API-Schlüssel." }, statusCode: StatusCodes.Status401Unauthorized);
            return Results.Ok(SyncsDto(await history.ListAsync(Allowed(key), take ?? 100, ctx.RequestAborted)));
        }).RequireRateLimiting("operatorApi");
    }
}
