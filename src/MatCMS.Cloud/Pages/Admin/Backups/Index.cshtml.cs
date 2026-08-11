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

    /// <summary>How many backups each instance holds — the overview's other honest number: 8 GB in
    /// one file and 8 GB in forty are not the same situation.</summary>
    public Dictionary<int, int> CountBytes { get; private set; } = new();

    public long UsedBy(int instanceId) => UsedBytes.TryGetValue(instanceId, out var u) ? u : 0;

    public int CountFor(int instanceId) => CountBytes.TryGetValue(instanceId, out var c) ? c : 0;

    /// <summary>When each instance last put a backup here. The overview's most load-bearing number:
    /// a quota that is only half full says nothing about whether anything recent is in it.</summary>
    public Dictionary<int, DateTime> LastBackup { get; private set; } = new();

    public DateTime? LastBackupFor(int instanceId) =>
        LastBackup.TryGetValue(instanceId, out var d) ? d : null;

    /// <summary>
    /// How long ago, as a resource key plus its number — so the wording stays in the resource files
    /// like every other string, instead of German being baked into the page model.
    /// <para>Rough on purpose: the question behind the column is "is this current or stale", and an
    /// exact figure would only invite reading it as precision. Compared in UTC, because that is how
    /// the times are stored — converting first would make the answer depend on the server's time
    /// zone.</para>
    /// </summary>
    public static (string Key, int Value) Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        // Negative means the instance's clock runs ahead of ours. Not worth reporting as anything
        // other than "just now" — it is a fact about two clocks, not about the backup.
        if (span < TimeSpan.Zero) return ("backups.agoNow", 0);
        if (span.TotalMinutes < 60) return ("backups.agoMinutes", (int)span.TotalMinutes);
        if (span.TotalHours < 48) return ("backups.agoHours", (int)span.TotalHours);
        return ("backups.agoDays", (int)span.TotalDays);
    }

    /// <summary>
    /// Which tab opens. Decided on the server, not left to the deep-link script: otherwise every
    /// link into the list would paint the overview first and swap a moment later.
    /// <para>Filtering by an instance implies the list even without <c>?tab=</c> — that link comes
    /// from the overview, or from the instance page, and both mean "show me these backups".</para>
    /// </summary>
    public string ActiveTab { get; private set; } = "overview";

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

    public async Task OnGetAsync(int? instance, string? tab)
    {
        Instances = await _db.Instances.AsNoTracking().Include(i => i.Profile).OrderBy(i => i.Name).ToListAsync();

        var query = _db.CloudBackups.AsNoTracking().AsQueryable();
        if (instance is int iid && Instances.Any(i => i.Id == iid))
        {
            FilteredInstance = iid;
            query = query.Where(b => b.InstanceId == iid);
        }
        Items = await query.OrderByDescending(b => b.CreatedAt).ToListAsync();

        ActiveTab = tab == "list" || FilteredInstance is not null ? "list" : "overview";

        // Over everything, not over the filter: it answers "how much disk is this costing me".
        TotalBytes = await _db.CloudBackups.SumAsync(b => (long?)b.SizeBytes) ?? 0;
        // For every instance, not just the filtered one: the overview below lists all of them, and
        // "who is close to their limit" is the question this page exists to answer.
        foreach (var i in Instances)
        {
            QuotaBytes[i.Id] = await _store.QuotaBytesAsync(i.Id);
            QuotaFrom[i.Id] = i.Profile?.BackupQuotaGb is > 0 ? i.Profile.Name : null;
        }

        var perInstance = await _db.CloudBackups.AsNoTracking()
            .GroupBy(b => b.InstanceId)
            .Select(g => new { g.Key, Sum = g.Sum(b => b.SizeBytes), Count = g.Count(), Last = g.Max(b => b.CreatedAt) })
            .ToListAsync();
        UsedBytes = perInstance.ToDictionary(x => x.Key, x => x.Sum);
        CountBytes = perInstance.ToDictionary(x => x.Key, x => x.Count);
        LastBackup = perInstance.ToDictionary(x => x.Key, x => x.Last);
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

    public async Task<IActionResult> OnPostRestoreAsync(int id, int? instance)
    {
        var row = await _db.CloudBackups.FirstOrDefaultAsync(b => b.Id == id);
        if (row is null) return RedirectToPage(new { instance });

        await _store.RequestRestoreAsync(row);
        TempData["Flash"] = $"„{row.FileName}“ wird beim nächsten Kontakt der Instanz zurückgespielt.";
        return RedirectToPage(new { instance });
    }

    public async Task<IActionResult> OnPostCancelRestoreAsync(int id, int? instance)
    {
        var row = await _db.CloudBackups.FirstOrDefaultAsync(b => b.Id == id);
        if (row is null) return RedirectToPage(new { instance });

        await _store.CancelRestoreAsync(row);
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
