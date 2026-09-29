using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services.Nodes;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Container actions on an instance the cloud can reach — on its own Docker host, or on a connected node
/// through that node's agent: status, start/stop/restart, update, logs. One implementation behind the
/// instance's Hosting tab, the operator API and the MCP tools — the handlers used to live in the Details page,
/// which meant an API or an AI agent could not do what the operator could (see the API-first rule in
/// docs/hosting-platform.md).
/// <para>On a node the same engine runs as a job (<see cref="NodeService.RunAsync"/>); the caller waits for the
/// report, so the shape of every method is the same for both.</para>
/// <para>Deliberately NOT gated on the Hosting module: these actions predate it and depend only on the
/// cloud reaching the container. The module switch governs provisioning and the Hosting menu.</para>
/// </summary>
public class HostingActionsService
{
    private readonly AppDbContext _db;
    private readonly InstanceService _instances;
    private readonly DockerHostService _docker;
    private readonly ReleaseWatcher _releases;
    private readonly NodeService _nodes;

    public HostingActionsService(AppDbContext db, InstanceService instances, DockerHostService docker, ReleaseWatcher releases, NodeService nodes)
    {
        _db = db; _instances = instances; _docker = docker; _releases = releases; _nodes = nodes;
    }

    public record ActionResult(bool Ok, string Message);

    /// <summary>The instance runs on the cloud's OWN daemon.</summary>
    public static bool IsLocal(Instance i) => i.Hosting == InstanceHosting.Local && i.ContainerId is not null;

    /// <summary>The instance runs on a connected node.</summary>
    public static bool IsOnNode(Instance i) => i.Hosting == InstanceHosting.Node && i.NodeId is not null && i.ContainerId is not null;

    /// <summary>The cloud can act on the instance's container at all — here or through a node.</summary>
    public static bool CanAct(Instance i) => IsLocal(i) || IsOnNode(i);

    private const string NotHere = "Diese Instanz läuft weder auf dem Docker-Host dieser Cloud noch auf einem verbundenen Node.";

    /// <summary>Re-reads hosting + container state (best effort). Called before an API/MCP action, which — unlike
    /// the page — has not just loaded and classified the instance.</summary>
    public async Task RefreshAsync(Instance item, CancellationToken ct = default)
    {
        try { await _instances.ClassifyAsync(item, ct); await _db.SaveChangesAsync(ct); }
        catch { /* keep last known state */ }
    }

    private async Task<Node?> NodeOfAsync(Instance item, CancellationToken ct) =>
        item.NodeId is { } id ? await _db.Nodes.FindAsync(new object[] { id }, ct) : null;

    public async Task<DockerHostService.ContainerDetails?> DetailsAsync(Instance item, CancellationToken ct = default)
    {
        if (IsLocal(item)) return await _docker.GetContainerDetailsAsync(item.ContainerId, ct);
        if (!IsOnNode(item) || await NodeOfAsync(item, ct) is not { } node) return null;
        // Short: this is on the Hosting tab's page load — a slow node must not hold the page for long.
        var r = await _nodes.RunAsync(node, NodeJobKinds.Details, new ContainerJob(item.ContainerId!), item.Id, TimeSpan.FromSeconds(8), ct);
        return r.Ok ? NodeJobExecutor.Deserialize<DockerHostService.ContainerDetails>(r.ResultJson) : null;
    }

    public async Task<(bool Ok, string Text)> LogsAsync(Instance item, int tail, CancellationToken ct = default)
    {
        if (IsLocal(item)) return await _docker.GetContainerLogsAsync(item.ContainerId!, tail, ct);
        if (!IsOnNode(item) || await NodeOfAsync(item, ct) is not { } node) return (false, NotHere);
        var r = await _nodes.RunAsync(node, NodeJobKinds.Logs, new ContainerJob(item.ContainerId!, Tail: Math.Clamp(tail, 1, 5000)), item.Id, TimeSpan.FromSeconds(30), ct);
        if (!r.Ok) return (false, r.Message);
        return (true, NodeJobExecutor.Deserialize<LogsResult>(r.ResultJson)?.Logs ?? "");
    }

    private sealed record LogsResult(string Logs);

    /// <summary>Start / stop / restart. <paramref name="item"/> must be tracked (events are logged on it).</summary>
    public async Task<ActionResult> PowerAsync(Instance item, DockerHostService.PowerAction action, CancellationToken ct = default)
    {
        ActionResult result;
        if (IsLocal(item))
        {
            var r = action switch
            {
                DockerHostService.PowerAction.Start => await _docker.StartContainerAsync(item.ContainerId!, ct),
                DockerHostService.PowerAction.Stop => await _docker.StopContainerAsync(item.ContainerId!, ct),
                _ => await _docker.RestartContainerAsync(item.ContainerId!, ct),
            };
            result = new(r.Ok, r.Message);
        }
        else if (IsOnNode(item) && await NodeOfAsync(item, ct) is { } node)
        {
            var verb = action switch { DockerHostService.PowerAction.Start => "start", DockerHostService.PowerAction.Stop => "stop", _ => "restart" };
            var r = await _nodes.RunAsync(node, NodeJobKinds.Power, new ContainerJob(item.ContainerId!, verb), item.Id, TimeSpan.FromSeconds(60), ct);
            result = new(r.Ok, r.Message + (r.Ok ? $" (Node „{node.Name}“)" : ""));
        }
        else return new(false, NotHere);

        if (result.Ok)
        {
            var kind = action switch
            {
                DockerHostService.PowerAction.Start => InstanceEventKind.ContainerStarted,
                DockerHostService.PowerAction.Stop => InstanceEventKind.ContainerStopped,
                _ => InstanceEventKind.ContainerRestarted,
            };
            _instances.Log(item, kind, result.Message);
        }

        // Reflect the new state at once — a stopped container never beats, and the monitor lags a tick. On a
        // node, the agent's report beat carried a fresh inventory, so classifying reads the new state too.
        try { await _instances.ClassifyAsync(item, ct); } catch { /* keep last state */ }
        await _db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Pull + recreate the instance container (blocking: the caller wants the outcome).</summary>
    public async Task<ActionResult> UpdateAsync(Instance item, CancellationToken ct = default)
    {
        if (!CanAct(item)) return new(false, NotHere + " Update dort ausführen.");
        var node = IsOnNode(item) ? await NodeOfAsync(item, ct) : null;
        if (IsOnNode(item) && node is null) return new(false, NotHere);

        _instances.Log(item, InstanceEventKind.UpdateStarted,
            node is null ? "Update über die Cloud gestartet." : $"Update über Node „{node.Name}“ gestartet.", notified: true);
        await _db.SaveChangesAsync(ct);

        ActionResult result;
        if (node is null)
        {
            var r = await _docker.UpdateContainerAsync(item.ContainerId!, ct);
            result = new(r.Ok, r.Message);
        }
        else
        {
            // Pull + recreate on a remote host can take minutes; past the wait the job keeps running on the node.
            var r = await _nodes.RunAsync(node, NodeJobKinds.Update, new ContainerJob(item.ContainerId!), item.Id, TimeSpan.FromMinutes(5), ct);
            if (!r.Finished) return new(true, r.Message);
            result = new(r.Ok, r.Message);
        }
        _instances.Log(item, result.Ok ? InstanceEventKind.UpdateSucceeded : InstanceEventKind.UpdateFailed, result.Message, notified: true);

        // The recreated container has not beaten yet, so its reported Version is still the old one — which
        // left "Update verfügbar" standing and invited a second, pointless update. Move it forward
        // optimistically; the next heartbeat reports the real version and corrects it if needed.
        if (result.Ok && _releases.LatestVersion is string latest)
            item.Version = latest;
        await _db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Parses the API/MCP action name. Null for anything else.</summary>
    public static DockerHostService.PowerAction? ParsePower(string? action) => action?.Trim().ToLowerInvariant() switch
    {
        "start" => DockerHostService.PowerAction.Start,
        "stop" => DockerHostService.PowerAction.Stop,
        "restart" => DockerHostService.PowerAction.Restart,
        _ => null,
    };
}
