using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Takes messages from instances into the spool, and decides who may put one there.
/// </summary>
public class MailSpool
{
    private readonly AppDbContext _db;
    public MailSpool(AppDbContext db) => _db = db;

    /// <summary>Messages one instance may hand over per hour. Generous for form notifications and
    /// low enough that a compromised site cannot turn the cloud's mail server into a spam relay
    /// before anybody notices — the refusals show up in the instance's own report.</summary>
    public const int MaxPerHour = 60;

    /// <summary>Recipients per message. A notification goes to a handful of people; a hundred is a
    /// mailing list, and this is not one.</summary>
    public const int MaxRecipients = 20;

    public sealed record Result(bool Queued, string? Error);

    /// <summary>
    /// Accepts a message for later delivery, or says why not.
    /// <para>Nothing is sent here. The instance must not be left waiting on a foreign SMTP server
    /// while a visitor's form submission hangs, and a delivery that fails is then something to retry
    /// rather than something lost.</para>
    /// </summary>
    public async Task<Result> EnqueueAsync(Instance instance, MailRequest req, CancellationToken ct = default)
    {
        // The profile has to have asked for this. An instance holding a valid token is not by itself
        // permission to send mail through somebody else's server.
        var profile = instance.ProfileId is int pid
            ? await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct)
            : null;
        if (profile is null || !profile.SyncSmtp || profile.MailSource != MailSources.Cloud)
            return new Result(false, "Für dieses Profil ist der Mailversand über die Cloud nicht aktiviert.");

        var recipients = (req.To ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (recipients.Count == 0) return new Result(false, "Keine Empfänger angegeben.");
        if (recipients.Count > MaxRecipients)
            return new Result(false, $"Zu viele Empfänger ({recipients.Count}, erlaubt sind {MaxRecipients}).");
        if (string.IsNullOrWhiteSpace(req.Subject))
            return new Result(false, "Kein Betreff angegeben.");

        var since = DateTime.UtcNow.AddHours(-1);
        var lastHour = await _db.SpooledMails.CountAsync(m => m.InstanceId == instance.Id && m.QueuedAt >= since, ct);
        if (lastHour >= MaxPerHour)
            return new Result(false, $"Stundenlimit erreicht ({MaxPerHour} Nachrichten).");

        _db.SpooledMails.Add(new SpooledMail
        {
            InstanceId = instance.Id,
            Recipients = string.Join("\n", recipients),
            Subject = req.Subject.Trim(),
            Body = req.Body ?? "",
            ReplyTo = string.IsNullOrWhiteSpace(req.ReplyTo) ? null : req.ReplyTo.Trim(),
            NextAttemptAt = DateTime.UtcNow,
        });

        await PruneAsync(instance.Id, ct);
        await _db.SaveChangesAsync(ct);
        return new Result(true, null);
    }

    /// <summary>Keeps the history bounded. Done on write because that is the only moment the table
    /// grows, and only settled rows are dropped — anything still queued is still owed to somebody.</summary>
    private async Task PruneAsync(int instanceId, CancellationToken ct)
    {
        var settled = await _db.SpooledMails
            .Where(m => m.InstanceId == instanceId && m.Status != SpoolStatus.Queued)
            .OrderByDescending(m => m.Id)
            .Skip(SpooledMail.KeepPerInstance)
            .ToListAsync(ct);
        if (settled.Count > 0) _db.SpooledMails.RemoveRange(settled);
    }
}
