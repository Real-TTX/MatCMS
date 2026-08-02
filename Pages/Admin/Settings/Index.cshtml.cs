using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Settings;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly EmailService _email;
    public IndexModel(AppDbContext db, EmailService email) { _db = db; _email = email; }

    [BindProperty] public Dictionary<string, string> Values { get; set; } = new();

    /// <summary>Checked non-default language codes (Sprachen tab).</summary>
    [BindProperty] public List<string> ActiveLanguages { get; set; } = new();

    /// <summary>Chosen DEFAULT (root) content language (Sprachen tab). Applied after a restart.</summary>
    [BindProperty] public string DefaultLanguage { get; set; } = Localizer.DefaultCulture;

    /// <summary>The configured default (root) language — the stored setting, or the running default.</summary>
    public string CurrentDefaultLanguage { get; private set; } = Localizer.DefaultCulture;

    /// <summary>Published pages offered as 404 / error targets (Fehlerhandling tab).</summary>
    public List<MatCMS.Models.Page> AllPages { get; private set; } = new();

    /// <summary>All routable language codes (Sprachen tab) and which are currently active.</summary>
    public IReadOnlyList<string> RoutableLanguages => Localizer.SupportedCultures;
    public HashSet<string> CurrentActive { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task OnGetAsync()
    {
        var existing = await _db.SiteSettings.ToDictionaryAsync(s => s.Key, s => s.Value);
        foreach (var key in SettingKeys.All.Concat(SettingKeys.Smtp).Concat(SettingKeys.Errors).Concat(SettingKeys.Code).Concat(SettingKeys.Maintenance).Concat(SettingKeys.Translate))
            Values[key] = existing.TryGetValue(key, out var v) ? v : "";
        CurrentActive = Localizer.ParseActive(existing.TryGetValue(SettingKeys.Languages, out var lv) ? lv : "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        CurrentDefaultLanguage = existing.TryGetValue(SettingKeys.DefaultLanguage, out var dl) && !string.IsNullOrWhiteSpace(dl)
            ? dl.Trim().ToLowerInvariant() : Localizer.DefaultCulture;
        DefaultLanguage = CurrentDefaultLanguage;
        AllPages = await _db.Pages.AsNoTracking()
            .Where(p => p.Locale == Localizer.DefaultCulture)
            .OrderBy(p => p.Title).ToListAsync();
    }

    public async Task<IActionResult> OnPostLanguagesAsync()
    {
        var routable = Localizer.SupportedCultures.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosen = (ActiveLanguages ?? new())
            .Select(c => (c ?? "").Trim().ToLowerInvariant())
            .Where(c => c.Length > 0 && routable.Contains(c))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The new root language (must be routable) — otherwise keep the current one.
        var newDefault = (DefaultLanguage ?? "").Trim().ToLowerInvariant();
        if (!routable.Contains(newDefault)) newDefault = Localizer.DefaultCulture;

        // Keep every active language explicit — including BOTH the current root and the new root — so
        // switching the root language never silently deactivates a language whose pages still exist.
        chosen.Add(Localizer.DefaultCulture);
        chosen.Add(newDefault);
        var csv = string.Join(",", Localizer.SupportedCultures.Where(chosen.Contains));

        await UpsertSettingAsync(SettingKeys.Languages, csv);
        await UpsertSettingAsync(SettingKeys.DefaultLanguage, newDefault);
        await _db.SaveChangesAsync();

        TempData["Flash"] = newDefault != Localizer.DefaultCulture
            ? "Sprachen gespeichert. Die neue Standardsprache wird nach einem Neustart des Servers aktiv."
            : "Sprachen gespeichert.";
        return RedirectToPage(new { tab = "languages" });
    }

    private async Task UpsertSettingAsync(string key, string value)
    {
        var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else row.Value = value;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await SaveKeysAsync(SettingKeys.All);
        TempData["Flash"] = "Einstellungen gespeichert.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSmtpAsync()
    {
        await SaveKeysAsync(SettingKeys.Smtp);
        TempData["Flash"] = "SMTP-Einstellungen gespeichert.";
        return RedirectToPage(new { tab = "smtp" });
    }

    public async Task<IActionResult> OnPostErrorsAsync()
    {
        await SaveKeysAsync(SettingKeys.Errors);
        TempData["Flash"] = "Fehlerhandling gespeichert.";
        return RedirectToPage(new { tab = "errors" });
    }

    public async Task<IActionResult> OnPostCodeAsync()
    {
        await SaveKeysAsync(SettingKeys.Code);
        TempData["Flash"] = "Code / Tracking gespeichert.";
        return RedirectToPage(new { tab = "code" });
    }

    public async Task<IActionResult> OnPostTranslateAsync()
    {
        await SaveKeysAsync(SettingKeys.Translate);
        TempData["Flash"] = "Übersetzungsdienst gespeichert.";
        return RedirectToPage(new { tab = "languages" });
    }

    public async Task<IActionResult> OnPostMaintenanceAsync()
    {
        // The checkbox only posts a value when ticked → SaveKeysAsync writes "" for the unticked case.
        await SaveKeysAsync(SettingKeys.Maintenance);
        TempData["Flash"] = Values.TryGetValue(SettingKeys.MaintenanceEnabled, out var on) && on == "1"
            ? "Wartungsmodus ist AKTIV — Besucher sehen die Wartungsseite."
            : "Wartungsmodus gespeichert (aus).";
        return RedirectToPage(new { tab = "maintenance" });
    }

    /// <summary>Sends a test e-mail using the values currently entered (no need to save first).</summary>
    public async Task<IActionResult> OnPostTestSmtpAsync()
    {
        string V(string k) => Values.TryGetValue(k, out var v) ? (v ?? "") : "";
        if (!int.TryParse(V(SettingKeys.SmtpPort), out var port) || port <= 0) port = 587;
        var ssl = V(SettingKeys.SmtpSsl).Trim().ToLowerInvariant();
        var cfg = new EmailService.SmtpConfig(
            V(SettingKeys.SmtpHost).Trim(), port, V(SettingKeys.SmtpUser), V(SettingKeys.SmtpPassword),
            V(SettingKeys.SmtpFromEmail).Trim(), V(SettingKeys.SmtpFromName).Trim(),
            ssl is "true" or "on" or "1" or "yes");

        var to = !string.IsNullOrWhiteSpace(cfg.FromEmail) ? cfg.FromEmail : cfg.User;
        if (string.IsNullOrWhiteSpace(to))
            TempData["FlashError"] = "Bitte zuerst eine Absender-Adresse eintragen.";
        else
        {
            var (ok, error) = await _email.SendTestAsync(cfg, to);
            TempData[ok ? "Flash" : "FlashError"] = ok
                ? $"Test-E-Mail an {to} gesendet."
                : $"Test fehlgeschlagen: {error}";
        }
        return RedirectToPage(new { tab = "smtp" });
    }

    private async Task SaveKeysAsync(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            var value = Values.TryGetValue(key, out var v) ? (v ?? "") : "";
            var setting = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
            if (setting is null)
                _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
            else
                setting.Value = value;
        }
        await _db.SaveChangesAsync();
    }
}
