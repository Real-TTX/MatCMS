using System.Text;
using MatCMS.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PageEntity = MatCMS.Models.Page;

namespace MatCMS.Pages.Admin.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<PageEntity> Items { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Items = await _db.Pages
            .OrderBy(p => p.NavOrder).ThenBy(p => p.FooterOrder).ThenBy(p => p.Title)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var page = await _db.Pages.FindAsync(id);
        if (page is not null)
        {
            _db.Pages.Remove(page);
            await _db.SaveChangesAsync();
            TempData["Flash"] = $"Seite „{page.Title}“ gelöscht.";
        }
        return RedirectToPage();
    }

    private static readonly HashSet<string> ReservedSlugs =
        new(StringComparer.OrdinalIgnoreCase) { "admin", "login", "logout", "error" };

    /// <summary>Slugs that collide with fixed application routes and would be unreachable.</summary>
    public static bool IsReserved(string slug) => ReservedSlugs.Contains(slug);

    public static string Slugify(string input)
    {
        input = input.Trim().ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var ch in input)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (ch == 'ä') sb.Append("ae");
            else if (ch == 'ö') sb.Append("oe");
            else if (ch == 'ü') sb.Append("ue");
            else if (ch == 'ß') sb.Append("ss");
            else if (ch is ' ' or '-' or '_' or '/') sb.Append('-');
            // else drop
        }
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}
