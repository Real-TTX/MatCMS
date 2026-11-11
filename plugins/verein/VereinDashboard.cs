using System.Globalization;

namespace MatCMS.Plugin.Verein;

/// <summary>
/// What a club's board looks at first (Admin → Verein → Übersicht) and the card on the site's own dashboard: the numbers, what is waiting
/// (answers, uploads, replies to an event), the next dates — and the people: birthdays of the coming weeks and this year's anniversaries of
/// membership, which a club congratulates on. Everything here is read only; each line leads to where it is dealt with.
/// </summary>
public static class VereinDashboard
{
    private static string R => VereinAdmin.Root;

    /// <summary>The next birthday on or after today (yyyy-MM-dd) and the age then, or null.</summary>
    internal static (DateTime Day, int Age)? NextBirthday(string born, DateTime today)
    {
        if (born.Length < 10 || !DateTime.TryParseExact(born[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var b)) return null;
        DateTime On(int year) => new(year, b.Month, b.Month == 2 && b.Day == 29 && !DateTime.IsLeapYear(year) ? 28 : b.Day);
        var next = On(today.Year);
        if (next < today.Date) next = On(today.Year + 1);
        return (next, next.Year - b.Year);
    }

    /// <summary>Active people whose birthday falls within the next <paramref name="days"/> days, soonest first.</summary>
    internal static List<(Person P, DateTime Day, int Age)> Birthdays(IEnumerable<Person> people, int days)
    {
        var today = V.Local(DateTime.UtcNow).Date;
        return people.Where(p => p.Active).Select(p => (p, b: NextBirthday(p.Born, today))).Where(x => x.b is { } b && (b.Day - today).TotalDays < days)
            .Select(x => (x.p, x.b!.Value.Day, x.b.Value.Age)).OrderBy(x => x.Day).ToList();
    }

    /// <summary>This year's round anniversaries of membership (10, 25, 40, 50, 60 … years).</summary>
    internal static List<(Person P, int Years, string Day)> Anniversaries(IEnumerable<Person> people)
    {
        var year = V.Local(DateTime.UtcNow).Year;
        return people.Where(p => p.Active && p.Joined.Length >= 4 && int.TryParse(p.Joined[..4], out _))
            .Select(p => (p, Years: year - int.Parse(p.Joined[..4], CultureInfo.InvariantCulture), Day: p.Joined))
            .Where(x => x.Years > 0 && (x.Years % 10 == 0 || x.Years == 25)).OrderByDescending(x => x.Years).ToList();
    }

    private static string When(IPluginContext ctx, DateTime day)
    {
        var today = V.Local(DateTime.UtcNow).Date;
        var d = (day - today).Days;
        return d == 0 ? ctx.T("verein.dash.today") : d == 1 ? ctx.T("verein.dash.tomorrow") : V.Date(ctx, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    public static async Task<PluginResult> PageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var board = VereinSettings.BoardList(ctx);
        var all = await store.PeopleAsync(ct);
        var people = all.Where(p => p.Active).ToList();
        var docs = await store.DocsAsync(false, ct);
        var answers = await store.AllAnswersAsync(ct);
        long open = 0;
        foreach (var d in docs)
        {
            var done = answers.Where(a => a.DocId == d.Id).Select(a => a.MemberId).ToHashSet();
            open += VereinStore.Audience(people, d.Audience, board).Count(p => !done.Contains(p.MemberId));
        }
        var events = await store.UpcomingAsync(V.Today, false, 6, ct);
        var replies = await store.AllRepliesAsync(ct);
        var waiting = (await store.UploadsAsync(ct: ct)).Count(u => !u.Approved);
        var next = (await store.MeetingsAsync(ct)).Where(m => m.Status == VereinStore.Planned && string.CompareOrdinal(m.Starts.Length >= 10 ? m.Starts[..10] : m.Starts, V.Today) >= 0)
            .OrderBy(m => m.Starts).FirstOrDefault();
        var birthdays = Birthdays(people, 30);
        var jubilees = Anniversaries(people);
        var newest = all.Where(p => p.Active && p.Created is not null).OrderByDescending(p => p.Created).Take(5).ToList();

        UiTableRow EventRow(Event e)
        {
            var mine = replies.Where(r => r.EventId == e.Id).ToList();
            var asked = e.Ask ? VereinStore.Asked(all, e, board).Count() : 0;
            return Ui.Row(Ui.Inline(Ui.Link(e.Title, R + "events/" + e.Id), e.Public ? null : Ui.Badge(ctx.T("verein.dash.internal"), UiTone.Info)),
                V.When(ctx, e.Starts) + (e.Place.Length > 0 ? " · " + e.Place : ""),
                e.Ask ? Ui.Badge(ctx.T("verein.dash.inOpen", mine.Count(r => r.Answer == VereinStore.Yes), Math.Max(0, asked - mine.Count)), mine.Count < asked ? UiTone.Warning : UiTone.Success) : Ui.Muted("–"));
        }

        var main = new List<UiNode?>
        {
            Ui.Card(ctx.T("verein.dash.nextEvents"), events.Count == 0 ? Ui.Empty(ctx.T("verein.dash.nothing")) : Ui.Table(new[] { Ui.Column(ctx.T("verein.dash.col.event")), Ui.Column(ctx.T("verein.dash.col.whenWhere")), Ui.Column(ctx.T("verein.dash.col.participation")) }, events.Select(EventRow)),
                Ui.Button(ctx.T("verein.dash.allEvents"), R + "events", UiButtonKind.Ghost, small: true)),
            next is null ? null : Ui.Card(ctx.T("verein.dash.nextMeeting"), Ui.KeyValue((ctx.T("verein.dash.what"), next.Title), (ctx.T("verein.dash.when"), V.When(ctx, next.Starts)), (ctx.T("verein.dash.where"), next.Place.Length > 0 ? next.Place : "–"),
                (ctx.T("verein.dash.invitation"), next.InvitedAt is null ? ctx.T("verein.dash.notSent") : ctx.T("verein.dash.sentAt", V.Stamp(ctx, next.InvitedAt)))), Ui.Button(ctx.T("verein.dash.open"), R + "meetings/" + next.Id, UiButtonKind.Ghost, small: true)),
        };
        var side = new List<UiNode?>
        {
            Ui.Card(ctx.T("verein.dash.birthdays"), birthdays.Count == 0 ? Ui.Muted(ctx.T("verein.dash.noBirthdays")) :
                Ui.Table(new[] { Ui.Column(ctx.T("verein.dash.col.who")), Ui.Column(ctx.T("verein.dash.col.when")), Ui.Column(ctx.T("verein.dash.col.age"), right: true) },
                    birthdays.Take(12).Select(b => Ui.Row(Ui.Link(b.P.Name, R + (b.P.Child ? "children/" : "members/") + b.P.Id), When(ctx, b.Day), b.Age.ToString(CultureInfo.InvariantCulture))))),
            jubilees.Count == 0 ? null : Ui.Card(ctx.T("verein.dash.jubilees", V.Local(DateTime.UtcNow).Year.ToString(CultureInfo.InvariantCulture)),
                Ui.Table(new[] { Ui.Column(ctx.T("verein.dash.col.who")), Ui.Column(ctx.T("verein.dash.col.years"), right: true), Ui.Column(ctx.T("verein.dash.col.since")) },
                    jubilees.Take(12).Select(j => Ui.Row(Ui.Link(j.P.Name, R + "members/" + j.P.Id), j.Years.ToString(CultureInfo.InvariantCulture), V.Date(ctx, j.Day))))),
            newest.Count == 0 ? null : Ui.Card(ctx.T("verein.dash.newest"), Ui.Stack(newest.Select(p => (UiNode?)Ui.Inline(Ui.Link(p.Name, R + (p.Child ? "children/" : "members/") + p.Id), p.Child ? Ui.Badge(ctx.T("verein.dash.child"), UiTone.Info) : null, Ui.Muted(V.Day(ctx, p.Created)))).ToArray())),
        };
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, VereinSettings.Club(ctx),
                Ui.Button(ctx.T("verein.dash.openArea"), VereinPublic.Base, UiButtonKind.Ghost, "ti-external-link", small: true)),
            Ui.Stats(
                Ui.Stat(ctx.T("verein.dash.members"), people.Count.ToString(CultureInfo.InvariantCulture),
                    new[] { people.Count(p => p.Child) is var k and > 0 ? ctx.T("verein.dash.children", k) : null, people.Count(p => p.Passive) is var pa and > 0 ? ctx.T("verein.dash.passive", pa) : null }
                        .Where(x => x is not null).ToList() is { Count: > 0 } of ? ctx.T("verein.dash.ofWhich", string.Join(", ", of)) : null, href: R + "members"),
                Ui.Stat(ctx.T("verein.dash.noAccess"), people.Count(p => !p.Child && !p.HasAccount).ToString(CultureInfo.InvariantCulture), href: R + "members?status=ohne-zugang"),
                Ui.Stat(ctx.T("verein.dash.openConfirmations"), open.ToString(CultureInfo.InvariantCulture), tone: open > 0 ? UiTone.Warning : UiTone.Success, href: R + "docs"),
                Ui.Stat(ctx.T("verein.dash.uploadsWaiting"), waiting.ToString(CultureInfo.InvariantCulture), tone: waiting > 0 ? UiTone.Warning : UiTone.Default, href: R + "uploads?status=offen")),
            Ui.Card(ctx.T("verein.dash.quickAdd"), Ui.Inline(
                Ui.Button(ctx.T("verein.dash.add.member"), R + "members/new", icon: "ti-user-plus"),
                Ui.Button(ctx.T("verein.dash.add.child"), R + "children/new", UiButtonKind.Ghost, "ti-mood-kid"),
                Ui.Button(ctx.T("verein.dash.add.doc"), R + "docs/new", UiButtonKind.Ghost, "ti-file-plus"),
                Ui.Button(ctx.T("verein.dash.add.meeting"), R + "meetings/new", UiButtonKind.Ghost, "ti-users-group"),
                Ui.Button(ctx.T("verein.dash.add.event"), R + "events/new", UiButtonKind.Ghost, "ti-calendar-plus"))),
            Ui.Columns(main, side)
        ), ctx.T("verein.dash.title"));
    }

    /// <summary>The card on the site's own dashboard: the next dates and the birthdays of the week.</summary>
    public static async Task<DashboardCard?> CardAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var events = await store.UpcomingAsync(V.Today, false, 4, ct);
        var birthdays = Birthdays(await store.PeopleAsync(ct), 8);
        if (events.Count == 0 && birthdays.Count == 0) return null;
        var rows = events.Select(e => new DashboardRow(e.Title, R + "events/" + e.Id) { Icon = "ti-calendar-event", Meta = V.Date(ctx, e.Starts), Badge = e.Public ? null : ctx.T("verein.dash.internal") })
            .Concat(birthdays.Take(4).Select(b => new DashboardRow(ctx.T("verein.dash.turns", b.P.Name, b.Age), R + (b.P.Child ? "children/" : "members/") + b.P.Id) { Icon = "ti-cake", Meta = When(ctx, b.Day) }))
            .ToList();
        return new DashboardCard(ctx.T("verein.dash.cardTitle"), rows) { AllUrl = R + "dashboard", AllLabel = ctx.T("verein.dash.cardAll") };
    }
}
