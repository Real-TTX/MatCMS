using MatCMS.Services;
using MatCMS.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Stats;

/// <summary>Admin → Statistik: the site's visitor statistics for a period. Shows only — whether it counts at all and
/// how long it keeps the figures is set under Einstellungen → Protokoll &amp; Statistik.</summary>
public class IndexModel : PageModel
{
    private readonly StatsService _stats;
    private readonly SiteContext _site;
    public IndexModel(StatsService stats, SiteContext site) { _stats = stats; _site = site; }

    public int Days { get; private set; } = 30;
    public StatsSummary Summary { get; private set; } = null!;
    public bool Enabled => _site.Get(SettingKeys.StatsEnabled) != "0";

    public async Task OnGetAsync(int? days)
    {
        Days = days is int d && StatsService.Periods.Contains(d) ? d : 30;
        Summary = await _stats.SummaryAsync(Days, HttpContext.RequestAborted);
    }
}
