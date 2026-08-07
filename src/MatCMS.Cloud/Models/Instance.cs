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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True once the instance has ever checked in.</summary>
    public bool HasConnected => LastHeartbeatUtc is not null;
}
