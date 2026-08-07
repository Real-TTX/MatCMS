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
    SyncFailed = 11
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
