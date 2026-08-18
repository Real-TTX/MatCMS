namespace MatCMS.Cloud.Models;

/// <summary>
/// How the cloud can reach an instance's container. Decided by <c>InstanceService.Classify</c> on
/// every heartbeat - never asked from the user, because the answer changes when a site is moved.
/// </summary>
public enum InstanceHosting
{
    /// <summary>No heartbeat has been matched yet.</summary>
    Unknown = 0,

    /// <summary>The reported container was found on the Docker daemon this cloud talks to, so the
    /// cloud can pull the new image and recreate the container itself.</summary>
    Local = 1,

    /// <summary>Runs on a different host (or the cloud has no Docker access). Notify only.</summary>
    Remote = 2
}

/// <summary>Where an instance stands in the enrollment flow.</summary>
public enum InstanceStatus
{
    /// <summary>Enrolled with a valid join code but not yet accepted. Heartbeats are recorded so the
    /// operator can see what is asking to join, but NO configuration is handed out.</summary>
    Pending = 0,

    /// <summary>Accepted — receives its profile's configuration.</summary>
    Approved = 1,

    /// <summary>Turned away. Heartbeats are refused with 403 so the instance stops asking and can
    /// show the operator why.</summary>
    Rejected = 2
}

/// <summary>A connected MatCMS installation.</summary>
public class Instance
{
    public int Id { get; set; }

    /// <summary>Public identifier handed to the instance at pairing; used in the API routes so the
    /// internal key is never exposed and cannot be enumerated.</summary>
    public string PublicId { get; set; } = "";

    /// <summary>SHA-256 of the bearer token the instance sends as <c>X-MatCMS-Instance-Token</c>.
    /// The raw token is shown exactly once, at pairing - the cloud never stores it.</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>Admin-editable label. Defaults to the site name the instance reports.</summary>
    public string Name { get; set; } = "";

    public InstanceStatus Status { get; set; } = InstanceStatus.Approved;

    /// <summary>The profile whose configuration and policy apply. Null = no configuration is pushed
    /// and the global settings act as the policy.</summary>
    public int? ProfileId { get; set; }
    public Profile? Profile { get; set; }

    // --- Sync bookkeeping ---------------------------------------------------
    /// <summary>Profile revision the instance last reported as successfully applied. Differs from
    /// <c>Profile.Revision</c> exactly while the instance is out of date.</summary>
    public int AppliedRevision { get; set; }

    /// <summary>What went wrong the last time the instance tried to apply its configuration; null
    /// when the last attempt succeeded.</summary>
    public string? LastSyncError { get; set; }

    /// <summary>The instance's own account of the last apply, item by item, as JSON. Stored verbatim:
    /// the cloud only keeps the record, it never derives it. Null means the instance has not reported
    /// one — which is NOT the same as "nothing happened".</summary>
    public string? LastSyncReportJson { get; set; }

    /// <summary>The instance-reported timestamp of the last apply we already recorded. Compared
    /// against the heartbeat to decide whether a report is a NEW run or the same one being repeated
    /// every minute.</summary>
    public DateTime? LastSyncRunAt { get; set; }

    public DateTime? LastSyncUtc { get; set; }

    /// <summary>Public URL of the site, for the "open" link. Reported by the instance, editable.</summary>
    public string? Url { get; set; }

    public string? Notes { get; set; }

    // --- Last heartbeat -----------------------------------------------------
    public DateTime? LastHeartbeatUtc { get; set; }

    /// <summary>Running MatCMS version as reported (e.g. "1.0.42-20260806120000").</summary>
    public string? Version { get; set; }

    /// <summary>Contract version the instance speaks. Older than
    /// <c>InstanceService.CurrentProtocolVersion</c> = "Outdated" badge.</summary>
    public int ProtocolVersion { get; set; }

    public string? HostName { get; set; }

    /// <summary>Container id the instance read from its own cgroup/hostname. The key to the
    /// local/remote decision - matched against the containers on the mounted engine socket.</summary>
    public string? ContainerId { get; set; }

    /// <summary>Image reference the instance believes it runs (e.g. "ghcr.io/real-ttx/matcms:latest").</summary>
    public string? ImageRef { get; set; }

    public InstanceHosting Hosting { get; set; } = InstanceHosting.Unknown;

    /// <summary>Container name resolved on the local daemon - shown so an operator can see exactly
    /// which container an "Update now" would recreate. Null while remote.</summary>
    public string? LocalContainerName { get; set; }

    /// <summary>
    /// Host port the local container publishes, e.g. 9101. Used to offer a preview address for an
    /// instance that has no public URL configured yet - which is the normal state of a site that was
    /// never given a canonical URL. Only meaningful when the operator's browser is on the Docker
    /// host, so it is a FALLBACK behind <see cref="Url"/>, never a replacement.
    /// </summary>
    public int? LocalPort { get; set; }

    /// <summary>
    /// True when the local container carries the label this cloud stamps on everything it creates
    /// itself (<see cref="Services.DockerHostService.ManagedLabel"/>) — i.e. the cloud built this
    /// site and owns its container and data volume.
    ///
    /// <para>This is the difference between "we may tear this down" and "we may only forget it".
    /// <see cref="Hosting"/> alone does NOT answer that: local merely means the container happens to
    /// sit on the daemon we can reach, which is also true of a site somebody started by hand next to
    /// us. Only the label says who put it there.</para>
    ///
    /// <para>Re-derived on EVERY heartbeat like the other two fields, and for the same reason: a site
    /// that moves, or a container that was replaced by hand, must fall back to "not ours" instead of
    /// leaving the cloud holding a licence to delete something it no longer recognises.</para>
    /// </summary>
    public bool CloudManaged { get; set; }

    /// <summary>Address to show the site at: what the instance reported (or an operator typed), else
    /// the local container's published port.</summary>
    public string? PreviewUrl =>
        !string.IsNullOrWhiteSpace(Url) ? Url
        : LocalPort is not null ? $"http://localhost:{LocalPort}"
        : null;

    /// <summary>True when the preview address is only a guess from the port mapping — the UI says so
    /// rather than letting an operator wonder why a blank frame appeared.</summary>
    public bool PreviewIsGuessed => string.IsNullOrWhiteSpace(Url) && LocalPort is not null;

    // --- Reported content stats (dashboard) --------------------------------
    public int PageCount { get; set; }
    public int PluginCount { get; set; }
    public int UserCount { get; set; }

    // --- Notification bookkeeping ------------------------------------------
    /// <summary>Set once an offline alert has been sent, cleared on the next heartbeat, so the
    /// dead-man switch mails once per outage instead of once per check.</summary>
    public bool OfflineNotified { get; set; }

    /// <summary>The version an update mail was last sent for - stops one mail per check for the
    /// same available release.</summary>
    public string? UpdateNotifiedVersion { get; set; }

    /// <summary>
    /// The version an automatic update was last ATTEMPTED for. Same idea as
    /// <see cref="UpdateNotifiedVersion"/>, and just as necessary: "update available" only becomes
    /// false once the instance has restarted AND reported its new version, so without this the
    /// watchdog would re-run the update — and mail about a failure — every 60 s forever. Cleared on
    /// a successful update so a later release is attempted again.
    /// </summary>
    public string? AutoUpdateAttemptedVersion { get; set; }

    // --- A backup the cloud asked this instance to make ---------------------
    // The mirror of the restore request, which lives on the backup row because the file it names
    // already exists. A backup request has no row to hang on yet — the file it is asking for is the
    // thing that does not exist — so it lives here, on the instance being asked.

    /// <summary>
    /// The outstanding backup request, or 0 for none. Bumped rather than reused, so an upload that
    /// answers an older request can never be mistaken for an answer to the current one.
    /// </summary>
    public int BackupRequestId { get; set; }

    /// <summary>When the cloud asked. Shown, and used to tell an operator how long a wait has been
    /// going on — never to end one.</summary>
    public DateTime? BackupRequestedAt { get; set; }

    /// <summary>
    /// What the instance said went wrong, if it answered at all. Set means the request is over and
    /// failed: the cloud stops asking, says why, and offers to ask again.
    /// <para>Stopping matters. Making a backup takes minutes and blocks the site's heartbeat while it
    /// runs, so re-asking on every beat for something that fails every time would grind a site down
    /// for as long as nobody looked.</para>
    /// </summary>
    public string? BackupRequestError { get; set; }

    /// <summary>Set once the operator has been mailed that a request is still unanswered, so that
    /// notice goes out once per request rather than once per check.</summary>
    public bool BackupWaitNotified { get; set; }

    // --- A removal waiting for that backup ----------------------------------

    /// <summary>
    /// The removal that is waiting for the backup above: <c>keep-data</c> or <c>full</c>, null for
    /// no removal pending. A string for the same reason the sync modes are strings — a value that
    /// does not parse must fall to the harmless end rather than to whatever happens to be 0.
    /// <para><b>It never expires.</b> There is no age at which this turns into a deletion: the whole
    /// point of the wait is that the backup has to be here first, and a timeout that removed the site
    /// anyway would be exactly the accident this way exists to prevent. It is ended by the backup
    /// arriving, or by an operator taking it back — nothing else.</para>
    /// </summary>
    public string? PendingRemovalMode { get; set; }

    /// <summary>The container id the operator was SHOWN when they confirmed. Carried across the wait
    /// so the check that guards the immediate ways still holds for the delayed one: between the
    /// question and the deed the site may have moved or been rebuilt, and removing something nobody
    /// was ever shown is what must not happen.</summary>
    public string? PendingRemovalContainerId { get; set; }

    /// <summary>When the operator confirmed the removal.</summary>
    public DateTime? PendingRemovalAt { get; set; }

    /// <summary>Why the delayed removal could not be carried out once the backup was there. Kept so
    /// the wait ends in something visible instead of in silence.</summary>
    public string? PendingRemovalError { get; set; }

    /// <summary>True while a removal is waiting for its backup — what the list and the detail page
    /// badge, so an instance in this state is never merely "still there".</summary>
    public bool RemovalPending => PendingRemovalMode is not null;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True once the instance has ever checked in.</summary>
    public bool HasConnected => LastHeartbeatUtc is not null;
}
