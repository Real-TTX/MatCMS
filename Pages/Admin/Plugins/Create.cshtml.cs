using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PagesIndex = MatCMS.Pages.Admin.Pages.IndexModel;

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
        "// Eigene Dateien aus dem Plugin-Ordner (unter „Dateien dieses Plugins\" hochladen):\n" +
        "// IncludeScript(\"app.js\");     // lädt /plugin-assets/<key>/app.js auf allen Seiten\n" +
        "// IncludeStyle(\"style.css\");   // lädt eine CSS-Datei im <head>\n" +
        "// var url = AssetUrl(\"logo.png\"); // URL einer Asset-Datei\n\n" +
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

        // Stable, unique slug key — names the plugin's own asset folder.
        var baseKey = PagesIndex.Slugify(name);
        if (string.IsNullOrEmpty(baseKey)) baseKey = "plugin";
        var key = baseKey;
        var n = 2;
        while (await _db.Plugins.AnyAsync(p => p.Key == key)) key = baseKey + "-" + n++;

        var plugin = new MatCMS.Models.Plugin { Name = name, Key = key, Code = Starter, Enabled = false };
        _db.Plugins.Add(plugin);
        await _db.SaveChangesAsync();
        return RedirectToPage("Edit", new { id = plugin.Id });
    }
}
