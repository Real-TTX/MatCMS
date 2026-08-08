using System.Diagnostics;
using System.Text;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MatCMS.Cloud.Data;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Sends e-mail via the SMTP server configured under Einstellungen → SMTP. Uses MailKit so BOTH
/// connection modes work: implicit SSL/TLS (SMTPS, port 465, e.g. IONOS) and STARTTLS (587/25) —
/// the built-in System.Net.Mail.SmtpClient can't do implicit SSL. Never throws to the caller;
/// failures come back as (false, error) so a notification loop is never taken down by a mail server.
/// </summary>
public class EmailService
{
    private readonly AppDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly ILogger<EmailService> _log;

    public EmailService(AppDbContext db, SecretProtector secrets, ILogger<EmailService> log)
    {
        _db = db;
        _secrets = secrets;
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
        var map = await _db.CloudSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        string G(string k) => map.TryGetValue(k, out var v) ? (v ?? "") : "";
        if (!int.TryParse(G(SettingKeys.SmtpPort), out var port) || port <= 0) port = 587;
        var ssl = G(SettingKeys.SmtpSsl).Trim().ToLowerInvariant();

        return new SmtpConfig(
            G(SettingKeys.SmtpHost).Trim(), port, G(SettingKeys.SmtpUser),
            // Stored encrypted (SecretProtector); an unmarked legacy value passes through unchanged.
            _secrets.Unprotect(G(SettingKeys.SmtpPassword)) ?? "",
            G(SettingKeys.SmtpFromEmail).Trim(), G(SettingKeys.SmtpFromName).Trim(),
            ssl is "true" or "on" or "1" or "yes");
    }

    public async Task<bool> IsConfiguredAsync() => (await GetConfigAsync()).IsConfigured;

    /// <summary>Splits a free-text recipient list (comma / semicolon / newline separated).</summary>
    public static List<string> ParseRecipients(string? raw) =>
        (raw ?? "")
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>The addresses notifications go to: the configured list, or every cloud user's e-mail
    /// when it is empty (so a fresh install still reaches somebody).</summary>
    public async Task<List<string>> ResolveRecipientsAsync()
    {
        var configured = await _db.CloudSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.NotifyRecipients)
            .Select(s => s.Value).FirstOrDefaultAsync();

        var list = ParseRecipients(configured);
        if (list.Count > 0) return list;

        return await _db.Users.AsNoTracking()
            .Where(u => u.Email != null && u.Email != "")
            .Select(u => u.Email!)
            .ToListAsync();
    }

    /// <summary>Sends a plain-text mail using the saved SMTP config. Returns (ok, error); never throws.</summary>
    public async Task<(bool ok, string? error)> SendAsync(
        IEnumerable<string> to, string subject, string body, string? replyTo = null)
        => await SendCoreAsync(await GetConfigAsync(), to, subject, body, replyTo);

    /// <summary>Sends a test e-mail with an explicit (possibly unsaved) config — used by the SMTP test button.</summary>
    public async Task<(bool ok, string? error)> SendTestAsync(SmtpConfig cfg, string to)
        => await SendCoreAsync(cfg, [to],
            "MatCMS.Cloud – SMTP-Test",
            "Diese Test-E-Mail bestätigt, dass die SMTP-Einstellungen funktionieren.\r\n\r\n– MatCMS.Cloud");

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

            // Pick the right TLS mode: port 465 = implicit SSL on connect; otherwise STARTTLS
            // (opportunistic when the SSL switch is off, so a plain server still works).
            var secure = cfg.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : cfg.Ssl ? SecureSocketOptions.StartTls : SecureSocketOptions.StartTlsWhenAvailable;

            var logger = new ProtocolLogger(traceStream) { RedactSecrets = true };
            using var client = new SmtpClient(logger);
            client.Timeout = 30_000; // 30s hard cap per operation

            phase = $"connect {cfg.Host}:{cfg.Port} ({secure})";
            await client.ConnectAsync(cfg.Host, cfg.Port, secure);

            if (!string.IsNullOrWhiteSpace(cfg.User))
            {
                phase = "authenticate";
                await client.AuthenticateAsync(cfg.User, cfg.Password);
            }

            phase = "send";
            await client.SendAsync(msg);
            await client.DisconnectAsync(true);
            _log.LogInformation("SMTP: sent in {Total} ms to {Recipients}", sw.ElapsedMilliseconds, string.Join(", ", recipients));
            return (true, null);
        }
        catch (Exception ex)
        {
            var trace = ReadTrace(traceStream);
            _log.LogWarning(ex,
                "SMTP FAILED in phase '{Phase}' after {Ms} ms (host={Host} port={Port}) to {Recipients}. Protocol trace:\n{Trace}",
                phase, sw.ElapsedMilliseconds, cfg.Host, cfg.Port, string.Join(", ", recipients), trace);

            // Full exception chain (the inner exception usually carries the real cause).
            var detail = new StringBuilder($"{ex.GetType().Name}: {ex.Message}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                detail.Append($" ⇐ {inner.GetType().Name}: {inner.Message}");
            return (false, $"[Phase: {phase}, {sw.ElapsedMilliseconds} ms] {detail}");
        }
    }

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
