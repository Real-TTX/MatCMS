using System.Globalization;
using System.Net;
using System.Text;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The packing slip (Lieferschein): what goes in the parcel, for the person packing it — the delivery address, the order number, the lines with quantity and article number, the customer's note.
/// No prices (a gift's recipient must not read them), no tax. A standalone page that prints on A4; the logo and accent colour are the invoice's. Downloads are left off the list: nothing of them ships.
/// </summary>
internal static class ShopSlip
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string Html(IPluginContext ctx, ShopConfig cfg, Order order, IReadOnlyList<OrderItem> items)
    {
        var de = !string.Equals(order.Locale, "en", StringComparison.OrdinalIgnoreCase);
        string L(string d, string e) => de ? d : e;
        var ship = order.Shipping.IsComplete ? order.Shipping : order.Billing;
        var accent = ShopInvoice.AccentOf(cfg.InvoiceAccent);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"").Append(de ? "de" : "en").Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"robots\" content=\"noindex\">");
        sb.Append("<title>").Append(E(L("Lieferschein", "Packing slip"))).Append(' ').Append(E(order.Number)).Append("</title><style>");
        sb.Append("body{font:14px/1.5 -apple-system,Segoe UI,Helvetica,Arial,sans-serif;color:#111;margin:0;padding:32px;background:#fff}.slip{max-width:820px;margin:0 auto}");
        sb.Append("h1{font-size:22px;margin:0 0 4px;color:").Append(accent).Append("}.logo{display:block;max-height:64px;max-width:240px;margin:0 0 14px}.muted{color:#666}");
        sb.Append(".top{display:flex;justify-content:space-between;gap:24px;flex-wrap:wrap;margin-bottom:28px}.to{font-size:16px;line-height:1.45;margin-bottom:24px}");
        sb.Append("table{width:100%;border-collapse:collapse}th,td{padding:8px 6px;border-bottom:1px solid #ddd;text-align:left;vertical-align:top}th{font-size:12px;text-transform:uppercase;letter-spacing:.04em;color:#555;border-bottom:2px solid #111}");
        sb.Append(".r{text-align:right}.box{margin-top:24px;padding:12px 14px;background:#f6f6f6;border-radius:6px;border-left:3px solid ").Append(accent).Append(";white-space:pre-line}");
        sb.Append(".chk{width:28px}.chk span{display:inline-block;width:14px;height:14px;border:1.5px solid #555;border-radius:3px}.noprint a{margin-right:14px}@media print{body{padding:0}.noprint{display:none}}</style></head><body><div class=\"slip\">");
        sb.Append("<p class=\"noprint\"><a href=\"javascript:window.print()\">").Append(E(L("Drucken", "Print"))).Append("</a></p>");

        sb.Append("<div class=\"top\"><div>");
        if (ShopPublic.SafeUrl(cfg.SellerLogo).Length > 0) sb.Append("<img class=\"logo\" src=\"").Append(E(ShopPublic.SafeUrl(cfg.SellerLogo))).Append("\" alt=\"").Append(E(cfg.SellerName)).Append("\">");
        sb.Append("<h1>").Append(E(L("Lieferschein", "Packing slip"))).Append("</h1>");
        sb.Append("<div>").Append(E(L("Bestellnummer: ", "Order number: "))).Append("<strong>").Append(E(order.Number)).Append("</strong></div>");
        sb.Append("<div>").Append(E(L("Bestelldatum: ", "Order date: "))).Append(order.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)).Append("</div>");
        if (order.ShippingMethod.Length > 0) sb.Append("<div class=\"muted\">").Append(E(L("Versandart: ", "Shipping: "))).Append(E(order.ShippingMethod)).Append("</div>");
        sb.Append("</div><div style=\"text-align:right\">");
        if (cfg.SellerName.Length > 0) sb.Append("<strong>").Append(E(cfg.SellerName)).Append("</strong><br>");
        if (cfg.SellerAddress.Length > 0) sb.Append(E(cfg.SellerAddress).Replace("\n", "<br>").Replace("\r", "")).Append("<br>");
        if (cfg.SellerEmail.Length > 0) sb.Append(E(cfg.SellerEmail)).Append("<br>");
        if (cfg.SellerPhone.Length > 0) sb.Append(E(cfg.SellerPhone));
        sb.Append("</div></div>");

        sb.Append("<div class=\"muted\">").Append(E(L("Lieferadresse", "Ship to"))).Append("</div><div class=\"to\"><strong>").Append(E(ship.Name)).Append("</strong>");
        foreach (var line in new[] { ship.Line1, ship.Line2, (ship.Zip + " " + ship.City).Trim(), Countries.Name(ship.Country, de ? "de" : "en") }.Where(l => l.Trim().Length > 0)) sb.Append("<br>").Append(E(line));
        if (ship.Phone.Length > 0 || order.Phone.Length > 0) sb.Append("<br><span class=\"muted\">").Append(E(ship.Phone.Length > 0 ? ship.Phone : order.Phone)).Append("</span>");
        sb.Append("</div>");

        var goods = items.Where(i => i.Kind != ProductKind.Digital).ToList();
        sb.Append("<table><thead><tr><th class=\"chk\"></th><th>").Append(E(L("Artikel", "Item"))).Append("</th><th>").Append(E(L("Art.-Nr.", "SKU"))).Append("</th><th class=\"r\">").Append(E(L("Menge", "Qty"))).Append("</th></tr></thead><tbody>");
        foreach (var i in goods)
        {
            sb.Append("<tr><td class=\"chk\"><span></span></td><td>").Append(E(i.Title));
            if (i.VariantTitle.Length > 0) sb.Append("<div class=\"muted\">").Append(E(i.VariantTitle)).Append("</div>");
            sb.Append("</td><td>").Append(E(i.Sku)).Append("</td><td class=\"r\"><strong>").Append(i.Qty.ToString(CultureInfo.InvariantCulture)).Append("</strong></td></tr>");
        }
        if (goods.Count == 0) sb.Append("<tr><td colspan=\"4\" class=\"muted\">").Append(E(L("Nichts zu versenden (nur digitale Artikel).", "Nothing to ship (digital items only)."))).Append("</td></tr>");
        sb.Append("</tbody></table>");
        if (order.Note.Length > 0) sb.Append("<div class=\"box\"><strong>").Append(E(L("Hinweis des Kunden", "Customer's note"))).Append("</strong>\n").Append(E(order.Note)).Append("</div>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }
}
