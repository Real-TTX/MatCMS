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
    public const string Color = "instaColor", Logo = "instaLogo", Hashtags = "instaHashtags";

    public static void Declare(ISettingsBuilder s) => s
        .Add(new SettingSpec(Color, "Instagram-Bild: Vereinsfarbe", SettingKind.Color) { Default = "#8a1f33" })
        .Add(new SettingSpec(Logo, "Instagram-Bild: Logo", SettingKind.Image) { Help = "Erscheint in einem weißen Kreis auf dem Bild (ein Bild aus der Mediathek; transparente Flächen werden weiß)." })
        .Text(Hashtags, "Instagram-Bild: Zeile unten", "", "z. B. #fasnet #narren #besigheim – leer = Name des Vereins.");

    private static readonly (string Key, string Label, int W, int H)[] Formats = { ("square", "Quadrat 1080 × 1080", 1080, 1080), ("portrait", "Hochformat 1080 × 1350", 1080, 1350), ("story", "Story 1080 × 1920", 1080, 1920) };

    public static async Task<PluginResult> PageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        string P(string k) => ctx.Request.IsPost ? ctx.Request.F(k) : ctx.Request.Q(k);
        var template = P("vorlage") == "plakat" ? "plakat" : "liste";
        var format = Formats.FirstOrDefault(f => f.Key == P("format")) is { Key: not null } ff ? ff : Formats[0];
        var upcoming = await store.UpcomingAsync(V.Today, publicOnly: false, 50, ct);
        var all = upcoming.Where(e => !e.Cancelled).ToList();
        long.TryParse(P("event"), out var eventId);
        var kind = P("art").Trim();
        var count = int.TryParse(P("anzahl"), out var n) ? Math.Clamp(n, 1, 8) : 5;
        var heading = P("titel") is { Length: > 0 } t ? V.Clip(t, 40) : template == "plakat" ? "" : "Nächste Termine";
        var photo = P("bild");
        var onlyPublic = ctx.Request.IsPost ? V.Checked(ctx.Request.F("oeffentlich")) : true;

        var events = (onlyPublic ? all.Where(e => e.Public) : all).Where(e => kind.Length == 0 || string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase)).ToList();
        var single = template == "plakat" ? (all.FirstOrDefault(e => e.Id == eventId) ?? events.FirstOrDefault()) : null;

        string? photoData = null, logoData = null;
        if (photo.StartsWith("/uploads/", StringComparison.Ordinal) && await ctx.Site.LoadImageAsync(photo, 1400, ct) is PluginImage pi) photoData = DataUrl(pi);
        if (ctx.Settings.Get(Logo) is { Length: > 0 } logo && await ctx.Site.LoadImageAsync(logo, 400, ct) is PluginImage li) logoData = DataUrl(li);
        var color = SafeColor(ctx.Settings.Get(Color));
        var footer = ctx.Settings.Get(Hashtags) is { Length: > 0 } h ? h : VereinSettings.Club(ctx);

        var svg = template == "plakat"
            ? (single is null ? null : Poster(single, format.W, format.H, color, photoData, logoData, VereinSettings.Club(ctx), footer, heading))
            : (events.Count == 0 ? null : List(events.Take(count).ToList(), format.W, format.H, color, photoData, logoData, VereinSettings.Club(ctx), footer, heading));

        var kinds = all.Select(e => e.Kind).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var form = Ui.Form(
            Ui.Grid(200,
                Ui.Select("vorlage", "Vorlage", template, Ui.Option("liste", "Terminliste"), Ui.Option("plakat", "Plakat (ein Termin)")),
                Ui.Select("format", "Format", format.Key, Formats.Select(f => Ui.Option(f.Key, f.Label)).ToArray()),
                Ui.Input("titel", "Überschrift", heading)),
            template == "plakat"
                ? Ui.Select("event", "Termin", single?.Id.ToString(CultureInfo.InvariantCulture) ?? "", all.Select(e => Ui.Option(e.Id.ToString(CultureInfo.InvariantCulture), V.Date(e.Starts) + " – " + e.Title)).ToArray())
                : Ui.Grid(200,
                    Ui.Select("art", "Nur diese Art", kind, new[] { Ui.Option("", "alle") }.Concat(kinds.Select(k => Ui.Option(k, k))).ToArray()),
                    Ui.Select("anzahl", "Wie viele", count.ToString(CultureInfo.InvariantCulture), new[] { "3", "4", "5", "6", "8" }.Select(x => Ui.Option(x, x)).ToArray())),
            Ui.Image("bild", "Hintergrundfoto (freiwillig)", photo),
            template == "plakat" ? null : Ui.Checkbox("oeffentlich", "Nur öffentliche Termine", onlyPublic),
            Ui.FormActions(Ui.Submit("Vorschau aktualisieren")));

        UiNode preview = svg is null
            ? Ui.Empty(template == "plakat" ? "Kein kommender Termin zum Zeigen." : "Keine passenden kommenden Termine.")
            : Ui.Html("<div id=\"vm-ig\" style=\"max-width:" + (format.H > format.W ? 360 : 460) + "px\">" + svg.Replace("<svg ", "<svg style=\"width:100%;height:auto;display:block;border-radius:12px;box-shadow:0 10px 30px rgba(0,0,0,.18)\" ") + "</div>" +
                "<p style=\"margin-top:14px\"><button type=\"button\" class=\"btn\" id=\"vm-ig-dl\"><i class=\"ti ti-download\"></i> PNG herunterladen</button></p>" +
                "<script>(function(){var b=document.getElementById('vm-ig-dl');b.addEventListener('click',function(){var svg=document.querySelector('#vm-ig svg');" +
                "var s=new XMLSerializer().serializeToString(svg).replace(/ style=\"[^\"]*\"/,'');var img=new Image();img.onload=function(){var c=document.createElement('canvas');c.width=" + format.W + ";c.height=" + format.H +
                ";c.getContext('2d').drawImage(img,0,0," + format.W + "," + format.H + ");c.toBlob(function(bl){var a=document.createElement('a');a.href=URL.createObjectURL(bl);a.download='" + (template == "plakat" ? "termin" : "termine") + "-" + format.Key +
                ".png';document.body.appendChild(a);a.click();a.remove();},'image/png');};img.src='data:image/svg+xml;charset=utf-8,'+encodeURIComponent(s);});})();</script>");

        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Termine als Bild für Instagram, Facebook oder WhatsApp – Vorlage wählen, Vorschau ansehen, herunterladen", "Instagram-Bild"),
            Ui.Columns(new UiNode?[] { Ui.Card("Einstellungen", form), Ui.Muted("Farbe, Logo und die Zeile unten stehen in den Einstellungen des Vereins.") }, new UiNode?[] { Ui.Card("Vorschau", preview) })), "Instagram-Bild");
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
