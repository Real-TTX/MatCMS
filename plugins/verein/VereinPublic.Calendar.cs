using System.Text;

namespace MatCMS.Plugin.Verein;

public static partial class VereinPublic
{
    /// <summary>
    /// /mitglied/kalender — the club's dates as a month (or a list), filtered by type. Only what this member may see anyway: no drafts; meetings of their
    /// audience (the board: all); birthdays only where the club shows dates of birth to this member, and only of the people they see in the member list;
    /// anniversaries of joining only for the board (the date one joined is shown to nobody else).
    /// </summary>
    private static async Task<PluginResult> CalendarAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var store = new VereinStore(ctx.Data);
        var boardRoles = VereinSettings.BoardList(ctx);
        bool IsBoard(Person x) => x.Roles.Any(r => boardRoles.Contains(r, StringComparer.OrdinalIgnoreCase));
        var people = (await store.PeopleAsync(ct)).Where(x => x.Active).ToList();
        var seeAll = who!.Board || ctx.Settings.GetBool(VereinSettings.DirectoryForAll);
        var seeBirthday = who.Board || ctx.Settings.GetBool(VereinSettings.BirthdayForAll);
        var birthdays = seeBirthday ? people.Where(x => seeAll || (!x.Child && IsBoard(x)) || x.Id == who.Person.Id).ToList() : new List<Person>();
        var entries = who.Board ? people.Where(x => !x.Child).ToList() : new List<Person>();

        var types = new List<string> { VereinCalendar.Event, VereinCalendar.Meeting };
        if (seeBirthday) types.Add(VereinCalendar.Birthday);
        if (who.Board) types.Add(VereinCalendar.Entry);
        var type = VereinCalendar.TypeOf(ctx);
        if (type.Length > 0 && !types.Contains(type)) type = "";

        var month = VereinCalendar.MonthOf(ctx);
        var items = VereinCalendar.Items(ctx, type, month, month.AddMonths(1).AddDays(-1),
            (await store.EventsAsync(ct)).Where(e => !e.Draft), await MeetingsForAsync(ctx, store, who, ct), birthdays, entries,
            e => Base + "/termine/" + e.Id, m => Base + "/sitzungen/" + m.Id, _ => Base + "/mitglieder");

        // subscribing: the member's own secret feed (events and their meetings — never birthdays), else where to make one
        var feed = new StringBuilder("<p class=\"vm-muted vm-cal-feed\"><i class=\"ti ti-calendar-plus\" aria-hidden=\"true\"></i> ");
        feed.Append(who.Person.CalToken.Length == 40
            ? "<a href=\"" + V.H(VereinIcal.PersonalFeed(who.Person.CalToken)) + "\">" + V.H(ctx.T("verein.cal.myFeed")) + "</a>"
            : "<a href=\"" + Base + "/profil\">" + V.H(ctx.T("verein.cal.makeFeed")) + "</a>");
        feed.Append(" · ").Append(V.H(ctx.T("verein.cal.feedNote"))).Append("</p>");

        var html = Notice(ctx) + VereinCalendar.MemberHtml(ctx, Base + "/kalender", types, type, month, ctx.Request.Q("ansicht") == "liste", items, feed.ToString());
        return await ShellAsync(ctx, who, "calendar", ctx.T("verein.cal.title"), html, ct);
    }
}
