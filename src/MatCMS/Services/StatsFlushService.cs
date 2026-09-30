using MatCMS.Data;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// Writes what <see cref="StatsCollector"/> counted into <c>StatCounters</c> once a minute (one upsert per counter,
/// in one transaction), and once an hour keeps the table small: days past the retention go, and for finished days
/// the long tail of paths, referrers and 404s is folded into one "…" row per kind — a scanner's thousand invented
/// URLs are one row, not a thousand, from the next hour on.
/// </summary>
public sealed class StatsFlushService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly StatsCollector _stats;
    private readonly ILogger<StatsFlushService> _log;
    public StatsFlushService(IServiceScopeFactory scopes, StatsCollector stats, ILogger<StatsFlushService> log)
    {
        _scopes = scopes; _stats = stats; _log = log;
    }

    public const int DefaultRetentionDays = 400;
    /// <summary>Keys kept per finished day and kind before the rest is folded.</summary>
    private const int KeepPerDay = 300;
    private static readonly string[] Foldable = { StatKinds.Path, StatKinds.Referrer, StatKinds.NotFound };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lastSweep = DateTime.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await FlushAsync(ct);
                if (DateTime.UtcNow - lastSweep > TimeSpan.FromHours(1))
                {
                    lastSweep = DateTime.UtcNow;
                    try { await SweepAsync(ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Statistik-Aufräumen fehlgeschlagen"); }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // A clean shutdown (container update) keeps the last minute too.
            try { await FlushAsync(CancellationToken.None); } catch { }
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        var rows = _stats.Drain();
        if (rows.Count == 0) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var r in rows)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO \"StatCounters\" (\"Day\", \"Kind\", \"Key\", \"Count\") VALUES ({r.Day}, {r.Kind}, {r.Key}, {r.Count}) ON CONFLICT(\"Day\", \"Kind\", \"Key\") DO UPDATE SET \"Count\" = \"Count\" + excluded.\"Count\"", ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            _stats.Return(rows);
            _log.LogWarning(ex, "Statistik konnte nicht gespeichert werden — wird beim nächsten Durchlauf erneut versucht");
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var raw = await db.SiteSettings.AsNoTracking().Where(s => s.Key == SettingKeys.StatsRetentionDays).Select(s => s.Value).FirstOrDefaultAsync(ct);
        var days = int.TryParse(raw, out var d) && d > 0 ? d : DefaultRetentionDays;
        var cut = StatKinds.DayOf(DateTime.UtcNow.AddDays(-days));
        await db.StatCounters.Where(c => string.Compare(c.Day, cut) < 0).ExecuteDeleteAsync(ct);

        // Fold finished days only: today's tail is still growing and would be folded again every hour.
        var today = StatKinds.DayOf(DateTime.UtcNow);
        var heavy = await db.StatCounters.AsNoTracking()
            .Where(c => string.Compare(c.Day, today) < 0 && Foldable.Contains(c.Kind) && c.Key != StatKinds.Other)
            .GroupBy(c => new { c.Day, c.Kind })
            .Where(g => g.Count() > KeepPerDay)
            .Select(g => new { g.Key.Day, g.Key.Kind })
            .ToListAsync(ct);
        foreach (var h in heavy)
        {
            var tail = await db.StatCounters
                .Where(c => c.Day == h.Day && c.Kind == h.Kind && c.Key != StatKinds.Other)
                .OrderByDescending(c => c.Count).ThenBy(c => c.Key)
                .Skip(KeepPerDay).ToListAsync(ct);
            if (tail.Count == 0) continue;
            var sum = tail.Sum(c => c.Count);
            db.StatCounters.RemoveRange(tail);
            var other = await db.StatCounters.FirstOrDefaultAsync(c => c.Day == h.Day && c.Kind == h.Kind && c.Key == StatKinds.Other, ct);
            if (other is null) db.StatCounters.Add(new Models.StatCounter { Day = h.Day, Kind = h.Kind, Key = StatKinds.Other, Count = sum });
            else other.Count += sum;
            await db.SaveChangesAsync(ct);
        }
    }
}
