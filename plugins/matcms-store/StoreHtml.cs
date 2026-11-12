using System.Globalization;
using System.Net;
using System.Text;

namespace MatCMS.Plugin.MatcmsStore;

/// <summary>
/// The look of the public store pages. Theme-able like the shop's: every colour, line and font is the SITE's variable with a fallback
/// (<c>var(--accent, …)</c>), so the store follows matcms.de's template. The cards are those of Store-Einblick 1.2, which the site already shows.
/// </summary>
internal static class StoreHtml
{
    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    public static string N(long n) => n.ToString("#,0", CultureInfo.GetCultureInfo("de-DE"));

    /// <summary>German dates by hand: the image runs with invariant globalization.</summary>
    public static string Day(DateTime? utc) => utc is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "";

    public static PluginResult Page(IPluginContext ctx, string title, string body, string? description = null, int status = 200, bool noIndex = false)
    {
        var html = "<style>" + Css + "</style><section class=\"section mst\"><div class=\"container\">" + Localize(ctx, body) + "</div></section>";
        var meta = noIndex ? new[] { new KeyValuePair<string, string>("robots", "noindex") } : null;
        var r = PluginResult.Page(html, title, description, meta);
        return status == 200 ? r : r.WithStatus(status);
    }

    private static readonly System.Text.RegularExpressions.Regex StoreLink =
        new("(href|action)=\"/store(?=[/?\"])(?!/api/)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// On a page served in another language than the site's default (<c>/en/store</c>), the store's own links keep that prefix, so a visitor who
    /// came in English stays in English. The package and picture addresses (<c>/store/api/…</c>) are the same in every language and stay as they are.
    /// </summary>
    internal static string Localize(IPluginContext ctx, string body) =>
        Prefix(ctx) is { Length: > 0 } p ? StoreLink.Replace(body, m => m.Groups[1].Value + "=\"" + p + "/store") : body;

    /// <summary>
    /// The site's own prefix for the visitor's language: "" for the default language while it lives at the plain addresses, <c>/en</c>, <c>/de</c> …
    /// for the others. Asked from the host (<see cref="IPluginSite.LocalePrefix"/>) — <c>Site.Locales[0]</c> is the first SUPPORTED language, not the
    /// default one, so a site whose default is English sent its English visitors to <c>/en/store</c>, which the host does not serve there.
    /// </summary>
    internal static string Prefix(IPluginContext ctx) =>
        string.IsNullOrEmpty(ctx.Locale) ? "" : ctx.Site.LocalePrefix(ctx.Locale);

    /// <summary>A redirect to one of the store's own pages, in the language the visitor is in (see <see cref="Localize"/>).</summary>
    internal static PluginResult Go(IPluginContext ctx, string url) =>
        PluginResult.Redirect(url.StartsWith("/store", StringComparison.Ordinal) && !url.StartsWith("/store/api/", StringComparison.Ordinal) ? Prefix(ctx) + url : url);

    public static string Notice(string text, string tone = "ok") => $"<p class=\"mst-notice mst-{tone}\">{E(text)}</p>";

    public static string Initials(string name)
    {
        var words = name.Split([' ', '-', '(', ')', '_', '.'], StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToList();
        return words.Count switch { 0 => "?", 1 => words[0][..1].ToUpperInvariant(), _ => (words[0][..1] + words[1][..1]).ToUpperInvariant() };
    }

    public static string KindBadge(IPluginContext ctx, string kind) =>
        $"<span class=\"mst-badge {(kind == "native" ? "mst-native" : "mst-script")}\">{E(ctx.T(StoreText.KindKey(kind)))}</span>";

    /// <summary>One card of the catalogue: cover (or the initials), name, publisher, the beginning of the text, version and downloads.</summary>
    public static string Card(IPluginContext ctx, Item item, Publisher? pub, Picture? cover)
    {
        var sb = new StringBuilder();
        sb.Append("<article class=\"mst-card\"><a class=\"mst-media\" href=\"").Append(E(StoreUrls.Item(item.Key))).Append("\">");
        if (cover is not null)
            sb.Append("<span class=\"mst-cover\"><img src=\"").Append(E(StoreUrls.Image(item.Key, cover.Id))).Append("\" alt=\"").Append(E(cover.Caption.Length > 0 ? cover.Caption : item.Name))
              .Append("\" loading=\"lazy\"></span>");
        else sb.Append("<span class=\"mst-ph\" aria-hidden=\"true\">").Append(E(Initials(item.Name))).Append("</span>");
        sb.Append("</a>").Append(KindBadge(ctx, item.LatestKind)).Append("<div class=\"mst-body\"><h3><a href=\"").Append(E(StoreUrls.Item(item.Key))).Append("\">")
          .Append(E(item.Name)).Append("</a></h3>");
        if (pub is not null)
            sb.Append("<div class=\"mst-by\">").Append(E(ctx.T("store.by"))).Append(" <a href=\"").Append(E(StoreUrls.Publisher(pub.Slug))).Append("\">")
              .Append(E(pub.Name)).Append("</a>").Append(pub.Verified ? "<span class=\"mst-check\" title=\"" + E(ctx.T("store.verified")) + "\">✓</span>" : "").Append("</div>");
        var text = item.Summary.Length > 0 ? item.Summary : item.Description;
        if (text.Length > 0) sb.Append("<p class=\"mst-desc\">").Append(E(text)).Append("</p>");
        sb.Append("<div class=\"mst-foot\"><span class=\"mst-chip\">").Append(E(ctx.T("store.version", item.LatestVersion))).Append("</span><span>")
          .Append(E(ctx.T("store.downloads", N(item.Downloads)))).Append("</span></div></div></article>");
        return sb.ToString();
    }

    public const string Css = """
.mst [hidden]{display:none!important}
.mst .mst-head{display:flex;flex-wrap:wrap;align-items:flex-end;justify-content:space-between;gap:16px;margin-bottom:6px}
.mst .mst-head h1{margin:0}
.mst .mst-intro{color:var(--muted,#6b7280);max-width:760px;margin:8px 0 0}
.mst .mst-bar{display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:12px;margin:22px 0 18px}
.mst .mst-tabs{display:flex;flex-wrap:wrap;gap:8px;min-width:0}
.mst .mst-tab{display:inline-flex;align-items:center;gap:8px;font-size:14px;font-weight:600;line-height:1;padding:9px 14px;border-radius:999px;border:1px solid var(--line,#e5e7eb);background:var(--bg,#fff);color:var(--ink,#111827);text-decoration:none}
.mst .mst-tab:hover{border-color:var(--accent,#2563eb)}
.mst .mst-tab.is-on{background:var(--accent,#2563eb);border-color:var(--accent,#2563eb);color:#fff}
.mst .mst-search{display:flex;gap:8px;flex:0 1 380px;min-width:0}
.mst .mst-search input{flex:1;min-width:0;font:inherit;font-size:15px;padding:10px 16px;border:1px solid var(--line,#e5e7eb);border-radius:999px;background:var(--bg,#fff);color:var(--ink,#111827)}
.mst .mst-search input:focus{outline:none;border-color:var(--accent,#2563eb);box-shadow:0 0 0 3px color-mix(in srgb,var(--accent,#2563eb) 18%,transparent)}
.mst .mst-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:22px}
.mst .mst-card{position:relative;display:flex;flex-direction:column;border:1px solid var(--line,#e5e7eb);border-radius:16px;background:var(--bg,#fff);overflow:hidden;transition:box-shadow .2s,border-color .2s}
.mst .mst-card:hover{border-color:color-mix(in srgb,var(--accent,#2563eb) 35%,var(--line,#e5e7eb));box-shadow:0 18px 40px -20px rgba(15,23,42,.35)}
.mst .mst-media{display:block;aspect-ratio:16/10;padding:20px 20px 0;overflow:hidden;background:radial-gradient(120% 90% at 0 0,color-mix(in srgb,var(--accent,#2563eb) 20%,var(--bg,#fff)) 0,transparent 60%),linear-gradient(160deg,color-mix(in srgb,var(--accent,#2563eb) 9%,var(--bg,#fff)),var(--bg-alt,#f3f4f6))}
.mst .mst-cover{display:block;height:100%;border-radius:10px 10px 0 0;overflow:hidden;background:#fff;box-shadow:0 12px 32px -10px rgba(15,23,42,.4),0 0 0 1px rgba(15,23,42,.07)}
.mst .mst-cover img{width:100%;height:100%;object-fit:cover;object-position:top center;display:block;transition:transform .4s}
.mst .mst-card:hover .mst-cover img{transform:scale(1.035)}
.mst .mst-ph{display:flex;height:100%;align-items:center;justify-content:center;padding-bottom:20px;font-family:var(--font-head,inherit);font-size:72px;font-weight:800;color:color-mix(in srgb,var(--accent,#2563eb) 45%,var(--bg,#fff))}
.mst .mst-badge{display:inline-block;font-size:12px;font-weight:700;line-height:1;padding:6px 10px;border-radius:999px;white-space:nowrap}
.mst .mst-card>.mst-badge{position:absolute;top:12px;right:12px;box-shadow:0 4px 12px rgba(15,23,42,.18)}
.mst .mst-script{background:color-mix(in srgb,var(--accent,#2563eb) 13%,var(--bg,#fff));color:var(--accent,#2563eb)}
.mst .mst-card>.mst-script{background:#fff}
.mst .mst-native{background:var(--ink,#111827);color:#fff}
.mst .mst-body{display:flex;flex-direction:column;gap:6px;flex:1;padding:16px 20px 18px}
.mst .mst-body h3{margin:0;font-size:19px;line-height:1.25}
.mst .mst-body h3 a{color:inherit;text-decoration:none}
.mst .mst-body h3 a::after{content:"";position:absolute;inset:0}
.mst .mst-by{font-size:13.5px;color:var(--muted,#6b7280)}
.mst .mst-by a{position:relative;z-index:1;color:var(--ink,#111827);font-weight:600;text-decoration:none}
.mst .mst-check{margin-left:4px;color:var(--accent,#2563eb);font-weight:700}
.mst .mst-desc{margin:2px 0 0;color:var(--muted,#4b5563);font-size:15px;line-height:1.55;display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:3;overflow:hidden}
.mst .mst-foot{display:flex;flex-wrap:wrap;align-items:center;gap:10px;margin-top:auto;padding-top:12px;font-size:13px;color:var(--muted,#6b7280)}
.mst .mst-chip{padding:4px 9px;border-radius:999px;background:var(--bg-alt,#f3f4f6);font-weight:600;color:var(--ink,#374151)}
.mst .mst-empty{color:var(--muted,#6b7280);padding:30px 0}
.mst .mst-notice{padding:12px 16px;border-radius:12px;margin:12px 0;font-size:15px}
.mst .mst-ok{background:color-mix(in srgb,#16a34a 10%,var(--bg,#fff));border:1px solid color-mix(in srgb,#16a34a 30%,var(--bg,#fff))}
.mst .mst-err{background:color-mix(in srgb,#dc2626 9%,var(--bg,#fff));border:1px solid color-mix(in srgb,#dc2626 30%,var(--bg,#fff))}
.mst .mst-info{background:color-mix(in srgb,var(--accent,#2563eb) 8%,var(--bg,#fff));border:1px solid color-mix(in srgb,var(--accent,#2563eb) 22%,var(--bg,#fff))}
.mst .mst-crumbs{font-size:14px;color:var(--muted,#6b7280);margin-bottom:14px}
.mst .mst-crumbs a{color:inherit}
.mst .mst-dhead{display:flex;align-items:center;gap:18px;flex-wrap:wrap}
.mst .mst-dhead h1{margin:0;font-size:34px;line-height:1.15}
.mst .mst-dhead>div{flex:1;min-width:0}
.mst .mst-mono{flex:0 0 auto;width:64px;height:64px;border-radius:18px;display:flex;align-items:center;justify-content:center;font-family:var(--font-head,inherit);font-size:24px;font-weight:800;background:color-mix(in srgb,var(--accent,#2563eb) 13%,var(--bg,#fff));color:var(--accent,#2563eb)}
.mst .mst-mono.mst-native{background:var(--ink,#111827);color:#fff}
.mst .mst-strip{display:grid;grid-auto-flow:column;grid-auto-columns:44%;gap:16px;overflow-x:auto;scroll-snap-type:x mandatory;margin:26px 0;padding-bottom:10px;scrollbar-width:thin}
.mst .mst-strip.mst-one{grid-auto-columns:100%}
.mst .mst-strip a{display:flex;flex-direction:column;gap:8px;color:inherit;text-decoration:none;scroll-snap-align:start;cursor:zoom-in}
.mst .mst-shot{display:block;aspect-ratio:16/10;border:1px solid var(--line,#e5e7eb);border-radius:12px;overflow:hidden;background:var(--bg-alt,#f3f4f6)}
.mst .mst-shot img{width:100%;height:100%;object-fit:cover;object-position:top center;display:block}
.mst .mst-cap{font-size:13.5px;color:var(--muted,#6b7280)}
.mst .mst-cols{display:grid;grid-template-columns:minmax(0,1.7fr) minmax(0,1fr);gap:34px;align-items:start}
.mst .mst-text p{margin:0 0 12px;font-size:16px;line-height:1.7}
.mst .mst-facts{margin:0;padding:4px 18px;border:1px solid var(--line,#e5e7eb);border-radius:14px;background:var(--bg-alt,#f6f7f9);font-size:14px}
.mst .mst-facts div{display:flex;justify-content:space-between;gap:14px;padding:11px 0;border-top:1px solid var(--line,#e5e7eb)}
.mst .mst-facts div:first-child{border-top:0}
.mst .mst-facts dt{color:var(--muted,#6b7280)}
.mst .mst-facts dd{margin:0;font-weight:600;text-align:right;overflow-wrap:anywhere}
.mst .mst-reviews{margin:30px 0 0}
.mst .mst-reviews h2{font-size:22px}
.mst .mst-stars{color:var(--accent,#d97706);letter-spacing:1px;white-space:nowrap}
.mst .mst-stars span{color:var(--text,#4b5563);letter-spacing:0;margin-left:4px}
.mst .mst-review{padding:12px 0;border-top:1px solid var(--line,#e5e7eb)}
.mst .mst-review p{margin:6px 0}
.mst .mst-review-head{display:flex;gap:8px;align-items:baseline;flex-wrap:wrap}
.mst .mst-answer{margin:8px 0 0 16px;padding:8px 12px;border-left:3px solid var(--line,#e5e7eb)}
.mst .mst-report{margin:4px 0 0}
.mst .mst-link{background:none;border:0;padding:0;color:var(--muted,#6b7280);font-size:13px;text-decoration:underline;cursor:pointer}
.mst .mst-starpick{border:0;padding:0;margin:0 0 8px;display:inline-flex;flex-direction:row-reverse;gap:2px}
.mst .mst-starpick legend{float:left;width:100%;font-weight:600;margin-bottom:4px}
.mst .mst-starpick input{position:absolute;opacity:0;width:1px;height:1px}
.mst .mst-starpick label{font-size:28px;line-height:1;cursor:pointer;color:var(--line,#d1d5db)}
.mst .mst-starpick input:checked~label,.mst .mst-starpick label:hover,.mst .mst-starpick label:hover~label{color:var(--accent,#d97706)}
.mst .mst-starpick input:focus-visible+label{outline:2px solid var(--accent,#d97706);outline-offset:2px}
.mst .mst-versions{margin:30px 0 0}
.mst .mst-versions h2{font-size:22px}
.mst .mst-version{padding:12px 0;border-top:1px solid var(--line,#e5e7eb)}
.mst .mst-version strong{margin-right:10px}
.mst .mst-version p{margin:6px 0 0;color:var(--muted,#4b5563);white-space:pre-line}
.mst .mst-actions{display:flex;flex-wrap:wrap;gap:10px;margin:18px 0 0}
.mst .mst-form{max-width:640px}
.mst .mst-form label{display:block;font-weight:600;margin:14px 0 6px}
.mst .mst-form input[type=text],.mst .mst-form input[type=email],.mst .mst-form input[type=password],.mst .mst-form input[type=url],.mst .mst-form textarea{width:100%;font:inherit;padding:10px 12px;border:1px solid var(--line,#e5e7eb);border-radius:10px;background:var(--bg,#fff);color:var(--ink,#111827)}
.mst .mst-form textarea{min-height:120px}
.mst .mst-hint{font-size:13.5px;color:var(--muted,#6b7280);margin:6px 0 0}
.mst .mst-check-row{display:flex;gap:8px;align-items:flex-start;margin:16px 0}
.mst .mst-check-row label{display:inline;font-weight:400;margin:0}
.mst .mst-table{width:100%;border-collapse:collapse;font-size:15px}
.mst .mst-table th,.mst .mst-table td{text-align:left;padding:10px 8px;border-bottom:1px solid var(--line,#e5e7eb);vertical-align:top}
.mst .mst-status{font-size:12px;font-weight:700;padding:4px 9px;border-radius:999px;background:var(--bg-alt,#f3f4f6);white-space:nowrap}
.mst .mst-pics{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:14px;margin:12px 0}
.mst .mst-pics figure{margin:0;border:1px solid var(--line,#e5e7eb);border-radius:12px;overflow:hidden}
.mst .mst-pics img{width:100%;aspect-ratio:16/10;object-fit:cover;object-position:top;display:block}
.mst .mst-pics figcaption{padding:8px 10px;font-size:13px}
.mst .mst-pics form{display:inline}
@media (max-width:900px){.mst .mst-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}
@media (max-width:700px){.mst .mst-cols{grid-template-columns:minmax(0,1fr)}.mst .mst-strip{grid-auto-columns:82%}.mst .mst-dhead h1{font-size:26px}}
@media (max-width:600px){.mst .mst-grid{grid-template-columns:minmax(0,1fr)}.mst .mst-search{flex-basis:100%}}
""";
}
