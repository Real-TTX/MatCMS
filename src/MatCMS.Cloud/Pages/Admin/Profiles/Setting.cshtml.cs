using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Profiles;

/// <summary>Create/edit one pushed setting on its own page — same shape as every other payload item.</summary>
public class SettingModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;

    public SettingModel(AppDbContext db, ProfileService profiles)
    {
        _db = db;
        _profiles = profiles;
    }

    public Profile Owner { get; private set; } = new();
    public ProfileSetting Item { get; private set; } = new();
    public bool IsNew => Item.Id == 0;

    public async Task<IActionResult> OnGetAsync(int profileId, int? id)
    {
        var owner = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId);
        if (owner is null) return RedirectToPage("Index");
        Owner = owner;

        if (id is null) return Page();

        var item = await _db.ProfileSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.ProfileId == profileId);
        if (item is null) return RedirectToPage("Edit", new { id = profileId, tab = "settings" });
        Item = item;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int profileId, int? id, string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            TempData["FlashError"] = "Bitte einen Schlüssel angeben.";
            return RedirectToPage(new { profileId, id });
        }

        var trimmed = key.Trim();
        var row = id is null
            ? await _db.ProfileSettings.FirstOrDefaultAsync(s => s.ProfileId == profileId && s.Key == trimmed)
            : await _db.ProfileSettings.FirstOrDefaultAsync(s => s.Id == id && s.ProfileId == profileId);

        if (row is null)
        {
            row = new ProfileSetting { ProfileId = profileId };
            _db.ProfileSettings.Add(row);
        }
        // Renaming onto an identity another row already holds violates the unique index, which
        // surfaces as an unhandled DbUpdateException — a 500 instead of a readable message.
        else if (row.Key != trimmed
                 && await _db.ProfileSettings.AnyAsync(t => t.ProfileId == profileId && t.Key == trimmed && t.Id != row.Id))
        {
            TempData["FlashError"] = $"Der Schlüssel \"{trimmed}\" wird bereits verwendet.";
            return RedirectToPage(new { profileId, id });
        }

        row.Key = trimmed;
        row.Value = value;

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(profileId);
        TempData["Flash"] = $"Einstellung \"{row.Key}\" gespeichert.";
        return RedirectToPage(new { profileId, id = row.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int profileId, int id)
    {
        var row = await _db.ProfileSettings.FirstOrDefaultAsync(s => s.Id == id && s.ProfileId == profileId);
        if (row is not null)
        {
            _db.ProfileSettings.Remove(row);
            await _db.SaveChangesAsync();
            await _profiles.TouchAsync(profileId);
            TempData["Flash"] = "Einstellung aus dem Profil entfernt. Auf den Instanzen bleibt der Wert stehen.";
        }
        return RedirectToPage("Edit", new { id = profileId, tab = "settings" });
    }
}
