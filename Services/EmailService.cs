using System.Diagnostics;
using System.Text;
using MatCMS.Data;
using MailKit;
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

        // Per-phase timing + a full SMTP protocol trace (secrets redacted) so a failure says WHERE it
        // hangs — connect/TLS, auth, or send — instead of a bare "operation timed out". The trace goes
        // to the container log (docker logs); the returned message names the phase + elapsed ms.
        var sw = Stopwatch.StartNew();
        var phase = "prepare";
        using var traceStream = new MemoryStream();
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

            _log.LogInformation("SMTP: connecting to {Host}:{Port} mode={Secure} user={User} ssl={Ssl}",
                cfg.Host, cfg.Port, secure, string.IsNullOrWhiteSpace(cfg.User) ? "(none)" : cfg.User, cfg.Ssl);

            // ProtocolLogger records the full C:/S: dialog; RedactSecrets hides the AUTH credentials.
            var logger = new ProtocolLogger(traceStream) { RedactSecrets = true };
            using var client = new SmtpClient(logger);
            client.Timeout = 30_000; // 30s hard cap per operation

            phase = $"connect {cfg.Host}:{cfg.Port} ({secure})";
            var t0 = sw.ElapsedMilliseconds;
            await client.ConnectAsync(cfg.Host, cfg.Port, secure);
            _log.LogInformation("SMTP: connected in {Ms} ms", sw.ElapsedMilliseconds - t0);

            if (!string.IsNullOrWhiteSpace(cfg.User))
            {
                phase = "authenticate";
                var t1 = sw.ElapsedMilliseconds;
                await client.AuthenticateAsync(cfg.User, cfg.Password);
                _log.LogInformation("SMTP: authenticated in {Ms} ms", sw.ElapsedMilliseconds - t1);
            }

            phase = "send";
            var t2 = sw.ElapsedMilliseconds;
            await client.SendAsync(msg);
            await client.DisconnectAsync(true);
            _log.LogInformation("SMTP: sent in {Ms} ms (total {Total} ms) to {Recipients}",
                sw.ElapsedMilliseconds - t2, sw.ElapsedMilliseconds, string.Join(", ", recipients));
            return (true, null);
        }
        catch (Exception ex)
        {
            var trace = ReadTrace(traceStream);
            _log.LogWarning(ex,
                "SMTP FAILED in phase '{Phase}' after {Ms} ms (host={Host} port={Port}) to {Recipients}. Protocol trace:\n{Trace}",
                phase, sw.ElapsedMilliseconds, cfg.Host, cfg.Port, string.Join(", ", recipients), trace);

            // Full exception chain (the inner exception usually carries the real cause, e.g. the socket error).
            var detail = new StringBuilder($"{ex.GetType().Name}: {ex.Message}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                detail.Append($" ⇐ {inner.GetType().Name}: {inner.Message}");
            return (false, $"[Phase: {phase}, {sw.ElapsedMilliseconds} ms] {detail}");
        }
    }

    /// <summary>Reads the captured SMTP protocol dialog (best-effort; secrets already redacted by the logger).</summary>
    private static string ReadTrace(MemoryStream ms)
    {
        try
        {
            var s = Encoding.UTF8.GetString(ms.ToArray());
            if (string.IsNullOrWhiteSpace(s))
                return "(no protocol data — the connection was never established, i.e. connect/TLS timed out)";
            return s.Length > 6000 ? "…" + s[^6000..] : s;
        }
        catch { return "(protocol trace unavailable)"; }
    }
}
