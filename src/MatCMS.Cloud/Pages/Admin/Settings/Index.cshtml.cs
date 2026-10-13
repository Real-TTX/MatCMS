using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Settings;

/// <summary>
/// The cloud's own settings: general + security, SMTP, AI, API keys, log. Each form saves ONLY its own keys.
/// Everything about hosts and containers is under Hosting — the old tabs here redirect there.
/// </summary>
// A configuration backup carries the store's plugin bundles — the default 30 MB request limit would cut an upload off.
[RequestSizeLimit(256L * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 256L * 1024 * 1024)]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CloudContext _cloud;
    private readonly EmailService _mail;
    private readonly SecretProtector _secrets;
    private readonly CloudConfigBackupService _config;
    private readonly ChatGptAuthService _chatgpt;

    public IndexModel(AppDbContext db, CloudContext cloud, EmailService mail, SecretProtector secrets, CloudConfigBackupService config, ChatGptAuthService chatgpt)
    {
        _chatgpt = chatgpt;
        _db = db;
        _cloud = cloud;
        _mail = mail;
        _secrets = secrets;
        _config = config;
    }

    // ---- Sicherung: backups of this cloud's configuration (CloudConfigBackupService — the same as REST and MCP) ----

    public List<CloudConfigBackupService.Info> ConfigBackups { get; private set; } = new();
    public string? ConfigReportTitle { get; private set; }
    public List<string> ConfigReport { get; private set; } = new();

    private IActionResult BackToConfig() => RedirectToPage(new { tab = "config" });

    public async Task<IActionResult> OnPostConfigCreateAsync(string[]? sections, string? passphrase, string? note)
    {
        var (info, error) = await _config.CreateAsync(sections, passphrase, note, HttpContext.RequestAborted);
        TempData[info is null ? "FlashError" : "Flash"] = info is null ? error
            : $"Sicherung „{info.Id}“ erstellt" + (info.HasSecrets ? " – geheime Werte mit dem Passwort verschlüsselt." : " – ohne geheime Werte (kein Passwort).");
        return BackToConfig();
    }

    public IActionResult OnGetConfigDownload(string id) =>
        _config.FilePath(id) is { } p ? PhysicalFile(p, "application/zip", id) : NotFound();

    public async Task<IActionResult> OnPostConfigUploadAsync(IFormFile? file)
    {
        if (file is null || file.Length == 0) { TempData["FlashError"] = "Bitte eine Sicherung (ZIP) wählen."; return BackToConfig(); }
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, HttpContext.RequestAborted);
        var (info, error) = _config.Upload(ms.ToArray(), file.FileName);
        TempData[info is null ? "FlashError" : "Flash"] = info is null ? error : $"Sicherung „{info.Id}“ hochgeladen – jetzt Vorschau ansehen, dann wiederherstellen.";
        return BackToConfig();
    }

    /// <summary>"Vorschau" (apply = false) reports what would change; "Wiederherstellen" applies it.</summary>
    public async Task<IActionResult> OnPostConfigRestoreAsync(string id, string[]? sections, string? passphrase, bool apply)
    {
        var r = await _config.RestoreAsync(id, sections, passphrase, dryRun: !apply, HttpContext.RequestAborted);
        TempData["ConfigReportTitle"] = !r.Ok ? "Wiederherstellen nicht möglich" : apply ? $"Wiederhergestellt aus „{id}“" : $"Vorschau für „{id}“ – noch nichts geändert";
        TempData["ConfigReport"] = string.Join("\n", r.Lines);
        if (r.Ok && apply) TempData["Flash"] = "Konfiguration wiederhergestellt.";
        return BackToConfig();
    }

    public IActionResult OnPostConfigDelete(string id)
    {
        var ok = _config.Delete(id);
        TempData[ok ? "Flash" : "FlashError"] = ok ? $"Sicherung „{id}“ gelöscht." : "Sicherung nicht gefunden.";
        return BackToConfig();
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
        if (tab == "docker") return RedirectToPage("/Admin/Hosting/Nodes/Details", new { tab = "docker" });
        // Backup quota and retention are a profile matter now; the default profile holds the fallback.
        if (tab == "backup")
        {
            var def = await _db.Profiles.AsNoTracking().Where(p => p.IsDefault).Select(p => (int?)p.Id).FirstOrDefaultAsync();
            return def is int pid ? RedirectToPage("/Admin/Profiles/Edit", new { id = pid }) : RedirectToPage("/Admin/Profiles/Index");
        }
        ConfigBackups = _config.List();
        ConfigReportTitle = TempData["ConfigReportTitle"] as string;
        if (TempData["ConfigReport"] is string rep) ConfigReport = rep.Split('\n').ToList();
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
        ChatGpt = await _chatgpt.GetStatusAsync(poll: false, HttpContext.RequestAborted);
        if (ChatGpt.Connected) ChatGptModels = await _chatgpt.ListModelsAsync(HttpContext.RequestAborted);
        return Page();
    }

    // ---- "Mit ChatGPT anmelden" (device code): the page script starts it, polls the status and signs out ----

    public ChatGptAuthService.Status ChatGpt { get; private set; } = new(false, null, null, null, null, null, null, null, null, null);
    public IReadOnlyList<ChatGptAuthService.ModelInfo> ChatGptModels { get; private set; } = Array.Empty<ChatGptAuthService.ModelInfo>();

    public async Task<IActionResult> OnPostChatGptStartAsync()
    {
        var (pending, error) = await _chatgpt.StartLoginAsync(HttpContext.RequestAborted);
        if (pending is null) return new JsonResult(new { ok = false, error });
        return new JsonResult(new { ok = true, status = ChatGptAuthService.StatusJson(await _chatgpt.GetStatusAsync(poll: false, HttpContext.RequestAborted)) });
    }

    /// <summary>Asks OpenAI (at most once per poll interval) whether the code was confirmed and answers the state.</summary>
    public async Task<IActionResult> OnGetChatGptStatusAsync() =>
        new JsonResult(ChatGptAuthService.StatusJson(await _chatgpt.GetStatusAsync(poll: true, HttpContext.RequestAborted)));

    /// <summary>"KI testen": one short call with the saved provider, answered as a flash message.</summary>
    public async Task<IActionResult> OnPostAiTestAsync([FromServices] AiService ai)
    {
        var r = await ai.TestAsync(HttpContext.RequestAborted);
        if (r.Ok) TempData["Flash"] = $"KI-Test erfolgreich ({r.PromptTokens + r.CompletionTokens} Tokens): {r.Text}";
        else TempData["FlashError"] = "KI-Test fehlgeschlagen: " + r.Error;
        return RedirectToPage(new { tab = "ai" });
    }

    public async Task<IActionResult> OnPostChatGptLogoutAsync()
    {
        await _chatgpt.LogoutAsync(HttpContext.RequestAborted);
        TempData["Flash"] = "Von ChatGPT abgemeldet.";
        return RedirectToPage(new { tab = "ai" });
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

    public int Days(string key, int fallback) => int.TryParse(Get(key), out var d) && d >= 0 ? d : fallback;

    /// <summary>What the cloud records in its own log and how long it keeps it. The log page only shows.</summary>
    public async Task<IActionResult> OnPostLogsAsync(bool requestsOn, int errorsDays, int requestsDays)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.LogRequests] = requestsOn ? "on" : "off",
            [SettingKeys.LogRetentionErrorsDays] = Math.Clamp(errorsDays, 0, 3650).ToString(),
            [SettingKeys.LogRetentionRequestsDays] = Math.Clamp(requestsDays, 0, 3650).ToString(),
        });
        TempData["Flash"] = "Protokoll-Einstellungen gespeichert.";
        return RedirectToPage(new { tab = "logs" });
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
