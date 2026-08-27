using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.ApiKeys;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<ApiKey> Items { get; private set; } = new();

    /// <summary>The just-created raw key, handed over by the create page via TempData and shown once.
    /// Never stored in the clear, so this is the only moment it can be copied.</summary>
    public string? NewKey { get; private set; }

    public async Task OnGetAsync()
    {
        Items = await _db.ApiKeys.Include(k => k.Instances)
            .AsNoTracking()
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();
        if (TempData["NewApiKey"] is string nk && !string.IsNullOrWhiteSpace(nk)) NewKey = nk;
    }

    /// <summary>Turns a key off without deleting it — a key that could restore a live site is worth
    /// keeping in the list as a record that it existed and when it was stopped.</summary>
    public async Task<IActionResult> OnPostRevokeAsync(int id)
    {
        var key = await _db.ApiKeys.FindAsync(id);
        if (key is null) return RedirectToPage();
        if (!key.Revoked)
        {
            key.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        TempData["Flash"] = "Schlüssel widerrufen.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var key = await _db.ApiKeys.FindAsync(id);
        if (key is null) return RedirectToPage();
        _db.ApiKeys.Remove(key);   // scope rows cascade with it
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Schlüssel gelöscht.";
        return RedirectToPage();
    }
}
