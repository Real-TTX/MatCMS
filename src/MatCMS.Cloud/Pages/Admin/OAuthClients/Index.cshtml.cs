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
        if (!client.Revoked) client.RevokedAt = DateTime.UtcNow;
        var keys = await RevokeMintedKeysAsync(client.Id);
        await _db.SaveChangesAsync();
        TempData["Flash"] = keys == 0 ? "Connector widerrufen." : $"Connector widerrufen, {keys} damit ausgestellte Schlüssel gesperrt.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var client = await _db.OAuthClients.FindAsync(id);
        if (client is null) return RedirectToPage();
        var keys = await RevokeMintedKeysAsync(client.Id);
        _db.OAuthClients.Remove(client);
        await _db.SaveChangesAsync();
        TempData["Flash"] = keys == 0 ? "Connector gelöscht." : $"Connector gelöscht, {keys} damit ausgestellte Schlüssel gesperrt.";
        return RedirectToPage();
    }

    // Every sign-in through a connector mints a full-access operator key; taking the connector away must take those keys
    // away too, or "widerrufen" only stops NEW sign-ins while the client keeps its access. Revoked, not deleted: a key
    // that could restore a site keeps its row as a trail (API-Schlüssel shows it). Keys minted before keys remembered their
    // client carry only the client's name in their own name ("Connector: {name} · {date}", no OAuthClientId) — those are
    // matched by that name, which only the token endpoint writes.
    private async Task<int> RevokeMintedKeysAsync(int clientId)
    {
        var client = await _db.OAuthClients.AsNoTracking().FirstAsync(c => c.Id == clientId);
        var legacyPrefix = $"Connector: {client.Name} · ";
        var minted = await _db.ApiKeys.Where(k => k.RevokedAt == null
            && (k.OAuthClientId == clientId || (k.OAuthClientId == null && k.Name.StartsWith(legacyPrefix)))).ToListAsync();
        foreach (var k in minted) k.RevokedAt = DateTime.UtcNow;
        return minted.Count;
    }
}
