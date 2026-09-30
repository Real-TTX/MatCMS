using MatCMS.Data;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>Reads the visitor statistics — for Admin → Statistik and for the counters the heartbeat carries to the
/// cloud. Both add what <see cref="StatsCollector"/> has not flushed yet, so "today" is live and the cloud never
/// gets a day a minute behind the site's own page.</summary>
public sealed class StatsService
{
    private readonly AppDbContext _db;
    private readonly StatsCollector _live;
    public StatsService(AppDbContext db, StatsCollector live) { _db = db; _live = live; }

    public static readonly int[] Periods = { 7, 30, 90, 365 };

    /// <summary>The last <paramref name="days"/> days up to today. Totals per day come as they are (also for the
    /// period before, for the trend); the top lists are summed in the database, so a year does not load every
    /// path of every day.</summary>
    public async Task<StatsSummary> SummaryAsync(int days, CancellationToken ct = default)
    {
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-(days - 1));
        var prevFrom = StatKinds.DayOf(from.AddDays(-days).ToDateTime(TimeOnly.MinValue));
        var fromDay = StatKinds.DayOf(from.ToDateTime(TimeOnly.MinValue));

        var rows = await _db.StatCounters.AsNoTracking()
            .Where(c => c.Kind == StatKinds.Total && string.Compare(c.Day, prevFrom) >= 0)
            .Select(c => new StatRow(c.Day, c.Kind, c.Key, c.Count))
            .ToListAsync(ct);
        foreach (var kind in new[] { StatKinds.Path, StatKinds.Referrer, StatKinds.NotFound, StatKinds.Device })
        {
            var top = await _db.StatCounters.AsNoTracking()
                .Where(c => c.Kind == kind && string.Compare(c.Day, fromDay) >= 0)
                .GroupBy(c => c.Key)
                .Select(g => new { Key = g.Key, Count = g.Sum(c => c.Count) })
                .OrderByDescending(x => x.Count).Take(60)
                .ToListAsync(ct);
            // Summed rows carry the period's first day: Build only needs them inside the period.
            rows.AddRange(top.Select(t => new StatRow(fromDay, kind, t.Key, t.Count)));
        }
        rows.AddRange(_live.Snapshot());
        return StatsSummary.Build(rows, from, to);
    }

    /// <summary>The counters of <paramref name="fromDay"/> … today for the cloud, at most <paramref name="maxDays"/>
    /// days and the top <see cref="StatKinds.WireTopPerKind"/> keys per kind and day.</summary>
    public async Task<List<StatDayReport>> DaysForCloudAsync(string? fromDay, int maxDays, CancellationToken ct = default)
    {
        var today = StatKinds.DayOf(DateTime.UtcNow);
        // Never before the oldest day the site has: a first link starts where the data starts, not 400 empty days ago.
        var oldest = await _db.StatCounters.AsNoTracking().OrderBy(c => c.Day).Select(c => c.Day).FirstOrDefaultAsync(ct) ?? today;
        if (fromDay is null || string.CompareOrdinal(fromDay, oldest) < 0) fromDay = oldest;
        if (string.CompareOrdinal(fromDay, today) > 0) fromDay = today;
        var until = StatKinds.DayOf(StatKinds.ParseDay(fromDay).AddDays(maxDays - 1).ToDateTime(TimeOnly.MinValue));
        if (string.CompareOrdinal(until, today) > 0) until = today;

        var rows = await _db.StatCounters.AsNoTracking()
            .Where(c => string.Compare(c.Day, fromDay) >= 0 && string.Compare(c.Day, until) <= 0)
            .Select(c => new StatRow(c.Day, c.Kind, c.Key, c.Count))
            .ToListAsync(ct);
        rows.AddRange(_live.Snapshot().Where(r => string.CompareOrdinal(r.Day, fromDay) >= 0 && string.CompareOrdinal(r.Day, until) <= 0));

        // Every day of the window with data (oldest first), and the window's last day ALWAYS — even empty — so the
        // cloud's "have until" moves past a gap instead of asking for the same empty window for ever.
        var days = rows.Select(r => r.Day).Append(until).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        var result = new List<StatDayReport>(days.Count);
        foreach (var day in days)
        {
            var counts = rows.Where(r => r.Day == day)
                .GroupBy(r => (r.Kind, r.Key))
                .Select(g => new StatCount { Kind = g.Key.Kind, Key = g.Key.Key, Count = g.Sum(r => r.Count) })
                .GroupBy(c => c.Kind)
                .SelectMany(g => g.Key == StatKinds.Total ? g : g.OrderByDescending(c => c.Count).Take(StatKinds.WireTopPerKind))
                .ToList();
            result.Add(new StatDayReport { Day = day, Counts = counts });
        }
        return result;
    }
}
