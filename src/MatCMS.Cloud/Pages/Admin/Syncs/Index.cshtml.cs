using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.Syncs;

/// <summary>Abgleiche — how the instances applied their profiles, newest first, across every visible instance: the
/// dashboard card's full list, with search and a filter by outcome (?filter=error|failed|changes|nochange).</summary>
public class IndexModel : PageModel
{
    private readonly SyncHistoryService _history;
    private readonly OperatorScope _scope;
    public IndexModel(SyncHistoryService history, OperatorScope scope) { _history = history; _scope = scope; }

    /// <summary>Each instance keeps its newest 50 runs; this shows the newest of all of them.</summary>
    public const int Show = 500;
    public List<InstanceSyncRun> Runs { get; private set; } = new();

    public async Task OnGetAsync() =>
        Runs = await _history.ListAsync(_scope.IsAdmin ? null : await _scope.AllowedInstanceIdsAsync(), Show, HttpContext.RequestAborted);
}
