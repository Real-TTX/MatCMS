using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.ApiKeys;

/// <summary>What <c>_ApiKeyList.cshtml</c> renders on Einstellungen → API.</summary>
/// <param name="NewKey">The just-created raw key, handed over via TempData and shown ONCE — it is never stored
/// in the clear, so this is the only moment it can be copied.</param>
public sealed record ApiKeyListView(List<ApiKey> Items, string? NewKey);

/// <summary>
/// The API keys moved into the settings (tab "API"). This page stays as the address the row actions post to
/// and as a forward for old links and bookmarks — a GET lands on the tab, with the one-time key (TempData)
/// still unread so the tab can show it.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    private IActionResult Tab() => RedirectToPage("/Admin/Settings/Index", new { tab = "api" });

    public IActionResult OnGet() => Tab();

    /// <summary>Turns a key off without deleting it — a key that could restore a live site is worth
    /// keeping in the list as a record that it existed and when it was stopped.</summary>
    public async Task<IActionResult> OnPostRevokeAsync(int id)
    {
        var key = await _db.ApiKeys.FindAsync(id);
        if (key is null) return Tab();
        if (!key.Revoked)
        {
            key.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        TempData["Flash"] = "Schlüssel widerrufen.";
        return Tab();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var key = await _db.ApiKeys.FindAsync(id);
        if (key is null) return Tab();
        _db.ApiKeys.Remove(key);   // scope rows cascade with it
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Schlüssel gelöscht.";
        return Tab();
    }
}
