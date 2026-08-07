using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.About;

public class IndexModel : PageModel
{
    private readonly VersionService _vs;
    public IndexModel(VersionService vs) => _vs = vs;

    public string Current => _vs.Current;
    public string ImageRef => _vs.ImageRef;
    public string UpdateCommand => _vs.UpdateCommand;

    public void OnGet() { }

    // Called via AJAX from the page ("?handler=Check"); returns the GHCR update check as JSON.
    public async Task<IActionResult> OnGetCheckAsync(CancellationToken ct)
        => new JsonResult(await _vs.CheckAsync(ct));
}
