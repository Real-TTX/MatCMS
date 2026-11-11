using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Verein;

/// <summary>
/// Termine → Exportieren: one page with a tab per way out — the picture for Instagram, a printable date plan (PDF), a spreadsheet (CSV) and the
/// calendar subscription. It replaced the Instagram picture's own menu entry (1.15): every export of the dates lives where the dates are.
/// </summary>
public static class VereinExport
{
    public const string Path = "events/export";
    public static string Url(string tab) => VereinAdmin.Root + Path + "?tab=" + tab;
    private static readonly (string Key, string Label)[] TabList = { ("instagram", "verein.export.tab.instagram"), ("pdf", "verein.export.tab.pdf"), ("csv", "verein.export.tab.csv"), ("kalender", "verein.export.tab.calendar") };

    public static async Task<PluginResult> PageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var tab = TabList.Any(t => t.Key == ctx.Request.Q("tab")) ? ctx.Request.Q("tab") : "instagram";
        var body = tab switch
        {
            "instagram" => await VereinInstagram.BodyAsync(ctx, ct),
            "kalender" => CalendarBody(ctx),
            _ => await PeriodBodyAsync(ctx, tab, ct)
        };
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("verein.export.title"),
                Ui.Button(ctx.T("verein.export.back"), VereinAdmin.Root + "events", UiButtonKind.Ghost, "ti-arrow-left", small: true)),
            Ui.Tabs(TabList.Select(t => Ui.Tab(ctx.T(t.Label), Url(t.Key), t.Key == tab)).ToArray()),
            body), ctx.T("verein.export.title"));
    }

    /// <summary>The old address of the Instagram page (bookmarks, links in older versions) lands on its tab with the same choices.</summary>
    public static PluginResult FromInstagram(IPluginContext ctx)
    {
        var sb = new StringBuilder(Url("instagram"));
        foreach (var k in new[] { "vorlage", "event", "stil", "format", "von", "bis", "art", "oeffentlich" })
            if (ctx.Request.Q(k) is { Length: > 0 } v) sb.Append('&').Append(k).Append('=').Append(Uri.EscapeDataString(v));
        return PluginResult.Redirect(sb.ToString());
    }

    // ---- the period: what the PDF and the spreadsheet contain ------------------------------------------------------

    internal sealed record Period(string From, string To, string Kind, bool OnlyPublic)
    {
        public string Query => "von=" + From + "&bis=" + To + (Kind.Length > 0 ? "&art=" + Uri.EscapeDataString(Kind) : "") + (OnlyPublic ? "&oeffentlich=1" : "");
        public bool Fits(Event e) => !e.Draft && !e.Cancelled && (!OnlyPublic || e.Public) && (Kind.Length == 0 || string.Equals(e.Kind, Kind, StringComparison.OrdinalIgnoreCase))
            && string.CompareOrdinal(e.Day, From) >= 0 && string.CompareOrdinal(e.Day, To) <= 0;
    }

    /// <summary>The period as asked: posted by the form or in the address; nothing given = the season of the Instagram settings. Internal dates are in unless "only public" is ticked.</summary>
    internal static Period ReadPeriod(IPluginContext ctx)
    {
        string P(string k) => ctx.Request.IsPost ? ctx.Request.F(k) : ctx.Request.Q(k);
        var (defFrom, defTo, _) = VereinInstagram.DefaultSeason(ctx, ctx.Settings.Get(VereinInstagram.SeasonKind));
        var from = V.CleanDate(P("von")) is { Length: 10 } f ? f : defFrom;
        var to = V.CleanDate(P("bis")) is { Length: 10 } t ? t : defTo;
        if (string.CompareOrdinal(to, from) < 0) (from, to) = (to, from);
        return new Period(from, to, V.Clip(P("art").Trim(), 60), V.Checked(P("oeffentlich")));
    }

    private static string Heading(IPluginContext ctx, Period p)
    {
        // the setting is the club's own word; only its untouched default (and an empty value) follows the language
        var title = VereinSettings.Shown(ctx, VereinInstagram.SeasonTitle, VereinInstagram.DefaultSeasonTitle, "verein.insta.planDefault") is { Length: > 0 } st ? st.Trim() : ctx.T("verein.insta.planDefault");
        return title + " " + (p.From[..4] == p.To[..4] ? p.From[..4] : p.From[..4] + "/" + p.To[2..4]);
    }


    private static async Task<UiNode> PeriodBodyAsync(IPluginContext ctx, string tab, CancellationToken ct)
    {
        var p = ReadPeriod(ctx);
        var all = await new VereinStore(ctx.Data).EventsAsync(ct);
        var list = all.Where(p.Fits).OrderBy(e => e.Starts).ToList();
        var kinds = all.Select(e => e.Kind).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k).ToList();
        var pdf = tab == "pdf";
        var form = Ui.Form(
            Ui.Grid(200, Ui.Input("von", ctx.T("verein.export.from"), p.From, UiInputType.Date), Ui.Input("bis", ctx.T("verein.export.to"), p.To, UiInputType.Date),
                Ui.Select("art", ctx.T("verein.export.onlyKind"), p.Kind, new[] { Ui.Option("", ctx.T("verein.export.all")) }.Concat(kinds.Select(k => Ui.Option(k, k))).ToArray())),
            Ui.Checkbox("oeffentlich", ctx.T("verein.export.onlyPublic"), p.OnlyPublic),
            Ui.FormActions(Ui.Submit(ctx.T("verein.export.apply"), UiButtonKind.Ghost, icon: "ti-refresh")));
        var go = list.Count == 0 ? null : pdf
            ? Ui.Button(ctx.T("verein.export.openPdf", list.Count), VereinAdmin.Root + Path + "/pdf?" + p.Query, icon: "ti-file-type-pdf")
            : Ui.Button(ctx.T("verein.export.downloadCsv", list.Count), VereinAdmin.Root + Path + "/csv?" + p.Query, icon: "ti-table-down");
        string When(Event e) => V.Badge(ctx, e.Starts).Weekday + " " + V.Date(ctx, e.Day) + (e.Starts.Length >= 16 && e.Starts[11..16] != "00:00" ? ", " + e.Starts[11..16] : "");
        var preview = Ui.Table(new[] { Ui.Column(ctx.T("verein.export.col.when")), Ui.Column(ctx.T("verein.export.col.event")), Ui.Column(ctx.T("verein.export.col.kind")), Ui.Column(ctx.T("verein.export.col.place")), Ui.Column(ctx.T("verein.export.col.visible")) },
            list.Select(e => Ui.Row(When(e), e.Title, e.Kind, e.Place, e.Public ? Ui.Badge(ctx.T("verein.export.public"), UiTone.Success) : Ui.Badge(ctx.T("verein.export.internal"), UiTone.Info))),
            emptyText: ctx.T("verein.export.none"));
        // the choice on top, what comes out below it in full width (a table of dates is too wide for a side column)
        return Ui.Stack(
            Ui.Card(ctx.T("verein.export.choice"), form),
            Ui.Card(ctx.T(pdf ? "verein.export.pdfTitle" : "verein.export.csvTitle"), Ui.Stack(
                Ui.Muted(ctx.T(pdf ? "verein.export.pdfHint" : "verein.export.csvHint")),
                go, preview)));
    }

    // ---- the files ------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> PdfAsync(IPluginContext ctx, CancellationToken ct)
    {
        var p = ReadPeriod(ctx);
        var list = (await new VereinStore(ctx.Data).EventsAsync(ct)).Where(p.Fits).OrderBy(e => e.Starts).ToList();
        var heading = Heading(ctx, p);
        return VereinPrint.Result(VereinPrint.Plan(await VereinPrint.LetterheadAsync(ctx, ct), heading, V.Date(ctx, p.From) + " – " + V.Date(ctx, p.To) + (p.Kind.Length > 0 ? " · " + p.Kind : ""), list), heading);
    }

    public static async Task<PluginResult> CsvAsync(IPluginContext ctx, CancellationToken ct)
    {
        var p = ReadPeriod(ctx);
        var list = (await new VereinStore(ctx.Data).EventsAsync(ct)).Where(p.Fits).OrderBy(e => e.Starts).ToList();
        return PluginResult.File(Encoding.UTF8.GetBytes(Csv(ctx, list)), "text/csv; charset=utf-8", "termine-" + p.From + "-bis-" + p.To + ".csv");
    }

    /// <summary>One row per date, in the request's language (nothing reads this file back). A cell that starts like a formula gets a quote (a spreadsheet would run it).</summary>
    internal static string Csv(IPluginContext ctx, IEnumerable<Event> list)
    {
        static string C(string s) { s ??= ""; if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
        static string Time(string s) => s.Length >= 16 ? s[11..16] : "";
        var head = new[] { "date", "weekday", "start", "end", "event", "kind", "place", "meetingPoint", "visible", "description" }.Select(k => ctx.T("verein.export.csv." + k));
        var sb = new StringBuilder("﻿" + string.Join(";", head.Select(C)) + "\r\n");
        foreach (var e in list)
            sb.Append(string.Join(";", new[]
            {
                V.Date(ctx, e.Day), V.Badge(ctx, e.Starts).Weekday, Time(e.Starts), e.Ends.Length >= 10 && e.Ends[..10] != e.Day ? V.Date(ctx, e.Ends[..10]) + " " + Time(e.Ends) : Time(e.Ends),
                e.Title, e.Kind, e.Place, e.Meeting, ctx.T(e.Public ? "verein.export.public" : "verein.export.internal"), e.Description
            }.Select(C))).Append("\r\n");
        return sb.ToString();
    }

    // ---- the calendar ------------------------------------------------------------------------------------------------

    private static UiNode CalendarBody(IPluginContext ctx)
    {
        var origin = VereinMail.Origin(ctx);
        var https = origin + VereinIcal.PublicFeed;
        var webcal = (origin.StartsWith("https://", StringComparison.Ordinal) ? "webcal://" + origin[8..] : origin.Replace("http://", "webcal://")) + VereinIcal.PublicFeed;
        return Ui.Columns(
            new UiNode?[]
            {
                Ui.Card(ctx.T("verein.export.cal.title"), Ui.Stack(
                    Ui.Muted(ctx.T("verein.export.cal.hint")),
                    Ui.KeyValue((ctx.T("verein.export.cal.address"), https)),
                    // webcal:// is no target the toolkit's buttons accept (local and http(s) only) — but it IS the one-click subscription, so it is drawn here, encoded
                    Ui.Html("<p><a class=\"btn\" href=\"" + V.H(webcal) + "\"><i class=\"ti ti-calendar-plus\" aria-hidden=\"true\"></i> " + V.H(ctx.T("verein.export.cal.subscribe")) + "</a> "
                        + "<a class=\"btn btn-ghost\" href=\"" + V.H(VereinIcal.PublicFeed) + "\"><i class=\"ti ti-download\" aria-hidden=\"true\"></i> " + V.H(ctx.T("verein.export.cal.download")) + "</a></p>")))
            },
            new UiNode?[]
            {
                Ui.Card(ctx.T("verein.export.cal.ownTitle"), Ui.Muted(ctx.T("verein.export.cal.ownHint")))
            });
    }
}
