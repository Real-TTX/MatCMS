using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Settings;

/// <summary>
/// One page, three independent forms (general / notifications / SMTP). Each form saves ONLY its own
/// keys — that is why <see cref="SettingKeys"/> groups them into separate arrays.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CloudContext _cloud;
    private readonly EmailService _mail;
    private readonly DockerHostService _docker;
    private readonly SecretProtector _secrets;
    private readonly HostingService _hosting;
    private readonly MatCMS.Cloud.Services.Proxy.ProxyService _proxy;

    public IndexModel(AppDbContext db, CloudContext cloud, EmailService mail, DockerHostService docker, SecretProtector secrets,
        HostingService hosting, MatCMS.Cloud.Services.Proxy.ProxyService proxy)
    {
        _db = db;
        _cloud = cloud;
        _mail = mail;
        _docker = docker;
        _secrets = secrets;
        _hosting = hosting;
        _proxy = proxy;
    }

    public string Get(string key) => _cloud.Get(key) ?? "";
    public bool Flag(string key) => _cloud.Flag(key);

    public bool DockerConfigured => _docker.Configured;
    public bool DockerReachable { get; private set; }

    /// <summary>How many instances the cloud found on its own daemon — the practical answer to
    /// "is the socket doing anything for me?".</summary>
    public int LocalCount { get; private set; }

    /// <summary>The API tab: the operator keys (list + the one-time display of a just-created key).</summary>
    public Pages.Admin.ApiKeys.ApiKeyListView ApiKeys { get; private set; } = null!;

    // --- AI usage this month (cloud-wide + per instance), from the AiUsage ledger ---
    public record AiUsageRow(string Instance, int Tokens, int Calls);
    public string AiPeriod { get; private set; } = "";
    public List<AiUsageRow> AiUsage { get; private set; } = new();
    public long AiTokensTotal { get; private set; }
    public int AiCallsTotal { get; private set; }

    public async Task OnGetAsync()
    {
        DockerReachable = await _docker.IsReachableAsync(HttpContext.RequestAborted);
        LocalCount = await _db.Instances.CountAsync(i => i.Hosting == InstanceHosting.Local);
        ApiKeys = new Pages.Admin.ApiKeys.ApiKeyListView(
            await _db.ApiKeys.Include(k => k.Instances).AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(),
            TempData["NewApiKey"] as string);

        AiPeriod = DateTime.UtcNow.ToString("yyyy-MM");
        AiUsage = await (from u in _db.AiUsages.AsNoTracking()
                         where u.Period == AiPeriod
                         join i in _db.Instances on u.InstanceId equals i.Id
                         orderby u.Tokens descending
                         select new AiUsageRow(i.Name, u.Tokens, u.Calls)).ToListAsync();
        AiTokensTotal = AiUsage.Sum(r => (long)r.Tokens);
        AiCallsTotal = AiUsage.Sum(r => r.Calls);
    }

    public async Task<IActionResult> OnPostGeneralAsync(string? cloudName, string? canonicalUrl, bool forceHttps)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.CloudName] = cloudName?.Trim(),
            [SettingKeys.CanonicalUrl] = canonicalUrl?.Trim().TrimEnd('/'),
            [SettingKeys.ForceHttpsUrls] = forceHttps ? "1" : "0"
        });
        TempData["Flash"] = "Einstellungen gespeichert.";
        return RedirectToPage(new { tab = "general" });
    }

    /// <summary>Removes OLD MatCMS images the host no longer needs (dangling, unused). Only MatCMS images,
    /// only if not in use — see DockerHostService.PruneMatCmsImagesAsync.</summary>
    public async Task<IActionResult> OnPostPruneImagesAsync()
    {
        var r = await _docker.PruneMatCmsImagesAsync(HttpContext.RequestAborted);
        TempData["Flash"] = r.Removed == 0
            ? "Keine alten MatCMS-Images zum Aufräumen."
            : $"{r.Removed} altes/alte MatCMS-Image(s) entfernt (~{r.BytesReclaimed / (1024.0 * 1024.0):0.#} MB).";
        return RedirectToPage(new { tab = "docker" });
    }

    /// <summary>Eigenes Formular, eigener Handler — jede Karte speichert nur ihre eigenen Schlüssel.
    /// Bliebe das Kontingent am Allgemein-Handler hängen, würde ein Speichern dort den Wert leeren,
    /// weil das Formular ihn gar nicht mehr mitschickt.</summary>
    public async Task<IActionResult> OnPostBackupAsync(
        string? backupQuotaGb,
        string? backupKeepDaily, string? backupKeepWeekly, string? backupKeepMonthly, string? backupMaxCount)
    {
        // Store the quota as an invariant-culture string so it reads back the same regardless of the
        // server locale; fractional allowed (0.1 = 100 MB), comma or dot on input.
        var quota = BackupStore.ParseGb(backupQuotaGb) is double gb && gb > 0
            ? gb.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        // Retention tiers: a number (incl. 0 = off) is stored; anything else is left empty, which the
        // resolver reads as "off" for the cloud-wide default.
        static string Tier(string? s) => int.TryParse(s, out var n) && n >= 0 ? n.ToString() : "";

        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.BackupQuotaGb] = quota,
            [SettingKeys.BackupKeepDaily] = Tier(backupKeepDaily),
            [SettingKeys.BackupKeepWeekly] = Tier(backupKeepWeekly),
            [SettingKeys.BackupKeepMonthly] = Tier(backupKeepMonthly),
            [SettingKeys.BackupMaxCount] = Tier(backupMaxCount),
        });
        TempData["Flash"] = "Backup-Einstellungen gespeichert.";
        return RedirectToPage(new { tab = "backup" });
    }

    /// <summary>Only the module switch lives here — Hosting is optional and its configuration is in its own menu
    /// group (Hosting → Einstellungen).</summary>
    public async Task<IActionResult> OnPostHostingAsync(bool hostingEnabled)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?> { [SettingKeys.HostingEnabled] = hostingEnabled ? "1" : "0" });
        TempData["Flash"] = hostingEnabled ? "Hosting eingeschaltet — die Menügruppe „Hosting“ ist jetzt da." : "Hosting ausgeschaltet.";
        return RedirectToPage(new { tab = "hosting" });
    }

    /// <summary>Security policy card. Its own form, so saving it never touches the other settings.</summary>
    public async Task<IActionResult> OnPostSecurityAsync(bool require2fa)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.Require2fa] = require2fa ? "1" : "0"
        });
        TempData["Flash"] = require2fa
            ? "Zwei-Faktor-Pflicht ist AKTIV — Konten ohne 2FA werden zur Einrichtung geführt."
            : "Sicherheitseinstellungen gespeichert.";
        return RedirectToPage(new { tab = "general" });
    }

    /// <summary>The automatic-update rule (who is TOLD about updates is the notification matrix).</summary>
    public async Task<IActionResult> OnPostUpdatesAsync(bool autoUpdateLocal)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?> { [SettingKeys.AutoUpdateLocal] = autoUpdateLocal ? "1" : "0" });
        TempData["Flash"] = "Update-Einstellungen gespeichert.";
        return RedirectToPage(new { tab = "general" });
    }

    public async Task<IActionResult> OnPostSmtpAsync(
        string? host, string? port, string? user, string? password, bool clearPassword,
        string? fromEmail, string? fromName, bool ssl)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.SmtpHost] = host?.Trim(),
            [SettingKeys.SmtpPort] = port?.Trim(),
            [SettingKeys.SmtpUser] = user?.Trim(),
            // An empty password field keeps the stored one VERBATIM — so saving the form neither
            // wipes the secret because the browser rendered it blank, nor encrypts an already
            // encrypted value a second time. Only a newly entered password is protected, and only
            // the explicit "remove" tick can empty it (a blank field alone must not, or a careless
            // save of the other fields would silently break sending).
            [SettingKeys.SmtpPassword] = clearPassword ? ""
                : string.IsNullOrEmpty(password) ? Get(SettingKeys.SmtpPassword)
                : _secrets.Protect(password),
            [SettingKeys.SmtpFromEmail] = fromEmail?.Trim(),
            [SettingKeys.SmtpFromName] = fromName?.Trim(),
            [SettingKeys.SmtpSsl] = ssl ? "1" : "0"
        });
        TempData["Flash"] = "SMTP-Einstellungen gespeichert.";
        return RedirectToPage(new { tab = "smtp" });
    }

    /// <summary>Central AI provider connection. The key is stored SecretProtector-encrypted with the
    /// same empty-keeps / explicit-clear rule as the SMTP password, and never echoed back to the form.</summary>
    public async Task<IActionResult> OnPostAiAsync(string? provider, string? model, string? baseUrl, string? apiKey, bool clearKey)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.AiProvider] = string.IsNullOrWhiteSpace(provider) ? "openai" : provider.Trim().ToLowerInvariant(),
            [SettingKeys.AiModel] = model?.Trim(),
            [SettingKeys.AiBaseUrl] = baseUrl?.Trim().TrimEnd('/'),
            [SettingKeys.AiApiKey] = clearKey ? ""
                : string.IsNullOrEmpty(apiKey) ? Get(SettingKeys.AiApiKey)
                : _secrets.Protect(apiKey),
        });
        TempData["Flash"] = "KI-Einstellungen gespeichert.";
        return RedirectToPage(new { tab = "ai" });
    }

    public async Task<IActionResult> OnPostSmtpTestAsync(string? testTo)
    {
        if (string.IsNullOrWhiteSpace(testTo))
        {
            TempData["FlashError"] = "Bitte eine Empfängeradresse für den Test angeben.";
        return RedirectToPage(new { tab = "smtp" });
        }

        var cfg = await _mail.GetConfigAsync();
        var (ok, error) = await _mail.SendTestAsync(cfg, testTo.Trim());
        if (ok) TempData["Flash"] = $"Test-E-Mail an {testTo} gesendet.";
        else TempData["FlashError"] = $"Test fehlgeschlagen: {error}";
        return RedirectToPage(new { tab = "smtp" });
    }
}
