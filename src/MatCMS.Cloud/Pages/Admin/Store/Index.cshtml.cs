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
    public List<Shared.PayloadTile> PluginTiles =>
        Plugins.Select(p => new Shared.PayloadTile(
            Url.Page("Plugin", new { id = p.Id })!, p.Name, $"{p.Key} · {p.Version}", false,
            $"{p.Name} {p.Key} {p.Description}")).ToList();

    public List<Shared.PayloadTile> TemplateTiles =>
        Templates.Select(t => new Shared.PayloadTile(
            Url.Page("Template", new { id = t.Id })!, t.Name, $"{t.HeadingFont} / {t.BodyFont}", false,
            $"{t.Name} {t.Description}", Accent: t.AccentColor)).ToList();

    public List<Shared.PayloadTile> ComponentTiles =>
        Components.Select(c => new Shared.PayloadTile(
            Url.Page("Component", new { id = c.Id })!, c.Name, c.Type, false,
            $"{c.Name} {c.Type} {c.Description}")).ToList();
}
