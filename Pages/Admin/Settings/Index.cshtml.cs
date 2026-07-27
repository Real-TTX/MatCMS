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

    public async Task OnGetAsync()
    {
        var existing = await _db.SiteSettings.ToDictionaryAsync(s => s.Key, s => s.Value);
        foreach (var key in SettingKeys.All.Concat(SettingKeys.Smtp))
            Values[key] = existing.TryGetValue(key, out var v) ? v : "";
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
