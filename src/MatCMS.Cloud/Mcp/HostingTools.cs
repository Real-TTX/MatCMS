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
