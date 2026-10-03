using MatCMS.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// "Braucht Aufmerksamkeit": what on this site needs a look — setup, maintenance mode, backups, mail delivery, unread
/// submissions, an update, the cloud sync, recent errors. ONE list for the dashboard card and its full page
/// (Admin/Attention), so the two cannot disagree. Each line says where it is dealt with and, where there is one, what
/// it comes down to (<see cref="Item.Detail"/>) and the newest error behind it (<see cref="Item.LastError"/>).
/// </summary>
public sealed class AttentionService
{
    private readonly AppDbContext _db;
    private readonly CloudState _cloud;
    private readonly BackupManager _backups;
    private readonly EmailService _mail;
    private readonly VersionService _version;
    private readonly Localizer _t;
    private readonly LinkGenerator _links;

    public AttentionService(AppDbContext db, CloudState cloud, BackupManager backups, EmailService mail, VersionService version,
        Localizer t, LinkGenerator links)
    {
        _db = db; _cloud = cloud; _backups = backups; _mail = mail; _version = version; _t = t; _links = links;
    }

    /// <param name="Level">err | warn | info.</param>
    /// <param name="Kind">What it is about (backupStale, errors, …) — the filter of the full page.</param>
    public sealed record Item(string Level, string Kind, string Icon, string Title, string Text, string Url, string? Detail = null, string? LastError = null);

    /// <summary>A scheduled backup that has not run for this long is worth a line.</summary>
    public static readonly TimeSpan StaleBackup = TimeSpan.FromDays(7);

    public async Task<List<Item>> BuildAsync(CancellationToken ct = default)
    {
        var settings = await _db.SiteSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        string? Get(string k) => settings.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        string Page(string page, object? values = null) => _links.GetPathByPage(page, values: values) ?? page;
        string Settings(string tab) => Page("/Admin/Settings/Index", new { tab });
        string D(string key, params object[] args) => args.Length == 0 ? _t["attention.detail." + key] : _t["attention.detail." + key, args];
        static string Local(DateTime utc) => utc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

        var a = new List<Item>();
        // Worded through the localizer like every other string — the admin runs in German or English.
        void Add(string level, string icon, string key, string url, string? detail, string? lastError, params object[] args) =>
            a.Add(new(level, key, icon, _t["dashboard.attn." + key + ".title"],
                args.Length == 0 ? _t["dashboard.attn." + key] : _t["dashboard.attn." + key, args], url, detail, lastError));

        if (Get(SettingKeys.SetupComplete) != "1") Add("info", "🧭", "setup", Page("/Admin/Setup/Index"), null, null);
        if (Get(SettingKeys.MaintenanceEnabled) == "1")
            Add("warn", "🚧", "maintenance", Settings("maintenance"), Get(SettingKeys.MaintenanceTitle) is { } mt ? D("maintenanceTitle", mt) : null, null);

        var cfg = await _backups.GetConfigAsync();
        var stored = _backups.ListStored();
        var last = stored.OrderByDescending(b => b.ModifiedUtc).FirstOrDefault();
        if (!cfg.Enabled && last is null) Add("warn", "💾", "backupNone", Page("/Admin/Backup/Index"), D("backupOff"), null);
        else if (last is not null && DateTime.UtcNow - last.ModifiedUtc > StaleBackup)
            Add("warn", "💾", "backupStale", Page("/Admin/Backup/Index"),
                D("lastBackup", Local(last.ModifiedUtc), last.Name) + (cfg.Enabled ? "" : " · " + D("backupOff")), null,
                (int)(DateTime.UtcNow - last.ModifiedUtc).TotalDays);

        if (!await _mail.IsConfiguredAsync()) Add("warn", "✉️", "mail", Settings("smtp"), D("mail"), null);

        var unread = await _db.FormSubmissions.CountAsync(s => !s.IsRead, ct);
        if (unread > 0)
        {
            var newest = await _db.FormSubmissions.AsNoTracking().Include(s => s.Form).Where(s => !s.IsRead)
                .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
            Add("info", "📨", "unread", Page("/Admin/Forms/Inbox", new { filter = "unread" }),
                newest is null ? null : D("newestSubmission", newest.Form?.Name ?? "—", Local(newest.CreatedAt)), null, unread);
        }

        if (_cloud.Connected && _cloud.UpdateAvailable)
            Add("info", "⬆️", "update", Settings("cloud"), D("updateViaCloud"), null, _cloud.LatestVersion ?? "", _version.Current);
        if (_cloud.Connected && !string.IsNullOrWhiteSpace(_cloud.SyncError))
            a.Add(new("err", "syncError", "☁️", _t["dashboard.attn.syncError.title"], _cloud.SyncError!, Settings("cloud"),
                D("sync", _cloud.LastSyncUtc is DateTime ls ? Local(ls) : "—", _cloud.AppliedRevision, _cloud.ConfigRevision)));
        else if (_cloud.OutOfSync)
            Add("info", "☁️", "syncPending", Settings("cloud"), D("sync", _cloud.LastSyncUtc is DateTime ls2 ? Local(ls2) : "—", _cloud.AppliedRevision, _cloud.ConfigRevision), null);

        var from = DateTime.UtcNow.AddDays(-7);
        var errors = await _db.Logs.CountAsync(l => l.Level == "Error" && l.CreatedAt >= from, ct);
        if (errors > 0)
        {
            var e = await _db.Logs.AsNoTracking().Where(l => l.Level == "Error").OrderByDescending(l => l.Id).FirstAsync(ct);
            var today = await _db.Logs.CountAsync(l => l.Level == "Error" && l.CreatedAt >= DateTime.UtcNow.Date, ct);
            Add("warn", "🐞", "errors", Page("/Admin/Logs/Index", new { level = "Error" }), D("errorsToday", today),
                _t["attention.detail.lastError", Local(e.CreatedAt), (e.StatusCode is int sc ? sc + " " : "") + (string.IsNullOrEmpty(e.Path) ? "" : e.Path + " · ") + e.Message],
                errors);
        }

        return a.OrderBy(x => x.Level switch { "err" => 0, "warn" => 1, _ => 2 }).ToList();
    }
}
