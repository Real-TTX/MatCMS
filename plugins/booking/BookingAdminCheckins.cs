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
            Ui.PageHead("Die Anreisen eines Zeitraums mit dem Stand des Online-Check-ins – und die Liste für die Meldung", "Meldungen",
                Ui.Button("Alle als CSV", R + "checkins/csv" + q, UiButtonKind.Ghost, "ti-table-down", small: true)),
            Ui.Card(Ui.Form(Ui.Inline(Ui.Input("von", "Anreise von", B.Iso(from), UiInputType.Date), Ui.Input("bis", "bis", B.Iso(to), UiInputType.Date),
                Ui.Submit("Anzeigen", UiButtonKind.Ghost, small: true))) with { Action = R + "checkins" }),
            Ui.Table(new[] { Ui.Column("Anreise"), Ui.Column("Unterkunft"), Ui.Column("Gast"), Ui.Column("Personen", right: true), Ui.Column("Check-in"), Ui.Column("", actions: true) },
                stays.Select(s => Ui.Row(B.Weekday(s.B.Arrival) + " " + B.Date(s.B.Arrival), s.Unit, Ui.Link(s.B.Name, R + "bookings/" + s.B.Id),
                    B.Num(s.B.Adults + s.B.Children),
                    s.People.Count > 0 ? Ui.Badge("ausgefüllt (" + s.People.Count + ")", UiTone.Success) : Ui.Badge("offen", UiTone.Warning),
                    s.People.Count == 0 ? null : Ui.Inline(Ui.Button("CSV", R + "bookings/" + s.B.Id + "/travellers.csv", UiButtonKind.Ghost, "ti-table-down", small: true),
                        Ui.Button("Meldeschein", R + "bookings/" + s.B.Id + "/meldeschein", UiButtonKind.Ghost, "ti-file-type-pdf", small: true)))),
                emptyText: "Keine Anreisen in diesem Zeitraum."),
            Ui.Muted("Die Angaben werden nach der eingestellten Frist automatisch gelöscht (Einstellungen → Check-in und Gästemails).")), "Meldungen");
    }

    private static PluginResult Csv(string text, string name) =>
        PluginResult.File(Encoding.UTF8.GetBytes(text), "text/csv; charset=utf-8", name).WithHeader("Cache-Control", "private, no-store");

    private static async Task<PluginResult> RangeCsvAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (from, to) = Range(ctx);
        return Csv(BookingCheckin.Csv((await StaysAsync(ctx, from, to, ct)).Where(s => s.People.Count > 0)), "meldungen-" + B.Iso(from) + "-" + B.Iso(to) + ".csv");
    }

    private static async Task<PluginResult> BookingCsvAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!long.TryParse(ctx.Request.R("id"), out var id) || await BookingFlow.GetAsync(ctx.Data, id, ct) is not { } b) return PluginResult.NotFound();
        var unit = await new BookingStore(ctx.Data).UnitAsync(b.UnitId, ct);
        return Csv(BookingCheckin.Csv(new[] { (b, unit?.Name ?? "", (IReadOnlyList<Traveller>)await BookingCheckin.TravellersAsync(ctx.Data, b.Id, ct)) }), "meldung-" + b.Number + ".csv");
    }

    private static async Task<PluginResult> PdfAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!long.TryParse(ctx.Request.R("id"), out var id) || await BookingFlow.GetAsync(ctx.Data, id, ct) is not { } b) return PluginResult.NotFound();
        var store = new BookingStore(ctx.Data);
        var unit = await store.UnitAsync(b.UnitId, ct);
        var house = unit is null ? null : await store.PropertyAsync(unit.PropertyId, ct);
        var people = await BookingCheckin.TravellersAsync(ctx.Data, b.Id, ct);
        var pdf = BookingCheckin.Pdf(await BookingPrint.LetterheadAsync(ctx, ct), b, unit, house, BookingCheckin.ProfileFor(ctx, house), people);
        return BookingPrint.Result(pdf, "Meldeschein " + b.Number);
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
        return Ui.Card("Online-Check-in", Ui.Stack(
            people.Count == 0 ? Ui.Badge("noch nicht ausgefüllt", UiTone.Warning) : Ui.Badge("ausgefüllt " + BookingMail.Local(row?.Timestamp("checkin_at")), UiTone.Success),
            row?.Text("arrival_time") is { Length: > 0 } at ? Ui.Text("Ankunft: " + at) : null,
            row?.Text("travel") is { Length: > 0 } tr ? Ui.Text("Anreise mit: " + tr) : null,
            people.Count == 0 ? null : Ui.Table(new[] { Ui.Column("Name") }.Concat(cols.Select(c => Ui.Column(BookingCheckin.Label(c)))),
                people.Select(t => Ui.Row(new object?[] { t.Name }.Concat(cols.Select(c => (object?)(c == "born" ? B.Date(t.Get(c)) : t.Get(c)))).ToArray()))),
            people.Count == 0 ? null : Ui.Inline(Ui.Button("CSV", R + "bookings/" + b.Id + "/travellers.csv", UiButtonKind.Ghost, "ti-table-down", small: true),
                Ui.Button("Meldeschein (PDF)", R + "bookings/" + b.Id + "/meldeschein", UiButtonKind.Ghost, "ti-file-type-pdf", small: true)),
            Ui.Muted("Gesendet: " + (row?.Text("mails") is { Length: > 0 } m ? string.Join(", ", m.Split(',').Select(x => x.Replace("booking.", ""))) : "noch keine Gästemail"))));
    }
}
