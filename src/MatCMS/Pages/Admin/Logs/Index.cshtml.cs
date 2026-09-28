using MatCMS.Data;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Logs;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<Models.LogEntry> Entries { get; private set; } = new();
    public string? Level { get; private set; }
    public string? Category { get; private set; }
    public int Total { get; private set; }
    public int ErrorCount { get; private set; }
    public bool HasRequestLog { get; private set; }

    // Config (Settings → Log).
    public bool RequestsOn { get; private set; }
    public int ErrorsDays { get; private set; }
    public int RequestsDays { get; private set; }

    private const int Keep = 2000;   // hard cap on stored entries (display-time safety net; the sweeper does day-based retention)
    private const int Show = 300;    // rows rendered

    public async Task OnGetAsync(string? level, string? category)
    {
        Level = string.IsNullOrWhiteSpace(level) ? null : level;
        Category = string.IsNullOrWhiteSpace(category) ? null : category;

        // Keep the table bounded even if the sweeper has not run yet: drop everything older than the newest `Keep`.
        Total = await _db.Logs.CountAsync();
        if (Total > Keep)
        {
            var cutId = await _db.Logs.OrderByDescending(l => l.Id).Skip(Keep).Select(l => l.Id).FirstOrDefaultAsync();
            if (cutId > 0) { await _db.Logs.Where(l => l.Id <= cutId).ExecuteDeleteAsync(); Total = await _db.Logs.CountAsync(); }
        }
        ErrorCount = await _db.Logs.CountAsync(l => l.Level == "Error");
        HasRequestLog = await _db.Logs.AnyAsync(l => l.Category == "webrequest");

        await LoadConfigAsync();

        var q = _db.Logs.AsNoTracking().AsQueryable();
        if (Level is not null) q = q.Where(l => l.Level == Level);
        if (Category is not null) q = q.Where(l => l.Category == Category);
        Entries = await q.OrderByDescending(l => l.Id).Take(Show).ToListAsync();
    }

    private async Task LoadConfigAsync()
    {
        var map = await _db.SiteSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.LogRequests || s.Key == SettingKeys.LogRetentionErrorsDays || s.Key == SettingKeys.LogRetentionRequestsDays)
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        RequestsOn = map.GetValueOrDefault(SettingKeys.LogRequests) == "on";
        ErrorsDays = int.TryParse(map.GetValueOrDefault(SettingKeys.LogRetentionErrorsDays), out var e) ? e : 90;
        RequestsDays = int.TryParse(map.GetValueOrDefault(SettingKeys.LogRetentionRequestsDays), out var r) ? r : 14;
    }

    public async Task<IActionResult> OnPostSaveConfigAsync(bool requestsOn, int errorsDays, int requestsDays)
    {
        async Task SetAsync(string key, string value)
        {
            var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
            if (row is null) _db.SiteSettings.Add(new Models.SiteSetting { Key = key, Value = value });
            else row.Value = value;
        }
        await SetAsync(SettingKeys.LogRequests, requestsOn ? "on" : "off");
        await SetAsync(SettingKeys.LogRetentionErrorsDays, Math.Clamp(errorsDays, 0, 3650).ToString());
        await SetAsync(SettingKeys.LogRetentionRequestsDays, Math.Clamp(requestsDays, 0, 3650).ToString());
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Protokoll-Einstellungen gespeichert.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        await _db.Logs.ExecuteDeleteAsync();
        TempData["Flash"] = "Protokoll geleert.";
        return RedirectToPage();
    }
}
