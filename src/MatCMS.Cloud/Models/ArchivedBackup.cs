namespace MatCMS.Cloud.Models;

/// <summary>
/// A backup that has outlived the instance it was made on.
///
/// <para><b>Why this is a table of its own and not a flag on <see cref="CloudBackup"/>:</b> a cloud
/// backup hangs off an instance row and cascades away with it — on every way out, including the one
/// that keeps the volumes. That is correct for the backups a running site keeps here, and it is
/// exactly wrong for the one taken BECAUSE the site was about to be removed: taking a backup and
/// then losing it in the same operation is the worst outcome the "back up first, then remove" way
/// could possibly have. A row with no foreign key cannot be cascaded, so the file survives by not
/// belonging to anything any more.</para>
///
/// <para>The bytes move too, out of <c>appdata/backups/&lt;instanceId&gt;/</c> and into
/// <c>appdata/backups-archive/&lt;id&gt;/</c>. Two reasons, both practical: instance ids are handed
/// out again, so a later instance would inherit a folder holding somebody else's data, and the quota
/// pruner and the orphan finder both work over the live folder — an archived backup would be an
/// unexplained file to one and a candidate for deletion to the other.</para>
///
/// <para>No quota applies here and nothing prunes it. An archive that deleted its own contents to
/// stay under a limit would be a hole in the one place that promised there would not be one; it is
/// removed when an operator removes it, and the backup page says how much room it takes.</para>
/// </summary>
public class ArchivedBackup
{
    public int Id { get; set; }

    /// <summary>What the instance was called when it was removed. A copy, not a link — the row it
    /// would link to is the one that no longer exists. This is the only thing left that says whose
    /// site this was, so it is worth storing even though it can never be kept up to date.</summary>
    public string InstanceName { get; set; } = "";

    /// <summary>The instance's public id, kept for the same reason: two sites can carry the same
    /// display name, and a year later the name alone may not identify which one this was.</summary>
    public string InstancePublicId { get; set; } = "";

    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }

    /// <summary>When the instance made it.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When it reached the cloud.</summary>
    public DateTime UploadedAt { get; set; }

    public string Sha256 { get; set; } = "";

    /// <summary>When it was archived — i.e. when the instance was removed.</summary>
    public DateTime ArchivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Which way the instance went out, in plain words. What an operator finding this file
    /// in a year needs in order to know what it is.</summary>
    public string Reason { get; set; } = "";
}
