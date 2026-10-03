using MatCMS.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Attention;

/// <summary>Braucht Aufmerksamkeit — the dashboard card's full list (AttentionService) with what each line comes down
/// to, searchable and filtered by severity (?filter=err|warn|info).</summary>
public class IndexModel : PageModel
{
    private readonly AttentionService _attention;
    public IndexModel(AttentionService attention) => _attention = attention;

    public List<AttentionService.Item> Items { get; private set; } = new();

    public async Task OnGetAsync() => Items = await _attention.BuildAsync(HttpContext.RequestAborted);
}
