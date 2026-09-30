using System.ComponentModel;
using MatCMS.Cloud.Api;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatCMS.Cloud.Mcp;

/// <summary>Visitor statistics for an AI client — the same figures and shapes as <c>/api/v1/stats</c>, scoped to
/// the key. Read-only.</summary>
[McpServerToolType]
public class StatsTools
{
    [McpServerTool(Name = "get_stats"), Description("Visitor statistics of the connected MatCMS sites. Without instanceId: per site views, visitors, 404s, server errors and the previous period's views, plus the total. With instanceId: that site's daily views/visitors, top pages, referrers, 404 pages and devices. Counted by the sites themselves, cookie-less; bots are counted separately and are not views.")]
    public static async Task<object> GetStats(
        McpContext me, AppDbContext db, StatsService stats,
        [Description("Optional instance id (from list_instances). Omit for the overview of all sites this key may see.")] string? instanceId = null,
        [Description("Period in days: 7, 30, 90 or 365 (default 30).")] int? days = null,
        CancellationToken ct = default)
    {
        var d = StatsService.NormalisePeriod(days);
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            var (rows, total) = await stats.FleetAsync(await StatsApi.VisibleAsync(db, me.Key, ct), d, ct);
            return StatsApi.FleetDto(rows, total, d);
        }
        var instance = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.PublicId == instanceId, ct);
        if (instance is null || !ApiKeyService.CanAccess(me.Key, instance)) throw new McpException("Instanz nicht gefunden.");
        return new { id = instance.PublicId, name = instance.Name, days = d, stats = StatsApi.SummaryDto(await stats.SummaryAsync(instance.Id, d, ct)) };
    }
}
