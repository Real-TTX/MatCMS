using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Profiles;

/// <summary>
/// The machine-translation credentials this profile rolls out.
/// <para>Its own page, like the mail configuration, because it is a GROUP: three values that only
/// make sense together, and credentials that must not reach a site because somebody once typed one
/// of them in. Which LANGUAGES a site offers is deliberately not here — that is a decision about
/// content, and switching it from the cloud would turn languages on and off underneath pages
/// written in them.</para>
/// </summary>
public class TranslationModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;
    private readonly SecretProtector _secrets;

    public TranslationModel(AppDbContext db, ProfileService profiles, SecretProtector secrets)
    {
        _db = db; _profiles = profiles; _secrets = secrets;
    }

    public const string ProviderKey = "translate.provider";
    public const string ApiKeyKey = "translate.apiKey";
    public const string UrlKey = "translate.url";

    public Profile Owner { get; private set; } = new();
    public List<ProfileSetting> Settings { get; private set; } = [];

    public string Setting(string key) => Settings.FirstOrDefault(s => s.Key == key)?.Value ?? "";

    /// <summary>True while the profile does not roll this out yet — nothing is in effect until saved.</summary>
    public bool IsNew { get; private set; }

    public async Task<IActionResult> OnGetAsync(int profileId)
    {
        if (!await LoadAsync(profileId)) return RedirectToPage("Index");
        IsNew = !Owner.SyncTranslation;
        return Page();
    }

    private async Task<bool> LoadAsync(int profileId)
    {
        var owner = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId);
        if (owner is null) return false;
        Owner = owner;
        Settings = await _db.ProfileSettings.AsNoTracking().Where(s => s.ProfileId == profileId).ToListAsync();
        return true;
    }

    public async Task<IActionResult> OnPostAsync(
        int profileId, string? provider, string? apiKey, string? url, bool clearApiKey)
    {
        var profile = await _db.Profiles.FindAsync(profileId);
        if (profile is null) return RedirectToPage("Index");

        // Being on this page at all means the profile rolls this out; the switch that decides THAT is
        // the add dialog and the row's delete, not a checkbox halfway down a form.
        profile.SyncTranslation = true;

        await UpsertAsync(profileId, ProviderKey, provider?.Trim());
        await UpsertAsync(profileId, UrlKey, url?.Trim());

        // Encrypted before it ever reaches the database, and an empty field KEEPS the stored key —
        // the box is rendered blank on purpose, so saving the form must not wipe the secret. The
        // explicit tick is the only way to clear it.
        if (clearApiKey)
            await UpsertAsync(profileId, ApiKeyKey, "", secret: true);
        else if (!string.IsNullOrEmpty(apiKey))
            await UpsertAsync(profileId, ApiKeyKey, _secrets.Protect(apiKey), secret: true);

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(profileId);
        TempData["Flash"] = "Übersetzungs-Zugang gespeichert.";
        return RedirectToPage("Edit", new { id = profileId, tab = "settings" });
    }

    /// <summary>Stops rolling the credentials out. The stored values survive — an operator who takes
    /// translation out of a profile has not asked to throw the account away.</summary>
    public async Task<IActionResult> OnPostRemoveAsync(int profileId)
    {
        var profile = await _db.Profiles.FindAsync(profileId);
        if (profile is null) return RedirectToPage("Index");

        profile.SyncTranslation = false;
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(profileId);
        TempData["Flash"] = "Übersetzungs-Zugang wird von diesem Profil nicht mehr ausgerollt.";
        return RedirectToPage("Edit", new { id = profileId, tab = "settings" });
    }

    private async Task UpsertAsync(int profileId, string key, string? value, bool secret = false)
    {
        var row = await _db.ProfileSettings.FirstOrDefaultAsync(s => s.ProfileId == profileId && s.Key == key);
        if (row is null)
        {
            row = new ProfileSetting { ProfileId = profileId, Key = key };
            _db.ProfileSettings.Add(row);
        }
        row.Value = value ?? "";
        row.IsSecret = secret;
    }
}
