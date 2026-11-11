using System.Text;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// Admin → Buchungen → Meldungen: the arrivals of a period with their check-in state, and the registration list as CSV for the authorities (eVisitor,
/// Meldeschein, Alloggiati). Per booking: the travellers as CSV and a registration form as PDF.
/// </summary>
public static class BookingAdminCheckins
{
    private static string R => BookingAdmin.Root;

    public static IAdminSection Register(IAdminSection s) => s
        .Page("checkins", null, ListAsync)
        .Page("checkins/csv", null, RangeCsvAsync)
        .Page("bookings/{id}/travellers.csv", null, BookingCsvAsync)
        .Page("bookings/{id}/meldeschein", null, PdfAsync);

    private static (DateOnly From, DateOnly To) Range(IPluginContext ctx)
    {
        var from = B.Day(ctx.Request.Q("von")) ?? B.Today;
        var to = B.Day(ctx.Request.Q("bis")) ?? from.AddDays(13);
        return to < from ? (to, from) : (from, to);
    }

    private static async Task<List<(Booking B, string Unit, IReadOnlyList<Traveller> People)>> StaysAsync(IPluginContext ctx, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var units = (await new BookingStore(ctx.Data).UnitsAsync(ct)).ToDictionary(u => u.Id, u => u.Name);
        var list = new List<(Booking, string, IReadOnlyList<Traveller>)>();
        foreach (var b in (await BookingFlow.ListAsync(ctx.Data, new[] { BookingStatus.Confirmed }, ct)).Where(b => b.Arrival >= from && b.Arrival <= to))
            list.Add((b, units.GetValueOrDefault(b.UnitId, ""), await BookingCheckin.TravellersAsync(ctx.Data, b.Id, ct)));
        return list;
    }

    private static async Task<PluginResult> ListAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (from, to) = Range(ctx);
        var stays = await StaysAsync(ctx, from, to, ct);
        var q = "?von=" + B.Iso(from) + "&bis=" + B.Iso(to);
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("booking.admin.navCheckins"),
                Ui.Button(ctx.T("booking.admin.allCsv"), R + "checkins/csv" + q, UiButtonKind.Ghost, "ti-table-down", small: true)),
            // the range filter sits right above its list, without a card of its own; the button has the fields' height
            Ui.Form(Ui.Inline(Ui.Input("von", ctx.T("booking.admin.arrivalFrom"), B.Iso(from), UiInputType.Date), Ui.Input("bis", ctx.T("booking.admin.until"), B.Iso(to), UiInputType.Date),
                Ui.Submit(ctx.T("booking.admin.show"), UiButtonKind.Ghost))) with { Action = R + "checkins" },
            Ui.Table(new[] { Ui.Column(ctx.T("booking.admin.arrival")), Ui.Column(ctx.T("booking.admin.colStay")), Ui.Column(ctx.T("booking.admin.colGuest")), Ui.Column(ctx.T("booking.admin.colPersons"), right: true), Ui.Column(ctx.T("booking.admin.colCheckin")), Ui.Column("", actions: true) },
                stays.Select(s => Ui.Row(B.Weekday(ctx.T, s.B.Arrival) + " " + B.Date(s.B.Arrival), s.Unit, Ui.Link(s.B.Name, R + "bookings/" + s.B.Id),
                    B.Num(s.B.Adults + s.B.Children),
                    s.People.Count > 0 ? Ui.Badge(ctx.T("booking.admin.filledN", s.People.Count), UiTone.Success) : Ui.Badge(ctx.T("booking.admin.open"), UiTone.Warning),
                    s.People.Count == 0 ? null : Ui.Inline(Ui.Button("CSV", R + "bookings/" + s.B.Id + "/travellers.csv", UiButtonKind.Ghost, "ti-table-down", small: true),
                        Ui.Button(ctx.T("booking.admin.regForm"), R + "bookings/" + s.B.Id + "/meldeschein", UiButtonKind.Ghost, "ti-file-type-pdf", small: true)))),
                emptyText: ctx.T("booking.admin.noArrivals"))), ctx.T("booking.admin.navCheckins"));
    }

    private static PluginResult Csv(string text, string name) =>
        PluginResult.File(Encoding.UTF8.GetBytes(text), "text/csv; charset=utf-8", name).WithHeader("Cache-Control", "private, no-store");

    private static async Task<PluginResult> RangeCsvAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (from, to) = Range(ctx);
        return Csv(BookingCheckin.Csv((await StaysAsync(ctx, from, to, ct)).Where(s => s.People.Count > 0), ctx.T), "meldungen-" + B.Iso(from) + "-" + B.Iso(to) + ".csv");
    }

    private static async Task<PluginResult> BookingCsvAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!long.TryParse(ctx.Request.R("id"), out var id) || await BookingFlow.GetAsync(ctx.Data, id, ct) is not { } b) return PluginResult.NotFound();
        var unit = await new BookingStore(ctx.Data).UnitAsync(b.UnitId, ct);
        return Csv(BookingCheckin.Csv(new[] { (b, unit?.Name ?? "", (IReadOnlyList<Traveller>)await BookingCheckin.TravellersAsync(ctx.Data, b.Id, ct)) }, ctx.T), "meldung-" + b.Number + ".csv");
    }

    private static async Task<PluginResult> PdfAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!long.TryParse(ctx.Request.R("id"), out var id) || await BookingFlow.GetAsync(ctx.Data, id, ct) is not { } b) return PluginResult.NotFound();
        var store = new BookingStore(ctx.Data);
        var unit = await store.UnitAsync(b.UnitId, ct);
        var house = unit is null ? null : await store.PropertyAsync(unit.PropertyId, ct);
        var people = await BookingCheckin.TravellersAsync(ctx.Data, b.Id, ct);
        var pdf = BookingCheckin.Pdf(await BookingPrint.LetterheadAsync(ctx, ct), b, unit, house, BookingCheckin.ProfileFor(ctx, house), people, ctx.T);
        return BookingPrint.Result(pdf, ctx.T("booking.admin.regFormN", b.Number));
    }

    /// <summary>The check-in card on a booking's page.</summary>
    public static async Task<UiNode?> CardAsync(IPluginContext ctx, Booking b, CancellationToken ct)
    {
        if (b.Status != BookingStatus.Confirmed) return null;
        var row = await ctx.Data.Table(BookingStore.TBookings).GetAsync(b.Id, ct);
        var people = await BookingCheckin.TravellersAsync(ctx.Data, b.Id, ct);
        var store = new BookingStore(ctx.Data);
        var unit = await store.UnitAsync(b.UnitId, ct);
        var house = unit is null ? null : await store.PropertyAsync(unit.PropertyId, ct);
        var profile = BookingCheckin.ProfileFor(ctx, house);
        var cols = BookingCheckin.Profiles[profile].All.Where(k => k is not ("first" or "last")).Take(4).ToList();
        return Ui.Card(ctx.T("booking.admin.onlineCheckin"), Ui.Stack(
            Ui.Inline(people.Count == 0 ? Ui.Badge(ctx.T("booking.admin.notFilled"), UiTone.Warning) : Ui.Badge(ctx.T("booking.admin.filledAt", BookingMail.Local(row?.Timestamp("checkin_at"))), UiTone.Success)),
            row?.Text("arrival_time") is { Length: > 0 } at ? Ui.Text(ctx.T("booking.admin.arrivalTime", at)) : null,
            row?.Text("travel") is { Length: > 0 } tr ? Ui.Text(ctx.T("booking.admin.travelBy", tr)) : null,
            people.Count == 0 ? null : Ui.Table(new[] { Ui.Column(ctx.T("booking.admin.name")) }.Concat(cols.Select(c => Ui.Column(BookingCheckin.Label(ctx.T, c)))),
                people.Select(t => Ui.Row(new object?[] { t.Name }.Concat(cols.Select(c => (object?)(c == "born" ? B.Date(t.Get(c)) : t.Get(c)))).ToArray()))),
            people.Count == 0 ? null : Ui.Inline(Ui.Button("CSV", R + "bookings/" + b.Id + "/travellers.csv", UiButtonKind.Ghost, "ti-table-down", small: true),
                Ui.Button(ctx.T("booking.admin.regFormPdf"), R + "bookings/" + b.Id + "/meldeschein", UiButtonKind.Ghost, "ti-file-type-pdf", small: true)),
            Ui.Muted(ctx.T("booking.admin.sent", row?.Text("mails") is { Length: > 0 } m ? string.Join(", ", m.Split(',').Select(x => x.Replace("booking.", ""))) : ctx.T("booking.admin.noGuestMail")))));
    }
}
