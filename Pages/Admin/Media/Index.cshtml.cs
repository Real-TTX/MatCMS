using MatCMS.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Media;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    public IndexModel(AppDbContext db, IWebHostEnvironment env) { _db = db; _env = env; }

    public List<MatCMS.Models.Media> Items { get; private set; } = new();

    public async Task OnGetAsync() =>
        Items = await _db.Media.OrderByDescending(m => m.Id).ToListAsync();

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var m = await _db.Media.FindAsync(id);
        if (m is not null)
        {
            // Delete the underlying file (basename only → no path traversal).
            var path = Path.Combine(_env.WebRootPath, "uploads", Path.GetFileName(m.Url));
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { /* ignore */ }
            _db.Media.Remove(m);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Medium gelöscht.";
        }
        return RedirectToPage();
    }
}
