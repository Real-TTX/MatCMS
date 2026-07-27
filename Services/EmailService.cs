using System.Net;
using System.Net.Mail;
using MatCMS.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// Sends e-mail via the SMTP server configured under Settings → SMTP (stored in SiteSettings).
/// Uses the built-in <see cref="SmtpClient"/> — no external dependency. Never throws to the caller;
/// failures come back as (false, error) so a form submission is never lost because mail is down.
/// </summary>
public class EmailService
{
    private readonly AppDbContext _db;
    private readonly ILogger<EmailService> _log;

    public EmailService(AppDbContext db, ILogger<EmailService> log)
    {
        _db = db;
        _log = log;
    }

    public sealed record SmtpConfig(
        string Host, int Port, string User, string Password,
        string FromEmail, string FromName, bool Ssl)
    {
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromEmail);
    }

    public async Task<SmtpConfig> GetConfigAsync()
    {
        var keys = SettingKeys.Smtp;
        var map = await _db.SiteSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        string G(string k) => map.TryGetValue(k, out var v) ? (v ?? "") : "";
        if (!int.TryParse(G(SettingKeys.SmtpPort), out var port) || port <= 0) port = 587;
        var ssl = G(SettingKeys.SmtpSsl).Trim().ToLowerInvariant();

        return new SmtpConfig(
            G(SettingKeys.SmtpHost).Trim(), port, G(SettingKeys.SmtpUser), G(SettingKeys.SmtpPassword),
            G(SettingKeys.SmtpFromEmail).Trim(), G(SettingKeys.SmtpFromName).Trim(),
            ssl is "true" or "on" or "1" or "yes");
    }

    public async Task<bool> IsConfiguredAsync() => (await GetConfigAsync()).IsConfigured;

    /// <summary>Sends a plain-text mail using the saved SMTP config. Returns (ok, error); never throws.</summary>
    public async Task<(bool ok, string? error)> SendAsync(
        IEnumerable<string> to, string subject, string body, string? replyTo = null)
        => await SendCoreAsync(await GetConfigAsync(), to, subject, body, replyTo);

    /// <summary>Sends a test e-mail with an explicit (possibly unsaved) config — used by the SMTP test button.</summary>
    public async Task<(bool ok, string? error)> SendTestAsync(SmtpConfig cfg, string to)
        => await SendCoreAsync(cfg, new[] { to },
            "MatCMS – SMTP-Test",
            "Diese Test-E-Mail bestätigt, dass die SMTP-Einstellungen funktionieren.\r\n\r\n– MatCMS");

    private async Task<(bool ok, string? error)> SendCoreAsync(
        SmtpConfig cfg, IEnumerable<string> to, string subject, string body, string? replyTo = null)
    {
        if (!cfg.IsConfigured) return (false, "SMTP ist nicht konfiguriert (Host und Absender-Adresse erforderlich).");

        var recipients = to.Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (recipients.Count == 0) return (false, "Keine Empfänger angegeben.");

        try
        {
            using var msg = new MailMessage
            {
                From = new MailAddress(cfg.FromEmail, string.IsNullOrWhiteSpace(cfg.FromName) ? cfg.FromEmail : cfg.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };
            foreach (var r in recipients)
                try { msg.To.Add(r); } catch { /* skip malformed address */ }
            if (msg.To.Count == 0) return (false, "Keine gültige Empfängeradresse.");
            if (!string.IsNullOrWhiteSpace(replyTo))
                try { msg.ReplyToList.Add(replyTo); } catch { /* ignore bad reply-to */ }

            using var client = new SmtpClient(cfg.Host, cfg.Port)
            {
                EnableSsl = cfg.Ssl,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };
            if (!string.IsNullOrWhiteSpace(cfg.User))
                client.Credentials = new NetworkCredential(cfg.User, cfg.Password);

            await client.SendMailAsync(msg);
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SMTP send failed to {Recipients}", string.Join(", ", recipients));
            return (false, ex.Message);
        }
    }
}
