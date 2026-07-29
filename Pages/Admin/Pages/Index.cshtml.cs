using System.Text;
using MatCMS.Data;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PageEntity = MatCMS.Models.Page;

namespace MatCMS.Pages.Admin.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    /// <summary>One language version of a logical page (with its own public URL).</summary>
    public record Version(int Id, string Locale, bool IsPublished, string Url);

    /// <summary>A logical page and all its language versions (the "versions" of one page).</summary>
    public record Group(PageEntity Primary, string Url, IReadOnlyList<Version> Versions);

    public List<Group> Groups { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var pages = await _db.Pages
            .OrderBy(p => p.NavOrder).ThenBy(p => p.FooterOrder).ThenBy(p => p.Title)
            .ToListAsync();

        // Group pages by their translation group (languages = versions of one logical page).
        // A page without a group is its own singleton. The default-locale page is the "primary".
        int localeRank(string loc) { var i = Array.IndexOf(Localizer.SupportedCultures.ToArray(), loc); return i < 0 ? 99 : i; }

        Groups = pages
            .GroupBy(p => string.IsNullOrWhiteSpace(p.TranslationGroup) ? $"__single:{p.Id}" : p.TranslationGroup!)
            .Select(g =>
            {
                var ordered = g.OrderBy(p => localeRank(p.Locale)).ThenBy(p => p.Id).ToList();
                var primary = ordered.FirstOrDefault(p => p.Locale == Localizer.DefaultCulture) ?? ordered[0];
                var versions = ordered
                    .Select(p => new Version(p.Id, p.Locale, p.IsPublished, MatCMS.Services.SiteContext.LocalizedUrl(p.Locale, p.Slug)))
                    .ToList();
                var url = versions.FirstOrDefault(v => v.Id == primary.Id)?.Url ?? "/";
                return new Group(primary, url, versions);
            })
            .OrderBy(g => g.Primary.NavOrder).ThenBy(g => g.Primary.FooterOrder).ThenBy(g => g.Primary.Title)
            .ToList();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var page = await _db.Pages.FindAsync(id);
        if (page is not null)
        {
            // Delete the whole logical page — all its language versions (same translation group).
            var toDelete = string.IsNullOrWhiteSpace(page.TranslationGroup)
                ? new List<PageEntity> { page }
                : await _db.Pages.Where(p => p.TranslationGroup == page.TranslationGroup).ToListAsync();
            _db.Pages.RemoveRange(toDelete);
            await _db.SaveChangesAsync();
            var extra = toDelete.Count > 1 ? $" ({toDelete.Count} Sprachversionen)" : "";
            TempData["Flash"] = $"Seite „{page.Title}“ gelöscht{extra}.";
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
