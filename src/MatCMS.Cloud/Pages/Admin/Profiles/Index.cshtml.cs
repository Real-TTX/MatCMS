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

    // Duplicating copies everything the profile rolls out into a new one with a fresh join code and no
    // assigned instances (see ProfileService.DuplicateAsync). Lands the operator in the copy to edit.
    public async Task<IActionResult> OnPostDuplicateAsync(int id)
    {
        if (!await _db.Profiles.AnyAsync(p => p.Id == id)) return RedirectToPage();
        var clone = await _profiles.DuplicateAsync(id);
        TempData["Flash"] = $"Profil dupliziert: \"{clone.Name}\". Es hat einen neuen Beitritts-Code und noch keine zugeordneten Instanzen.";
        return RedirectToPage("Edit", new { id = clone.Id });
    }

    // Making a profile the default is edited ON the profile (General tab), not as a row action —
    // per-record settings belong to the record's own page.
}
