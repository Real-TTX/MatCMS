using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Api;

/// <summary>
/// Visitor statistics over the operator API, through the same <see cref="StatsService"/> as the Statistik pages;
/// the MCP tool <c>get_stats</c> returns the same shapes (<see cref="SummaryDto"/>, <see cref="FleetDto"/>). Reading
/// needs any key, scoped like every instance call — a scoped key sees its own sites and not the sum of the others.
/// </summary>
public static class StatsApi
{
    public static object SummaryDto(StatsSummary s) => new
    {
        from = s.From, to = s.To,
        views = s.Views, visitors = s.Visitors, bots = s.Bots, notFound = s.NotFound, serverErrors = s.ServerErrors,
        previousViews = s.PreviousViews, previousVisitors = s.PreviousVisitors,
        days = s.Days.Select(d => new { day = d.Day, views = d.Views, visitors = d.Visitors }),
        topPages = s.TopPages.Select(e => new { path = e.Key, views = e.Count }),
        referrers = s.Referrers.Select(e => new { host = e.Key, views = e.Count }),
        notFoundPages = s.NotFoundPages.Select(e => new { path = e.Key, hits = e.Count }),
        devices = s.Devices.Select(e => new { device = e.Key, views = e.Count }),
    };

    public static object FleetDto(List<StatsService.FleetRow> rows, StatsSummary total, int days) => new
    {
        days,
        total = new { views = total.Views, visitors = total.Visitors, bots = total.Bots, notFound = total.NotFound, serverErrors = total.ServerErrors, previousViews = total.PreviousViews },
        instances = rows.Select(r => new
        {
            id = r.Instance.PublicId, name = r.Instance.Name, hasData = r.HasData,
            views = r.Views, visitors = r.Visitors, notFound = r.NotFound, serverErrors = r.ServerErrors, previousViews = r.PreviousViews,
        }),
    };

    /// <summary>The approved instances a key may see.</summary>
    public static async Task<List<Instance>> VisibleAsync(AppDbContext db, ApiKey key, CancellationToken ct)
    {
        var q = db.Instances.AsNoTracking().Where(i => i.Status == InstanceStatus.Approved);
        if (!key.AllInstances)
        {
            var ids = key.Instances.Select(s => s.InstanceId).ToList();
            q = q.Where(i => ids.Contains(i.Id));
        }
        return await q.ToListAsync(ct);
    }

    public static void MapStatsApi(this WebApplication app)
    {
        app.MapGet("/api/v1/stats", async (HttpContext ctx, ApiKeyService keys, AppDbContext db, StatsService stats, int? days) =>
        {
            var key = await keys.AuthenticateAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
            if (key is null) return Results.Json(new { error = "Ungültiger oder fehlender API-Schlüssel." }, statusCode: StatusCodes.Status401Unauthorized);
            var d = StatsService.NormalisePeriod(days);
            var (rows, total) = await stats.FleetAsync(await VisibleAsync(db, key, ctx.RequestAborted), d, ctx.RequestAborted);
            return Results.Ok(FleetDto(rows, total, d));
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/instances/{publicId}/stats", async (HttpContext ctx, string publicId, ApiKeyService keys, AppDbContext db, StatsService stats, int? days) =>
        {
            var key = await keys.AuthenticateAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
            if (key is null) return Results.Json(new { error = "Ungültiger oder fehlender API-Schlüssel." }, statusCode: StatusCodes.Status401Unauthorized);
            var instance = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.PublicId == publicId, ctx.RequestAborted);
            if (instance is null || !ApiKeyService.CanAccess(key, instance))
                return Results.Json(new { error = "Instanz nicht gefunden." }, statusCode: StatusCodes.Status404NotFound);
            var summary = await stats.SummaryAsync(instance.Id, StatsService.NormalisePeriod(days), ctx.RequestAborted);
            return Results.Ok(new { id = instance.PublicId, name = instance.Name, stats = SummaryDto(summary) });
        }).RequireRateLimiting("operatorApi");
    }
}
