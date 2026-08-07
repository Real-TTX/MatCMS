using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Posts;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<Post> Items { get; private set; } = new();

    public async Task OnGetAsync() =>
        Items = await _db.Posts.AsNoTracking()
            .OrderByDescending(p => p.PublishedAt).ThenByDescending(p => p.Id).ToListAsync();

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var p = await _db.Posts.FindAsync(id);
        if (p is not null)
        {
            _db.Posts.Remove(p);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Beitrag gelöscht.";
        }
        return RedirectToPage();
    }
}
