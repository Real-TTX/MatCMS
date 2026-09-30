using MatCMS.Shared;

namespace MatCMS.Shared.Web;

/// <summary>
/// The statistics view — tiles, the daily chart and the top lists — shared by the site's own Admin → Statistik and
/// the cloud's statistics of that site, so both show the same figures the same way. The numbers come from
/// <see cref="StatsSummary.Build"/>; the wording is passed in as strings (a shared view cannot reach either
/// application's <c>Localizer</c>).
/// </summary>
/// <param name="Summary">The period's figures.</param>
/// <param name="L">Wording.</param>
/// <param name="SiteUrl">Base address the top pages link to (the cloud knows the site's address); null = relative
/// links, which is right on the site itself. No links at all for 404 paths — they lead nowhere by definition.</param>
/// <param name="Compact">Tiles and chart only — for a sum over several sites, whose top lists would mix paths of
/// different sites into one meaningless list.</param>
public sealed record SiteStats(StatsSummary Summary, SiteStatsLabels L, string? SiteUrl = null, bool Compact = false);

public sealed record SiteStatsLabels(
    string Views, string Visitors, string Bots, string NotFound, string ServerErrors,
    string VsPrevious,          // "{0} ggü. Vorzeitraum"
    string Chart, string TopPages, string Referrers, string NotFoundPages, string Devices,
    string Page, string Source, string Count, string Empty, string Other,
    string Desktop, string Mobile, string Tablet);
