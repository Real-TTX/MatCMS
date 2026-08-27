using System.Security.Cryptography;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Where uploaded backups live, and what may go in.
/// <para>Files on disk, metadata in the database. That split is deliberate — see
/// <see cref="CloudBackup"/> — and it is also why this class owns BOTH: everything that creates or
/// removes a backup goes through here, so the two cannot drift apart except by somebody reaching
/// into the volume, which is exactly what the orphan list is for.</para>
/// </summary>
public class BackupStore
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<BackupStore> _log;

    public BackupStore(AppDbContext db, IWebHostEnvironment env, ILogger<BackupStore> log)
    {
        _db = db; _env = env; _log = log;
    }

    /// <summary>Used when nothing is configured. Over the quota the OLDEST backups are dropped until
    /// the new one fits — a refused upload would be a silent hole in the chain, and a hole is worse
    /// than a shorter history.</summary>
    public const int DefaultQuotaGb = 2;

    /// <summary>
    /// The cloud-wide default quota in GB — what an instance gets when its profile does not say
    /// otherwise.
    /// <para>Read per call rather than cached: it is asked for once per upload and once per page view,
    /// and a stale value would go on deleting to a limit the operator has already changed.</para>
    /// </summary>
    public async Task<double> DefaultQuotaGbAsync(CancellationToken ct = default)
    {
        var row = await _db.CloudSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.BackupQuotaGb, ct);
        return ParseGb(row?.Value) is double v && v > 0 ? v : DefaultQuotaGb;
    }

    /// <summary>Parses a GB value that a human typed — accepting a comma OR a dot as the decimal mark,
    /// so "0,1" and "0.1" (both = 100 MB) mean the same thing. Null when it is not a number.</summary>
    public static double? ParseGb(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// What ONE instance is granted, in bytes: its profile's quota if that profile sets one,
    /// otherwise the cloud-wide default.
    /// <para>Per instance rather than one number for everybody, because that is how the disk is
    /// actually handed out — a customer with a media-heavy site needs more room than a brochure page,
    /// and raising the limit for everyone to suit one of them is how a control plane runs out of
    /// disk.</para>
    /// <para>An instance with no profile falls back to the default. That is a real state (an
    /// instance can be pending, or its profile deleted) and it must not mean "no quota", which would
    /// delete every backup it owns.</para>
    /// </summary>
    public async Task<long> QuotaBytesAsync(int instanceId, CancellationToken ct = default)
    {
        var profileQuota = await _db.Instances.AsNoTracking()
            .Where(i => i.Id == instanceId)
            .Select(i => i.Profile != null ? i.Profile.BackupQuotaGb : null)
            .FirstOrDefaultAsync(ct);

        var gb = profileQuota is double q && q > 0 ? q : await DefaultQuotaGbAsync(ct);
        return (long)(gb * 1024 * 1024 * 1024);
    }

    /// <summary>A hard ceiling per file, so a broken instance cannot fill the disk with one request.
    /// Well above a normal site with media.</summary>
    public const long MaxUploadBytes = 1L * 1024 * 1024 * 1024;          // 1 GB

    /// <summary>Never fewer than this, whatever the quota says: a single backup that exceeds the
    /// quota on its own must not delete the only other copy that exists.</summary>
    public const int KeepAtLeast = 1;

    public string RootDir => Path.Combine(_env.ContentRootPath, "appdata", "backups");

    /// <summary>Where archived backups live — a SIBLING of <see cref="RootDir"/>, not a folder
    /// inside it, so the quota pruner and the orphan finder cannot reach them by walking the live
    /// tree. Neither would understand a file that belongs to no instance.</summary>
    public string ArchiveRootDir => Path.Combine(_env.ContentRootPath, "appdata", "backups-archive");

    private string DirFor(int instanceId) => Path.Combine(RootDir, instanceId.ToString());

    public string PathFor(CloudBackup b) => Path.Combine(DirFor(b.InstanceId), b.FileName);

    /// <summary>Keyed by the archive row's own id, not by the instance's: instance ids are handed out
    /// again, and a later instance must not inherit a folder holding a removed one's data.</summary>
    public string PathFor(ArchivedBackup b) => Path.Combine(ArchiveRootDir, b.Id.ToString(), b.FileName);

    public sealed record Result(bool Ok, string? Error, CloudBackup? Backup);

    /// <summary>
    /// Streams an upload to disk and records it.
    /// <para>Streamed, never buffered: a backup with media runs to hundreds of megabytes, and holding
    /// one in memory per upload is how a control plane falls over when two sites back up at once.</para>
    /// </summary>
    public async Task<Result> StoreAsync(
        Instance instance, string fileName, string origin, DateTime createdAt,
        Stream content, CancellationToken ct = default, int requestId = 0)
    {
        // The name comes from the instance, so it decides nothing about where the file lands.
        var safe = Path.GetFileName(fileName ?? "");
        if (string.IsNullOrWhiteSpace(safe) || !safe.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return new Result(false, "Ungültiger Dateiname.", null);

        var dir = DirFor(instance.Id);
        Directory.CreateDirectory(dir);

        // Written under a temporary name first: a half-written file that shares the final name would
        // look like a finished backup to everything that lists the directory.
        var target = Path.Combine(dir, safe);
        var temp = target + ".part";
        long written = 0;
        string hash;

        try
        {
            await using (var file = File.Create(temp))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    written += read;
                    if (written > MaxUploadBytes)
                    {
                        file.Close();
                        TryDelete(temp);
                        return new Result(false, $"Backup ist größer als das erlaubte Maximum ({MaxUploadBytes / (1024 * 1024)} MB).", null);
                    }
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                sha.TransformFinalBlock([], 0, 0);
                hash = Convert.ToHexString(sha.Hash ?? []).ToLowerInvariant();
            }

            if (written == 0)
            {
                TryDelete(temp);
                return new Result(false, "Leere Datei.", null);
            }

            // Same name twice: the newer one wins, because a site that re-ran a backup means the
            // second attempt. The row is replaced too, so name stays unique per instance.
            var existing = await _db.CloudBackups
                .FirstOrDefaultAsync(b => b.InstanceId == instance.Id && b.FileName == safe, ct);
            if (existing is not null) _db.CloudBackups.Remove(existing);

            File.Move(temp, target, overwrite: true);

            var row = new CloudBackup
            {
                InstanceId = instance.Id,
                FileName = safe,
                SizeBytes = written,
                CreatedAt = createdAt == default ? DateTime.UtcNow : createdAt,
                Origin = string.IsNullOrWhiteSpace(origin) ? "auto" : origin.Trim(),
                Sha256 = hash,
                RequestId = requestId,
            };
            _db.CloudBackups.Add(row);
            await _db.SaveChangesAsync(ct);

            await EnforceRetentionAsync(instance.Id, ct);
            return new Result(true, null, row);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            _log.LogError(ex, "Storing a backup for instance {Instance} failed", instance.Id);
            return new Result(false, ex.Message, null);
        }
    }

    /// <summary>Resolved retention numbers for one instance: profile value if set, else the
    /// cloud-wide default, else 0 (that tier off). 0 everywhere = retention disabled, quota only.</summary>
    public sealed record Retention(int KeepDaily, int KeepWeekly, int KeepMonthly, int MaxCount)
    {
        public bool Any => KeepDaily > 0 || KeepWeekly > 0 || KeepMonthly > 0 || MaxCount > 0;
    }

    public async Task<Retention> ResolveRetentionAsync(int instanceId, CancellationToken ct = default)
    {
        var prof = await _db.Instances.AsNoTracking()
            .Where(i => i.Id == instanceId).Select(i => i.Profile).FirstOrDefaultAsync(ct);
        var settings = await _db.CloudSettings.AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        int Resolve(int? profileVal, string key)
        {
            if (profileVal is int p && p >= 0) return p;                 // profile wins (0 = off on purpose)
            return settings.TryGetValue(key, out var v) && int.TryParse(v, out var n) && n >= 0 ? n : 0;
        }
        return new Retention(
            Resolve(prof?.BackupKeepDaily, SettingKeys.BackupKeepDaily),
            Resolve(prof?.BackupKeepWeekly, SettingKeys.BackupKeepWeekly),
            Resolve(prof?.BackupKeepMonthly, SettingKeys.BackupKeepMonthly),
            Resolve(prof?.BackupMaxCount, SettingKeys.BackupMaxCount));
    }

    private static bool IsAuto(CloudBackup b) =>
        string.Equals(b.Origin, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Prunes an instance's backups to policy. Two layers, and both leave MANUAL/API uploads alone —
    /// only the site's own scheduled ("auto") backups are ever auto-deleted:
    /// <list type="number">
    /// <item><b>GFS + max-count</b> (when configured): keep the newest auto backup per day for
    /// KeepDaily days, per ISO week for KeepWeekly weeks, per month for KeepMonthly months, and never
    /// more than MaxCount auto backups in total. The newest auto backup is always kept.</item>
    /// <item><b>Disk quota</b>: if the total is still over the (fractional) GB quota, drop the oldest
    /// remaining AUTO backups until it fits — never the very last backup overall.</item>
    /// </list>
    /// Retention entirely unset (all zero) leaves layer 1 off, so this behaves exactly like the old
    /// quota-only pruner. Called after every upload and by the monitor's periodic sweep.
    /// </summary>
    public async Task EnforceRetentionAsync(int instanceId, CancellationToken ct = default)
    {
        var rows = await _db.CloudBackups
            .Where(b => b.InstanceId == instanceId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
        if (rows.Count == 0) return;

        var auto = rows.Where(IsAuto).ToList();   // newest-first; the only ones we may auto-delete
        var doomed = new List<CloudBackup>();

        // --- Layer 1: GFS + max-count over the auto backups ---
        var policy = await ResolveRetentionAsync(instanceId, ct);
        if (policy.Any && auto.Count > 0)
        {
            var keep = new HashSet<int> { auto[0].Id };   // always keep the newest auto backup
            void Tier(int limit, Func<DateTime, string> period)
            {
                if (limit <= 0) return;
                var seen = new HashSet<string>();
                foreach (var b in auto)
                {
                    var p = period(b.CreatedAt);
                    if (seen.Contains(p)) continue;   // an older backup in a period already kept
                    if (seen.Count >= limit) break;   // this tier's periods are full
                    seen.Add(p);
                    keep.Add(b.Id);
                }
            }
            Tier(policy.KeepDaily, d => d.ToString("yyyy-MM-dd"));
            Tier(policy.KeepWeekly, d => $"{System.Globalization.ISOWeek.GetYear(d)}-W{System.Globalization.ISOWeek.GetWeekOfYear(d):00}");
            Tier(policy.KeepMonthly, d => d.ToString("yyyy-MM"));
            if (policy.MaxCount > 0) foreach (var b in auto.Take(policy.MaxCount)) keep.Add(b.Id);

            doomed.AddRange(auto.Where(b => !keep.Contains(b.Id)));
        }

        // --- Layer 2: disk quota (drops oldest surviving AUTO backups first) ---
        var quota = await QuotaBytesAsync(instanceId, ct);
        long total = rows.Where(b => !doomed.Contains(b)).Sum(b => b.SizeBytes);
        foreach (var b in rows.Where(b => IsAuto(b) && !doomed.Contains(b)).OrderBy(b => b.CreatedAt))
        {
            if (total <= quota) break;
            if (rows.Count - doomed.Count <= KeepAtLeast) break;   // never remove the last backup
            doomed.Add(b);
            total -= b.SizeBytes;
        }

        if (doomed.Count == 0) return;
        foreach (var b in doomed.DistinctBy(b => b.Id))
        {
            TryDelete(PathFor(b));
            _db.CloudBackups.Remove(b);
        }
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Retention: removed {Count} auto backup(s) for instance {Instance}", doomed.Count, instanceId);
    }

    /// <summary>
    /// Asks the instance to restore this backup. The cloud only marks it — the site picks it up on
    /// its next heartbeat and does the work itself.
    /// <para>A previous outcome is cleared, so the request and its result always describe the same
    /// attempt rather than a new request wearing an old answer.</para>
    /// <para>Here rather than on a page because two pages offer it now (the backup list and the
    /// instance's own tab), and "what marking a restore means" must not become two answers.</para>
    /// </summary>
    public async Task RequestRestoreAsync(CloudBackup b, CancellationToken ct = default)
    {
        b.RestoreRequestedAt = DateTime.UtcNow;
        b.RestoreDoneAt = null;
        b.RestoreError = null;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Lifts one backup out of the instance's storage and into the archive, so it survives the
    /// removal of the instance it belongs to.
    /// <para>Row first, file second, and the row is only kept if the bytes actually moved. The other
    /// order would leave a "safe" archive entry pointing at a file the cascade then deleted — which
    /// is the same loss as not archiving at all, but reported as success.</para>
    /// </summary>
    public async Task<ArchivedBackup?> ArchiveAsync(CloudBackup b, Instance instance, string reason, CancellationToken ct = default)
    {
        var source = PathFor(b);
        if (!File.Exists(source))
        {
            _log.LogError("Cannot archive backup {File} of instance {Instance}: the file is gone", b.FileName, instance.Id);
            return null;
        }

        var row = new ArchivedBackup
        {
            InstanceName = instance.Name,
            InstancePublicId = instance.PublicId,
            FileName = b.FileName,
            SizeBytes = b.SizeBytes,
            CreatedAt = b.CreatedAt,
            UploadedAt = b.UploadedAt,
            Sha256 = b.Sha256,
            Reason = reason,
        };
        _db.ArchivedBackups.Add(row);
        await _db.SaveChangesAsync(ct);           // the id is what names the folder

        try
        {
            var target = PathFor(row);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target, overwrite: true);
        }
        catch (Exception ex)
        {
            // Moved nothing, so the row is a lie and goes again. The caller is expected to treat a
            // null answer as "do not proceed" — the backup is what the removal was waiting for.
            _log.LogError(ex, "Archiving backup {File} of instance {Instance} failed", b.FileName, instance.Id);
            _db.ArchivedBackups.Remove(row);
            await _db.SaveChangesAsync(ct);
            return null;
        }

        // The live row goes; its bytes are somewhere else now. Deleting it through DeleteAsync would
        // try to delete the file we just moved and log a warning about it.
        _db.CloudBackups.Remove(b);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Backup {File} of instance {Instance} archived as #{Id}", row.FileName, instance.Name, row.Id);
        return row;
    }

    /// <summary>Removes an archived backup, file and row together. The only thing that ever deletes
    /// one — nothing here prunes the archive on its own.</summary>
    public async Task DeleteArchivedAsync(ArchivedBackup b, CancellationToken ct = default)
    {
        TryDelete(PathFor(b));
        try
        {
            var dir = Path.GetDirectoryName(PathFor(b));
            if (dir is not null && Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not remove the archive folder"); }

        _db.ArchivedBackups.Remove(b);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Takes back a request the instance has not picked up yet. Nothing to undo once it has.</summary>
    public async Task CancelRestoreAsync(CloudBackup b, CancellationToken ct = default)
    {
        b.RestoreRequestedAt = null;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Removes one backup, file and row together.</summary>
    public async Task DeleteAsync(CloudBackup b, CancellationToken ct = default)
    {
        TryDelete(PathFor(b));
        _db.CloudBackups.Remove(b);
        await _db.SaveChangesAsync(ct);
    }

    // --- Orphans ------------------------------------------------------------------------------

    /// <param name="File">Absolute path of a file with no row, or null for a row with no file.</param>
    /// <param name="Row">The row with no file, or null for a file with no row.</param>
    public sealed record Orphan(string Kind, string Name, long SizeBytes, string? File, CloudBackup? Row);

    /// <summary>
    /// What does not add up between disk and database.
    /// <para>Two kinds, and both happen for real: a FILE with no row is what an instance deletion
    /// leaves behind (the rows cascade, the bytes do not), and a ROW with no file is what somebody
    /// clearing out the volume by hand leaves behind. Neither is dangerous, both are confusing —
    /// a backup list that quietly counts megabytes nobody can restore is worse than one that says so.</para>
    /// </summary>
    public async Task<List<Orphan>> FindOrphansAsync(CancellationToken ct = default)
    {
        var found = new List<Orphan>();
        var rows = await _db.CloudBackups.AsNoTracking().ToListAsync(ct);

        foreach (var b in rows)
        {
            if (!File.Exists(PathFor(b)))
                found.Add(new Orphan("row", $"{b.FileName}", b.SizeBytes, null, b));
        }

        if (!Directory.Exists(RootDir)) return found;

        var known = rows.Select(b => PathFor(b)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.GetDirectories(RootDir))
        {
            foreach (var f in Directory.GetFiles(dir, "*.zip"))
            {
                if (known.Contains(f)) continue;
                var info = new FileInfo(f);
                found.Add(new Orphan("file", Path.GetFileName(f) + "  (" + Path.GetFileName(dir) + ")", info.Length, f, null));
            }
            // Half-written uploads count too: a .part left behind means an upload died mid-flight.
            foreach (var f in Directory.GetFiles(dir, "*.part"))
            {
                var info = new FileInfo(f);
                found.Add(new Orphan("file", Path.GetFileName(f) + "  (" + Path.GetFileName(dir) + ")", info.Length, f, null));
            }
        }
        return found;
    }

    /// <summary>Clears out everything the orphan list found. Files are deleted, dangling rows removed —
    /// neither refers to anything that can still be restored.</summary>
    public async Task<int> CleanOrphansAsync(CancellationToken ct = default)
    {
        var orphans = await FindOrphansAsync(ct);
        foreach (var o in orphans)
        {
            if (o.File is not null) TryDelete(o.File);
            if (o.Row is not null)
            {
                var row = await _db.CloudBackups.FirstOrDefaultAsync(b => b.Id == o.Row.Id, ct);
                if (row is not null) _db.CloudBackups.Remove(row);
            }
        }
        await _db.SaveChangesAsync(ct);
        return orphans.Count;
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not delete {Path}", path); }
    }
}
