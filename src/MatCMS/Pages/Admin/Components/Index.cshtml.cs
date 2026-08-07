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
}
