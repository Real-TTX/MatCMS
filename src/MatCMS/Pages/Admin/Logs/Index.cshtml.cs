using MatCMS.Data;
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
    public int Total { get; private set; }
    public int ErrorCount { get; private set; }

    private const int Keep = 2000;   // hard cap on stored entries
    private const int Show = 300;    // rows rendered

    public async Task OnGetAsync(string? level)
    {
        Level = string.IsNullOrWhiteSpace(level) ? null : level;

        // Keep the table bounded: drop everything older than the newest `Keep` entries.
        Total = await _db.Logs.CountAsync();
        if (Total > Keep)
        {
            var cutId = await _db.Logs.OrderByDescending(l => l.Id).Skip(Keep).Select(l => l.Id).FirstOrDefaultAsync();
            if (cutId > 0) { await _db.Logs.Where(l => l.Id <= cutId).ExecuteDeleteAsync(); Total = await _db.Logs.CountAsync(); }
        }
        ErrorCount = await _db.Logs.CountAsync(l => l.Level == "Error");

        var q = _db.Logs.AsNoTracking().AsQueryable();
        if (Level is not null) q = q.Where(l => l.Level == Level);
        Entries = await q.OrderByDescending(l => l.Id).Take(Show).ToListAsync();
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        await _db.Logs.ExecuteDeleteAsync();
        TempData["Flash"] = "Protokoll geleert.";
        return RedirectToPage();
    }
}
