using MatCMS.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Forms;

/// <summary>Alle Eingänge — the submissions of every form in one list, newest first (the dashboard card's "Alle
/// anzeigen"). Filter by read state or form (?filter=unread | read | form-&lt;id&gt;); a row opens its form's submissions,
/// where it is read, marked and deleted.</summary>
public class InboxModel : PageModel
{
    private readonly AppDbContext _db;
    public InboxModel(AppDbContext db) => _db = db;

    public const int Show = 500;
    public record Row(int Id, int FormId, string FormName, DateTime CreatedAt, bool IsRead, string Summary);
    public List<Row> Items { get; private set; } = new();
    public List<(int Id, string Name)> Forms { get; private set; } = new();
    public int Total { get; private set; }

    public async Task OnGetAsync()
    {
        Total = await _db.FormSubmissions.CountAsync();
        var subs = await _db.FormSubmissions.AsNoTracking().Include(s => s.Form)
            .OrderByDescending(s => s.CreatedAt).Take(Show).ToListAsync();
        // The first few answers, as a line — enough to recognise a submission without opening it.
        Items = subs.Select(s => new Row(s.Id, s.FormId, s.Form?.Name ?? "—", s.CreatedAt, s.IsRead,
            string.Join(" · ", SubmissionsModel.ParseFields(s.DataJson).Where(f => !string.IsNullOrWhiteSpace(f.Value)).Take(3).Select(f => f.Value.Length > 60 ? f.Value[..60] + "…" : f.Value))))
            .ToList();
        Forms = await _db.Forms.AsNoTracking().OrderBy(f => f.Name).Select(f => new ValueTuple<int, string>(f.Id, f.Name)).ToListAsync();
    }

    public async Task<IActionResult> OnPostMarkAllReadAsync()
    {
        await _db.FormSubmissions.Where(s => !s.IsRead).ExecuteUpdateAsync(x => x.SetProperty(s => s.IsRead, true));
        TempData["Flash"] = "Alle Eingänge als gelesen markiert.";
        return RedirectToPage();
    }
}
