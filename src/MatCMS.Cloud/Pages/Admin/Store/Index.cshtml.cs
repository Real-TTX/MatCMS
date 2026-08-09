using MatCMS.Shared;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Store;

/// <summary>
/// The global store: everything that exists independently of a profile. Profiles pick from here, and
/// instances can browse it directly. One tabbed page with a list per type; each item is edited on its
/// own page, same as everywhere else.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<StorePlugin> Plugins { get; private set; } = new();
    public List<StoreTemplate> Templates { get; private set; } = new();
    public List<StoreComponent> Components { get; private set; } = new();

    /// <summary>How many profiles use each entry — an operator changing something shared should see
    /// how far it reaches before they save.</summary>
    public Dictionary<int, int> PluginUse { get; private set; } = new();
    public Dictionary<int, int> TemplateUse { get; private set; } = new();
    public Dictionary<int, int> ComponentUse { get; private set; } = new();

    public async Task OnGetAsync()
    {
        // Bundles are never loaded for the listing — a store with a few plugins would otherwise drag
        // megabytes through memory on every page view.
        Plugins = await _db.StorePlugins.AsNoTracking()
            .Select(p => new StorePlugin
            {
                Id = p.Id, Key = p.Key, Name = p.Name, Version = p.Version,
                Description = p.Description, UploadedAt = p.UploadedAt
            })
            .OrderBy(p => p.Name).ToListAsync();

        Templates = await _db.StoreTemplates.AsNoTracking()
            .Select(t => new StoreTemplate
            {
                Id = t.Id, Name = t.Name, Description = t.Description,
                AccentColor = t.AccentColor, HeadingFont = t.HeadingFont, BodyFont = t.BodyFont
            })
            .OrderBy(t => t.Name).ToListAsync();

        Components = await _db.StoreComponents.AsNoTracking().OrderBy(c => c.Name).ToListAsync();

        PluginUse = await CountAsync(_db.ProfileStorePlugins.Select(x => x.StorePluginId));
        TemplateUse = await CountAsync(_db.ProfileStoreTemplates.Select(x => x.StoreTemplateId));
        ComponentUse = await CountAsync(_db.ProfileStoreComponents.Select(x => x.StoreComponentId));
    }

    private static async Task<Dictionary<int, int>> CountAsync(IQueryable<int> ids) =>
        await ids.GroupBy(id => id).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

    public int Uses(Dictionary<int, int> map, int id) => map.TryGetValue(id, out var c) ? c : 0;

    // Tile views. Everything in the store IS global, so no origin badge here — the flag exists for
    // the profile lists, where own and taken entries sit in one table.
    public Shared.PayloadTileList PluginTiles => new(
        Plugins.Select(p => new Shared.PayloadTile(
            Url.Page("Plugin", new { id = p.Id })!, p.Name, $"{p.Key} · {p.Version}", false,
            $"{p.Name} {p.Key} {p.Description}")).ToList(), "store.noPlugins");

    public Shared.PayloadTileList TemplateTiles => new(
        Templates.Select(t => new Shared.PayloadTile(
            Url.Page("Template", new { id = t.Id })!, t.Name, $"{t.HeadingFont} / {t.BodyFont}", false,
            $"{t.Name} {t.Description}", Accent: t.AccentColor,
            PreviewUrl: Url.Page("/Admin/TemplatePreview", new { kind = "store", id = t.Id }))).ToList(), "store.noTemplates");

    public Shared.PayloadTileList ComponentTiles => new(
        Components.Select(c => new Shared.PayloadTile(
            Url.Page("Component", new { id = c.Id })!, c.Name, c.Type, false,
            $"{c.Name} {c.Type} {c.Description}",
            PreviewUrl: Url.Page("/Admin/ComponentPreview", new { kind = "store", id = c.Id }))).ToList(),
        "store.noComponents");

    /// <summary>Imports a component into the store. <c>Type</c> is the identity, so re-importing
    /// updates the entry every profile that selected it already points at.</summary>
    public async Task<IActionResult> OnPostImportComponentAsync(string? componentJson)
    {
        using var doc = JsonImport.TryParse(componentJson);
        if (doc is null)
        {
            TempData["FlashError"] = "Bitte gültiges Komponenten-JSON einfügen.";
            return RedirectToPage(new { tab = "components" });
        }

        var root = doc.RootElement;
        var type = JsonImport.Text(root, "Type").Trim().ToLowerInvariant();
        var name = JsonImport.Text(root, "Name").Trim();
        if (type.Length == 0 || name.Length == 0)
        {
            TempData["FlashError"] = "Im JSON fehlen Typ oder Name.";
            return RedirectToPage(new { tab = "components" });
        }

        var row = await _db.StoreComponents.FirstOrDefaultAsync(c => c.Type == type);
        if (row is null)
        {
            row = new StoreComponent { Type = type };
            _db.StoreComponents.Add(row);
        }
        row.Name = name;
        row.Description = JsonImport.Text(root, "Description");
        row.Icon = JsonImport.Text(root, "Icon");
        row.FieldsJson = JsonImport.Raw(root, "FieldsJson", "[]");
        row.TemplateHtml = JsonImport.Text(root, "TemplateHtml");

        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Komponente \"{row.Name}\" in den Store importiert.";
        return RedirectToPage(new { tab = "components" });
    }
}
