namespace MatCMS.Models;

/// <summary>
/// Configuration for automatic scheduled backups. Persisted as a JSON blob in a single
/// <c>SiteSetting</c> row (key "backup.schedule") so it needs no schema/table change.
/// </summary>
public class BackupScheduleConfig
{
    public bool Enabled { get; set; }

    /// <summary>How often a scheduled backup runs, in hours (e.g. 24 = daily, 168 = weekly).</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>How many scheduled backup files to keep on disk (oldest are pruned).</summary>
    public int Retain { get; set; } = 7;

    // Which sections to include — mirrors ContentTransferService.BackupOptions.
    public bool Templates { get; set; } = true;
    public bool Pages { get; set; } = true;
    public bool Menus { get; set; } = true;
    public bool Settings { get; set; } = true;
    public bool Submissions { get; set; } = true;
    public bool Forms { get; set; } = true;
    public bool Assets { get; set; } = true;
    /// <summary>Plugins (code, further script files, configuration). Default ON — and the default is
    /// what an already-stored schedule from before this field falls back to, so existing sites start
    /// carrying their plugins on the very next run instead of only after someone re-saves the form.</summary>
    public bool Plugins { get; set; } = true;
    /// <summary>The files uploaded into the plugins' own asset folders. Default ON for the same reason
    /// as Plugins — a stored schedule from before this field falls back to the default, so a site starts
    /// carrying them on the next run instead of only after someone re-saves the form. Its own tick
    /// because these are binaries: they can grow a backup, and the backup quota is finite.</summary>
    public bool PluginAssets { get; set; } = true;

    // Granular within-section selection. Empty = the whole section; otherwise only these items.
    /// <summary>Template names to back up (empty = all).</summary>
    public List<string> TemplateNames { get; set; } = new();
    /// <summary>Page keys ("slug|locale") to back up (empty = all).</summary>
    public List<string> PageKeys { get; set; } = new();
    /// <summary>Form slugs to back up (empty = all).</summary>
    public List<string> FormSlugs { get; set; } = new();

    /// <summary>UTC timestamp (ISO-8601) of the last successful scheduled run, or null.</summary>
    public string? LastRunUtc { get; set; }
}
