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
        if (string.IsNullOrWhiteSpace(redirectUris))
        {
            TempData["FlashError"] = "Bitte mindestens eine gültige Redirect-URI angeben.";
            return Page();
        }
        var created = await _clients.CreateAsync(name, redirectUris);
        // The secret exists only here — handed to the list page to be shown exactly once.
        TempData["NewClientId"] = created.Client.ClientId;
        TempData["NewSecret"] = created.ClientSecret;
        TempData["Flash"] = "Connector angelegt.";
        return RedirectToPage("Index");
    }
}
