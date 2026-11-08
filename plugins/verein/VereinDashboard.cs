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

    private static string When(DateTime day)
    {
        var today = V.Local(DateTime.UtcNow).Date;
        var d = (day - today).Days;
        return d == 0 ? "heute" : d == 1 ? "morgen" : V.Date(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
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
            return Ui.Row(Ui.Inline(Ui.Link(e.Title, R + "events/" + e.Id), e.Public ? null : Ui.Badge("intern")),
                V.When(e.Starts) + (e.Place.Length > 0 ? " · " + e.Place : ""),
                e.Ask ? Ui.Badge(mine.Count(r => r.Answer == VereinStore.Yes) + " dabei · " + Math.Max(0, asked - mine.Count) + " offen", mine.Count < asked ? UiTone.Warning : UiTone.Success) : Ui.Muted("–"));
        }

        var main = new List<UiNode?>
        {
            Ui.Card("Nächste Termine", events.Count == 0 ? Ui.Empty("Gerade steht nichts an.") : Ui.Table(new[] { Ui.Column("Termin"), Ui.Column("Wann · Wo"), Ui.Column("Teilnahme") }, events.Select(EventRow)),
                Ui.Inline(Ui.Button("Alle Termine", R + "events", UiButtonKind.Ghost, small: true), Ui.Button("Neuer Termin", R + "events/new", UiButtonKind.Ghost, "ti-calendar-plus", small: true))),
            next is null ? null : Ui.Card("Nächste Sitzung", Ui.KeyValue(("Was", next.Title), ("Wann", V.When(next.Starts)), ("Wo", next.Place.Length > 0 ? next.Place : "–"),
                ("Einladung", next.InvitedAt is null ? "noch nicht verschickt" : "verschickt am " + V.Stamp(next.InvitedAt))), Ui.Button("Öffnen", R + "meetings/" + next.Id, UiButtonKind.Ghost, small: true)),
        };
        var side = new List<UiNode?>
        {
            Ui.Card("Geburtstage (30 Tage)", birthdays.Count == 0 ? Ui.Muted("Keine – oder es fehlen Geburtsdaten.") :
                Ui.Table(new[] { Ui.Column("Wer"), Ui.Column("Wann"), Ui.Column("wird", right: true) },
                    birthdays.Take(12).Select(b => Ui.Row(Ui.Link(b.P.Name, R + (b.P.Child ? "children/" : "members/") + b.P.Id), When(b.Day), b.Age.ToString(CultureInfo.InvariantCulture))))),
            jubilees.Count == 0 ? null : Ui.Card("Jubiläen " + V.Local(DateTime.UtcNow).Year,
                Ui.Table(new[] { Ui.Column("Wer"), Ui.Column("Jahre", right: true), Ui.Column("seit") },
                    jubilees.Take(12).Select(j => Ui.Row(Ui.Link(j.P.Name, R + "members/" + j.P.Id), j.Years.ToString(CultureInfo.InvariantCulture), V.Date(j.Day))))),
            newest.Count == 0 ? null : Ui.Card("Zuletzt aufgenommen", Ui.Stack(newest.Select(p => (UiNode?)Ui.Inline(Ui.Link(p.Name, R + (p.Child ? "children/" : "members/") + p.Id), p.Child ? Ui.Badge("Kind", UiTone.Info) : null, Ui.Muted(V.Stamp(p.Created)[..10]))).ToArray())),
        };
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Mitglieder, Dokumente, Sitzungen und Termine auf einen Blick", VereinSettings.Club(ctx),
                Ui.Button("Mitgliederbereich öffnen", VereinPublic.Base, UiButtonKind.Ghost, "ti-external-link", small: true)),
            Ui.Stats(
                Ui.Stat("Aktive Mitglieder", people.Count.ToString(CultureInfo.InvariantCulture), people.Count(p => p.Child) is var k and > 0 ? "davon " + k + " Kinder" : null, href: R + "members"),
                Ui.Stat("Ohne Zugang", people.Count(p => !p.Child && !p.HasAccount).ToString(CultureInfo.InvariantCulture), "nur geführt", href: R + "members?status=ohne-zugang"),
                Ui.Stat("Offene Bestätigungen", open.ToString(CultureInfo.InvariantCulture), tone: open > 0 ? UiTone.Warning : UiTone.Success, href: R + "docs"),
                Ui.Stat("Uploads zur Freigabe", waiting.ToString(CultureInfo.InvariantCulture), tone: waiting > 0 ? UiTone.Warning : UiTone.Default, href: R + "uploads?status=offen")),
            Ui.Card("Schnell anlegen", Ui.Inline(
                Ui.Button("Mitglied", R + "members/new", icon: "ti-user-plus"),
                Ui.Button("Kind", R + "children/new", UiButtonKind.Ghost, "ti-mood-kid"),
                Ui.Button("Dokument", R + "docs/new", UiButtonKind.Ghost, "ti-file-plus"),
                Ui.Button("Sitzung", R + "meetings/new", UiButtonKind.Ghost, "ti-users-group"),
                Ui.Button("Termin", R + "events/new", UiButtonKind.Ghost, "ti-calendar-plus"),
                Ui.Button("Instagram-Bild", R + "instagram", UiButtonKind.Ghost, "ti-brand-instagram"))),
            Ui.Columns(main, side),
            Ui.Muted("Auf der Website: Der Mitgliederbereich liegt unter " + VereinPublic.Base + ". Öffentliche Termine zeigt der Block „Vereinstermine“; als Kalender: " + VereinIcal.PublicFeed + ".")
        ), "Verein");
    }

    /// <summary>The card on the site's own dashboard: the next dates and the birthdays of the week.</summary>
    public static async Task<DashboardCard?> CardAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var events = await store.UpcomingAsync(V.Today, false, 4, ct);
        var birthdays = Birthdays(await store.PeopleAsync(ct), 8);
        if (events.Count == 0 && birthdays.Count == 0) return null;
        var rows = events.Select(e => new DashboardRow(e.Title, R + "events/" + e.Id) { Icon = "ti-calendar-event", Meta = V.Date(e.Starts), Badge = e.Public ? null : "intern" })
            .Concat(birthdays.Take(4).Select(b => new DashboardRow(b.P.Name + " wird " + b.Age, R + (b.P.Child ? "children/" : "members/") + b.P.Id) { Icon = "ti-cake", Meta = When(b.Day) }))
            .ToList();
        return new DashboardCard("Verein · Termine & Geburtstage", rows) { AllUrl = R + "dashboard", AllLabel = "Zur Vereinsübersicht" };
    }
}
