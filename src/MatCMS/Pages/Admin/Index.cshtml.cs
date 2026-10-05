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
    public int PostCount { get; private set; }
    public int PostDrafts { get; private set; }
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
        PostCount = await _db.Posts.CountAsync();
        PostDrafts = await _db.Posts.CountAsync(p => !p.IsPublished);
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
        SetupComplete = Get(SettingKeys.SetupComplete) == "1";
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
        var posts = await _db.Posts.AsNoTracking().OrderByDescending(p => p.UpdatedAt).Take(30)
            .Select(p => new { p.Id, p.Title, p.IsPublished, p.UpdatedAt }).ToListAsync();
        RecentlyEdited = pages.Select(p => new Edited("page", p.Title, p.IsPublished, p.UpdatedAt, Url.Page("/Admin/Pages/Editor", new { id = p.Id })!))
            .Concat(posts.Select(p => new Edited("post", p.Title, p.IsPublished, p.UpdatedAt, Url.Page("/Admin/Posts/Edit", new { id = p.Id })!)))
            .OrderByDescending(e => e.UpdatedAt).Take(30).ToList();

        AttentionItems = await _attention.BuildAsync(HttpContext.RequestAborted);
    }
}
