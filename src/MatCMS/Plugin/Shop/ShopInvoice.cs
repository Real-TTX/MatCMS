using System.Globalization;
using System.Net;
using System.Text;

namespace MatCMS.Plugin.Shop;

/// <summary>The two kinds of document an order can have.</summary>
internal static class InvoiceKinds
{
    public const string Invoice = "invoice";
    public const string Credit = "credit";
}

/// <summary>One row of a document. <see cref="Qty"/> 0 = a row without quantity and unit price (a discount); <see cref="TaxBp"/> null = the document shows no VAT column.</summary>
internal sealed record DocLine(string Text, string Sku, int Qty, long UnitCents, int? TaxBp, long TotalCents);

/// <summary>The gross amount of a tax rate and the VAT inside it.</summary>
internal sealed record TaxGroup(int Bp, long GrossCents, long TaxCents)
{
    public long NetCents => GrossCents - TaxCents;
}

/// <summary>
/// Everything a printed document says, in one value: the HTML page and the PDF are both drawn from it, so they can never disagree. It is built ONCE, when the order is
/// paid (an invoice) or money goes back (a credit note), and the result is stored — what the customer was billed does not change when a setting or a product is edited later.
/// </summary>
internal sealed record InvoiceDoc
{
    public string Kind { get; init; } = InvoiceKinds.Invoice;
    public string Number { get; init; } = "";
    /// <summary>For a credit note: the invoice it corrects.</summary>
    public string InvoiceNumber { get; init; } = "";
    public string OrderNumber { get; init; } = "";
    public DateTime IssuedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? PaidUtc { get; init; }
    public bool En { get; init; }
    public string Currency { get; init; } = "EUR";
    public string SellerName { get; init; } = "";
    public string SellerAddress { get; init; } = "";
    public string SellerEmail { get; init; } = "";
    public string SellerPhone { get; init; } = "";
    public string SellerVatId { get; init; } = "";
    public string SellerTaxNumber { get; init; } = "";
    public string Billing { get; init; } = "";
    public string Shipping { get; init; } = "";
    public IReadOnlyList<DocLine> Lines { get; init; } = Array.Empty<DocLine>();
    public IReadOnlyList<TaxGroup> Groups { get; init; } = Array.Empty<TaxGroup>();
    public long TotalCents { get; init; }
    public bool SmallBusiness { get; init; }
    /// <summary>The remark about how the VAT was found ("" for the seller's own rate).</summary>
    public string TaxNote { get; init; } = "";
    public string BankInfo { get; init; } = "";
    public string Footer { get; init; } = "";

    public bool IsCredit => Kind == InvoiceKinds.Credit;
    public string Locale => En ? "en" : "de";
    public bool ShowTax => !SmallBusiness;
    public string L(string de, string en) => En ? en : de;
    public string M(long cents) => Money.Format(cents, Currency, Locale);
    public string Title => IsCredit ? L("Gutschrift", "Credit note") : L("Rechnung", "Invoice");
    public static string D(DateTime d) => d.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
}

/// <summary>Builds the documents of an order and draws the invoice as an HTML page. The PDF is <see cref="ShopPdf"/>.</summary>
internal static class ShopInvoice
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string InvoiceNumber(ShopConfig cfg, long sequence) => cfg.InvoicePrefix + sequence.ToString("00000", CultureInfo.InvariantCulture);
    public static string CreditNumber(ShopConfig cfg, long sequence) => cfg.CreditPrefix + sequence.ToString("00000", CultureInfo.InvariantCulture);

    /// <summary>The VAT rate shipping was charged with: the one frozen in the order, or (an order from before it was stored) today's setting.</summary>
    private static int ShippingBp(Order o, ShopConfig cfg) => o.SmallBusiness ? 0 : o.ShippingTaxBp >= 0 ? o.ShippingTaxBp : cfg.ShippingTaxBp;

    /// <summary>The gross amounts and the VAT inside them per rate — the goods after the coupon, and shipping. Together they are the order's total.</summary>
    public static IReadOnlyList<TaxGroup> Groups(Order o, IReadOnlyList<OrderItem> items, ShopConfig cfg)
    {
        var groups = items.GroupBy(i => i.TaxBp).Select(g => new TaxGroup(g.Key, g.Sum(i => i.NetOfDiscountCents), g.Sum(i => i.TaxCents))).ToList();
        if (o.ShippingCents > 0)
        {
            var bp = ShippingBp(o, cfg);
            var shipTax = Money.TaxIncluded(o.ShippingCents, bp);
            var ix = groups.FindIndex(g => g.Bp == bp);
            if (ix >= 0) groups[ix] = groups[ix] with { GrossCents = groups[ix].GrossCents + o.ShippingCents, TaxCents = groups[ix].TaxCents + shipTax };
            else groups.Add(new TaxGroup(bp, o.ShippingCents, shipTax));
        }
        return groups.OrderBy(g => g.Bp).ToList();
    }

    private static InvoiceDoc Header(Order o, ShopConfig cfg, string number, DateTime issuedUtc, string kind) => new()
    {
        Kind = kind, Number = number, OrderNumber = o.Number, IssuedUtc = issuedUtc, PaidUtc = o.PaidUtc, En = o.Locale == "en", Currency = o.Currency,
        SellerName = cfg.SellerName, SellerAddress = cfg.SellerAddress, SellerEmail = cfg.SellerEmail, SellerPhone = cfg.SellerPhone, SellerVatId = cfg.SellerVatId,
        SellerTaxNumber = cfg.SellerTaxNumber, Billing = o.Billing.Lines(), SmallBusiness = o.SmallBusiness, TaxNote = o.SmallBusiness ? "" : o.TaxNote, Footer = cfg.InvoiceFooter
    };

    /// <summary>The invoice of a paid order.</summary>
    public static InvoiceDoc Build(Order o, IReadOnlyList<OrderItem> items, ShopConfig cfg, string number, DateTime issuedUtc, string bankInfo)
    {
        var en = o.Locale == "en";
        string L(string de, string eng) => en ? eng : de;
        var bp = ShippingBp(o, cfg);
        var lines = new List<DocLine>();
        foreach (var i in items) lines.Add(new DocLine(i.DisplayTitle, i.Sku, i.Qty, i.UnitCents, o.SmallBusiness ? null : i.TaxBp, i.LineCents));
        if (o.DiscountCents > 0)
            lines.Add(new DocLine(o.CouponCode.Length > 0 ? L("Gutschein ", "Coupon ") + o.CouponCode : L("Rabatt", "Discount"), "", 0, 0, null, -o.DiscountCents));
        if (o.ShippingCents > 0 || !string.IsNullOrWhiteSpace(o.ShippingMethod))
            lines.Add(new DocLine(L("Versand", "Shipping") + (string.IsNullOrWhiteSpace(o.ShippingMethod) ? "" : " (" + o.ShippingMethod + ")"), "", 1, o.ShippingCents, o.SmallBusiness ? null : bp, o.ShippingCents));
        return Header(o, cfg, number, issuedUtc, InvoiceKinds.Invoice) with
        {
            Shipping = items.Any(i => i.Kind == ProductKind.Physical) && o.Shipping.IsComplete ? o.Shipping.Lines() : "",
            Lines = lines, Groups = o.SmallBusiness ? Array.Empty<TaxGroup>() : Groups(o, items, cfg), TotalCents = o.TotalCents, BankInfo = bankInfo
        };
    }

    /// <summary>
    /// A credit note for money that went back: <paramref name="amountCents"/>, spread over the VAT rates of the invoice in proportion to what each rate contributed (so the VAT
    /// that is paid back is the VAT that was paid in, to the cent).
    /// </summary>
    public static InvoiceDoc BuildCredit(Order o, IReadOnlyList<OrderItem> items, ShopConfig cfg, string number, DateTime issuedUtc, string invoiceNumber, long amountCents)
    {
        var en = o.Locale == "en";
        string L(string de, string eng) => en ? eng : de;
        var basis = o.SmallBusiness ? new List<TaxGroup> { new(0, o.TotalCents, 0) } : Groups(o, items, cfg).ToList();
        var parts = Money.Allocate(amountCents, basis.Select(g => g.GrossCents).ToList());
        var lines = new List<DocLine>(); var groups = new List<TaxGroup>();
        for (var i = 0; i < basis.Count; i++)
        {
            if (parts[i] == 0) continue;
            var what = L("Erstattung zu Rechnung ", "Refund for invoice ") + invoiceNumber + (basis.Count > 1 && !o.SmallBusiness ? $" ({Money.Percent(basis[i].Bp)} %)" : "");
            lines.Add(new DocLine(what, "", 1, -parts[i], o.SmallBusiness ? null : basis[i].Bp, -parts[i]));
            if (!o.SmallBusiness) groups.Add(new TaxGroup(basis[i].Bp, -parts[i], -Money.TaxIncluded(parts[i], basis[i].Bp)));
        }
        return Header(o, cfg, number, issuedUtc, InvoiceKinds.Credit) with { InvoiceNumber = invoiceNumber, Lines = lines, Groups = groups, TotalCents = -amountCents };
    }

    // ---- the HTML page ------------------------------------------------------------------------------------

    /// <summary>The marker the page's own "PDF" link carries; the answering route puts the real address in (it knows the order key — the stored page never does).</summary>
    public const string PdfMarker = "%%PDF%%";

    public static string Html(InvoiceDoc d)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"").Append(d.En ? "en" : "de").Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(E(d.Title)).Append(' ').Append(E(d.Number)).Append("</title><style>");
        sb.Append("body{font:14px/1.5 -apple-system,Segoe UI,Helvetica,Arial,sans-serif;color:#111;margin:0;padding:32px;background:#fff}");
        sb.Append(".inv{max-width:820px;margin:0 auto}.top{display:flex;justify-content:space-between;gap:24px;flex-wrap:wrap;margin-bottom:28px}");
        sb.Append("h1{font-size:22px;margin:0 0 4px}.muted{color:#666}table{width:100%;border-collapse:collapse;margin:18px 0}");
        sb.Append("th,td{padding:8px 6px;border-bottom:1px solid #ddd;text-align:left;vertical-align:top}th{font-size:12px;text-transform:uppercase;letter-spacing:.04em;color:#555}");
        sb.Append(".r{text-align:right}.sum td{border:0;padding:3px 6px}.total td{font-weight:700;border-top:2px solid #111;padding-top:8px}");
        sb.Append(".box{margin-top:24px;padding:12px 14px;background:#f6f6f6;border-radius:6px;white-space:pre-line}.foot{margin-top:28px;font-size:12px;color:#555;white-space:pre-line}");
        sb.Append(".noprint a{margin-right:14px}@media print{body{padding:0}.noprint{display:none}}</style></head><body><div class=\"inv\">");
        sb.Append("<p class=\"noprint\"><a href=\"").Append(PdfMarker).Append("\">").Append(E(d.L("Als PDF herunterladen", "Download as PDF"))).Append("</a>")
          .Append("<a href=\"javascript:window.print()\">").Append(E(d.L("Drucken", "Print"))).Append("</a></p>");

        sb.Append("<div class=\"top\"><div><h1>").Append(E(d.Title)).Append("</h1>");
        sb.Append("<div>").Append(E(d.IsCredit ? d.L("Gutschriftnummer: ", "Credit note number: ") : d.L("Rechnungsnummer: ", "Invoice number: "))).Append("<strong>").Append(E(d.Number)).Append("</strong></div>");
        sb.Append("<div>").Append(E(d.IsCredit ? d.L("Datum: ", "Date: ") : d.L("Rechnungsdatum: ", "Invoice date: "))).Append(InvoiceDoc.D(d.IssuedUtc)).Append("</div>");
        if (d.IsCredit) sb.Append("<div>").Append(E(d.L("zu Rechnung: ", "For invoice: "))).Append(E(d.InvoiceNumber)).Append("</div>");
        sb.Append("<div>").Append(E(d.L("Bestellnummer: ", "Order number: "))).Append(E(d.OrderNumber)).Append("</div>");
        if (!d.IsCredit && d.PaidUtc is { } paid) sb.Append("<div class=\"muted\">").Append(E(d.L("Leistungs-/Zahldatum: ", "Date of supply / payment: "))).Append(InvoiceDoc.D(paid)).Append("</div>");
        sb.Append("</div><div style=\"text-align:right\">");
        if (d.SellerName.Length > 0) sb.Append("<strong>").Append(E(d.SellerName)).Append("</strong><br>");
        if (d.SellerAddress.Length > 0) sb.Append(E(d.SellerAddress).Replace("\n", "<br>").Replace("\r", "")).Append("<br>");
        if (d.SellerEmail.Length > 0) sb.Append(E(d.SellerEmail)).Append("<br>");
        if (d.SellerPhone.Length > 0) sb.Append(E(d.SellerPhone)).Append("<br>");
        if (d.SellerVatId.Length > 0) sb.Append(E(d.L("USt-IdNr.: ", "VAT ID: "))).Append(E(d.SellerVatId)).Append("<br>");
        if (d.SellerTaxNumber.Length > 0) sb.Append(E(d.L("Steuernummer: ", "Tax number: "))).Append(E(d.SellerTaxNumber));
        sb.Append("</div></div>");

        sb.Append("<div style=\"display:flex;gap:40px;flex-wrap:wrap\"><div><div class=\"muted\">").Append(E(d.L("Rechnungsadresse", "Billing address"))).Append("</div>")
          .Append(E(d.Billing).Replace("\n", "<br>").Replace("\r", "")).Append("</div>");
        if (d.Shipping.Length > 0)
            sb.Append("<div><div class=\"muted\">").Append(E(d.L("Lieferadresse", "Shipping address"))).Append("</div>").Append(E(d.Shipping).Replace("\n", "<br>").Replace("\r", "")).Append("</div>");
        sb.Append("</div>");

        sb.Append("<table><thead><tr><th>#</th><th>").Append(E(d.L("Bezeichnung", "Description"))).Append("</th><th class=\"r\">").Append(E(d.L("Menge", "Qty")))
          .Append("</th><th class=\"r\">").Append(E(d.L("Einzelpreis", "Unit price"))).Append("</th>");
        if (d.ShowTax) sb.Append("<th class=\"r\">").Append(E(d.L("MwSt.", "VAT"))).Append("</th>");
        sb.Append("<th class=\"r\">").Append(E(d.L("Gesamt", "Total"))).Append("</th></tr></thead><tbody>");
        var pos = 1;
        foreach (var l in d.Lines)
        {
            sb.Append("<tr><td>").Append(l.Qty > 0 ? (pos++).ToString(CultureInfo.InvariantCulture) : "").Append("</td><td>").Append(E(l.Text));
            if (l.Sku.Length > 0) sb.Append("<div class=\"muted\">").Append(E(d.L("Art.-Nr. ", "SKU "))).Append(E(l.Sku)).Append("</div>");
            sb.Append("</td><td class=\"r\">").Append(l.Qty > 0 ? l.Qty.ToString(CultureInfo.InvariantCulture) : "").Append("</td><td class=\"r\">").Append(l.Qty > 0 ? E(d.M(l.UnitCents)) : "").Append("</td>");
            if (d.ShowTax) sb.Append("<td class=\"r\">").Append(l.TaxBp is { } bp ? E(Money.Percent(bp)) + " %" : "").Append("</td>");
            sb.Append("<td class=\"r\">").Append(E(d.M(l.TotalCents))).Append("</td></tr>");
        }
        sb.Append("</tbody></table>");

        sb.Append("<table class=\"sum\" style=\"max-width:360px;margin-left:auto\"><tbody>");
        // Net and VAT per rate, as an invoice must show them.
        foreach (var g in d.Groups)
            sb.Append("<tr><td class=\"muted\">").Append(E(d.L("Netto ", "Net "))).Append(E(Money.Percent(g.Bp))).Append(" %</td><td class=\"r muted\">").Append(E(d.M(g.NetCents))).Append("</td></tr>")
              .Append("<tr><td class=\"muted\">").Append(E(d.L("MwSt. ", "VAT "))).Append(E(Money.Percent(g.Bp))).Append(" %</td><td class=\"r muted\">").Append(E(d.M(g.TaxCents))).Append("</td></tr>");
        sb.Append("<tr class=\"total\"><td>").Append(E(d.IsCredit ? d.L("Erstattungsbetrag", "Amount refunded") : d.L("Gesamtbetrag", "Total"))).Append("</td><td class=\"r\">").Append(E(d.M(d.TotalCents))).Append("</td></tr></tbody></table>");

        if (d.SmallBusiness)
            sb.Append("<p class=\"muted\">").Append(E(d.L("Gemäß § 19 UStG wird keine Umsatzsteuer berechnet.", "No VAT is charged (small business exemption)."))).Append("</p>");
        else if (d.TaxNote.Length > 0)
            sb.Append("<p class=\"muted\">").Append(E(ShopTax.NoteText(d.TaxNote, d.En))).Append("</p>");
        if (!string.IsNullOrWhiteSpace(d.BankInfo)) sb.Append("<div class=\"box\">").Append(E(d.BankInfo)).Append("</div>");
        if (d.Footer.Length > 0) sb.Append("<div class=\"foot\">").Append(E(d.Footer)).Append("</div>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }
}
