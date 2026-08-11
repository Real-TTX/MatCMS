using System.Net.Http.Headers;
using System.Security.Cryptography;
using MatCMS.Data;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// This site's side of cloud backup storage: hand a finished backup over, and — when the cloud asks —
/// fetch one back and restore it.
/// <para>The restore itself is NOT reimplemented here. The file is downloaded into the normal backups
/// folder and the existing import runs on it, so the one piece of code that can overwrite a site
/// stays the one piece of code that overwrites a site.</para>
/// </summary>
public class CloudBackupService
{
    private readonly AppDbContext _db;
    private readonly CloudService _cloud;
    private readonly BackupManager _backups;
    private readonly ContentTransferService _transfer;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<CloudBackupService> _log;

    public CloudBackupService(
        AppDbContext db, CloudService cloud, BackupManager backups, ContentTransferService transfer,
        IHttpClientFactory http, ILogger<CloudBackupService> log)
    {
        _db = db; _cloud = cloud; _backups = backups; _transfer = transfer; _http = http; _log = log;
    }

    /// <summary>Whether this site sends its backups to the cloud. Off by default: uploading a
    /// customer's whole site somewhere is a decision, not a default.</summary>
    public async Task<bool> IsEnabledAsync()
    {
        var row = await _db.SiteSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.BackupToCloud);
        return row?.Value?.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";
    }

    /// <summary>
    /// Uploads one stored backup, streamed from disk.
    /// <para>Streamed rather than read into memory: the file is already on the disk that made it, and
    /// a site with media runs to hundreds of megabytes — loading it again to send it would double the
    /// worst moment of the day for no reason.</para>
    /// </summary>
    public async Task<(bool ok, string? error)> UploadAsync(
        string fileName, string origin = "auto", CancellationToken ct = default)
    {
        var settings = await _cloud.GetSettingsAsync();
        if (!settings.Configured) return (false, "Diese Website ist mit keiner Cloud verbunden.");

        var path = Path.Combine(_backups.BackupsDir, Path.GetFileName(fileName));
        if (!File.Exists(path)) return (false, "Backup nicht gefunden.");

        try
        {
            var info = new FileInfo(path);
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);   // a big upload over a slow line is normal
            client.DefaultRequestHeaders.Add(CloudProtocol.TokenHeader, settings.Token);

            await using var stream = File.OpenRead(path);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{settings.Url}/api/instances/{settings.InstanceId}/backups") { Content = content };
            req.Headers.Add("X-MatCMS-Backup-Name", info.Name);
            req.Headers.Add("X-MatCMS-Backup-Origin", origin);
            req.Headers.Add("X-MatCMS-Backup-Created", info.LastWriteTimeUtc.ToString("o"));

            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return (false, $"Die Cloud hat den Upload abgelehnt (HTTP {(int)res.StatusCode}).");

            var answer = await res.Content.ReadFromJsonAsync<UploadAnswer>(cancellationToken: ct);
            if (answer is null) return (false, "Die Cloud hat nicht geantwortet.");
            return (answer.Ok, answer.Ok ? null : answer.Error ?? "Unbekannter Grund.");
        }
        catch (Exception ex)
        {
            // Never thrown at the caller: a failed upload must not turn a successful backup into an
            // error. The backup itself is on disk either way, which is the part that matters.
            _log.LogWarning(ex, "Uploading a backup to the cloud failed");
            return (false, ex.Message);
        }
    }

    private sealed class UploadAnswer
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public int? Id { get; set; }
    }

    /// <summary>
    /// Carries out a restore the cloud asked for: download, verify, import, report back.
    /// <para>The hash is checked BEFORE anything is imported. A truncated download that got as far as
    /// looking like a ZIP would otherwise overwrite a live site with half of itself.</para>
    /// </summary>
    public async Task<(bool ok, string? error)> RestoreAsync(PendingRestore request, CancellationToken ct = default)
    {
        var settings = await _cloud.GetSettingsAsync();
        if (!settings.Configured) return (false, "Diese Website ist mit keiner Cloud verbunden.");

        var target = Path.Combine(_backups.BackupsDir, "cloud-" + Path.GetFileName(request.FileName));
        try
        {
            Directory.CreateDirectory(_backups.BackupsDir);

            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.Add(CloudProtocol.TokenHeader, settings.Token);

            using (var res = await client.GetAsync(
                $"{settings.Url}/api/instances/{settings.InstanceId}/backups/{request.BackupId}",
                HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!res.IsSuccessStatusCode)
                    return (false, $"Backup konnte nicht geladen werden (HTTP {(int)res.StatusCode}).");

                await using var from = await res.Content.ReadAsStreamAsync(ct);
                await using var to = File.Create(target);
                await from.CopyToAsync(to, ct);
            }

            if (!string.IsNullOrWhiteSpace(request.Sha256))
            {
                await using var check = File.OpenRead(target);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).ToLowerInvariant();
                if (!string.Equals(hash, request.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(target);
                    return (false, "Prüfsumme stimmt nicht — die Übertragung war unvollständig. Es wurde nichts verändert.");
                }
            }

            var data = await File.ReadAllBytesAsync(target, ct);
            var summary = await _transfer.ImportAsync(data);
            _log.LogInformation("Restored cloud backup {File}: {Summary}", request.FileName, summary);
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restoring cloud backup {File} failed", request.FileName);
            return (false, ex.Message);
        }
        finally
        {
            // The downloaded copy is not kept: it is the cloud's file, it is still there, and leaving
            // it in the local folder would let the site's own retention delete something it never made.
            TryDelete(target);
        }
    }

    /// <summary>Tells the cloud what became of a restore it asked for. Best effort — the site is
    /// already restored (or not) whatever this call does.</summary>
    public async Task ReportRestoreAsync(int backupId, bool ok, string? error, CancellationToken ct = default)
    {
        try
        {
            var settings = await _cloud.GetSettingsAsync();
            if (!settings.Configured) return;

            var client = _http.CreateClient();
            client.DefaultRequestHeaders.Add(CloudProtocol.TokenHeader, settings.Token);
            await client.PostAsJsonAsync(
                $"{settings.Url}/api/instances/{settings.InstanceId}/backups/restored",
                new RestoreReport { BackupId = backupId, Ok = ok, Error = error }, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reporting the restore outcome failed");
        }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not delete {Path}", path); }
    }
}
