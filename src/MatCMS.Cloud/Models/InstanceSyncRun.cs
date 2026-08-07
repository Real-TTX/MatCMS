namespace MatCMS.Cloud.Models;

/// <summary>
/// One completed apply on an instance, as the instance reported it. The cloud keeps the history; it
/// never derives it — a run is appended only when the instance says the run is new
/// (<c>HeartbeatRequest.SyncRunAt</c>), not when a report merely looks different.
/// <para>Kept per instance and pruned to <see cref="InstanceSyncRun.KeepPerInstance"/> rows: this
/// table grows with every rollout across every site, and nobody reads the two-hundredth entry.</para>
/// </summary>
public class InstanceSyncRun
{
    /// <summary>How many runs are kept per instance. Older ones are dropped as new ones arrive.</summary>
    public const int KeepPerInstance = 50;

    public int Id { get; set; }
    public int InstanceId { get; set; }
    public Instance? Instance { get; set; }

    /// <summary>When the apply finished, as reported BY THE INSTANCE — not when the cloud heard about
    /// it. A site that was offline for an hour still shows the hour-old time it actually ran.</summary>
    public DateTime RanAt { get; set; }

    /// <summary>When this row was written here. Differs from <see cref="RanAt"/> exactly when the
    /// instance could not reach the cloud right away.</summary>
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Profile revision the instance applied in this run.</summary>
    public int Revision { get; set; }

    /// <summary>What went wrong, verbatim from the instance. Null = the run succeeded.</summary>
    public string? Error { get; set; }

    /// <summary>The full per-item report as JSON, stored exactly as received.</summary>
    public string ReportJson { get; set; } = "[]";

    // Counts, denormalised on write so a listing does not have to parse N JSON blobs.
    public int Installed { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }

    public int Total => Installed + Updated + Skipped + Failed;
}
