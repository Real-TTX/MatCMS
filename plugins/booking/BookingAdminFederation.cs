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

    private static string When(IPluginContext ctx, DateTime d) => d == DateTime.MinValue ? ctx.T("booking.fed.never") : BookingMail.Local(d);

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
    private static string Period(IPluginContext ctx, Entry e) => e.Arrival is { } a && e.Departure is { } d ? B.Date(ctx.T, a) + " – " + B.Date(ctx.T, d) + " (" + (d.DayNumber - a.DayNumber) + ")" : "–";
    private static UiNode StatusBadge(IPluginContext ctx, string s) => Ui.Badge(BookingStatus.Label(ctx.T, s), BookingStatus.Tone(s));
    private static UiColumn[] Columns(IPluginContext ctx) => new[]
    {
        Ui.Column(ctx.T("booking.bookings.colNumber")), Ui.Column(ctx.T("booking.bookings.colHouse")), Ui.Column(ctx.T("booking.bookings.colGuest")), Ui.Column(ctx.T("booking.bookings.colUnit")),
        Ui.Column(ctx.T("booking.bookings.colPeriod")), Ui.Column(ctx.T("booking.bookings.colAmount"), right: true), Ui.Column(ctx.T("booking.bookings.colStatus"))
    };
    private static bool IsOpen(Entry e) => e.Status is BookingStatus.Request or BookingStatus.Offer;

    private static UiNode Tabs(IPluginContext ctx, string active) => Ui.Tabs(Ui.Tab(ctx.T("booking.fed.tabOverview"), R + "federation", active == "overview"), Ui.Tab(ctx.T("booking.bookings.all"), R + "federation/bookings", active == "all"));

    private static async Task<PluginResult> PageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var role = BookingFederation.Role(ctx);
        var self = R + "federation";
        var settings = Ui.Button(ctx.T("booking.fed.settings"), R + "_settings?group=" + Uri.EscapeDataString(BookingFederation.Group), UiButtonKind.Ghost, "ti-settings", small: true);

        if (role == BookingFederation.Member)
        {
            if (ctx.Request.IsPost)
            {
                if (ctx.Request.Action == "probe")
                    return await BookingFederation.ProbeAsync(ctx, ct) is { } why
                        ? PluginResult.Redirect(self, ctx.T("booking.fed.noConnection", BookingStrings.Shown(ctx.T, why)), true)
                        : PluginResult.Redirect(self, ctx.T("booking.fed.connected"));
                var r = await BookingFederation.SyncAsync(ctx, ct);
                return r.Ok
                    ? PluginResult.Redirect(self, ctx.T("booking.fed.synced", r.Pushed, r.Stays, r.Pulled))
                    : PluginResult.Redirect(self, ctx.T("booking.fed.syncFailed", BookingStrings.Shown(ctx.T, r.Error)), true);
            }
            var (g, s) = await BookingFederation.PendingAsync(ctx.Data, ct);
            var error = await BookingFederation.MetaAsync(ctx.Data, "fed.last_error", ct);
            var ok = await BookingFederation.MetaTimeAsync(ctx.Data, "fed.last_ok", ct);
            return PluginResult.Page(Ui.Stack(
                Ui.PageHead(null, ctx.T("booking.fed.title"), settings),
                error.Length > 0 ? Ui.Alert(ctx.T("booking.fed.lastFailed", BookingStrings.Shown(ctx.T, error)), UiTone.Warning) : null,
                Ui.Card(ctx.T("booking.fed.memberCard"), Ui.Stack(
                    Ui.Stats(
                        Ui.Stat(ctx.T("booking.fed.central"), ctx.Settings.Get(BookingFederation.UrlKey) is { Length: > 0 } u ? u : ctx.T("booking.fed.notSet")),
                        Ui.Stat(ctx.T("booking.fed.lastSynced"), When(ctx, ok)),
                        Ui.Stat(ctx.T("booking.fed.waiting"), (g == 1 ? ctx.T("booking.fed.oneGuest") : ctx.T("booking.fed.guests", g)) + ", " + (s == 1 ? ctx.T("booking.fed.oneBooking") : ctx.T("booking.fed.bookings", s)))),
                    Ui.Form(Ui.FormActions(Ui.Submit(ctx.T("booking.fed.syncNow"), UiButtonKind.Primary, "action", "sync"), Ui.Submit(ctx.T("booking.fed.probe"), UiButtonKind.Ghost, "action", "probe")))))), ctx.T("booking.fed.title"));
        }

        if (role != BookingFederation.Lead)
            return PluginResult.Page(Ui.Stack(
                Ui.PageHead(null, ctx.T("booking.fed.title")),
                Ui.Card(ctx.T("booking.fed.solo"), Ui.Stack(
                    Ui.Text(ctx.T("booking.fed.soloText")),
                    Ui.Button(ctx.T("booking.fed.setRole"), R + "_settings?group=" + Uri.EscapeDataString(BookingFederation.Group), UiButtonKind.Primary, "ti-affiliate")))), ctx.T("booking.fed.title"));

        // ---- the central site ----
        var all = await EntriesAsync(ctx, ct);
        var today = B.Today;
        var open = all.Where(IsOpen).OrderBy(e => e.Created ?? DateTime.MaxValue).ToList();
        var arriving = all.Where(e => e.Status == BookingStatus.Confirmed && e.Arrival == today).ToList();
        var leaving = all.Where(e => e.Status == BookingStatus.Confirmed && e.Departure == today).ToList();
        var seen = await BookingFederation.SeenAsync(ctx.Data, ct);
        var houses = all.Select(e => e.House).Distinct().Count();

        var inbox = Ui.Table(Columns(ctx),
            open.Select(e => Ui.RowFor(e.Number + " " + e.House + " " + e.Guest + " " + e.Email, Open(e), e.House, e.Guest, e.Unit, Period(ctx, e), B.Money(ctx.T, e.Total, e.Currency), StatusBadge(ctx, e.Status))),
            emptyText: ctx.T("booking.bookings.noOpen"));
        UiNode Today(string title, List<Entry> list) => Ui.Card(title, Ui.Table(new[] { Ui.Column(ctx.T("booking.bookings.colHouse")), Ui.Column(ctx.T("booking.bookings.colGuest")), Ui.Column(ctx.T("booking.bookings.colUnit")), Ui.Column(ctx.T("booking.bookings.colPersons"), right: true) },
            list.Select(e => Ui.Row(e.House, e.Guest, e.Unit, B.Num(e.Persons))), emptyText: ctx.T("booking.fed.nobody")));

        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("booking.fed.title"), settings),
            Tabs(ctx, "overview"),
            Ui.Stats(
                Ui.Stat(ctx.T("booking.fed.openRequests"), B.Num(open.Count), tone: open.Count > 0 ? UiTone.Warning : UiTone.Default),
                Ui.Stat(ctx.T("booking.fed.arrivalsToday"), B.Num(arriving.Count)),
                Ui.Stat(ctx.T("booking.fed.departuresToday"), B.Num(leaving.Count)),
                Ui.Stat(ctx.T("booking.fed.houses"), B.Num(Math.Max(houses, seen.Count + 1)))),
            Ui.Card(ctx.T("booking.fed.openAll"), inbox),
            Ui.Grid(320, Today(ctx.T("booking.fed.arrivalsToday"), arriving), Today(ctx.T("booking.fed.departuresToday"), leaving)),
            // how to connect a house is said only while none is connected
            Ui.Card(ctx.T("booking.fed.housesIn"), Ui.Stack(
                Ui.Table(new[] { Ui.Column(ctx.T("booking.fed.colKey")), Ui.Column(ctx.T("booking.fed.colLastReport")) },
                    seen.Select(x => Ui.Row(x.Site, x.At is { } at ? BookingMail.Local(at) : "–")),
                    emptyText: ctx.T("booking.fed.noHouse", ctx.Site.Origin))))), ctx.T("booking.fed.title"));
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
            Ui.PageHead(null, ctx.T("booking.fed.title")),
            Tabs(ctx, "all"),
            Ui.Tabs(Ui.Tab(ctx.T("booking.bookings.tabOpen"), self, tab == "offen"), Ui.Tab(ctx.T("booking.bookings.tabComing"), self + "?tab=kommend", tab == "kommend"), Ui.Tab(ctx.T("booking.bookings.tabAll"), self + "?tab=alle", tab == "alle")),
            Ui.Table(Columns(ctx),
                list.Select(e => Ui.RowFor(e.Number + " " + e.House + " " + e.Guest + " " + e.Email, Open(e), e.House, e.Guest, e.Unit, Period(ctx, e), B.Money(ctx.T, e.Total, e.Currency), StatusBadge(ctx, e.Status))),
                searchPlaceholder: ctx.T("booking.fed.search"),
                emptyText: tab == "offen" ? ctx.T("booking.bookings.noOpen") : ctx.T("booking.bookings.none"))), ctx.T("booking.fed.title"));
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
            note = error is not null ? ctx.T("booking.fed.unreachable", BookingStrings.Shown(ctx.T, error)) : g is null ? ctx.T("booking.fed.unknownThere") : null;
        }
        return Ui.Card(ctx.T("booking.fed.otherHouses"), Ui.Stack(
            note is null ? null : Ui.Muted(note),
            Ui.Table(new[] { Ui.Column(ctx.T("booking.bookings.colHouse")), Ui.Column(ctx.T("booking.bookings.colUnit")), Ui.Column(ctx.T("booking.bookings.colPeriod")), Ui.Column(ctx.T("booking.bookings.colAmount"), right: true), Ui.Column(ctx.T("booking.bookings.colStatus")) },
                stays.Select(x => Ui.Row(x.Site, (x.Property.Length > 0 ? x.Property + " · " : "") + x.Unit,
                    x.Arrival is { } a && x.Departure is { } d ? B.Date(ctx.T, a) + " – " + B.Date(ctx.T, d) : "–", B.Money(ctx.T, x.Total, x.Currency.Length == 3 ? x.Currency : "EUR"),
                    Ui.Badge(BookingStatus.Label(ctx.T, x.Status), BookingStatus.Tone(x.Status)))),
                emptyText: ctx.T("booking.fed.noOtherStays"))));
    }
}
