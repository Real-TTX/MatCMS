using System.Globalization;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// Holiday homes, villas and apartments (docs/booking-plugin.md). Phase B1: houses, units, seasons, prices, fees, availability without double bookings,
/// and the price of a stay — in the admin ("Preis prüfen") and for the website as JSON (<see cref="PricePath"/>), the base of the request form in B2.
/// <para>A native plugin, written against <c>MatCMS.Plugin.Abstractions</c> only — no host type, no reflection-based JSON over its own types.</para>
/// </summary>
public sealed class BookingPlugin : IPlugin
{
    public const string PricePath = "/buchung/preis";
    public const string CurrencyKey = "currency", NotifyKey = "notifyEmail", HoldKey = "holdHours", OfferKey = "offerDays", PaymentInfoKey = "paymentInfo",
        PrivacyKey = "privacyUrl";

    public static string Currency(IPluginContext ctx) => ctx.Settings.Get(CurrencyKey) is { Length: 3 } c ? c : "EUR";
    public static string NotifyAddress(IPluginContext ctx) => (ctx.Settings.Get(NotifyKey) ?? "").Trim();
    public static int HoldHours(IPluginContext ctx) => double.TryParse(ctx.Settings.Get(HoldKey), NumberStyles.Number, CultureInfo.InvariantCulture, out var h) && h >= 1 ? Math.Clamp((int)h, 1, 336) : 48;
    public static int OfferDays(IPluginContext ctx) => double.TryParse(ctx.Settings.Get(OfferKey), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 1 ? Math.Clamp((int)d, 1, 30) : 3;

    /// <inheritdoc />
    public void Configure(IPluginBuilder b)
    {
        BookingStore.Declare(b.Data);
        BookingStrings.Register(b);
        // the first setting names the group "Allgemein": settings in front of the first named group were not reachable in the back office
        b.Settings.Add(new SettingSpec(CurrencyKey, "booking.set.currency", SettingKind.Choice)
        {
            Group = "booking.set.groupGeneral", Default = "EUR", Help = "booking.set.currencyHelp",
            Options = new[] { new FieldOption("EUR", "booking.set.eur"), new FieldOption("CHF", "booking.set.chf"), new FieldOption("GBP", "booking.set.gbp"), new FieldOption("USD", "booking.set.usd") }
        });
        b.Settings.Text(NotifyKey, "booking.set.notify", "", "booking.set.notifyHelp")
            .Number(HoldKey, "booking.set.hold", 48, 1, 336, "booking.set.holdHelp")
            .Number(OfferKey, "booking.set.offer", 3, 1, 30)
            .TextArea(PaymentInfoKey, "booking.set.paymentInfo", "", "booking.set.paymentInfoHelp")
            .Text(PrivacyKey, "booking.set.privacy", "/datenschutz", "booking.set.privacyHelp");
        BookingPayments.Declare(b.Settings);
        BookingPrint.Declare(b.Settings);
        BookingCheckin.Declare(b.Settings);
        BookingGuestMails.Declare(b.Settings);
        BookingCleaning.Declare(b.Settings);
        BookingFederation.Declare(b.Settings);
        foreach (var spec in BookingMail.Specs.Concat(BookingGuestMails.Specs)) b.MailTemplate(spec);
        BookingAdmin.Configure(b);
        BookingPublic.Configure(b);
        BookingBlocks.Configure(b);
        BookingIcal.Configure(b);
        BookingCleaning.Configure(b);
        BookingFederation.Configure(b);
        b.Job("guest-mails", JobSchedule.EveryInterval(TimeSpan.FromHours(1)), BookingGuestMails.RunAsync);
        b.Job("checkin-purge", JobSchedule.EveryInterval(TimeSpan.FromHours(24)), async (ctx, ct) => await BookingCheckin.PurgeAsync(ctx, ct));

        // what nobody answered in time runs out and frees the calendar; the guest is told
        b.Job("expire", JobSchedule.EveryInterval(TimeSpan.FromMinutes(15)), async (ctx, ct) =>
        {
            var store = new BookingStore(ctx.Data);
            foreach (var x in await BookingFlow.ExpireAsync(ctx.Data, ct))
            {
                var unit = await store.UnitAsync(x.UnitId, ct);
                var house = unit is null ? null : await store.PropertyAsync(unit.PropertyId, ct);
                await BookingFlow.NoteAsync(ctx.Data, x.Id, "Mail an den Gast: " + await BookingMail.SendAsync(ctx, BookingMail.Expired, x.Email, BookingMail.Values(ctx, x, unit, house, null), ct), ct);
            }
        });
        b.Job("payment-reminders", JobSchedule.EveryInterval(TimeSpan.FromHours(6)), BookingPayments.RemindAsync);
        b.Admin.Attention(async (ctx, ct) =>
        {
            var notes = new List<AttentionNote>();
            var open = (await BookingFlow.ListAsync(ctx.Data, new[] { BookingStatus.Request }, ct)).Count;
            if (open > 0)
                notes.Add(new AttentionNote(open == 1 ? ctx.T("booking.attn.oneRequest") : ctx.T("booking.attn.requests", open), ctx.T("booking.attn.requestsText"), BookingAdmin.Root + "bookings")
                { Tone = UiTone.Warning, Icon = "ti-calendar-question" });
            foreach (var f in await BookingIcal.FeedsAsync(ctx.Data, ct))
            {
                if (f.Conflicts.Length > 0)
                    notes.Add(new AttentionNote(ctx.T("booking.attn.doubleBooking", f.Name), f.Conflicts.Replace("\n", " · "), BookingAdmin.Root + "channels") { Tone = UiTone.Error, Icon = "ti-calendar-x" });
                // an error that lasts: the portal's calendar has not been read for over two hours
                else if (f.LastError.Length > 0 && f.Url.Length > 0 && (f.LastOk is null || f.LastOk < DateTime.UtcNow.AddHours(-2)))
                    notes.Add(new AttentionNote(ctx.T("booking.attn.feedNotRead", f.Name), f.LastError, BookingAdmin.Root + "channels") { Tone = UiTone.Warning, Icon = "ti-calendar-off" });
            }
            return notes;
        });
        b.Route(PricePath, PriceAsync, new PublicRouteOptions { Methods = PluginMethod.Get });

        // the row the write lock of HoldAsync updates — a transaction that starts with a write queues behind another instead of being refused
        b.Lifecycle.OnLoad(async ctx =>
        {
            var meta = ctx.Data.Table(BookingStore.TMeta);
            if (await meta.FindAsync(DataQuery.Everything.Eq("name", "__lock"), default) is null)
                await meta.InsertAsync(new Dictionary<string, object?> { ["name"] = "__lock", ["value"] = "" });
        });
    }

    /// <summary>
    /// <c>GET /buchung/preis?einheit=&amp;anreise=&amp;abreise=&amp;erwachsene=&amp;kinder=&amp;extras=</c> — the bill of a stay and whether it is free. The unit by its
    /// slug or number; extras = ids of optional fees, comma-separated. Answers only for active units and never says WHY a night is taken.
    /// </summary>
    private static async Task<PluginResult> PriceAsync(IPluginContext ctx, CancellationToken ct)
    {
        var q = ctx.Request;
        var store = new BookingStore(ctx.Data);
        var key = q.Q("einheit").Trim();
        var unit = long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? await store.UnitAsync(id, ct) : await store.UnitBySlugAsync(B.Slug(key), ct);
        if (unit is null || !unit.Active) return PluginResult.Json(new JsonObject { ["ok"] = false, ["error"] = ctx.T("booking.err.unknownUnit") }, 404);
        if (B.Day(q.Q("anreise")) is not { } arrival || B.Day(q.Q("abreise")) is not { } departure)
            return PluginResult.Json(new JsonObject { ["ok"] = false, ["error"] = ctx.T("booking.err.datesNeeded") }, 400);
        int N(string k, int def) => int.TryParse(q.Q(k), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 50) : def;
        var extras = q.Q("extras").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out var e) ? e : 0).Where(e => e > 0).ToHashSet();
        var stay = new Stay(arrival, departure, N("erwachsene", 2), N("kinder", 0), extras);
        var quote = (await store.QuoteAsync(unit.Id, stay, Currency(ctx), ct, ctx.T))!;
        var free = stay.Nights is > 0 and <= Pricing.MaxNights && await store.IsFreeAsync(unit, arrival, departure, ct);
        var cur = Currency(ctx);
        var lines = new JsonArray();
        foreach (var l in quote.Lines)
            lines.Add(new JsonObject { ["kind"] = l.Kind, ["text"] = l.Text, ["quantity"] = l.Quantity, ["unit"] = l.Unit, ["amount"] = l.Amount, ["amountText"] = B.Money(l.Amount, cur), ["detail"] = l.Detail });
        var problems = new JsonArray();
        foreach (var p in quote.Problems) problems.Add(p);
        var days = new JsonArray();
        foreach (var d in quote.ArrivalDays) days.Add(d);
        return PluginResult.Json(new JsonObject
        {
            ["ok"] = quote.Ok && free, ["available"] = free, ["unit"] = unit.Slug, ["nights"] = stay.Nights, ["currency"] = cur,
            ["total"] = quote.Total, ["totalText"] = B.Money(quote.Total, cur), ["minNights"] = quote.MinNights, ["arrivalDays"] = days,
            ["problems"] = problems, ["lines"] = lines
        }).WithHeader("Cache-Control", "no-store");
    }
}
