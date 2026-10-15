using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// A small PDF writer: A4 pages, text in Helvetica (regular and bold), lines and grey boxes — exactly what an invoice needs and nothing more. It writes a plain PDF 1.4 by hand
/// (no library, no font files: Helvetica is one of the 14 fonts every PDF reader has) so the image carries no new dependency. Text is WinAnsi, i.e. Western European: German,
/// French, Spanish … letters and the euro sign are right; a character outside it prints as <c>?</c>.
/// </summary>
internal sealed class PdfWriter
{
    public const double PageWidth = 595, PageHeight = 842;

    private readonly List<PdfPage> _pages = new();
    public IReadOnlyList<PdfPage> Pages => _pages;
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";

    public PdfPage AddPage() { var p = new PdfPage(); _pages.Add(p); return p; }

    // ---- the font metrics (Adobe's AFM widths of Helvetica and Helvetica-Bold, for 32…126) ---------------------

    private static readonly int[] Regular =
    {
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584
    };

    private static readonly int[] Bold =
    {
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584
    };

    /// <summary>The letter an accented one is measured as (the AFM widths of the accented forms equal their base letters').</summary>
    private static char BaseLetter(char c) => c switch
    {
        'À' or 'Á' or 'Â' or 'Ã' or 'Ä' or 'Å' => 'A', 'Ç' => 'C', 'È' or 'É' or 'Ê' or 'Ë' => 'E', 'Ì' or 'Í' or 'Î' or 'Ï' => 'I', 'Ñ' => 'N',
        'Ò' or 'Ó' or 'Ô' or 'Õ' or 'Ö' or 'Ø' => 'O', 'Ù' or 'Ú' or 'Û' or 'Ü' => 'U', 'Ý' or 'Ÿ' => 'Y', 'Š' => 'S', 'Ž' => 'Z',
        'à' or 'á' or 'â' or 'ã' or 'ä' or 'å' => 'a', 'ç' => 'c', 'è' or 'é' or 'ê' or 'ë' => 'e', 'ì' or 'í' or 'î' or 'ï' => 'i', 'ñ' => 'n',
        'ò' or 'ó' or 'ô' or 'õ' or 'ö' or 'ø' => 'o', 'ù' or 'ú' or 'û' or 'ü' => 'u', 'ý' or 'ÿ' => 'y', 'š' => 's', 'ž' => 'z', _ => c
    };

    /// <summary>The byte WinAnsi uses for a character, or <c>?</c> when it has none.</summary>
    public static byte WinAnsi(char c)
    {
        if (c is >= ' ' and <= '~') return (byte)c;
        if (c is >= ' ' and <= 'ÿ') return (byte)c;                       // Latin-1 and WinAnsi agree here
        return c switch
        {
            '€' => 0x80, '‚' => 0x82, 'ƒ' => 0x83, '„' => 0x84, '…' => 0x85, '†' => 0x86, '‡' => 0x87, 'ˆ' => 0x88, '‰' => 0x89, 'Š' => 0x8A, '‹' => 0x8B, 'Œ' => 0x8C,
            'Ž' => 0x8E, '‘' => 0x91, '’' => 0x92, '“' => 0x93, '”' => 0x94, '•' => 0x95, '–' => 0x96, '—' => 0x97, '™' => 0x99, 'š' => 0x9A, '›' => 0x9B, 'œ' => 0x9C,
            'ž' => 0x9E, 'Ÿ' => 0x9F,
            '−' => (byte)'-',                                                       // the minus sign Money.Format writes
            '\t' => (byte)' ', '­' => (byte)'-',
            _ => (byte)'?'
        };
    }

    /// <summary>The width of a text in points.</summary>
    public static double Width(string text, double size, bool bold)
    {
        double units = 0;
        foreach (var raw in text)
        {
            var c = BaseLetter(raw);
            if (c is >= ' ' and <= '~') units += (bold ? Bold : Regular)[c - ' '];
            else units += raw switch
            {
                'ß' => 611, 'Æ' => 1000, 'æ' => 889, 'Œ' => 1000, 'œ' => 944, '€' => 556, '—' => 1000, '…' => 1000, '•' => 350, '–' => 556, '−' => 333, '°' => 400,
                '„' or '“' or '”' => bold ? 500 : 333, '‚' or '‘' or '’' => bold ? 278 : 222, '©' or '®' => 737, '·' => 278, ' ' => 278, _ => 556
            };
        }
        return units * size / 1000.0;
    }

    /// <summary>Breaks a text into lines no wider than <paramref name="maxWidth"/> (at spaces; a word wider than a line is cut). Line breaks in the text are kept.</summary>
    public static List<string> Wrap(string text, double maxWidth, double size, bool bold)
    {
        var lines = new List<string>();
        foreach (var paragraph in (text ?? "").Replace("\r", "").Split('\n'))
        {
            if (paragraph.Trim().Length == 0) { lines.Add(""); continue; }
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var w = word;
                while (Width(w, size, bold) > maxWidth)               // a single word that does not fit: cut it where it does
                {
                    var cut = w.Length - 1;
                    while (cut > 1 && Width(w[..cut], size, bold) > maxWidth) cut--;
                    if (line.Length > 0) { lines.Add(line.ToString()); line.Clear(); }
                    lines.Add(w[..cut]);
                    w = w[cut..];
                }
                var candidate = line.Length == 0 ? w : line + " " + w;
                if (line.Length > 0 && Width(candidate, size, bold) > maxWidth) { lines.Add(line.ToString()); line.Clear(); line.Append(w); }
                else { line.Clear(); line.Append(candidate); }
            }
            lines.Add(line.ToString());
        }
        return lines;
    }

    // ---- writing the file ----------------------------------------------------------------------------------

    /// <summary>A PDF string literal for a text: parentheses and backslash escaped, everything outside printable ASCII as an octal escape (so the file stays 7-bit clean).</summary>
    internal static string Literal(string text)
    {
        var sb = new StringBuilder("(");
        foreach (var ch in text)
        {
            var b = WinAnsi(ch);
            if (b is (byte)'(' or (byte)')' or (byte)'\\') sb.Append('\\').Append((char)b);
            else if (b is < 32 or > 126) sb.Append('\\').Append(System.Convert.ToString(b, 8).PadLeft(3, '0'));
            else sb.Append((char)b);
        }
        return sb.Append(')').ToString();
    }

    public byte[] ToBytes()
    {
        if (_pages.Count == 0) AddPage();
        var buffer = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s) { var bytes = Encoding.Latin1.GetBytes(s); buffer.Write(bytes, 0, bytes.Length); }
        void Object(int number, string body) { offsets.Add(buffer.Position); Write($"{number} 0 obj\n{body}\nendobj\n"); }

        Write("%PDF-1.4\n%âãÏÓ\n");
        // 1 catalogue, 2 page tree, 3 + 4 fonts, then a page and its content per page, then the info dictionary.
        const int FirstPage = 5;
        var kids = string.Join(" ", Enumerable.Range(0, _pages.Count).Select(i => $"{FirstPage + i * 2} 0 R"));
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, $"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>");
        Object(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Object(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        for (var i = 0; i < _pages.Count; i++)
        {
            var content = _pages[i].Content;
            Object(FirstPage + i * 2, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(PageWidth)} {Num(PageHeight)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {FirstPage + i * 2 + 1} 0 R >>");
            Object(FirstPage + i * 2 + 1, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        var info = FirstPage + _pages.Count * 2;
        Object(info, $"<< /Title {Literal(Title)} /Author {Literal(Author)} /Producer (MatCMS Shop) /CreationDate (D:{DateTime.UtcNow:yyyyMMddHHmmss}Z) >>");

        var xref = buffer.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write(o.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return buffer.ToArray();
    }

    internal static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>One page. Positions are in points from the TOP-left corner (the way a layout is thought of); the PDF's own origin is bottom-left and is handled here.</summary>
internal sealed class PdfPage
{
    private readonly StringBuilder _content = new();
    public string Content => _content.ToString();

    private static string N(double v) => PdfWriter.Num(v);
    private static double Y(double top) => PdfWriter.PageHeight - top;

    /// <summary>Text with its baseline at <paramref name="baseline"/> points from the top.</summary>
    public void Text(double x, double baseline, string text, double size, bool bold = false, double gray = 0)
    {
        if (text.Length == 0) return;
        _content.Append($"BT /{(bold ? "F2" : "F1")} {N(size)} Tf {N(gray)} g {N(x)} {N(Y(baseline))} Td {PdfWriter.Literal(text)} Tj ET\n");
    }

    /// <summary>Text whose RIGHT edge is at <paramref name="right"/>.</summary>
    public void TextRight(double right, double baseline, string text, double size, bool bold = false, double gray = 0) =>
        Text(right - PdfWriter.Width(text, size, bold), baseline, text, size, bold, gray);

    public void Line(double x1, double top1, double x2, double top2, double width = 0.5, double gray = 0.75) =>
        _content.Append($"{N(gray)} G {N(width)} w {N(x1)} {N(Y(top1))} m {N(x2)} {N(Y(top2))} l S\n");

    /// <summary>A filled grey rectangle (<paramref name="top"/> is its upper edge).</summary>
    public void Box(double x, double top, double width, double height, double gray = 0.95) =>
        _content.Append($"{N(gray)} g {N(x)} {N(Y(top + height))} {N(width)} {N(height)} re f\n");
}

/// <summary>Draws an <see cref="InvoiceDoc"/> as a PDF: the same document the HTML page shows, laid out for paper.</summary>
internal static class ShopPdf
{
    private const double Left = 50, Right = 545, Top = 52, Bottom = 780, Line = 12.5;

    public static byte[] Render(InvoiceDoc d)
    {
        var pdf = new PdfWriter { Title = d.Title + " " + d.Number, Author = d.SellerName };
        var page = pdf.AddPage();
        var y = Top;

        // ---- head: the title and the numbers on the left, the seller on the right ----------------------
        page.Text(Left, y + 14, d.Title, 20, bold: true);
        var left = y + 36;
        void Pair(string label, string value)
        {
            page.Text(Left, left, label, 9.5, gray: 0.4);
            page.Text(Left + PdfWriter.Width(label, 9.5, false) + 4, left, value, 9.5, bold: true);
            left += Line;
        }
        Pair(d.IsCredit ? d.L("Gutschriftnummer:", "Credit note number:") : d.L("Rechnungsnummer:", "Invoice number:"), d.Number);
        Pair(d.IsCredit ? d.L("Datum:", "Date:") : d.L("Rechnungsdatum:", "Invoice date:"), InvoiceDoc.D(d.IssuedUtc));
        if (d.IsCredit) Pair(d.L("zu Rechnung:", "For invoice:"), d.InvoiceNumber);
        Pair(d.L("Bestellnummer:", "Order number:"), d.OrderNumber);
        if (!d.IsCredit && d.PaidUtc is { } paid) Pair(d.L("Leistungs-/Zahldatum:", "Date of supply / payment:"), InvoiceDoc.D(paid));

        var right = y + 4;
        void SellerLine(string text, bool bold = false)
        {
            foreach (var l in PdfWriter.Wrap(text, 230, 9.5, bold)) { page.TextRight(Right, right + 8, l, 9.5, bold); right += Line; }
        }
        if (d.SellerName.Length > 0) SellerLine(d.SellerName, true);
        if (d.SellerAddress.Length > 0) SellerLine(d.SellerAddress);
        if (d.SellerEmail.Length > 0) SellerLine(d.SellerEmail);
        if (d.SellerPhone.Length > 0) SellerLine(d.SellerPhone);
        if (d.SellerVatId.Length > 0) SellerLine(d.L("USt-IdNr.: ", "VAT ID: ") + d.SellerVatId);
        if (d.SellerTaxNumber.Length > 0) SellerLine(d.L("Steuernummer: ", "Tax number: ") + d.SellerTaxNumber);
        y = Math.Max(left, right) + 22;

        // ---- addresses ---------------------------------------------------------------------------------
        var top = y;
        page.Text(Left, y, d.L("Rechnungsadresse", "Billing address"), 8.5, gray: 0.4);
        var by = y + 13;
        foreach (var l in d.Billing.Split('\n')) { page.Text(Left, by, l.TrimEnd('\r'), 10); by += Line; }
        var sy = top;
        if (d.Shipping.Length > 0)
        {
            page.Text(300, sy, d.L("Lieferadresse", "Shipping address"), 8.5, gray: 0.4);
            sy += 13;
            foreach (var l in d.Shipping.Split('\n')) { page.Text(300, sy, l.TrimEnd('\r'), 10); sy += Line; }
        }
        y = Math.Max(by, sy) + 14;

        // ---- the table ---------------------------------------------------------------------------------
        double colTotal = Right, colTax = 487, colUnit = d.ShowTax ? 432 : 487, colQty = d.ShowTax ? 372 : 427, textX = Left + 22, textW = colQty - 46 - textX;
        void Header()
        {
            page.Box(Left, y - 11, Right - Left, 18, 0.93);
            page.Text(Left + 3, y + 1.5, "#", 8.5, true);
            page.Text(textX, y + 1.5, d.L("Bezeichnung", "Description"), 8.5, true);
            page.TextRight(colQty, y + 1.5, d.L("Menge", "Qty"), 8.5, true);
            page.TextRight(colUnit, y + 1.5, d.L("Einzelpreis", "Unit price"), 8.5, true);
            if (d.ShowTax) page.TextRight(colTax, y + 1.5, d.L("MwSt.", "VAT"), 8.5, true);
            page.TextRight(colTotal - 3, y + 1.5, d.L("Gesamt", "Total"), 8.5, true);
            y += 20;
        }
        PdfPage NewPage()
        {
            var p = pdf.AddPage();
            y = Top;
            return p;
        }
        Header();
        var pos = 1;
        foreach (var l in d.Lines)
        {
            var title = PdfWriter.Wrap(l.Text, textW, 9.5, false);
            var height = title.Count * Line + (l.Sku.Length > 0 ? Line - 1 : 0) + 7;
            if (y + height > Bottom) { page = NewPage(); Header(); }
            var rowTop = y;
            var baseline = y + 10;
            if (l.Qty > 0) page.Text(Left + 3, baseline, (pos++).ToString(CultureInfo.InvariantCulture), 9.5);
            foreach (var t in title) { page.Text(textX, baseline, t, 9.5); baseline += Line; }
            if (l.Sku.Length > 0) { page.Text(textX, baseline - 1, d.L("Art.-Nr. ", "SKU ") + l.Sku, 8, gray: 0.4); }
            if (l.Qty > 0)
            {
                page.TextRight(colQty, rowTop + 10, l.Qty.ToString(CultureInfo.InvariantCulture), 9.5);
                page.TextRight(colUnit, rowTop + 10, d.M(l.UnitCents), 9.5);
            }
            if (d.ShowTax && l.TaxBp is { } bp) page.TextRight(colTax, rowTop + 10, Money.Percent(bp) + " %", 9.5);
            page.TextRight(colTotal - 3, rowTop + 10, d.M(l.TotalCents), 9.5);
            y += height;
            page.Line(Left, y - 3, Right, y - 3, 0.4, 0.85);
        }
        y += 8;

        // ---- sums, kept together on one page ------------------------------------------------------------
        var sumRows = d.Groups.Count * 2 + 1;
        if (y + sumRows * 14 + 10 > Bottom) { page = NewPage(); }
        const double sumLeft = 330;
        foreach (var g in d.Groups)
        {
            page.Text(sumLeft, y + 8, d.L("Netto ", "Net ") + Money.Percent(g.Bp) + " %", 9, gray: 0.35);
            page.TextRight(Right - 3, y + 8, d.M(g.NetCents), 9, gray: 0.35);
            y += 13;
            page.Text(sumLeft, y + 8, d.L("MwSt. ", "VAT ") + Money.Percent(g.Bp) + " %", 9, gray: 0.35);
            page.TextRight(Right - 3, y + 8, d.M(g.TaxCents), 9, gray: 0.35);
            y += 13;
        }
        page.Line(sumLeft, y + 2, Right, y + 2, 1, 0);
        page.Text(sumLeft, y + 16, d.IsCredit ? d.L("Erstattungsbetrag", "Amount refunded") : d.L("Gesamtbetrag", "Total"), 11, bold: true);
        page.TextRight(Right - 3, y + 16, d.M(d.TotalCents), 11, bold: true);
        y += 34;

        // ---- notes ------------------------------------------------------------------------------------
        void Paragraph(string text, double size, double gray)
        {
            foreach (var l in PdfWriter.Wrap(text, Right - Left, size, false))
            {
                if (y + size + 4 > Bottom) { page = NewPage(); }
                page.Text(Left, y + size, l, size, gray: gray);
                y += size + 3.5;
            }
            y += 4;
        }
        if (d.SmallBusiness) Paragraph(d.L("Gemäß § 19 UStG wird keine Umsatzsteuer berechnet.", "No VAT is charged (small business exemption)."), 9, 0.35);
        else if (d.TaxNote.Length > 0) Paragraph(ShopTax.NoteText(d.TaxNote, d.En), 9, 0.35);
        if (!string.IsNullOrWhiteSpace(d.BankInfo))
        {
            var lines = PdfWriter.Wrap(d.BankInfo, Right - Left - 20, 9, false);
            var boxHeight = lines.Count * 12 + 16;
            if (y + boxHeight > Bottom) { page = NewPage(); }
            page.Box(Left, y, Right - Left, boxHeight, 0.95);
            var ly = y + 16;
            foreach (var l in lines) { page.Text(Left + 10, ly, l, 9); ly += 12; }
            y += boxHeight + 14;
        }
        if (d.Footer.Length > 0) Paragraph(d.Footer, 8, 0.4);

        // ---- the footer of every page ---------------------------------------------------------------------
        for (var i = 0; i < pdf.Pages.Count; i++)
        {
            var p = pdf.Pages[i];
            p.Line(Left, 800, Right, 800, 0.4, 0.8);
            p.Text(Left, 814, (d.SellerName.Length > 0 ? d.SellerName + " · " : "") + d.Title + " " + d.Number, 8, gray: 0.45);
            p.TextRight(Right, 814, d.L("Seite ", "Page ") + (i + 1) + " / " + pdf.Pages.Count, 8, gray: 0.45);
        }
        return pdf.ToBytes();
    }
}
