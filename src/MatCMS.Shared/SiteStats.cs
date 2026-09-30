namespace MatCMS.Shared;

/// <summary>
/// Visitor statistics — the counter format both applications share. A site counts its own traffic into
/// daily counters (<c>day × kind × key → count</c>); the cloud receives those counters, never single requests.
/// <para>One generic shape instead of a table per figure: adding a figure later is a new kind, not a migration on
/// both sides and a protocol change. Days are ISO strings (<c>yyyy-MM-dd</c>, UTC) because they travel through
/// JSON and two SQLite databases and must compare as text.</para>
/// </summary>
public static class StatKinds
{
    /// <summary>Totals of the day; keys <see cref="Views"/>, <see cref="Visitors"/>, <see cref="Bots"/> and the
    /// status classes <c>s2</c>…<c>s5</c>.</summary>
    public const string Total = "t";
    /// <summary>Page views per path.</summary>
    public const string Path = "p";
    /// <summary>Visits arriving from another site, per host.</summary>
    public const string Referrer = "r";
    /// <summary>Requests answered 404, per path.</summary>
    public const string NotFound = "n";
    /// <summary>Page views per device class: desktop / mobile / tablet.</summary>
    public const string Device = "d";

    public const string Views = "views";
    public const string Visitors = "visitors";
    public const string Bots = "bots";

    /// <summary>Key under which several rare keys of one kind were folded together, so a day of 404 probes cannot
    /// grow the table without bound.</summary>
    public const string Other = "…";

    /// <summary>How many keys per kind and day travel to the cloud. The cloud shows top lists; the long tail
    /// stays on the site.</summary>
    public const int WireTopPerKind = 50;

    public static string StatusKey(int status) => "s" + Math.Clamp(status / 100, 1, 5);

    public static string DayOf(DateTime utc) => utc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static DateOnly ParseDay(string day) =>
        DateOnly.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>One counter on the wire.</summary>
public sealed class StatCount
{
    public string Kind { get; set; } = "";
    public string Key { get; set; } = "";
    public long Count { get; set; }
}

/// <summary>All counters of one day as the site has them NOW. The cloud replaces its copy of that day wholesale,
/// so sending a day twice is harmless — which is what makes a lost response or a restart safe.</summary>
public sealed class StatDayReport
{
    public string Day { get; set; } = "";
    public List<StatCount> Counts { get; set; } = new();
}

/// <summary>A counter row as either application stores it.</summary>
public readonly record struct StatRow(string Day, string Kind, string Key, long Count);

/// <summary>
/// What a statistics page shows for a period — computed from counter rows by ONE piece of code, so the site's own
/// page and the cloud's page for that site cannot disagree about what a number means.
/// </summary>
public sealed class StatsSummary
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public long Views { get; init; }
    public long Visitors { get; init; }
    public long Bots { get; init; }
    public long NotFound { get; init; }
    public long ServerErrors { get; init; }
    /// <summary>Views of the period before, same length — for the trend next to the figure. Null when unknown.</summary>
    public long? PreviousViews { get; init; }
    public long? PreviousVisitors { get; init; }
    /// <summary>Every day of the period, also days without traffic, so a chart has no silent gaps.</summary>
    public List<StatsDay> Days { get; init; } = new();
    public List<StatsEntry> TopPages { get; init; } = new();
    public List<StatsEntry> Referrers { get; init; } = new();
    public List<StatsEntry> NotFoundPages { get; init; } = new();
    public List<StatsEntry> Devices { get; init; } = new();

    public bool IsEmpty => Views == 0 && Bots == 0 && NotFound == 0;

    /// <summary>Builds the summary for <paramref name="from"/>…<paramref name="to"/> (inclusive). Rows outside the
    /// period are used only for the previous-period comparison.</summary>
    public static StatsSummary Build(IEnumerable<StatRow> rows, DateOnly from, DateOnly to, int top = 20)
    {
        var len = to.DayNumber - from.DayNumber + 1;
        var prevFrom = from.AddDays(-len);
        var inRange = new List<StatRow>();
        long prevViews = 0, prevVisitors = 0;
        var sawPrevious = false;
        foreach (var r in rows)
        {
            if (!DateOnly.TryParseExact(r.Day, "yyyy-MM-dd", out var d)) continue;
            if (d >= from && d <= to) inRange.Add(r);
            else if (d >= prevFrom && d < from && r.Kind == StatKinds.Total)
            {
                sawPrevious = true;
                if (r.Key == StatKinds.Views) prevViews += r.Count;
                else if (r.Key == StatKinds.Visitors) prevVisitors += r.Count;
            }
        }

        long Total(string key) => inRange.Where(r => r.Kind == StatKinds.Total && r.Key == key).Sum(r => r.Count);
        List<StatsEntry> Top(string kind) => inRange.Where(r => r.Kind == kind)
            .GroupBy(r => r.Key).Select(g => new StatsEntry(g.Key, g.Sum(r => r.Count)))
            // The folded remainder is listed last whatever its size: it is not a page.
            .OrderBy(e => e.Key == StatKinds.Other).ThenByDescending(e => e.Count).ThenBy(e => e.Key, StringComparer.Ordinal)
            .Take(top).ToList();

        var perDay = inRange.Where(r => r.Kind == StatKinds.Total)
            .GroupBy(r => r.Day)
            .ToDictionary(g => g.Key, g => (Views: g.Where(r => r.Key == StatKinds.Views).Sum(r => r.Count),
                                             Visitors: g.Where(r => r.Key == StatKinds.Visitors).Sum(r => r.Count)));
        var days = new List<StatsDay>(len);
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var key = d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var v = perDay.TryGetValue(key, out var x) ? x : default;
            days.Add(new StatsDay(d, v.Views, v.Visitors));
        }

        return new StatsSummary
        {
            From = from, To = to,
            Views = Total(StatKinds.Views),
            Visitors = Total(StatKinds.Visitors),
            Bots = Total(StatKinds.Bots),
            NotFound = inRange.Where(r => r.Kind == StatKinds.NotFound).Sum(r => r.Count),
            ServerErrors = Total(StatKinds.StatusKey(500)),
            PreviousViews = sawPrevious ? prevViews : null,
            PreviousVisitors = sawPrevious ? prevVisitors : null,
            Days = days,
            TopPages = Top(StatKinds.Path),
            Referrers = Top(StatKinds.Referrer),
            NotFoundPages = Top(StatKinds.NotFound),
            Devices = Top(StatKinds.Device),
        };
    }
}

public readonly record struct StatsDay(DateOnly Day, long Views, long Visitors);
public readonly record struct StatsEntry(string Key, long Count);
