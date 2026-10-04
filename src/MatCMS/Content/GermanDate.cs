namespace MatCMS.Content;

/// <summary>
/// German dates for public pages. The app runs with InvariantGlobalization, so "de-DE" exists but
/// carries invariant data — <c>ToString("dd. MMMM yyyy")</c> printed "04. October 2026" on every post.
/// Posts are German-only (<c>Post.Locale</c> is always "de"), so the names are spelled out here.
/// </summary>
public static class GermanDate
{
    private static readonly string[] Months =
        ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"];

    /// <summary>"04. Oktober 2026".</summary>
    public static string Long(DateTime d) => $"{d.Day:00}. {Months[d.Month - 1]} {d.Year}";
}
