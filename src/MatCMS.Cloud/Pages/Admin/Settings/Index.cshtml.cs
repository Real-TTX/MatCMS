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

    public IndexModel(AppDbContext db, CloudContext cloud, EmailService mail, DockerHostService docker)
    {
        _db = db;
        _cloud = cloud;
        _mail = mail;
        _docker = docker;
    }

    public string Get(string key) => _cloud.Get(key) ?? "";
    public bool Flag(string key) => _cloud.Flag(key);

    public bool DockerConfigured => _docker.Configured;
    public bool DockerReachable { get; private set; }

    /// <summary>How many instances the cloud found on its own daemon — the practical answer to
    /// "is the socket doing anything for me?".</summary>
    public int LocalCount { get; private set; }

    public async Task OnGetAsync()
    {
        DockerReachable = await _docker.IsReachableAsync(HttpContext.RequestAborted);
        LocalCount = await _db.Instances.CountAsync(i => i.Hosting == InstanceHosting.Local);
    }

    public async Task<IActionResult> OnPostGeneralAsync(string? cloudName, string? canonicalUrl)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.CloudName] = cloudName?.Trim(),
            [SettingKeys.CanonicalUrl] = canonicalUrl?.Trim().TrimEnd('/')
        });
        TempData["Flash"] = "Einstellungen gespeichert.";
        return RedirectToPage(new { tab = "general" });
    }

    public async Task<IActionResult> OnPostNotificationsAsync(
        string? recipients, bool notifyOffline, bool notifyUpdate, bool autoUpdateLocal)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.NotifyRecipients] = recipients?.Trim(),
            [SettingKeys.NotifyOffline] = notifyOffline ? "1" : "0",
            [SettingKeys.NotifyUpdate] = notifyUpdate ? "1" : "0",
            [SettingKeys.AutoUpdateLocal] = autoUpdateLocal ? "1" : "0"
        });
        TempData["Flash"] = "Benachrichtigungen gespeichert.";
        return RedirectToPage(new { tab = "notifications" });
    }

    public async Task<IActionResult> OnPostSmtpAsync(
        string? host, string? port, string? user, string? password,
        string? fromEmail, string? fromName, bool ssl)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.SmtpHost] = host?.Trim(),
            [SettingKeys.SmtpPort] = port?.Trim(),
            [SettingKeys.SmtpUser] = user?.Trim(),
            // An empty password field keeps the stored one — so saving the form does not wipe the
            // secret just because the browser rendered it blank.
            [SettingKeys.SmtpPassword] = string.IsNullOrEmpty(password) ? Get(SettingKeys.SmtpPassword) : password,
            [SettingKeys.SmtpFromEmail] = fromEmail?.Trim(),
            [SettingKeys.SmtpFromName] = fromName?.Trim(),
            [SettingKeys.SmtpSsl] = ssl ? "1" : "0"
        });
        TempData["Flash"] = "SMTP-Einstellungen gespeichert.";
        return RedirectToPage(new { tab = "smtp" });
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
