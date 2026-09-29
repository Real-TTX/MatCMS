namespace MatCMS.Cloud.Services;

/// <summary>What the shared <c>_CloudUpdateCard</c> partial renders. <paramref name="CanAct"/> = the viewer
/// may start an update (Admins only; the pages hosting the card are not all Admin-only).</summary>
public record CloudUpdateCard(CloudUpdaterService.Status Status, bool CanAct);

/// <summary>
/// The cloud updating ITSELF. One service behind all three surfaces — the admin UI (About + Hosting),
/// the operator API (<c>/api/v1/cloud/update</c>) and the MCP tools — so an AI agent can do exactly what
/// the operator can. The actual swap runs in a helper container (<see cref="DockerHostService.SpawnSelfUpdateHelperAsync"/>);
/// this service decides whether it may start and reports how the last run went.
/// </summary>
public class CloudUpdaterService
{
    private readonly DockerHostService _docker;
    private readonly VersionService _version;
    private readonly string _dataDir;

    public CloudUpdaterService(DockerHostService docker, VersionService version, IWebHostEnvironment env)
    {
        _docker = docker;
        _version = version;
        _dataDir = Path.Combine(env.ContentRootPath, "appdata");
    }

    /// <param name="Latest">Null unless a registry check was requested (it is a network call).</param>
    /// <param name="Blocker">Why a self-update cannot run here, in the operator's words; null = it can.</param>
    public record Status(string Current, string ImageRef, string? Latest, bool? UpdateAvailable, string? CheckError,
        bool DockerReachable, string? SelfContainerId, string? Blocker, SelfUpdateState? LastRun)
    {
        public bool CanSelfUpdate => Blocker is null;
    }

    public async Task<Status> StatusAsync(bool checkRegistry, CancellationToken ct = default)
    {
        string? latest = null, error = null;
        bool? available = null;
        if (checkRegistry)
        {
            var c = await _version.CheckAsync(ct);
            latest = c.Latest; error = c.Error; available = c.Error is null ? c.UpdateAvailable : null;
        }

        var reachable = await _docker.IsReachableAsync(ct);
        var selfId = SelfContainer.Current;
        var last = SelfUpdateState.Load(_dataDir);

        // A run still marked in flight whose helper is no longer running died without writing a result
        // (e.g. a new image that cannot even start its runtime). Say so, instead of "läuft" forever — the
        // grace period covers the seconds between asking for the helper and it starting.
        if (last is { InFlight: true } && last.StartedAt < DateTime.UtcNow.AddSeconds(-30) && reachable)
        {
            var helper = await _docker.GetUpdaterHelperStateAsync(ct);
            if (!helper.Running)
            {
                last.State = "failed";
                last.FinishedAt = DateTime.UtcNow;
                last.Message = helper.Exists
                    ? $"Der Update-Helfer hat sich ohne Abschlussmeldung beendet (Exit-Code {helper.ExitCode}). Siehe Protokoll."
                    : "Der Update-Helfer läuft nicht mehr und hat kein Ergebnis hinterlassen.";
                last.Log.Add($"{DateTime.UtcNow:HH:mm:ss} {last.Message}");
                last.Save(_dataDir);
            }
        }

        string? blocker = null;
        if (!_docker.Configured) blocker = "Kein Docker-Zugriff konfiguriert (MatCmsCloud__Docker__Endpoint).";
        else if (!reachable) blocker = "Der Docker-Daemon antwortet nicht.";
        else if (selfId is null) blocker = "Die Cloud läuft nicht erkennbar in einem Container.";
        else if (await _docker.FindContainerAsync(selfId, ct) is null) blocker = "Der eigene Container ist auf diesem Docker-Host nicht zu finden.";
        else if (last is { InFlight: true } && last.StartedAt > DateTime.UtcNow.AddMinutes(-10))
            blocker = "Ein Cloud-Update läuft bereits.";

        return new Status(_version.Current, _version.ImageRef, latest, available, error, reachable, selfId, blocker, last);
    }

    /// <summary>Starts the self-update. Returns once the helper runs; the cloud restarts shortly after.</summary>
    public async Task<DockerHostService.SpawnResult> StartAsync(CancellationToken ct = default)
    {
        var s = await StatusAsync(checkRegistry: false, ct);
        if (s.Blocker is not null) return new(false, s.Blocker);

        // Written BEFORE the helper starts, so the "läuft" state is visible at once and survives the
        // restart the helper is about to cause.
        var state = new SelfUpdateState
        {
            State = "started",
            StartedAt = DateTime.UtcNow,
            FromVersion = _version.Current,
            Log = { $"{DateTime.UtcNow:HH:mm:ss} Update angefordert (Version {_version.Current})." }
        };
        state.Save(_dataDir);

        var r = await _docker.SpawnSelfUpdateHelperAsync(s.SelfContainerId!, ct);
        if (!r.Ok)
        {
            state.State = "failed";
            state.Message = r.Message;
            state.FinishedAt = DateTime.UtcNow;
            state.Log.Add($"{DateTime.UtcNow:HH:mm:ss} {r.Message}");
            state.Save(_dataDir);
        }
        return r;
    }
}
