namespace MatCMS.Cloud.Models;

/// <summary>
/// A Docker host other than the cloud's own, running the node-agent (the cloud image in
/// <c>--node-agent</c> mode). The agent connects OUT to the cloud — the cloud never opens a connection to a
/// Docker host — pulls <see cref="NodeJob"/>s and reports back (docs/hosting-platform.md, increment 4).
/// <para>The cloud's own daemon is NOT a row: "Dieser Host" is virtual (<see cref="Instance.NodeId"/> null) and
/// configured by the cloud-wide hosting.* settings, so a single-host setup needs nothing new.</para>
/// </summary>
public class Node
{
    public int Id { get; set; }

    /// <summary>The node's id on the wire (URL segment of its heartbeat).</summary>
    public string PublicId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the agent token (shown once), like an instance token.</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>A revoked node's beats are answered 403 and it receives no jobs. The row (and the instances'
    /// history on it) stays until deleted.</summary>
    public bool Revoked { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }

    /// <summary>The "Node offline" notification went out for the current outage; the next beat re-arms it.</summary>
    public bool OfflineNotified { get; set; }

    // --- Reported by the agent ----------------------------------------------
    public string? AgentVersion { get; set; }
    public string? HostName { get; set; }
    public string? DockerVersion { get; set; }

    /// <summary>Why the agent cannot talk to its daemon (no socket, permission denied), or null.</summary>
    public string? DockerError { get; set; }

    /// <summary>The host's size as the agent reported it (CPU count, total memory in bytes). Null for an agent
    /// that predates the field.</summary>
    public int? Cpus { get; set; }
    public long? MemTotal { get; set; }

    /// <summary>The MatCMS containers on the host as last reported (JSON list of
    /// <c>NodeContainer</c>). Classification looks instances up here.</summary>
    public string? InventoryJson { get; set; }
    public DateTime? InventoryAt { get; set; }

    // --- Configuration (edited in the cloud, sent along with the jobs) -------
    // Exactly the fields of ProxySettings — the node's copy of what the cloud-wide hosting.* settings are
    // for "Dieser Host".
    public string ProxyKind { get; set; } = "none";
    public string? MatcadUrl { get; set; }

    /// <summary>DataProtection-encrypted (<c>SecretProtector</c>), never read back to a client.</summary>
    public string? MatcadTokenEnc { get; set; }
    public string? CaddyAdminUrl { get; set; }
    public string? CaddyServer { get; set; }
    public string ProxyUpstream { get; set; } = "network";
    public string? ProxyNetwork { get; set; }
    public string? ProxyUpstreamHost { get; set; }

    /// <summary>"Automatische Adressen": every instance on this node gets <c>name.{AutoDomainBase}</c> at the node's
    /// proxy. Needs a wildcard DNS record <c>*.{AutoDomainBase}</c> pointing at the node.</summary>
    public bool AutoDomainEnabled { get; set; }
    public string? AutoDomainBase { get; set; }

    /// <summary>How the cloud's EDGE proxy reaches this node (IP or host name, public or private network) — used
    /// when the edge forwards to <c>address:port</c> instead of the node's automatic address.</summary>
    public string? Address { get; set; }

    public int PortFrom { get; set; } = 9201;
    public int PortTo { get; set; } = 9299;

    /// <summary>Agent beats roughly every 25 s (long poll); two missed windows = offline.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(75);

    public bool IsOnline(DateTime utcNow) => !Revoked && LastSeenAt is { } s && utcNow - s < OfflineAfter;
}

/// <summary>
/// One unit of work for a node's agent. Handed out ONCE (<see cref="NodeJobState.Pending"/> →
/// <see cref="NodeJobState.Running"/>) — jobs like "create a container" are not idempotent, so a lost report
/// is a failed job, never a second container.
/// </summary>
public class NodeJob
{
    public long Id { get; set; }

    public int NodeId { get; set; }
    public Node? Node { get; set; }

    /// <summary>The instance the job is about, when there is one (not for proxy tests or provisioning).</summary>
    public int? InstanceId { get; set; }

    /// <summary>See <c>NodeJobKinds</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Parameters as JSON; the agent parses and checks them itself.</summary>
    public string PayloadJson { get; set; } = "";

    public NodeJobState State { get; set; } = NodeJobState.Pending;

    /// <summary>Human-readable outcome from the agent (or why the cloud gave up).</summary>
    public string? Message { get; set; }

    /// <summary>Structured result (container details, logs, route id …) as JSON.</summary>
    public string? ResultJson { get; set; }

    /// <summary>Who asked — "ui:admin", "api:&lt;key name&gt;", "mcp:…" — for the job history.</summary>
    public string? RequestedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary>Never picked up by the agent within this → expired. Short on purpose: a connected agent picks a job
    /// up within about a second (long poll), so an old pending job means the node was gone — and a "stop" the
    /// operator was told is still waiting must not fire by surprise minutes later when the node returns.</summary>
    public static readonly TimeSpan PendingTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Picked up but never reported within this → failed. Generous: moving a large site streams its whole
    /// data volume inside one job.</summary>
    public static readonly TimeSpan RunningTimeout = TimeSpan.FromMinutes(60);

    /// <summary>History kept per node (pruned on write).</summary>
    public const int KeepPerNode = 200;
}

public enum NodeJobState
{
    Pending = 0,
    Running = 1,
    Done = 2,
    Failed = 3,
}
