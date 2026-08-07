namespace MatCMS.Cloud.Services;

/// <summary>
/// Central MatCMS release poll. A singleton cache + a background refresh (every 30 min), so a page
/// render, a heartbeat and the notifier all read the same in-memory answer and GHCR sees one request
/// per half hour no matter how many instances are connected.
/// </summary>
public class ReleaseWatcher
{
    // The image the managed instances run.
    public const string Owner = "real-ttx";
    public const string Repo = "matcms";
    public static string ImageRef => $"ghcr.io/{Owner}/{Repo}";

    private readonly GhcrClient _ghcr;
    private readonly ILogger<ReleaseWatcher> _log;

    public ReleaseWatcher(GhcrClient ghcr, ILogger<ReleaseWatcher> log)
    {
        _ghcr = ghcr;
        _log = log;
    }

    /// <summary>Newest release tag of ghcr.io/real-ttx/matcms, or null until the first poll succeeds.</summary>
    public string? LatestVersion { get; private set; }

    public DateTime? LastCheckedUtc { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>True when the instance's reported version is older than the newest release. Unknown
    /// or non-numeric versions (nightly/local builds) never count as outdated.</summary>
    public bool IsUpdateAvailableFor(string? instanceVersion) =>
        ReleaseVersion.IsNewer(LatestVersion, instanceVersion);

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var tags = await _ghcr.ListTagsAsync(Owner, Repo, ct);
        LastCheckedUtc = DateTime.UtcNow;
        if (!tags.Ok)
        {
            LastError = tags.Error;
            _log.LogWarning("Release check failed: {Error}", tags.Error);
            return;
        }

        var latest = ReleaseVersion.Latest(tags.Tags);
        LastError = latest is null ? "Keine Versions-Tags gefunden." : null;
        if (latest is not null && latest != LatestVersion)
        {
            _log.LogInformation("Newest MatCMS release: {Version}", latest);
            LatestVersion = latest;
        }
    }
}

/// <summary>Refreshes <see cref="ReleaseWatcher"/> on startup and every 30 minutes.</summary>
public class ReleaseWatcherService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private readonly ReleaseWatcher _watcher;

    public ReleaseWatcherService(ReleaseWatcher watcher) => _watcher = watcher;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Never let a registry hiccup take the host down — RefreshAsync already swallows its own
            // errors, this is the belt-and-braces guard for the loop itself.
            try { await _watcher.RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch { /* logged inside */ }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
