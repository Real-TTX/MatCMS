using System.ComponentModel;
using MatCMS.Cloud.Api;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using MatCMS.Cloud.Services.Nodes;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatCMS.Cloud.Mcp;

/// <summary>
/// MCP tools for nodes (Hosting increment 4) and provisioning — the same capabilities and rights as the Nodes
/// pages and <c>/api/v1/nodes</c>, through the same <see cref="NodeService"/>/<see cref="HostingService"/>.
/// Every tool needs the hosting right; changing nodes and creating sites additionally an all-instances key.
/// </summary>
[McpServerToolType]
public class NodeTools
{
    private static void RequireHosting(McpContext me)
    {
        if (!me.Key.CanManageHosting)
            throw new McpException("Dieser Schlüssel darf kein Hosting steuern (fehlendes Hosting-Recht).");
    }

    private static void RequireCloudWide(McpContext me)
    {
        if (!me.Key.CanManageHosting || !me.Key.AllInstances)
            throw new McpException("Nodes verwalten und Instanzen anlegen brauchen das Hosting-Recht UND einen Schlüssel für alle Instanzen.");
    }

    private static async Task<Node> ResolveAsync(AppDbContext db, string nodeId, CancellationToken ct) =>
        await db.Nodes.FirstOrDefaultAsync(n => n.PublicId == nodeId, ct) ?? throw new McpException("Node nicht gefunden.");

    [McpServerTool(Name = "list_nodes"), Description(
        "The Docker hosts this cloud can run sites on: 'local' (the cloud's own host) and every connected node with online state, agent/Docker version, number of sites and proxy provider. Requires the hosting right.")]
    public static async Task<object> ListNodes(McpContext me, AppDbContext db, DockerHostService docker, Services.Proxy.ProxyService proxy, CancellationToken ct)
    {
        RequireHosting(me);
        var counts = await db.Instances.Where(i => i.NodeId != null).GroupBy(i => i.NodeId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var list = await db.Nodes.AsNoTracking().OrderBy(n => n.Name).ToListAsync(ct);
        return new
        {
            local = new
            {
                id = "local", dockerReachable = await docker.IsReachableAsync(ct),
                instances = await db.Instances.CountAsync(i => i.Hosting == InstanceHosting.Local, ct), proxy = proxy.PublicConfig(),
            },
            nodes = list.Select(n => NodeService.PublicJson(n, counts.FirstOrDefault(c => c.Key == n.Id)?.N ?? 0, withInventory: false)),
        };
    }

    [McpServerTool(Name = "get_node"), Description(
        "One node in detail, including the MatCMS containers its agent reports (name, image, state, port). Requires the hosting right.")]
    public static async Task<object> GetNode(McpContext me, AppDbContext db, VersionService version,
        [Description("The node id, as returned by list_nodes.")] string nodeId, CancellationToken ct)
    {
        RequireHosting(me);
        var n = await ResolveAsync(db, nodeId, ct);
        return NodeService.PublicJson(n, await db.Instances.CountAsync(i => i.NodeId == n.Id, ct), withInventory: true, version.Current);
    }

    [McpServerTool(Name = "create_node"), Description(
        "Register a new Docker host. Returns the agent token ONCE and the exact 'docker run' command to start the node-agent on that host (the user runs it there; the agent then connects out to this cloud). Requires the hosting right on an all-instances key.")]
    public static async Task<object> CreateNode(McpContext me, NodeService nodes,
        [Description("A unique name, e.g. hetzner-1.")] string name, CancellationToken ct)
    {
        RequireCloudWide(me);
        var (node, token, err) = await nodes.CreateAsync(name, ct);
        if (node is null) throw new McpException(err ?? "Node konnte nicht angelegt werden.");
        return new { id = node.PublicId, name = node.Name, token, command = nodes.AgentCommand(node, token!) };
    }

    [McpServerTool(Name = "update_node"), Description(
        "Change a node's name, port range for new sites, or its reverse-proxy settings (same fields as configure_proxy, applied to THIS node; the proxy is reached from the node, not from the cloud). Omitted parameters keep their value. Requires the hosting right on an all-instances key.")]
    public static async Task<object> UpdateNode(McpContext me, AppDbContext db, NodeService nodes,
        [Description("The node id.")] string nodeId,
        string? name = null, int? portFrom = null, int? portTo = null,
        [Description("none, matcad or caddy.")] string? provider = null,
        string? matcadUrl = null, [Description("Stored encrypted, never returned.")] string? matcadToken = null, bool clearMatcadToken = false,
        string? caddyAdminUrl = null, string? caddyServer = null,
        [Description("network or hostport.")] string? upstream = null, string? network = null, string? upstreamHost = null,
        [Description("Automatic addresses: every instance on this node gets name.<autoDomainBase> at the node's proxy.")] bool? autoDomainEnabled = null,
        [Description("Base domain for automatic addresses, e.g. server1.example.de (needs a wildcard DNS record *.<base> to the node).")] string? autoDomainBase = null,
        [Description("How the cloud's edge proxy reaches this node (IP or host name) when it forwards to address:port.")] string? address = null,
        CancellationToken ct = default)
    {
        RequireCloudWide(me);
        var n = await ResolveAsync(db, nodeId, ct);
        var err = await nodes.UpdateAsync(n, new NodeService.NodeInput(name, portFrom, portTo, provider, matcadUrl, matcadToken, clearMatcadToken,
            caddyAdminUrl, caddyServer, upstream, network, upstreamHost, autoDomainEnabled, autoDomainBase, address), ct);
        if (err is not null) throw new McpException(err);
        return NodeService.PublicJson(n, await db.Instances.CountAsync(i => i.NodeId == n.Id, ct), withInventory: false);
    }

    [McpServerTool(Name = "create_missing_host_addresses"), Description(
        "Create the automatic host address (name.<base domain>) for every instance on a host that has none yet — after switching automatic addresses on for a host that already runs sites. nodeId omitted = this cloud's own host. Requires the hosting right on an all-instances key.")]
    public static async Task<object> CreateMissingHostAddresses(McpContext me, AppDbContext db, Services.Proxy.ProxyService proxy,
        [Description("The node id; omit for this cloud's own host.")] string? nodeId = null, CancellationToken ct = default)
    {
        RequireCloudWide(me);
        var n = nodeId is null ? null : await ResolveAsync(db, nodeId, ct);
        var r = await proxy.PublishMissingHostAddressesAsync(n, ct);
        return new { created = r.Created, failed = r.Failed, errors = r.Errors };
    }

    [McpServerTool(Name = "set_node_revoked"), Description(
        "Revoke (true) or re-activate (false) a node. A revoked node's agent is turned away and receives no jobs; its sites keep running. Requires the hosting right on an all-instances key.")]
    public static async Task<object> SetNodeRevoked(McpContext me, AppDbContext db, NodeService nodes,
        [Description("The node id.")] string nodeId, bool revoked, CancellationToken ct)
    {
        RequireCloudWide(me);
        var n = await ResolveAsync(db, nodeId, ct);
        await nodes.SetRevokedAsync(n, revoked, ct);
        return new { ok = true, revoked };
    }

    [McpServerTool(Name = "rotate_node_token"), Description(
        "Issue a new agent token for a node (the old one stops working at once — the agent must be restarted with the new one). Returns the token ONCE and the docker run command. Requires the hosting right on an all-instances key.")]
    public static async Task<object> RotateNodeToken(McpContext me, AppDbContext db, NodeService nodes,
        [Description("The node id.")] string nodeId, CancellationToken ct)
    {
        RequireCloudWide(me);
        var n = await ResolveAsync(db, nodeId, ct);
        var token = await nodes.RotateTokenAsync(n, ct);
        return new { ok = true, token, command = nodes.AgentCommand(n, token) };
    }

    [McpServerTool(Name = "update_node_agent"), Description(
        "Update a node's agent to the newest image of its tag: the agent starts a helper container that swaps it and rolls back if the new agent does not keep running. The node is briefly disconnected (about a minute); its sites keep running. get_node then shows the new agentVersion (agentOutdated = false once it matches the cloud). Requires the hosting right on an all-instances key.")]
    public static async Task<object> UpdateNodeAgent(McpContext me, AppDbContext db, NodeService nodes,
        [Description("The node id.")] string nodeId, CancellationToken ct)
    {
        RequireCloudWide(me);
        var n = await ResolveAsync(db, nodeId, ct);
        var r = await nodes.UpdateAgentAsync(n, ct);
        if (!r.Ok) throw new McpException(r.Message);
        return new { ok = true, message = r.Message };
    }

    [McpServerTool(Name = "delete_node"), Description(
        "Delete a node record. Its sites keep running on the host but the cloud can no longer act on them. Confirm with the user first. Requires the hosting right on an all-instances key.")]
    public static async Task<object> DeleteNode(McpContext me, AppDbContext db, NodeService nodes,
        [Description("The node id.")] string nodeId, CancellationToken ct)
    {
        RequireCloudWide(me);
        var n = await ResolveAsync(db, nodeId, ct);
        await nodes.DeleteAsync(n, ct);
        return new { ok = true };
    }

    [McpServerTool(Name = "test_node_proxy"), Description(
        "Test a node's reverse-proxy configuration FROM the node (provider reachable, proxy network exists). Requires the hosting right.")]
    public static async Task<object> TestNodeProxy(McpContext me, AppDbContext db, Services.Proxy.ProxyService proxy,
        [Description("The node id.")] string nodeId, CancellationToken ct)
    {
        RequireHosting(me);
        var n = await ResolveAsync(db, nodeId, ct);
        var r = await proxy.TestAsync(n, ct);
        return new { ok = r.Ok, provider = r.Kind, message = r.Message };
    }

    [McpServerTool(Name = "list_node_jobs"), Description(
        "The recent jobs of a node (kind, state pending/running/done/failed, message, who asked, times) — e.g. to follow an update that is still running. Requires the hosting right.")]
    public static async Task<object> ListNodeJobs(McpContext me, AppDbContext db, NodeService nodes,
        [Description("The node id.")] string nodeId, [Description("How many (1–200, default 30).")] int take = 30, CancellationToken ct = default)
    {
        RequireHosting(me);
        var n = await ResolveAsync(db, nodeId, ct);
        return (await nodes.JobsAsync(n.Id, take, ct)).Select(NodeApi.JobJson);
    }

    [McpServerTool(Name = "create_instance"), Description(
        "Provision a NEW MatCMS site: creates the container on this cloud's host or on a node, optionally publishes a domain there (route + TLS), and lets the site enroll itself with its profile's join code — it appears in list_instances within a few minutes. Requires the Hosting module to be on and the hosting right on an all-instances key.")]
    public static async Task<object> CreateInstance(McpContext me, AppDbContext db, HostingService hosting,
        [Description("Display name; container and volume names are derived from it.")] string name,
        [Description("Node id from list_nodes, or 'local' / omitted for this cloud's own host.")] string? nodeId = null,
        [Description("Profile id; omitted = the default profile.")] int? profileId = null,
        [Description("Optional public domain, e.g. shop.example.de (DNS must point at that host's proxy).")] string? domain = null,
        [Description("Image tag, default latest.")] string? imageTag = null,
        CancellationToken ct = default)
    {
        RequireCloudWide(me);
        int? nid = null;
        if (!string.IsNullOrWhiteSpace(nodeId) && nodeId != "local") nid = (await ResolveAsync(db, nodeId, ct)).Id;
        var pid = profileId ?? await db.Profiles.AsNoTracking().Where(p => p.IsDefault).Select(p => (int?)p.Id).FirstOrDefaultAsync(ct) ?? 0;
        var r = await hosting.ProvisionAsync(name, pid, domain, imageTag, nid, pushCanonical: true, ct);
        if (!r.Ok) throw new McpException(r.Message);
        return new { ok = true, containerName = r.ContainerName, port = r.Port, domainFailed = r.DomainFailed, message = r.Message };
    }
}
