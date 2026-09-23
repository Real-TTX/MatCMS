using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.OAuthClients;

public class CreateModel : PageModel
{
    private readonly OAuthClientService _clients;
    public CreateModel(OAuthClientService clients) => _clients = clients;

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string name, string redirectUris)
    {
        // Redirect URI may be left empty on purpose: a ChatGPT connector only learns its callback URL after
        // the action is saved in ChatGPT, so the operator adds it afterwards via Edit. See _ConnectorHelp.
        var created = await _clients.CreateAsync(name, redirectUris ?? "");
        // The secret exists only here — handed to the list page to be shown exactly once.
        TempData["NewClientId"] = created.Client.ClientId;
        TempData["NewSecret"] = created.ClientSecret;
        TempData["Flash"] = "Connector angelegt.";
        return RedirectToPage("Index");
    }
}
