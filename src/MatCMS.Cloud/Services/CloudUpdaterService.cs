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
    private readonly Data.AppDbContext _db;
    private readonly BulkUpdateService _bulk;
    private readonly string _dataDir;

    public CloudUpdaterService(DockerHostService docker, VersionService version, Data.AppDbContext db, BulkUpdateService bulk, IWebHostEnvironment env)
    {
        _docker = docker;
        _version = version;
        _db = db;
        _bulk = bulk;
        _dataDir = Path.Combine(env.ContentRootPath, "appdata");
    }

    /// <summary>The current run as the helper writes it (a file on the shared volume — readable while the new
    /// version starts). Null = never updated.</summary>
    public SelfUpdateState? LastRun() => SelfUpdateState.Load(_dataDir);

    /// <summary>
    /// Work the cloud's own restart would cut off: a bulk update in the middle of its list, a move between hosts
    /// (the data streams THROUGH the cloud), or a node job still pending or running (its report could never be
    /// folded in). Null = nothing is running. Checked before a self-update starts — on every surface.
    /// </summary>
    public async Task<string?> BusyWithAsync(CancellationToken ct = default)
    {
        if (_bulk.AnyRunning) return "Gerade läuft „Alle aktualisieren“ — erst danach kann die Cloud sich selbst aktualisieren.";
        if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(_db.InstanceMigrations, m => m.State == "running", ct))
            return "Gerade zieht eine Instanz auf einen anderen Host um — erst danach kann die Cloud sich selbst aktualisieren.";
        // Within the same windows the node service expires jobs in: a job for a node that went offline stays
        // "pending" in the table until that node beats again, and must not block the cloud for ever.
        var now = DateTime.UtcNow;
        var pendingCut = now - Models.NodeJob.PendingTimeout;
        var runningCut = now - Models.NodeJob.RunningTimeout;
        if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(_db.NodeJobs,
                j => (j.State == Models.NodeJobState.Pending && j.CreatedAt >= pendingCut)
                  || (j.State == Models.NodeJobState.Running && j.StartedAt >= runningCut), ct))
            return "Auf einem Node läuft noch ein Auftrag — erst danach kann die Cloud sich selbst aktualisieren.";
        return null;
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
        if (await BusyWithAsync(ct) is { } busy) return new(false, busy);

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
        SelfUpdateLock.Invalidate();

        var r = await _docker.SpawnSelfUpdateHelperAsync(s.SelfContainerId!, ct);
        if (!r.Ok)
        {
            state.State = "failed";
            state.Message = r.Message;
            state.FinishedAt = DateTime.UtcNow;
            state.Log.Add($"{DateTime.UtcNow:HH:mm:ss} {r.Message}");
            state.Save(_dataDir);
            SelfUpdateLock.Invalidate();
        }
        return r;
    }
}
