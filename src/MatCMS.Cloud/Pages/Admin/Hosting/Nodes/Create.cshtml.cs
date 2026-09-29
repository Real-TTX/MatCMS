using MatCMS.Cloud.Services.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.Hosting.Nodes;

/// <summary>Registers a node. Asks only for the name; the token and the start command are shown ONCE on the
/// node's page afterwards (TempData), everything else is edited there.</summary>
public class CreateModel : PageModel
{
    private readonly NodeService _nodes;
    public CreateModel(NodeService nodes) => _nodes = nodes;

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string? name)
    {
        var (node, token, err) = await _nodes.CreateAsync(name, HttpContext.RequestAborted);
        if (node is null)
        {
            TempData["FlashError"] = err;
            return RedirectToPage();
        }
        TempData["NodeCommand"] = _nodes.AgentCommand(node, token!);
        TempData["Flash"] = $"Node „{node.Name}“ angelegt — jetzt den Agent auf dem Host starten.";
        return RedirectToPage("Details", new { id = node.Id });
    }
}
