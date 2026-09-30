using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services;
using MatCMS.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Stats;

/// <summary>Statistik: the visitor figures of every site this user may see, side by side, with the sum on top. A
/// site's details (top pages, referrers, 404s) are on its own Statistik tab — a list of paths across sites would
/// mean nothing. Operators see their own instances only.</summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly StatsService _stats;
    private readonly OperatorScope _scope;
    public IndexModel(AppDbContext db, StatsService stats, OperatorScope scope) { _db = db; _stats = stats; _scope = scope; }

    public int Days { get; private set; } = 30;
    public List<StatsService.FleetRow> Rows { get; private set; } = new();
    public StatsSummary Total { get; private set; } = null!;

    public async Task OnGetAsync(int? days)
    {
        Days = StatsService.NormalisePeriod(days);
        var q = _db.Instances.AsNoTracking().Where(i => i.Status == Models.InstanceStatus.Approved);
        if (!_scope.IsAdmin)
        {
            var allowed = await _scope.AllowedInstanceIdsAsync();
            q = q.Where(i => allowed.Contains(i.Id));
        }
        (Rows, Total) = await _stats.FleetAsync(await q.ToListAsync(), Days, HttpContext.RequestAborted);
    }
}
