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
        b.Settings.Choice(CurrencyKey, "Währung", "EUR", new[]
        {
            new FieldOption("EUR", "Euro (€)"), new FieldOption("CHF", "Schweizer Franken"), new FieldOption("GBP", "Pfund (£)"), new FieldOption("USD", "US-Dollar ($)")
        }, "Eine Währung für alle Preise dieser Website.");
        b.Settings.Text(NotifyKey, "Neue Anfragen an", "", "E-Mail-Adresse des Gastgebers. Leer = keine Mail, die Anfrage steht trotzdem im Admin.")
            .Number(HoldKey, "Anfrage hält den Zeitraum (Stunden)", 48, 1, 336, "So lange sind die Nächte nach einer Anfrage für den Gast vorgemerkt – danach sind sie wieder frei, wenn nichts passiert ist.")
            .Number(OfferKey, "Angebot gilt (Tage)", 3, 1, 30)
            .TextArea(PaymentInfoKey, "Zahlungshinweis", "", "Steht in der Buchungsbestätigung und auf der Seite des Gastes, z. B. Bankverbindung, Anzahlung und bis wann.")
            .Text(PrivacyKey, "Datenschutzerklärung", "/datenschutz", "Die Seite, auf die das Anfrageformular verweist.");
        foreach (var spec in BookingMail.Specs) b.MailTemplate(spec);
        BookingAdmin.Configure(b);
        BookingPublic.Configure(b);
        BookingBlocks.Configure(b);

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
        b.Admin.Attention(async (ctx, ct) =>
        {
            var open = (await BookingFlow.ListAsync(ctx.Data, new[] { BookingStatus.Request }, ct)).Count;
            return open == 0 ? Array.Empty<AttentionNote>() : new[]
            {
                new AttentionNote(open == 1 ? "1 neue Buchungsanfrage" : open + " neue Buchungsanfragen", "Annehmen, Angebot schicken oder ablehnen – sonst läuft die Vormerkung ab.", BookingAdmin.Root + "bookings")
                { Tone = UiTone.Warning, Icon = "ti-calendar-question" }
            };
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
        if (unit is null || !unit.Active) return PluginResult.Json(new JsonObject { ["ok"] = false, ["error"] = "Unbekannte Einheit." }, 404);
        if (B.Day(q.Q("anreise")) is not { } arrival || B.Day(q.Q("abreise")) is not { } departure)
            return PluginResult.Json(new JsonObject { ["ok"] = false, ["error"] = "Bitte An- und Abreise angeben (JJJJ-MM-TT)." }, 400);
        int N(string k, int def) => int.TryParse(q.Q(k), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 50) : def;
        var extras = q.Q("extras").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out var e) ? e : 0).Where(e => e > 0).ToHashSet();
        var stay = new Stay(arrival, departure, N("erwachsene", 2), N("kinder", 0), extras);
        var quote = (await store.QuoteAsync(unit.Id, stay, Currency(ctx), ct))!;
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
