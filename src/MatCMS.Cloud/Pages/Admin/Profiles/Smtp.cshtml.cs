using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Profiles;

/// <summary>
/// The profile's mail configuration, on its own page.
/// <para>It used to be a card permanently open on the profile's Einstellungen tab, which made that
/// one tab behave unlike every other: the others are a list plus one "Hinzufügen", this one was a
/// form you could not get rid of and a list underneath it. SMTP is a payload like any other, so it
/// is added through the same dialog and edited on its own page — exactly as a template or a
/// component is.</para>
/// </summary>
public class SmtpModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;
    private readonly SecretProtector _secrets;

    public SmtpModel(AppDbContext db, ProfileService profiles, SecretProtector secrets)
    {
        _db = db; _profiles = profiles; _secrets = secrets;
    }

    public Profile Owner { get; private set; } = new();
    public List<ProfileSetting> Settings { get; private set; } = [];
    public Dictionary<string, string?> GlobalSmtp { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Setting(string key) => Settings.FirstOrDefault(s => s.Key == key)?.Value ?? "";
    public string Global(string key) => GlobalSmtp.TryGetValue(key, out var v) ? (v ?? "") : "";

    /// <summary>What a field shows: the global value while the global configuration is in use, the
    /// profile's own otherwise. Read-only in the first case, so the operator sees what would
    /// actually be rolled out instead of a set of empty boxes.</summary>
    public string Field(string key) => Owner.UseGlobalSmtp ? Global(key) : Setting(key);

    public bool Flag(string key) => Setting(key).Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";

    /// <summary>True when the profile does not roll SMTP out yet — the page is then being used to add
    /// it, and nothing is in effect until it is saved.</summary>
    public bool IsNew { get; private set; }

    /// <param name="source">"global" or "own", from the add dialog's second step. It only PRESELECTS
    /// where the values come from; nothing is rolled out until the operator saves. Answering a menu
    /// must not silently change what a live site's mail configuration is.</param>
    public async Task<IActionResult> OnGetAsync(int profileId, string? source)
    {
        if (!await LoadAsync(profileId)) return RedirectToPage("Index");

        IsNew = !Owner.SyncSmtp;
        if (IsNew && source is not null)
            Owner.UseGlobalSmtp = string.Equals(source, "global", StringComparison.OrdinalIgnoreCase);
        return Page();
    }

    private async Task<bool> LoadAsync(int profileId)
    {
        var owner = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId);
        if (owner is null) return false;
        Owner = owner;
        Settings = await _db.ProfileSettings.AsNoTracking().Where(s => s.ProfileId == profileId).ToListAsync();
        var smtpKeys = SettingKeys.Smtp;
        GlobalSmtp = await _db.CloudSettings.AsNoTracking().Where(x => smtpKeys.Contains(x.Key))
            .ToDictionaryAsync(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        return true;
    }

    public async Task<IActionResult> OnPostAsync(
        int profileId, bool useGlobalSmtp, bool clearPassword,
        string? host, string? port, string? user, string? password,
        string? fromEmail, string? fromName, bool ssl)
    {
        var profile = await _db.Profiles.FindAsync(profileId);
        if (profile is null) return RedirectToPage("Index");

        // Being on this page at all means the profile rolls SMTP out; the switch that decides THAT
        // lives in the add dialog and in the row's delete, not in a checkbox halfway down a form.
        profile.SyncSmtp = true;
        profile.UseGlobalSmtp = useGlobalSmtp;

        // With the global configuration in use the fields are shown READ-ONLY, filled with the global
        // values — so what posts back is the global data, not this profile's. Writing it would
        // quietly copy the global values into the profile and they would stop following the global
        // ones. The profile's own values stay untouched and reappear the moment it is switched over.
        if (useGlobalSmtp)
        {
            await _db.SaveChangesAsync();
            await _profiles.TouchAsync(profileId);
            TempData["Flash"] = "Globale SMTP-Einstellungen werden ausgerollt.";
            return RedirectToPage("Edit", new { id = profileId, tab = "settings" });
        }

        await UpsertAsync(profileId, "smtp.host", host?.Trim());
        await UpsertAsync(profileId, "smtp.port", port?.Trim());
        await UpsertAsync(profileId, "smtp.user", user?.Trim());
        // Encrypted before it ever reaches the database. An empty field keeps the stored value, so
        // saving the form does not wipe the secret; the explicit tick is the only way to clear it.
        if (clearPassword)
            await UpsertAsync(profileId, "smtp.password", "", secret: true);
        else if (!string.IsNullOrEmpty(password))
            await UpsertAsync(profileId, "smtp.password", _secrets.Protect(password), secret: true);
        await UpsertAsync(profileId, "smtp.fromEmail", fromEmail?.Trim());
        await UpsertAsync(profileId, "smtp.fromName", fromName?.Trim());
        await UpsertAsync(profileId, "smtp.ssl", ssl ? "1" : "0");

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(profileId);
        TempData["Flash"] = "SMTP-Einstellungen gespeichert.";
        return RedirectToPage("Edit", new { id = profileId, tab = "settings" });
    }

    /// <summary>Stops rolling SMTP out. The stored values survive on purpose — an operator who takes
    /// mail out of a profile has not asked to throw away the configuration, and putting it back must
    /// not mean typing it all again.</summary>
    public async Task<IActionResult> OnPostRemoveAsync(int profileId)
    {
        var profile = await _db.Profiles.FindAsync(profileId);
        if (profile is null) return RedirectToPage("Index");

        profile.SyncSmtp = false;
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(profileId);
        TempData["Flash"] = "SMTP wird von diesem Profil nicht mehr ausgerollt.";
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
