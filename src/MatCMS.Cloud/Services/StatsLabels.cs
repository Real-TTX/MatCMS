using MatCMS.Shared.Web;

namespace MatCMS.Cloud.Services;

/// <summary>The wording of the shared statistics view (<c>_SiteStats</c>), looked up once for every cloud page
/// that shows it. The keys are the CMS's (<c>stats.*</c>), so both applications say the same thing.</summary>
public static class StatsLabels
{
    public static SiteStatsLabels For(Localizer T) => new(
        T["stats.views"], T["stats.visitors"], T["stats.bots"], T["stats.notFound"], T["stats.serverErrors"],
        T["stats.vsPrevious"], T["stats.chart"], T["stats.topPages"], T["stats.referrers"], T["stats.notFoundPages"],
        T["stats.devices"], T["stats.page"], T["stats.source"], T["stats.count"], T["stats.empty"], T["stats.other"],
        T["stats.desktop"], T["stats.mobile"], T["stats.tablet"]);
}
