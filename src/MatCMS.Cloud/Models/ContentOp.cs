namespace MatCMS.Cloud.Models;

/// <summary>
/// One pending or completed CONTENT change for an instance — the write side of Stage 2, enqueued by the MCP
/// server (an AI, e.g. ChatGPT, changing a connected site). The cloud only ever ASKS: the op rides out on the
/// heartbeat as <see cref="MatCMS.Shared.PendingContentOp"/>, the instance applies it through its OWN
/// validated writers and reports the outcome, which is folded back into <see cref="DoneAt"/>/<see cref="Outcome"/>.
/// Modelled on <see cref="CloudBackup"/>/the pending-backup flow, not on the profile rollout — content is
/// per-site, so it does NOT ride <c>Profile.Revision</c> (that would thrash every sibling in the profile).
/// <para>The row <see cref="Id"/> is the op's identity on the wire (echoed back in the report). Cascades with
/// the instance.</para>
/// </summary>
public class ContentOp
{
    public int Id { get; set; }

    public int InstanceId { get; set; }
    public Instance? Instance { get; set; }

    /// <summary>What to do: "page.create" | "page.generate" | "site.generate" (Increment 1).</summary>
    public string Kind { get; set; } = "";

    /// <summary>Op parameters as JSON — shape depends on <see cref="Kind"/>. Stored and forwarded verbatim;
    /// the instance parses and RE-VALIDATES it. A string, so a new kind needs no schema change.</summary>
    public string PayloadJson { get; set; } = "";

    /// <summary>False = add-only (the safe default). True = overwrite; only set for a caller whose key may
    /// restore.</summary>
    public bool Overwrite { get; set; }

    /// <summary>Why the cloud is asking, in the site's language — written to the instance's log.</summary>
    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the instance reported this op done (UTC), or null while still pending. A pending op is
    /// offered on every beat until this is set; add-only ops are idempotent, so a re-offer after a lost
    /// report is harmless.</summary>
    public DateTime? DoneAt { get; set; }

    /// <summary>The instance's reported outcome: "applied" | "partial" | "skipped-exists" | "failed".</summary>
    public string? Outcome { get; set; }

    /// <summary>Human detail from the instance (created slug, skip reason, or error). Shown verbatim.</summary>
    public string? Detail { get; set; }

    /// <summary>For a READ op: the content the instance serialized back (it owns its format; the cloud only
    /// stores and forwards it). Fetched by the MCP client via get_content_op. Null for a write op.</summary>
    public string? ResultJson { get; set; }
}
