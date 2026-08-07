using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Store;

/// <summary>
/// Store setting editor: one MatCMS setting key rolled out to every profile that selected it.
/// <para>A setting marked as a secret is encrypted before it reaches the database and is never
/// rendered back into the form — the field stays blank and an empty submit keeps the stored value,
/// the same rule the SMTP forms follow.</para>
/// </summary>
public class SettingModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;
    private readonly SecretProtector _secrets;

    public SettingModel(AppDbContext db, ProfileService profiles, SecretProtector secrets)
    {
        _db = db;
        _profiles = profiles;
        _secrets = secrets;
    }

    public StoreSetting Item { get; private set; } = new();
    public bool IsNew => Item.Id == 0;
    public List<string> UsedBy { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null) return Page();

        var item = await _db.StoreSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (item is null) return RedirectToPage("Index");

        Item = item;
        UsedBy = await _db.ProfileStoreSettings.AsNoTracking()
            .Where(x => x.StoreSettingId == item.Id).Select(x => x.Profile!.Name).ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id, string? key, string? value, bool isSecret)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            TempData["FlashError"] = "Bitte einen Schlüssel angeben.";
            return RedirectToPage(new { id });
        }

        var trimmed = key.Trim();
        var row = id is null
            ? await _db.StoreSettings.FirstOrDefaultAsync(s => s.Key == trimmed)
            : await _db.StoreSettings.FirstOrDefaultAsync(s => s.Id == id);

        if (row is null)
        {
            row = new StoreSetting();
            _db.StoreSettings.Add(row);
        }

        row.Key = trimmed;
        row.IsSecret = isSecret;

        // A secret field is rendered blank, so an empty submit must mean "keep what is stored" —
        // otherwise opening the page and saving would silently wipe the value.
        if (isSecret)
        {
            if (!string.IsNullOrEmpty(value)) row.Value = _secrets.Protect(value);
        }
        else
        {
            // Switching a secret back to a plain setting: the old ciphertext is meaningless as a
            // plain value, so it is replaced rather than left behind unreadable.
            row.Value = value;
        }

        await _db.SaveChangesAsync();
        await TouchUsersAsync(row.Id);
        TempData["Flash"] = $"Einstellung \"{row.Key}\" im Store gespeichert.";
        return RedirectToPage(new { id = row.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var row = await _db.StoreSettings.FirstOrDefaultAsync(s => s.Id == id);
        if (row is not null)
        {
            var affected = await _db.ProfileStoreSettings.Where(x => x.StoreSettingId == id)
                .Select(x => x.ProfileId).Distinct().ToListAsync();
            _db.StoreSettings.Remove(row);
            await _db.SaveChangesAsync();
            foreach (var profileId in affected) await _profiles.TouchAsync(profileId);
            TempData["Flash"] = "Einstellung aus dem Store entfernt. Auf den Instanzen bleibt der Wert stehen.";
        }
        return RedirectToPage("Index");
    }

    /// <summary>Bumps every profile that selected this entry, so their instances pull the change.</summary>
    private async Task TouchUsersAsync(int storeSettingId)
    {
        var profileIds = await _db.ProfileStoreSettings.AsNoTracking()
            .Where(x => x.StoreSettingId == storeSettingId).Select(x => x.ProfileId).Distinct().ToListAsync();
        foreach (var profileId in profileIds) await _profiles.TouchAsync(profileId);
    }
}
