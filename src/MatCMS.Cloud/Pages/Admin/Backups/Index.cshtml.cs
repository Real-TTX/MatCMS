using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Backups;

/// <summary>
/// Every backup every instance uploaded, and what does not add up.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly BackupStore _store;

    public IndexModel(AppDbContext db, BackupStore store)
    {
        _db = db; _store = store;
    }

    public List<CloudBackup> Items { get; private set; } = new();
    public List<Instance> Instances { get; private set; } = new();
    public List<BackupStore.Orphan> Orphans { get; private set; } = new();

    public long TotalBytes { get; private set; }

    /// <summary>What each instance is granted, by instance id. Per instance because the profile may
    /// raise it — one number for everybody stopped being true when quotas moved to the profile.</summary>
    public Dictionary<int, long> QuotaBytes { get; private set; } = new();

    public int? FilteredInstance { get; private set; }

    public long QuotaFor(int instanceId) => QuotaBytes.TryGetValue(instanceId, out var q) ? q : 0;

    /// <summary>Where an instance's quota comes from — the profile that raised it, or nothing when it
    /// is simply the cloud-wide default. Without this the column shows two different numbers with no
    /// hint of why, and the operator has to go looking through profiles to find out.</summary>
    public Dictionary<int, string?> QuotaFrom { get; private set; } = new();

    public string? QuotaSource(int instanceId) => QuotaFrom.TryGetValue(instanceId, out var s) ? s : null;

    public string InstanceName(int id) => Instances.FirstOrDefault(i => i.Id == id)?.Name ?? "—";

    /// <summary>Bytes each instance occupies, against its quota — the number that decides which of its
    /// backups the next upload will push out.
    /// <para>Summed over the whole table, NOT over <see cref="Items"/>: that list is filtered, and a
    /// usage figure that shrinks because somebody picked a filter would be a lie about how full the
    /// instance is.</para></summary>
    public Dictionary<int, long> UsedBytes { get; private set; } = new();

    public long UsedBy(int instanceId) => UsedBytes.TryGetValue(instanceId, out var u) ? u : 0;

    /// <summary>Percent of the quota in use, capped at 100 for the bar.</summary>
    public int UsedPercent(int instanceId)
    {
        var quota = QuotaFor(instanceId);
        if (quota <= 0) return 0;
        return (int)Math.Min(100, UsedBy(instanceId) * 100 / quota);
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };

    public async Task OnGetAsync(int? instance)
    {
        Instances = await _db.Instances.AsNoTracking().Include(i => i.Profile).OrderBy(i => i.Name).ToListAsync();

        var query = _db.CloudBackups.AsNoTracking().AsQueryable();
        if (instance is int iid && Instances.Any(i => i.Id == iid))
        {
            FilteredInstance = iid;
            query = query.Where(b => b.InstanceId == iid);
        }
        Items = await query.OrderByDescending(b => b.CreatedAt).ToListAsync();

        // Over everything, not over the filter: it answers "how much disk is this costing me".
        TotalBytes = await _db.CloudBackups.SumAsync(b => (long?)b.SizeBytes) ?? 0;
        // For every instance, not just the filtered one: the overview below lists all of them, and
        // "who is close to their limit" is the question this page exists to answer.
        foreach (var i in Instances)
        {
            QuotaBytes[i.Id] = await _store.QuotaBytesAsync(i.Id);
            QuotaFrom[i.Id] = i.Profile?.BackupQuotaGb is > 0 ? i.Profile.Name : null;
        }

        UsedBytes = await _db.CloudBackups.AsNoTracking()
            .GroupBy(b => b.InstanceId)
            .Select(g => new { g.Key, Sum = g.Sum(b => b.SizeBytes) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum);
        Orphans = await _store.FindOrphansAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, int? instance)
    {
        var row = await _db.CloudBackups.FirstOrDefaultAsync(b => b.Id == id);
        if (row is null) return RedirectToPage(new { instance });

        await _store.DeleteAsync(row);
        TempData["Flash"] = $"„{row.FileName}“ gelöscht.";
        return RedirectToPage(new { instance });
    }

    /// <summary>
    /// Asks the instance to restore this backup. The cloud only marks it — the site picks it up on
    /// its next heartbeat and does the work itself.
    /// <para>A previous outcome is cleared, so the request and its result always describe the same
    /// attempt rather than a new request wearing an old answer.</para>
    /// </summary>
    public async Task<IActionResult> OnPostRestoreAsync(int id, int? instance)
    {
        var row = await _db.CloudBackups.FirstOrDefaultAsync(b => b.Id == id);
        if (row is null) return RedirectToPage(new { instance });

        row.RestoreRequestedAt = DateTime.UtcNow;
        row.RestoreDoneAt = null;
        row.RestoreError = null;
        await _db.SaveChangesAsync();

        TempData["Flash"] = $"„{row.FileName}“ wird beim nächsten Kontakt der Instanz zurückgespielt.";
        return RedirectToPage(new { instance });
    }

    /// <summary>Takes back a request the instance has not picked up yet. Nothing to undo once it has.</summary>
    public async Task<IActionResult> OnPostCancelRestoreAsync(int id, int? instance)
    {
        var row = await _db.CloudBackups.FirstOrDefaultAsync(b => b.Id == id);
        if (row is null) return RedirectToPage(new { instance });

        row.RestoreRequestedAt = null;
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Anforderung zurückgenommen.";
        return RedirectToPage(new { instance });
    }

    public async Task<IActionResult> OnPostCleanOrphansAsync(int? instance)
    {
        var count = await _store.CleanOrphansAsync();
        TempData["Flash"] = count == 0 ? "Nichts aufzuräumen." : $"{count} verwaiste Einträge entfernt.";
        return RedirectToPage(new { instance });
    }

    public async Task<IActionResult> OnGetDownloadAsync(int id)
    {
        var row = await _db.CloudBackups.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
        if (row is null) return RedirectToPage();

        var path = _store.PathFor(row);
        if (!System.IO.File.Exists(path)) return RedirectToPage();
        // The result is built directly rather than through the File()/PhysicalFile() helpers: neither
        // offers range processing together with a download name, and a few hundred megabytes that
        // cannot resume after a dropped connection start again from zero.
        return new PhysicalFileResult(path, "application/zip")
        {
            FileDownloadName = row.FileName,
            EnableRangeProcessing = true,
        };
    }
}
