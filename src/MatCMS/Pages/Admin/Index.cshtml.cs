using MatCMS.Data;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin;

/// <summary>
/// The start page of a site: what is there, what needs a look, what was worked on last. Everything is a READ
/// of state other pages own, each line linking to where it is dealt with.
/// <para>MatCMS works 100% without a cloud, so the cloud is ONE optional line here — a standalone site sees a
/// complete dashboard (backups, mail, maintenance, errors) with no hint that something is missing.</para>
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CloudState _cloud;
    private readonly BackupManager _backups;
    private readonly EmailService _mail;
    private readonly VersionService _version;
    private readonly Localizer _t;
    private readonly StatsService _stats;
    private readonly AttentionService _attention;

    public IndexModel(AppDbContext db, CloudState cloud, BackupManager backups, EmailService mail, VersionService version, Localizer t, StatsService stats, AttentionService attention)
    {
        _attention = attention;
        _db = db; _cloud = cloud; _backups = backups; _mail = mail; _version = version; _t = t; _stats = stats;
    }

    // ---- Tiles ------------------------------------------------------------------------------------------
    /// <summary>Rows a dashboard card shows; the rest is behind its "Alle anzeigen".</summary>
    public const int CardRows = 6;
    public int PageCount { get; private set; }
    public int PageDrafts { get; private set; }
    public int MediaCount { get; private set; }
    public long MediaBytes { get; private set; }
    public int UserCount { get; private set; }
    public int MemberCount { get; private set; }
    public int SubmissionCount { get; private set; }
    public int UnreadCount { get; private set; }
    public List<(DateTime Day, int Count)> ErrorsPerDay { get; private set; } = new();
    /// <summary>Visitor statistics of the last 7 days, for the tile that leads to Admin → Statistik.</summary>
    public MatCMS.Shared.StatsSummary Stats7d { get; private set; } = null!;
    public int ErrorCount7d => ErrorsPerDay.Sum(d => d.Count);

    // ---- Website / system ------------------------------------------------------------------------------
    public bool SetupComplete { get; private set; }
    public bool Maintenance { get; private set; }
    public string? SiteName { get; private set; }
    public string? CanonicalUrl { get; private set; }
    public string? ActiveTemplate { get; private set; }
    public string Languages { get; private set; } = "";
    public string Version => _version.Current;
    public bool MailConfigured { get; private set; }
    public bool BackupScheduled { get; private set; }
    public DateTime? LastBackup { get; private set; }
    public int BackupCount { get; private set; }

    public bool CloudConnected => _cloud.Connected;
    public bool CloudOutOfSync => _cloud.OutOfSync;
    public string? CloudSyncError => _cloud.SyncError;
    public DateTime? CloudLastSync => _cloud.LastSyncUtc;
    public bool UpdateAvailable => _cloud.Connected && _cloud.UpdateAvailable;
    public string? LatestVersion => _cloud.LatestVersion;

    // ---- Lists ---------------------------------------------------------------------------------------------
    public sealed record Edited(string Kind, string Title, bool Published, DateTime UpdatedAt, string Url);
    public List<Edited> RecentlyEdited { get; private set; } = new();
    /// <summary>The tiles loaded plugins add to the row of figures.</summary>
    public IReadOnlyList<MatCMS.Plugin.UiStat> PluginTiles { get; private set; } = Array.Empty<MatCMS.Plugin.UiStat>();
    /// <summary>The cards plugins bring (the shop's latest orders), shown after the standard ones.</summary>
    public IReadOnlyList<(string Id, MatCMS.Plugin.DashboardCard Card)> PluginCards { get; private set; } = Array.Empty<(string, MatCMS.Plugin.DashboardCard)>();

    // ---- the layout: what is shown, in which order — the user's own, else the site's default ---------------
    /// <summary>Every widget in the layout's order (hidden ones too) — what the customise panel lists.</summary>
    public IReadOnlyList<DashboardLayout.Widget> AllWidgets { get; private set; } = Array.Empty<DashboardLayout.Widget>();
    /// <summary>The ids the layout hides.</summary>
    public IReadOnlySet<string> HiddenWidgets { get; private set; } = new HashSet<string>();
    /// <summary>What is drawn: the visible widgets that have something to show, grouped into rows.</summary>
    public IReadOnlyList<List<DashboardLayout.Widget>> Rows { get; private set; } = Array.Empty<List<DashboardLayout.Widget>>();
    /// <summary>True when this user has a layout of his own (else the site's default or the built-in one).</summary>
    public bool OwnLayout { get; private set; }
    /// <summary>True when an admin has saved a default for everybody.</summary>
    public bool SiteDefault { get; private set; }
    public bool IsAdmin => User.IsInRole("Admin");
    public List<MatCMS.Models.FormSubmission> RecentSubmissions { get; private set; } = new();
    public List<MatCMS.Models.LogEntry> RecentErrors { get; private set; } = new();

    /// <summary>What needs a look — the same list as Admin/Attention (AttentionService).</summary>
    public List<AttentionService.Item> AttentionItems { get; private set; } = new();

    /// <summary>A scheduled backup that has not run for this long is worth a line.</summary>
    public static readonly TimeSpan StaleBackup = TimeSpan.FromDays(7);

    public async Task OnGetAsync()
    {
        PageCount = await _db.Pages.CountAsync();
        PageDrafts = await _db.Pages.CountAsync(p => !p.IsPublished);
        MediaCount = await _db.Media.CountAsync();
        MediaBytes = MediaCount == 0 ? 0 : await _db.Media.SumAsync(m => m.SizeBytes);
        UserCount = await _db.Users.CountAsync();
        MemberCount = await _db.SiteMembers.CountAsync();
        SubmissionCount = await _db.FormSubmissions.CountAsync();
        UnreadCount = await _db.FormSubmissions.CountAsync(s => !s.IsRead);
        RecentSubmissions = await _db.FormSubmissions.AsNoTracking().Include(s => s.Form)
            .OrderByDescending(s => s.CreatedAt).Take(CardRows).ToListAsync();

        var settings = await _db.SiteSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        string? Get(string k) => settings.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        SetupComplete = SettingKeys.IsSetupDone(Get);
        Maintenance = Get(SettingKeys.MaintenanceEnabled) == "1";
        SiteName = Get(SettingKeys.SiteName);
        CanonicalUrl = Get(SettingKeys.CanonicalUrl);
        Languages = Get(SettingKeys.Languages) ?? "de";
        ActiveTemplate = await _db.Templates.AsNoTracking().Where(t => t.IsActive).Select(t => t.Name).FirstOrDefaultAsync();
        MailConfigured = await _mail.IsConfiguredAsync();

        var cfg = await _backups.GetConfigAsync();
        BackupScheduled = cfg.Enabled;
        var stored = _backups.ListStored();
        BackupCount = stored.Count;
        LastBackup = stored.Count == 0 ? null : stored.Max(b => b.ModifiedUtc);

        // Errors per day for the last 7 days (oldest first).
        var from = DateTime.UtcNow.Date.AddDays(-6);
        var days = (await _db.Logs.AsNoTracking().Where(l => l.Level == "Error" && l.CreatedAt >= from).Select(l => l.CreatedAt).ToListAsync())
            .GroupBy(d => d.Date).ToDictionary(g => g.Key, g => g.Count());
        for (var d = from; d <= DateTime.UtcNow.Date; d = d.AddDays(1)) ErrorsPerDay.Add((d, days.GetValueOrDefault(d)));
        Stats7d = await _stats.SummaryAsync(7);
        RecentErrors = await _db.Logs.AsNoTracking().Where(l => l.Level == "Error").OrderByDescending(l => l.Id).Take(5).ToListAsync();

        // What was worked on last — the fastest way back into an edit.
        var pages = await _db.Pages.AsNoTracking().OrderByDescending(p => p.UpdatedAt).Take(30)
            .Select(p => new { p.Id, p.Title, p.IsPublished, p.UpdatedAt }).ToListAsync();
        RecentlyEdited = pages.Select(p => new Edited("page", p.Title, p.IsPublished, p.UpdatedAt, Url.Page("/Admin/Pages/Editor", new { id = p.Id })!))
            .OrderByDescending(e => e.UpdatedAt).Take(30).ToList();

        AttentionItems = await _attention.BuildAsync(HttpContext.RequestAborted);

        var contributions = HttpContext.RequestServices.GetRequiredService<MatCMS.Services.Plugins.PluginContributions>();
        PluginTiles = await contributions.TilesAsync(HttpContext.RequestServices, HttpContext.RequestAborted);
        PluginCards = await contributions.CardsAsync(HttpContext.RequestServices, HttpContext.RequestAborted);
        RecentlyEdited = RecentlyEdited
            .Concat((await contributions.RecentAsync(HttpContext.RequestServices, HttpContext.RequestAborted))
                .Select(r => new Edited(r.Kind, r.Title, r.Published, r.UpdatedUtc, r.Url)))
            .OrderByDescending(e => e.UpdatedAt).Take(30).ToList();

        // The widgets, in the order they have by default; the layout then orders and hides them.
        var defaults = new List<DashboardLayout.Widget>
        {
            new("tiles", _t["dashboard.cfg.tiles"], true, "_DashTiles"),
            new("attention", _t["dashboard.attention"], false, "_DashAttention"),
            new("website", _t["dashboard.website"], false, "_DashWebsite"),
            new("recent", _t["dashboard.recentlyEdited"], false, "_DashRecent"),
            new("submissions", _t["dashboard.recentSubmissions"], false, "_DashSubmissions")
        };
        defaults.AddRange(PluginCards.Select(c => new DashboardLayout.Widget(c.Id, c.Card.Title, false, "_DashPluginCard", c.Card)));
        defaults.Add(new("errors", _t["dashboard.recentErrors"], true, "_DashErrors", Present: RecentErrors.Count > 0, Note: _t["dashboard.cfg.onlyWhen"]));
        defaults.Add(new("quickstart", _t["dashboard.quickstart"], true, "_DashQuickstart", Present: PageCount < 3, Note: _t["dashboard.cfg.onlyWhen"]));

        var mine = await CurrentUserAsync();
        OwnLayout = !string.IsNullOrWhiteSpace(mine?.DashboardJson);
        var siteDefault = Get(SettingKeys.DashboardLayout);
        SiteDefault = siteDefault is not null;
        var (ordered, hidden) = DashboardLayout.Apply(defaults, DashboardLayout.Parse(mine?.DashboardJson) ?? DashboardLayout.Parse(siteDefault));
        AllWidgets = ordered;
        HiddenWidgets = hidden;
        Rows = DashboardLayout.Rows(ordered.Where(w => !hidden.Contains(w.Id) && w.Present));
    }

    private async Task<MatCMS.Models.User?> CurrentUserAsync() =>
        int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var uid) ? await _db.Users.FirstOrDefaultAsync(u => u.Id == uid) : null;

    /// <summary>Saves the customise panel: the order of the rows and which are ticked. An admin may make it everybody's default.</summary>
    public async Task<IActionResult> OnPostLayoutAsync(List<string>? id, List<string>? show, bool asDefault)
    {
        var json = DashboardLayout.Serialize(id, show);
        var me = await CurrentUserAsync();
        if (me is null) return Redirect("/login");
        me.DashboardJson = json;
        if (asDefault && IsAdmin) await SetSiteDefaultAsync(json);
        await _db.SaveChangesAsync();
        TempData["Flash"] = _t[asDefault && IsAdmin ? "dashboard.cfg.savedDefault" : "dashboard.cfg.saved"];
        return RedirectToPage();
    }

    /// <summary>Back to the site's default (or the built-in layout): the user's own choice is forgotten.</summary>
    public async Task<IActionResult> OnPostResetLayoutAsync(bool everybody)
    {
        var me = await CurrentUserAsync();
        if (me is null) return Redirect("/login");
        me.DashboardJson = null;
        if (everybody && IsAdmin) await SetSiteDefaultAsync(null);
        await _db.SaveChangesAsync();
        TempData["Flash"] = _t["dashboard.cfg.resetDone"];
        return RedirectToPage();
    }

    private async Task SetSiteDefaultAsync(string? json)
    {
        var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.DashboardLayout);
        if (json is null) { if (row is not null) _db.SiteSettings.Remove(row); return; }
        if (row is null) _db.SiteSettings.Add(new MatCMS.Models.SiteSetting { Key = SettingKeys.DashboardLayout, Value = json });
        else row.Value = json;
    }
}
