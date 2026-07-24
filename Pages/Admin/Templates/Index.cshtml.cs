using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Templates;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<Template> Items { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Items = await _db.Templates
            .OrderByDescending(t => t.IsActive).ThenBy(t => t.Name)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostActivateAsync(int id)
    {
        var target = await _db.Templates.FindAsync(id);
        if (target is null)
        {
            TempData["FlashError"] = "Template nicht gefunden.";
            return RedirectToPage();
        }

        var all = await _db.Templates.ToListAsync();
        foreach (var t in all)
            t.IsActive = t.Id == id;

        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Template „{target.Name}“ ist jetzt aktiv.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var target = await _db.Templates.FindAsync(id);
        if (target is null) return RedirectToPage();

        if (target.IsActive)
        {
            TempData["FlashError"] = "Das aktive Template kann nicht gelöscht werden.";
            return RedirectToPage();
        }
        if (await _db.Templates.CountAsync() <= 1)
        {
            TempData["FlashError"] = "Das letzte Template kann nicht gelöscht werden.";
            return RedirectToPage();
        }

        _db.Templates.Remove(target);
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Template gelöscht.";
        return RedirectToPage();
    }
}
