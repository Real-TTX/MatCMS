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
    public List<string> AllTags { get; private set; } = new();
    public string? ActiveTag { get; private set; }

    public async Task OnGetAsync(string? tag)
    {
        ActiveTag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        var all = await _db.Media.OrderByDescending(m => m.Id).ToListAsync();

        AllTags = all.SelectMany(m => MatCMS.Content.TagUtil.Split(m.Tags))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Items = ActiveTag is null
            ? all
            : all.Where(m => MatCMS.Content.TagUtil.Split(m.Tags).Contains(ActiveTag, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, string? tags, string? alt)
    {
        var m = await _db.Media.FindAsync(id);
        if (m is not null)
        {
            m.Tags = MatCMS.Content.TagUtil.Normalize(tags);
            m.Alt = string.IsNullOrWhiteSpace(alt) ? null : alt.Trim();
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Medium gespeichert.";
        }
        return RedirectToPage(new { tag = ActiveTag });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var m = await _db.Media.FindAsync(id);
        if (m is not null)
        {
            var path = Path.Combine(_env.WebRootPath, "uploads", Path.GetFileName(m.Url));
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { /* ignore */ }
            _db.Media.Remove(m);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Medium gelöscht.";
        }
        return RedirectToPage(new { tag = ActiveTag });
    }

}
