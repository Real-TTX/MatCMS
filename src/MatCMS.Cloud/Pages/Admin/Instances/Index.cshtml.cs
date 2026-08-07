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

    public async Task OnGetAsync()
    {
        Items = await _db.Instances.AsNoTracking().Include(i => i.Profile).OrderBy(i => i.Name).ToListAsync();
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
