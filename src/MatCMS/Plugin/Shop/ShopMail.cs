using System.Globalization;
using System.Net;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>The shop's mails: their default wording (the operator edits it under Mailvorlagen) and how an order is turned into the values they use.</summary>
internal static class ShopMail
{
    public const string Placed = "shop.order.placed";
    public const string Paid = "shop.order.paid";
    public const string Shipped = "shop.order.shipped";
    public const string Cancelled = "shop.order.cancelled";
    public const string Refunded = "shop.order.refunded";
    public const string AdminOrder = "shop.admin.order";

    private static readonly MailPlaceholder[] Common =
    {
        new("order_number", "Die Bestellnummer"), new("customer_name", "Name des Kunden"), new("total", "Gesamtbetrag, mit Währung"),
        new("order_url", "Link zur Bestellung (für Gäste mit Zugangsschlüssel)"), new("site_name", "Name der Website"),
        new("billing_address", "Rechnungsadresse, mehrzeilig"), new("shipping_address", "Lieferadresse, mehrzeilig"),
        new("subtotal", "Zwischensumme"), new("shipping_cost", "Versandkosten"), new("shipping_method", "Gewählte Versandart"),
    };

    private static readonly MailLoop ItemsLoop = new("items", "Einmal je Bestellposition",
        new MailPlaceholder[] { new("title", "Name des Artikels (mit Variante)"), new("qty", "Menge"), new("unit", "Einzelpreis"), new("line", "Zeilensumme") });

    private static IReadOnlyList<MailPlaceholder> With(params MailPlaceholder[] extra) => Common.Concat(extra).ToList();

    public static IReadOnlyList<MailTemplateSpec> Specs { get; } = new MailTemplateSpec[]
    {
        new(Placed, "Shop: Bestellbestätigung", "Geht an den Kunden, sobald die Bestellung aufgegeben ist und noch bezahlt werden muss (Vorkasse).",
            "Ihre Bestellung {{order_number}} bei {{site_name}}",
            """
            Hallo {{customer_name}},

            vielen Dank für Ihre Bestellung {{order_number}}!

            {{#items}}{{qty}} × {{title}}  –  {{line}}
            {{/items}}
            Zwischensumme: {{subtotal}}
            Versand ({{shipping_method}}): {{shipping_cost}}
            Gesamtbetrag: {{total}}

            {{payment_info}}

            Ihre Bestellung können Sie jederzeit hier ansehen:
            {{order_url}}

            Rechnungsadresse:
            {{billing_address}}

            Lieferadresse:
            {{shipping_address}}

            Mit freundlichen Grüßen
            {{site_name}}
            """) { Placeholders = With(new MailPlaceholder("payment_info", "Wie bezahlt wird (z. B. die Bankverbindung bei Vorkasse)")), Loops = new[] { ItemsLoop } },

        new(Paid, "Shop: Zahlung erhalten", "Geht an den Kunden, wenn die Bestellung bezahlt ist — mit Downloads und Rechnung.",
            "Zahlung erhalten – Bestellung {{order_number}}",
            """
            Hallo {{customer_name}},

            wir haben Ihre Zahlung für die Bestellung {{order_number}} erhalten. Vielen Dank!

            {{#items}}{{qty}} × {{title}}  –  {{line}}
            {{/items}}
            Gesamtbetrag: {{total}}

            {{downloads}}
            Ihre Rechnung: {{invoice_url}}

            Ihre Bestellung: {{order_url}}

            Mit freundlichen Grüßen
            {{site_name}}
            """) { Placeholders = With(new MailPlaceholder("downloads", "Die Download-Links digitaler Artikel (leer, wenn es keine gibt)"), new MailPlaceholder("invoice_url", "Link zur Rechnung")), Loops = new[] { ItemsLoop } },

        new(Shipped, "Shop: Versandbestätigung", "Geht an den Kunden, wenn die Ware versendet wurde.",
            "Ihre Bestellung {{order_number}} ist unterwegs",
            """
            Hallo {{customer_name}},

            Ihre Bestellung {{order_number}} wurde versendet.

            Sendungsnummer: {{tracking}}

            Lieferadresse:
            {{shipping_address}}

            Ihre Bestellung: {{order_url}}

            Mit freundlichen Grüßen
            {{site_name}}
            """) { Placeholders = With(new MailPlaceholder("tracking", "Die Sendungsnummer (leer, wenn keine eingetragen wurde); kennt der Shop den Paketdienst, steht der Link zur Sendungsverfolgung in der Zeile darunter"), new MailPlaceholder("tracking_url", "Nur der Link zur Sendungsverfolgung (leer, wenn es keinen gibt)"), new MailPlaceholder("carrier", "Der Paketdienst (leer, wenn keiner gewählt wurde)")), Loops = new[] { ItemsLoop } },

        new(Cancelled, "Shop: Bestellung storniert", "Geht an den Kunden, wenn eine unbezahlte Bestellung storniert wurde (auch nach Ablauf der Zahlungsfrist).",
            "Ihre Bestellung {{order_number}} wurde storniert",
            """
            Hallo {{customer_name}},

            Ihre Bestellung {{order_number}} wurde storniert. Es ist keine Zahlung erfolgt und es entstehen keine Kosten.

            Wenn Sie die Artikel noch möchten, bestellen Sie gern erneut auf {{site_name}}.

            Mit freundlichen Grüßen
            {{site_name}}
            """) { Placeholders = Common },

        new(Refunded, "Shop: Erstattung", "Geht an den Kunden, wenn Geld zurückgezahlt wurde.",
            "Erstattung zur Bestellung {{order_number}}",
            """
            Hallo {{customer_name}},

            wir haben {{refund_amount}} zur Bestellung {{order_number}} erstattet. Je nach Zahlungsart dauert es einige Tage, bis der Betrag bei Ihnen ankommt.

            {{credit_info}}

            Mit freundlichen Grüßen
            {{site_name}}
            """) { Placeholders = With(new MailPlaceholder("refund_amount", "Der erstattete Betrag, mit Währung"), new MailPlaceholder("credit_info", "Hinweis mit Link zur Gutschrift (leer, wenn es keine Rechnung gab)")) },

        new(AdminOrder, "Shop: Neue Bestellung (an den Betreiber)", "Geht an die Benachrichtigungsadresse des Shops, wenn eine Bestellung bezahlt wurde.",
            "Neue bezahlte Bestellung {{order_number}} ({{total}})",
            """
            Eine Bestellung ist bezahlt:

            {{order_number}} – {{customer_name}} – {{total}}

            {{#items}}{{qty}} × {{title}}  –  {{line}}
            {{/items}}
            Lieferadresse:
            {{shipping_address}}

            Im Admin: {{admin_url}}
            """) { Placeholders = With(new MailPlaceholder("admin_url", "Link zur Bestellung im Admin")), Loops = new[] { ItemsLoop } },
    };

    private static string OrderUrl(IPluginContext ctx, Order o) => $"{ctx.Site.Origin}/shop/order/{Uri.EscapeDataString(o.Number)}?t={Uri.EscapeDataString(o.AccessToken)}";

    /// <summary>The values a mail of this order uses (the ones every template shares; callers add their own).</summary>
    public static Dictionary<string, string> Values(IPluginContext ctx, Order o, IReadOnlyList<OrderItem> items, string site)
    {
        string M(long c) => Money.Format(c, o.Currency, o.Locale);
        var cfg = ShopConfig.From(ctx.Settings);
        var trackUrl = ShopTracking.UrlOf(cfg, o);
        return new Dictionary<string, string>
        {
            ["order_number"] = o.Number, ["customer_name"] = string.IsNullOrWhiteSpace(o.Name) ? o.Billing.Name : o.Name, ["total"] = M(o.TotalCents),
            ["order_url"] = OrderUrl(ctx, o), ["site_name"] = site, ["billing_address"] = o.Billing.Lines(), ["shipping_address"] = o.Shipping.Lines(),
            ["subtotal"] = M(o.SubtotalCents), ["shipping_cost"] = M(o.ShippingCents), ["shipping_method"] = string.IsNullOrWhiteSpace(o.ShippingMethod) ? "—" : o.ShippingMethod,
            ["tracking"] = trackUrl.Length > 0 && !string.Equals(trackUrl, o.Tracking, StringComparison.OrdinalIgnoreCase) ? o.Tracking + "\n" + trackUrl : o.Tracking, ["tracking_url"] = trackUrl, ["carrier"] = ShopTracking.NameOf(cfg, o), ["payment_info"] = "", ["downloads"] = "", ["invoice_url"] = "", ["admin_url"] = "", ["refund_amount"] = "", ["credit_info"] = ""
        };
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>> Lists(Order o, IReadOnlyList<OrderItem> items) =>
        new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>
        {
            ["items"] = items.Select(i => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["title"] = i.DisplayTitle, ["qty"] = i.Qty.ToString(CultureInfo.InvariantCulture),
                ["unit"] = Money.Format(i.UnitCents, o.Currency, o.Locale), ["line"] = Money.Format(i.LineCents, o.Currency, o.Locale)
            }).ToList()
        };

    /// <summary>
    /// Sends one of the order mails to the customer and writes what came of it into the order's history. A mail that does not go out never stops the order:
    /// the history says so, and "Mail erneut senden" in the admin tries again.
    /// </summary>
    public static async Task<bool> SendAsync(IPluginContext ctx, ShopStore store, string key, Order order, IReadOnlyList<OrderItem> items,
        Action<Dictionary<string, string>>? extra = null, string? to = null, CancellationToken ct = default)
    {
        var recipient = to ?? order.Email;
        if (string.IsNullOrWhiteSpace(recipient)) return false;
        var values = Values(ctx, order, items, ctx.Site.Name);
        extra?.Invoke(values);
        var result = await ctx.Mail.SendAsync(key, new[] { recipient }, values, Lists(order, items), ct: ct);
        var label = Specs.FirstOrDefault(s => s.Key == key)?.Name ?? key;
        await store.AddEventAsync(order.Id, result.Ok ? "mail" : "mail-failed",
            result.Ok ? $"{label}: an {recipient} gesendet" : $"{label}: Senden an {recipient} fehlgeschlagen – {result.Error}", "system", null, ct);
        if (!result.Ok) ctx.Log($"Mail {key} zu {order.Number}: {result.Error}");
        return result.Ok;
    }

    /// <summary>The links to a paid order's downloads as lines of text for a mail.</summary>
    public static string DownloadLines(IPluginContext ctx, Order o, IReadOnlyList<ProductFile> files, string locale)
    {
        if (files.Count == 0) return "";
        var lines = files.Select(f => $"• {f.FileName}: {ctx.Site.Origin}/shop/download/{Uri.EscapeDataString(o.Number)}/{f.Id}?t={Uri.EscapeDataString(o.AccessToken)}");
        return (locale == "en" ? "Your downloads:" : "Ihre Downloads:") + "\n" + string.Join("\n", lines) + "\n";
    }

    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
}
