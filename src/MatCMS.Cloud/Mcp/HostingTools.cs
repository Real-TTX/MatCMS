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
        "The public domain of an instance: domain, via which proxy provider, route id, last error, publish time, and (check=true) whether the route still exists at the proxy. Any valid key within its instance scope.")]
    public static async Task<object> GetDomainStatus(McpContext me, AppDbContext db, Services.Proxy.ProxyService proxy,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("Ask the proxy whether the route still exists (default true).")] bool check = true,
        CancellationToken ct = default)
    {
        var inst = await ResolveAsync(me, db, instanceId, ct);
        var s = await proxy.StatusAsync(inst, check, ct);
        return new { domain = s.Domain, provider = s.Provider, routeId = s.RouteId, error = s.Error, publishedAt = s.PublishedAt, routeExists = s.RouteExists };
    }

    [McpServerTool(Name = "publish_domain"), Description(
        "Publish an instance under a domain (or move it to a new one): with a managing proxy (matcad/caddy) a route with TLS is created/updated for the instance's container; with provider 'none' the domain is only recorded. The domain's DNS must already point at the proxy host. pushCanonical (default true) also sets the site's canonical URL to https://<domain>. Requires the hosting right; honours the key's instance scope.")]
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
}
