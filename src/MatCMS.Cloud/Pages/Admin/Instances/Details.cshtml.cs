using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Instances;

public class DetailsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly InstanceService _instances;
    private readonly ReleaseWatcher _releases;
    private readonly DockerHostService _docker;

    public DetailsModel(AppDbContext db, InstanceService instances, ReleaseWatcher releases, DockerHostService docker)
    {
        _db = db;
        _instances = instances;
        _releases = releases;
        _docker = docker;
    }

    public Instance Item { get; private set; } = new();
    public List<InstanceEvent> Events { get; private set; } = new();
    public List<Profile> Profiles { get; private set; } = new();

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
        return true;
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
}
