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

    /// <summary>The site name a fresh MatCMS reports before anybody configured one (SiteContext's
    /// default). It is NOT a name: taken as one, every new instance stayed "MatCMS" in the cloud for good —
    /// the site's real name arrives later (set by hand, or with the restore that brings the content), and
    /// by then the label no longer counted as unset. Treated like the placeholder on both ends.</summary>
    public const string FreshSiteName = "MatCMS";

    /// <summary>Whether a label/site name means "not named yet".</summary>
    public static bool IsUnnamed(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim() == PlaceholderName || name.Trim() == FreshSiteName;

    private readonly AppDbContext _db;
    private readonly DockerHostService _docker;
    private readonly ReleaseWatcher _releases;
    private readonly ProfileService _profiles;
    private readonly CloudContext _cloud;
    private readonly BackupStore _backups;

    public InstanceService(AppDbContext db, DockerHostService docker, ReleaseWatcher releases, ProfileService profiles, CloudContext cloud, BackupStore backups)
    {
        _cloud = cloud;
        _db = db;
        _docker = docker;
        _releases = releases;
        _profiles = profiles;
        _backups = backups;
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
            Name = IsUnnamed(request.SiteName) ? PlaceholderName : request.SiteName!.Trim(),
            Url = SafeUrl(request.Url),
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
        // Not when the operator pinned the URL (they typed the domain when adopting) — their input wins,
        // otherwise the site reporting a different (e.g. internal) address would wipe the entered domain.
        if (!instance.UrlPinned && SafeUrl(beat.Url) is string beatUrl) instance.Url = ForceHttps(beatUrl);
        // The reported site name only SEEDS the label — never overwrite a name an operator set here.
        // "Set" is anything other than the placeholder; the placeholder counts as "not set yet", so an
        // instance that enrolled before its site name was configured still picks it up instead of
        // staying "Neue Instanz" forever.
        //
        // Deliberately NOT gated on firstEver any more. An ADOPTED instance is given its name by the
        // operator BEFORE it ever beats, and "firstEver ||" overwrote exactly that name with the fresh
        // site's default ("MatCMS") on the very first heartbeat — and again whenever a restart or a
        // restore made the instance look first-ever. The label an operator typed must survive both.
        if (!instance.NamePinned && IsUnnamed(instance.Name) && !IsUnnamed(beat.SiteName))
            instance.Name = beat.SiteName!.Trim();

        await RecordSyncReportAsync(instance, beat, ct);
        await RecordContentOpReportsAsync(instance, beat, ct);
        await RecordInstanceLogsAsync(instance, beat, ct);
        await RecordStatsAsync(instance, beat, ct);
        await ClassifyAsync(instance, ct);

        if (firstEver)
            Log(instance, InstanceEventKind.Connected, $"Instanz verbunden ({instance.Version ?? "?"}).");
        else if (wasOffline)
            Log(instance, InstanceEventKind.Recovered, "Instanz meldet sich wieder.");

        // A new beat ends the outage, so the dead-man switch may fire again next time.
        instance.OfflineNotified = false;

        await _db.SaveChangesAsync(ct);

        // Wird die Anforderung noch gestellt? Nur gefragt, wenn überhaupt eine offen ist — der
        // normale Herzschlag kostet dadurch keine zusätzliche Abfrage.
        //
        // Das Verstummen ist der Punkt: Ohne es steht die Anforderung weiter in JEDER Antwort, und
        // die Instanz baut im Minutentakt ein neues Backup. Genau das ist beim Prüfen passiert —
        // fünfzehn Backups in fünfzehn Minuten für eine einzige Anforderung. Gefragt wird dieselbe
        // Stelle, die auch das Entfernen freigibt, damit es nicht zwei Meinungen darüber gibt, ob
        // ein Backup da ist: verschwindet die Datei wieder, wird auch wieder gefragt.
        var wantsBackup = instance.Status == InstanceStatus.Approved
                          && instance.BackupRequestId > 0
                          && instance.BackupRequestError is null
                          && await ArrivedBackupAsync(instance, ct) is null;

        return new HeartbeatResponse
        {
            ProtocolVersion = CurrentProtocolVersion,
            Status = instance.Status.ToString(),
            // What the operator types into a browser — the instance cannot know it, and needs it to
            // allow the embedding. Empty means "same as the address you already use".
            CloudPublicUrl = _cloud.Get(SettingKeys.CanonicalUrl),
            LatestVersion = _releases.LatestVersion,
            UpdateAvailable = IsUpdateAvailable(instance),
            CloudCanUpdate = instance.Hosting is InstanceHosting.Local or InstanceHosting.Node,
            DisplayName = instance.Name,
            ProfileName = instance.Profile?.Name,
            // A pending instance is told 0 so it never even asks for configuration.
            ConfigRevision = instance.Status == InstanceStatus.Approved ? instance.Profile?.Revision ?? 0 : 0,
            ResyncRequested = instance.Status == InstanceStatus.Approved && instance.ProfileId != null && instance.ResyncRequestedAt != null,

            // A backup somebody asked to be restored. Only for an approved instance, and only the
            // OLDEST outstanding one — asking a site to overwrite itself twice in a row is never
            // what was meant, and the second request is still there on the next beat.
            Restore = instance.Status == InstanceStatus.Approved ? await PendingRestoreAsync(instance.Id, ct) : null,

            // A backup the cloud asked for. Same gate as the restore — an instance that is not
            // approved is not asked to do work for us — and repeated on every beat until it is
            // answered, which is how a site that was down when we asked still hears about it.
            // An answered-with-failure request is NOT repeated: see Instance.BackupRequestError.
            Backup = wantsBackup
                ? new PendingBackup
                {
                    RequestId = instance.BackupRequestId,
                    Reason = instance.RemovalPending
                        ? "Die Cloud sichert diese Website, bevor sie entfernt wird."
                        : "Die Cloud hat ein Backup angefordert."
                }
                : null,

            // Content operations (AI changes) to apply. Only for an approved instance, and only one that
            // speaks the contract that introduced them (v15) — an older instance ignores the field and
            // would never report back, so an op offered to it would stand for ever. Offered on every beat
            // until reported; add-only ops are idempotent, so a re-offer after a lost report is harmless.
            ContentOps = instance.Status == InstanceStatus.Approved && beat.ProtocolVersion >= 15
                ? await PendingContentOpsAsync(instance.Id, instance.Profile?.BackupBeforeAiChange ?? false, ct)
                : null,

            // Full-log request (Variante B). Only for an approved instance speaking the contract that
            // introduced it (v16); offered on every beat until the upload arrives and clears the id.
            LogFetch = instance.Status == InstanceStatus.Approved && beat.ProtocolVersion >= 16 && instance.LogFetchRequestId > 0
                ? new PendingLogFetch { RequestId = instance.LogFetchRequestId }
                : null,

            // Visitor statistics: this cloud takes them, and this is where the site should resume.
            StatsAccepted = true,
            StatsHaveUntil = instance.StatsHaveUntil
        };
    }

    /// <summary>A heartbeat carries at most this many days (the site sends 14); more is refused as a whole.</summary>
    private const int MaxStatDaysPerBeat = 31;
    public const int StatsKeepDays = 400;

    /// <summary>Stores the statistics days the site sent (<see cref="HeartbeatRequest.Stats"/>): each day REPLACES the
    /// stored one — the site sends a day as it has it now, so a repeat or a lost response is harmless — and
    /// <see cref="Instance.StatsHaveUntil"/> moves to the newest day received. Kinds and keys are bounded here too;
    /// the site is trusted to count, not to decide how big the cloud's table gets.</summary>
    private async Task RecordStatsAsync(Instance instance, HeartbeatRequest beat, CancellationToken ct)
    {
        if (beat.Stats is not { Count: > 0 } days || days.Count > MaxStatDaysPerBeat) return;
        var valid = days.Where(d => DateOnly.TryParseExact(d.Day, "yyyy-MM-dd", out var day)
                                    && day <= DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)).ToList();
        if (valid.Count == 0) return;
        var names = valid.Select(d => d.Day).Distinct().ToList();
        await _db.InstanceStats.Where(x => x.InstanceId == instance.Id && names.Contains(x.Day)).ExecuteDeleteAsync(ct);
        foreach (var d in valid.GroupBy(d => d.Day).Select(g => g.Last()))
        {
            var counts = (d.Counts ?? new())
                .Where(c => c.Count > 0 && !string.IsNullOrEmpty(c.Kind) && c.Kind.Length <= 4)
                .GroupBy(c => (c.Kind, Key: Cap(c.Key, 200) ?? ""))
                .Select(g => new { g.Key.Kind, g.Key.Key, Count = g.Sum(c => c.Count) })
                .GroupBy(c => c.Kind)
                .SelectMany(g => g.OrderByDescending(c => c.Count).Take(StatKinds.WireTopPerKind * 2));
            foreach (var c in counts)
                _db.InstanceStats.Add(new InstanceStat { InstanceId = instance.Id, Day = d.Day, Kind = c.Kind, Key = c.Key, Count = c.Count });
        }
        // The cloud keeps as long as a site does by default; a site's own shorter setting governs only the site.
        var cut = StatKinds.DayOf(DateTime.UtcNow.AddDays(-StatsKeepDays));
        await _db.InstanceStats.Where(x => x.InstanceId == instance.Id && string.Compare(x.Day, cut) < 0).ExecuteDeleteAsync(ct);
        var newest = names.Max(StringComparer.Ordinal)!;
        if (instance.StatsHaveUntil is null || string.CompareOrdinal(newest, instance.StatsHaveUntil) > 0)
            instance.StatsHaveUntil = newest;
    }

    /// <summary>Marks a full-log request for an instance (Variante B). Bumped, not reused, so a stale
    /// upload cannot answer it. The instance uploads on its next beat and the file clears the request.</summary>
    public async Task RequestFullLogAsync(Instance instance, CancellationToken ct = default)
    {
        instance.LogFetchRequestId += 1;
        instance.LogFetchRequestedAt = DateTime.UtcNow;
        Log(instance, InstanceEventKind.LogRequested, "Volles Protokoll angefordert.");
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Stores an instance's uploaded FULL log (Variante B): replaces its mirror with the coherent
    /// snapshot (with stack traces), capped, and clears the request if this upload answers it. A stale
    /// upload (wrong/zero request id) is ignored so an offline site returning later cannot overwrite a
    /// fresh mirror with an answer to a request nobody is waiting for.</summary>
    public async Task<bool> StoreFullLogAsync(Instance instance, LogUpload upload, CancellationToken ct = default)
    {
        if (upload.RequestId <= 0 || upload.RequestId != instance.LogFetchRequestId) return false;

        await _db.InstanceLogs.Where(l => l.InstanceId == instance.Id).ExecuteDeleteAsync(ct);
        var entries = (upload.Entries ?? new()).OrderByDescending(e => e.SourceId).Take(1000);
        foreach (var r in entries)
            _db.InstanceLogs.Add(new InstanceLogEntry
            {
                InstanceId = instance.Id,
                SourceId = r.SourceId,
                TimeUtc = r.TimeUtc,
                Level = Trim(r.Level) ?? "Error",
                Message = Cap(r.Message, 1000) ?? "",
                Category = Cap(r.Category, 100),
                Path = Cap(r.Path, 500),
                Method = Cap(r.Method, 10),
                StatusCode = r.StatusCode,
                Exception = Cap(r.Exception, 8000)
            });
        instance.LogFetchRequestId = 0;
        instance.LogFetchRequestedAt = null;
        Log(instance, InstanceEventKind.LogReceived, "Volles Protokoll empfangen.");
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Enqueues a content operation for an instance (the MCP server's write path). The cloud only
    /// records the intent; the instance applies it on its next beat through its own validated writers and
    /// reports back. The returned row's <see cref="ContentOp.Id"/> is the op id the caller can poll on.</summary>
    public async Task<ContentOp> EnqueueContentOpAsync(Instance instance, string kind, string payloadJson,
        bool overwrite, string? reason, CancellationToken ct = default)
    {
        var op = new ContentOp
        {
            InstanceId = instance.Id,
            Kind = kind,
            PayloadJson = payloadJson,
            Overwrite = overwrite,
            Reason = reason
        };
        _db.ContentOps.Add(op);
        Log(instance, InstanceEventKind.ContentOpQueued, $"KI-Änderung eingereiht ({kind}).");
        await _db.SaveChangesAsync(ct);
        return op;
    }

    /// <summary>The still-pending content ops for an instance, oldest first, as the wire DTO. Null (not an
    /// empty list) when there are none, so the heartbeat response carries nothing.</summary>
    private async Task<List<PendingContentOp>?> PendingContentOpsAsync(int instanceId, bool backupFirst, CancellationToken ct)
    {
        var ops = await _db.ContentOps.AsNoTracking()
            .Where(o => o.InstanceId == instanceId && o.DoneAt == null)
            .OrderBy(o => o.Id)
            .Select(o => new PendingContentOp
            {
                OpId = o.Id,
                Kind = o.Kind,
                PayloadJson = o.PayloadJson,
                Overwrite = o.Overwrite,
                Reason = o.Reason,
                // From the profile's "Backup vor KI-Änderung" switch: the instance backs up before applying.
                // Only for a WRITE op (overwrite, or a create) — a read never changes anything.
                BackupFirst = backupFirst && (o.Overwrite || o.Kind == "page.create" || o.Kind == "post.create"),
            })
            .ToListAsync(ct);
        return ops.Count == 0 ? null : ops;
    }

    /// <summary>Folds the instance's reported content-op outcomes into their rows (so each is marked done and
    /// stops being offered) and logs them, then prunes long-done ops so the table stays bounded — the same
    /// "the report is what the cloud records, it computes nothing" shape as the sync report.</summary>
    private async Task RecordContentOpReportsAsync(Instance instance, HeartbeatRequest beat, CancellationToken ct)
    {
        if (beat.ContentOpReports is { Count: > 0 })
        {
            foreach (var r in beat.ContentOpReports)
            {
                var op = await _db.ContentOps.FirstOrDefaultAsync(o => o.Id == r.OpId && o.InstanceId == instance.Id, ct);
                if (op is null || op.DoneAt is not null) continue;   // unknown, or already recorded on an earlier beat
                op.DoneAt = DateTime.UtcNow;
                op.Outcome = r.Outcome;
                op.Detail = r.Detail;
                op.ResultJson = r.ResultJson;
                var failed = string.Equals(r.Outcome, "failed", StringComparison.Ordinal);
                Log(instance, failed ? InstanceEventKind.ContentOpFailed : InstanceEventKind.ContentOpApplied,
                    failed
                        ? $"KI-Änderung fehlgeschlagen ({op.Kind}): {r.Detail}"
                        : $"KI-Änderung angewendet ({op.Kind}): {r.Outcome}{(string.IsNullOrWhiteSpace(r.Detail) ? "" : " – " + r.Detail)}");
            }
        }

        // Prune this instance's long-done ops (>2 days). A heartbeat is the only moment this table grows for
        // a site, so this is the natural place to trim it.
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(2);
        await _db.ContentOps
            .Where(o => o.InstanceId == instance.Id && o.DoneAt != null && o.DoneAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>How many mirrored log entries to keep per instance. The instance sends only its newest
    /// few each beat, so this is the depth of the cloud's overview — enough to see a run of errors, not
    /// the full log (that would be the on-demand pull, still on the backlog).</summary>
    private const int KeepLogsPerInstance = 300;

    /// <summary>Stores the log entries an instance piggybacked on its beat (see
    /// <see cref="HeartbeatRequest.RecentLogs"/>), deduped on (instance, SourceId) so the same newest
    /// entries riding on successive beats are kept once, and prunes the mirror to the newest
    /// <see cref="KeepLogsPerInstance"/> per instance. The prune runs against already-persisted rows
    /// (raw delete), the new ones are saved with the beat — so the count may briefly exceed the cap by
    /// one beat's worth, which the next beat trims.</summary>
    private async Task RecordInstanceLogsAsync(Instance instance, HeartbeatRequest beat, CancellationToken ct)
    {
        if (beat.RecentLogs is { Count: > 0 })
        {
            var ids = beat.RecentLogs.Select(r => r.SourceId).ToList();
            var have = (await _db.InstanceLogs
                .Where(l => l.InstanceId == instance.Id && ids.Contains(l.SourceId))
                .Select(l => l.SourceId).ToListAsync(ct)).ToHashSet();
            foreach (var r in beat.RecentLogs)
            {
                if (!have.Add(r.SourceId)) continue;   // already stored, or a duplicate within this beat
                _db.InstanceLogs.Add(new InstanceLogEntry
                {
                    InstanceId = instance.Id,
                    SourceId = r.SourceId,
                    TimeUtc = r.TimeUtc,
                    Level = Trim(r.Level) ?? "Error",
                    Message = Cap(r.Message, 1000) ?? "",
                    Category = Cap(r.Category, 100),
                    Path = Cap(r.Path, 500),
                    Method = Cap(r.Method, 10),
                    StatusCode = r.StatusCode
                });
            }
        }

        // Keep only the newest N per instance. Ordered by the cloud-side Id (insertion order), which is
        // monotonic even if the instance's own ids restart after a restore — SourceId is not.
        var cutId = await _db.InstanceLogs
            .Where(l => l.InstanceId == instance.Id)
            .OrderByDescending(l => l.Id)
            .Skip(KeepLogsPerInstance)
            .Select(l => l.Id)
            .FirstOrDefaultAsync(ct);
        if (cutId > 0)
            await _db.InstanceLogs
                .Where(l => l.InstanceId == instance.Id && l.Id <= cutId)
                .ExecuteDeleteAsync(ct);
    }

    private static string? Cap(string? s, int max) => s is null ? null : (s.Length <= max ? s : s[..max]);

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
        // A requested re-sync is done once the instance has applied again.
        instance.ResyncRequestedAt = null;

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
        var onNode = container is null ? await FindOnNodeAsync(instance.ContainerId, ct) : null;

        // Fallback: the reported id matches nothing, but the host name is Docker's SHORT container id. Older
        // instances read the first 64-hex from mountinfo, which under nested Docker (dind, some LXC/VM setups)
        // is an OUTER volume id — the site then looked unmanageable although its container was right there.
        var shortId = instance.HostName?.Trim().ToLowerInvariant();
        if (container is null && onNode is null && shortId is { Length: 12 } && shortId.All(Uri.IsHexDigit)
            && !string.Equals(shortId, instance.ContainerId, StringComparison.OrdinalIgnoreCase))
        {
            container = await _docker.FindContainerAsync(shortId, ct);
            onNode = container is null ? await FindOnNodeAsync(shortId, ct) : null;
        }

        // What the cloud acts on from here is the id the DAEMON confirmed — not a guess the instance made.
        if (container is not null) instance.ContainerId = container.Id;
        else if (onNode is { } confirmed) instance.ContainerId = confirmed.Container.Id;

        if (container is not null)
        {
            instance.Hosting = InstanceHosting.Local;
            instance.NodeId = null;
            instance.LocalContainerName = container.Name;
            instance.LocalPort = container.PublishedPort;
            instance.ContainerState = container.State;
            instance.CloudManaged = container.CloudManaged;

            if (instance.ProxyDomain is null && !string.IsNullOrEmpty(container.Name))
                await AdoptPendingRouteAsync(instance, PendingRouteKey(null, container.Name), ct);
        }
        else if (onNode is { } hit)
        {
            // Found in the inventory a connected node reported: the cloud acts on it through that node's agent.
            instance.Hosting = InstanceHosting.Node;
            instance.NodeId = hit.NodeId;
            instance.LocalContainerName = hit.Container.Name;
            instance.LocalPort = hit.Container.PublishedPort;
            instance.ContainerState = hit.Container.State;
            instance.CloudManaged = hit.Container.CloudManaged;

            if (instance.ProxyDomain is null && !string.IsNullOrEmpty(hit.Container.Name))
                await AdoptPendingRouteAsync(instance, PendingRouteKey(hit.NodeId, hit.Container.Name), ct);
        }
        else
        {
            instance.Hosting = InstanceHosting.Remote;
            instance.NodeId = null;
            instance.LocalContainerName = null;
            instance.LocalPort = null;
            instance.ContainerState = null;
            // No container here means no claim on one. Clearing this is the whole point of re-running
            // the classification: a site that moved away must not keep a licence to be torn down.
            instance.CloudManaged = false;
        }

        if (before != InstanceHosting.Unknown && before != instance.Hosting)
            Log(instance, InstanceEventKind.HostingChanged,
                $"Hosting-Erkennung geändert: {Describe(before)} → {Describe(instance.Hosting)}.");
    }

    /// <summary>
    /// A route created while the site was being provisioned (the container existed, its instance row did
    /// not) is taken over the first time the new instance is seen on its container — see
    /// <c>ProxyService.PublishForNewContainerAsync</c>. Deliberately here and not in ProxyService: nothing
    /// is called on the proxy any more, the route already exists; only the record moves onto the instance.
    /// </summary>
    /// <summary>Where a route created at provisioning waits for its instance. Container names are only unique per
    /// Docker host, so a node's routes carry the node in the key.</summary>
    public static string PendingRouteKey(int? nodeId, string containerName) =>
        SettingKeys.HostingPendingRoutePrefix + (nodeId is null ? "" : $"node{nodeId}/") + containerName;

    /// <summary>
    /// Looks the reported container up in the inventories of the nodes that reported recently. Only nodes seen
    /// within the last minutes count — an inventory from a node that went silent says where a container WAS.
    /// </summary>
    private async Task<(int NodeId, Nodes.NodeContainer Container)?> FindOnNodeAsync(string? containerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(containerId)) return null;
        var id = containerId.Trim().ToLowerInvariant();
        if (id.Length < 12) return null;
        var cut = DateTime.UtcNow.AddMinutes(-5);
        var nodes = await _db.Nodes.AsNoTracking()
            .Where(n => !n.Revoked && n.InventoryAt != null && n.InventoryAt > cut)
            .Select(n => new { n.Id, n.InventoryJson }).ToListAsync(ct);
        foreach (var n in nodes)
        {
            var list = Nodes.NodeJobExecutor.Deserialize<List<Nodes.NodeContainer>>(n.InventoryJson);
            var c = list?.FirstOrDefault(c => DockerHostService.IdMatches(c.Id, id));
            if (c is not null) return (n.Id, c);
        }
        return null;
    }

    private async Task AdoptPendingRouteAsync(Instance instance, string key, CancellationToken ct)
    {
        var row = await _db.CloudSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.Value)) return;

        Proxy.PendingRoute? p;
        try { p = System.Text.Json.JsonSerializer.Deserialize<Proxy.PendingRoute>(row.Value); } catch { p = null; }
        _db.CloudSettings.Remove(row);
        if (p is null) return;

        // The host address (automatic, per host) and the customer domain (at the host or the edge) — either may
        // be missing. The customer domain wins as the site's address.
        if (p.HostDomain is not null)
        {
            instance.HostDomain = p.HostDomain;
            instance.HostProvider = p.HostProvider;
            instance.HostRouteId = p.HostRouteId;
            instance.HostRouteError = null;
            instance.HostPublishedAt = DateTime.UtcNow;
            Log(instance, InstanceEventKind.DomainPublished, $"Bei der Provisionierung angelegte Host-Adresse übernommen: {p.HostDomain}.");
        }
        if (p.Domain is not null)
        {
            instance.ProxyDomain = p.Domain;
            instance.ProxyProvider = p.Provider;
            instance.ProxyRouteId = p.RouteId;
            instance.ProxyVia = p.Via ?? Proxy.ProxyVia.Host;
            instance.ProxyError = null;
            instance.ProxyPublishedAt = DateTime.UtcNow;
            Log(instance, InstanceEventKind.DomainPublished, $"Bei der Provisionierung angelegte Domain übernommen: {p.Domain}.");
        }
        var address = p.Domain ?? p.HostDomain;
        if (address is null) return;
        instance.Url = "https://" + address;
        instance.UrlPinned = true;

        if (p.PushCanonical && instance.Status == InstanceStatus.Approved)
        {
            var url = "https://" + address;
            await EnqueueContentOpAsync(instance, "setting.set",
                System.Text.Json.JsonSerializer.Serialize(new { key = "site.canonicalUrl", value = url }), overwrite: true, reason: "Hosting: öffentliche Adresse", ct);
            await EnqueueContentOpAsync(instance, "setting.set",
                System.Text.Json.JsonSerializer.Serialize(new { key = "site.behindHttpsProxy", value = "1" }), overwrite: true, reason: "Hosting: hinter HTTPS-Proxy", ct);
        }
    }

    public static string Describe(InstanceHosting hosting) => hosting switch
    {
        InstanceHosting.Local => "lokal",
        InstanceHosting.Remote => "remote",
        InstanceHosting.Node => "auf einem Node",
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

    /// <summary>
    /// Asks the instance for a fresh backup. Bumps the id rather than reusing it, so an upload that
    /// answers the previous request can never be read as an answer to this one — and clears the
    /// previous outcome, so request and answer always describe the same attempt.
    /// </summary>
    public async Task RequestBackupAsync(Instance instance, CancellationToken ct = default)
    {
        instance.BackupRequestId += 1;
        instance.BackupRequestedAt = DateTime.UtcNow;
        instance.BackupRequestError = null;
        instance.BackupWaitNotified = false;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The backup that answers the outstanding request, or null while none has arrived.
    ///
    /// <para><b>This is the gate everything else hangs on.</b> It asks the one question that means
    /// the data is safe — is the FILE here — and it asks it of the stored row, not of anything the
    /// instance claimed. "We asked" is not an answer; "the instance said it worked" is not an answer
    /// either, because the upload can still have failed after the report was written. Only a row
    /// carrying this request's id, whose bytes are on our disk, is.</para>
    ///
    /// <para>The file is checked on disk as well as in the table. Row and file are two things that
    /// can drift apart (see <see cref="CloudBackup"/>), and of all the places to trust the table
    /// alone, the one that then deletes a customer's site is the worst.</para>
    /// </summary>
    public async Task<CloudBackup?> ArrivedBackupAsync(Instance instance, CancellationToken ct = default)
    {
        if (instance.BackupRequestId <= 0) return null;

        var row = await _db.CloudBackups
            .Where(b => b.InstanceId == instance.Id && b.RequestId == instance.BackupRequestId)
            .OrderByDescending(b => b.UploadedAt)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        return File.Exists(_backups.PathFor(row)) ? row : null;
    }

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
    /// <summary>An instance-reported URL, accepted only if it is an absolute http/https address —
    /// anything else (e.g. a <c>javascript:</c> scheme) is dropped. The instance is a separate,
    /// possibly hostile principal and the control plane renders this value as an href/src, so an unsafe
    /// scheme must never be stored. Empty/whitespace → null. Defence in depth next to the render-side
    /// guard in <see cref="Models.Instance.PreviewUrl"/>.</summary>
    private static string? SafeUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? url!.Trim() : null;

    /// <summary>Turns http into https when the operator has said their instances are reachable that
    /// way. Only the scheme — host, port and path are the instance's to report.</summary>
    private string ForceHttps(string url) =>
        _cloud.Flag(SettingKeys.ForceHttpsUrls) && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url.Substring("http://".Length)
            : url;
}
