using System.Security.Cryptography;
using System.Text;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;
namespace MatCMS.Cloud.Services;

/// <summary>
/// Enrollment, authentication and heartbeat handling for connected MatCMS installations — plus the
/// local/remote classification that decides whether the cloud may update an instance itself.
/// </summary>
public class InstanceService
{
    /// <summary>Contract version this build speaks; instances reporting less are badged "veraltet".
    /// Defined once in <c>MatCMS.Shared</c> — this alias only keeps the cloud-side call sites
    /// readable, so there is no second number to bump.</summary>
    public const int CurrentProtocolVersion = CloudProtocol.Version;

    /// <summary>An instance counts as offline after ~2.5 missed beats (60 s cadence).</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(150);

    /// <summary>Label for an instance that has not told us its site name yet. Treated as "unset", so
    /// a later heartbeat carrying a real name replaces it.</summary>
    public const string PlaceholderName = "Neue Instanz";

    private readonly AppDbContext _db;
    private readonly DockerHostService _docker;
    private readonly ReleaseWatcher _releases;
    private readonly ProfileService _profiles;
    private readonly CloudContext _cloud;

    public InstanceService(AppDbContext db, DockerHostService docker, ReleaseWatcher releases, ProfileService profiles, CloudContext cloud)
    {
        _cloud = cloud;
        _db = db;
        _docker = docker;
        _releases = releases;
        _profiles = profiles;
    }

    public static bool IsOnline(Instance i) =>
        i.LastHeartbeatUtc is not null && DateTime.UtcNow - i.LastHeartbeatUtc.Value <= OfflineAfter;

    /// <summary>An instance that has connected but speaks an older contract than this build.</summary>
    public static bool IsOutdatedProtocol(Instance i) =>
        i.HasConnected && i.ProtocolVersion < CurrentProtocolVersion;

    public bool IsUpdateAvailable(Instance i) => _releases.IsUpdateAvailableFor(i.Version);

    /// <summary>True while the instance has not applied its profile's current revision.</summary>
    public static bool IsOutOfSync(Instance i) =>
        i.Profile is not null && i.AppliedRevision < i.Profile.Revision;

    /// <summary>
    /// What the instance's last report adds up to. The revision alone stopped being enough to mean
    /// "in sync" once modes existed: an instance can sit on the current revision with items skipped
    /// (intentionally, in <c>once</c>/<c>add</c> mode) or failed (not intentionally at all).
    /// </summary>
    /// <param name="Skipped">Left alone on purpose — nothing to act on, but worth showing so
    /// "synchron" does not imply "everything from the profile is there".</param>
    /// <param name="Failed">Items the instance could not apply. Usually the apply also threw and
    /// <c>LastSyncError</c> is set, but not always: a template named for activation that never
    /// arrived fails on its own without aborting anything.</param>
    public sealed record SyncSummary(int Installed, int Updated, int Skipped, int Failed)
    {
        public int Total => Installed + Updated + Skipped + Failed;
    }

    /// <summary>Never throws — the report is foreign input from an instance that may run a newer or
    /// a broken build, and a malformed one must not take a listing down.</summary>
    public static SyncSummary Summarise(string? reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson)) return new(0, 0, 0, 0);

        List<SyncItemReport>? items;
        try { items = System.Text.Json.JsonSerializer.Deserialize<List<SyncItemReport>>(reportJson); }
        catch { return new(0, 0, 0, 0); }
        if (items is null) return new(0, 0, 0, 0);

        return new(
            items.Count(x => x.Outcome == "installed"),
            items.Count(x => x.Outcome == "updated"),
            items.Count(x => x.Outcome.StartsWith("skipped", StringComparison.Ordinal)),
            items.Count(x => x.Outcome == "failed"));
    }

    // --- Enrollment ---------------------------------------------------------

    public sealed record RegisterResult(Instance? Instance, string? Token, string? Error);

    /// <summary>
    /// Instance-initiated enrollment: the instance presents a profile's join code and gets an id +
    /// token back. This is the direction that works behind NAT — nothing has to reach the site.
    /// <para>An unknown code is refused outright, so knowing the cloud URL alone is not enough to
    /// create records here.</para>
    /// </summary>
    public async Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var profile = await _profiles.FindByJoinCodeAsync(request.JoinCode);
        if (profile is null) return new(null, null, "Ungültiger Join-Code.");

        var token = NewToken();
        var instance = new Instance
        {
            PublicId = NewPublicId(),
            TokenHash = HashToken(token),
            Name = string.IsNullOrWhiteSpace(request.SiteName) ? PlaceholderName : request.SiteName!.Trim(),
            Url = string.IsNullOrWhiteSpace(request.Url) ? null : request.Url!.Trim(),
            ProfileId = profile.Id,
            Status = profile.AutoApprove ? InstanceStatus.Approved : InstanceStatus.Pending,
            ProtocolVersion = request.ProtocolVersion,
            Version = Trim(request.Version),
            HostName = Trim(request.HostName),
            ContainerId = Trim(request.ContainerId),
            ImageRef = Trim(request.ImageRef)
        };

        _db.Instances.Add(instance);
        await _db.SaveChangesAsync(ct);

        await ClassifyAsync(instance, ct);
        Log(instance, InstanceEventKind.Connected,
            instance.Status == InstanceStatus.Approved
                ? $"Instanz hat sich über Profil \"{profile.Name}\" angemeldet und wurde automatisch angenommen."
                : $"Instanz hat sich über Profil \"{profile.Name}\" angemeldet und wartet auf Freigabe.");
        await _db.SaveChangesAsync(ct);

        return new(instance, token, null);
    }

    /// <summary>
    /// Cloud-initiated adoption: the operator supplies an existing instance's URL and one of ITS
    /// admin accounts. We mint the credentials here and hand them over; the instance verifies the
    /// account against its own user table before accepting. Returns the instance and the raw token
    /// so the caller can perform the handover.
    /// </summary>
    public async Task<(Instance instance, string token)> CreateForAdoptionAsync(string name, int? profileId)
    {
        var token = NewToken();
        var instance = new Instance
        {
            PublicId = NewPublicId(),
            TokenHash = HashToken(token),
            Name = string.IsNullOrWhiteSpace(name) ? PlaceholderName : name.Trim(),
            ProfileId = profileId,
            Status = InstanceStatus.Approved
        };
        _db.Instances.Add(instance);
        await _db.SaveChangesAsync();
        return (instance, token);
    }

    /// <summary>Issues a fresh token for an existing instance (the old one stops working at once).</summary>
    public async Task<string> RotateTokenAsync(Instance instance)
    {
        var token = NewToken();
        instance.TokenHash = HashToken(token);
        await _db.SaveChangesAsync();
        return token;
    }

    public async Task SetStatusAsync(Instance instance, InstanceStatus status)
    {
        if (instance.Status == status) return;
        instance.Status = status;
        Log(instance, status switch
        {
            InstanceStatus.Approved => InstanceEventKind.Approved,
            InstanceStatus.Rejected => InstanceEventKind.Rejected,
            _ => InstanceEventKind.Connected
        }, status switch
        {
            InstanceStatus.Approved => "Instanz freigegeben.",
            InstanceStatus.Rejected => "Instanz abgelehnt — Heartbeats werden zurückgewiesen.",
            _ => "Instanz wartet auf Freigabe."
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Resolves an instance from the public id + bearer token. The hash comparison is
    /// length-constant (<see cref="CryptographicOperations.FixedTimeEquals"/>) so a wrong token
    /// cannot be found byte by byte through timing. The profile is included because every caller
    /// needs it (policy, revision, config).
    /// </summary>
    public async Task<Instance?> AuthenticateAsync(string? publicId, string? token)
    {
        if (string.IsNullOrWhiteSpace(publicId) || string.IsNullOrWhiteSpace(token)) return null;

        var instance = await _db.Instances.Include(i => i.Profile)
            .FirstOrDefaultAsync(i => i.PublicId == publicId);
        if (instance is null) return null;

        var expected = Encoding.UTF8.GetBytes(instance.TokenHash);
        var actual = Encoding.UTF8.GetBytes(HashToken(token));
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual)
            ? instance
            : null;
    }

    // --- Heartbeat ----------------------------------------------------------

    /// <summary>Applies a heartbeat: stores what was reported, re-classifies local/remote, records
    /// the sync state and builds the response.</summary>
    public async Task<HeartbeatResponse> RecordHeartbeatAsync(
        Instance instance, HeartbeatRequest beat, CancellationToken ct = default)
    {
        var wasOffline = !IsOnline(instance);
        var firstEver = !instance.HasConnected;

        instance.LastHeartbeatUtc = DateTime.UtcNow;
        instance.ProtocolVersion = beat.ProtocolVersion;
        instance.Version = Trim(beat.Version);
        instance.HostName = Trim(beat.HostName);
        instance.ContainerId = Trim(beat.ContainerId);
        instance.ImageRef = Trim(beat.ImageRef);
        instance.PageCount = beat.PageCount;
        instance.PluginCount = beat.PluginCount;
        instance.UserCount = beat.UserCount;
        // Upgraded on the way IN, so everything downstream — frame, links, the mixed-content guard —
        // sees one address and cannot disagree about it. Reversible: switch the setting off and the
        // next heartbeat writes what the instance actually said.
        if (!string.IsNullOrWhiteSpace(beat.Url)) instance.Url = ForceHttps(beat.Url!.Trim());
        // The reported site name only SEEDS the label — never overwrite a name an operator has set
        // here. The placeholder counts as "not set yet", so an instance that enrolled before its
        // site name was configured still picks it up instead of staying "Neue Instanz" forever.
        if ((firstEver || instance.Name == PlaceholderName) && !string.IsNullOrWhiteSpace(beat.SiteName))
            instance.Name = beat.SiteName!.Trim();

        await RecordSyncReportAsync(instance, beat, ct);
        await ClassifyAsync(instance, ct);

        if (firstEver)
            Log(instance, InstanceEventKind.Connected, $"Instanz verbunden ({instance.Version ?? "?"}).");
        else if (wasOffline)
            Log(instance, InstanceEventKind.Recovered, "Instanz meldet sich wieder.");

        // A new beat ends the outage, so the dead-man switch may fire again next time.
        instance.OfflineNotified = false;

        await _db.SaveChangesAsync(ct);

        return new HeartbeatResponse
        {
            ProtocolVersion = CurrentProtocolVersion,
            Status = instance.Status.ToString(),
            // What the operator types into a browser — the instance cannot know it, and needs it to
            // allow the embedding. Empty means "same as the address you already use".
            CloudPublicUrl = _cloud.Get(SettingKeys.CanonicalUrl),
            LatestVersion = _releases.LatestVersion,
            UpdateAvailable = IsUpdateAvailable(instance),
            CloudCanUpdate = instance.Hosting == InstanceHosting.Local,
            DisplayName = instance.Name,
            ProfileName = instance.Profile?.Name,
            // A pending instance is told 0 so it never even asks for configuration.
            ConfigRevision = instance.Status == InstanceStatus.Approved ? instance.Profile?.Revision ?? 0 : 0,

            // A backup somebody asked to be restored. Only for an approved instance, and only the
            // OLDEST outstanding one — asking a site to overwrite itself twice in a row is never
            // what was meant, and the second request is still there on the next beat.
            Restore = instance.Status == InstanceStatus.Approved ? await PendingRestoreAsync(instance.Id, ct) : null
        };
    }

    /// <summary>Folds the instance's self-reported sync outcome into its record and logs the
    /// transitions — a sync that starts failing, and one that recovers, are both worth an entry.</summary>
    private async Task RecordSyncReportAsync(Instance instance, HeartbeatRequest beat, CancellationToken ct)
    {
        var previousRevision = instance.AppliedRevision;
        var previousError = instance.LastSyncError;

        instance.AppliedRevision = beat.AppliedRevision;
        instance.LastSyncError = string.IsNullOrWhiteSpace(beat.SyncError) ? null : beat.SyncError!.Trim();
        // Stored verbatim as the instance sent it — the cloud renders it, nothing more.
        if (beat.SyncReport is not null)
            instance.LastSyncReportJson = System.Text.Json.JsonSerializer.Serialize(beat.SyncReport);
        if (beat.AppliedRevision != previousRevision || instance.LastSyncError != previousError)
            instance.LastSyncUtc = DateTime.UtcNow;

        if (instance.LastSyncError is not null && instance.LastSyncError != previousError)
            Log(instance, InstanceEventKind.SyncFailed, $"Konfiguration konnte nicht angewendet werden: {instance.LastSyncError}");
        else if (previousError is not null && instance.LastSyncError is null)
            Log(instance, InstanceEventKind.SyncApplied, $"Konfiguration angewendet (Revision {beat.AppliedRevision}).");
        else if (beat.AppliedRevision > previousRevision && previousRevision > 0)
            Log(instance, InstanceEventKind.SyncApplied, $"Konfiguration angewendet (Revision {beat.AppliedRevision}).");

        await RecordSyncRunAsync(instance, beat, ct);
    }

    /// <summary>
    /// Appends the run to the history — but only when the instance says it IS a new run. The same
    /// report rides on every beat until the next apply, so appending on "the report changed" would
    /// both miss a re-apply with identical outcomes and risk duplicating one. An instance that
    /// predates <c>SyncRunAt</c> simply contributes no history rather than a wrong one.
    /// </summary>
    private async Task RecordSyncRunAsync(Instance instance, HeartbeatRequest beat, CancellationToken ct)
    {
        if (beat.SyncRunAt is null || beat.SyncRunAt == instance.LastSyncRunAt) return;

        instance.LastSyncRunAt = beat.SyncRunAt;

        var report = beat.SyncReport ?? new List<SyncItemReport>();
        _db.InstanceSyncRuns.Add(new InstanceSyncRun
        {
            InstanceId = instance.Id,
            RanAt = beat.SyncRunAt.Value,
            Revision = beat.AppliedRevision,
            Error = instance.LastSyncError,
            ReportJson = System.Text.Json.JsonSerializer.Serialize(report),
            Installed = report.Count(x => x.Outcome == "installed"),
            Updated = report.Count(x => x.Outcome == "updated"),
            Skipped = report.Count(x => x.Outcome.StartsWith("skipped", StringComparison.Ordinal)),
            Failed = report.Count(x => x.Outcome == "failed")
        });

        // Prune here rather than in a background job: this table only ever grows on a heartbeat, so
        // this is the one place that knows it needs trimming.
        // KeepPerInstance - 1, because the row added just above is not saved yet and therefore not
        // in this query: skipping the full count would leave one more than the limit.
        var stale = await _db.InstanceSyncRuns
            .Where(r => r.InstanceId == instance.Id)
            .OrderByDescending(r => r.RanAt)
            .Skip(InstanceSyncRun.KeepPerInstance - 1)
            .ToListAsync(ct);
        if (stale.Count > 0) _db.InstanceSyncRuns.RemoveRange(stale);
    }

    /// <summary>
    /// Decides local vs. remote by looking the reported container up on OUR daemon. Re-run on every
    /// heartbeat on purpose: a site that moves to another host must fall back to remote instead of
    /// leaving the cloud pointing at a container that is now something else entirely.
    /// </summary>
    public async Task ClassifyAsync(Instance instance, CancellationToken ct = default)
    {
        var before = instance.Hosting;

        var container = await _docker.FindContainerAsync(instance.ContainerId, ct);
        if (container is null)
        {
            instance.Hosting = InstanceHosting.Remote;
            instance.LocalContainerName = null;
            instance.LocalPort = null;
        }
        else
        {
            instance.Hosting = InstanceHosting.Local;
            instance.LocalContainerName = container.Name;
            instance.LocalPort = container.PublishedPort;
        }

        if (before != InstanceHosting.Unknown && before != instance.Hosting)
            Log(instance, InstanceEventKind.HostingChanged,
                $"Hosting-Erkennung geändert: {Describe(before)} → {Describe(instance.Hosting)}.");
    }

    public static string Describe(InstanceHosting hosting) => hosting switch
    {
        InstanceHosting.Local => "lokal",
        InstanceHosting.Remote => "remote",
        _ => "unbekannt"
    };

    // --- Events -------------------------------------------------------------

    /// <summary>Adds an event to the change tracker (the caller saves). <paramref name="notified"/>
    /// pre-marks events that must never produce a mail.</summary>
    public void Log(Instance instance, InstanceEventKind kind, string message, bool notified = false)
    {
        _db.InstanceEvents.Add(new InstanceEvent
        {
            InstanceId = instance.Id,
            Instance = instance,
            Kind = kind,
            Message = message,
            Notified = notified
        });
    }

    // --- Token helpers ------------------------------------------------------

    /// <summary>URL-safe random id (no ambiguity, not enumerable).</summary>
    private static string NewPublicId() => Base64Url(RandomNumberGenerator.GetBytes(12));

    private static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<PendingRestore?> PendingRestoreAsync(int instanceId, CancellationToken ct)
    {
        var row = await _db.CloudBackups.AsNoTracking()
            .Where(b => b.InstanceId == instanceId && b.RestoreRequestedAt != null
                        && b.RestoreDoneAt == null && b.RestoreError == null)
            .OrderBy(b => b.RestoreRequestedAt)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        return new PendingRestore
        {
            BackupId = row.Id,
            FileName = row.FileName,
            SizeBytes = row.SizeBytes,
            Sha256 = row.Sha256,
        };
    }
    /// <summary>Turns http into https when the operator has said their instances are reachable that
    /// way. Only the scheme — host, port and path are the instance's to report.</summary>
    private string ForceHttps(string url) =>
        _cloud.Flag(SettingKeys.ForceHttpsUrls) && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url.Substring("http://".Length)
            : url;
}
