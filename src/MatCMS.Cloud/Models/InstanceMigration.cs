namespace MatCMS.Cloud.Models;

/// <summary>
/// Moving an instance from one host to another (Hosting increment 5) — one row per attempt, written step by
/// step so the Hosting tab, the API and an AI agent can follow it, and so an interrupted move is recognisable
/// after a restart. Run by <c>MigrationService</c> in the background: it takes minutes, the data volume
/// travels through the cloud.
/// <para>Host ids: null = "Dieser Host" (the cloud's own daemon), otherwise a <see cref="Node"/>. Plain ids,
/// not foreign keys: the history of a move must survive deleting the node it came from.</para>
/// </summary>
public class InstanceMigration
{
    public int Id { get; set; }

    public int InstanceId { get; set; }
    public Instance? Instance { get; set; }

    public int? FromNodeId { get; set; }
    public int? ToNodeId { get; set; }
    public string FromName { get; set; } = "";
    public string ToName { get; set; } = "";

    /// <summary>running | succeeded | failed | rolled-back</summary>
    public string State { get; set; } = "running";

    /// <summary>The step it is in / ended in — shown next to the state.</summary>
    public string Step { get; set; } = "";

    /// <summary>Timestamped lines, one per step and decision (newline-separated).</summary>
    public string Log { get; set; } = "";

    /// <summary>True = remove the old container AND its volume once the site runs on the target; false (default)
    /// = retire it (renamed, stopped, never restarted by itself), so a way back exists.</summary>
    public bool RemoveSource { get; set; }

    public string? SourceContainerId { get; set; }
    public string? TargetContainerId { get; set; }

    /// <summary>The random id the data travels under (appdata/transfers/&lt;id&gt;.tar); only the source may upload
    /// it and only the target may download it, and only while this move runs.</summary>
    public string TransferId { get; set; } = "";

    /// <summary>The source was stopped — a failure from here on must bring it back (or say why it could not).</summary>
    public bool SourceStopped { get; set; }

    /// <summary>The target container was started — from here on the source must NEVER be started again unless
    /// the target is gone: two containers with one cloud identity would fight over the instance record.</summary>
    public bool TargetStarted { get; set; }

    public long? Bytes { get; set; }
    public string? RequestedBy { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }

    public bool Running => State == "running";
}
