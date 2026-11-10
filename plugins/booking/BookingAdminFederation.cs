using System.Globalization;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// Admin → Buchungen → Verbund. On the CENTRAL site (role lead, B7a) the one place for every house: the open requests of all houses, today's arrivals and
/// departures, all bookings, the houses and when each last reported. A house's booking opens in that house's own admin (B7a only reads — answering from
/// here is B7b). On a house in the federation: its state (last run, what waits) with "Jetzt abgleichen" and "Verbindung prüfen".
/// </summary>
public static class BookingAdminFederation
{
    private static string R => BookingAdmin.Root;

    public static IAdminSection Register(IAdminSection s) => s.Page("federation", null, PageAsync).Page("federation/bookings", null, AllAsync);

    private static string When(DateTime d) => d == DateTime.MinValue ? "noch nie" : BookingMail.Local(d);

    /// <summary>A booking as the central site lists it — its own or the mirror of another house's.</summary>
    private sealed record Entry(string House, string Number, string Guest, string Email, string Unit, DateOnly? Arrival, DateOnly? Departure, int Persons,
        long Total, string Currency, string Status, DateTime? Created, string Href, bool External, string Message);

    private static async Task<List<Entry>> EntriesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var units = (await store.UnitsAsync(ct)).ToDictionary(u => u.Id);
        var cur = BookingPlugin.Currency(ctx);
        var list = (await BookingFlow.ListAsync(ctx.Data, null, ct)).Select(b => new Entry(ctx.Site.Name, b.Number, b.Name, b.Email,
            units.TryGetValue(b.UnitId, out var u) ? u.Name : "–", b.Arrival, b.Departure, b.Adults + b.Children, b.Total, cur, b.Status, b.Created,
            R + "bookings/" + b.Id, false, b.Message)).ToList();
        foreach (var r in await ctx.Data.Table(BookingFederation.TRemote).QueryAsync(DataQuery.Everything, ct))
        {
            var origin = r.Text("origin");
            DateTime? created = DateTime.TryParse(r.Text("created"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var c) ? c : null;
            list.Add(new Entry(r.Text("site_name").Length > 0 ? r.Text("site_name") : r.Text("site"), r.Text("number"), r.Text("name"), r.Text("email"),
                (r.Text("property").Length > 0 ? r.Text("property") + " · " : "") + r.Text("unit"), B.Day(r.Text("arrival")), B.Day(r.Text("departure")),
                (int)(r.Int("adults") + r.Int("children")), r.Int("total"), r.Text("currency") is { Length: 3 } rc ? rc : cur, r.Text("status"), created,
                origin.Length > 0 ? origin + R + "bookings/" + r.Text("ref") : "", true, r.Text("message")));
        }
        return list;
    }

    private static UiNode Open(Entry e) => e.Href.Length == 0 ? Ui.Text(e.Number) : Ui.Link(e.Number, e.Href, newTab: e.External);
    private static string Period(Entry e) => e.Arrival is { } a && e.Departure is { } d ? B.Date(a) + " – " + B.Date(d) + " (" + (d.DayNumber - a.DayNumber) + ")" : "–";
    private static UiNode StatusBadge(string s) => Ui.Badge(BookingStatus.Label(s), BookingStatus.Tone(s));
    private static bool IsOpen(Entry e) => e.Status is BookingStatus.Request or BookingStatus.Offer;

    private static UiNode Tabs(string active) => Ui.Tabs(Ui.Tab("Übersicht", R + "federation", active == "overview"), Ui.Tab("Alle Buchungen", R + "federation/bookings", active == "all"));

    private static async Task<PluginResult> PageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var role = BookingFederation.Role(ctx);
        var self = R + "federation";
        var settings = Ui.Button("Einstellungen", R + "_settings?group=" + Uri.EscapeDataString(BookingFederation.Group), UiButtonKind.Ghost, "ti-settings", small: true);

        if (role == BookingFederation.Member)
        {
            if (ctx.Request.IsPost)
            {
                if (ctx.Request.Action == "probe")
                    return await BookingFederation.ProbeAsync(ctx, ct) is { } why
                        ? PluginResult.Redirect(self, "Keine Verbindung: " + why, true)
                        : PluginResult.Redirect(self, "Verbindung steht.");
                var r = await BookingFederation.SyncAsync(ctx, ct);
                return r.Ok
                    ? PluginResult.Redirect(self, "Abgeglichen: " + r.Pushed + " Gäste und " + r.Stays + " Buchungen gesendet, " + r.Pulled + " Gäste übernommen.")
                    : PluginResult.Redirect(self, "Abgleich nicht möglich: " + r.Error + " Die Änderungen bleiben in der Warteschlange.", true);
            }
            var (g, s) = await BookingFederation.PendingAsync(ctx.Data, ct);
            var error = await BookingFederation.MetaAsync(ctx.Data, "fed.last_error", ct);
            var ok = await BookingFederation.MetaTimeAsync(ctx.Data, "fed.last_ok", ct);
            return PluginResult.Page(Ui.Stack(
                Ui.PageHead(null, "Verbund", settings),
                error.Length > 0 ? Ui.Alert("Letzter Abgleich ging nicht: " + error + " Anfragen gehen trotzdem und werden nachgereicht.", UiTone.Warning) : null,
                Ui.Card("Haus im Verbund", Ui.Stack(
                    Ui.Stats(
                        Ui.Stat("Zentrale", ctx.Settings.Get(BookingFederation.UrlKey) is { Length: > 0 } u ? u : "nicht eingetragen"),
                        Ui.Stat("Zuletzt abgeglichen", When(ok)),
                        Ui.Stat("Wartet", g + (g == 1 ? " Gast" : " Gäste") + ", " + s + (s == 1 ? " Buchung" : " Buchungen"))),
                    Ui.Form(Ui.FormActions(Ui.Submit("Jetzt abgleichen", UiButtonKind.Primary, "action", "sync"), Ui.Submit("Verbindung prüfen", UiButtonKind.Ghost, "action", "probe")))))), "Verbund");
        }

        if (role != BookingFederation.Lead)
            return PluginResult.Page(Ui.Stack(
                Ui.PageHead(null, "Verbund"),
                Ui.Card("Diese Seite arbeitet für sich", Ui.Stack(
                    Ui.Text("Mehrere Häuser verbinden: eine Seite wird Zentrale, die anderen melden an sie."),
                    Ui.Button("Rolle einstellen", R + "_settings?group=" + Uri.EscapeDataString(BookingFederation.Group), UiButtonKind.Primary, "ti-affiliate")))), "Verbund");

        // ---- the central site ----
        var all = await EntriesAsync(ctx, ct);
        var today = B.Today;
        var open = all.Where(IsOpen).OrderBy(e => e.Created ?? DateTime.MaxValue).ToList();
        var arriving = all.Where(e => e.Status == BookingStatus.Confirmed && e.Arrival == today).ToList();
        var leaving = all.Where(e => e.Status == BookingStatus.Confirmed && e.Departure == today).ToList();
        var seen = await BookingFederation.SeenAsync(ctx.Data, ct);
        var houses = all.Select(e => e.House).Distinct().Count();

        var inbox = Ui.Table(new[] { Ui.Column("Nr."), Ui.Column("Haus"), Ui.Column("Gast"), Ui.Column("Unterkunft"), Ui.Column("Zeitraum"), Ui.Column("Betrag", right: true), Ui.Column("Status") },
            open.Select(e => Ui.RowFor(e.Number + " " + e.House + " " + e.Guest + " " + e.Email, Open(e), e.House, e.Guest, e.Unit, Period(e), B.Money(e.Total, e.Currency), StatusBadge(e.Status))),
            emptyText: "Keine offenen Anfragen.");
        UiNode Today(string title, List<Entry> list) => Ui.Card(title, Ui.Table(new[] { Ui.Column("Haus"), Ui.Column("Gast"), Ui.Column("Unterkunft"), Ui.Column("Personen", right: true) },
            list.Select(e => Ui.Row(e.House, e.Guest, e.Unit, B.Num(e.Persons))), emptyText: "Niemand."));

        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, "Verbund", settings),
            Tabs("overview"),
            Ui.Stats(
                Ui.Stat("Offene Anfragen", B.Num(open.Count), tone: open.Count > 0 ? UiTone.Warning : UiTone.Default),
                Ui.Stat("Anreisen heute", B.Num(arriving.Count)),
                Ui.Stat("Abreisen heute", B.Num(leaving.Count)),
                Ui.Stat("Häuser", B.Num(Math.Max(houses, seen.Count + 1)))),
            Ui.Card("Offene Anfragen aller Häuser", inbox),
            Ui.Grid(320, Today("Anreisen heute", arriving), Today("Abreisen heute", leaving)),
            // how to connect a house is said only while none is connected
            Ui.Card("Häuser im Verbund", Ui.Stack(
                Ui.Table(new[] { Ui.Column("Schlüssel"), Ui.Column("Zuletzt gemeldet") },
                    seen.Select(x => Ui.Row(x.Site, x.At is { } at ? BookingMail.Local(at) : "–")),
                    emptyText: "Noch kein Haus verbunden. Ein Haus verbinden: hier unter Admin → API-Schlüssel einen Schlüssel mit „Buchungen: Personendaten“ anlegen; im Haus Rolle „Haus im Verbund“, " +
                        "Adresse " + ctx.Site.Origin + " und Schlüssel eintragen.")))), "Verbund");
    }

    private static async Task<PluginResult> AllAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (BookingFederation.Role(ctx) != BookingFederation.Lead) return PluginResult.Redirect(R + "federation");
        var tab = ctx.Request.Q("tab") is "kommend" or "alle" ? ctx.Request.Q("tab") : "offen";
        var today = B.Today;
        var all = await EntriesAsync(ctx, ct);
        var list = tab switch
        {
            "kommend" => all.Where(e => e.Status == BookingStatus.Confirmed && e.Departure >= today).OrderBy(e => e.Arrival).ToList(),
            "alle" => all.OrderByDescending(e => e.Created ?? DateTime.MinValue).ToList(),
            _ => all.Where(IsOpen).OrderBy(e => e.Created ?? DateTime.MaxValue).ToList()
        };
        var houses = all.Select(e => e.House).Distinct().OrderBy(h => h).ToList();
        var self = R + "federation/bookings";
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, "Verbund"),
            Tabs("all"),
            Ui.Tabs(Ui.Tab("Offen", self, tab == "offen"), Ui.Tab("Kommend", self + "?tab=kommend", tab == "kommend"), Ui.Tab("Alle", self + "?tab=alle", tab == "alle")),
            Ui.Table(new[] { Ui.Column("Nr."), Ui.Column("Haus"), Ui.Column("Gast"), Ui.Column("Unterkunft"), Ui.Column("Zeitraum"), Ui.Column("Betrag", right: true), Ui.Column("Status") },
                list.Select(e => Ui.RowFor(e.Number + " " + e.House + " " + e.Guest + " " + e.Email, Open(e), e.House, e.Guest, e.Unit, Period(e), B.Money(e.Total, e.Currency), StatusBadge(e.Status))),
                searchPlaceholder: "Haus, Gast, E-Mail oder Nummer …",
                emptyText: tab == "offen" ? "Keine offenen Anfragen." : "Keine Buchungen.")), "Verbund");
    }

    /// <summary>The federation's card on a guest's page: on the central site the stays the houses reported, on a house what the central card index knows.</summary>
    public static async Task<UiNode?> GuestCardAsync(IPluginContext ctx, string email, CancellationToken ct)
    {
        var role = BookingFederation.Role(ctx);
        if (role == BookingFederation.Solo || email.Length == 0) return null;
        List<BookingFederation.RemoteStay> stays;
        string? note = null;
        if (role == BookingFederation.Lead) stays = await BookingFederation.RemoteStaysAsync(ctx.Data, email, ct);
        else
        {
            var (g, error) = await BookingFederation.LookupAsync(ctx, email, ct);
            stays = (g?["stays"] as System.Text.Json.Nodes.JsonArray ?? new()).OfType<System.Text.Json.Nodes.JsonObject>().Select(BookingFederation.RemoteStay.From).ToList();
            note = error is not null ? "Die Zentrale ist gerade nicht erreichbar: " + error : g is null ? "In der Zentrale noch nicht bekannt – kommt mit dem nächsten Abgleich." : null;
        }
        return Ui.Card("In anderen Häusern", Ui.Stack(
            note is null ? null : Ui.Muted(note),
            Ui.Table(new[] { Ui.Column("Haus"), Ui.Column("Unterkunft"), Ui.Column("Zeitraum"), Ui.Column("Betrag", right: true), Ui.Column("Status") },
                stays.Select(x => Ui.Row(x.Site, (x.Property.Length > 0 ? x.Property + " · " : "") + x.Unit,
                    x.Arrival is { } a && x.Departure is { } d ? B.Date(a) + " – " + B.Date(d) : "–", B.Money(x.Total, x.Currency.Length == 3 ? x.Currency : "EUR"),
                    Ui.Badge(BookingStatus.Label(x.Status), BookingStatus.Tone(x.Status)))),
                emptyText: "Keine Aufenthalte in anderen Häusern.")));
    }
}
