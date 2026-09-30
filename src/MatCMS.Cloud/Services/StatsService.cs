using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Reads the visitor statistics the sites sent (<see cref="InstanceStat"/>) — for one site (the same
/// <see cref="StatsSummary"/> the site's own page shows) and for the fleet overview. One service for UI, REST and
/// MCP. It never scopes by itself: callers pass the instances the user or key may see.
/// </summary>
public sealed class StatsService
{
    private readonly AppDbContext _db;
    public StatsService(AppDbContext db) => _db = db;

    public static readonly int[] Periods = { 7, 30, 90, 365 };
    public static int NormalisePeriod(int? days) => days is int d && Periods.Contains(d) ? d : 30;

    private static (DateOnly From, DateOnly To, string FromDay, string PrevDay) Window(int days)
    {
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-(days - 1));
        return (from, to, Day(from), Day(from.AddDays(-days)));
    }

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One site's figures for the last <paramref name="days"/> days — totals per day as stored, top lists
    /// summed in the database.</summary>
    public async Task<StatsSummary> SummaryAsync(int instanceId, int days, CancellationToken ct = default)
    {
        var w = Window(days);
        var rows = await _db.InstanceStats.AsNoTracking()
            .Where(s => s.InstanceId == instanceId && s.Kind == StatKinds.Total && string.Compare(s.Day, w.PrevDay) >= 0)
            .Select(s => new StatRow(s.Day, s.Kind, s.Key, s.Count))
            .ToListAsync(ct);
        foreach (var kind in new[] { StatKinds.Path, StatKinds.Referrer, StatKinds.NotFound, StatKinds.Device })
        {
            var top = await _db.InstanceStats.AsNoTracking()
                .Where(s => s.InstanceId == instanceId && s.Kind == kind && string.Compare(s.Day, w.FromDay) >= 0)
                .GroupBy(s => s.Key)
                .Select(g => new { g.Key, Count = g.Sum(s => s.Count) })
                .OrderByDescending(x => x.Count).Take(60)
                .ToListAsync(ct);
            rows.AddRange(top.Select(t => new StatRow(w.FromDay, kind, t.Key, t.Count)));
        }
        return StatsSummary.Build(rows, w.From, w.To);
    }

    /// <summary>One line of the fleet overview.</summary>
    public sealed record FleetRow(Instance Instance, long Views, long Visitors, long NotFound, long ServerErrors,
        long? PreviousViews, List<long> ViewsPerDay, bool HasData);

    /// <summary>The fleet overview: per site the period's totals, the trend and a views-per-day series, plus the
    /// sum over all of them (built from the same rows, so it is exactly the sum of the lines).</summary>
    public async Task<(List<FleetRow> Rows, StatsSummary Total)> FleetAsync(IReadOnlyCollection<Instance> instances, int days, CancellationToken ct = default)
    {
        var w = Window(days);
        var ids = instances.Select(i => i.Id).ToList();
        var totals = await _db.InstanceStats.AsNoTracking()
            .Where(s => ids.Contains(s.InstanceId) && s.Kind == StatKinds.Total && string.Compare(s.Day, w.PrevDay) >= 0)
            .Select(s => new { s.InstanceId, Row = new StatRow(s.Day, s.Kind, s.Key, s.Count) })
            .ToListAsync(ct);
        var notFound = await _db.InstanceStats.AsNoTracking()
            .Where(s => ids.Contains(s.InstanceId) && s.Kind == StatKinds.NotFound && string.Compare(s.Day, w.FromDay) >= 0)
            .GroupBy(s => s.InstanceId)
            .Select(g => new { InstanceId = g.Key, Count = g.Sum(s => s.Count) })
            .ToDictionaryAsync(x => x.InstanceId, x => x.Count, ct);

        var byInstance = totals.GroupBy(t => t.InstanceId).ToDictionary(g => g.Key, g => g.Select(t => t.Row).ToList());
        var list = new List<FleetRow>(instances.Count);
        foreach (var inst in instances)
        {
            var rows = byInstance.GetValueOrDefault(inst.Id) ?? new();
            var s = StatsSummary.Build(rows, w.From, w.To);
            list.Add(new FleetRow(inst, s.Views, s.Visitors, notFound.GetValueOrDefault(inst.Id), s.ServerErrors,
                s.PreviousViews, s.Days.Select(d => d.Views).ToList(), inst.StatsHaveUntil is not null));
        }
        var all = totals.Select(t => t.Row)
            .Concat(notFound.Select(n => new StatRow(w.FromDay, StatKinds.NotFound, "*", n.Value)));
        return (list.OrderByDescending(r => r.Views).ThenBy(r => r.Instance.Name).ToList(), StatsSummary.Build(all, w.From, w.To));
    }
}
