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

        var q = _db.Logs.AsNoTracking().AsQueryable();
        if (Level is not null) q = q.Where(l => l.Level == Level);
        if (Category is not null) q = q.Where(l => l.Category == Category);
        Entries = await q.OrderByDescending(l => l.Id).Take(Show).ToListAsync();
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        await _db.Logs.ExecuteDeleteAsync();
        TempData["Flash"] = "Protokoll geleert.";
        return RedirectToPage();
    }
}
