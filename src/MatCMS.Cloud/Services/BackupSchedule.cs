namespace MatCMS.Cloud.Services;

/// <summary>
/// The backup policy a profile rolls out: whether its sites back themselves up, how often, how much
/// history they keep, what goes in — and whether the finished file is handed to the cloud.
///
/// <para>The schedule travels as ONE key (<see cref="ScheduleKey"/>) holding the JSON the instance's
/// own <c>BackupManager</c> reads, rather than as a field per option. That format is the instance's,
/// and re-modelling it here would mean two definitions of the same thing drifting apart; the site's
/// backup page and a rolled-out policy have to mean exactly the same thing or the operator is being
/// lied to on one of the two screens.</para>
///
/// <para>The granular lists in that format (which templates, which pages, which forms) are written
/// EMPTY on purpose — empty means "the whole section". They name items that exist on one site and
/// nowhere else, so a profile that distributed them would leave every other instance backing up
/// nothing at all, silently and with a green tick.</para>
///
/// <para>This lives in <c>Services</c> rather than on the page that edits it because it is a wire
/// format, not page state: the profile editor writes it, and it has already outlived one page.</para>
/// </summary>
public sealed class BackupSchedule
{
    /// <summary>Same key the instance's BackupManager reads its schedule from.</summary>
    public const string ScheduleKey = "backup.schedule";

    /// <summary>Whether finished backups are uploaded to the cloud.</summary>
    public const string ToCloudKey = "backup.toCloud";

    // The property NAMES are the contract here, because that is what the JSON carries and the
    // instance deserialises case-sensitively. Mirrors the instance's `BackupScheduleConfig`.
    public bool Enabled { get; set; }
    public int IntervalHours { get; set; } = 24;
    public int Retain { get; set; } = 7;
    public bool Templates { get; set; } = true;
    public bool Pages { get; set; } = true;
    public bool Menus { get; set; } = true;
    public bool Settings { get; set; } = true;
    public bool Submissions { get; set; } = true;
    public bool Forms { get; set; } = true;
    public bool Assets { get; set; } = true;

    /// <summary>Plugin code and its script files. Worth its own tick because it is the only backup
    /// section whose contents exist nowhere but the instance's own database.</summary>
    public bool Plugins { get; set; } = true;

    /// <summary>The files uploaded into the plugins' own asset folders. Its own tick because these
    /// are binaries: they grow a backup, backups land unencrypted in the cloud volume and every
    /// instance has a quota. Rolling the policy out without this field would leave each site on its
    /// own default — which is exactly the "it just happens somewhere" this group exists to prevent.</summary>
    public bool PluginAssets { get; set; } = true;

    // Always empty — see the class comment. Written so the instance sees the field it expects rather
    // than falling back to whatever it had stored before.
    public List<string> TemplateNames { get; set; } = [];
    public List<string> PageKeys { get; set; } = [];
    public List<string> FormSlugs { get; set; } = [];

    /// <summary>Reads a stored schedule, falling back to the defaults when there is none or when it
    /// no longer parses — the editor still has to open, or a broken value could never be corrected.</summary>
    public static BackupSchedule Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new BackupSchedule();
        try { return System.Text.Json.JsonSerializer.Deserialize<BackupSchedule>(json) ?? new BackupSchedule(); }
        catch { return new BackupSchedule(); }
    }
}
