using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Booking;

/// <summary>One cleaning: a unit on the day a stay ends, with what comes next.</summary>
public sealed record CleaningTask(Unit Unit, DateOnly Day, string Leaving, DateOnly? NextArrival, int NextPersons, string NextExtras, string Assignee, DateTime? Done, string Note)
{
    /// <summary>The next guests arrive the same day — the cleaning has a deadline (check-in time).</summary>
    public bool Turnover => NextArrival == Day;
}

/// <summary>
/// Admin → Buchungen → Reinigung (phase B5): the turnover list, DERIVED from the occupied nights — a stay (booking or a portal's reservation) that ends
/// on a day means a cleaning that day; nothing has to be planned by hand. Who does it, "done" and a note are kept per unit and day (<c>tasks</c>).
/// A "whole house" is cleaned as its apartments (its nights are written into them). The cleaning team gets a secret calendar without guest names and a
/// printable list with each unit's checklist.
/// </summary>
public static class BookingCleaning
{
    public const string CleanersKey = "cleaners", TokenMeta = "cleaning_token", IcsPath = "/buchung/reinigung/";

    public static void Declare(ISettingsBuilder s) => s
        .Add(new SettingSpec(CleanersKey, "Reinigung: Zuständige", SettingKind.TextArea) { Group = "Reinigung", Help = "Ein Name je Zeile (z. B. „Ana – 0385 91 123 456“). Erscheint in der Auswahl der Reinigungsliste." });

    public static IAdminSection Register(IAdminSection s) => s.Page("cleaning", null, PageAsync).Page("cleaning/pdf", null, PdfAsync);

    public static void Configure(IPluginBuilder b) => b.Route(IcsPath + "{token}", IcsAsync, new PublicRouteOptions { Methods = PluginMethod.Get });

    public static List<string> Cleaners(IPluginContext ctx) =>
        (ctx.Settings.Get(CleanersKey) ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(30).ToList();

    /// <summary>The cleanings of [from, to]: per unit (not the "whole house" units) every day a stay ends, with the next arrival after it.</summary>
    public static async Task<List<CleaningTask>> TasksAsync(IPluginData data, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var store = new BookingStore(data);
        var units = (await store.UnitsAsync(ct)).Where(u => u.Active && u.Contains.Count == 0).ToList();
        var nights = await store.NightsAsync(units.Select(u => u.Id), from.AddDays(-1), to.AddDays(60), ct);
        var bookings = (await BookingFlow.ListAsync(data, null, ct)).ToDictionary(b => b.Id);
        var blocks = new Dictionary<long, Block>();
        foreach (var u in units) foreach (var bl in await store.BlocksAsync(u.Id, ct)) blocks[bl.Id] = bl;
        var fees = new Dictionary<long, string>();
        foreach (var h in units.Select(u => u.PropertyId).Distinct()) foreach (var f in await store.FeesAsync(h, ct)) fees[f.Id] = f.Name;
        var kept = (await data.Table(BookingStore.TTasks).QueryAsync(DataQuery.Everything.Where("day", DataOp.Ge, B.Iso(from)).Where("day", DataOp.Le, B.Iso(to)), ct))
            .ToDictionary(r => (r.Int("unit_id"), r.Text("day")));

        // a stay = a booking or a portal's reservation; the owner's own blocks (repairs, own use) need no turnover cleaning
        string? Owner(Night n) => n.BookingId > 0 ? "b" + n.BookingId : blocks.TryGetValue(n.BlockId, out var bl) && bl.Source.StartsWith("feed:", StringComparison.Ordinal) ? "f" + n.BlockId : null;
        string Label(string owner) => owner[0] == 'b' && bookings.TryGetValue(long.Parse(owner[1..], CultureInfo.InvariantCulture), out var b) ? "Buchung " + b.Number
            : owner[0] == 'f' && blocks.TryGetValue(long.Parse(owner[1..], CultureInfo.InvariantCulture), out var bl) ? bl.Reason : "Aufenthalt";

        var list = new List<CleaningTask>();
        foreach (var u in units)
        {
            var byDay = nights.Where(n => n.UnitId == u.Id).GroupBy(n => n.Day).ToDictionary(g => g.Key, g => Owner(g.First()));
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var before = byDay.GetValueOrDefault(d.AddDays(-1));
                var now = byDay.GetValueOrDefault(d);
                if (before is null || before == now) continue;                    // nobody leaves today
                // the next stay that starts on or after today
                DateOnly? next = null; var persons = 0; var extras = "";
                for (var x = d; x <= to.AddDays(60); x = x.AddDays(1))
                    if (byDay.GetValueOrDefault(x) is { } o && (x == d ? o != before : byDay.GetValueOrDefault(x.AddDays(-1)) != o))
                    {
                        next = x;
                        if (o[0] == 'b' && bookings.TryGetValue(long.Parse(o[1..], CultureInfo.InvariantCulture), out var nb))
                        {
                            persons = nb.Adults + nb.Children;
                            extras = string.Join(", ", nb.Extras.Select(e => fees.GetValueOrDefault(e, "")).Where(e => e.Length > 0));
                        }
                        break;
                    }
                var k = kept.GetValueOrDefault((u.Id, B.Iso(d)));
                list.Add(new CleaningTask(u, d, Label(before), next, persons, extras, k?.Text("assignee") ?? "", k?.Timestamp("done_at"), k?.Text("note") ?? ""));
            }
        }
        return list.OrderBy(t => t.Day).ThenBy(t => t.Unit.Sort).ThenBy(t => t.Unit.Name).ToList();
    }

    private static async Task SetAsync(IPluginData data, long unitId, DateOnly day, Dictionary<string, object?> values, CancellationToken ct)
    {
        var t = data.Table(BookingStore.TTasks);
        var row = await t.FindAsync(DataQuery.Everything.Eq("unit_id", unitId).Eq("day", B.Iso(day)), ct);
        if (row is null) { values["unit_id"] = unitId; values["day"] = B.Iso(day); await t.InsertAsync(values, ct); }
        else await t.UpdateAsync(row.Id, values, ct);
    }

    private static (DateOnly From, DateOnly To) Range(IPluginContext ctx)
    {
        var from = B.Day(ctx.Request.Q("von")) ?? B.Today;
        var to = B.Day(ctx.Request.Q("bis")) ?? from.AddDays(13);
        if (to < from) (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber > 92) to = from.AddDays(92);
        return (from, to);
    }

    private static async Task<PluginResult> PageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (from, to) = Range(ctx);
        var self = BookingAdmin.Root + "cleaning?von=" + B.Iso(from) + "&bis=" + B.Iso(to);
        var f = ctx.Request;
        if (f.IsPost)
        {
            if (f.Action == "renew")
            {
                await SetTokenAsync(ctx.Data, BookingIcal.NewToken(), ct);
                return PluginResult.Redirect(self, "Neue Kalender-Adresse erzeugt – die alte geht nicht mehr.");
            }
            if (long.TryParse(f.F("unit"), out var unitId) && B.Day(f.F("day")) is { } day)
            {
                switch (f.Action)
                {
                    case "assign": await SetAsync(ctx.Data, unitId, day, new() { ["assignee"] = Cleaners(ctx).Contains(f.F("assignee")) ? f.F("assignee") : "" }, ct); break;
                    case "done": await SetAsync(ctx.Data, unitId, day, new() { ["done_at"] = DateTime.UtcNow }, ct); break;
                    case "undo": await SetAsync(ctx.Data, unitId, day, new() { ["done_at"] = null }, ct); break;
                    case "note": await SetAsync(ctx.Data, unitId, day, new() { ["note"] = B.Clip(f.F("note"), 300) }, ct); break;
                }
            }
            return PluginResult.Redirect(self);
        }
        var tasks = await TasksAsync(ctx.Data, from, to, ct);
        var cleaners = Cleaners(ctx);
        var house = await new BookingStore(ctx.Data).PropertiesAsync(ct) is { Count: > 0 } hs ? hs[0] : null;
        UiNode Assign(CleaningTask t) => cleaners.Count == 0 ? Ui.Muted(t.Assignee.Length > 0 ? t.Assignee : "–") : Ui.Form(
            Ui.Hidden("action", "assign"), Ui.Hidden("unit", B.Num(t.Unit.Id)), Ui.Hidden("day", B.Iso(t.Day)),
            Ui.Inline(Ui.Select("assignee", "Zuständig", t.Assignee, new[] { Ui.Option("", "– niemand –") }.Concat(cleaners.Select(c => Ui.Option(c, c))).ToArray()),
                Ui.Submit("OK", UiButtonKind.Ghost, small: true)));
        UiNode Done(CleaningTask t) => t.Done is null
            ? Ui.Action("Erledigt", new Dictionary<string, string> { ["action"] = "done", ["unit"] = B.Num(t.Unit.Id), ["day"] = B.Iso(t.Day) }, UiButtonKind.Ghost, icon: "ti-check")
            : Ui.Inline(Ui.Badge("erledigt", UiTone.Success), Ui.Action("Zurück", new Dictionary<string, string> { ["action"] = "undo", ["unit"] = B.Num(t.Unit.Id), ["day"] = B.Iso(t.Day) }, icon: "ti-arrow-back-up"));
        var token = await TokenAsync(ctx.Data, ct);
        var ics = (ctx.Site.Origin ?? "").TrimEnd('/') + IcsPath + token + ".ics";
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Wer reist wann ab, wer kommt danach – die Liste ergibt sich aus den Buchungen und den Portal-Kalendern", "Reinigung",
                Ui.Button("Als PDF", BookingAdmin.Root + "cleaning/pdf?von=" + B.Iso(from) + "&bis=" + B.Iso(to), UiButtonKind.Ghost, "ti-printer", small: true)),
            Ui.Card(Ui.Form(Ui.Inline(Ui.Input("von", "Von", B.Iso(from), UiInputType.Date), Ui.Input("bis", "Bis", B.Iso(to), UiInputType.Date), Ui.Submit("Anzeigen", UiButtonKind.Ghost, small: true)))
                with { Action = BookingAdmin.Root + "cleaning" }),
            Ui.Table(new[] { Ui.Column("Tag"), Ui.Column("Einheit"), Ui.Column("Abreise"), Ui.Column("Danach"), Ui.Column("Zuständig"), Ui.Column("", actions: true) },
                tasks.Select(t => Ui.Row(
                    Ui.Stack(Ui.Text(B.Weekday(t.Day) + " " + B.Date(t.Day)), t.Turnover ? Ui.Badge("Wechsel" + (house?.CheckIn is { Length: > 0 } ci ? " bis " + ci + " Uhr" : ""), UiTone.Warning) : null),
                    t.Unit.Name, t.Leaving,
                    t.NextArrival is { } n ? (n == t.Day ? "heute" : B.Weekday(n) + " " + B.Date(n)) + (t.NextPersons > 0 ? " · " + t.NextPersons + " Pers." : "") + (t.NextExtras.Length > 0 ? " · " + t.NextExtras : "") : "–",
                    Assign(t), Done(t))),
                emptyText: "In diesem Zeitraum reist niemand ab."),
            Ui.Card("Kalender für das Reinigungsteam", Ui.Stack(
                Ui.KeyValue(("Adresse", ics)),
                Ui.Muted("Jede Reinigung als Termin mit Einheit, Uhrzeiten, Personenzahl der nächsten Gäste und Checkliste – ohne Namen. Zum Abonnieren im Handy-Kalender."),
                Ui.Action("Neue Adresse erzeugen", "renew", confirm: "Die bisherige Kalender-Adresse geht danach nicht mehr. Fortfahren?", icon: "ti-key")))), "Reinigung");
    }

    // ---- the team's calendar and the printout -------------------------------------------------------------------------

    private static async Task<string> TokenAsync(IPluginData data, CancellationToken ct)
    {
        if (await data.Table(BookingStore.TMeta).FindAsync(DataQuery.Everything.Eq("name", TokenMeta), ct) is { } r && r.Text("value").Length >= 20) return r.Text("value");
        var token = BookingIcal.NewToken();
        await SetTokenAsync(data, token, ct);
        return token;
    }

    private static async Task SetTokenAsync(IPluginData data, string token, CancellationToken ct)
    {
        var t = data.Table(BookingStore.TMeta);
        if (await t.FindAsync(DataQuery.Everything.Eq("name", TokenMeta), ct) is { } r) await t.UpdateAsync(r.Id, new Dictionary<string, object?> { ["value"] = token }, ct);
        else await t.InsertAsync(new Dictionary<string, object?> { ["name"] = TokenMeta, ["value"] = token }, ct);
    }

    private static async Task<PluginResult> IcsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var token = ctx.Request.R("token");
        if (token.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) token = token[..^4];
        if (token.Length < 20 || await ctx.Data.Table(BookingStore.TMeta).FindAsync(DataQuery.Everything.Eq("name", TokenMeta).Eq("value", token), ct) is null)
            return PluginResult.NotFound();
        var houses = await new BookingStore(ctx.Data).PropertiesAsync(ct);
        var tasks = await TasksAsync(ctx.Data, B.Today.AddDays(-7), B.Today.AddDays(90), ct);
        var host = Uri.TryCreate(ctx.Site.Origin, UriKind.Absolute, out var o) ? o.Host : "matcms";
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");
        var sb = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//MatCMS//Reinigung//DE\r\nMETHOD:PUBLISH\r\nX-WR-CALNAME:Reinigung\r\n");
        foreach (var t in tasks)
        {
            var h = houses.FirstOrDefault(x => x.Id == t.Unit.PropertyId);
            var desc = new StringBuilder();
            if (h?.CheckOut is { Length: > 0 } co) desc.Append("Abreise bis ").Append(co).Append(" Uhr\n");
            desc.Append(t.Turnover ? "Neue Gäste heute" + (h?.CheckIn is { Length: > 0 } ci ? " ab " + ci + " Uhr" : "") : t.NextArrival is { } n ? "Nächste Anreise " + B.Date(n) : "Keine Anreise geplant");
            if (t.NextPersons > 0) desc.Append(", ").Append(t.NextPersons).Append(" Personen");
            if (t.NextExtras.Length > 0) desc.Append(", ").Append(t.NextExtras);
            if (t.Assignee.Length > 0) desc.Append("\nZuständig: ").Append(t.Assignee);
            if (t.Unit.Checklist.Length > 0) desc.Append("\n\n").Append(t.Unit.Checklist);
            var day = t.Day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            sb.Append("BEGIN:VEVENT\r\nUID:clean-").Append(t.Unit.Id).Append('-').Append(day).Append('@').Append(host).Append("\r\n")
              .Append("DTSTAMP:").Append(DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)).Append("\r\n")
              .Append("DTSTART;VALUE=DATE:").Append(day).Append("\r\nDTEND;VALUE=DATE:").Append(t.Day.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append("\r\n")
              .Append("SUMMARY:").Append(Esc("Reinigung " + t.Unit.Name + (t.Turnover ? " (Wechsel)" : ""))).Append("\r\n")
              .Append("DESCRIPTION:").Append(Esc(desc.ToString())).Append("\r\nTRANSP:TRANSPARENT\r\nEND:VEVENT\r\n");
        }
        sb.Append("END:VCALENDAR\r\n");
        return PluginResult.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/calendar; charset=utf-8").WithHeader("Cache-Control", "no-store").WithHeader("X-Robots-Tag", "noindex");
    }

    private static async Task<PluginResult> PdfAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (from, to) = Range(ctx);
        var tasks = await TasksAsync(ctx.Data, from, to, ct);
        var p = new PrintLayout(await BookingPrint.LetterheadAsync(ctx, ct), "Reinigung " + B.Date(from) + " – " + B.Date(to));
        p.Kicker("Reinigungsliste · " + B.Date(from) + " – " + B.Date(to));
        p.Title("Reinigung");
        if (tasks.Count == 0) p.Body("In diesem Zeitraum reist niemand ab.");
        else
            p.Table(new[] { "Tag", "Einheit", "Danach", "Zuständig", "Erledigt" }, new[] { 0.2, 0.26, 0.28, 0.16, 0.1 },
                tasks.Select(t => new[]
                {
                    B.Weekday(t.Day) + " " + B.Date(t.Day) + (t.Turnover ? " (Wechsel)" : ""), t.Unit.Name,
                    t.NextArrival is { } n ? (n == t.Day ? "heute" : B.Date(n)) + (t.NextPersons > 0 ? ", " + t.NextPersons + " Pers." : "") + (t.NextExtras.Length > 0 ? ", " + t.NextExtras : "") : "–",
                    t.Assignee, t.Done is null ? "[   ]" : "ja"
                }).ToList());
        foreach (var u in tasks.Select(t => t.Unit).DistinctBy(u => u.Id).Where(u => u.Checklist.Length > 0))
        {
            p.Heading("Checkliste " + u.Name);
            p.Body(string.Join("\n", u.Checklist.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => "[  ] " + l.Trim())));
        }
        return BookingPrint.Result(p.ToBytes(), "Reinigung " + B.Iso(from));
    }
}
