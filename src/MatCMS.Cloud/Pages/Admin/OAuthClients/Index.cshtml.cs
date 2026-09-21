using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.OAuthClients;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<OAuthClient> Items { get; private set; } = new();

    /// <summary>The just-created client id + secret, handed over by the create page via TempData and shown
    /// once. The secret is never stored in the clear, so this is the only moment it can be copied.</summary>
    public string? NewClientId { get; private set; }
    public string? NewSecret { get; private set; }

    /// <summary>This cloud's own base URL, so the page can show the ready-to-paste Authorization/Token URLs.</summary>
    public string BaseUrl { get; private set; } = "";

    public async Task OnGetAsync()
    {
        Items = await _db.OAuthClients.AsNoTracking().OrderByDescending(c => c.CreatedAt).ToListAsync();
        if (TempData["NewClientId"] is string cid) NewClientId = cid;
        if (TempData["NewSecret"] is string s) NewSecret = s;
        BaseUrl = $"{Request.Scheme}://{Request.Host}";
    }

    public async Task<IActionResult> OnPostRevokeAsync(int id)
    {
        var client = await _db.OAuthClients.FindAsync(id);
        if (client is null) return RedirectToPage();
        if (!client.Revoked)
        {
            client.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        TempData["Flash"] = "Connector widerrufen.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var client = await _db.OAuthClients.FindAsync(id);
        if (client is null) return RedirectToPage();
        _db.OAuthClients.Remove(client);
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Connector gelöscht.";
        return RedirectToPage();
    }
}
