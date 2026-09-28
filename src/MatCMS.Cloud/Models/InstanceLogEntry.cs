namespace MatCMS.Cloud.Models;

/// <summary>
/// A log entry an instance reported on its heartbeat (see <see cref="MatCMS.Shared.LogReport"/>) — the
/// cloud's mirror of a site's newest errors/5xx, so *Protokoll* can show them per instance without ever
/// reaching in. Deduped on (<see cref="InstanceId"/>, <see cref="SourceId"/>) so the same entries riding
/// on successive beats are stored once; pruned per instance so the table cannot grow without bound.
/// Cascades with the instance. Deliberately WITHOUT the exception/stack blob — this is the overview, not
/// the full log (that would be the on-demand pull, still on the backlog).
/// </summary>
public class InstanceLogEntry
{
    public int Id { get; set; }

    public int InstanceId { get; set; }
    public Instance? Instance { get; set; }

    /// <summary>The entry's id in the INSTANCE's own log table — the dedup key together with the instance.</summary>
    public long SourceId { get; set; }

    /// <summary>When the entry was recorded on the instance (UTC).</summary>
    public DateTime TimeUtc { get; set; }

    /// <summary>"Error" | "Warning" | "Info".</summary>
    public string Level { get; set; } = "Error";

    public string Message { get; set; } = "";
    public string? Category { get; set; }
    public string? Path { get; set; }
    public string? Method { get; set; }
    public int? StatusCode { get; set; }

    /// <summary>Full exception text — only present for entries from the on-demand FULL log upload
    /// (<see cref="MatCMS.Shared.LogUpload"/>); null for the lean per-heartbeat overview.</summary>
    public string? Exception { get; set; }
}
