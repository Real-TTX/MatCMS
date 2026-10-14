using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
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

    internal const string Starter =
        "// Mein Plugin — geschrieben gegen die Plugin-API, dieselbe wie bei nativen Plugins:\n" +
        "// Plugin.Route · Plugin.Admin.Section · Plugin.Block · Plugin.Settings · Plugin.Data · Plugin.Job · Plugin.On · Plugin.Strings\n" +
        "// Weitere Bausteine einfügen: „Blaupause“ im Dateibaum.\n" +
        "Plugin.Admin.Section(\"Mein Plugin\", \"ti-plug\")\n" +
        "    .Page(\"start\", \"Start\", ctx => PluginResult.Page(\n" +
        "        Ui.Stack(\n" +
        "            Ui.PageHead(\"Was dieses Plugin hier tut.\"),\n" +
        "            Ui.Card(\"Hallo\", Ui.Text(\"Dieses Plugin läuft. Der Code steht links in plugin.csx.\"))),\n" +
        "        \"Mein Plugin\"));\n\n" +
        "// Ältere Schreibweise (läuft weiter): AddAdminMenu(label, url, icon), Service<T>()\n" +
        "// Eigene Dateien aus dem Plugin-Ordner (unter „Dateien dieses Plugins\" hochladen):\n" +
        "// IncludeScript(\"app.js\");     // lädt /plugin-assets/<key>/app.js auf allen Seiten\n" +
        "// IncludeStyle(\"style.css\");   // lädt eine CSS-Datei im <head>\n" +
        "// var url = AssetUrl(\"logo.png\"); // URL einer Asset-Datei\n\n" +
        "// Konfiguration (unter „Konfiguration\" Schlüssel/Wert pflegen, ohne den Code zu ändern):\n" +
        "// var apiKey = Config(\"apiKey\");\n\n" +
        "// Einmalige Datenmigration bei Updates (läuft je Version genau einmal):\n" +
        "// Migrate(\"2\", () => { /* gespeicherte Daten aufs neue Format bringen */ });\n\n" +
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

        // Die Rollenkarte wird AUSDRÜCKLICH gesetzt, nicht leer gelassen: leer heißt „nie geschrieben“
        // und wird als Bestand gelesen. Ein neues Plugin fängt mit einem Assets-Ordner an — er heißt
        // „assets“, weil man irgendwo anfangen muss, und darf umbenannt werden wie jeder andere.
        var plugin = new MatCMS.Models.Plugin
        {
            Name = name, Key = key, Code = Starter, Enabled = false,
            MappingJson = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string> { [PluginMapping.LegacyAssetFolder] = PluginMapping.RoleAssets })
        };
        _db.Plugins.Add(plugin);
        await _db.SaveChangesAsync();
        return RedirectToPage("Edit", new { id = plugin.Id });
    }
}
