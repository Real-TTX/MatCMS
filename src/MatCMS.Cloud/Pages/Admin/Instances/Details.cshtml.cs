using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using MatCMS.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace MatCMS.Cloud.Pages.Admin.Instances;

// A backup ZIP with media runs to hundreds of megabytes, well past the framework's 128 MB
// multipart default and Kestrel's 30 MB body cap. Both are lifted to BackupStore.MaxUploadBytes so
// the manual upload below can accept the same size the streaming guard already enforces; the other
// handlers on this page post tiny forms, to which a higher ceiling makes no difference.
[RequestSizeLimit(BackupStore.MaxUploadBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = BackupStore.MaxUploadBytes)]
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

    /// <summary>The backup that answers the outstanding request, if one has arrived. Shown instead of
    /// "we asked", because those are two different facts and only one of them means the data is
    /// safe.</summary>
    public CloudBackup? RequestedBackup { get; private set; }

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

    /// <param name="view">Set only by the view toggle. Any other way in here leaves the
    /// remembered choice alone — otherwise opening one instance from a list would silently
    /// decide how every later one opens.</param>
    public async Task<IActionResult> OnGetAsync(int id, string? view = null)
    {
        if (view is not null) ContextSwitcher.Remember(HttpContext, view);
        if (!await LoadAsync(id)) return RedirectToPage("Index");

        if (Item.BackupRequestId > 0)
            RequestedBackup = await _db.CloudBackups.AsNoTracking()
                .Where(b => b.InstanceId == Item.Id && b.RequestId == Item.BackupRequestId)
                .OrderByDescending(b => b.UploadedAt)
                .FirstOrDefaultAsync();

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
    /// <summary>
    /// Asks the instance for a fresh backup, here and now. Independent of any removal: the cloud
    /// holding the disk should be able to fetch a copy when it wants one, and the removal way is a
    /// USER of this rather than the only reason for it.
    /// <para>Nothing is removed, nothing is scheduled — the request stands on its own and the answer
    /// simply appears in the backup list.</para>
    /// </summary>
    public async Task<IActionResult> OnPostRequestBackupAsync(int id)
    {
        var item = await _db.Instances.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return RedirectToPage("Index");

        if (item.Status != InstanceStatus.Approved)
        {
            TempData["FlashError"] = "Eine nicht freigegebene Instanz wird nicht um ein Backup gebeten.";
            return RedirectToPage(new { id });
        }
        // A request that is already outstanding is left alone. Bumping it would make the instance
        // start over, and the work already under way would answer an id nobody waits for any more.
        if (item.BackupRequestId > 0 && item.BackupRequestError is null && item.RemovalPending)
        {
            TempData["FlashError"] = "Für diese Instanz läuft bereits eine Anforderung.";
            return RedirectToPage(new { id });
        }

        await _instances.RequestBackupAsync(item);
        _instances.Log(item, InstanceEventKind.BackupRequested,
            "Backup in der Cloud angefordert — die Instanz holt es beim nächsten Kontakt.");
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Backup angefordert. Es trifft beim nächsten Kontakt der Instanz ein.";
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Takes a backup ZIP the operator uploads from their own machine and stores it as one of THIS
    /// instance's backups — streamed to disk by <see cref="BackupStore.StoreAsync"/> exactly like an
    /// instance-pushed one, so it is restorable by the very same path and no second format exists.
    /// <para>With <paramref name="restoreNow"/> it is also marked for restore straight away: the
    /// instance downloads and applies it on its next heartbeat, keeping its own cloud link (the
    /// importer preserves the <c>cloud.*</c> keys), so uploading a foreign site's backup cannot hand
    /// this container another identity.</para>
    /// </summary>
    public async Task<IActionResult> OnPostUploadBackupAsync(int id, IFormFile? file, bool restoreNow)
    {
        var item = await _db.Instances.Include(i => i.Profile).FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return RedirectToPage("Index");

        if (file is null || file.Length == 0)
        {
            TempData["FlashError"] = "Keine Datei ausgewählt.";
            return RedirectToPage(new { id, tab = "backup" });
        }

        // The real size guard is in StoreAsync, which streams and stops at MaxUploadBytes rather than
        // trusting a Content-Length; the page attributes only stop the request being rejected earlier.
        await using var stream = file.OpenReadStream();
        var result = await _backups.StoreAsync(item, file.FileName, "upload", DateTime.UtcNow, stream,
            HttpContext.RequestAborted);

        if (!result.Ok || result.Backup is null)
        {
            TempData["FlashError"] = result.Error ?? "Upload fehlgeschlagen.";
            return RedirectToPage(new { id, tab = "backup" });
        }

        _instances.Log(item, InstanceEventKind.BackupUploaded,
            $"Backup \"{result.Backup.FileName}\" über die Cloud hochgeladen.");
        await _db.SaveChangesAsync();

        if (restoreNow)
        {
            // Marking a restore is offered to an Approved instance only — for any other status the
            // heartbeat never hands the pending restore out. The mark is harmless meanwhile and is
            // picked up once the instance is approved, so we set it and say so rather than refuse.
            await _backups.RequestRestoreAsync(result.Backup);
            TempData["Flash"] = item.Status == InstanceStatus.Approved
                ? $"„{result.Backup.FileName}“ hochgeladen und wird beim nächsten Kontakt der Instanz zurückgespielt."
                : $"„{result.Backup.FileName}“ hochgeladen und zum Einspielen vorgemerkt — es wird erst nach Freigabe der Instanz zurückgespielt.";
        }
        else
        {
            TempData["Flash"] = $"„{result.Backup.FileName}“ hochgeladen. Zum Einspielen „Zurückspielen“ in der Liste wählen.";
        }
        return RedirectToPage(new { id, tab = "backup" });
    }

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

        // The container was just recreated on the new image but has not beaten back yet, so its
        // reported Version is still the OLD one — which left "Update verfügbar" (and the button)
        // standing until the next heartbeat, inviting a second, pointless update. Move the version
        // forward optimistically so the badge clears at once; the next heartbeat reports the real
        // version and corrects this if anything went wrong.
        if (result.Ok && _releases.LatestVersion is string latest)
            item.Version = latest;
        await _db.SaveChangesAsync();

        if (result.Ok) TempData["Flash"] = result.Message;
        else TempData["FlashError"] = result.Message;
        return RedirectToPage(new { id });
    }

    // Das frühere OnPostDelete ist absichtlich weg. Es löschte nur die Zeile — der Container lief
    // weiter, kannte seine Cloud noch und hinterließ Backup-Dateien ohne Datensatz. Vor allem aber
    // war es ein zweiter, ungefragter Löschweg neben dem, der die drei Ausgänge auseinanderhält.
    // Entfernen läuft ausschließlich über Instances/Delete.
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
