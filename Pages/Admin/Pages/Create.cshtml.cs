using MatCMS.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PageEntity = MatCMS.Models.Page;

namespace MatCMS.Pages.Admin.Pages;

public class CreateModel : PageModel
{
    private readonly AppDbContext _db;
    public CreateModel(AppDbContext db) => _db = db;

    [BindProperty] public string? Title { get; set; }
    [BindProperty] public string? Slug { get; set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var title = (Title ?? "").Trim();
        var slug = IndexModel.Slugify(string.IsNullOrWhiteSpace(Slug) ? title : Slug!);

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(slug))
        {
            Error = "Bitte einen Titel (und optional einen Slug) angeben.";
            return Page();
        }
        if (IndexModel.IsReserved(slug))
        {
            Error = $"Der Slug „{slug}“ ist reserviert und kann nicht verwendet werden.";
            return Page();
        }
        if (await _db.Pages.AnyAsync(p => p.Slug == slug))
        {
            Error = $"Der Slug „{slug}“ ist bereits vergeben.";
            return Page();
        }

        var page = new PageEntity { Title = title, Slug = slug, IsPublished = false };
        _db.Pages.Add(page);
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Seite erstellt. Fügen Sie nun Blöcke hinzu.";
        return RedirectToPage("Edit", new { id = page.Id });
    }
}
