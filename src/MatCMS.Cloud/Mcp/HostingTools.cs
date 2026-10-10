using System.ComponentModel;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatCMS.Cloud.Mcp;

/// <summary>
/// MCP tools for hosting — the same capabilities and the same rights as the Hosting UI and
/// <c>/api/v1/hosting</c>, through the same services: container status/actions/logs of an instance on this
/// cloud's Docker host, the Hosting module switch, and the cloud's own self-update. Unlike the content
/// tools these act at once (the cloud talks to its own Docker daemon); only the self-update is deferred,
/// because the helper swaps the container after the call has answered.
/// </summary>
[McpServerToolType]
public class HostingTools
{
    private static void RequireHosting(McpContext me)
    {
        if (!me.Key.CanManageHosting)
            throw new McpException("Dieser Schlüssel darf kein Hosting steuern (fehlendes Hosting-Recht).");
    }

    private static void RequireCloudWide(McpContext me)
    {
        if (!me.Key.CanManageHosting || !me.Key.AllInstances)
            throw new McpException("Cloud-weite Hosting-Aktionen brauchen das Hosting-Recht UND einen Schlüssel für alle Instanzen.");
    }

    private static async Task<Instance> ResolveAsync(McpContext me, AppDbContext db, string instanceId, CancellationToken ct)
    {
        var inst = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == instanceId, ct);
        if (inst is null || !ApiKeyService.CanAccess(me.Key, inst)) throw new McpException("Instanz nicht gefunden.");
        return inst;
    }

    [McpServerTool(Name = "get_hosting_status"), Description(
        "Whether the Hosting module is switched on and whether this cloud can reach its Docker daemon. Any valid key.")]
    public static async Task<object> GetHostingStatus(McpContext me, CloudContext cloud, DockerHostService docker, CancellationToken ct)
    {
        _ = me.Key;
        return new
        {
            enabled = cloud.Flag(SettingKeys.HostingEnabled),
            dockerConfigured = docker.Configured,
            dockerReachable = await docker.IsReachableAsync(ct),
            canManageHosting = me.Key.CanManageHosting,
        };
    }

    [McpServerTool(Name = "set_hosting_enabled"), Description(
        "Switch the Hosting module on or off (governs the Hosting menu and provisioning of new instances; container actions on existing instances keep working either way). Requires the hosting right on an all-instances key.")]
    public static async Task<object> SetHostingEnabled(McpContext me, CloudContext cloud,
        [Description("true = on, false = off.")] bool enabled, CancellationToken ct)
    {
        RequireCloudWide(me);
        await cloud.SaveAsync(new Dictionary<string, string?> { [SettingKeys.HostingEnabled] = enabled ? "1" : "0" });
        return new { ok = true, enabled };
    }

    [McpServerTool(Name = "get_hosting_overview"), Description(
        "Hosting dashboard: every host (this cloud's Docker host and each node) with online state, CPU count, total RAM, running/total sites and summed CPU/RAM use; and every MatCMS container on them with instance, host, state, CPU %, RAM, domain, version and whether an update is available. Node figures are the agent's last sample (about once a minute). Requires the hosting right on an all-instances key.")]
    public static async Task<object> GetHostingOverview(McpContext me, HostingOverviewService overview, CancellationToken ct)
    {
        RequireCloudWide(me);
        return HostingOverviewJson.Overview(await overview.BuildAsync(ct));
    }

    [McpServerTool(Name = "list_images"), Description(
        "The MatCMS Docker images on one host — this cloud's host or a node: tag, version, size, created, how many containers use each, and which are old (untagged) and unused — what prune_images would remove. Requires the hosting right on an all-instances key.")]
    public static async Task<object> ListImages(McpContext me, AppDbContext db, HostImagesService images,
        [Description("Node id (from list_nodes); omit or 'local' for this cloud's host.")] string? nodeId = null, CancellationToken ct = default)
    {
        RequireCloudWide(me);
        var (n, missing) = await Api.HostingApi.HostAsync(db, nodeId);
        if (missing) throw new McpException("Node nicht gefunden.");
        var r = await images.ListAsync(n, ct);
        if (r.Images is null) throw new McpException(r.Error ?? "Images nicht lesbar.");
        return HostingOverviewJson.Images(r.Images, r.Versions);
    }

    [McpServerTool(Name = "prune_images"), Description(
        "Remove old MatCMS images on one host — this cloud's host or a node: only untagged ones left behind by updates, only MatCMS images, and never one a container still uses. Returns how many were removed and the approximate bytes reclaimed. Requires the hosting right on an all-instances key.")]
    public static async Task<object> PruneImages(McpContext me, AppDbContext db, HostImagesService images,
        [Description("Node id (from list_nodes); omit or 'local' for this cloud's host.")] string? nodeId = null, CancellationToken ct = default)
    {
        RequireCloudWide(me);
        var (n, missing) = await Api.HostingApi.HostAsync(db, nodeId);
        if (missing) throw new McpException("Node nicht gefunden.");
        var (ok, message, r) = await images.PruneAsync(n, ct);
        if (!ok) throw new McpException(message);
        return new { removed = r?.Removed ?? 0, bytesReclaimed = r?.BytesReclaimed ?? 0 };
    }

    [McpServerTool(Name = "list_update_candidates"), Description(
        "Instances the cloud can update itself (on its own host or a node) that run an older version than the latest release, within the key's instance scope. Any valid key.")]
    public static async Task<object> ListUpdateCandidates(McpContext me, InstanceUpdatesService updates, ReleaseWatcher releases, CancellationToken ct)
    {
        var list = await updates.CandidatesAsync(i => ApiKeyService.CanAccess(me.Key, i), ct);
        return new { latest = releases.LatestVersion, instances = list.Select(i => new { instanceId = i.PublicId, name = i.Name, node = i.Node?.Name, version = i.Version }) };
    }

    [McpServerTool(Name = "start_instance_updates"), Description(
        "Update instances one after another, each with rollback on failure. Pass the instance ids to update a subset, or none to update every candidate. Ids that need no update are skipped. Refused while the cloud updates itself. Returns a runId for get_update_run. Requires the hosting right.")]
    public static async Task<object> StartInstanceUpdates(McpContext me, InstanceUpdatesService updates,
        [Description("Instance ids (from list_update_candidates); omit for all candidates.")] string[]? instanceIds, CancellationToken ct)
    {
        RequireHosting(me);
        var r = await updates.StartAsync(instanceIds, i => ApiKeyService.CanAccess(me.Key, i), ct);
        if (!r.Ok) throw new McpException(r.Error ?? "Start nicht möglich.");
        return new { runId = r.RunId, count = r.Count };
    }

    [McpServerTool(Name = "get_update_run"), Description(
        "Progress of an instance update run started with start_instance_updates: done flag, completed/total and per instance its status (pending, updating, done, failed, skipped) with message. Any valid key.")]
    public static object GetUpdateRun(McpContext me, InstanceUpdatesService updates,
        [Description("The runId returned by start_instance_updates.")] string runId)
    {
        _ = me.Key;
        return updates.Progress(runId) ?? throw new McpException("Lauf nicht gefunden.");
    }

    [McpServerTool(Name = "get_container_status"), Description(
        "Live container status of an instance: whether it runs on this cloud's Docker host (local), its container name/image/state, start time, restart count, published port, health. Any valid key within its instance scope.")]
    public static async Task<object> GetContainerStatus(McpContext me, AppDbContext db, HostingActionsService hosting,
        [Description("The instance id, as returned by list_instances.")] string instanceId, CancellationToken ct)
    {
        var inst = await ResolveAsync(me, db, instanceId, ct);
        await hosting.RefreshAsync(inst, ct);
        var d = await hosting.DetailsAsync(inst, ct);
        return new
        {
            local = HostingActionsService.IsLocal(inst),
            onNode = HostingActionsService.IsOnNode(inst),
            hosting = inst.Hosting.ToString().ToLowerInvariant(),
            containerState = inst.ContainerState,
            container = d is null ? null : new
            {
                name = d.Name, image = d.Image, state = d.State, startedAt = d.StartedAt,
                restartCount = d.RestartCount, publishedPort = d.PublishedPort, cloudManaged = d.CloudManaged, health = d.Health,
            },
        };
    }

    [McpServerTool(Name = "container_action"), Description(
        "Start, stop, restart or update (pull the newest image and recreate, with automatic rollback) the container of an instance that runs on this cloud's Docker host. 'stop' takes the site offline and 'update' restarts it — confirm with the user first. Requires the hosting right; honours the key's instance scope.")]
    public static async Task<object> ContainerAction(McpContext me, AppDbContext db, HostingActionsService hosting,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("One of: start, stop, restart, update.")] string action, CancellationToken ct)
    {
        RequireHosting(me);
        var inst = await ResolveAsync(me, db, instanceId, ct);
        await hosting.RefreshAsync(inst, ct);

        HostingActionsService.ActionResult r;
        if (string.Equals(action, "update", StringComparison.OrdinalIgnoreCase)) r = await hosting.UpdateAsync(inst, ct);
        else if (HostingActionsService.ParsePower(action) is { } p) r = await hosting.PowerAsync(inst, p, ct);
        else throw new McpException("Unbekannte Aktion. Erlaubt: start, stop, restart, update.");

        if (!r.Ok) throw new McpException(r.Message);
        return new { ok = true, message = r.Message, containerState = inst.ContainerState };
    }

    [McpServerTool(Name = "get_container_logs"), Description(
        "The last lines of an instance container's stdout/stderr (with timestamps) — for diagnosing a site that will not start or misbehaves. Requires the hosting right; honours the key's instance scope.")]
    public static async Task<object> GetContainerLogs(McpContext me, AppDbContext db, HostingActionsService hosting,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("How many lines from the end (1–5000, default 200).")] int tail = 200,
        CancellationToken ct = default)
    {
        RequireHosting(me);
        var inst = await ResolveAsync(me, db, instanceId, ct);
        await hosting.RefreshAsync(inst, ct);
        var (ok, text) = await hosting.LogsAsync(inst, tail, ct);
        if (!ok) throw new McpException(text);
        return new { ok = true, tail, logs = text };
    }

    [McpServerTool(Name = "get_proxy_config"), Description(
        "The reverse-proxy configuration of this cloud: provider (none = no proxy, sites are reached on their host port; matcad; caddy), its address, how the proxy reaches an instance (upstream 'network' = attach the container to a shared Docker network, 'hostport' = upstreamHost:hostPort), and whether a Matcad API key is set (the key itself is never returned). Any valid key.")]
    public static object GetProxyConfig(McpContext me, Services.Proxy.ProxyService proxy)
    {
        _ = me.Key;
        return proxy.PublicConfig();
    }

    [McpServerTool(Name = "configure_proxy"), Description(
        "Change the reverse-proxy configuration. Every parameter is optional; omitted ones keep their value. Changing the provider does NOT move already published domains — republish them (publish_domain) afterwards. Run test_proxy after a change. Requires the hosting right on an all-instances key.")]
    public static async Task<object> ConfigureProxy(McpContext me, Services.Proxy.ProxyService proxy,
        [Description("none, matcad or caddy.")] string? provider = null,
        [Description("Matcad base URL, e.g. http://matcad:8080.")] string? matcadUrl = null,
        [Description("Matcad API key (X-Api-Key). Stored encrypted, never returned.")] string? matcadToken = null,
        [Description("true = delete the stored Matcad API key.")] bool clearMatcadToken = false,
        [Description("Caddy admin API URL, e.g. http://caddy:2019.")] string? caddyAdminUrl = null,
        [Description("Caddy HTTP server name to add routes to (default srv0).")] string? caddyServer = null,
        [Description("network or hostport.")] string? upstream = null,
        [Description("Docker network shared by the proxy and the instances (upstream=network).")] string? network = null,
        [Description("Host/IP the proxy reaches published host ports on (upstream=hostport), e.g. host.docker.internal.")] string? upstreamHost = null)
    {
        RequireCloudWide(me);
        await proxy.UpdateSettingsAsync(new(provider, matcadUrl, matcadToken, clearMatcadToken, caddyAdminUrl, caddyServer,
            upstream, network, upstreamHost));
        return proxy.PublicConfig();
    }

    [McpServerTool(Name = "test_proxy"), Description(
        "Check that the configured reverse proxy answers and (upstream=network) that the proxy network exists. Requires the hosting right.")]
    public static async Task<object> TestProxy(McpContext me, Services.Proxy.ProxyService proxy, CancellationToken ct)
    {
        RequireHosting(me);
        var r = await proxy.TestAsync(null, ct);
        return new { ok = r.Ok, provider = r.Kind, message = r.Message };
    }

    [McpServerTool(Name = "get_domain_status"), Description(
        "The addresses of an instance: its customer domain (via 'edge' = the cloud's central edge proxy, or 'host' = the proxy of the host it runs on; provider, route id, last error, publish time) and its automatic host address (name.<host base domain>), and (check=true) whether the routes still exist at the proxies. Any valid key within its instance scope.")]
    public static async Task<object> GetDomainStatus(McpContext me, AppDbContext db, Services.Proxy.ProxyService proxy,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("Ask the proxy whether the route still exists (default true).")] bool check = true,
        CancellationToken ct = default)
    {
        var inst = await ResolveAsync(me, db, instanceId, ct);
        var s = await proxy.StatusAsync(inst, check, ct);
        return Services.Proxy.ProxyService.StatusJson(s);
    }

    [McpServerTool(Name = "publish_domain"), Description(
        "Publish an instance under a customer domain (or move it to a new one). With the edge proxy switched on the route is created at the edge (DNS points at the cloud server) and forwards to the site's host; otherwise at the proxy of the host the site runs on (DNS points at that host); with provider 'none' the domain is only recorded. pushCanonical (default true) also sets the site's canonical URL to https://<domain>. Requires the hosting right; honours the key's instance scope.")]
    public static async Task<object> PublishDomain(McpContext me, AppDbContext db, Services.Proxy.ProxyService proxy,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("A single hostname, e.g. shop.example.de (no scheme, path, port or wildcard).")] string domain,
        [Description("Also set the site's canonical URL (default true).")] bool pushCanonical = true,
        CancellationToken ct = default)
    {
        RequireHosting(me);
        var inst = await ResolveAsync(me, db, instanceId, ct);
        var r = await proxy.PublishAsync(inst, domain, pushCanonical, ct);
        if (!r.Ok) throw new McpException(r.Message);
        return new { ok = true, domain = r.Domain, message = r.Message };
    }

    [McpServerTool(Name = "unpublish_domain"), Description(
        "Remove an instance's public domain: deletes the proxy route (the site is then no longer reachable under it — confirm with the user first) and clears the record. pushCanonical (default true) also clears the site's canonical URL. Requires the hosting right; honours the key's instance scope.")]
    public static async Task<object> UnpublishDomain(McpContext me, AppDbContext db, Services.Proxy.ProxyService proxy,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("Also clear the site's canonical URL (default true).")] bool pushCanonical = true,
        CancellationToken ct = default)
    {
        RequireHosting(me);
        var inst = await ResolveAsync(me, db, instanceId, ct);
        var r = await proxy.UnpublishAsync(inst, pushCanonical, ct);
        if (!r.Ok) throw new McpException(r.Message);
        return new { ok = true, message = r.Message };
    }

    [McpServerTool(Name = "publish_host_address"), Description(
        "Create (or renew) the automatic host address of an instance — name.<base domain> at the proxy of the host it runs on (automatic addresses must be switched on for that host). Without a customer domain it becomes the site's address. Requires the hosting right; honours the key's instance scope.")]
    public static async Task<object> PublishHostAddress(McpContext me, AppDbContext db, Services.Proxy.ProxyService proxy,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("Also set the site's canonical URL when it has no customer domain (default true).")] bool pushCanonical = true,
        CancellationToken ct = default)
    {
        RequireHosting(me);
        var inst = await ResolveAsync(me, db, instanceId, ct);
        var r = await proxy.PublishHostAddressAsync(inst, pushCanonical, ct: ct);
        if (!r.Ok) throw new McpException(r.Message);
        return new { ok = true, hostAddress = r.Domain, message = r.Message };
    }

    [McpServerTool(Name = "remove_host_address"), Description(
        "Remove the automatic host address of an instance (deletes its route at the host's proxy). A customer domain at the edge is re-pointed to host:port. Requires the hosting right; honours the key's instance scope.")]
    public static async Task<object> RemoveHostAddress(McpContext me, AppDbContext db, Services.Proxy.ProxyService proxy,
        [Description("The instance id, as returned by list_instances.")] string instanceId, CancellationToken ct = default)
    {
        RequireHosting(me);
        var inst = await ResolveAsync(me, db, instanceId, ct);
        var r = await proxy.RemoveHostAddressAsync(inst, pushCanonical: true, ct);
        if (!r.Ok) throw new McpException(r.Message);
        return new { ok = true, message = r.Message };
    }

    [McpServerTool(Name = "set_auto_domain"), Description(
        "Switch automatic addresses on or off for THIS cloud's own host (every new instance there gets name.<baseDomain>; needs a wildcard DNS record *.<baseDomain> to this server and a proxy). For nodes use update_node(autoDomainEnabled, autoDomainBase); create_missing_host_addresses fills in existing instances. Requires the hosting right on an all-instances key.")]
    public static async Task<object> SetAutoDomain(McpContext me, Services.Proxy.ProxyService proxy,
        bool enabled, [Description("e.g. cloud.example.de")] string? baseDomain = null, CancellationToken ct = default)
    {
        RequireCloudWide(me);
        if (enabled && Services.Proxy.ProxyService.NormaliseDomain(baseDomain) is null) throw new McpException("Keine gültige Basis-Domain.");
        await proxy.SetAutoDomainAsync(enabled, baseDomain);
        return new { ok = true, missing = await proxy.MissingHostAddressCountAsync(null, ct) };
    }

    [McpServerTool(Name = "configure_wildcard"), Description(
        "One wildcard certificate for *.<base domain> of a host's automatic addresses via the DNS challenge, instead of one certificate per instance. Caddy: dnsProvider = the Caddy DNS module (hetzner, cloudflare, netcup …, compiled into that Caddy) and credentials its fields (e.g. api_token); Matcad: dnsProvider = the name of a DNS provider set up in Matcad (credentials stay there, pass none). nodeId omitted = this cloud's own host. Requires the hosting right on an all-instances key.")]
    public static async Task<object> ConfigureWildcard(McpContext me, AppDbContext db, Services.Proxy.ProxyService proxy,
        bool enabled, string? dnsProvider = null,
        [Description("Key/value credentials for the Caddy DNS module; omit to keep the stored ones (never returned).")] Dictionary<string, string>? credentials = null,
        [Description("The node id; omit for this cloud's own host.")] string? nodeId = null, CancellationToken ct = default)
    {
        RequireCloudWide(me);
        Node? node = null;
        if (nodeId is not null) node = await db.Nodes.FirstOrDefaultAsync(n => n.PublicId == nodeId, ct) ?? throw new McpException("Node nicht gefunden.");
        var creds = credentials is null ? null : string.Join("\n", credentials.Select(kv => kv.Key + "=" + kv.Value));
        var r = await proxy.SetWildcardAsync(node, enabled, dnsProvider, creds, ct);
        if (!r.Ok) throw new McpException(r.Message);
        return new { ok = true, message = r.Message };
    }

    [McpServerTool(Name = "get_edge_config"), Description(
        "The central edge proxy for customer domains: whether it is on, whether it is the same proxy as this cloud's host, its provider and addresses (never the Matcad key). With Caddy the edge forwards to a site's automatic host address; with Matcad to node-address:port. Any valid key.")]
    public static object GetEdgeConfig(McpContext me, Services.Proxy.ProxyService proxy)
    {
        _ = me.Key;
        return proxy.PublicEdgeConfig();
    }

    [McpServerTool(Name = "configure_edge"), Description(
        "Configure the central edge proxy for customer domains. enabled = customer domains are routed at the edge (DNS to the cloud server), which forwards to the site's host; useHostProxy = the edge is the proxy this cloud's host already uses (default). Omitted parameters keep their value. Switching does not move existing domains — call move_domains_to_edge_setting. Requires the hosting right on an all-instances key.")]
    public static async Task<object> ConfigureEdge(McpContext me, Services.Proxy.ProxyService proxy,
        bool? enabled = null, bool? useHostProxy = null, [Description("caddy or matcad (only when useHostProxy is false).")] string? provider = null,
        string? caddyAdminUrl = null, string? caddyServer = null, string? matcadUrl = null,
        [Description("Stored encrypted, never returned.")] string? matcadToken = null, bool clearMatcadToken = false,
        [Description("The edge's source addresses as the nodes see them (comma-separated IPs). Every Caddy host is told to trust them, so the visitor's IP survives the second proxy.")] string? trustedIps = null,
        CancellationToken ct = default)
    {
        RequireCloudWide(me);
        await proxy.UpdateEdgeAsync(new(enabled, useHostProxy, provider, matcadUrl, matcadToken, clearMatcadToken, caddyAdminUrl, caddyServer, trustedIps));
        var trustErrors = await proxy.ApplyTrustEverywhereAsync(ct);
        return new { edge = proxy.PublicEdgeConfig(), domainsOnOtherWay = await proxy.CustomerDomainsOnOtherWayCountAsync(ct), trustErrors };
    }

    [McpServerTool(Name = "test_edge"), Description("Whether the edge proxy is reachable with its configured address. Requires the hosting right.")]
    public static async Task<object> TestEdge(McpContext me, Services.Proxy.ProxyService proxy, CancellationToken ct)
    {
        RequireHosting(me);
        var r = await proxy.TestEdgeAsync(ct);
        return new { ok = r.Ok, provider = r.Kind, message = r.Message };
    }

    [McpServerTool(Name = "move_domains_to_edge_setting"), Description(
        "Re-publish every customer domain that is still routed the other way than the edge switch says (edge on → move to the edge, off → back to the host proxies). Each old route is removed first. DNS of moved domains must point at the new entry (the cloud server for the edge). Confirm with the user first. Requires the hosting right on an all-instances key.")]
    public static async Task<object> MoveDomainsToEdgeSetting(McpContext me, Services.Proxy.ProxyService proxy, CancellationToken ct)
    {
        RequireCloudWide(me);
        var r = await proxy.MoveCustomerDomainsToCurrentWayAsync(ct);
        return new { moved = r.Created, failed = r.Failed, errors = r.Errors };
    }


    [McpServerTool(Name = "migrate_instance"), Description(
        "Move a site to another host ('local' = this cloud's own Docker host, or a node id from list_nodes). The data volume is copied 1:1, so the site keeps its identity, content and cloud link; its domain route moves along (DNS must then point at the new host). The site is OFFLINE during the move (minutes, depending on its size) — confirm with the user first. Runs in the background; poll get_migrations. removeSource=true deletes the old container AND its data afterwards (needs the restore right as well); default keeps it stopped and renamed as a way back. Requires the hosting right; honours the key's instance scope.")]
    public static async Task<object> MigrateInstance(McpContext me, AppDbContext db, Services.Nodes.MigrationService migrations,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("'local' or a node id.")] string target,
        [Description("Delete the old copy (container + data) after a successful move. Default false.")] bool removeSource = false,
        CancellationToken ct = default)
    {
        RequireHosting(me);
        if (removeSource && !me.Key.CanRestore)
            throw new McpException("Die alte Kopie entfernen braucht zusätzlich das Wiederherstellen-Recht.");
        var inst = await ResolveAsync(me, db, instanceId, ct);
        Node? node = null;
        if (target != "local")
            node = await db.Nodes.FirstOrDefaultAsync(n => n.PublicId == target, ct) ?? throw new McpException("Ziel-Node nicht gefunden.");
        var (m, err) = await migrations.StartAsync(inst, node, removeSource, "mcp:" + me.Key.Name, ct);
        if (m is null) throw new McpException(err ?? "Umzug nicht möglich.");
        return new { ok = true, migration = Services.Nodes.MigrationService.Json(m) };
    }

    [McpServerTool(Name = "get_migrations"), Description(
        "The moves of a site, newest first: state (running/succeeded/failed/rolled-back), current step, source and target, and the step log. Any valid key within its instance scope.")]
    public static async Task<object> GetMigrations(McpContext me, AppDbContext db, Services.Nodes.MigrationService migrations,
        [Description("The instance id, as returned by list_instances.")] string instanceId, CancellationToken ct = default)
    {
        var inst = await ResolveAsync(me, db, instanceId, ct);
        return (await migrations.HistoryAsync(inst.Id, 10, ct)).Select(Services.Nodes.MigrationService.Json);
    }

    [McpServerTool(Name = "get_cloud_update_status"), Description(
        "The cloud's own version, the newest published version, whether a self-update can run here (and why not), and how the last self-update went (state + step log). Any valid key.")]
    public static async Task<object> GetCloudUpdateStatus(McpContext me, CloudUpdaterService updater, CancellationToken ct)
    {
        _ = me.Key;
        var s = await updater.StatusAsync(checkRegistry: true, ct);
        return new
        {
            current = s.Current, latest = s.Latest, updateAvailable = s.UpdateAvailable, checkError = s.CheckError,
            canSelfUpdate = s.CanSelfUpdate, blocker = s.Blocker,
            lastRun = s.LastRun is null ? null : new { state = s.LastRun.State, message = s.LastRun.Message, startedAt = s.LastRun.StartedAt, finishedAt = s.LastRun.FinishedAt, log = s.LastRun.Log },
        };
    }

    [McpServerTool(Name = "update_cloud"), Description(
        "Update THIS cloud to the newest image. A helper container swaps the cloud's container, health-checks the new one and rolls back (container AND database) if it does not come up. The cloud is unreachable for about 1–2 minutes — confirm with the user first. Afterwards poll get_cloud_update_status. Requires the hosting right on an all-instances key.")]
    public static async Task<object> UpdateCloud(McpContext me, CloudUpdaterService updater, CancellationToken ct)
    {
        RequireCloudWide(me);
        var r = await updater.StartAsync(ct);
        if (!r.Ok) throw new McpException(r.Message);
        return new { ok = true, message = r.Message };
    }

    // ---- Matcad of "Dieser Host" (MatcadAdminService — the same as Hosting → Hosts → Dieser Host → Matcad) --------

    private static MatCMS.Cloud.Services.Proxy.MatcadAdminService.Result Must(MatCMS.Cloud.Services.Proxy.MatcadAdminService.Result r) =>
        r.Ok ? r : throw new McpException(r.Message);

    [McpServerTool(Name = "get_matcad"), Description(
        "The Matcad of this cloud's own host (only when its proxy is Matcad): base domain + ACME e-mail, the DNS provider types with their credential fields, the DNS providers (secrets never returned — only which secret fields are set) and EVERY domain (route) it serves. Routes with managedBy were created by the cloud for an instance and can only be changed on that instance. Requires the hosting right on an all-instances key.")]
    public static async Task<object> GetMatcad(McpContext me, MatCMS.Cloud.Services.Proxy.MatcadAdminService matcad, CancellationToken ct = default)
    {
        RequireCloudWide(me);
        try { return await matcad.OverviewAsync(ct); }
        catch (MatCMS.Cloud.Services.Proxy.MatcadException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "set_matcad_settings"), Description(
        "Set Matcad's base domain (scope of its login cookie, grouping) and/or the ACME e-mail for Let's Encrypt. Also the DNS propagation wait for wildcard certificates (slow DNS like netcup needs several minutes). Omit a value to keep it, pass an empty string to clear it. The base domain of the instances' automatic addresses is a different setting: set_auto_domain. Requires the hosting right on an all-instances key.")]
    public static async Task<object> SetMatcadSettings(McpContext me, MatCMS.Cloud.Services.Proxy.MatcadAdminService matcad,
        string? baseDomain = null, string? acmeEmail = null,
        [Description("Seconds to wait after writing the DNS record (netcup: 600–900).")] int? propagationDelay = null,
        [Description("Seconds to wait at most for the record to be visible; -1 = do not check.")] int? propagationTimeout = null,
        CancellationToken ct = default)
    {
        RequireCloudWide(me);
        return new { ok = true, message = Must(await matcad.SaveSettingsAsync(baseDomain, acmeEmail, propagationDelay, propagationTimeout, ct)).Message };
    }

    [McpServerTool(Name = "save_matcad_provider"), Description(
        "Create (no id) or update a DNS provider in Matcad — needed for wildcard certificates (DNS challenge). type = a provider type id from get_matcad (netcup, cloudflare, hetzner …), credentials = its fields. On update an omitted or empty secret keeps the stored value. Set test=true to check the credentials against the provider instead of saving. Requires the hosting right on an all-instances key.")]
    public static async Task<object> SaveMatcadProvider(McpContext me, MatCMS.Cloud.Services.Proxy.MatcadAdminService matcad,
        string type, string? name = null, long? id = null, Dictionary<string, string?>? credentials = null, bool test = false,
        CancellationToken ct = default)
    {
        RequireCloudWide(me);
        if (test)
        {
            var t = await matcad.TestProviderAsync(id, type, credentials, ct);
            return new { ok = t.Ok, message = t.Message };
        }
        var r = Must(await matcad.SaveProviderAsync(id, name, type, credentials, ct));
        return new { ok = true, id = r.Id, message = r.Message };
    }

    [McpServerTool(Name = "delete_matcad_provider"), Description(
        "Delete a DNS provider in Matcad (refused while a domain still uses it). Requires the hosting right on an all-instances key.")]
    public static async Task<object> DeleteMatcadProvider(McpContext me, MatCMS.Cloud.Services.Proxy.MatcadAdminService matcad, long id,
        CancellationToken ct = default)
    {
        RequireCloudWide(me);
        return new { ok = true, message = Must(await matcad.DeleteProviderAsync(id, ct)).Message };
    }

    [McpServerTool(Name = "save_matcad_route"), Description(
        "Create (no id) or update a domain in Matcad that the cloud does not manage itself — e.g. the cloud's own address or another service on the server. target \"proxy\" forwards to upstream (http://container:8080 on the proxy's network, or host:port), \"redirect\" sends visitors to fallbackUrl. wildcard=true serves *.host with one certificate and needs providerId. For an instance's own domain use publish_domain instead. Requires the hosting right on an all-instances key.")]
    public static async Task<object> SaveMatcadRoute(McpContext me, MatCMS.Cloud.Services.Proxy.MatcadAdminService matcad,
        string host, string target = "proxy", string? upstream = null, string? fallbackUrl = null, long? id = null, string? name = null,
        bool wildcard = false, long? providerId = null, bool enabled = true, bool allowEmbedding = false, CancellationToken ct = default)
    {
        RequireCloudWide(me);
        var r = Must(await matcad.SaveRouteAsync(new(id, host, name, target, upstream, fallbackUrl, wildcard, providerId, enabled, allowEmbedding), ct));
        return new { ok = true, id = r.Id, message = r.Message };
    }

    [McpServerTool(Name = "delete_matcad_route"), Description(
        "Delete a domain in Matcad. Refused for a route the cloud created for an instance (use unpublish_domain / remove_host_address there). Requires the hosting right on an all-instances key.")]
    public static async Task<object> DeleteMatcadRoute(McpContext me, MatCMS.Cloud.Services.Proxy.MatcadAdminService matcad, long id,
        CancellationToken ct = default)
    {
        RequireCloudWide(me);
        return new { ok = true, message = Must(await matcad.DeleteRouteAsync(id, ct)).Message };
    }
}
