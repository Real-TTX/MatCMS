using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// Manages scheduled backups: loads/saves the schedule config (stored in a SiteSetting),
/// writes backup ZIPs to a persisted folder (appdata/backups), and lists/reads/deletes them.
/// Used both by the admin Backup page and by <see cref="BackupSchedulerService"/>.
/// </summary>
public class BackupManager
{
    public const string ConfigKey = "backup.schedule";

    private readonly AppDbContext _db;
    private readonly ContentTransferService _transfer;
    private readonly IWebHostEnvironment _env;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public BackupManager(AppDbContext db, ContentTransferService transfer, IWebHostEnvironment env)
    {
        _db = db;
        _transfer = transfer;
        _env = env;
    }

    /// <summary>Folder where scheduled backups live (persisted via the appdata volume, outside wwwroot).</summary>
    public string BackupsDir
    {
        get
        {
            var dir = Path.Combine(_env.ContentRootPath, "appdata", "backups");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public async Task<BackupScheduleConfig> GetConfigAsync()
    {
        var row = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == ConfigKey);
        if (row is null || string.IsNullOrWhiteSpace(row.Value)) return new BackupScheduleConfig();
        try { return JsonSerializer.Deserialize<BackupScheduleConfig>(row.Value) ?? new(); }
        catch { return new BackupScheduleConfig(); }
    }

    public async Task SaveConfigAsync(BackupScheduleConfig cfg)
    {
        var json = JsonSerializer.Serialize(cfg, JsonOpts);
        var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == ConfigKey);
        if (row is null) _db.SiteSettings.Add(new SiteSetting { Key = ConfigKey, Value = json });
        else row.Value = json;
        await _db.SaveChangesAsync();
    }

    public static bool IsDue(BackupScheduleConfig cfg, DateTime nowUtc)
    {
        if (!cfg.Enabled) return false;
        if (string.IsNullOrWhiteSpace(cfg.LastRunUtc)) return true;
        if (!DateTime.TryParse(cfg.LastRunUtc, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var last))
            return true;
        return nowUtc - last >= TimeSpan.FromHours(Math.Max(1, cfg.IntervalHours));
    }

    public ContentTransferService.BackupOptions ToOptions(BackupScheduleConfig cfg) => new()
    {
        Templates = cfg.Templates,
        Pages = cfg.Pages,
        Menus = cfg.Menus,
        Settings = cfg.Settings,
        Submissions = cfg.Submissions,
        Forms = cfg.Forms,
        Assets = cfg.Assets,
        TemplateNames = cfg.TemplateNames is { Count: > 0 } ? cfg.TemplateNames : null,
        PageKeys = cfg.PageKeys is { Count: > 0 } ? cfg.PageKeys : null,
        FormSlugs = cfg.FormSlugs is { Count: > 0 } ? cfg.FormSlugs : null
    };

    /// <summary>The site name as a safe filename slug (used as the backup filename prefix).</summary>
    public async Task<string> SiteSlugAsync()
    {
        var name = (await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKeys.SiteName))?.Value;
        var slug = FileSlug(name);
        return string.IsNullOrEmpty(slug) ? "backup" : slug;
    }

    /// <summary>Normalises text to a lowercase, ASCII, filename-safe slug (umlauts transliterated,
    /// everything else collapsed to single underscores).</summary>
    public static string FileSlug(string? s)
    {
        s = (s ?? "").Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(ch < 128 && char.IsLetterOrDigit(ch) ? ch : '_');
        return Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
    }

    /// <summary>Runs a backup with the config's selection, writes it to disk, prunes old files, and
    /// records the run time. Returns the created file name.</summary>
    public async Task<string> RunAsync(BackupScheduleConfig cfg, string prefix = "auto")
    {
        var options = ToOptions(cfg);
        var bytes = await _transfer.ExportAsync(options);
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss");
        var name = $"{await SiteSlugAsync()}_{prefix}_{stamp}.zip";
        await File.WriteAllBytesAsync(Path.Combine(BackupsDir, name), bytes);

        Prune(cfg.Retain);

        cfg.LastRunUtc = DateTime.UtcNow.ToString("o");
        await SaveConfigAsync(cfg);
        return name;
    }

    /// <summary>Deletes the oldest scheduled backups beyond the retention count.</summary>
    public void Prune(int retain)
    {
        if (retain < 1) retain = 1;
        var files = new DirectoryInfo(BackupsDir).GetFiles("*.zip")
            .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        foreach (var f in files.Skip(retain))
            try { f.Delete(); } catch { /* ignore */ }
    }

    public record StoredBackup(string Name, long SizeBytes, DateTime ModifiedUtc);

    public List<StoredBackup> ListStored() =>
        new DirectoryInfo(BackupsDir).GetFiles("*.zip")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new StoredBackup(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();

    /// <summary>Resolves a stored-backup path safely (file name only, must live in BackupsDir).</summary>
    private string? ResolvePath(string name)
    {
        var safe = Path.GetFileName(name ?? "");
        if (string.IsNullOrWhiteSpace(safe) || !safe.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        var path = Path.Combine(BackupsDir, safe);
        return File.Exists(path) ? path : null;
    }

    public async Task<byte[]?> ReadStoredAsync(string name)
    {
        var path = ResolvePath(name);
        return path is null ? null : await File.ReadAllBytesAsync(path);
    }

    public bool DeleteStored(string name)
    {
        var path = ResolvePath(name);
        if (path is null) return false;
        try { File.Delete(path); return true; } catch { return false; }
    }
}
