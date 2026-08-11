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
    public int? FilteredInstance { get; private set; }

    public string InstanceName(int id) => Instances.FirstOrDefault(i => i.Id == id)?.Name ?? "—";

    /// <summary>Bytes this instance occupies, against its quota — the number that decides which of its
    /// backups the next upload will push out.</summary>
    public long UsedBy(int instanceId) => Items.Where(b => b.InstanceId == instanceId).Sum(b => b.SizeBytes);

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };

    public async Task OnGetAsync(int? instance)
    {
        Instances = await _db.Instances.AsNoTracking().OrderBy(i => i.Name).ToListAsync();

        var query = _db.CloudBackups.AsNoTracking().AsQueryable();
        if (instance is int iid && Instances.Any(i => i.Id == iid))
        {
            FilteredInstance = iid;
            query = query.Where(b => b.InstanceId == iid);
        }
        Items = await query.OrderByDescending(b => b.CreatedAt).ToListAsync();

        // Over everything, not over the filter: it answers "how much disk is this costing me".
        TotalBytes = await _db.CloudBackups.SumAsync(b => (long?)b.SizeBytes) ?? 0;
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
