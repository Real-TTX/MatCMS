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
    public async Task<int> DefaultQuotaGbAsync(CancellationToken ct = default)
    {
        var row = await _db.CloudSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.BackupQuotaGb, ct);
        return int.TryParse(row?.Value, out var v) && v > 0 ? v : DefaultQuotaGb;
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

        var gb = profileQuota is int q && q > 0 ? q : await DefaultQuotaGbAsync(ct);
        return (long)gb * 1024 * 1024 * 1024;
    }

    /// <summary>A hard ceiling per file, so a broken instance cannot fill the disk with one request.
    /// Well above a normal site with media.</summary>
    public const long MaxUploadBytes = 1L * 1024 * 1024 * 1024;          // 1 GB

    /// <summary>Never fewer than this, whatever the quota says: a single backup that exceeds the
    /// quota on its own must not delete the only other copy that exists.</summary>
    public const int KeepAtLeast = 1;

    public string RootDir => Path.Combine(_env.ContentRootPath, "appdata", "backups");

    private string DirFor(int instanceId) => Path.Combine(RootDir, instanceId.ToString());

    public string PathFor(CloudBackup b) => Path.Combine(DirFor(b.InstanceId), b.FileName);

    public sealed record Result(bool Ok, string? Error, CloudBackup? Backup);

    /// <summary>
    /// Streams an upload to disk and records it.
    /// <para>Streamed, never buffered: a backup with media runs to hundreds of megabytes, and holding
    /// one in memory per upload is how a control plane falls over when two sites back up at once.</para>
    /// </summary>
    public async Task<Result> StoreAsync(
        Instance instance, string fileName, string origin, DateTime createdAt,
        Stream content, CancellationToken ct = default)
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
            };
            _db.CloudBackups.Add(row);
            await _db.SaveChangesAsync(ct);

            await EnforceQuotaAsync(instance.Id, ct);
            return new Result(true, null, row);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            _log.LogError(ex, "Storing a backup for instance {Instance} failed", instance.Id);
            return new Result(false, ex.Message, null);
        }
    }

    /// <summary>Drops the oldest backups until the instance is inside its quota. Never below
    /// <see cref="KeepAtLeast"/>: one oversized backup must not take the last other copy with it.</summary>
    public async Task EnforceQuotaAsync(int instanceId, CancellationToken ct = default)
    {
        var quota = await QuotaBytesAsync(instanceId, ct);
        var rows = await _db.CloudBackups
            .Where(b => b.InstanceId == instanceId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);

        long total = 0;
        var doomed = new List<CloudBackup>();
        for (var i = 0; i < rows.Count; i++)
        {
            total += rows[i].SizeBytes;
            if (total > quota && i >= KeepAtLeast) doomed.Add(rows[i]);
        }
        if (doomed.Count == 0) return;

        foreach (var b in doomed)
        {
            TryDelete(PathFor(b));
            _db.CloudBackups.Remove(b);
        }
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Backup quota: dropped {Count} old backup(s) for instance {Instance}", doomed.Count, instanceId);
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
