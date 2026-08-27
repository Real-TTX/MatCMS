using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Roles;

/// <summary>The role vocabulary for the members area (e.g. "Familie", "Trauzeugen"). Kept on one page
/// — a role is only a name — with an inline add and per-row delete.</summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<SiteRole> Items { get; private set; } = new();

    public async Task OnGetAsync() =>
        Items = await _db.SiteRoles.AsNoTracking().OrderBy(r => r.Name).ToListAsync();

    public async Task<IActionResult> OnPostAddAsync(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
        {
            TempData["FlashError"] = "Bitte einen Rollennamen eingeben.";
            return RedirectToPage();
        }
        if (await _db.SiteRoles.AnyAsync(r => r.Name == trimmed))
        {
            TempData["FlashError"] = $"Die Rolle „{trimmed}“ gibt es schon.";
            return RedirectToPage();
        }
        _db.SiteRoles.Add(new SiteRole { Name = trimmed });
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Rolle angelegt.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var role = await _db.SiteRoles.FindAsync(id);
        if (role is not null)
        {
            _db.SiteRoles.Remove(role);
            await _db.SaveChangesAsync();
            // Members keep the (now unknown) role string in their CSV — harmless: a page can only
            // require a role that exists, so an orphaned tick simply never grants anything.
            TempData["Flash"] = "Rolle gelöscht.";
        }
        return RedirectToPage();
    }
}
