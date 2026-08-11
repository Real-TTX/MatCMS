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

    /// <summary>Dropdown value that means "I want to type my own key". Not the empty string, so that
    /// an unanswered dropdown and a deliberate "own key" stay distinguishable.</summary>
    public const string CustomMarker = "__custom";

    public Profile Owner { get; private set; } = new();
    public ProfileSetting Item { get; private set; } = new();
    public bool IsNew => Item.Id == 0;

    /// <summary>
    /// What may still be picked: the catalogue minus everything this profile already has.
    /// <para>A key that is already in the list would collide with the unique index and be refused on
    /// save — offering it means offering an action that can only fail. The row being EDITED keeps its
    /// own key in the list, or the field would come up empty on its own record.</para>
    /// </summary>
    public List<InstanceSettingCatalog.Group> Available { get; private set; } = [];

    /// <summary>True when the stored key is not one the catalogue knows — an older row, or something
    /// typed by hand. The form then opens on the free-text field instead of silently presenting a
    /// dropdown that does not contain the value it is showing.</summary>
    public bool IsCustomKey => !string.IsNullOrEmpty(Item.Key) && InstanceSettingCatalog.Find(Item.Key) is null;

    public async Task<IActionResult> OnGetAsync(int profileId, int? id)
    {
        var owner = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId);
        if (owner is null) return RedirectToPage("Index");
        Owner = owner;

        if (id is not null)
        {
            var item = await _db.ProfileSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.ProfileId == profileId);
            if (item is null) return RedirectToPage("Edit", new { id = profileId, tab = "settings" });
            Item = item;
        }

        await BuildChoicesAsync(profileId);
        return Page();
    }

    private async Task BuildChoicesAsync(int profileId)
    {
        var taken = await _db.ProfileSettings.AsNoTracking()
            .Where(s => s.ProfileId == profileId && s.Key != Item.Key)
            .Select(s => s.Key)
            .ToListAsync();
        var takenSet = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Available = InstanceSettingCatalog.Groups
            .Select(g => new InstanceSettingCatalog.Group(g.Name, g.Entries.Where(e => !takenSet.Contains(e.Key)).ToList()))
            .Where(g => g.Entries.Count > 0)
            .ToList();
    }

    /// <param name="keyPick">The chosen catalogue key, or <c>CustomMarker</c> for "own key".</param>
    /// <param name="customKey">Only read when the dropdown says so — so a value left in the text
    /// field cannot override a key the operator picked from the list.</param>
    public async Task<IActionResult> OnPostAsync(int profileId, int? id, string? keyPick, string? customKey, string? value)
    {
        var key = keyPick == CustomMarker || string.IsNullOrWhiteSpace(keyPick) ? customKey : keyPick;
        if (string.IsNullOrWhiteSpace(key))
        {
            TempData["FlashError"] = "Bitte einen Schlüssel angeben.";
            return RedirectToPage(new { profileId, id });
        }

        var trimmed = key.Trim();

        // A key that belongs to a settings group is refused here rather than stored: the rollout skips
        // those rows unless their group is switched on, so it would sit in the list looking active and
        // never arrive anywhere.
        if (ProfileService.IsGroupKey(trimmed))
        {
            TempData["FlashError"] = $"\"{trimmed}\" gehört zu einer eigenen Gruppe — bitte über deren Eintrag im Hinzufügen-Menü setzen.";
            return RedirectToPage(new { profileId, id });
        }
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
