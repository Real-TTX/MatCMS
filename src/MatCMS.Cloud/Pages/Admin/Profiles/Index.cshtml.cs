using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Profiles;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;

    public IndexModel(AppDbContext db, ProfileService profiles)
    {
        _db = db;
        _profiles = profiles;
    }

    public List<Profile> Items { get; private set; } = new();

    /// <summary>Instance count per profile — an operator deleting a profile should see what it costs.</summary>
    public Dictionary<int, int> InstanceCounts { get; private set; } = new();

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        Items = await _db.Profiles.AsNoTracking()
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync();
        InstanceCounts = await _db.Instances.Where(i => i.ProfileId != null)
            .GroupBy(i => i.ProfileId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var profile = await _db.Profiles.FindAsync(id);
        if (profile is null) return RedirectToPage();

        // Assigned instances survive with ProfileId = null (see the DbContext relation) — they just
        // stop receiving configuration. Losing a profile must never orphan a running site.
        _db.Profiles.Remove(profile);
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Profil \"{profile.Name}\" gelöscht. Zugeordnete Instanzen bleiben bestehen, erhalten aber keine Konfiguration mehr.";
        return RedirectToPage();
    }

    // Making a profile the default is edited ON the profile (General tab), not as a row action —
    // per-record settings belong to the record's own page.
}
