using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace MatCMS.Pages.Admin.Components;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CloudCatalogService _catalog;
    public IndexModel(AppDbContext db, CloudCatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    /// <summary>Catalogue of the connected cloud, fetched only on demand (?browse=true).</summary>
    public StoreCatalog? Catalog { get; private set; }
    public bool CloudConnected { get; private set; }
    public string? CatalogError { get; private set; }

    /// <summary>The catalogue shaped for the store dialog. Built here rather than in the view so
    /// the "already installed?" lookup stays out of the markup.</summary>
    public Shared.StoreDialog StoreDialog => new(
        TitleKey: "components.cloudCatalog",
        IntroKey: "components.cloudIntro",
        RouteName: "type",
        Items: (Catalog?.Components ?? []).Select(c => new Shared.StoreItem(
            Title: c.Name,
            Sub: c.Type,
            Description: c.Description,
            RouteValue: c.Type,
            InstalledVersion: Items.Any(i => i.Type == c.Type) ? "" : null)).ToList(),
        Error: CatalogError);

    public async Task<IActionResult> OnPostInstallFromCloudAsync(string type)
    {
        var (ok, message) = await _catalog.InstallComponentAsync(type, HttpContext.RequestAborted);
        TempData[ok ? "Flash" : "FlashError"] = message;
        return RedirectToPage(new { browse = true });
    }

    /// <summary>User-defined components ("Eigene Komponenten").</summary>
    public List<Component> Items { get; private set; } = new();

    /// <summary>Built-in block types ("Systemkomponenten"), shown read-only.</summary>
    public IReadOnlyList<BlockDefinition> System { get; private set; } = BlockRegistry.Builtins;

    public async Task OnGetAsync(bool browse = false)
    {
        Items = await _db.Components.OrderBy(c => c.Name).ToListAsync();
        CloudConnected = await _catalog.IsAvailableAsync();
        if (browse && CloudConnected)
            (Catalog, CatalogError) = await _catalog.GetCatalogAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var c = await _db.Components.FindAsync(id);
        if (c is not null)
        {
            _db.Components.Remove(c);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Komponente gelöscht.";
        }
        return RedirectToPage();
    }

    /// <summary>
    /// Takes the JSON a component editor exports — this one or a cloud profile's. The TYPE is the
    /// identity, because that is what a {{placeholder}} block on an existing page refers to:
    /// importing a type that already exists has to update it, or the blocks already placed would
    /// point at the older of two components.
    /// </summary>
    public async Task<IActionResult> OnPostImportAsync(string? componentJson)
    {
        using var doc = JsonImport.TryParse(componentJson);
        if (doc is null)
        {
            TempData["FlashError"] = "Bitte gültiges Komponenten-JSON einfügen.";
            return RedirectToPage();
        }

        var root = doc.RootElement;
        var type = JsonImport.Text(root, "Type").Trim().ToLowerInvariant();
        var name = JsonImport.Text(root, "Name").Trim();
        if (type.Length == 0 || name.Length == 0)
        {
            TempData["FlashError"] = "Im JSON fehlen Typ oder Name.";
            return RedirectToPage();
        }

        var row = await _db.Components.FirstOrDefaultAsync(c => c.Type == type);
        if (row is null)
        {
            row = new Component { Type = type };
            _db.Components.Add(row);
        }
        row.Name = name;
        row.Description = JsonImport.Text(root, "Description");
        row.Icon = JsonImport.Text(root, "Icon");
        row.FieldsJson = JsonImport.Raw(root, "FieldsJson", "[]");
        row.TemplateHtml = JsonImport.Text(root, "TemplateHtml");

        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Komponente \"{row.Name}\" importiert.";
        return RedirectToPage();
    }
}
