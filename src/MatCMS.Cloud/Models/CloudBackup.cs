namespace MatCMS.Cloud.Models;

/// <summary>
/// One backup an instance uploaded, as a row. The ZIP itself lives as a FILE in the cloud's appdata
/// volume — a few hundred megabytes as a SQLite blob would be a problem of its own.
/// <para>Because file and row are two things, they can drift apart: a row whose file was deleted by
/// hand, a file whose row went with a deleted instance. Both are surfaced rather than ignored — see
/// the orphan list on the backup page.</para>
/// <para><b>Not encrypted at rest.</b> A backup is the whole site, so that is a real decision and not
/// an oversight: the DataProtection keys live in the same volume as the backups, so encrypting with
/// them barely helps against someone who has the disk — while adding the failure mode "keys lost, every
/// backup unrestorable". If the volume needs protecting, that belongs at the volume level.</para>
/// </summary>
public class CloudBackup
{
    public int Id { get; set; }

    public int InstanceId { get; set; }
    public Instance? Instance { get; set; }

    /// <summary>File name as the instance produced it, e.g. <c>villa-nika_auto_2026-08-11-0300.zip</c>.
    /// Kept verbatim so a downloaded file is the same one the site would have made locally.</summary>
    public string FileName { get; set; } = "";

    public long SizeBytes { get; set; }

    /// <summary>When the INSTANCE made it — not when it arrived. A site that was offline for a day
    /// uploads yesterday's backup today, and the older date is the one that matters.</summary>
    public DateTime CreatedAt { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    /// <summary>"auto" (the schedule) or "manual". Free text on purpose: it comes from the instance,
    /// and a value this cloud has not heard of should show as itself rather than be dropped.</summary>
    public string Origin { get; set; } = "auto";

    /// <summary>SHA-256 of the uploaded bytes, computed while streaming it in. Lets a download be
    /// checked, and makes a truncated upload visible instead of silently unrestorable.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>
    /// The backup request this file answers (<see cref="Instance.BackupRequestId"/>), or 0 for one
    /// the site made on its own schedule.
    /// <para>This is what "the backup has arrived" means. Not "the instance said it made one" and not
    /// "a backup turned up after we asked": a site that was offline for a week uploads last week's
    /// file the moment it returns, and a removal that took that for its answer would delete a site
    /// against a backup of the site as it was a week ago.</para>
    /// </summary>
    public int RequestId { get; set; }

    /// <summary>Set when an operator asked for this backup to be restored. The cloud only MARKS it —
    /// the instance picks it up on its next heartbeat and does the work itself, because nothing here
    /// ever reaches into a site.</summary>
    public DateTime? RestoreRequestedAt { get; set; }

    /// <summary>When the instance reported the restore as done. Cleared together with the request
    /// when a new one is made, so the pair always describes the same attempt.</summary>
    public DateTime? RestoreDoneAt { get; set; }

    /// <summary>What the instance said went wrong, if anything.</summary>
    public string? RestoreError { get; set; }

    /// <summary>Waiting to be picked up. A request that already has an outcome is not pending.</summary>
    public bool RestorePending => RestoreRequestedAt is not null && RestoreDoneAt is null && RestoreError is null;
}
