using System.Net;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The EU harmonised notice on the legal guarantee of conformity (Directive (EU) 2024/825, Implementing Regulation (EU) 2025/1960, applies since
/// 27 September 2026): every seller of GOODS to consumers must show it "in a prominent manner". The Commission's practical guidelines (April 2026) say how
/// online: the FULL notice, colour/RGB, unaltered, legible at default size, appearing on the first click of a sentence such as "Your legal guarantee
/// rights" (product catalogue, header, checkout), a clickable link to the QR code's destination always beside it, and the notice in the confirmation mail.
/// <para>The notice is the Commission's own file (<c>Resources/guarantee-notice-{de,en}.svg</c>, unchanged; the notice may not be redrawn or edited), served by
/// the shop at <see cref="ImagePath"/> with a sandbox policy — an SVG from the site's own origin is code, and plugin assets may not carry SVG at all. The
/// sentence opens it as an overlay with a plain <c>&lt;details&gt;</c>: no script, so it works everywhere the shop does. Only German and English exist as
/// files here; every other page language gets the English notice (the Your Europe link still goes to that language where the guidelines name one).</para>
/// <para>Shown only where goods are sold: a product page of a physical product, a checkout with goods in it, the mails of an order with goods. A shop that
/// sells only downloads is not a seller of goods. The switch (<see cref="Keys.GuaranteeNotice"/>) exists for a shop that does not sell to
/// consumers at all — it is on by default.</para>
/// </summary>
internal static class ShopGuarantee
{
    public const string PagePath = "/shop/gewaehrleistung";
    public const string ImagePath = "/shop/gewaehrleistung/notice";

    /// <summary>The QR code's destination per language — the guidelines' table (section 2.3); a language without its own entry gets the English page.</summary>
    private static readonly Dictionary<string, string> YourEurope = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de"] = "https://europa.eu/youreurope/garantien", ["en"] = "https://europa.eu/youreurope/guarantees", ["fr"] = "https://europa.eu/youreurope/garanties",
        ["nl"] = "https://europa.eu/youreurope/garantie", ["it"] = "https://europa.eu/youreurope/garanzie", ["hr"] = "https://europa.eu/youreurope/jamstva_hr",
        ["sk"] = "https://europa.eu/youreurope/záruky_sk", ["pl"] = "https://europa.eu/youreurope/gwarancje", ["da"] = "https://europa.eu/youreurope/garantier",
        ["pt"] = "https://europa.eu/youreurope/garantias", ["es"] = "https://europa.eu/youreurope/garantías", ["sv"] = "https://europa.eu/youreurope/reklamationsrätt",
        ["cs"] = "https://europa.eu/youreurope/záruky_cs", ["sl"] = "https://europa.eu/youreurope/jamstva_sl", ["fi"] = "https://europa.eu/youreurope/virhevastuu",
    };

    /// <summary>The language of the notice FILE: German or English (the only two shipped).</summary>
    public static string NoticeLanguage(string? locale) => string.Equals(locale, "de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";

    public static string InfoUrl(string? locale) => YourEurope.TryGetValue((locale ?? "").Trim(), out var u) ? u : YourEurope["en"];

    public static void Register(IPluginBuilder b)
    {
        var get = new PublicRouteOptions { Methods = PluginMethod.Get };
        b.Route(PagePath, PageAsync, get);
        b.Route(ImagePath + "/{lang}", ImageAsync, get);
        // The "general reminder on the website" of the guidelines: a footer link, added once (an editor may move or delete it). Nothing while the switch is off.
        b.MenuElement(new MenuElementSpec("guarantee", "shop.el.guarantee") { Help = "shop.el.guaranteeHelp", Icon = "shield-check", DefaultMenu = "footer" },
            (ctx, ct) => Task.FromResult(ShopConfig.From(ctx.Settings).GuaranteeNotice ? new MenuEntry(ctx.T("shop.guarantee.link"), PagePath) : null));
    }

    /// <summary>
    /// The sentence that opens the full notice on the first click, with the link to Your Europe under it. <paramref name="compact"/> drops the outer margin
    /// (inside the product page's list). Empty while the switch is off.
    /// </summary>
    public static string Html(IPluginContext ctx, ShopConfig cfg, bool compact = false)
    {
        if (!cfg.GuaranteeNotice) return "";
        var lang = NoticeLanguage(ctx.Locale);
        var info = InfoUrl(ctx.Locale);
        string E(string s) => WebUtility.HtmlEncode(s);
        return $"<details class=\"shop-guarantee{(compact ? " compact" : "")}\"><summary><span class=\"open\">{E(ctx.T("shop.guarantee.link"))}</span><span class=\"close\" aria-label=\"{E(ctx.T("shop.guarantee.close"))}\">×</span></summary>"
            + "<div class=\"shop-guarantee-body\"><div class=\"shop-guarantee-sheet\">"
            + $"<a href=\"{E(info)}\" target=\"_blank\" rel=\"noopener\"><img src=\"{ImagePath}/{lang}\" alt=\"{E(ctx.T("shop.guarantee.alt"))}\" width=\"595\" height=\"842\" loading=\"lazy\"></a>"
            + $"<p><a href=\"{E(info)}\" target=\"_blank\" rel=\"noopener\">{E(info.Replace("https://", ""))}</a></p></div></div></details>";
    }

    /// <summary>The lines an order mail carries (plain text): the link to the full notice on this site and to Your Europe. Empty when off or the order holds no goods.</summary>
    public static string MailText(IPluginContext ctx, ShopConfig cfg, Order o, IReadOnlyList<OrderItem> items)
    {
        if (!cfg.GuaranteeNotice || items.Count == 0 || items.All(i => i.Kind == ProductKind.Digital)) return "";
        var en = o.Locale == "en";
        return "\n" + (en ? "Your legal guarantee rights (EU notice): " : "Ihre gesetzlichen Gewährleistungsrechte (EU-Hinweis): ") + ctx.Site.Origin + PagePath
            + "\n" + (en ? "More information: " : "Mehr dazu: ") + InfoUrl(o.Locale) + "\n";
    }

    /// <summary>The notice on a page of its own — what the footer link and the mails point to.</summary>
    private static async Task<PluginResult> PageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        if (!cfg.GuaranteeNotice) return PluginResult.NotFound();
        var lang = NoticeLanguage(ctx.Locale);
        var info = InfoUrl(ctx.Locale);
        string E(string s) => WebUtility.HtmlEncode(s);
        var html = $"<h1>{E(ctx.T("shop.guarantee.title"))}</h1><div class=\"shop-guarantee-page\">"
            + $"<a href=\"{E(info)}\" target=\"_blank\" rel=\"noopener\"><img src=\"{ImagePath}/{lang}\" alt=\"{E(ctx.T("shop.guarantee.alt"))}\" width=\"595\" height=\"842\"></a>"
            + $"<p><a href=\"{E(info)}\" target=\"_blank\" rel=\"noopener\">{E(info.Replace("https://", ""))}</a></p></div>";
        return await ShopHtml.PageAsync(ctx, cfg, ctx.T("shop.guarantee.title"), html);
    }

    /// <summary>The Commission's file, unchanged. Served with a sandbox policy (it is an SVG from this origin) and cached for a day — it changes only with the shop.</summary>
    private static Task<PluginResult> ImageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var lang = ctx.Request.R("lang") == "de" ? "de" : ctx.Request.R("lang") == "en" ? "en" : "";
        if (lang.Length == 0) return Task.FromResult(PluginResult.NotFound());
        return Task.FromResult(PluginResult.File(Svg(lang), "image/svg+xml")
            .WithHeader("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; img-src data:; sandbox")
            .WithHeader("X-Content-Type-Options", "nosniff")
            .WithHeader("Cache-Control", "public, max-age=86400"));
    }

    private static readonly Dictionary<string, byte[]> Files = new();

    internal static byte[] Svg(string lang)
    {
        lock (Files)
        {
            if (Files.TryGetValue(lang, out var b)) return b;
            using var s = typeof(ShopGuarantee).Assembly.GetManifestResourceStream($"MatCMS.Plugin.Shop.Resources.guarantee-notice-{lang}.svg")
                ?? throw new InvalidOperationException("guarantee notice missing: " + lang);
            using var m = new MemoryStream();
            s.CopyTo(m);
            return Files[lang] = m.ToArray();
        }
    }
}
