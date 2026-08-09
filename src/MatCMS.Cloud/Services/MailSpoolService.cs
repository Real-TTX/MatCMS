using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Drains the mail spool: takes what is due, sends it with the cloud's own SMTP configuration, and
/// puts a failure back with a growing delay instead of dropping it.
/// <para>One message at a time and in order. A cloud relaying for a handful of sites is not moving
/// enough mail to need concurrency, and a serial worker cannot half-send a batch — which is worth
/// more here than throughput.</para>
/// </summary>
public class MailSpoolService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MailSpoolService> _log;

    /// <summary>How often the spool is looked at. Short enough that a form notification does not sit
    /// visibly long, long enough to be nothing on a database.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    /// <summary>Messages per pass. Keeps one large backlog from monopolising the worker and starving
    /// whatever arrives while it drains.</summary>
    private const int BatchSize = 20;

    public MailSpoolService(IServiceProvider services, ILogger<MailSpoolService> log)
    {
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DrainAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Never let the loop die: the spool is the only thing standing between a site's
                // notifications and nowhere.
                _log.LogError(ex, "Mail spool pass failed.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<EmailService>();

        var now = DateTime.UtcNow;
        var due = await db.SpooledMails
            .Where(m => m.Status == SpoolStatus.Queued && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
            .OrderBy(m => m.Id)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (due.Count == 0) return;

        // Asked once per pass rather than per message: an unconfigured cloud would otherwise burn an
        // attempt per queued mail and push the whole backlog into the long end of the backoff.
        if (!await email.IsConfiguredAsync())
        {
            _log.LogWarning("Mail spool: {Count} message(s) waiting, but this cloud has no SMTP configuration.", due.Count);
            return;
        }

        foreach (var mail in due)
        {
            if (ct.IsCancellationRequested) break;

            var recipients = mail.Recipients.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // The cloud's own sender, always. What the instance may steer is where a reply goes —
            // that is the difference between a relay and an open door.
            var (ok, error) = await email.SendAsync(recipients, mail.Subject, mail.Body, mail.ReplyTo);

            mail.Attempts++;
            if (ok)
            {
                mail.Status = SpoolStatus.Sent;
                mail.SentAt = DateTime.UtcNow;
                mail.NextAttemptAt = null;
                _log.LogInformation("Mail spool: sent #{Id} for instance {Instance} on attempt {Attempt}.",
                    mail.Id, mail.InstanceId, mail.Attempts);
            }
            else
            {
                mail.LastError = error;
                if (mail.Attempts >= SpooledMail.MaxAttempts)
                {
                    mail.Status = SpoolStatus.Failed;
                    mail.NextAttemptAt = null;
                    _log.LogWarning("Mail spool: giving up on #{Id} for instance {Instance} after {Attempts} attempts: {Error}",
                        mail.Id, mail.InstanceId, mail.Attempts, error);
                }
                else
                {
                    mail.NextAttemptAt = DateTime.UtcNow + SpooledMail.Backoff(mail.Attempts);
                    _log.LogInformation("Mail spool: #{Id} failed (attempt {Attempt}), retrying at {At}: {Error}",
                        mail.Id, mail.Attempts, mail.NextAttemptAt, error);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
