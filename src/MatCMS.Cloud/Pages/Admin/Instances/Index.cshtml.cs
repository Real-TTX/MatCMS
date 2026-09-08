using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Instances;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ReleaseWatcher _releases;
    private readonly CloudContext _cloud;
    private readonly OperatorScope _scope;

    /// <summary>Ob die Cloud selbst Instanzen anlegen darf — entscheidet, ob der Knopf dafür
    /// überhaupt erscheint. Ein Operator darf ohnehin keine anlegen.</summary>
    public bool HostingEnabled => _scope.IsAdmin && _cloud.Flag(SettingKeys.HostingEnabled);

    /// <summary>An Operator only manages assigned instances — no create/delete/fleet actions.</summary>
    public bool IsAdmin => _scope.IsAdmin;

    public IndexModel(AppDbContext db, ReleaseWatcher releases, CloudContext cloud, OperatorScope scope)
    {
        _db = db;
        _releases = releases;
        _cloud = cloud;
        _scope = scope;
    }

    public List<Instance> Items { get; private set; } = new();
    public string? LatestVersion => _releases.LatestVersion;

    public bool HasUpdate(Instance i) => _releases.IsUpdateAvailableFor(i.Version);

    /// <summary>Profile the list is narrowed to, or null for all. Set from the profile page, which
    /// links here instead of listing its instances itself.</summary>
    public Profile? FilteredProfile { get; private set; }

    /// <summary>Everything the dropdown offers. The UNFILTERED list, so the control can always take
    /// you somewhere else instead of only ever narrowing further.</summary>
    public List<Profile> AllProfiles { get; private set; } = new();

    public async Task OnGetAsync(int? profile = null)
    {
        // The profile filter is an admin tool; an Operator doesn't browse the fleet by profile.
        AllProfiles = _scope.IsAdmin
            ? await _db.Profiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync()
            : new();

        var query = _db.Instances.AsNoTracking().Include(i => i.Profile).AsQueryable();

        // Operators see ONLY their assigned instances — the one place this is enforced for the list.
        if (!_scope.IsAdmin)
        {
            var allowed = await _scope.AllowedInstanceIdsAsync();
            query = query.Where(i => allowed.Contains(i.Id));
        }

        if (_scope.IsAdmin && profile is int pid)
        {
            FilteredProfile = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid);
            // An unknown id narrows to nothing rather than silently showing everything — otherwise a
            // stale link would look like the profile has every instance.
            query = query.Where(i => i.ProfileId == pid);
        }
        Items = await query.OrderBy(i => i.Name).ToListAsync();
    }

    public static bool IsOutOfSync(Instance i) => InstanceService.IsOutOfSync(i);

    /// <summary>Items the instance reported as failed. Shown even when the revision matches — a
    /// template that could not be activated does not abort the apply, so without this the row would
    /// read "synchron" while something never arrived.</summary>
    public static int FailedItems(Instance i) => InstanceService.Summarise(i.LastSyncReportJson).Failed;

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        // Removing an instance is admin-only (the dedicated Delete page is too); block the bare handler.
        if (!_scope.IsAdmin) return Forbid();
        var instance = await _db.Instances.FindAsync(id);
        if (instance is null) return RedirectToPage();

        _db.Instances.Remove(instance);
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Instanz \"{instance.Name}\" entfernt.";
        return RedirectToPage();
    }
}
