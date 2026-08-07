using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace MatCMS.Pages.Admin.Settings;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly EmailService _email;
    private readonly CloudService _cloud;
    private readonly CloudSyncService _sync;
    public CloudState CloudState { get; }

    public IndexModel(AppDbContext db, EmailService email, CloudService cloud, CloudSyncService sync, CloudState cloudState)
    {
        _db = db;
        _email = email;
        _cloud = cloud;
        _sync = sync;
        CloudState = cloudState;
    }

    /// <summary>Current cloud link (Cloud tab). The token is never rendered back — only whether one
    /// is stored — so it cannot be read out of the page source.</summary>
    public CloudService.CloudSettings Cloud { get; private set; } = new("", "", "");
    public bool CloudHasToken => !string.IsNullOrEmpty(Cloud.Token);

    /// <summary>What the last rollout from the cloud actually did here, item by item — the same log
    /// the cloud is sent, shown to this site's own admin.</summary>
    public List<SyncItemReport> SyncReport { get; private set; } = new();

    /// <summary>Result of a "Vorschau" click, surviving the PRG redirect in TempData. Null unless the
    /// operator just asked for one — the preview is a question, not a state of the site.</summary>
    public List<SyncItemReport>? Preview { get; private set; }

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
        Cloud = await _cloud.GetSettingsAsync();
        if (Cloud.Configured) SyncReport = await _sync.LastReportAsync() ?? new();

        if (TempData["CloudPreview"] is string previewJson)
        {
            // Never throws: this is our own JSON, but a stale cookie from an older build must not
            // take the settings page down.
            try { Preview = System.Text.Json.JsonSerializer.Deserialize<List<SyncItemReport>>(previewJson); }
            catch { Preview = null; }
        }
    }

    // --- MatCMS.Cloud link (Cloud tab) --------------------------------------
    // Not routed through SaveKeysAsync: the token must be encrypted, and connecting should give
    // immediate feedback instead of leaving the operator to wait for the next minute's heartbeat.

    /// <summary>Enrollment with a profile's join code — the normal way in. Cloud-URL + code, and the
    /// instance fetches its own id and token.</summary>
    public async Task<IActionResult> OnPostCloudConnectAsync(string? cloudUrl, string? joinCode)
    {
        var (ok, error) = await _cloud.RegisterAsync(cloudUrl, joinCode, HttpContext.RequestAborted);
        if (!ok)
        {
            TempData["FlashError"] = $"Verbindung fehlgeschlagen: {error}";
            return RedirectToPage(new { tab = "cloud" });
        }

        TempData["Flash"] = CloudState.IsPending
            ? "Verbunden. Die Instanz wartet nun auf die Freigabe in der Cloud."
            : "Mit der Cloud verbunden.";
        return RedirectToPage(new { tab = "cloud" });
    }

    /// <summary>Advanced path: paste an id + token that were issued elsewhere.</summary>
    public async Task<IActionResult> OnPostCloudManualAsync(string? cloudUrl, string? cloudInstanceId, string? cloudToken)
    {
        if (string.IsNullOrWhiteSpace(cloudUrl) || string.IsNullOrWhiteSpace(cloudInstanceId))
        {
            TempData["FlashError"] = "Cloud-URL und Instanz-ID sind erforderlich.";
            return RedirectToPage(new { tab = "cloud" });
        }

        await _cloud.SaveSettingsAsync(cloudUrl, cloudInstanceId, cloudToken);
        await _cloud.SendHeartbeatAsync(HttpContext.RequestAborted);

        if (CloudState.Connected)
            TempData["Flash"] = "Mit der Cloud verbunden.";
        else
            TempData["FlashError"] = $"Verbindung fehlgeschlagen: {CloudState.LastError}";
        return RedirectToPage(new { tab = "cloud" });
    }

    /// <summary>Shows what the next apply would change, without changing anything. Carried through
    /// the redirect as JSON in TempData so the page keeps its PRG shape.</summary>
    public async Task<IActionResult> OnPostCloudPreviewAsync()
    {
        var result = await _cloud.PreviewAsync(HttpContext.RequestAborted);
        if (!result.Ok)
        {
            TempData["FlashError"] = $"Vorschau nicht möglich: {result.Error}";
            return RedirectToPage(new { tab = "cloud" });
        }

        TempData["CloudPreview"] = System.Text.Json.JsonSerializer.Serialize(result.Report);
        TempData["Flash"] = result.Report.Count == 0
            ? "Es gibt nichts anzuwenden — die Instanz ist auf dem Stand des Profils."
            : $"Vorschau für Revision {result.Revision}: {result.Report.Count} Objekte.";
        return RedirectToPage(new { tab = "cloud" });
    }

    /// <summary>Pulls and applies the profile configuration immediately instead of waiting for the
    /// next heartbeat.</summary>
    public async Task<IActionResult> OnPostCloudSyncAsync()
    {
        var result = await _cloud.PullAndApplyAsync(ct: HttpContext.RequestAborted);
        if (result.Ok)
            TempData["Flash"] = result.Applied.Count == 0
                ? $"Konfiguration angewendet (Revision {result.Revision})."
                : $"Konfiguration angewendet (Revision {result.Revision}): {string.Join(", ", result.Applied)}.";
        else
            TempData["FlashError"] = $"Konfiguration konnte nicht angewendet werden: {result.Error}";
        return RedirectToPage(new { tab = "cloud" });
    }

    public async Task<IActionResult> OnPostCloudTestAsync()
    {
        await _cloud.SendHeartbeatAsync(HttpContext.RequestAborted);
        if (CloudState.Connected)
            TempData["Flash"] = "Heartbeat erfolgreich gesendet.";
        else
            TempData["FlashError"] = $"Heartbeat fehlgeschlagen: {CloudState.LastError}";
        return RedirectToPage(new { tab = "cloud" });
    }

    public async Task<IActionResult> OnPostCloudDisconnectAsync()
    {
        await _cloud.DisconnectAsync(HttpContext.RequestAborted);
        TempData["Flash"] = "Verbindung zur Cloud getrennt.";
        return RedirectToPage(new { tab = "cloud" });
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
