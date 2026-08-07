using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PagesIndex = MatCMS.Pages.Admin.Pages.IndexModel;

namespace MatCMS.Pages.Admin.Forms;

public class CreateModel : PageModel
{
    private readonly AppDbContext _db;
    public CreateModel(AppDbContext db) => _db = db;

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Slug { get; set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var name = (Name ?? "").Trim();
        var slug = PagesIndex.Slugify(string.IsNullOrWhiteSpace(Slug) ? name : Slug!);

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(slug))
        {
            Error = "Bitte einen Namen (und optional einen Slug) angeben.";
            return Page();
        }
        if (await _db.Forms.AnyAsync(f => f.Slug == slug))
        {
            Error = $"Der Slug „{slug}“ ist bereits vergeben.";
            return Page();
        }

        var form = new Form { Name = name, Slug = slug, DefinitionJson = "[]" };
        _db.Forms.Add(form);
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Formular erstellt. Fügen Sie nun Elemente hinzu.";
        return RedirectToPage("Edit", new { id = form.Id });
    }
}
