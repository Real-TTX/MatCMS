using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Submissions;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<ContactSubmission> Items { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Items = await _db.ContactSubmissions
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostToggleReadAsync(int id)
    {
        var s = await _db.ContactSubmissions.FindAsync(id);
        if (s is not null)
        {
            s.IsRead = !s.IsRead;
            await _db.SaveChangesAsync();
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var s = await _db.ContactSubmissions.FindAsync(id);
        if (s is not null)
        {
            _db.ContactSubmissions.Remove(s);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Anfrage gelöscht.";
        }
        return RedirectToPage();
    }
}
