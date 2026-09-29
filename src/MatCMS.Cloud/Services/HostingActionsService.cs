using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Container actions on an instance that runs on this cloud's Docker host: status, start/stop/restart,
/// update, logs. One implementation behind the instance's Hosting tab, the operator API and the MCP
/// tools — the handlers used to live in the Details page, which meant an API or an AI agent could not do
/// what the operator could (see the API-first rule in docs/hosting-platform.md).
/// <para>Deliberately NOT gated on the Hosting module: these actions predate it and depend only on the
/// cloud reaching the container. The module switch governs provisioning and the Hosting menu.</para>
/// </summary>
public class HostingActionsService
{
    private readonly AppDbContext _db;
    private readonly InstanceService _instances;
    private readonly DockerHostService _docker;
    private readonly ReleaseWatcher _releases;

    public HostingActionsService(AppDbContext db, InstanceService instances, DockerHostService docker, ReleaseWatcher releases)
    {
        _db = db; _instances = instances; _docker = docker; _releases = releases;
    }

    public record ActionResult(bool Ok, string Message);

    /// <summary>Whether the cloud can act on this instance's container: it runs on our daemon.</summary>
    public static bool IsLocal(Instance i) => i.Hosting == InstanceHosting.Local && i.ContainerId is not null;

    /// <summary>Re-reads hosting + container state from the daemon (best effort). Called before an
    /// API/MCP action, which — unlike the page — has not just loaded and classified the instance.</summary>
    public async Task RefreshAsync(Instance item, CancellationToken ct = default)
    {
        try { await _instances.ClassifyAsync(item, ct); await _db.SaveChangesAsync(ct); }
        catch { /* keep last known state */ }
    }

    public Task<DockerHostService.ContainerDetails?> DetailsAsync(Instance item, CancellationToken ct = default)
        => IsLocal(item) ? _docker.GetContainerDetailsAsync(item.ContainerId, ct) : Task.FromResult<DockerHostService.ContainerDetails?>(null);

    public async Task<(bool Ok, string Text)> LogsAsync(Instance item, int tail, CancellationToken ct = default)
        => IsLocal(item) ? await _docker.GetContainerLogsAsync(item.ContainerId!, tail, ct)
                         : (false, "Diese Instanz läuft nicht auf diesem Docker-Host.");

    /// <summary>Start / stop / restart. <paramref name="item"/> must be tracked (events are logged on it).</summary>
    public async Task<ActionResult> PowerAsync(Instance item, DockerHostService.PowerAction action, CancellationToken ct = default)
    {
        if (!IsLocal(item)) return new(false, "Diese Instanz läuft nicht auf diesem Docker-Host.");

        var result = action switch
        {
            DockerHostService.PowerAction.Start => await _docker.StartContainerAsync(item.ContainerId!, ct),
            DockerHostService.PowerAction.Stop => await _docker.StopContainerAsync(item.ContainerId!, ct),
            _ => await _docker.RestartContainerAsync(item.ContainerId!, ct),
        };

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

        // Reflect the new state at once — a stopped container never beats, and the monitor lags a tick.
        try { await _instances.ClassifyAsync(item, ct); } catch { /* keep last state */ }
        await _db.SaveChangesAsync(ct);
        return new(result.Ok, result.Message);
    }

    /// <summary>Pull + recreate the instance container (blocking: the caller wants the outcome).</summary>
    public async Task<ActionResult> UpdateAsync(Instance item, CancellationToken ct = default)
    {
        if (!IsLocal(item)) return new(false, "Diese Instanz läuft nicht auf diesem Docker-Host — Update dort ausführen.");

        _instances.Log(item, InstanceEventKind.UpdateStarted, "Update über die Cloud gestartet.", notified: true);
        await _db.SaveChangesAsync(ct);

        var result = await _docker.UpdateContainerAsync(item.ContainerId!, ct);
        _instances.Log(item, result.Ok ? InstanceEventKind.UpdateSucceeded : InstanceEventKind.UpdateFailed,
            result.Message, notified: true);

        // The recreated container has not beaten yet, so its reported Version is still the old one — which
        // left "Update verfügbar" standing and invited a second, pointless update. Move it forward
        // optimistically; the next heartbeat reports the real version and corrects it if needed.
        if (result.Ok && _releases.LatestVersion is string latest)
            item.Version = latest;
        await _db.SaveChangesAsync(ct);
        return new(result.Ok, result.Message);
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
