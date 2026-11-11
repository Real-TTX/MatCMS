using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Verein;

/// <summary>
/// Documents, invitations and minutes as A4 PDFs on the club's letterhead (Admin → Verein and the member area). The letterhead is a few settings — logo,
/// the lines top right, the footer, a colour; what is left empty comes from the site itself (its name, address and logo), so a club that never set it
/// up still gets a sensible paper. The PDF is written by <see cref="PdfWriter"/>, a copy of the shop's: a native plugin may not use a host type.
/// </summary>
public static class VereinPrint
{
    public const string Logo = "printLogo", Head = "printHead", Foot = "printFoot", Color = "printColor";

    public static void Declare(ISettingsBuilder s) => s
        .Add(new SettingSpec(Logo, "verein.print.set.logo", SettingKind.Image) { Group = "verein.print.set.group", Help = "verein.print.set.logoHelp" })
        .Add(new SettingSpec(Head, "verein.print.set.head", SettingKind.List) { AddLabel = "verein.print.set.line", Help = "verein.print.set.headHelp" })
        .Add(new SettingSpec(Foot, "verein.print.set.foot", SettingKind.List) { AddLabel = "verein.print.set.line", Help = "verein.print.set.footHelp" })
        .Add(new SettingSpec(Color, "verein.print.set.color", SettingKind.Color) { Default = "#334155", Help = "verein.print.set.colorHelp" });

    /// <summary>What every page carries. <c>Ctx</c> is the request the paper is made for: its words and dates are in that request's language.</summary>
    internal sealed record Letterhead(string Club, IReadOnlyList<string> Head, IReadOnlyList<string> Foot, (double R, double G, double B) Accent, PluginImage? Logo, IPluginContext Ctx)
    {
        public string T(string key, params object[] args) => Ctx.T(key, args);
    }

    internal static async Task<Letterhead> LetterheadAsync(IPluginContext ctx, CancellationToken ct)
    {
        var club = VereinSettings.Club(ctx);
        var host = HostOf(ctx.Site.Origin);
        var head = Lines(ctx.Settings.Get(Head), 7);
        if (head.Count == 0) head = new[] { club, host }.Where(x => x.Length > 0).ToList();
        var foot = Lines(ctx.Settings.Get(Foot), 3);
        if (foot.Count == 0) foot = new List<string> { club + (host.Length > 0 ? " · " + host : "") };
        var color = ctx.Settings.Get(Color) is { Length: 7 } c && c[0] == '#' && c[1..].All(Uri.IsHexDigit) ? c : "#334155";
        PluginImage? logo = null;
        foreach (var url in new[] { ctx.Settings.Get(Logo), ctx.Settings.Get(VereinInstagram.Logo), SiteLogo(ctx.Site) })
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            try { logo = await ctx.Site.LoadImageAsync(url.Trim(), 600, ct); } catch { logo = null; }
            if (logo is not null) break;                                       // an SVG or a missing file gives null: try the next
        }
        return new Letterhead(club, head, foot, Rgb(color), logo, ctx);
    }

    /// <summary>The site's logo — a host older than this API has no such member, which shows only when the method is compiled, hence the extra frame.</summary>
    private static string SiteLogo(IPluginSite site)
    {
        try { return LogoOf(site); } catch (MissingMethodException) { return ""; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string LogoOf(IPluginSite site) => site.LogoUrl ?? "";

    private static string HostOf(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var u) ? u.Host : "";

    private static List<string> Lines(string s, int max) =>
        (s ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(max).ToList();

    private static (double, double, double) Rgb(string hex) =>
        (Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0, Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0, Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0);

    /// <summary>The answer: shown in the browser (to print or save), with a file name for saving.</summary>
    internal static PluginResult Result(byte[] pdf, string name) =>
        PluginResult.File(pdf, "application/pdf")
            .WithHeader("Content-Disposition", "inline; filename=\"" + FileName(name) + ".pdf\"")
            .WithHeader("X-Content-Type-Options", "nosniff").WithHeader("Cache-Control", "private, no-store");

    internal static string FileName(string title)
    {
        var map = new Dictionary<char, string> { ['ä'] = "ae", ['ö'] = "oe", ['ü'] = "ue", ['Ä'] = "Ae", ['Ö'] = "Oe", ['Ü'] = "Ue", ['ß'] = "ss" };
        var sb = new StringBuilder();
        foreach (var ch in title.Trim())
            if (map.TryGetValue(ch, out var r)) sb.Append(r);
            else if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "dokument" : s.Length > 80 ? s[..80] : s;
    }

    // ---- the papers -----------------------------------------------------------------------------------------------

    private static string DocKind(Letterhead lh, string k) => lh.T(k switch { VereinStore.Sign => "verein.print.kind.sign", VereinStore.Read => "verein.print.kind.read", _ => "verein.print.kind.confirm" });

    private static string Reply(Letterhead lh, string a) => lh.T(a switch { VereinStore.Yes => "verein.print.reply.yes", VereinStore.No => "verein.print.reply.no", _ => "verein.print.reply.maybe" });

    /// <summary>A document. <paramref name="mine"/> = the reader's own answer (member area); <paramref name="proof"/> = everybody's answers (the board's proof).</summary>
    internal static byte[] Document(Letterhead lh, Doc d, Answer? mine = null, IReadOnlyList<(string Name, Answer? Answer)>? proof = null)
    {
        var ctx = lh.Ctx;
        var p = new PrintLayout(lh, d.Title);
        p.Kicker(DocKind(lh, d.Kind) + (d.Due.Length > 0 ? lh.T("verein.print.dueBy", V.Date(ctx, d.Due)) : ""));
        p.Title(d.Title);
        p.Facts((lh.T("verein.print.date"), V.Day(ctx, d.Created)), (lh.T("verein.print.attachment"), d.FileName.Length > 0 ? lh.T("verein.print.inMemberArea", d.FileName) : ""));
        p.Body(d.Body);
        if (mine is not null)
            p.Box(mine.Status == VereinStore.Accepted
                ? lh.T(d.Kind == VereinStore.Sign ? "verein.print.mine.signed" : d.Kind == VereinStore.Read ? "verein.print.mine.read" : "verein.print.mine.confirmed", V.Stamp(ctx, mine.At), mine.Signature)
                : lh.T("verein.print.mine.declined", V.Stamp(ctx, mine.At)));
        else if (proof is null && d.Kind == VereinStore.Sign)
            p.Signatures(lh.T("verein.print.placeDate"), lh.T("verein.print.signature"));
        if (proof is not null)
        {
            var done = proof.Count(x => x.Answer is not null);
            p.Heading(lh.T("verein.print.proof", done, proof.Count));
            p.Table(new[] { lh.T("verein.print.col.name"), lh.T("verein.print.col.answer"), lh.T("verein.print.signature"), lh.T("verein.print.col.when") }, new[] { 0.32, 0.18, 0.28, 0.22 },
                proof.Select(x => new[]
                {
                    x.Name,
                    lh.T(x.Answer is null ? "verein.print.open" : x.Answer.Status == VereinStore.Accepted ? (d.Kind == VereinStore.Read ? "verein.print.ans.read" : d.Kind == VereinStore.Sign ? "verein.print.ans.signed" : "verein.print.ans.confirmed") : "verein.print.ans.declined"),
                    x.Answer?.Signature ?? "", V.Stamp(ctx, x.Answer?.At)
                }).ToList());
        }
        return p.ToBytes();
    }

    /// <summary>Who takes part in an event: counts, then everybody with their answer and a box to tick on the day.</summary>
    internal static byte[] Participants(Letterhead lh, Event e, IReadOnlyList<(Person P, EventReply? R)> list)
    {
        var p = new PrintLayout(lh, lh.T("verein.print.participantsTitle", e.Title));
        p.Kicker(lh.T("verein.print.participants") + (e.Kind.Length > 0 ? " · " + e.Kind : ""));
        p.Title(e.Title);
        int N(string a) => list.Count(x => x.R?.Answer == a);
        p.Facts((lh.T("verein.print.col.when"), V.When(lh.Ctx, e.Starts)), (lh.T("verein.print.where"), e.Place), (lh.T("verein.print.meetingPoint"), e.Meeting),
            (lh.T("verein.print.state"), lh.T("verein.print.counts", N(VereinStore.Yes), N(VereinStore.Maybe), N(VereinStore.No), list.Count(x => x.R is null))));
        p.Table(new[] { lh.T("verein.print.col.name"), lh.T("verein.print.col.answer"), lh.T("verein.print.col.note"), lh.T("verein.print.col.there") }, new[] { 0.36, 0.2, 0.36, 0.08 },
            list.Select(x => new[] { x.P.Name + (x.P.Child ? lh.T("verein.print.child") : ""), x.R is null ? lh.T("verein.print.open") : Reply(lh, x.R.Answer), x.R?.Note ?? "", "[   ]" }).ToList());
        return p.ToBytes();
    }

    /// <summary>The date plan (Termine → Exportieren → PDF): one table row per date — day, time, what, where — to hang up or hand out.</summary>
    internal static byte[] Plan(Letterhead lh, string heading, string period, IReadOnlyList<Event> list)
    {
        var p = new PrintLayout(lh, heading);
        var ctx = lh.Ctx;
        p.Kicker(lh.T("verein.print.events") + " · " + period);
        p.Title(heading);
        static string Time(Event e) => e.Starts.Length >= 16 && e.Starts[11..16] != "00:00" ? e.Starts[11..16] + (e.Ends.Length >= 16 && e.Ends[..10] == e.Day ? "–" + e.Ends[11..16] : "") : "";
        if (list.Count == 0) p.Body(lh.T("verein.print.noEvents"));
        else
            p.Table(new[] { lh.T("verein.print.date"), lh.T("verein.print.col.time"), lh.T("verein.print.col.event"), lh.T("verein.print.col.place") }, new[] { 0.18, 0.1, 0.41, 0.31 },
                list.Select(e => new[]
                {
                    V.Badge(ctx, e.Starts).Weekday + " " + V.Date(ctx, e.Day) + (e.Ends.Length >= 10 && e.Ends[..10] != e.Day ? " – " + V.Date(ctx, e.Ends[..10]) : ""),
                    Time(e), e.Title + (e.Kind.Length > 0 ? " (" + e.Kind + ")" : "") + (e.Public ? "" : lh.T("verein.print.internal")), e.Place
                }).ToList());
        return p.ToBytes();
    }

    /// <summary>A meeting: the invitation with its agenda, or (<paramref name="minutes"/>) the minutes with places to sign.</summary>
    internal static byte[] Meeting(Letterhead lh, Meeting m, bool minutes)
    {
        var p = new PrintLayout(lh, lh.T(minutes ? "verein.print.minutesTitle" : "verein.print.invitationTitle", m.Title));
        p.Kicker(minutes ? lh.T("verein.print.minutes") + (m.MinutesStatus == VereinStore.Final ? "" : lh.T("verein.print.draft")) : lh.T("verein.print.invitation") + (m.Kind.Length > 0 ? " · " + m.Kind : ""));
        p.Title(m.Title);
        p.Facts((lh.T("verein.print.col.when"), V.When(lh.Ctx, m.Starts)), (lh.T("verein.print.where"), m.Place), (lh.T("verein.print.kind"), minutes ? m.Kind : ""),
            (lh.T("verein.print.status"), m.Status == VereinStore.Cancelled ? lh.T("verein.print.cancelled") : ""));
        if (!minutes)
        {
            p.Heading(lh.T("verein.print.agenda"));
            p.Body(m.Agenda.Trim().Length > 0 ? m.Agenda : lh.T("verein.print.agendaFollows"));
        }
        else
        {
            if (m.Agenda.Trim().Length > 0) { p.Heading(lh.T("verein.print.agenda")); p.Body(m.Agenda); }
            p.Heading(lh.T("verein.print.minutes"));
            p.Body(m.Minutes.Trim().Length > 0 ? m.Minutes : lh.T("verein.print.noMinutes"));
            p.Signatures(lh.T("verein.print.chair"), lh.T("verein.print.recorder"));
        }
        return p.ToBytes();
    }
}

/// <summary>Flows text over A4 pages: the full letterhead on the first, a slim head on the others, the footer and page numbers on all.</summary>
internal sealed class PrintLayout
{
    private const double Left = 56, Right = 539, Bottom = 772;
    private readonly PdfWriter _pdf;
    private readonly VereinPrint.Letterhead _lh;
    private readonly string _title;
    private PdfPage _page;
    private double _y;

    public PrintLayout(VereinPrint.Letterhead lh, string title)
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
        var stand = _lh.T("verein.print.asOf", V.Date(_lh.Ctx, V.Today));
        for (var i = 0; i < _pdf.Pages.Count; i++)
        {
            var p = _pdf.Pages[i];
            p.Line(Left, 792, Right, 792, 0.4, 0.8);
            var y = 804.0;
            foreach (var f in _lh.Foot.SelectMany(f => PdfWriter.Wrap(f, Right - Left - 90, 7.5, false)).Take(3)) { p.Text(Left, y, f, 7.5, false, 0.4); y += 10; }
            p.TextRight(Right, 804, _lh.T("verein.print.page", (i + 1).ToString(CultureInfo.InvariantCulture), _pdf.Pages.Count.ToString(CultureInfo.InvariantCulture)), 7.5, false, 0.4);
            p.TextRight(Right, 814, stand, 7.5, false, 0.4);
        }
        return _pdf.ToBytes();
    }
}
