namespace MatCMS.Cloud.Services.Nodes;

/// <summary>
/// The wire contract between the cloud and a node-agent (Hosting increment 4). Deliberately NOT in
/// <c>MatCMS.Shared</c>: both ends are this project (the agent is the cloud image in <c>--node-agent</c> mode),
/// and MatCMS instances never see it — so it moves without touching <c>CloudProtocol.Version</c>.
/// <para>The agent always connects OUT: <c>POST /api/nodes/{publicId}/heartbeat</c>, token in
/// <see cref="TokenHeader"/>. The cloud answers with jobs; with nothing to hand out it holds the request up to
/// <see cref="LongPoll"/> and answers the moment a job is enqueued.</para>
/// </summary>
public static class NodeProtocol
{
    public const int Version = 1;
    public const string TokenHeader = "X-MatCMS-Node-Token";
    public static readonly TimeSpan LongPoll = TimeSpan.FromSeconds(25);
}

public sealed class NodeHeartbeatRequest
{
    public int ProtocolVersion { get; set; }
    public string? AgentVersion { get; set; }
    public string? HostName { get; set; }
    public string? DockerVersion { get; set; }
    public string? DockerError { get; set; }

    /// <summary>The MatCMS containers on the host; null = could not be listed (keep the last inventory).</summary>
    public List<NodeContainer>? Containers { get; set; }

    public List<NodeJobReport> Reports { get; set; } = new();

    /// <summary>True = nothing to report, the agent is idle: the cloud may hold the request (long poll).</summary>
    public bool Wait { get; set; }
}

public sealed class NodeContainer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public string State { get; set; } = "";
    public int? PublishedPort { get; set; }
    public bool CloudManaged { get; set; }
}

public sealed class NodeJobReport
{
    public long JobId { get; set; }
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? ResultJson { get; set; }
}

public sealed class NodeHeartbeatResponse
{
    public int ProtocolVersion { get; set; } = NodeProtocol.Version;
    public List<NodeJobOffer> Jobs { get; set; } = new();
}

public sealed class NodeJobOffer
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string PayloadJson { get; set; } = "";
}

/// <summary>The job kinds an agent executes. Anything else is refused on the node.</summary>
public static class NodeJobKinds
{
    public const string Details = "container.details";
    public const string Logs = "container.logs";
    public const string Power = "container.power";
    public const string Update = "container.update";
    public const string Create = "instance.create";
    public const string Proxy = "proxy";
}

/// <summary>Payload of the container.* jobs.</summary>
/// <param name="Action">container.power: start | stop | restart.</param>
public sealed record ContainerJob(string ContainerId, string? Action = null, int Tail = 200);
