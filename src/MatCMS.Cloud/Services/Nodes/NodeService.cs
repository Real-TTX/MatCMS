using System.Security.Cryptography;
using System.Text;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services.Proxy;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services.Nodes;

/// <summary>
/// Everything about nodes on the cloud side — one implementation behind the Nodes pages, <c>/api/v1/nodes</c>
/// and the MCP node tools (API-first): enrolment (token shown once, stored SHA-256), configuration, the agent's
/// heartbeat, and running jobs on a node. Callers that act on an INSTANCE on a node (HostingActionsService,
/// ProxyService, HostingService) go through <see cref="RunAsync"/>, so they keep their synchronous shape.
/// </summary>
public class NodeService
{
    private readonly AppDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly NodeSignal _signal;
    private readonly CloudContext _cloud;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<NodeService> _log;
    private readonly DockerHostService _docker;
    private readonly IHttpClientFactory _httpFactory;

    public NodeService(AppDbContext db, SecretProtector secrets, NodeSignal signal, CloudContext cloud,
        IHttpContextAccessor http, ILogger<NodeService> log, DockerHostService docker, IHttpClientFactory httpFactory)
    {
        _db = db; _secrets = secrets; _signal = signal; _cloud = cloud; _http = http; _log = log;
        _docker = docker; _httpFactory = httpFactory;
    }

    /// <summary>
    /// Runs a job on <paramref name="node"/> — or, for null, on "Dieser Host": the SAME executor in-process, with
    /// the cloud's own daemon and the transfer file written directly. Lets code that spans two hosts (moving a
    /// site) stay one code path instead of a branch per host.
    /// </summary>
    public async Task<JobOutcome> RunOnAsync(Node? node, string kind, object payload, int? instanceId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (node is not null) return await RunAsync(node, kind, payload, instanceId, timeout, ct);
        var http = _httpFactory.CreateClient("proxy");
        http.Timeout = TimeSpan.FromSeconds(15);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var r = await NodeJobExecutor.ExecuteAsync(new NodeJobOffer { Kind = kind, PayloadJson = NodeJobExecutor.Serialize(payload) },
            _docker, http, new FileNodeTransfer(), cts.Token);
        return new(true, r.Ok, r.Message ?? "", r.ResultJson, 0);
    }

    // ---- Enrolment & configuration ----------------------------------------------------------------

    public async Task<(Node? Node, string? Token, string? Error)> CreateAsync(string? name, CancellationToken ct = default)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return (null, null, "Bitte einen Namen angeben.");
        if (await _db.Nodes.AnyAsync(x => x.Name == n, ct)) return (null, null, $"Ein Node „{n}“ gibt es schon.");
        var token = NewToken();
        var node = new Node { PublicId = NewPublicId(), Name = n, TokenHash = InstanceService.HashToken(token) };
        _db.Nodes.Add(node);
        await _db.SaveChangesAsync(ct);
        return (node, token, null);
    }

    /// <summary>A fresh token; the old one stops working at once (the running agent is turned away until it is
    /// restarted with the new one).</summary>
    public async Task<string> RotateTokenAsync(Node node, CancellationToken ct = default)
    {
        var token = NewToken();
        node.TokenHash = InstanceService.HashToken(token);
        await _db.SaveChangesAsync(ct);
        return token;
    }

    public async Task SetRevokedAsync(Node node, bool revoked, CancellationToken ct = default)
    {
        node.Revoked = revoked;
        if (revoked)
        {
            // Nothing handed out to a revoked node may stay "pending" forever.
            var open = await _db.NodeJobs.Where(j => j.NodeId == node.Id && (j.State == NodeJobState.Pending || j.State == NodeJobState.Running)).ToListAsync(ct);
            foreach (var j in open) { j.State = NodeJobState.Failed; j.Message = "Node gesperrt."; j.FinishedAt = DateTime.UtcNow; ClosePayload(j); }
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes the node record. Its sites keep running (they are not the node's property); they fall back
    /// to remote on their next beat — the cloud simply can no longer act on them.</summary>
    public async Task DeleteAsync(Node node, CancellationToken ct = default)
    {
        _db.Nodes.Remove(node);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Partial update (null = keep), for UI, API and MCP alike. The Matcad key is encrypted and never
    /// read back.</summary>
    public sealed record NodeInput(string? Name = null, int? PortFrom = null, int? PortTo = null,
        string? Provider = null, string? MatcadUrl = null, string? MatcadToken = null, bool ClearMatcadToken = false,
        string? CaddyAdminUrl = null, string? CaddyServer = null, string? Upstream = null, string? Network = null,
        string? UpstreamHost = null);

    public async Task<string?> UpdateAsync(Node node, NodeInput b, CancellationToken ct = default)
    {
        if (b.Name is not null)
        {
            var n = b.Name.Trim();
            if (n.Length == 0) return "Der Name darf nicht leer sein.";
            if (await _db.Nodes.AnyAsync(x => x.Id != node.Id && x.Name == n, ct)) return $"Ein Node „{n}“ gibt es schon.";
            node.Name = n;
        }
        if (b.PortFrom is { } f) node.PortFrom = Math.Clamp(f, 1, 65535);
        if (b.PortTo is { } t) node.PortTo = Math.Clamp(t, 1, 65535);
        if (node.PortFrom > node.PortTo) (node.PortFrom, node.PortTo) = (node.PortTo, node.PortFrom);
        if (b.Provider is not null) node.ProxyKind = ProxyKinds.Normalise(b.Provider);
        if (b.MatcadUrl is not null) node.MatcadUrl = Clean(b.MatcadUrl)?.TrimEnd('/');
        if (b.ClearMatcadToken) node.MatcadTokenEnc = null;
        else if (!string.IsNullOrEmpty(b.MatcadToken)) node.MatcadTokenEnc = _secrets.Protect(b.MatcadToken);
        if (b.CaddyAdminUrl is not null) node.CaddyAdminUrl = Clean(b.CaddyAdminUrl)?.TrimEnd('/');
        if (b.CaddyServer is not null) node.CaddyServer = Clean(b.CaddyServer);
        if (b.Upstream is not null) node.ProxyUpstream = UpstreamModes.Normalise(b.Upstream);
        if (b.Network is not null) node.ProxyNetwork = Clean(b.Network);
        if (b.UpstreamHost is not null) node.ProxyUpstreamHost = Clean(b.UpstreamHost);
        await _db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>The node's proxy settings, in the same shape as the cloud-wide ones of "Dieser Host".</summary>
    public ProxySettings ProxySettingsFor(Node n) => new(
        ProxyKinds.Normalise(n.ProxyKind), n.MatcadUrl, _secrets.Unprotect(n.MatcadTokenEnc),
        n.CaddyAdminUrl, string.IsNullOrWhiteSpace(n.CaddyServer) ? "srv0" : n.CaddyServer!,
        UpstreamModes.Normalise(n.ProxyUpstream), n.ProxyNetwork, n.ProxyUpstreamHost);

    public static List<NodeContainer> Inventory(Node n) =>
        NodeJobExecutor.Deserialize<List<NodeContainer>>(n.InventoryJson) ?? new();

    /// <summary>The command that starts the agent on the host — the only thing an operator has to run there.
    /// The image is the cloud's own (<c>--node-agent</c> mode), so agent and cloud always speak the same protocol.</summary>
    public string AgentCommand(Node n, string token)
    {
        var cloudUrl = _cloud.Get(SettingKeys.CanonicalUrl) is { Length: > 0 } u ? u.TrimEnd('/') : "https://<cloud-adresse>";
        return "docker run -d --name matcms-node --restart unless-stopped \\\n" +
               "  -v /var/run/docker.sock:/var/run/docker.sock \\\n" +
               $"  -e MatCmsNode__CloudUrl={cloudUrl} \\\n" +
               $"  -e MatCmsNode__NodeId={n.PublicId} \\\n" +
               $"  -e MatCmsNode__Token={token} \\\n" +
               "  ghcr.io/real-ttx/matcms-cloud:latest dotnet MatCMS.Cloud.dll --node-agent";
    }

    /// <summary>What the API/MCP show about a node — never the token, never the Matcad key.</summary>
    public static object PublicJson(Node n, int instanceCount, bool withInventory, string? cloudVersion = null)
    {
        var now = DateTime.UtcNow;
        return new
        {
            id = n.PublicId, name = n.Name, revoked = n.Revoked, online = n.IsOnline(now),
            lastSeenAt = n.LastSeenAt, agentVersion = n.AgentVersion, hostName = n.HostName,
            agentOutdated = cloudVersion is null ? (bool?)null : AgentOutdated(n, cloudVersion),
            dockerVersion = n.DockerVersion, dockerError = n.DockerError, instances = instanceCount,
            portFrom = n.PortFrom, portTo = n.PortTo,
            proxy = new
            {
                provider = ProxyKinds.Normalise(n.ProxyKind), matcadUrl = n.MatcadUrl, matcadTokenSet = !string.IsNullOrEmpty(n.MatcadTokenEnc),
                caddyAdminUrl = n.CaddyAdminUrl, caddyServer = string.IsNullOrWhiteSpace(n.CaddyServer) ? "srv0" : n.CaddyServer,
                upstream = UpstreamModes.Normalise(n.ProxyUpstream), network = n.ProxyNetwork, upstreamHost = n.ProxyUpstreamHost,
            },
            containers = withInventory ? Inventory(n).Select(c => new { id = c.Id[..Math.Min(12, c.Id.Length)], c.Name, c.Image, c.State, c.PublishedPort, c.CloudManaged }) : null,
        };
    }

    /// <summary>
    /// The agent speaks the cloud's own version when both run the same image — the normal state, since the agent IS
    /// the cloud image. A different version means the agent was left behind by a cloud update (or is ahead of it).
    /// "local" builds compare as unknown rather than raising a false alarm.
    /// </summary>
    public static bool AgentOutdated(Node n, string cloudVersion) =>
        !string.IsNullOrEmpty(n.AgentVersion) && !n.AgentVersion.StartsWith("local", StringComparison.OrdinalIgnoreCase)
        && !cloudVersion.StartsWith("local", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(n.AgentVersion, cloudVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks the agent to update ITSELF: it pulls its image tag and a helper swaps its container (with
    /// rollback if the new agent does not keep running). The new version shows on its next beat.</summary>
    public Task<JobOutcome> UpdateAgentAsync(Node node, CancellationToken ct = default) =>
        RunAsync(node, NodeJobKinds.AgentUpdate, new { }, null, TimeSpan.FromSeconds(30), ct);

    // ---- The agent's heartbeat ----------------------------------------------------------------------

    public async Task<Node?> AuthenticateAsync(string publicId, string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var node = await _db.Nodes.FirstOrDefaultAsync(n => n.PublicId == publicId, ct);
        if (node is null) return null;
        var a = Encoding.ASCII.GetBytes(InstanceService.HashToken(token));
        var b = Encoding.ASCII.GetBytes(node.TokenHash);
        return CryptographicOperations.FixedTimeEquals(a, b) ? node : null;
    }

    public async Task<NodeHeartbeatResponse> HeartbeatAsync(Node node, NodeHeartbeatRequest req, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        node.LastSeenAt = now;
        node.OfflineNotified = false;   // back — the next outage may notify again
        node.AgentVersion = Trunc(req.AgentVersion, 100);
        node.HostName = Trunc(req.HostName, 200);
        node.DockerVersion = Trunc(req.DockerVersion, 50);
        node.DockerError = Trunc(req.DockerError, 500);

        if (req.Containers is not null)
        {
            node.InventoryJson = NodeJobExecutor.Serialize(req.Containers.Take(500).ToList());
            node.InventoryAt = now;
            // A stopped site does not beat — its node does. Reflect the state of the node's instances at once.
            var mine = await _db.Instances.Where(i => i.NodeId == node.Id && i.ContainerId != null).ToListAsync(ct);
            foreach (var i in mine)
            {
                var c = req.Containers.FirstOrDefault(c => DockerHostService.IdMatches(c.Id, i.ContainerId!.ToLowerInvariant()));
                if (c is null) continue;
                i.ContainerState = c.State; i.LocalPort = c.PublishedPort; i.LocalContainerName = c.Name; i.CloudManaged = c.CloudManaged;
            }
        }

        // Fold reports — only into a job that is still running, so a report sent twice (the agent re-sends when
        // it is unsure whether the cloud got it) changes nothing the second time.
        var completed = new List<long>();
        if (req.Reports.Count > 0)
        {
            var ids = req.Reports.Select(r => r.JobId).ToList();
            var jobs = await _db.NodeJobs.Where(j => j.NodeId == node.Id && ids.Contains(j.Id) && j.State == NodeJobState.Running).ToListAsync(ct);
            foreach (var j in jobs)
            {
                var r = req.Reports.First(x => x.JobId == j.Id);
                j.State = r.Ok ? NodeJobState.Done : NodeJobState.Failed;
                j.Message = Trunc(r.Message, 4000);
                // Logs can be large; they are fetched once and shown, not archived.
                j.ResultJson = r.ResultJson is { Length: > 2_000_000 } ? null : r.ResultJson;
                j.FinishedAt = now;
                ClosePayload(j);
                completed.Add(j.Id);
            }
        }

        await ExpireAsync(node.Id, now, ct);
        await _db.SaveChangesAsync(ct);
        foreach (var id in completed) _signal.CompleteJob(id);

        var offer = await TakeAsync(node, ct);
        if (offer.Count == 0 && req.Wait && req.Reports.Count == 0 && !node.Revoked)
        {
            await _signal.WaitNodeAsync(node.Id, NodeProtocol.LongPoll, ct);
            if (!ct.IsCancellationRequested) offer = await TakeAsync(node, ct);
        }
        if (completed.Count > 0) await PruneAsync(node.Id, ct);
        return new NodeHeartbeatResponse { Jobs = offer };
    }

    private async Task<List<NodeJobOffer>> TakeAsync(Node node, CancellationToken ct)
    {
        var pending = await _db.NodeJobs.Where(j => j.NodeId == node.Id && j.State == NodeJobState.Pending)
            .OrderBy(j => j.Id).Take(20).ToListAsync(ct);
        if (pending.Count == 0) return new();
        var now = DateTime.UtcNow;
        foreach (var j in pending) { j.State = NodeJobState.Running; j.StartedAt = now; }
        await _db.SaveChangesAsync(ct);
        return pending.Select(j => new NodeJobOffer { Id = j.Id, Kind = j.Kind, PayloadJson = _secrets.Unprotect(j.PayloadJson) ?? "" }).ToList();
    }

    /// <summary>A finished job's parameters are not needed any more — and may contain a secret.</summary>
    private static void ClosePayload(NodeJob j) => j.PayloadJson = "";

    private async Task ExpireAsync(int nodeId, DateTime now, CancellationToken ct)
    {
        var pendingCut = now - NodeJob.PendingTimeout;
        var runningCut = now - NodeJob.RunningTimeout;
        var stale = await _db.NodeJobs.Where(j => j.NodeId == nodeId &&
                ((j.State == NodeJobState.Pending && j.CreatedAt < pendingCut) ||
                 (j.State == NodeJobState.Running && j.StartedAt < runningCut)))
            .ToListAsync(ct);
        foreach (var j in stale)
        {
            j.Message = j.State == NodeJobState.Pending ? "Vom Node nicht abgeholt (nicht erreichbar)." : "Keine Rückmeldung vom Node.";
            j.State = NodeJobState.Failed;
            j.FinishedAt = now;
            ClosePayload(j);
        }
    }

    private async Task PruneAsync(int nodeId, CancellationToken ct)
    {
        var old = await _db.NodeJobs.Where(j => j.NodeId == nodeId && (j.State == NodeJobState.Done || j.State == NodeJobState.Failed))
            .OrderByDescending(j => j.Id).Skip(NodeJob.KeepPerNode).ToListAsync(ct);
        if (old.Count == 0) return;
        _db.NodeJobs.RemoveRange(old);
        await _db.SaveChangesAsync(ct);
    }

    // ---- Running jobs -------------------------------------------------------------------------------

    /// <param name="Finished">False = still running past the timeout; <see cref="JobId"/> tells where to look.</param>
    public sealed record JobOutcome(bool Finished, bool Ok, string Message, string? ResultJson, long JobId);

    public async Task<NodeJob> EnqueueAsync(Node node, string kind, object payload, int? instanceId, CancellationToken ct = default)
    {
        var job = new NodeJob
        {
            NodeId = node.Id, InstanceId = instanceId, Kind = kind,
            // Encrypted at rest: a proxy job carries the node's Matcad key in clear. Decrypted only when handed
            // to the agent, and dropped once the job is finished (see ClosePayload).
            PayloadJson = _secrets.Protect(NodeJobExecutor.Serialize(payload)) ?? "", RequestedBy = Caller(),
        };
        _db.NodeJobs.Add(job);
        await _db.SaveChangesAsync(ct);
        _signal.NotifyNode(node.Id);
        return job;
    }

    /// <summary>
    /// Enqueues a job and waits for the node's report up to <paramref name="timeout"/>. An offline node fails at
    /// once instead of making the caller sit out the timeout — the job would only expire unseen.
    /// </summary>
    public async Task<JobOutcome> RunAsync(Node node, string kind, object payload, int? instanceId, TimeSpan timeout, CancellationToken ct = default)
    {
        // Read the node's state FRESH: callers may hold the entity for minutes (a move loads it once at the
        // start), and a stale LastSeenAt declared a perfectly connected node "nicht verbunden" mid-move.
        var fresh = await _db.Nodes.AsNoTracking().Where(n => n.Id == node.Id)
            .Select(n => new { n.Revoked, n.LastSeenAt }).FirstOrDefaultAsync(ct);
        if (fresh is null) return new(true, false, $"Node „{node.Name}“ ist nicht mehr eingetragen.", null, 0);
        if (fresh.Revoked) return new(true, false, $"Node „{node.Name}“ ist gesperrt.", null, 0);
        if (!(fresh.LastSeenAt is { } seen && DateTime.UtcNow - seen < Node.OfflineAfter))
            return new(true, false, $"Node „{node.Name}“ ist nicht verbunden (zuletzt gesehen: {(fresh.LastSeenAt?.ToLocalTime().ToString("dd.MM. HH:mm") ?? "nie")}).", null, 0);

        var job = await EnqueueAsync(node, kind, payload, instanceId, ct);
        var done = _signal.JobTask(job.Id);
        var deadline = DateTime.UtcNow + timeout;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;
                // The signal is the fast path; the re-read every second is what makes it correct (another cloud
                // process, a lost signal).
                await Task.WhenAny(done, Task.Delay(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), ct));
                var now = await _db.NodeJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id, ct);
                if (now.State is NodeJobState.Done or NodeJobState.Failed)
                    return new(true, now.State == NodeJobState.Done, now.Message ?? "", now.ResultJson, job.Id);
            }
        }
        catch (OperationCanceledException) { }
        finally { _signal.ForgetJob(job.Id); }
        return new(false, false, $"Der Auftrag läuft auf Node „{node.Name}“ noch (Auftrag #{job.Id}) — das Ergebnis erscheint im Auftragsverlauf.", null, job.Id);
    }

    public Task<List<NodeJob>> JobsAsync(int nodeId, int take, CancellationToken ct = default) =>
        _db.NodeJobs.AsNoTracking().Where(j => j.NodeId == nodeId).OrderByDescending(j => j.Id).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);

    // ---- helpers ------------------------------------------------------------------------------------

    public const string ApiKeyItem = "operatorApi.key";

    private string Caller()
    {
        var ctx = _http.HttpContext;
        if (ctx is null) return "system";
        if (ctx.Items.TryGetValue(Mcp.McpContext.ItemKey, out var k) && k is ApiKey mk) return "mcp:" + mk.Name;
        // Stashed by the REST handlers (HostingApi/NodeApi.CallerAsync).
        if (ctx.Items.TryGetValue(ApiKeyItem, out var a) && a is ApiKey ak) return "api:" + ak.Name;
        return ctx.User.Identity?.IsAuthenticated == true ? "ui:" + ctx.User.Identity.Name : "system";
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string? Trunc(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
    private static string NewPublicId() => B64(RandomNumberGenerator.GetBytes(12));
    private static string NewToken() => "mcn_" + B64(RandomNumberGenerator.GetBytes(32));
    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
