using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using MatCMS.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace MatCMS.Cloud.Pages.Admin.Instances;

public class DetailsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly InstanceService _instances;
    private readonly ReleaseWatcher _releases;
    private readonly DockerHostService _docker;
    private readonly BackupStore _backups;

    public DetailsModel(AppDbContext db, InstanceService instances, ReleaseWatcher releases, DockerHostService docker, BackupStore backups)
    {
        _db = db;
        _instances = instances;
        _releases = releases;
        _docker = docker;
        _backups = backups;
    }

    /// <summary>What this instance occupies in the cloud, and what it is granted — formatted here
    /// rather than in the view so the page and the backup overview cannot disagree about a size.</summary>
    public string BackupUsed { get; private set; } = "0 B";
    public string BackupQuota { get; private set; } = "—";

    /// <summary>Name of the profile that raised the quota, or null when it is the cloud default.</summary>
    public string? BackupQuotaFromProfile { get; private set; }

    public int BackupPercent { get; private set; }

    /// <summary>This instance's backups, newest first.</summary>
    public List<CloudBackup> Backups { get; private set; } = new();

    public Instance Item { get; private set; } = new();
    public List<InstanceEvent> Events { get; private set; } = new();
    public List<Profile> Profiles { get; private set; } = new();

    /// <summary>What the instance said it did with the last configuration, item by item. The cloud
    /// derives nothing from it — an instance that predates the report simply sends none, which is why
    /// an empty list must read as "no information", not as "nothing was applied".</summary>
    public List<SyncItemReport> SyncReport { get; private set; } = new();

    /// <summary>The same report counted up, for the state badge.</summary>
    public InstanceService.SyncSummary Summary { get; private set; } = new(0, 0, 0, 0);

    /// <summary>Completed applies, newest first — one row per run as the instance reported it.</summary>
    public List<InstanceSyncRun> Runs { get; private set; } = new();

    /// <summary>What this instance handed to the cloud for delivery, newest first. Bodies are left
    /// in the database: the list wants subject, recipients and outcome, and a page that dragged
    /// every message body through memory to show none of them would be pure waste.</summary>
    public List<SpooledMail> Spool { get; private set; } = new();

    /// <summary>True when this instance's profile actually routes mail through the cloud — the tab
    /// then explains itself instead of showing an empty table nobody asked for.</summary>
    public bool UsesRelay => Item.Profile is { SyncSmtp: true } p && p.MailSource == MailSources.Cloud;


    public bool OutOfSync => InstanceService.IsOutOfSync(Item);

    /// <summary>Token shown once after a rotation (never stored in the clear).</summary>
    public string? NewToken { get; private set; }

    public string? LatestVersion => _releases.LatestVersion;
    public bool HasUpdate => _releases.IsUpdateAvailableFor(Item.Version);
    public bool Online => InstanceService.IsOnline(Item);
    public bool CanCloudUpdate => Item.Hosting == InstanceHosting.Local && Item.ContainerId is not null;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id)) return RedirectToPage("Index");

        Backups = await _db.CloudBackups.AsNoTracking()
            .Where(b => b.InstanceId == id)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        var used = Backups.Sum(b => b.SizeBytes);
        var quota = await _backups.QuotaBytesAsync(id);
        BackupUsed = Pages.Admin.Backups.IndexModel.Size(used);
        BackupQuota = Pages.Admin.Backups.IndexModel.Size(quota);
        BackupPercent = quota > 0 ? (int)Math.Min(100, used * 100 / quota) : 0;
        BackupQuotaFromProfile = Item.Profile?.BackupQuotaGb is > 0 ? Item.Profile.Name : null;
        return Page();
    }

    private async Task<bool> LoadAsync(int id)
    {
        var item = await _db.Instances.Include(i => i.Profile).FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return false;
        Item = item;
        Events = await _db.InstanceEvents.AsNoTracking()
            .Where(e => e.InstanceId == id)
            .OrderByDescending(e => e.CreatedAt)
            .Take(50)
            .ToListAsync();
        Profiles = await _db.Profiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        Spool = await _db.SpooledMails.AsNoTracking()
            .Where(m => m.InstanceId == id)
            .OrderByDescending(m => m.Id)
            .Take(50)
            .Select(m => new SpooledMail
            {
                Id = m.Id, QueuedAt = m.QueuedAt, Recipients = m.Recipients, Subject = m.Subject,
                Status = m.Status, Attempts = m.Attempts, NextAttemptAt = m.NextAttemptAt,
                SentAt = m.SentAt, LastError = m.LastError
            })
            .ToListAsync();
        SyncReport = ParseReport(item.LastSyncReportJson);
        Summary = InstanceService.Summarise(item.LastSyncReportJson);
        // The report body is not loaded for the listing — the counts are denormalised on the row
        // precisely so a history of 50 runs does not drag 50 JSON blobs through memory.
        Runs = await _db.InstanceSyncRuns.AsNoTracking()
            .Where(r => r.InstanceId == id)
            .OrderByDescending(r => r.RanAt)
            .Select(r => new InstanceSyncRun
            {
                Id = r.Id, RanAt = r.RanAt, Revision = r.Revision, Error = r.Error,
                Installed = r.Installed, Updated = r.Updated, Skipped = r.Skipped, Failed = r.Failed
            })
            .ToListAsync();
        return true;
    }

    /// <summary>Never throws: the report is foreign input from an instance that may run a newer or
    /// broken build, and a malformed one must not take the whole details page down.</summary>
    private static List<SyncItemReport> ParseReport(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<SyncItemReport>>(json) ?? new(); }
        catch { return new(); }
    }

    // --- Backups ---------------------------------------------------------------------------------
    // The same three actions the backup list offers, on the instance itself. They call BackupStore
    // rather than repeating what a restore mark or a delete means: two pages, one implementation.
    // Each returns to THIS page's backup tab, because that is where the operator was standing.

    /// <summary>Looks the backup up WITHIN this instance. Taking the id on its own would let a wrong
    /// or crafted id act on another instance's backup from a page that claims to be about this one.</summary>
    private Task<CloudBackup?> OwnBackupAsync(int instanceId, int backupId) =>
        _db.CloudBackups.FirstOrDefaultAsync(b => b.Id == backupId && b.InstanceId == instanceId);

    public async Task<IActionResult> OnPostRestoreAsync(int id, int backupId)
    {
        var row = await OwnBackupAsync(id, backupId);
        if (row is null) return RedirectToPage(new { id, tab = "backup" });

        await _backups.RequestRestoreAsync(row);
        TempData["Flash"] = $"„{row.FileName}“ wird beim nächsten Kontakt der Instanz zurückgespielt.";
        return RedirectToPage(new { id, tab = "backup" });
    }

    public async Task<IActionResult> OnPostCancelRestoreAsync(int id, int backupId)
    {
        var row = await OwnBackupAsync(id, backupId);
        if (row is null) return RedirectToPage(new { id, tab = "backup" });

        await _backups.CancelRestoreAsync(row);
        TempData["Flash"] = "Anforderung zurückgenommen.";
        return RedirectToPage(new { id, tab = "backup" });
    }

    public async Task<IActionResult> OnPostDeleteBackupAsync(int id, int backupId)
    {
        var row = await OwnBackupAsync(id, backupId);
        if (row is null) return RedirectToPage(new { id, tab = "backup" });

        await _backups.DeleteAsync(row);
        TempData["Flash"] = $"„{row.FileName}“ gelöscht.";
        return RedirectToPage(new { id, tab = "backup" });
    }

    public async Task<IActionResult> OnPostApproveAsync(int id)
    {
        var item = await _db.Instances.FindAsync(id);
        if (item is null) return RedirectToPage("Index");

        await _instances.SetStatusAsync(item, InstanceStatus.Approved);
        TempData["Flash"] = "Instanz freigegeben.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRejectAsync(int id)
    {
        var item = await _db.Instances.FindAsync(id);
        if (item is null) return RedirectToPage("Index");

        await _instances.SetStatusAsync(item, InstanceStatus.Rejected);
        TempData["Flash"] = "Instanz abgelehnt.";
        return RedirectToPage(new { id });
    }

    /// <summary>Moves the instance to another profile. The applied revision is reset so the instance
    /// pulls the new profile's configuration on its next beat even if the revision numbers happen to
    /// line up — two profiles' revisions are unrelated counters.</summary>
    public async Task<IActionResult> OnPostAssignProfileAsync(int id, int? profileId)
    {
        var item = await _db.Instances.FindAsync(id);
        if (item is null) return RedirectToPage("Index");

        if (item.ProfileId != profileId)
        {
            item.ProfileId = profileId;
            item.AppliedRevision = 0;
            item.LastSyncError = null;
            var name = profileId is null
                ? null
                : (await _db.Profiles.FindAsync(profileId.Value))?.Name;
            _instances.Log(item, InstanceEventKind.SyncApplied,
                name is null ? "Profil entfernt — es wird keine Konfiguration mehr ausgerollt." : $"Profil \"{name}\" zugewiesen.",
                notified: true);
            await _db.SaveChangesAsync();
        }

        TempData["Flash"] = "Profil zugewiesen.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, string name, string? url, string? notes)
    {
        var item = await _db.Instances.FindAsync(id);
        if (item is null) return RedirectToPage("Index");

        item.Name = string.IsNullOrWhiteSpace(name) ? item.Name : name.Trim();
        item.Url = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        item.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Gespeichert.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRotateAsync(int id)
    {
        var item = await _db.Instances.FindAsync(id);
        if (item is null) return RedirectToPage("Index");

        NewToken = await _instances.RotateTokenAsync(item);
        await LoadAsync(id);
        TempData["FlashError"] = "Der alte Token gilt nicht mehr — die Instanz meldet sich erst wieder, wenn der neue eingetragen ist.";
        return Page();
    }

    /// <summary>Runs the update for a LOCAL instance: pull + recreate the container. Blocking on
    /// purpose — the operator clicked it and wants the outcome, not a fire-and-forget.</summary>
    public async Task<IActionResult> OnPostUpdateAsync(int id)
    {
        var item = await _db.Instances.FindAsync(id);
        if (item is null) return RedirectToPage("Index");

        if (item.Hosting != InstanceHosting.Local || item.ContainerId is null)
        {
            TempData["FlashError"] = "Diese Instanz läuft nicht auf diesem Docker-Host — Update dort ausführen.";
            return RedirectToPage(new { id });
        }

        _instances.Log(item, InstanceEventKind.UpdateStarted, "Update über die Cloud gestartet.", notified: true);
        await _db.SaveChangesAsync();

        var result = await _docker.UpdateContainerAsync(item.ContainerId, HttpContext.RequestAborted);
        _instances.Log(item,
            result.Ok ? InstanceEventKind.UpdateSucceeded : InstanceEventKind.UpdateFailed,
            result.Message, notified: true);
        await _db.SaveChangesAsync();

        if (result.Ok) TempData["Flash"] = result.Message;
        else TempData["FlashError"] = result.Message;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var item = await _db.Instances.FindAsync(id);
        if (item is null) return RedirectToPage("Index");

        _db.Instances.Remove(item);
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Instanz \"{item.Name}\" entfernt.";
        return RedirectToPage("Index");
    }
    /// <summary>
    /// Puts given-up messages back in the queue. An operator who just fixed the mail server wants the
    /// backlog delivered, not a clean slate — so the attempt counter is reset and the worker picks
    /// them up on its next pass.
    /// </summary>
    public async Task<IActionResult> OnPostRetryMailAsync(int id, int? mailId)
    {
        var query = _db.SpooledMails.Where(m => m.InstanceId == id && m.Status == SpoolStatus.Failed);
        if (mailId is int one) query = query.Where(m => m.Id == one);

        var rows = await query.ToListAsync();
        foreach (var m in rows)
        {
            m.Status = SpoolStatus.Queued;
            m.Attempts = 0;
            m.NextAttemptAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        TempData["Flash"] = rows.Count == 0
            ? "Nichts erneut zu versuchen."
            : $"{rows.Count} Nachricht(en) zurück in die Warteschlange.";
        return RedirectToPage(new { id, tab = "mail" });
    }

}
