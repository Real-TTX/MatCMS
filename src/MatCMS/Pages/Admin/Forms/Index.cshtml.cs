using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Forms;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public record Row(Form Form, int Total, int Unread);

    public List<Row> Items { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var forms = await _db.Forms.OrderBy(f => f.Name).ToListAsync();
        var counts = await _db.FormSubmissions
            .GroupBy(s => s.FormId)
            .Select(g => new { FormId = g.Key, Total = g.Count(), Unread = g.Count(s => !s.IsRead) })
            .ToListAsync();

        Items = forms.Select(f =>
        {
            var c = counts.FirstOrDefault(x => x.FormId == f.Id);
            return new Row(f, c?.Total ?? 0, c?.Unread ?? 0);
        }).ToList();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var form = await _db.Forms.FindAsync(id);
        if (form is not null)
        {
            _db.Forms.Remove(form); // cascades to submissions
            await _db.SaveChangesAsync();
            TempData["Flash"] = $"Formular „{form.Name}“ gelöscht.";
        }
        return RedirectToPage();
    }
}
