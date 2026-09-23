using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.OAuthClients;

/// <summary>
/// Edit an existing connector: change its label and — the point of the page — add the redirect URI that
/// ChatGPT only reveals after the action is saved. The client id and secret are deliberately not editable;
/// the secret was shown once at creation and re-creating the client would rotate it.
/// </summary>
public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly OAuthClientService _clients;
    public EditModel(AppDbContext db, OAuthClientService clients) { _db = db; _clients = clients; }

    public OAuthClient Client { get; private set; } = default!;
    public string BaseUrl { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var c = await _db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return RedirectToPage("Index");
        Client = c;
        BaseUrl = $"{Request.Scheme}://{Request.Host}";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, string name, string redirectUris)
    {
        if (!await _clients.UpdateAsync(id, name, redirectUris))
            return RedirectToPage("Index");
        TempData["Flash"] = "Connector aktualisiert.";
        return RedirectToPage("Index");
    }
}
