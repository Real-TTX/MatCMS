namespace MatCMS.Cloud.Models;

/// <summary>
/// One message an instance handed to the cloud, waiting to be sent or already dealt with.
/// <para>Everything goes through here, never straight out: an instance must not sit waiting on a
/// foreign SMTP server while a visitor's form submission hangs, and a delivery that fails at 3am is
/// something to retry rather than to lose. The same rows are the audit trail — what the cloud sent
/// on whose behalf is not a thing to keep only in a log file.</para>
/// </summary>
public class SpooledMail
{
    public int Id { get; set; }

    public int InstanceId { get; set; }
    public Instance? Instance { get; set; }

    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Recipients, one per line. Stored as text because this row is a record of what was
    /// sent, not a model to query by recipient.</summary>
    public string Recipients { get; set; } = "";

    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";

    /// <summary>Where a reply goes. The only thing the instance may steer about addressing — the
    /// FROM is always the cloud's own, so no instance can send as somebody else.</summary>
    public string? ReplyTo { get; set; }

    public SpoolStatus Status { get; set; } = SpoolStatus.Queued;

    public int Attempts { get; set; }

    /// <summary>Earliest next try. Backoff lives in the row rather than in the worker's memory, so a
    /// restart does not turn every deferred message into an immediate retry storm.</summary>
    public DateTime? NextAttemptAt { get; set; }

    public DateTime? SentAt { get; set; }

    /// <summary>Why the last attempt failed. Kept on a message that later succeeds too — "it went
    /// out on the third try" is worth knowing.</summary>
    public string? LastError { get; set; }

    /// <summary>Attempts before a message is given up on. Roughly a day of retries with the backoff
    /// below, which covers an SMTP server being down overnight without keeping dead mail forever.</summary>
    public const int MaxAttempts = 8;

    /// <summary>How long to wait after the n-th failure: 1, 2, 5, 15, 30, 60, 120, 240 minutes.
    /// Quick at first — most failures are a blip — then slow enough not to hammer a server that is
    /// genuinely down.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(
        attempts switch { <= 1 => 1, 2 => 2, 3 => 5, 4 => 15, 5 => 30, 6 => 60, 7 => 120, _ => 240 });

    /// <summary>Rows kept per instance. Pruned when new mail is queued, because that is the only
    /// moment the table grows.</summary>
    public const int KeepPerInstance = 200;
}

public enum SpoolStatus
{
    Queued = 0,
    Sent = 1,

    /// <summary>Given up on after <see cref="SpooledMail.MaxAttempts"/>. Still listed, and can be
    /// put back in the queue by hand — an operator who fixed the mail server wants the backlog out,
    /// not a fresh start.</summary>
    Failed = 2,
}
