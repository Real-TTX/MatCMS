using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Plugins;

public class CreateModel : PageModel
{
    private readonly AppDbContext _db;
    public CreateModel(AppDbContext db) => _db = db;

    [BindProperty] public string? Name { get; set; }
    public string? Error { get; private set; }

    private const string Starter =
        "// Beispiel-Plugin. Verfügbar: AddAdminMenu(label, url, icon), Service<T>()\n" +
        "AddAdminMenu(\"Mein Plugin\", \"/admin\", \"🔌\");\n\n" +
        "// Datenzugriff:\n" +
        "// var db = Service<AppDbContext>();\n" +
        "// var seiten = db.Pages.Count();\n";

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var name = (Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Error = "Bitte einen Namen angeben.";
            return Page();
        }
        var plugin = new MatCMS.Models.Plugin { Name = name, Code = Starter, Enabled = false };
        _db.Plugins.Add(plugin);
        await _db.SaveChangesAsync();
        return RedirectToPage("Edit", new { id = plugin.Id });
    }
}
