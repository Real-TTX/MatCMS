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
    public const string CurrencyKey = "currency";

    public static string Currency(IPluginContext ctx) => ctx.Settings.Get(CurrencyKey) is { Length: 3 } c ? c : "EUR";

    /// <inheritdoc />
    public void Configure(IPluginBuilder b)
    {
        BookingStore.Declare(b.Data);
        b.Settings.Choice(CurrencyKey, "Währung", "EUR", new[]
        {
            new FieldOption("EUR", "Euro (€)"), new FieldOption("CHF", "Schweizer Franken"), new FieldOption("GBP", "Pfund (£)"), new FieldOption("USD", "US-Dollar ($)")
        }, "Eine Währung für alle Preise dieser Website.");
        BookingAdmin.Configure(b);
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
