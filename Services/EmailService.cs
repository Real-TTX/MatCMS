using MatCMS.Data;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatCMS.Services;

/// <summary>
/// Sends e-mail via the SMTP server configured under Settings → SMTP (stored in SiteSettings).
/// Uses MailKit so BOTH connection modes work: implicit SSL/TLS (SMTPS, port 465, e.g. IONOS) and
/// STARTTLS (port 587/25). The built-in System.Net.Mail.SmtpClient can't do implicit SSL, which is
/// why IONOS on 465 failed. Never throws to the caller; failures come back as (false, error) so a
/// form submission is never lost because mail is down.
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
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(cfg.FromName) ? cfg.FromEmail : cfg.FromName, cfg.FromEmail));
            foreach (var r in recipients)
                try { msg.To.Add(MailboxAddress.Parse(r)); } catch { /* skip malformed address */ }
            if (msg.To.Count == 0) return (false, "Keine gültige Empfängeradresse.");
            if (!string.IsNullOrWhiteSpace(replyTo))
                try { msg.ReplyTo.Add(MailboxAddress.Parse(replyTo)); } catch { /* ignore bad reply-to */ }
            msg.Subject = subject;
            msg.Body = new TextPart("plain") { Text = body };

            // Pick the right TLS mode: port 465 = implicit SSL on connect; otherwise STARTTLS (opportunistic
            // when the SSL switch is off, so a plain server still works). This is what makes IONOS:465 work.
            var secure = cfg.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : cfg.Ssl ? SecureSocketOptions.StartTls : SecureSocketOptions.StartTlsWhenAvailable;

            using var client = new SmtpClient();
            client.Timeout = 20_000; // 20s, so a wrong host/port fails fast instead of hanging the request
            await client.ConnectAsync(cfg.Host, cfg.Port, secure);
            if (!string.IsNullOrWhiteSpace(cfg.User))
                await client.AuthenticateAsync(cfg.User, cfg.Password);
            await client.SendAsync(msg);
            await client.DisconnectAsync(true);
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SMTP send failed to {Recipients}", string.Join(", ", recipients));
            return (false, ex.Message);
        }
    }
}
