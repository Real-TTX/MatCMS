using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// The booking confirmation as an A4 PDF on the house's letterhead (admin and the guest's page). The letterhead is a few settings — logo, the lines top
/// right, the footer, a colour; what is left empty comes from the site (its name, logo, address). <see cref="PdfWriter"/> and <see cref="PrintLayout"/>
/// are copies of the club plugin's: a native plugin may not use a host type or another plugin's.
/// </summary>
public static class BookingPrint
{
    public const string Logo = "printLogo", Head = "printHead", Foot = "printFoot", Color = "printColor";

    public static void Declare(ISettingsBuilder s) => s
        .Add(new SettingSpec(Logo, "Logo", SettingKind.Image) { Group = "Druckvorlage (PDF)", Help = "Oben links auf der Buchungsbestätigung. Leer = das Logo der Website." })
        .Add(new SettingSpec(Head, "Briefkopf (rechts oben)", SettingKind.List) { AddLabel = "Zeile", Help = "Die erste Zeile fett – z. B. Name, Anschrift, Telefon, E-Mail. Leer = Name der Website und ihre Adresse." })
        .Add(new SettingSpec(Foot, "Fußzeile", SettingKind.List) { AddLabel = "Zeile", Help = "Bis zu drei Zeilen unten, z. B. Bankverbindung, Steuernummer, Website." })
        .Add(new SettingSpec(Color, "Farbe", SettingKind.Color) { Default = "#334155", Help = "Titel, Überschriften und Linien." });

    internal sealed record Letterhead(string Club, IReadOnlyList<string> Head, IReadOnlyList<string> Foot, (double R, double G, double B) Accent, PluginImage? Logo);

    internal static async Task<Letterhead> LetterheadAsync(IPluginContext ctx, CancellationToken ct)
    {
        var name = ctx.Site.Name;
        var host = Uri.TryCreate(ctx.Site.Origin, UriKind.Absolute, out var u) ? u.Host : "";
        var head = Lines(ctx.Settings.Get(Head), 7);
        if (head.Count == 0) head = new[] { name, host }.Where(x => x.Length > 0).ToList();
        var foot = Lines(ctx.Settings.Get(Foot), 3);
        if (foot.Count == 0) foot = new List<string> { name + (host.Length > 0 ? " · " + host : "") };
        var color = ctx.Settings.Get(Color) is { Length: 7 } c && c[0] == '#' && c[1..].All(Uri.IsHexDigit) ? c : "#334155";
        PluginImage? logo = null;
        foreach (var url in new[] { ctx.Settings.Get(Logo), SiteLogo(ctx.Site) })
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            try { logo = await ctx.Site.LoadImageAsync(url.Trim(), 600, ct); } catch { logo = null; }
            if (logo is not null) break;                                       // an SVG or a missing file gives null: try the next
        }
        return new Letterhead(name, head, foot, (Convert.ToInt32(color.Substring(1, 2), 16) / 255.0, Convert.ToInt32(color.Substring(3, 2), 16) / 255.0, Convert.ToInt32(color.Substring(5, 2), 16) / 255.0), logo);
    }

    private static string SiteLogo(IPluginSite site)
    {
        try { return LogoOf(site); } catch (MissingMethodException) { return ""; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string LogoOf(IPluginSite site) => site.LogoUrl ?? "";

    private static List<string> Lines(string? s, int max) =>
        (s ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(max).ToList();

    /// <summary>The answer: shown in the browser (to print or save), with a file name for saving. Never cached — it names a guest.</summary>
    internal static PluginResult Result(byte[] pdf, string name) =>
        PluginResult.File(pdf, "application/pdf")
            .WithHeader("Content-Disposition", "inline; filename=\"" + B.Slug(name) + ".pdf\"")
            .WithHeader("X-Content-Type-Options", "nosniff").WithHeader("Cache-Control", "private, no-store");

    /// <summary>The booking confirmation: the stay, the bill, the payment plan with what is paid, and the payment note.</summary>
    internal static byte[] Confirmation(Letterhead lh, Booking b, Unit? unit, Property? house, PaymentPlan plan, string paymentInfo, string currency)
    {
        var p = new PrintLayout(lh, "Buchungsbestätigung " + b.Number);
        p.Kicker("Buchungsbestätigung · Nr. " + b.Number);
        p.Title(unit?.Name ?? "Unterkunft");
        p.Facts(("Gast", b.Name), ("Unterkunft", (unit?.Name ?? "") + (house is null ? "" : ", " + house.Name)), ("Adresse", house?.Address.Replace("\n", ", ") ?? ""),
            ("Anreise", B.Weekday(b.Arrival) + " " + B.Date(b.Arrival) + (house?.CheckIn is { Length: > 0 } ci ? ", ab " + ci + " Uhr" : "")),
            ("Abreise", B.Weekday(b.Departure) + " " + B.Date(b.Departure) + (house?.CheckOut is { Length: > 0 } co ? ", bis " + co + " Uhr" : "")),
            ("Nächte", B.Num(b.Nights)), ("Gäste", BookingMail.Persons(b.Adults, b.Children)), ("Status", BookingStatus.Label(b.Status)));
        p.Heading("Preis");
        p.Table(new[] { "Posten", "Berechnung", "Betrag" }, new[] { 0.36, 0.44, 0.2 },
            b.Lines.Select(l => new[] { l.Text, l.Detail, B.Money(l.Amount, currency) }).Append(new[] { "Gesamt", "", B.Money(b.Total, currency) }).ToList());
        if (b.Status == BookingStatus.Confirmed)
        {
            p.Heading("Zahlung");
            var rows = new List<string[]>();
            if (plan.Deposit > 0 && plan.Deposit < b.Total) rows.Add(new[] { "Anzahlung", "fällig am " + B.Date(plan.DepositDue), B.Money(plan.Deposit, currency) });
            rows.Add(new[] { plan.Deposit > 0 && plan.Deposit < b.Total ? "Restbetrag" : "Gesamtbetrag", "fällig am " + B.Date(plan.Deposit > 0 && plan.Deposit < b.Total ? plan.RestDue : plan.DepositDue), B.Money(b.Total - (plan.Deposit < b.Total ? plan.Deposit : 0), currency) });
            if (plan.Paid > 0) rows.Add(new[] { "Bereits bezahlt", "", B.Money(plan.Paid, currency) });
            rows.Add(new[] { "Noch offen", "", B.Money(Math.Max(0, b.Total - plan.Paid), currency) });
            p.Table(new[] { "", "", "Betrag" }, new[] { 0.36, 0.44, 0.2 }, rows);
            if (paymentInfo.Trim().Length > 0) p.Body(paymentInfo.Trim() + "\nVerwendungszweck: " + b.Number);
        }
        return p.ToBytes();
    }
}

/// <summary>Flows text over A4 pages: the full letterhead on the first, a slim head on the others, the footer and page numbers on all.</summary>
internal sealed class PrintLayout
{
    private const double Left = 56, Right = 539, Bottom = 772;
    private readonly PdfWriter _pdf;
    private readonly BookingPrint.Letterhead _lh;
    private readonly string _title;
    private PdfPage _page;
    private double _y;

    public PrintLayout(BookingPrint.Letterhead lh, string title)
    {
        _lh = lh; _title = title;
        _pdf = new PdfWriter { Title = title, Author = lh.Club };
        _page = _pdf.AddPage();
        _y = Letterhead();
    }

    private double Letterhead()
    {
        const double top = 40;
        var left = top;
        if (_lh.Logo is { Width: > 0, Height: > 0 } logo)
        {
            var scale = Math.Min(160.0 / logo.Width, 62.0 / logo.Height);           // as large as fits, never stretched
            var h = logo.Height * scale;
            _page.Image(_pdf.AddImage(logo.Jpeg, logo.Width, logo.Height), Left, top, logo.Width * scale, h);
            left = top + h;
        }
        else
            foreach (var l in PdfWriter.Wrap(_lh.Club, 250, 15, true)) { _page.TextRgb(Left, left + 15, l, 15, _lh.Accent, true); left += 19; }
        var right = top;
        for (var i = 0; i < _lh.Head.Count; i++)
        {
            var first = i == 0;
            var size = first ? 9.5 : 8.5;
            foreach (var l in PdfWriter.Wrap(_lh.Head[i], 240, size, first)) { _page.TextRight(Right, right + 9, l, size, first, first ? 0 : 0.35); right += first ? 12.5 : 11; }
        }
        var line = Math.Max(left, right) + 10;
        _page.LineRgb(Left, line, Right, line, 1.2, _lh.Accent);
        return line + 28;
    }

    private void NewPage()
    {
        _page = _pdf.AddPage();
        _page.Text(Left, 44, _lh.Club, 8.5, true, 0.35);
        var t = _title;
        while (t.Length > 4 && PdfWriter.Width(t, 8.5, false) > 260) t = t[..^5] + "…";
        _page.TextRight(Right, 44, t, 8.5, false, 0.35);
        _page.LineRgb(Left, 52, Right, 52, 0.8, _lh.Accent);
        _y = 76;
    }

    private void Need(double height) { if (_y + height > Bottom) NewPage(); }

    public void Kicker(string text)
    {
        if (text.Length == 0) return;
        Need(16);
        _page.TextRgb(Left, _y + 9, text, 9, _lh.Accent, true);
        _y += 16;
    }

    public void Title(string text)
    {
        foreach (var l in PdfWriter.Wrap(text, Right - Left, 19, true)) { Need(24); _page.Text(Left, _y + 18, l, 19, true); _y += 24; }
        _y += 8;
    }

    /// <summary>Label/value lines; empty values are left out.</summary>
    public void Facts(params (string Label, string Value)[] facts)
    {
        const double labelW = 70, size = 10, line = 13.5;
        var any = false;
        foreach (var (label, value) in facts.Where(f => f.Value.Trim().Length > 0))
        {
            var lines = PdfWriter.Wrap(value, Right - Left - labelW, size, false);
            Need(lines.Count * line);
            _page.Text(Left, _y + 10, label, 9, false, 0.45);
            foreach (var l in lines) { _page.Text(Left + labelW, _y + 10, l, size); _y += line; }
            any = true;
        }
        if (any) _y += 10;
    }

    public void Heading(string text)
    {
        _y += 8;
        Need(48);                                                                 // a heading never stays alone at the foot of a page
        _page.TextRgb(Left, _y + 12, text, 12.5, _lh.Accent, true);
        _y += 18;
        _page.Line(Left, _y, Right, _y, 0.5, 0.8);
        _y += 12;
    }

    private static readonly Regex Marker = new(@"^\s*([-•*–]|\d{1,3}[.)]|[a-zA-Z][)]|TOP\s*\d{1,3}[.:]?)\s+", RegexOptions.CultureInvariant);

    /// <summary>Plain text as typed: blank lines separate paragraphs, a line that starts like a list item ("-", "1.", "a)", "TOP 3") hangs its continuation lines.</summary>
    public void Body(string text)
    {
        const double size = 10.5, line = 14.5;
        var gap = false;
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            if (raw.Trim().Length == 0) { if (!gap) _y += 7; gap = true; continue; }
            gap = false;
            var m = Marker.Match(raw);
            string mark = "", rest = raw.Trim();
            double indent = 0;
            if (m.Success)
            {
                mark = m.Groups[1].Value is "-" or "*" or "–" ? "•" : m.Groups[1].Value;
                rest = raw[m.Length..].Trim();
                indent = Math.Max(16, PdfWriter.Width(mark, size, false) + 7);
            }
            var lines = PdfWriter.Wrap(rest, Right - Left - indent, size, false);
            for (var i = 0; i < lines.Count; i++)
            {
                Need(line);
                if (i == 0 && mark.Length > 0) _page.Text(Left, _y + 11, mark, size);
                _page.Text(Left + indent, _y + 11, lines[i], size);
                _y += line;
            }
        }
        _y += 6;
    }

    /// <summary>A grey box with a short note (the reader's own answer).</summary>
    public void Box(string text)
    {
        var lines = PdfWriter.Wrap(text, Right - Left - 24, 10, false);
        var h = lines.Count * 13.5 + 16;
        _y += 6;
        Need(h);
        _page.Box(Left, _y, Right - Left, h, 0.94);
        var y = _y + 18;
        foreach (var l in lines) { _page.Text(Left + 12, y, l, 10, true); y += 13.5; }
        _y += h + 10;
    }

    /// <summary>Lines to sign on, side by side.</summary>
    public void Signatures(params string[] labels)
    {
        _y += 34;
        Need(50);
        const double gap = 36;
        var w = (Right - Left - gap * (labels.Length - 1)) / labels.Length;
        for (var i = 0; i < labels.Length; i++)
        {
            var x = Left + i * (w + gap);
            _page.Line(x, _y + 26, x + w, _y + 26, 0.7, 0.3);
            _page.Text(x, _y + 38, labels[i], 8.5, false, 0.4);
        }
        _y += 50;
    }

    /// <summary>A table; <paramref name="share"/> = each column's part of the width. The head is repeated on a new page.</summary>
    public void Table(string[] head, double[] share, IReadOnlyList<string[]> rows)
    {
        const double size = 9, line = 12;
        var widths = share.Select(s => s * (Right - Left)).ToArray();
        void Head()
        {
            Need(20 + line);
            _page.Box(Left, _y, Right - Left, 18, 0.93);
            var x = Left;
            for (var c = 0; c < head.Length; c++) { _page.Text(x + 4, _y + 12.5, head[c], 8.5, true); x += widths[c]; }
            _y += 22;
        }
        Head();
        foreach (var row in rows)
        {
            var cells = row.Select((t, c) => PdfWriter.Wrap(t ?? "", widths[c] - 8, size, false)).ToList();
            var h = cells.Max(c => c.Count) * line + 6;
            if (_y + h > Bottom) { NewPage(); Head(); }
            var x = Left;
            for (var c = 0; c < cells.Count; c++)
            {
                var y = _y + 9;
                foreach (var l in cells[c]) { _page.Text(x + 4, y, l, size); y += line; }
                x += widths[c];
            }
            _y += h;
            _page.Line(Left, _y - 2, Right, _y - 2, 0.4, 0.85);
        }
        _y += 8;
    }

    public byte[] ToBytes()
    {
        var stand = "Stand: " + B.Date(B.Today);
        for (var i = 0; i < _pdf.Pages.Count; i++)
        {
            var p = _pdf.Pages[i];
            p.Line(Left, 792, Right, 792, 0.4, 0.8);
            var y = 804.0;
            foreach (var f in _lh.Foot.SelectMany(f => PdfWriter.Wrap(f, Right - Left - 90, 7.5, false)).Take(3)) { p.Text(Left, y, f, 7.5, false, 0.4); y += 10; }
            p.TextRight(Right, 804, "Seite " + (i + 1).ToString(CultureInfo.InvariantCulture) + " / " + _pdf.Pages.Count.ToString(CultureInfo.InvariantCulture), 7.5, false, 0.4);
            p.TextRight(Right, 814, stand, 7.5, false, 0.4);
        }
        return _pdf.ToBytes();
    }
}
