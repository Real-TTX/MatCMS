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

    public IndexModel(AppDbContext db, ReleaseWatcher releases)
    {
        _db = db;
        _releases = releases;
    }

    public List<Instance> Items { get; private set; } = new();
    public string? LatestVersion => _releases.LatestVersion;

    public bool HasUpdate(Instance i) => _releases.IsUpdateAvailableFor(i.Version);

    /// <summary>Profile the list is narrowed to, or null for all. Set from the profile page, which
    /// links here instead of listing its instances itself.</summary>
    public Profile? FilteredProfile { get; private set; }

    /// <summary>Id the instance dropdown is set to, or null for all.</summary>
    public int? FilteredInstance { get; private set; }

    /// <summary>Everything the dropdown offers — the UNFILTERED list, so the control can always take
    /// you somewhere else instead of only ever narrowing further.</summary>
    public List<Instance> AllInstances { get; private set; } = new();

    public async Task OnGetAsync(int? profile = null, int? instance = null)
    {
        AllInstances = await _db.Instances.AsNoTracking().OrderBy(i => i.Name).ToListAsync();

        var query = _db.Instances.AsNoTracking().Include(i => i.Profile).AsQueryable();
        if (instance is int iid && AllInstances.Any(i => i.Id == iid))
        {
            FilteredInstance = iid;
            query = query.Where(i => i.Id == iid);
        }
        if (profile is int pid)
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
        var instance = await _db.Instances.FindAsync(id);
        if (instance is null) return RedirectToPage();

        _db.Instances.Remove(instance);
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Instanz \"{instance.Name}\" entfernt.";
        return RedirectToPage();
    }
}
