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
    private static readonly (string Key, string Label)[] TabList = { ("instagram", "Instagram-Bild"), ("pdf", "PDF"), ("csv", "Tabelle (CSV)"), ("kalender", "Kalender-Abo") };

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
            Ui.PageHead(null, "Termine exportieren",
                Ui.Button("Zurück zu den Terminen", VereinAdmin.Root + "events", UiButtonKind.Ghost, "ti-arrow-left", small: true)),
            Ui.Tabs(TabList.Select(t => Ui.Tab(t.Label, Url(t.Key), t.Key == tab)).ToArray()),
            body), "Termine exportieren");
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
        var (defFrom, defTo, _) = VereinInstagram.DefaultSeason(ctx.Settings.Get(VereinInstagram.SeasonKind));
        var from = V.CleanDate(P("von")) is { Length: 10 } f ? f : defFrom;
        var to = V.CleanDate(P("bis")) is { Length: 10 } t ? t : defTo;
        if (string.CompareOrdinal(to, from) < 0) (from, to) = (to, from);
        return new Period(from, to, V.Clip(P("art").Trim(), 60), V.Checked(P("oeffentlich")));
    }

    private static string Heading(IPluginContext ctx, Period p)
    {
        var title = ctx.Settings.Get(VereinInstagram.SeasonTitle) is { Length: > 0 } st ? st.Trim() : "Terminplan";
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
            Ui.Grid(200, Ui.Input("von", "Von", p.From, UiInputType.Date), Ui.Input("bis", "Bis", p.To, UiInputType.Date),
                Ui.Select("art", "Nur diese Art", p.Kind, new[] { Ui.Option("", "alle") }.Concat(kinds.Select(k => Ui.Option(k, k))).ToArray())),
            Ui.Checkbox("oeffentlich", "Nur öffentliche Termine", p.OnlyPublic),
            Ui.FormActions(Ui.Submit("Auswahl übernehmen", UiButtonKind.Ghost, icon: "ti-refresh")));
        var go = list.Count == 0 ? null : pdf
            ? Ui.Button("PDF öffnen (" + list.Count + " Termine)", VereinAdmin.Root + Path + "/pdf?" + p.Query, icon: "ti-file-type-pdf")
            : Ui.Button("CSV herunterladen (" + list.Count + " Termine)", VereinAdmin.Root + Path + "/csv?" + p.Query, icon: "ti-table-down");
        static string When(Event e) => V.Badge(e.Starts).Weekday + " " + V.Date(e.Day) + (e.Starts.Length >= 16 && e.Starts[11..16] != "00:00" ? ", " + e.Starts[11..16] : "");
        var preview = Ui.Table(new[] { Ui.Column("Wann"), Ui.Column("Termin"), Ui.Column("Art"), Ui.Column("Ort"), Ui.Column("Sichtbar") },
            list.Select(e => Ui.Row(When(e), e.Title, e.Kind, e.Place, e.Public ? Ui.Badge("öffentlich", UiTone.Success) : Ui.Badge("intern", UiTone.Info))),
            emptyText: "In diesem Zeitraum gibt es keine passenden Termine.");
        // the choice on top, what comes out below it in full width (a table of dates is too wide for a side column)
        return Ui.Stack(
            Ui.Card("Auswahl", form),
            Ui.Card(pdf ? "Terminplan als PDF" : "Termine als Tabelle", Ui.Stack(
                Ui.Muted(pdf ? "Auf dem Briefkopf des Vereins, ohne Entwürfe und abgesagte Termine."
                             : "Eine Zeile je Termin, für Excel, Numbers oder LibreOffice."),
                go, preview)));
    }

    // ---- the files ------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> PdfAsync(IPluginContext ctx, CancellationToken ct)
    {
        var p = ReadPeriod(ctx);
        var list = (await new VereinStore(ctx.Data).EventsAsync(ct)).Where(p.Fits).OrderBy(e => e.Starts).ToList();
        var heading = Heading(ctx, p);
        return VereinPrint.Result(VereinPrint.Plan(await VereinPrint.LetterheadAsync(ctx, ct), heading, V.Date(p.From) + " – " + V.Date(p.To) + (p.Kind.Length > 0 ? " · " + p.Kind : ""), list), heading);
    }

    public static async Task<PluginResult> CsvAsync(IPluginContext ctx, CancellationToken ct)
    {
        var p = ReadPeriod(ctx);
        var list = (await new VereinStore(ctx.Data).EventsAsync(ct)).Where(p.Fits).OrderBy(e => e.Starts).ToList();
        return PluginResult.File(Encoding.UTF8.GetBytes(Csv(list)), "text/csv; charset=utf-8", "termine-" + p.From + "-bis-" + p.To + ".csv");
    }

    /// <summary>One row per date. A cell that starts like a formula gets a quote (a spreadsheet would run it).</summary>
    internal static string Csv(IEnumerable<Event> list)
    {
        static string C(string s) { s ??= ""; if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
        static string Time(string s) => s.Length >= 16 ? s[11..16] : "";
        var sb = new StringBuilder("﻿" + string.Join(";", new[] { "Datum", "Wochentag", "Beginn", "Ende", "Termin", "Art", "Ort", "Treffpunkt", "Sichtbar", "Beschreibung" }.Select(C)) + "\r\n");
        foreach (var e in list)
            sb.Append(string.Join(";", new[]
            {
                V.Date(e.Day), V.Badge(e.Starts).Weekday, Time(e.Starts), e.Ends.Length >= 10 && e.Ends[..10] != e.Day ? V.Date(e.Ends[..10]) + " " + Time(e.Ends) : Time(e.Ends),
                e.Title, e.Kind, e.Place, e.Meeting, e.Public ? "öffentlich" : "intern", e.Description
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
                Ui.Card("Öffentliche Termine abonnieren", Ui.Stack(
                    Ui.Muted("Nur die öffentlichen Termine; Änderungen kommen von selbst nach."),
                    Ui.KeyValue(("Adresse", https)),
                    // webcal:// is no target the toolkit's buttons accept (local and http(s) only) — but it IS the one-click subscription, so it is drawn here, encoded
                    Ui.Html("<p><a class=\"btn\" href=\"" + V.H(webcal) + "\"><i class=\"ti ti-calendar-plus\" aria-hidden=\"true\"></i> Im Kalender abonnieren</a> "
                        + "<a class=\"btn btn-ghost\" href=\"" + V.H(VereinIcal.PublicFeed) + "\"><i class=\"ti ti-download\" aria-hidden=\"true\"></i> Als .ics herunterladen</a></p>")))
            },
            new UiNode?[]
            {
                Ui.Card("Eigener Kalender der Mitglieder", Ui.Muted("Jedes Mitglied hat im Profil eine eigene Adresse – mit internen Terminen und seinen Sitzungen."))
            });
    }
}
