using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Verein;

/// <summary>
/// Events as a picture for Instagram (Admin → Verein → Instagram-Bild). The server draws an SVG in the chosen template and format; the browser turns it into
/// a PNG (no image library in the plugin). Pictures inside the SVG are embedded as data (an SVG drawn as an image may not load anything from outside), so
/// background photo and logo come through <see cref="IPluginSite.LoadImageAsync"/> as JPEG. Fonts are the system's: a page font is not available there either.
/// </summary>
public static class VereinInstagram
{
    public const string Color = "instaColor", Logo = "instaLogo", Hashtags = "instaHashtags", SeasonKind = "instaSeason", SeasonTitle = "instaSeasonTitle";

    public static void Declare(ISettingsBuilder s) => s
        .Add(new SettingSpec(Color, "Vereinsfarbe", SettingKind.Color) { Group = "Instagram-Bild", Default = "#8a1f33", Help = "Hintergrund und Akzente der Bilder." })
        .Add(new SettingSpec(Logo, "Logo", SettingKind.Image) { Help = "Erscheint in einem weißen Kreis auf dem Bild (ein Bild aus der Mediathek; transparente Flächen werden weiß)." })
        .Choice(SeasonKind, "Zeitraum der Saison", "monate", new[] { new FieldOption("monate", "die nächsten drei Monate"), new FieldOption("jahr", "das laufende Jahr"), new FieldOption("fasnet", "die nächste Fasnet (6. Januar bis Aschermittwoch)") },
            "Voreinstellung der Vorlage „Saison“ – beim Erstellen lässt sich der Zeitraum ändern.")
        .Text(SeasonTitle, "Überschrift der Saison", "Terminplan", "z. B. „Saisonplan“, „Spielplan“ oder „Narrenfahrplan“ – die Jahreszahl kommt dazu.")
        .Text(Hashtags, "Zeile unten", "", "z. B. #fasnet #narren #besigheim – leer = Name des Vereins.");

    private static readonly (string Key, string Label, int W, int H)[] Formats = { ("square", "Quadrat 1080 × 1080", 1080, 1080), ("portrait", "Hochformat 1080 × 1350", 1080, 1350), ("story", "Story 1080 × 1920", 1080, 1920) };

    public static async Task<PluginResult> PageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        string P(string k) => ctx.Request.IsPost ? ctx.Request.F(k) : ctx.Request.Q(k);
        var template = P("vorlage") is "plakat" or "saison" ? P("vorlage") : "liste";
        var format = Formats.FirstOrDefault(f => f.Key == P("format")) is { Key: not null } ff ? ff : Formats[0];
        var everything = (await store.EventsAsync(ct)).Where(e => !e.Cancelled && !e.Draft).ToList();
        var all = everything.Where(e => string.CompareOrdinal(e.Ends.Length >= 10 ? e.Ends[..10] : e.Day, V.Today) >= 0).ToList();
        long.TryParse(P("event"), out var eventId);
        var kind = P("art").Trim();
        var count = int.TryParse(P("anzahl"), out var n) ? Math.Clamp(n, 1, 8) : 5;
        var (defFrom, defTo, season) = DefaultSeason(ctx.Settings.Get(SeasonKind));
        var from = V.CleanDate(P("von")) is { Length: 10 } f1 ? f1 : defFrom;
        var to = V.CleanDate(P("bis")) is { Length: 10 } t1 ? t1 : defTo;
        if (string.CompareOrdinal(to, from) < 0) (from, to) = (to, from);
        var heading = P("titel") is { Length: > 0 } t ? V.Clip(t, 40)
            : template switch { "plakat" => "", "saison" => (ctx.Settings.Get(SeasonTitle) is { Length: > 0 } st ? st.Trim() : "Terminplan") + " " + (from[..4] == to[..4] ? to[..4] : from[..4] + "/" + to[2..4]), _ => "Nächste Termine" };
        var photo = P("bild");
        var onlyPublic = ctx.Request.IsPost ? V.Checked(ctx.Request.F("oeffentlich")) : true;
        bool Fits(Event e) => (!onlyPublic || e.Public) && (kind.Length == 0 || string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase));

        var events = all.Where(Fits).ToList();
        // a season counts every date in its period, past ones too (the plan is made before and shared during it)
        var seasonEvents = everything.Where(e => Fits(e) && string.CompareOrdinal(e.Day, from) >= 0 && string.CompareOrdinal(e.Day, to) <= 0).ToList();
        var single = template == "plakat" ? (all.FirstOrDefault(e => e.Id == eventId) ?? all.FirstOrDefault(e => e.Public) ?? all.FirstOrDefault()) : null;

        string? photoData = null, logoData = null;
        if (photo.StartsWith("/uploads/", StringComparison.Ordinal) && await ctx.Site.LoadImageAsync(photo, 1400, ct) is PluginImage pi) photoData = DataUrl(pi);
        if (ctx.Settings.Get(Logo) is { Length: > 0 } logo && await ctx.Site.LoadImageAsync(logo, 400, ct) is PluginImage li) logoData = DataUrl(li);
        var color = SafeColor(ctx.Settings.Get(Color));
        var footer = ctx.Settings.Get(Hashtags) is { Length: > 0 } h ? h : VereinSettings.Club(ctx);

        var club = VereinSettings.Club(ctx);
        // one picture, or – a season with more dates than fit – several, numbered
        var svgs = new List<string>();
        if (template == "plakat") { if (single is not null) svgs.Add(Poster(single, format.W, format.H, color, photoData, logoData, club, footer, heading)); }
        else if (template == "saison")
        {
            var per = SeasonCapacity(format.H);
            var pages = (seasonEvents.Count + per - 1) / per;
            for (var i = 0; i < pages; i++)
                svgs.Add(Season(seasonEvents.Skip(i * per).Take(per).ToList(), i + 1, pages, from, to, format.W, format.H, color, photoData, logoData, club, footer, heading));
        }
        else if (events.Count > 0) svgs.Add(List(events.Take(count).ToList(), format.W, format.H, color, photoData, logoData, club, footer, heading));

        var kinds = everything.Select(e => e.Kind).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var kindSelect = Ui.Select("art", "Nur diese Art", kind, new[] { Ui.Option("", "alle") }.Concat(kinds.Select(k => Ui.Option(k, k))).ToArray());
        var form = Ui.Form(
            Ui.Grid(200,
                Ui.Select("vorlage", "Vorlage", template, Ui.Option("liste", "Terminliste (die nächsten)"), Ui.Option("saison", "Saison (alle Termine eines Zeitraums)"), Ui.Option("plakat", "Plakat (ein Termin)")),
                Ui.Select("format", "Format", format.Key, Formats.Select(f => Ui.Option(f.Key, f.Label)).ToArray()),
                Ui.Input("titel", "Überschrift", heading)),
            template switch
            {
                "plakat" => Ui.Select("event", "Termin", single?.Id.ToString(CultureInfo.InvariantCulture) ?? "", all.Select(e => Ui.Option(e.Id.ToString(CultureInfo.InvariantCulture), V.Date(e.Starts) + " – " + e.Title)).ToArray()),
                "saison" => Ui.Grid(200, Ui.Input("von", "Von", from, UiInputType.Date), Ui.Input("bis", "Bis", to, UiInputType.Date), kindSelect),
                _ => Ui.Grid(200, kindSelect, Ui.Select("anzahl", "Wie viele", count.ToString(CultureInfo.InvariantCulture), new[] { "3", "4", "5", "6", "8" }.Select(x => Ui.Option(x, x)).ToArray()))
            },
            template == "saison" ? Ui.Muted("Voreingestellt: " + season + " (Einstellungen → Instagram-Bild). Passen nicht alle Termine auf ein Bild, entstehen mehrere.") : null,
            Ui.Image("bild", "Hintergrundfoto (freiwillig)", photo),
            template == "plakat" ? null : Ui.Checkbox("oeffentlich", "Nur öffentliche Termine", onlyPublic),
            Ui.FormActions(Ui.Submit("Vorschau aktualisieren")));

        var name = template switch { "plakat" => "termin", "saison" => "narrenfahrplan", _ => "termine" } + "-" + format.Key;
        UiNode preview;
        if (svgs.Count == 0)
            preview = Ui.Empty(template switch { "plakat" => "Kein kommender Termin zum Zeigen.", "saison" => "Keine Termine in diesem Zeitraum.", _ => "Keine passenden kommenden Termine." });
        else
        {
            var html = new StringBuilder("<div class=\"vm-ig-list\" style=\"display:flex;flex-direction:column;gap:22px\">");
            for (var i = 0; i < svgs.Count; i++)
                html.Append("<div class=\"vm-ig-item\" style=\"max-width:").Append(format.H > format.W ? 360 : 460).Append("px\">")
                    .Append(svgs[i].Replace("<svg ", "<svg style=\"width:100%;height:auto;display:block;border-radius:12px;box-shadow:0 10px 30px rgba(0,0,0,.18)\" "))
                    .Append("<p style=\"margin-top:12px\"><button type=\"button\" class=\"btn vm-ig-dl\" data-name=\"").Append(name).Append(svgs.Count > 1 ? "-" + (i + 1) : "")
                    .Append("\"><i class=\"ti ti-download\"></i> PNG herunterladen").Append(svgs.Count > 1 ? " (" + (i + 1) + "/" + svgs.Count + ")" : "").Append("</button></p></div>");
            html.Append("</div><script>(function(){document.querySelectorAll('.vm-ig-dl').forEach(function(b){b.addEventListener('click',function(){var svg=b.closest('.vm-ig-item').querySelector('svg');")
                .Append("var s=new XMLSerializer().serializeToString(svg).replace(/ style=\"[^\"]*\"/,'');var img=new Image();img.onload=function(){var c=document.createElement('canvas');c.width=").Append(format.W)
                .Append(";c.height=").Append(format.H).Append(";c.getContext('2d').drawImage(img,0,0,").Append(format.W).Append(',').Append(format.H)
                .Append(");c.toBlob(function(bl){var a=document.createElement('a');a.href=URL.createObjectURL(bl);a.download=b.dataset.name+'.png';document.body.appendChild(a);a.click();a.remove();},'image/png');};")
                .Append("img.src='data:image/svg+xml;charset=utf-8,'+encodeURIComponent(s);});});})();</script>");
            preview = Ui.Html(html.ToString());
        }

        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Termine als Bild für Instagram, Facebook oder WhatsApp – Vorlage wählen, Vorschau ansehen, herunterladen", "Instagram-Bild"),
            Ui.Columns(new UiNode?[] { Ui.Card("Einstellungen", form), Ui.Muted("Farbe, Logo und die Zeile unten stehen in den Einstellungen des Vereins.") }, new UiNode?[] { Ui.Card("Vorschau", preview) })), "Instagram-Bild");
    }

    /// <summary>The season preselected by the setting: the next three months (default), this year, or the next Fasnet.</summary>
    public static (string From, string To, string Label) DefaultSeason(string kind)
    {
        var today = V.Local(DateTime.UtcNow).Date;
        string D(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return kind switch
        {
            "fasnet" => NextFasnet(),
            "jahr" => (D(new DateTime(today.Year, 1, 1)), D(new DateTime(today.Year, 12, 31)), "das Jahr " + today.Year.ToString(CultureInfo.InvariantCulture)),
            _ => (D(today), D(today.AddMonths(3)), "die nächsten drei Monate")
        };
    }

    /// <summary>The next Fasnet: 6 January to Ash Wednesday (Easter Sunday − 46 days). After this year's Ash Wednesday it is next year's.</summary>
    public static (string From, string To, string Label) NextFasnet()
    {
        var today = V.Local(DateTime.UtcNow).Date;
        var year = today.Year;
        if (today > Easter(year).AddDays(-46)) year++;
        return (year.ToString(CultureInfo.InvariantCulture) + "-01-06", Easter(year).AddDays(-46).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Fasnet " + year.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Easter Sunday (Gregorian calendar, the anonymous algorithm).</summary>
    public static DateTime Easter(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        return new DateTime(y, (h + l - 7 * m + 114) / 31, (h + l - 7 * m + 114) % 31 + 1);
    }

    private const int SeasonRow = 92;
    private static int SeasonTop(int h) => h >= 1900 ? 250 : h >= 1300 ? 130 : 100;
    private static int SeasonBottom(int h) => h - (h >= 1900 ? 290 : 70);
    /// <summary>How many dates fit on one season picture of this height.</summary>
    public static int SeasonCapacity(int h) => Math.Max(1, (SeasonBottom(h) - 70 - (SeasonTop(h) + 190)) / SeasonRow);

    /// <summary>"Saison / Narrenfahrplan": every date of a period, compact — date pill, title, time and place on one row; several pictures when they do not fit.</summary>
    private static string Season(List<Event> events, int page, int pages, string from, string to, int w, int h, string color, string? photo, string? logo, string club, string footer, string heading)
    {
        var sb = new StringBuilder();
        var top = SeasonTop(h);
        sb.Append(LogoBadge(logo, w - 140, top + 20));
        sb.Append("<text x=\"80\" y=\"").Append(top).Append("\" font-family=\"").Append(Body).Append("\" font-size=\"28\" font-weight=\"700\" letter-spacing=\"5\" fill=\"#fff\" opacity=\".85\">").Append(X(club.ToUpperInvariant())).Append("</text>");
        sb.Append("<text x=\"80\" y=\"").Append(top + 78).Append("\" font-family=\"").Append(Head).Append("\" font-size=\"72\" fill=\"#fff\">").Append(X(heading)).Append("</text>");
        var period = V.When(from, weekday: false) + " – " + V.When(to, weekday: false) + (pages > 1 ? "   ·   Seite " + page + "/" + pages : "");
        sb.Append("<text x=\"80\" y=\"").Append(top + 128).Append("\" font-family=\"").Append(Body).Append("\" font-size=\"30\" font-weight=\"600\" fill=\"#fff\" opacity=\".9\">").Append(X(period)).Append("</text>");
        var y = top + 190;
        foreach (var e in events)
        {
            V.TryParse(e.Starts, out var d, out var hasTime);
            var wd = V.Badge(e.Starts).Weekday;
            sb.Append("<rect x=\"80\" y=\"").Append(y).Append("\" width=\"190\" height=\"").Append(SeasonRow - 18).Append("\" rx=\"16\" fill=\"#fff\"/>");
            sb.Append("<text x=\"175\" y=\"").Append(y + 49).Append("\" text-anchor=\"middle\" font-family=\"").Append(Head).Append("\" font-size=\"34\" fill=\"").Append(color).Append("\">")
              .Append(X(wd + " " + d.ToString("dd.MM.", CultureInfo.InvariantCulture))).Append("</text>");
            var tx = 300;
            var maxChars = (w - tx - 60) / 24;
            sb.Append("<text x=\"").Append(tx).Append("\" y=\"").Append(y + 34).Append("\" font-family=\"").Append(Head).Append("\" font-size=\"38\" fill=\"#fff\">").Append(X(Wrap(e.Title, maxChars, 1)[0])).Append("</text>");
            var time = hasTime && (d.Hour != 0 || d.Minute != 0) ? d.ToString("HH:mm", CultureInfo.InvariantCulture) + " Uhr" : "";
            var line2 = string.Join(" · ", new[] { time, e.Place }.Where(x => x.Length > 0));
            if (line2.Length > 0)
                sb.Append("<text x=\"").Append(tx).Append("\" y=\"").Append(y + 68).Append("\" font-family=\"").Append(Body).Append("\" font-size=\"28\" fill=\"#fff\" opacity=\".85\">").Append(X(Wrap(line2, maxChars + 8, 1)[0])).Append("</text>");
            y += SeasonRow;
        }
        sb.Append("<text x=\"80\" y=\"").Append(SeasonBottom(h)).Append("\" font-family=\"").Append(Body).Append("\" font-size=\"30\" font-weight=\"700\" fill=\"#fff\" opacity=\".85\">").Append(X(footer)).Append("</text>");
        return Frame(w, h, color, photo, sb.ToString());
    }

    private static string DataUrl(PluginImage img) => "data:image/jpeg;base64," + Convert.ToBase64String(img.Jpeg);
    private static string SafeColor(string c) => System.Text.RegularExpressions.Regex.IsMatch(c ?? "", "^#[0-9a-fA-F]{6}$") ? c : "#8a1f33";
    private static string X(string s) => V.H(s);

    /// <summary>Breaks text into lines of at most <paramref name="max"/> characters (by words) — SVG text does not wrap by itself.</summary>
    private static List<string> Wrap(string text, int max, int maxLines)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var w in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + w.Length > max) { lines.Add(line); line = w; }
            else line = line.Length == 0 ? w : line + " " + w;
        }
        if (line.Length > 0) lines.Add(line);
        if (lines.Count > maxLines) { lines = lines.Take(maxLines).ToList(); lines[^1] = lines[^1].TrimEnd('.', ',') + " …"; }
        return lines;
    }

    private const string Head = "'Arial Black','Segoe UI Black','Helvetica Neue',Arial,sans-serif";
    private const string Body = "'Segoe UI','Helvetica Neue',Arial,sans-serif";

    private static string Frame(int w, int h, string color, string? photo, string inner)
    {
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 ").Append(w).Append(' ').Append(h).Append("\" width=\"").Append(w).Append("\" height=\"").Append(h).Append("\">");
        sb.Append("<defs><linearGradient id=\"sh\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#000\" stop-opacity=\".25\"/><stop offset=\".55\" stop-color=\"#000\" stop-opacity=\".45\"/><stop offset=\"1\" stop-color=\"#000\" stop-opacity=\".82\"/></linearGradient>");
        sb.Append("<clipPath id=\"lc\"><circle cx=\"0\" cy=\"0\" r=\"62\"/></clipPath></defs>");
        sb.Append("<rect width=\"").Append(w).Append("\" height=\"").Append(h).Append("\" fill=\"").Append(color).Append("\"/>");
        if (photo is not null)
            sb.Append("<image href=\"").Append(photo).Append("\" xlink:href=\"").Append(photo).Append("\" x=\"0\" y=\"0\" width=\"").Append(w).Append("\" height=\"").Append(h).Append("\" preserveAspectRatio=\"xMidYMid slice\"/>")
              .Append("<rect width=\"").Append(w).Append("\" height=\"").Append(h).Append("\" fill=\"url(#sh)\"/>");
        else
            sb.Append("<circle cx=\"").Append(w - 80).Append("\" cy=\"120\" r=\"260\" fill=\"#fff\" opacity=\".07\"/><circle cx=\"60\" cy=\"").Append(h - 60).Append("\" r=\"200\" fill=\"#000\" opacity=\".10\"/>");
        sb.Append(inner).Append("</svg>");
        return sb.ToString();
    }

    private static string LogoBadge(string? logo, int x, int y) => logo is null ? "" :
        "<g transform=\"translate(" + x + "," + y + ")\"><circle r=\"70\" fill=\"#fff\"/><image href=\"" + logo + "\" xlink:href=\"" + logo + "\" x=\"-62\" y=\"-62\" width=\"124\" height=\"124\" clip-path=\"url(#lc)\" preserveAspectRatio=\"xMidYMid meet\"/></g>";

    /// <summary>"Terminliste": a heading and up to eight dates, each with a date badge.</summary>
    private static string List(List<Event> events, int w, int h, string color, string? photo, string? logo, string club, string footer, string heading)
    {
        var sb = new StringBuilder();
        var top = h >= 1900 ? 260 : h >= 1300 ? 150 : 110;
        sb.Append(LogoBadge(logo, w - 140, top - 10));
        sb.Append("<text x=\"80\" y=\"").Append(top - 30).Append("\" font-family=\"").Append(Body).Append("\" font-size=\"30\" font-weight=\"700\" letter-spacing=\"5\" fill=\"#fff\" opacity=\".85\">").Append(X(club.ToUpperInvariant())).Append("</text>");
        sb.Append("<text x=\"80\" y=\"").Append(top + 50).Append("\" font-family=\"").Append(Head).Append("\" font-size=\"78\" fill=\"#fff\">").Append(X(heading)).Append("</text>");
        var avail = h - (top + 110) - (h >= 1900 ? 300 : 140);
        var row = Math.Min(190, avail / Math.Max(1, events.Count));
        // few dates: the list sits in the middle of the free space instead of hanging under the heading
        var y = top + 120 + Math.Max(0, (avail - row * events.Count) / 3);
        foreach (var e in events)
        {
            var (day, month, wd) = V.Badge(e.Starts);
            var box = Math.Min(140, row - 22);
            sb.Append("<rect x=\"80\" y=\"").Append(y).Append("\" width=\"").Append(box).Append("\" height=\"").Append(box).Append("\" rx=\"20\" fill=\"#fff\"/>");
            sb.Append("<text x=\"").Append(80 + box / 2).Append("\" y=\"").Append(y + box * 0.52).Append("\" text-anchor=\"middle\" font-family=\"").Append(Head).Append("\" font-size=\"").Append((int)(box * 0.42)).Append("\" fill=\"").Append(color).Append("\">").Append(X(day)).Append("</text>");
            sb.Append("<text x=\"").Append(80 + box / 2).Append("\" y=\"").Append(y + box * 0.80).Append("\" text-anchor=\"middle\" font-family=\"").Append(Body).Append("\" font-size=\"").Append((int)(box * 0.2)).Append("\" font-weight=\"800\" fill=\"").Append(color).Append("\">").Append(X(month.ToUpperInvariant() + " · " + wd)).Append("</text>");
            var tx = 80 + box + 34;
            var maxChars = (w - tx - 70) / 26;
            var title = Wrap(e.Title, maxChars, 1)[0];
            sb.Append("<text x=\"").Append(tx).Append("\" y=\"").Append(y + box * 0.45).Append("\" font-family=\"").Append(Head).Append("\" font-size=\"44\" fill=\"#fff\">").Append(X(title)).Append("</text>");
            var sub = V.When(e.Starts, weekday: false).Split(", ").Skip(1).FirstOrDefault() is { } time ? time : "";
            var line2 = string.Join(" · ", new[] { sub, e.Place }.Where(x => x.Length > 0));
            sb.Append("<text x=\"").Append(tx).Append("\" y=\"").Append(y + box * 0.80).Append("\" font-family=\"").Append(Body).Append("\" font-size=\"32\" fill=\"#fff\" opacity=\".85\">").Append(X(Wrap(line2, maxChars + 6, 1).FirstOrDefault() ?? "")).Append("</text>");
            y += row;
        }
        sb.Append("<text x=\"80\" y=\"").Append(h - (h >= 1900 ? 220 : 70)).Append("\" font-family=\"").Append(Body).Append("\" font-size=\"30\" font-weight=\"700\" fill=\"#fff\" opacity=\".85\">").Append(X(footer)).Append("</text>");
        return Frame(w, h, color, photo, sb.ToString());
    }

    /// <summary>"Plakat": one event, big — date block, title, time and place.</summary>
    private static string Poster(Event e, int w, int h, string color, string? photo, string? logo, string club, string footer, string heading)
    {
        var sb = new StringBuilder();
        var top = h >= 1900 ? 260 : 110;
        sb.Append(LogoBadge(logo, 150, top + 40));
        sb.Append("<text x=\"").Append(logo is null ? 80 : 250).Append("\" y=\"").Append(top + 52).Append("\" font-family=\"").Append(Body).Append("\" font-size=\"32\" font-weight=\"800\" letter-spacing=\"4\" fill=\"#fff\">").Append(X(club.ToUpperInvariant())).Append("</text>");
        // laid out from the bottom up, so a long title or place never runs into the line at the foot
        var footerY = h - (h >= 1900 ? 300 : 70);
        var ruleY = footerY - 54;
        var info = new[] { V.When(e.Starts), e.Place }.Where(x => x.Length > 0).SelectMany(x => Wrap(x, 44, 2)).Take(3).ToList();
        var infoFirst = ruleY - 40 - (info.Count - 1) * 50;
        var (day, month, wd) = V.Badge(e.Starts);
        var lines = Wrap(e.Title, 16, 3);
        var titleSize = lines.Count >= 3 ? 92 : 104;
        var titleTop = infoFirst - 60 - lines.Count * (titleSize + 6);
        // date block above the title
        var dateY = titleTop - 240;
        sb.Append("<rect x=\"80\" y=\"").Append(dateY).Append("\" width=\"210\" height=\"210\" rx=\"28\" fill=\"#fff\"/>");
        sb.Append("<text x=\"185\" y=\"").Append(dateY + 118).Append("\" text-anchor=\"middle\" font-family=\"").Append(Head).Append("\" font-size=\"96\" fill=\"").Append(color).Append("\">").Append(X(day)).Append("</text>");
        sb.Append("<text x=\"185\" y=\"").Append(dateY + 172).Append("\" text-anchor=\"middle\" font-family=\"").Append(Body).Append("\" font-size=\"36\" font-weight=\"800\" fill=\"").Append(color).Append("\">").Append(X(month.ToUpperInvariant() + " · " + wd)).Append("</text>");
        if (heading.Length > 0)
            sb.Append("<text x=\"320\" y=\"").Append(dateY + 120).Append("\" font-family=\"").Append(Body).Append("\" font-size=\"38\" font-weight=\"800\" letter-spacing=\"3\" fill=\"#fff\">").Append(X(heading.ToUpperInvariant())).Append("</text>");
        var y = titleTop + titleSize;
        foreach (var l in lines)
        {
            sb.Append("<text x=\"80\" y=\"").Append(y).Append("\" font-family=\"").Append(Head).Append("\" font-size=\"").Append(titleSize).Append("\" fill=\"#fff\">").Append(X(l)).Append("</text>");
            y += titleSize + 6;
        }
        var iy = infoFirst;
        foreach (var l in info) { sb.Append("<text x=\"80\" y=\"").Append(iy).Append("\" font-family=\"").Append(Body).Append("\" font-size=\"38\" font-weight=\"600\" fill=\"#fff\">").Append(X(l)).Append("</text>"); iy += 50; }
        sb.Append("<rect x=\"80\" y=\"").Append(ruleY).Append("\" width=\"120\" height=\"8\" rx=\"4\" fill=\"#fff\"/>");
        sb.Append("<text x=\"80\" y=\"").Append(footerY).Append("\" font-family=\"").Append(Body).Append("\" font-size=\"30\" font-weight=\"700\" fill=\"#fff\" opacity=\".9\">").Append(X(footer)).Append("</text>");
        return Frame(w, h, color, photo, sb.ToString());
    }
}
