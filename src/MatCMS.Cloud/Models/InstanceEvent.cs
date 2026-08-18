namespace MatCMS.Cloud.Models;

public enum InstanceEventKind
{
    Connected = 0,
    Offline = 1,
    Recovered = 2,
    UpdateAvailable = 3,
    UpdateStarted = 4,
    UpdateSucceeded = 5,
    UpdateFailed = 6,
    HostingChanged = 7,
    Approved = 8,
    Rejected = 9,
    SyncApplied = 10,
    SyncFailed = 11,

    /// <summary>A backup was restored onto the instance — or the attempt failed. Its own kind
    /// because it is the one event here that OVERWRITES a live site, and it should be findable as
    /// such rather than buried among sync entries.</summary>
    BackupRestored = 12,
    BackupRestoreFailed = 13,

    /// <summary>The cloud asked this instance for a backup, and what came of it. Its own kind
    /// because a removal can be waiting on exactly this — when nothing happens, this is the trail
    /// that says whether the request went out, was answered, or was refused.</summary>
    BackupRequested = 14,
    BackupRequestFailed = 15,

    /// <summary>A removal that is waiting for its backup, and its end. Kept apart from the request
    /// above because "the backup arrived" and "the site was therefore removed" are two different
    /// facts, and the second one has to be findable on its own.</summary>
    RemovalPending = 16,
    RemovalCancelled = 17
}

/// <summary>Audit trail per instance: what happened, and whether a notification went out for it.
/// This is what the instance detail page renders and what the notifier reads to stay idempotent.</summary>
public class InstanceEvent
{
    public int Id { get; set; }
    public int InstanceId { get; set; }
    public Instance? Instance { get; set; }

    public InstanceEventKind Kind { get; set; }
    public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Notified { get; set; }
}
