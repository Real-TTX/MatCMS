using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Verein;

/// <summary>One entry of the calendar view: an event, a meeting, a birthday or the anniversary of joining the club.</summary>
/// <param name="Type">One of <see cref="VereinCalendar.Types"/>.</param>
/// <param name="Start">"yyyy-MM-dd" or "yyyy-MM-ddTHH:mm" (local time as typed).</param>
/// <param name="End">The last day ("yyyy-MM-dd") of an all-day entry, the end time of a timed one, or "".</param>
/// <param name="Highlight">A round anniversary / birthday — worth a congratulation.</param>
/// <param name="Note">Draft / internal: shown with the entry, so nobody takes a draft for a date.</param>
internal sealed record CalItem(string Type, string Start, string End, string Title, string Place, string Url, bool Cancelled, bool Highlight, string Note)
{
    public string Day => Start.Length >= 10 ? Start[..10] : Start;
    public string Time => Start.Length >= 16 && Start[11..16] != "00:00" ? Start[11..16] : "";
}

/// <summary>
/// The calendar view of the club's dates (2026-11 order: „Kalenderansicht mit Filter nach Typ“): events, meetings, birthdays and membership anniversaries
/// in one month view and one list, filtered by type. Admin → Verein → Kalender draws it with MatUi's schedule (<c>.mat-schedule</c>, month and agenda;
/// matui.js is loaded in the back office); the member area (<c>/mitglied/kalender</c>, the public site never loads MatUi) draws a month grid and a list
/// of its own, server-side, in the member area's look. What a member sees follows the member area's rules (<see cref="VereinPublic"/>): no drafts,
/// meetings only of their audience, birthdays only where the club shows dates of birth, anniversaries of joining only to the board.
/// Personal data (birthdays, names) never reaches an iCal feed: the feeds stay events (and, in a member's own secret feed, their meetings).
/// </summary>
internal static class VereinCalendar
{
    public const string Event = "termine", Meeting = "sitzungen", Birthday = "geburtstage", Entry = "eintritte";
    public static readonly string[] Types = { Event, Meeting, Birthday, Entry };

    /// <summary>The type asked for in the address (<c>?typ=</c>); "" = all.</summary>
    public static string TypeOf(IPluginContext ctx) => Types.Contains(ctx.Request.Q("typ")) ? ctx.Request.Q("typ") : "";

    public static string TypeLabel(IPluginContext ctx, string type) => type switch
    {
        Event => ctx.T("verein.cal.type.events"),
        Meeting => ctx.T("verein.cal.type.meetings"),
        Birthday => ctx.T("verein.cal.type.birthdays"),
        Entry => ctx.T("verein.cal.type.entries"),
        _ => ctx.T("verein.cal.type.all")
    };

    private static string OneLabel(IPluginContext ctx, string type) => type switch
    {
        Event => ctx.T("verein.cal.one.event"),
        Meeting => ctx.T("verein.cal.one.meeting"),
        Birthday => ctx.T("verein.cal.one.birthday"),
        _ => ctx.T("verein.cal.one.entry")
    };

    // ---- the entries (pure) -----------------------------------------------------------------------------------------

    /// <summary>The day a yearly date falls on in <paramref name="year"/> (29 February → 28 February in a common year).</summary>
    internal static DateTime? OnYear(string date, int year)
    {
        if (date.Length < 10 || !DateTime.TryParseExact(date[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;
        return new DateTime(year, d.Month, d.Month == 2 && d.Day == 29 && !DateTime.IsLeapYear(year) ? 28 : d.Day);
    }

    /// <summary>
    /// Every entry from <paramref name="from"/> to <paramref name="to"/> (days, inclusive) of the types wanted. Birthdays and anniversaries repeat every
    /// year of the window; a birthday says the age, an anniversary the years (the day of joining itself is "Eintritt").
    /// </summary>
    internal static List<CalItem> Items(IPluginContext ctx, string type, DateTime from, DateTime to, IEnumerable<Event> events, IEnumerable<Meeting> meetings,
        IEnumerable<Person> birthdays, IEnumerable<Person> entries, Func<Event, string> eventUrl, Func<Meeting, string> meetingUrl, Func<Person, string> personUrl)
    {
        var list = new List<CalItem>();
        var f = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var t = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool In(string day, string last) => string.CompareOrdinal(day, t) <= 0 && string.CompareOrdinal(last.Length >= 10 ? last[..10] : day, f) >= 0;
        if (type is "" or Event)
            foreach (var e in events)
            {
                if (!V.TryParse(e.Starts, out _, out _)) continue;
                var last = e.Ends.Length >= 10 && string.CompareOrdinal(e.Ends[..10], e.Day) >= 0 ? e.Ends : e.Day;
                if (!In(e.Day, last)) continue;
                var note = e.Draft ? ctx.T("verein.cal.draft") : e.Public ? "" : ctx.T("verein.cal.internal");
                list.Add(new CalItem(Event, e.Starts, e.Ends, e.Title, e.Place, eventUrl(e), e.Cancelled, false, note));
            }
        if (type is "" or Meeting)
            foreach (var m in meetings)
            {
                if (!V.TryParse(m.Starts, out _, out _)) continue;
                var day = m.Starts.Length >= 10 ? m.Starts[..10] : m.Starts;
                if (!In(day, day)) continue;
                list.Add(new CalItem(Meeting, m.Starts, "", m.Title, m.Place, meetingUrl(m), m.Status == VereinStore.Cancelled, false, ""));
            }
        for (var year = from.Year; year <= to.Year; year++)
        {
            if (type is "" or Birthday)
                foreach (var p in birthdays)
                {
                    if (OnYear(p.Born, year) is not { } d || d < from.Date || d > to.Date) continue;
                    var age = year - int.Parse(p.Born[..4], CultureInfo.InvariantCulture);
                    if (age <= 0) continue;
                    list.Add(new CalItem(Birthday, d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "", ctx.T("verein.cal.birthdayOf", p.Name, age), "", personUrl(p), false, age % 10 == 0, ""));
                }
            if (type is "" or Entry)
                foreach (var p in entries)
                {
                    if (OnYear(p.Joined, year) is not { } d || d < from.Date || d > to.Date) continue;
                    var years = year - int.Parse(p.Joined[..4], CultureInfo.InvariantCulture);
                    if (years < 0) continue;
                    var title = years == 0 ? ctx.T("verein.cal.joined", p.Name) : ctx.T("verein.cal.yearsIn", p.Name, years);
                    list.Add(new CalItem(Entry, d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "", title, "", personUrl(p), false, years > 0 && (years % 10 == 0 || years == 25), ""));
                }
        }
        // in the order of the day; on one day the timed ones by their time, the all-day ones (birthdays …) first
        return list.OrderBy(i => i.Day, StringComparer.Ordinal).ThenBy(i => i.Time, StringComparer.Ordinal).ThenBy(i => i.Title, StringComparer.CurrentCulture).ToList();
    }

    // ---- the back office (Admin → Verein → Kalender) -------------------------------------------------------------------

    public const string AdminPath = "calendar";

    public static async Task<PluginResult> AdminAsync(IPluginContext ctx, CancellationToken ct)
    {
        var type = TypeOf(ctx);
        var store = new VereinStore(ctx.Data);
        var today = V.Local(DateTime.UtcNow).Date;
        // birthdays and anniversaries repeat: the window is last year, this year and the next two (the schedule pages through it without asking again)
        var from = new DateTime(today.Year - 1, 1, 1);
        var to = new DateTime(today.Year + 2, 12, 31);
        var people = (await store.PeopleAsync(ct)).Where(p => p.Active).ToList();
        var items = Items(ctx, type, from, to, await store.EventsAsync(ct), await store.MeetingsAsync(ct), people, people.Where(p => !p.Child),
            e => VereinAdmin.Root + "events/" + e.Id, m => VereinAdmin.Root + "meetings/" + m.Id, p => VereinAdmin.Root + (p.Child ? "children/" : "members/") + p.Id);

        var json = new JsonArray();
        var n = 0;
        foreach (var i in items) json.Add(Json(ctx, i, ++n));
        // the text inside the root is for a reader without script: matui.js replaces it with the schedule
        var fallback = new StringBuilder("<ul>");
        foreach (var i in items.Where(i => string.CompareOrdinal(i.Day, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) >= 0).Take(30))
            fallback.Append("<li><a href=\"").Append(V.H(i.Url)).Append("\">").Append(V.H(V.Date(ctx, i.Day) + " · " + i.Title)).Append("</a></li>");
        fallback.Append("</ul>");
        var schedule = "<section class=\"mat-schedule verein-cal\" data-schedule data-view=\"month\" data-views=\"month agenda week\" data-agenda=\"month\" data-week-numbers data-date=\"" +
                       today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\" data-locale=\"" + (V.En(ctx) ? "en-GB" : "de-DE") + "\" aria-label=\"" + V.H(ctx.T("verein.cal.title")) +
                       "\" data-empty-title=\"" + V.H(ctx.T("verein.cal.emptyTitle")) + "\" data-empty=\"" + V.H(ctx.T("verein.cal.emptyText")) + "\" data-events=\"" + V.H(json.ToJsonString()) + "\">" + fallback + "</section>";

        var origin = VereinMail.Origin(ctx);
        var feeds = Ui.Card(ctx.T("verein.cal.feeds"), Ui.Stack(
            Ui.Muted(ctx.T("verein.cal.feedsHint")),
            Ui.KeyValue((ctx.T("verein.cal.feedPublic"), origin + VereinIcal.PublicFeed)),
            Ui.Button(ctx.T("verein.cal.feedMore"), VereinExport.Url("kalender"), UiButtonKind.Ghost, "ti-calendar-plus", small: true)));

        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("verein.cal.title"),
                Ui.Button(ctx.T("verein.cal.newEvent"), VereinAdmin.Root + "events/new", UiButtonKind.Ghost, "ti-calendar-plus", small: true)),
            Ui.Tabs(new[] { "" }.Concat(Types).Select(x => Ui.Tab(TypeLabel(ctx, x), VereinAdmin.Root + AdminPath + (x.Length > 0 ? "?typ=" + x : ""), x == type)).ToArray()),
            Ui.Card(Ui.Html(schedule)),
            feeds), ctx.T("verein.cal.title"));
    }

    /// <summary>One entry as MatUi's schedule reads it. The type is the pill (status) — the tone only tells cancelled, draft and round days apart (an icon and words, never colour alone).</summary>
    private static JsonObject Json(IPluginContext ctx, CalItem i, int n)
    {
        var o = new JsonObject { ["id"] = i.Type + "-" + n, ["title"] = i.Title, ["start"] = i.Start, ["status"] = OneLabel(ctx, i.Type) + (i.Note.Length > 0 ? " · " + i.Note : ""), ["url"] = i.Url };
        if (i.Start.Length > 10)
        {
            // a timed entry without an end lasts two hours (as in the iCal feed) — the week view needs a length
            var end = V.TryParse(i.End, out var e, out var hasTime) && hasTime && V.TryParse(i.Start, out var s, out _) && e > s ? e
                : V.TryParse(i.Start, out var s2, out _) ? s2.AddHours(2) : DateTime.MinValue;
            if (end != DateTime.MinValue) o["end"] = end.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        }
        else if (i.End.Length >= 10 && string.CompareOrdinal(i.End[..10], i.Day) > 0) o["end"] = i.End[..10];
        if (i.Place.Length > 0) o["location"] = i.Place;
        o["tone"] = i.Cancelled ? "error" : i.Note.Length > 0 && i.Note == ctx.T("verein.cal.draft") ? "warn" : i.Highlight ? "accent" : i.Type == Meeting ? "info" : "plain";
        if (i.Cancelled) o["note"] = ctx.T("verein.cal.cancelled");
        return o;
    }

    // ---- the member area (/mitglied/kalender) — server-side, in the member area's own look ------------------------------

    /// <summary>The month asked for (<c>?monat=yyyy-MM</c>), else the current one.</summary>
    public static DateTime MonthOf(IPluginContext ctx)
    {
        var today = V.Local(DateTime.UtcNow);
        return DateTime.TryParseExact(ctx.Request.Q("monat"), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m) && m.Year is > 1900 and < 2200
            ? m : new DateTime(today.Year, today.Month, 1);
    }

    /// <summary>
    /// The member area's calendar: type chips (only the types this member may see), month navigation, the view (month | list), then the month as a grid
    /// (on a phone the grid gives way to the list) or as the list. <paramref name="types"/> are the types offered; <paramref name="items"/> are the month's.
    /// </summary>
    public static string MemberHtml(IPluginContext ctx, string basePath, IReadOnlyList<string> types, string type, DateTime month, bool list, IReadOnlyList<CalItem> items, string feedHtml)
    {
        string Link(string? t = null, DateTime? m = null, bool? asList = null)
        {
            var q = new List<string>();
            var mm = m ?? month;
            q.Add("monat=" + mm.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            var tt = t ?? type;
            if (tt.Length > 0) q.Add("typ=" + tt);
            if (asList ?? list) q.Add("ansicht=liste");
            return basePath + "?" + string.Join("&amp;", q);
        }
        var names = V.MonthNames(ctx);
        var title = names[month.Month - 1] + " " + month.Year.ToString(CultureInfo.InvariantCulture);
        var now = V.Local(DateTime.UtcNow);
        var sb = new StringBuilder("<div class=\"vm-cal\">");
        sb.Append("<nav class=\"vm-chips-row vm-cal-types\" aria-label=\"").Append(V.H(ctx.T("verein.cal.filter"))).Append("\">");
        foreach (var t in new[] { "" }.Concat(types))
            sb.Append("<a href=\"").Append(Link(t)).Append('"').Append(t == type ? " class=\"on\" aria-current=\"page\"" : "").Append('>').Append(V.H(TypeLabel(ctx, t))).Append("</a>");
        sb.Append("</nav><div class=\"vm-cal-head\"><div class=\"vm-cal-nav\"><a class=\"vm-btn ghost small\" href=\"").Append(Link(m: month.AddMonths(-1))).Append("\" aria-label=\"").Append(V.H(ctx.T("verein.cal.prev")))
          .Append("\"><i class=\"ti ti-chevron-left\" aria-hidden=\"true\"></i></a><h2>").Append(V.H(title)).Append("</h2><a class=\"vm-btn ghost small\" href=\"").Append(Link(m: month.AddMonths(1))).Append("\" aria-label=\"")
          .Append(V.H(ctx.T("verein.cal.next"))).Append("\"><i class=\"ti ti-chevron-right\" aria-hidden=\"true\"></i></a>");
        if (month.Year != now.Year || month.Month != now.Month)
            sb.Append("<a class=\"vm-btn ghost small\" href=\"").Append(Link(m: new DateTime(now.Year, now.Month, 1))).Append("\">").Append(V.H(ctx.T("verein.cal.today"))).Append("</a>");
        sb.Append("</div><nav class=\"vm-tabs\" aria-label=\"").Append(V.H(ctx.T("verein.cal.view"))).Append("\"><a href=\"").Append(Link(asList: false)).Append('"').Append(list ? "" : " class=\"on\" aria-current=\"page\"").Append('>')
          .Append(V.H(ctx.T("verein.cal.month"))).Append("</a><a href=\"").Append(Link(asList: true)).Append('"').Append(list ? " class=\"on\" aria-current=\"page\"" : "").Append('>').Append(V.H(ctx.T("verein.cal.list"))).Append("</a></nav></div>");
        if (!list) sb.Append(Grid(ctx, month, items));
        sb.Append("<div class=\"vm-cal-list").Append(list ? " is-list" : "").Append("\">").Append(ListHtml(ctx, items)).Append("</div>");
        sb.Append(feedHtml).Append("</div>");
        return sb.ToString();
    }

    private static readonly string[] DowDe = { "Mo", "Di", "Mi", "Do", "Fr", "Sa", "So" }, DowEn = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    private static string Grid(IPluginContext ctx, DateTime month, IReadOnlyList<CalItem> items)
    {
        var today = V.Today;
        var byDay = items.GroupBy(i => i.Day).ToDictionary(g => g.Key, g => g.ToList());
        var first = month.AddDays(-(((int)month.DayOfWeek + 6) % 7));                       // the Monday of the first week
        var days = (int)Math.Ceiling(((month.AddMonths(1) - first).TotalDays) / 7) * 7;
        var sb = new StringBuilder("<div class=\"vm-cal-grid\" role=\"table\" aria-label=\"").Append(V.H(V.MonthNames(ctx)[month.Month - 1] + " " + month.Year)).Append("\"><div class=\"vm-cal-row vm-cal-dow\" role=\"row\">");
        foreach (var d in V.En(ctx) ? DowEn : DowDe) sb.Append("<span role=\"columnheader\">").Append(d).Append("</span>");
        sb.Append("</div>");
        for (var i = 0; i < days; i++)
        {
            if (i % 7 == 0) sb.Append("<div class=\"vm-cal-row\" role=\"row\">");
            var day = first.AddDays(i);
            var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            sb.Append("<div class=\"vm-cal-day").Append(day.Month != month.Month ? " out" : "").Append(key == today ? " today" : "").Append("\" role=\"cell\"><span class=\"vm-cal-num\">").Append(day.Day).Append("</span>");
            if (byDay.TryGetValue(key, out var list))
                foreach (var it in list)
                    sb.Append("<a class=\"vm-cal-item t-").Append(it.Type).Append(it.Cancelled ? " is-cancelled" : "").Append(it.Highlight ? " is-round" : "").Append("\" href=\"").Append(V.H(it.Url)).Append("\" title=\"")
                      .Append(V.H(OneLabel(ctx, it.Type) + ": " + it.Title + (it.Time.Length > 0 ? ", " + it.Time : "") + (it.Place.Length > 0 ? " · " + it.Place : ""))).Append("\">")
                      .Append(Icon(it.Type)).Append(it.Time.Length > 0 ? "<b>" + it.Time + "</b> " : "").Append(V.H(it.Title)).Append("</a>");
            sb.Append("</div>");
            if (i % 7 == 6) sb.Append("</div>");
        }
        return sb.Append("</div>").ToString();
    }

    private static string Icon(string type) => "<i class=\"ti ti-" + type switch { Meeting => "users-group", Birthday => "cake", Entry => "award", _ => "calendar-event" } + "\" aria-hidden=\"true\"></i> ";

    private static string ListHtml(IPluginContext ctx, IReadOnlyList<CalItem> items)
    {
        if (items.Count == 0) return "<p class=\"vm-muted\">" + V.H(ctx.T("verein.cal.emptyMonth")) + "</p>";
        var sb = new StringBuilder("<div class=\"vm-agenda\">");
        foreach (var it in items)
        {
            var (day, _, wd) = V.Badge(ctx, it.Day);
            var meta = new List<string> { OneLabel(ctx, it.Type) };
            if (it.Time.Length > 0) meta.Add(it.Time);
            if (it.Place.Length > 0) meta.Add(it.Place);
            if (it.Cancelled) meta.Add(ctx.T("verein.cal.cancelled"));
            sb.Append("<div class=\"vm-agenda-row").Append(it.Cancelled ? " is-cancelled" : "").Append("\"><a class=\"vm-agenda-main\" href=\"").Append(V.H(it.Url)).Append("\"><span class=\"vm-date sm\"><b>").Append(V.H(day))
              .Append("</b><em>").Append(V.H(wd)).Append("</em></span><span class=\"vm-agenda-text\"><strong>").Append(Icon(it.Type)).Append(V.H(it.Title)).Append("</strong><small>").Append(V.H(string.Join(" · ", meta))).Append("</small></span></a></div>");
        }
        return sb.Append("</div>").ToString();
    }
}
