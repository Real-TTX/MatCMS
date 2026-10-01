using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.Attention;

/// <summary>Braucht Aufmerksamkeit — the dashboard card's full list (AttentionService), with search and a filter by
/// severity; ?filter=err|warn|info presets it. Operators see their own instances and no fleet lines.</summary>
public class IndexModel : PageModel
{
    private readonly AttentionService _attention;
    private readonly OperatorScope _scope;
    public IndexModel(AttentionService attention, OperatorScope scope) { _attention = attention; _scope = scope; }

    public List<AttentionService.Item> Items { get; private set; } = new();

    public async Task OnGetAsync() =>
        Items = await _attention.BuildAsync(_scope.IsAdmin ? null : await _scope.AllowedInstanceIdsAsync(), fleet: _scope.IsAdmin, HttpContext.RequestAborted);
}
