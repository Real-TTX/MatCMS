using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Settings;

/// <summary>
/// The cloud's own settings: general + security, SMTP, AI, backups, API keys. Each form saves ONLY its own keys.
/// Everything about hosts and containers is under Hosting — the old tabs here redirect there.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CloudContext _cloud;
    private readonly EmailService _mail;
    private readonly SecretProtector _secrets;

    public IndexModel(AppDbContext db, CloudContext cloud, EmailService mail, SecretProtector secrets)
    {
        _db = db;
        _cloud = cloud;
        _mail = mail;
        _secrets = secrets;
    }

    public string Get(string key) => _cloud.Get(key) ?? "";
    public bool Flag(string key) => _cloud.Flag(key);

    /// <summary>The API tab: the operator keys (list + the one-time display of a just-created key).</summary>
    public Pages.Admin.ApiKeys.ApiKeyListView ApiKeys { get; private set; } = null!;

    // --- AI usage this month (cloud-wide + per instance), from the AiUsage ledger ---
    public record AiUsageRow(string Instance, int Tokens, int Calls);
    public string AiPeriod { get; private set; } = "";
    public List<AiUsageRow> AiUsage { get; private set; } = new();
    public long AiTokensTotal { get; private set; }
    public int AiCallsTotal { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? tab)
    {
        // These tabs moved to Hosting; old links and bookmarks land where the thing is now.
        if (tab == "hosting") return RedirectToPage("/Admin/Hosting/Settings");
        if (tab == "docker") return RedirectToPage("/Admin/Hosting/Docker");
        // Backup quota and retention are a profile matter now; the default profile holds the fallback.
        if (tab == "backup")
        {
            var def = await _db.Profiles.AsNoTracking().Where(p => p.IsDefault).Select(p => (int?)p.Id).FirstOrDefaultAsync();
            return def is int pid ? RedirectToPage("/Admin/Profiles/Edit", new { id = pid }) : RedirectToPage("/Admin/Profiles/Index");
        }
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
        return Page();
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
