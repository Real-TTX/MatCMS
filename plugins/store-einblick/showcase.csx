// Store-Einblick — the shared code of BOTH variants: the script plugin loads this file (plugin.csx: #load "showcase.csx"), the native one
// compiles it (store-einblick-native/StoreEinblick.csproj links it). So it is written as plain C# that is valid in both: usings, then types,
// no namespace (a script cannot declare one) and no top-level statements (a library cannot have them). Everything goes through the plugin
// API only — no host type — and JSON only through JsonNode, never reflection over own types (that would pin a native plugin in memory).
// Spelled out: a script has none of a project's implicit usings, and the two variants must compile from the same text.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MatCMS.Plugin;

/// <summary>The plugin: a block that shows the plugin store of a MatCMS cloud — plugins (with their kind: script or native, their publisher
/// and their pictures), templates (as a small picture of a page in their own colours) and components (with their icon) — read from the
/// cloud's public catalogue (<c>GET {cloud}/api/public/store</c>, no key needed), kept for a while in memory. The pictures are passed through
/// THIS site (<c>/store-einblick/bild/{key}/{id}</c>), never linked to the cloud: a visitor's browser talks to the website alone.</summary>
public static class SeShowcase
{
    public const string BlockType = "store_einblick";
    private const string Agent = "MatCMS-Store-Einblick/1.3";

    public static void Configure(IPluginBuilder b)
    {
        b.Strings("de", SeText.De);
        b.Strings("en", SeText.En);

        b.Settings
            .Choice("source", "se.set.source", "cloud", new[] { new FieldOption("cloud", "se.o.srcCloud"), new FieldOption("matcms", "se.o.srcMatcms") }, "se.set.sourceHelp")
            .Text("storeUrl", "se.set.url", "https://cloud2.matthix.de", "se.set.urlHelp")
            .Number("cacheMinutes", "se.set.cache", 30, 1, 1440, "se.set.cacheHelp");

        b.Block(new BlockSpec(BlockType, "se.block.name", "se.block.desc")
        {
            Category = "design",
            IconSvg = "<path d=\"M3 9l1.5-5h15L21 9\"/><path d=\"M4 9v11h16V9\"/><path d=\"M3 9a3 3 0 0 0 6 0 3 3 0 0 0 6 0 3 3 0 0 0 6 0\"/>",
            Fields = new[]
            {
                new FieldSpec("heading", "se.f.heading"),
                new FieldSpec("intro", "se.f.intro", FieldKind.Textarea),
                new FieldSpec("show", "se.f.show", FieldKind.Select)
                {
                    Default = "plugins",
                    Options = new[] { new FieldOption("plugins", "se.o.plugins"), new FieldOption("templates", "se.o.templates"), new FieldOption("components", "se.o.components"), new FieldOption("all", "se.o.all") }
                },
                new FieldSpec("kind", "se.f.kind", FieldKind.Select)
                {
                    Default = "", ShowWhenField = "show", ShowWhenValue = "plugins",
                    Options = new[] { new FieldOption("", "se.o.kindAll"), new FieldOption("script", "se.o.kindScript"), new FieldOption("native", "se.o.kindNative") }
                },
                new FieldSpec("limit", "se.f.limit") { Help = "se.f.limitHelp" },
                new FieldSpec("columns", "se.f.columns", FieldKind.Select)
                {
                    Default = "3", Options = new[] { new FieldOption("2", "2"), new FieldOption("3", "3"), new FieldOption("4", "4") }
                },
                new FieldSpec("counts", "se.f.counts", FieldKind.Select)
                {
                    Default = "yes", Options = new[] { new FieldOption("yes", "se.o.yes"), new FieldOption("no", "se.o.no") }
                },
                new FieldSpec("moreText", "se.f.moreText"),
                new FieldSpec("moreUrl", "se.f.moreUrl", FieldKind.Url)
            }
        }, RenderAsync);

        b.Admin.Section("se.nav", "ti-building-store").Page("status", "se.tab.status", StatusAsync);
        b.Route(ImageRoute + "/{key}/{id}", ImageAsync, new PublicRouteOptions { Methods = PluginMethod.Get });
    }

    /// <summary>Where this site serves a store picture. The same in both variants, so they draw the same HTML.</summary>
    public const string ImageRoute = "/store-einblick/bild";

    // ---- the catalogue ----------------------------------------------------------------------------------------------

    private sealed record Cached(DateTime At, JsonObject Data);
    private static readonly ConcurrentDictionary<string, Cached> Cache = new();

    /// <summary>The catalogue of the configured cloud: from memory while it is fresh, else fetched; when the cloud cannot be reached the last
    /// copy is used however old it is (a shop window that goes blank because a server restarts is worse than a slightly old one).</summary>
    public static async Task<(JsonObject? Data, string? Error)> CatalogAsync(IPluginContext ctx, bool fresh, CancellationToken ct)
    {
        var url = BaseUrl(ctx);
        if (url is null) return (null, ctx.T("se.err.url"));
        var minutes = double.TryParse(ctx.Settings.Get("cacheMinutes"), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? Math.Clamp(m, 1, 1440) : 30;
        var cacheKey = url + "|" + Source(ctx);
        if (!fresh && Cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(minutes)) return (hit.Data, null);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Agent);
            using var res = await http.GetAsync(url + CatalogPath(ctx), ct);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException("HTTP " + (int)res.StatusCode);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (body.Length > 2_000_000 || JsonNode.Parse(body) is not JsonObject data) throw new HttpRequestException("keine gültige Antwort");
            Cache[cacheKey] = new Cached(DateTime.UtcNow, data);
            return (data, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ctx.Log("Store-Einblick: " + url + " – " + ex.Message);
            return Cache.TryGetValue(cacheKey, out var stale) ? (stale.Data, null) : (null, ctx.T("se.err.fetch"));
        }
    }

    /// <summary>Where the catalogue comes from: a MatCMS cloud's store ("cloud", <c>/api/public/store</c>) or the official MatCMS Store on
    /// matcms.de ("matcms", <c>/store/api/catalog</c> — the same shape, see plugins/matcms-store). Only these two, never a path from the settings.</summary>
    public static string Source(IPluginContext ctx) => ctx.Settings.Get("source") == "matcms" ? "matcms" : "cloud";
    private static string CatalogPath(IPluginContext ctx) => Source(ctx) == "matcms" ? "/store/api/catalog" : "/api/public/store";
    private static string ImagePath(IPluginContext ctx, string key, int id) => Source(ctx) == "matcms"
        ? "/store/api/plugins/" + Uri.EscapeDataString(key) + "/images/" + N(id)
        : "/api/public/store/plugins/" + Uri.EscapeDataString(key) + "/image/" + N(id);

    /// <summary>The cloud's address from the settings: only http(s), no path, no trailing slash.</summary>
    public static string? BaseUrl(IPluginContext ctx)
    {
        var raw = (ctx.Settings.Get("storeUrl") ?? "").Trim().TrimEnd('/');
        return Uri.TryCreate(raw, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) && string.IsNullOrEmpty(u.Query)
            ? u.GetLeftPart(UriPartial.Authority) : null;
    }

    /// <summary>An entry in the page's language: the MatCMS Store sends its German texts plus <c>translations.{lang}</c> (name, summary, description —
    /// each optional, an empty one keeps the German). A cloud's store sends no translations; its entries stay as they are. A copy, never the
    /// cached catalogue itself.</summary>
    private static JsonObject Localized(JsonObject e, string locale)
    {
        var lang = (locale ?? "").Split('-')[0].ToLowerInvariant();
        if (lang is "" or "de" || e["translations"] is not JsonObject tr || tr[lang] is not JsonObject t) return e;
        var copy = (JsonObject)e.DeepClone();
        foreach (var f in new[] { "name", "summary", "description" })
            if (S(t[f]) is { Length: > 0 } v) copy[f] = v;
        return copy;
    }

    private static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    // ---- the block --------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> RenderAsync(IPluginContext ctx, CancellationToken ct)
    {
        var block = ctx.Block;
        string F(string id) => block?.Get(id) ?? "";
        var show = F("show") is { Length: > 0 } sv ? sv : "plugins";
        var kind = F("kind");
        var limit = int.TryParse(F("limit"), out var l) && l > 0 ? l : int.MaxValue;
        var cols = F("columns") is "2" or "4" ? F("columns") : "3";
        var counts = F("counts") != "no";

        var (data, error) = await CatalogAsync(ctx, false, ct);
        var sb = new StringBuilder("<section class=\"section se-store\" data-se-store><div class=\"container\">");
        if (F("heading") is { Length: > 0 } h) sb.Append("<h2>").Append(E(h)).Append("</h2>");
        if (F("intro") is { Length: > 0 } intro) sb.Append("<p class=\"se-intro\">").Append(E(intro)).Append("</p>");
        if (data is null)
        {
            // A visitor sees a quiet line; the editor (preview) sees why.
            sb.Append("<p class=\"se-empty\">").Append(E(ctx.IsEditorPreview ? error ?? "" : ctx.T("se.unavailable"))).Append("</p>");
            return PluginResult.Fragment(sb.Append("</div></section>").Append(Css).ToString());
        }

        List<JsonObject> All(string name, bool wanted, Func<JsonObject, bool>? keep = null) => wanted
            ? (data[name] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(x => keep is null || keep(x)).Select(x => Localized(x, ctx.Locale)).ToList()
            : new List<JsonObject>();
        var allPlugins = All("plugins", show is "plugins" or "all", p => kind.Length == 0 || S(p["kind"]) == kind);
        var allTemplates = All("templates", show is "templates" or "all");
        var allComponents = All("components", show is "components" or "all");
        var plugins = allPlugins.Take(limit).ToList();
        var templates = allTemplates.Take(limit).ToList();
        var components = allComponents.Take(limit).ToList();
        var shown = plugins.Count + templates.Count + components.Count;

        // The toolbar — the kinds as tabs, a search box — only where it helps, and hidden until the script runs: without it every entry stays
        // visible and nothing on the page pretends to filter.
        var grouped = show == "all";
        var tabs = grouped && new[] { plugins.Count, templates.Count, components.Count }.Count(n => n > 0) > 1;
        var search = shown > 6;
        if (tabs || search)
        {
            sb.Append("<div class=\"se-bar\" hidden>");
            if (tabs)
            {
                sb.Append("<div class=\"se-tabs\">");
                Tab(sb, "", ctx.T("se.tabAll"), shown, counts, true);
                if (plugins.Count > 0) Tab(sb, "plugins", ctx.T("se.o.plugins"), plugins.Count, counts, false);
                if (templates.Count > 0) Tab(sb, "templates", ctx.T("se.o.templates"), templates.Count, counts, false);
                if (components.Count > 0) Tab(sb, "components", ctx.T("se.o.components"), components.Count, counts, false);
                sb.Append("</div>");
            }
            if (search)
                sb.Append("<input type=\"search\" class=\"se-search\" placeholder=\"").Append(E(ctx.T("se.search"))).Append("\" aria-label=\"")
                  .Append(E(ctx.T("se.search"))).Append("\" autocomplete=\"off\">");
            sb.Append("</div>");
        }
        if (counts && !grouped)
        {
            // One kind: how many the store holds (the headings of "everything" carry their own numbers).
            var parts = new List<string>();
            if (show == "plugins") parts.Add(ctx.T("se.count.plugins", allPlugins.Count));
            if (show == "templates") parts.Add(ctx.T("se.count.templates", allTemplates.Count));
            if (show == "components") parts.Add(ctx.T("se.count.components", allComponents.Count));
            sb.Append("<p class=\"se-counts\">").Append(E(string.Join(" · ", parts))).Append("</p>");
        }

        if (show is "plugins" or "all")
        {
            OpenGroup(sb, "plugins", grouped ? ctx.T("se.o.plugins") : null, plugins.Count, counts, cols);
            foreach (var p in plugins) PluginCard(sb, ctx, p);
            sb.Append("</div>");
            if (plugins.Count == 0) sb.Append("<p class=\"se-empty\">").Append(E(ctx.T("se.none"))).Append("</p>");
            sb.Append("</div>");
        }
        if (show == "templates" || grouped && templates.Count > 0)
        {
            OpenGroup(sb, "templates", grouped ? ctx.T("se.o.templates") : null, templates.Count, counts, cols);
            foreach (var t in templates) TemplateCard(sb, t);
            sb.Append("</div></div>");
        }
        if (show == "components" || grouped && components.Count > 0)
        {
            OpenGroup(sb, "components", grouped ? ctx.T("se.o.components") : null, components.Count, counts, cols);
            foreach (var c in components) ComponentCard(sb, c);
            sb.Append("</div></div>");
        }
        if (tabs || search) sb.Append("<p class=\"se-empty se-nohit\" hidden>").Append(E(ctx.T("se.nothing"))).Append("</p>");

        var more = F("moreUrl");
        if (F("moreText") is { Length: > 0 } mt && (more.StartsWith('/') && !more.StartsWith("//") || more.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            sb.Append("<p class=\"se-more\"><a class=\"btn\" href=\"").Append(E(more)).Append("\">").Append(E(mt)).Append("</a></p>");
        return PluginResult.Fragment(sb.Append("</div></section>").Append(Css).Append(Script).ToString());
    }

    private static void Tab(StringBuilder sb, string key, string label, int count, bool counts, bool on)
    {
        sb.Append("<button type=\"button\" class=\"se-tab\" data-se-tab=\"").Append(key).Append("\" aria-pressed=\"").Append(on ? "true" : "false").Append("\">")
          .Append(E(label));
        if (counts) sb.Append("<span>").Append(N(count)).Append("</span>");
        sb.Append("</button>");
    }

    private static void OpenGroup(StringBuilder sb, string key, string? title, int count, bool counts, string cols)
    {
        sb.Append("<div class=\"se-group\" data-se-group=\"").Append(key).Append("\">");
        if (title is not null)
        {
            sb.Append("<h3 class=\"se-sub\">").Append(E(title));
            if (counts) sb.Append(" <span class=\"se-n\">").Append(N(count)).Append("</span>");
            sb.Append("</h3>");
        }
        sb.Append("<div class=\"se-grid se-cols-").Append(cols).Append("\">");
    }

    /// <summary>A plugin: cover and up to four more pictures, name, publisher, the beginning of its text — and a details view with everything,
    /// drawn hidden inside the card (the script opens it; without the script the card is all there is).</summary>
    private static void PluginCard(StringBuilder sb, IPluginContext ctx, JsonObject p)
    {
        var native = S(p["kind"]) == "native";
        var key = S(p["key"]);
        var name = S(p["name"]);
        var description = S(p["description"]);
        var pics = Pictures(p);
        var kindText = ctx.T(native ? "se.o.kindNative" : "se.o.kindScript");
        var kindClass = native ? "se-native" : "se-script";
        string Src(int i) => ImageRoute + "/" + Uri.EscapeDataString(key) + "/" + N(pics[i].Id);
        string Alt(int i) => pics[i].Caption.Length > 0 ? pics[i].Caption : name;
        void Link(int i, string cls, bool withPicture)
        {
            sb.Append("<a");
            if (cls.Length > 0) sb.Append(" class=\"").Append(cls).Append('"');
            sb.Append(" href=\"").Append(E(Src(i))).Append("\" data-lightbox data-caption=\"").Append(E(pics[i].Caption)).Append('"');
            if (!withPicture) { sb.Append(" hidden></a>"); return; }
            sb.Append("><img src=\"").Append(E(Src(i))).Append("\" alt=\"").Append(E(Alt(i))).Append("\" loading=\"lazy\">");
        }

        sb.Append("<article class=\"se-card se-plugin\" data-se-text=\"").Append(E(SearchText(name, description, S(p["publisher"]), key))).Append('"');
        // The card is its own lightbox group: "next" stays inside this plugin's pictures (the site's gallery.js does the viewing).
        if (pics.Count > 0) sb.Append(" data-lightbox-group=\"se-").Append(E(key)).Append('"');
        sb.Append("><div class=\"se-media\">");
        // From three pictures on, up to four small ones under the cover (the last saying how many more there are); with two, a count on
        // the cover — one lonely thumbnail looked lost. Pictures not shown are links without a picture, so the lightbox still walks
        // through all of them, in the store's order.
        const int Thumbs = 4;
        var thumbs = pics.Count >= 3;
        if (pics.Count > 0)
        {
            Link(0, "se-cover", true);
            if (pics.Count == 2) sb.Append("<span class=\"se-count\">").Append(E(ctx.T("se.pictures", pics.Count))).Append("</span>");
            sb.Append("</a>");
        }
        else sb.Append("<span class=\"se-ph\" aria-hidden=\"true\">").Append(E(Initials(name))).Append("</span>");
        sb.Append("<span class=\"se-badge ").Append(kindClass).Append("\">").Append(E(kindText)).Append("</span></div>");
        if (pics.Count > 1)
        {
            if (thumbs) sb.Append("<div class=\"se-thumbs\">");
            for (var i = 1; i < pics.Count; i++)
            {
                if (!thumbs || i > Thumbs) { Link(i, "", false); continue; }
                Link(i, "", true);
                if (i == Thumbs && pics.Count > Thumbs + 1) sb.Append("<span class=\"se-plus\">+").Append(N(pics.Count - Thumbs - 1)).Append("</span>");
                sb.Append("</a>");
            }
            if (thumbs) sb.Append("</div>");
        }
        sb.Append("<div class=\"se-body\"><h3>").Append(E(name)).Append("</h3>");
        Publisher(sb, ctx, p);
        if (description.Length > 0) sb.Append("<p class=\"se-desc\">").Append(E(description)).Append("</p>");
        sb.Append("<div class=\"se-foot\"><span class=\"se-chip\">").Append(E(ctx.T("se.version", S(p["version"])))).Append("</span>");
        if (Day(S(p["updated"]), ctx.Locale) is { } day) sb.Append("<span>").Append(E(day)).Append("</span>");
        sb.Append("<button type=\"button\" class=\"se-open\" data-se-open hidden>").Append(E(ctx.T("se.details"))).Append(" <span aria-hidden=\"true\">→</span></button></div></div>");

        sb.Append("<div class=\"se-detail\" role=\"dialog\" aria-modal=\"true\" aria-label=\"").Append(E(name)).Append("\" hidden><div class=\"se-dbox\">")
          .Append("<button type=\"button\" class=\"se-x\" data-se-close aria-label=\"").Append(E(ctx.T("se.close"))).Append("\">×</button>")
          .Append("<div class=\"se-dhead\"><span class=\"se-mono ").Append(kindClass).Append("\" aria-hidden=\"true\">").Append(E(Initials(name))).Append("</span><div><h3>")
          .Append(E(name)).Append("</h3>");
        Publisher(sb, ctx, p);
        sb.Append("</div><span class=\"se-badge ").Append(kindClass).Append("\">").Append(E(kindText)).Append("</span></div>");
        if (pics.Count > 0)
        {
            // A strip to scroll through, each picture with its caption. Buttons, not lightbox links: each opens the card's own link of
            // that picture, so every picture is ONE entry in the lightbox.
            sb.Append("<div class=\"se-strip\"><div class=\"se-dpics").Append(pics.Count == 1 ? " se-one" : pics.Count == 2 ? " se-two" : "")
              .Append("\" role=\"group\" aria-label=\"").Append(E(ctx.T("se.pictures", pics.Count))).Append("\">");
            for (var i = 0; i < pics.Count; i++)
            {
                sb.Append("<button type=\"button\" data-se-pic=\"").Append(N(i)).Append("\"><span class=\"se-shot\"><img src=\"").Append(E(Src(i)))
                  .Append("\" alt=\"").Append(E(Alt(i))).Append("\" loading=\"lazy\"></span>");
                if (pics[i].Caption.Length > 0) sb.Append("<span class=\"se-cap\">").Append(E(pics[i].Caption)).Append("</span>");
                sb.Append("</button>");
            }
            sb.Append("</div>");
            if (pics.Count > 2)
                sb.Append("<button type=\"button\" class=\"se-arrow se-prev\" data-se-scroll=\"-1\" aria-label=\"").Append(E(ctx.T("se.prev"))).Append("\" hidden>‹</button>")
                  .Append("<button type=\"button\" class=\"se-arrow se-next\" data-se-scroll=\"1\" aria-label=\"").Append(E(ctx.T("se.next"))).Append("\" hidden>›</button>");
            sb.Append("</div>");
        }
        sb.Append("<div class=\"se-dcols\"><div class=\"se-dtext\">");
        foreach (var para in description.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            sb.Append("<p>").Append(E(para)).Append("</p>");
        sb.Append("</div><dl class=\"se-facts\">");
        Fact(sb, ctx.T("se.fact.version"), S(p["version"]));
        Fact(sb, ctx.T("se.fact.updated"), Day(S(p["updated"]), ctx.Locale) ?? "");
        Fact(sb, ctx.T("se.fact.kind"), native && S(p["api"]) is { Length: > 0 } api ? kindText + " · " + ctx.T("se.fact.api", api) : kindText);
        Fact(sb, ctx.T("se.fact.publisher"), S(p["publisher"]));
        if (pics.Count > 0) Fact(sb, ctx.T("se.fact.pictures"), N(pics.Count));
        sb.Append("</dl></div><p class=\"se-install\">").Append(E(ctx.T("se.install")));
        if (native) sb.Append(' ').Append(E(ctx.T("se.installNative")));
        sb.Append("</p></div></div></article>");
    }

    private static void Publisher(StringBuilder sb, IPluginContext ctx, JsonObject p)
    {
        if (S(p["publisher"]) is not { Length: > 0 } pub) return;
        sb.Append("<div class=\"se-publisher\">").Append(E(ctx.T("se.by"))).Append(' ');
        if (SafeUrl(S(p["publisherUrl"])) is { Length: > 0 } purl)
            sb.Append("<a href=\"").Append(E(purl)).Append("\" rel=\"noopener\" target=\"_blank\">").Append(E(pub)).Append("</a>");
        else sb.Append("<strong>").Append(E(pub)).Append("</strong>");
        sb.Append("</div>");
    }

    private static void Fact(StringBuilder sb, string label, string value)
    {
        if (value.Length == 0) return;
        sb.Append("<div><dt>").Append(E(label)).Append("</dt><dd>").Append(E(value)).Append("</dd></div>");
    }

    /// <summary>A template: a small picture of a page in the template's own colours, button corners and heading font, drawn from VALIDATED
    /// values only (#rrggbb, a number, a font name of letters, digits and spaces) — they go into a style attribute. An older cloud sends only
    /// the accent colour; the picture then takes white and dark grey for the rest.</summary>
    private static void TemplateCard(StringBuilder sb, JsonObject t)
    {
        var name = S(t["name"]);
        var accent = Hex(S(t["accent"])) ?? "#888888";
        var bg = Hex(S(t["background"])) ?? "#ffffff";
        var head = Hex(S(t["heading"])) ?? "#111111";
        var text = Hex(S(t["text"])) ?? head;
        var headerBg = Hex(S(t["headerBg"])) ?? bg;
        var headerText = Hex(S(t["headerText"])) ?? head;
        var alt = Hex(S(t["alt"]));
        var secondary = Hex(S(t["secondary"]));
        var radius = int.TryParse(S(t["buttonRadius"]), NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? Math.Clamp(r, 0, 40) : 4;
        var headingFont = Font(S(t["headingFont"]));
        var bodyFont = Font(S(t["bodyFont"]));
        var style = new StringBuilder()
            .Append("--ta:").Append(accent).Append(";--tb:").Append(bg).Append(";--th:").Append(head).Append(";--tx:").Append(text)
            .Append(";--thb:").Append(headerBg).Append(";--tht:").Append(headerText)
            .Append(";--tl:").Append(alt ?? "color-mix(in srgb," + accent + " 9%,#fff)")
            .Append(";--tr:").Append((radius / 2.0).ToString("0.#", CultureInfo.InvariantCulture)).Append("px");
        if (headingFont.Length > 0) style.Append(";--tf:'").Append(headingFont).Append("',sans-serif");

        sb.Append("<article class=\"se-card se-template\" data-se-text=\"").Append(E(SearchText(name, S(t["description"])))).Append("\">")
          .Append("<div class=\"se-tpl\" style=\"").Append(E(style.ToString())).Append("\" aria-hidden=\"true\">")
          .Append("<div class=\"se-tpl-head\"><i></i><b></b><b></b><b></b></div>")
          .Append("<div class=\"se-tpl-hero\"><strong>").Append(E(name)).Append("</strong><span></span><span></span><p><em></em><em></em></p></div>")
          .Append("<div class=\"se-tpl-band\"><span></span><span></span><span></span></div></div>")
          .Append("<div class=\"se-body\"><h3>").Append(E(name)).Append("</h3>");
        if (S(t["description"]) is { Length: > 0 } d) sb.Append("<p class=\"se-desc\">").Append(E(d)).Append("</p>");
        sb.Append("<div class=\"se-foot se-swatches\">");
        foreach (var c in new[] { accent, secondary, alt, head }.OfType<string>().Distinct())
            sb.Append("<i style=\"background:").Append(c).Append("\"></i>");
        var fonts = new[] { headingFont, bodyFont }.Where(f => f.Length > 0).Distinct().ToList();
        if (fonts.Count > 0) sb.Append("<span>").Append(E(string.Join(" · ", fonts))).Append("</span>");
        sb.Append("</div></div></article>");
    }

    /// <summary>A component: its icon (a Tabler name — the public site loads the Tabler font), name, text and block type.</summary>
    private static void ComponentCard(StringBuilder sb, JsonObject c)
    {
        var name = S(c["name"]);
        var icon = S(c["icon"]).Trim();
        if (icon.StartsWith("ti ", StringComparison.Ordinal)) icon = icon[3..].Trim();
        if (icon.StartsWith("ti-", StringComparison.Ordinal)) icon = icon[3..];
        if (icon.Length is 0 or > 40 || !icon.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) icon = "puzzle";
        sb.Append("<article class=\"se-card se-component\" data-se-text=\"").Append(E(SearchText(name, S(c["description"]), S(c["type"])))).Append("\">")
          .Append("<span class=\"se-cicon\" aria-hidden=\"true\"><i class=\"ti ti-").Append(icon).Append("\"></i></span><div class=\"se-body\"><h3>")
          .Append(E(name)).Append("</h3>");
        if (S(c["description"]) is { Length: > 0 } d) sb.Append("<p class=\"se-desc\">").Append(E(d)).Append("</p>");
        if (S(c["type"]) is { Length: > 0 } type) sb.Append("<code class=\"se-type\">").Append(E(type)).Append("</code>");
        sb.Append("</div></article>");
    }

    /// <summary>What the search box compares with: the visible words, lower case.</summary>
    private static string SearchText(params string[] parts) => string.Join(" ", parts.Where(x => x.Length > 0)).ToLowerInvariant();

    /// <summary>A colour as #rrggbb (lower case), or null — anything else could break out of a style attribute.</summary>
    public static string? Hex(string v) =>
        v.Length == 7 && v[0] == '#' && v.Skip(1).All(Uri.IsHexDigit) ? v.ToLowerInvariant() : null;

    /// <summary>A font name made of letters, digits and spaces only, or "".</summary>
    public static string Font(string v)
    {
        v = v.Trim();
        return v.Length is > 0 and <= 40 && v.All(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ') ? v : "";
    }

    /// <summary>One or two letters for a plugin without a picture: the first letters of its first two words ("Cookie-Banner" → CB).</summary>
    public static string Initials(string name)
    {
        var words = name.Split(new[] { ' ', '-', '(', ')', '_', '.', '/' }, StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToList();
        return words.Count switch
        {
            0 => "?",
            1 => words[0][..1].ToUpperInvariant(),
            _ => (words[0][..1] + words[1][..1]).ToUpperInvariant()
        };
    }

    /// <summary>A catalogue day as the page's language writes it: 20.10.2026 in German, 20 Oct 2026 otherwise (the invariant culture's
    /// month names are English — the image runs with invariant globalization, so "de-DE" would not help).</summary>
    private static string? Day(string updated, string locale) =>
        updated.Length == 10 && DateTime.TryParseExact(updated, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.ToString(locale.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "dd.MM.yyyy" : "d MMM yyyy", CultureInfo.InvariantCulture) : null;

    /// <summary>An address a visitor may be sent to: absolute http(s), nothing else — it comes from the cloud, not from this site.</summary>
    public static string SafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.AbsoluteUri : "";

    public sealed record Picture(int Id, string Caption);

    /// <summary>A catalogue entry's pictures (at most 8, the store's own limit), in the catalogue's order — the first is the cover.</summary>
    public static List<Picture> Pictures(JsonObject p) =>
        (p["images"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(i => i["id"] is JsonValue v && v.TryGetValue<int>(out var id) && id > 0 ? new Picture(id, S(i["caption"])) : null)
            .Where(x => x is not null).Select(x => x!).Take(8).ToList();

    // ---- the pictures, passed through ---------------------------------------------------------------------------------

    private const int MaxPictureBytes = 3 * 1024 * 1024;     // the store's own limit
    private const long MaxCachedBytes = 48L * 1024 * 1024;   // all pictures of a busy store fit; beyond that the oldest go
    private sealed record Pic(byte[] Bytes, string Type, string ETag, DateTime At);
    private static readonly ConcurrentDictionary<string, Pic> Pics = new();

    /// <summary><c>/store-einblick/bild/{key}/{id}</c>: a picture of a plugin the CURRENT catalogue lists with exactly that picture —
    /// key and id go into a request to the cloud, so they are checked against the catalogue, never trusted. Fetched once, then served
    /// from memory (a day for browsers; an id is never reused by the cloud). Only PNG, JPEG or WebP by their first bytes.</summary>
    public static async Task<PluginResult> ImageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var key = ctx.Request.R("key") ?? "";
        if (!int.TryParse(ctx.Request.R("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) return PluginResult.NotFound();
        // per store: the same key and id mean another picture in another catalogue
        var cacheKey = (BaseUrl(ctx) ?? "") + "|" + Source(ctx) + "|" + key + "/" + N(id);
        if (Pics.TryGetValue(cacheKey, out var hit)) return Answer(hit);
        var (data, _) = await CatalogAsync(ctx, false, ct);
        var entry = (data?["plugins"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(p => S(p["key"]) == key);
        if (entry is null || !Pictures(entry).Any(x => x.Id == id) || BaseUrl(ctx) is not { } url) return PluginResult.NotFound();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Agent);
            using var res = await http.GetAsync(url + ImagePath(ctx, key, id),
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > MaxPictureBytes) return PluginResult.NotFound();
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > MaxPictureBytes || Sniff(bytes) is not { } type) return PluginResult.NotFound();
            // A strong validator per content (1.1 sent one constant text for every picture — a raw-string slip).
            var pic = new Pic(bytes, type, "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + "\"", DateTime.UtcNow);
            Pics[cacheKey] = pic;
            // Keep the memory bounded: drop the oldest until it fits.
            while (Pics.Values.Sum(x => (long)x.Bytes.Length) > MaxCachedBytes && Pics.Count > 1)
            {
                var oldest = Pics.OrderBy(x => x.Value.At).First().Key;
                if (oldest == cacheKey) break;
                Pics.TryRemove(oldest, out _);
            }
            return Answer(pic);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ctx.Log("Store-Einblick picture: " + ex.Message);
            return PluginResult.NotFound();
        }
    }

    private static PluginResult Answer(Pic p) =>
        PluginResult.File(p.Bytes, p.Type).WithHeader("Cache-Control", "public, max-age=86400").WithHeader("ETag", p.ETag)
            .WithHeader("X-Content-Type-Options", "nosniff");

    /// <summary>PNG, JPEG or WebP by their first bytes, whatever the other side calls it. Anything else (SVG, HTML) is refused.</summary>
    public static string? Sniff(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        return null;
    }

    // ---- look and behaviour ------------------------------------------------------------------------------------------

    // Theme-able: the site's own variables stand behind everything (accent, line, muted, ink, the alternate background, the heading font).
    // The details view is moved to <body> when it opens (an ancestor with a transform would trap position:fixed), so its rules are not
    // scoped to .se-store. z-index 990: above the site's header, below the site's lightbox (1000), which opens over it.
    private const string Css = """
<style>
.se-store [hidden]{display:none!important}
.se-store .se-intro{color:var(--muted,#6b7280);max-width:720px}
.se-store .se-counts{color:var(--muted,#6b7280);font-size:14px;margin:4px 0 18px}
.se-store .se-bar{display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:12px;margin:14px 0 4px}
.se-store .se-tabs{display:flex;flex-wrap:wrap;gap:8px;min-width:0;max-width:100%}
.se-store .se-tab{display:inline-flex;align-items:center;gap:8px;font:inherit;font-size:14px;font-weight:600;line-height:1;padding:9px 14px;border-radius:999px;border:1px solid var(--line,#e5e7eb);background:var(--bg,#fff);color:var(--ink,#111827);cursor:pointer;transition:background .15s,border-color .15s,color .15s}
.se-store .se-tab:hover{border-color:var(--accent,#2563eb)}
.se-store .se-tab span{font-size:12px;padding:3px 7px;border-radius:999px;background:var(--bg-alt,#f3f4f6);color:var(--muted,#6b7280)}
.se-store .se-tab[aria-pressed=true]{background:var(--accent,#2563eb);border-color:var(--accent,#2563eb);color:#fff}
.se-store .se-tab[aria-pressed=true] span{background:rgba(255,255,255,.22);color:#fff}
.se-store .se-search{font:inherit;font-size:15px;width:min(320px,100%);padding:10px 16px 10px 40px;border:1px solid var(--line,#e5e7eb);border-radius:999px;color:var(--ink,#111827);background:var(--bg,#fff) url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='16' height='16' fill='none' stroke='%236b7280' stroke-width='2' stroke-linecap='round' viewBox='0 0 24 24'%3E%3Ccircle cx='11' cy='11' r='7'/%3E%3Cpath d='m20 20-3.5-3.5'/%3E%3C/svg%3E") no-repeat 15px 50%}
.se-store .se-search:focus{outline:none;border-color:var(--accent,#2563eb);box-shadow:0 0 0 3px color-mix(in srgb,var(--accent,#2563eb) 18%,transparent)}
.se-store .se-sub{display:flex;align-items:center;gap:10px;margin:36px 0 16px;font-size:22px}
.se-store .se-n{font-size:13px;font-weight:700;line-height:1;padding:5px 10px;border-radius:999px;background:var(--bg-alt,#f3f4f6);color:var(--muted,#6b7280)}
.se-store .se-grid{display:grid;gap:22px}
.se-store .se-cols-2{grid-template-columns:repeat(2,minmax(0,1fr))}
.se-store .se-cols-3{grid-template-columns:repeat(3,minmax(0,1fr))}
.se-store .se-cols-4{grid-template-columns:repeat(4,minmax(0,1fr))}
.se-store .se-card{position:relative;display:flex;flex-direction:column;border:1px solid var(--line,#e5e7eb);border-radius:16px;background:var(--bg,#fff);overflow:hidden;transition:box-shadow .2s ease,border-color .2s ease}
.se-store .se-card:hover{border-color:color-mix(in srgb,var(--accent,#2563eb) 35%,var(--line,#e5e7eb));box-shadow:0 18px 40px -20px rgba(15,23,42,.35)}
.se-store .se-media{position:relative;aspect-ratio:16/10;padding:20px 20px 0;overflow:hidden;background:radial-gradient(120% 90% at 0 0,color-mix(in srgb,var(--accent,#2563eb) 20%,var(--bg,#fff)) 0,transparent 60%),linear-gradient(160deg,color-mix(in srgb,var(--accent,#2563eb) 9%,var(--bg,#fff)),var(--bg-alt,#f3f4f6))}
.se-store .se-cover{position:relative;z-index:1;display:block;height:100%;border-radius:10px 10px 0 0;overflow:hidden;background:#fff;box-shadow:0 12px 32px -10px rgba(15,23,42,.4),0 0 0 1px rgba(15,23,42,.07)}
.se-store .se-cover img{width:100%;height:100%;object-fit:cover;object-position:top center;display:block;transition:transform .4s ease}
.se-store .se-card:hover .se-cover img{transform:scale(1.035)}
.se-store .se-ph{display:flex;height:100%;align-items:center;justify-content:center;padding-bottom:20px;font-family:var(--font-head,inherit);font-size:72px;font-weight:800;line-height:1;color:color-mix(in srgb,var(--accent,#2563eb) 45%,var(--bg,#fff))}
:is(.se-store,.se-detail) .se-badge{display:inline-block;flex:0 0 auto;font-size:12px;font-weight:700;line-height:1;padding:6px 10px;border-radius:999px;white-space:nowrap}
:is(.se-store,.se-detail) .se-script{background:color-mix(in srgb,var(--accent,#2563eb) 13%,var(--bg,#fff));color:var(--accent,#2563eb)}
:is(.se-store,.se-detail) .se-native{background:var(--ink,#111827);color:#fff}
.se-store .se-media .se-badge{position:absolute;z-index:2;top:12px;right:12px;box-shadow:0 4px 12px rgba(15,23,42,.18)}
.se-store .se-media .se-script{background:#fff}
.se-store .se-count{position:absolute;left:10px;bottom:10px;font-size:12px;font-weight:700;line-height:1;padding:6px 10px;border-radius:999px;background:rgba(15,23,42,.78);color:#fff}
.se-store .se-thumbs{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:8px;padding:12px 20px 0}
.se-store .se-thumbs a{position:relative;z-index:1;display:block;aspect-ratio:16/10;border-radius:7px;overflow:hidden;border:1px solid var(--line,#e5e7eb);background:var(--bg-alt,#f3f4f6)}
.se-store .se-thumbs img{width:100%;height:100%;object-fit:cover;object-position:top center;display:block;transition:opacity .15s}
.se-store .se-thumbs a:hover img{opacity:.8}
.se-store .se-plus{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;background:rgba(15,23,42,.62);color:#fff;font-weight:700;font-size:15px}
.se-store .se-body{display:flex;flex-direction:column;gap:6px;flex:1;padding:16px 20px 18px}
.se-store .se-body h3{margin:0;font-size:19px;line-height:1.25}
:is(.se-store,.se-detail) .se-publisher{font-size:13.5px;color:var(--muted,#6b7280)}
:is(.se-store,.se-detail) .se-publisher :is(a,strong){position:relative;z-index:1;color:var(--ink,#111827);font-weight:600;text-decoration:none}
:is(.se-store,.se-detail) .se-publisher a:hover{text-decoration:underline;text-underline-offset:2px}
.se-store .se-desc{margin:2px 0 0;color:var(--muted,#4b5563);font-size:15px;line-height:1.55;display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:3;overflow:hidden}
.se-store .se-foot{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin-top:auto;padding-top:12px;font-size:13px;color:var(--muted,#6b7280)}
.se-store .se-chip{padding:4px 9px;border-radius:999px;background:var(--bg-alt,#f3f4f6);font-weight:600;line-height:1.2;color:var(--ink,#374151)}
.se-store .se-open{margin-left:auto;display:inline-flex;align-items:center;gap:5px;padding:0;border:0;background:none;font:inherit;font-size:14px;font-weight:700;color:var(--accent,#2563eb);cursor:pointer}
.se-store .se-open::after{content:"";position:absolute;inset:0}
.se-store .se-open:hover{text-decoration:underline;text-underline-offset:3px}
.se-store .se-open:focus-visible{outline:none}
.se-store .se-card:has(.se-open:focus-visible){outline:2px solid var(--accent,#2563eb);outline-offset:2px}
.se-store .se-tpl{display:flex;flex-direction:column;aspect-ratio:16/10;overflow:hidden;background:var(--tb);border-bottom:1px solid var(--line,#e5e7eb)}
.se-store .se-tpl-head{display:flex;align-items:center;gap:8px;padding:10px 16px;background:var(--thb);box-shadow:0 1px 0 rgba(15,23,42,.08)}
.se-store .se-tpl-head i{width:18px;height:18px;border-radius:5px;background:var(--ta);margin-right:auto}
.se-store .se-tpl-head b{width:26px;height:5px;border-radius:3px;background:var(--tht);opacity:.5}
.se-store .se-tpl-hero{flex:1;display:flex;flex-direction:column;justify-content:center;gap:8px;padding:12px 22px;min-height:0}
.se-store .se-tpl-hero strong{font-family:var(--tf,var(--font-head,inherit));font-size:26px;line-height:1.05;font-weight:800;color:var(--th);letter-spacing:-.01em;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.se-store .se-tpl-hero span{display:block;height:6px;width:70%;border-radius:3px;background:var(--tx);opacity:.2}
.se-store .se-tpl-hero span+span{width:48%}
.se-store .se-tpl-hero p{display:flex;gap:8px;margin:4px 0 0}
.se-store .se-tpl-hero em{width:64px;height:20px;border-radius:var(--tr);background:var(--ta)}
.se-store .se-tpl-hero em+em{background:transparent;box-shadow:inset 0 0 0 2px var(--ta)}
.se-store .se-tpl-band{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px;padding:12px 22px 14px;background:var(--tl)}
.se-store .se-tpl-band span{height:30px;border-radius:calc(var(--tr) + 3px);background:var(--tb);box-shadow:inset 0 3px 0 var(--ta),0 1px 2px rgba(15,23,42,.08)}
.se-store .se-swatches{gap:0}
.se-store .se-swatches i{width:20px;height:20px;border-radius:50%;border:2px solid var(--bg,#fff);margin-left:-7px;box-shadow:0 0 0 1px rgba(15,23,42,.12)}
.se-store .se-swatches i:first-child{margin-left:0}
.se-store .se-swatches span{margin-left:auto;font-size:12.5px}
.se-store .se-component{flex-direction:row;align-items:flex-start;gap:16px;padding:20px}
.se-store .se-component .se-body{padding:0;gap:5px}
.se-store .se-cicon{flex:0 0 auto;width:52px;height:52px;border-radius:14px;display:flex;align-items:center;justify-content:center;font-size:26px;color:var(--accent,#2563eb);background:color-mix(in srgb,var(--accent,#2563eb) 12%,var(--bg,#fff))}
.se-store .se-type{align-self:flex-start;margin-top:auto;padding:3px 8px;border-radius:6px;background:var(--bg-alt,#f3f4f6);font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:12px;color:var(--muted,#6b7280)}
.se-store .se-empty{color:var(--muted,#6b7280)}
.se-store .se-nohit{margin-top:24px}
.se-store .se-more{margin-top:28px}
html.se-lock{overflow:hidden}
.se-detail{position:fixed;inset:0;z-index:990;display:flex;align-items:flex-start;justify-content:center;padding:6vh 20px;overflow-y:auto;overscroll-behavior:contain;background:rgba(15,23,42,.6);-webkit-backdrop-filter:blur(4px);backdrop-filter:blur(4px)}
.se-detail[hidden]{display:none}
.se-detail .se-dbox{position:relative;width:min(1000px,100%);margin:auto 0;padding:30px 32px 32px;border-radius:20px;background:var(--bg,#fff);color:var(--ink,#111827);box-shadow:0 40px 90px -30px rgba(0,0,0,.55)}
.se-detail .se-x{position:sticky;top:14px;z-index:3;float:right;margin:-14px -16px 0 14px;width:40px;height:40px;display:flex;align-items:center;justify-content:center;border:0;border-radius:50%;background:var(--bg-alt,#f3f4f6);color:var(--ink,#111827);font-size:24px;line-height:1;cursor:pointer;box-shadow:0 2px 10px rgba(15,23,42,.12)}
.se-detail .se-x:hover{background:var(--line,#e5e7eb)}
.se-detail .se-dhead{display:flex;align-items:center;gap:16px}
.se-detail .se-dhead>div{flex:1;min-width:0}
.se-detail .se-dhead h3{margin:0 0 2px;font-size:26px;line-height:1.2}
.se-detail .se-mono{flex:0 0 auto;width:56px;height:56px;border-radius:16px;display:flex;align-items:center;justify-content:center;font-family:var(--font-head,inherit);font-size:22px;font-weight:800;line-height:1}
.se-detail .se-strip{position:relative;margin:24px -32px 24px}
.se-detail .se-dpics{display:grid;grid-auto-flow:column;grid-auto-columns:44%;gap:16px;padding:0 32px 10px;overflow-x:auto;scroll-snap-type:x mandatory;scroll-padding:0 32px;overscroll-behavior-x:contain;scrollbar-width:thin}
.se-detail .se-dpics.se-one{grid-auto-columns:100%}
.se-detail .se-dpics.se-two{grid-auto-columns:calc(50% - 8px)}
.se-detail .se-dpics button{display:flex;flex-direction:column;gap:8px;min-width:0;padding:0;border:0;background:none;color:inherit;font:inherit;text-align:left;cursor:zoom-in;scroll-snap-align:start}
.se-detail .se-shot{display:block;aspect-ratio:16/10;border:1px solid var(--line,#e5e7eb);border-radius:12px;overflow:hidden;background:var(--bg-alt,#f3f4f6)}
.se-detail .se-dpics img{display:block;width:100%;height:100%;object-fit:cover;object-position:top center;transition:transform .3s ease}
.se-detail .se-dpics button:hover img{transform:scale(1.025)}
.se-detail .se-cap{font-size:13.5px;line-height:1.4;color:var(--muted,#6b7280)}
.se-detail .se-arrow{position:absolute;top:calc(50% - 34px);z-index:2;width:44px;height:44px;display:flex;align-items:center;justify-content:center;border:0;border-radius:50%;background:var(--bg,#fff);color:var(--ink,#111827);font-size:28px;line-height:1;cursor:pointer;box-shadow:0 4px 16px rgba(15,23,42,.25)}
.se-detail .se-arrow:hover{color:var(--accent,#2563eb)}
.se-detail .se-arrow[hidden]{display:none}
.se-detail .se-prev{left:14px}
.se-detail .se-next{right:14px}
.se-detail .se-dcols{display:grid;grid-template-columns:minmax(0,1.7fr) minmax(0,1fr);gap:30px;align-items:start}
.se-detail .se-dtext p{margin:0 0 12px;font-size:16px;line-height:1.7}
.se-detail .se-facts{margin:0;padding:4px 18px;border:1px solid var(--line,#e5e7eb);border-radius:14px;background:var(--bg-alt,#f6f7f9);font-size:14px}
.se-detail .se-facts div{display:flex;justify-content:space-between;gap:14px;padding:11px 0;border-top:1px solid var(--line,#e5e7eb)}
.se-detail .se-facts div:first-child{border-top:0}
.se-detail .se-facts dt{color:var(--muted,#6b7280)}
.se-detail .se-facts dd{margin:0;font-weight:600;text-align:right}
.se-detail .se-install{margin:22px 0 0;padding:14px 18px;border-radius:12px;font-size:14.5px;line-height:1.55;background:color-mix(in srgb,var(--accent,#2563eb) 8%,var(--bg,#fff));border:1px solid color-mix(in srgb,var(--accent,#2563eb) 22%,var(--bg,#fff))}
@media (max-width:900px){.se-store .se-cols-3,.se-store .se-cols-4{grid-template-columns:repeat(2,minmax(0,1fr))}}
@media (max-width:600px){.se-store .se-grid{grid-template-columns:minmax(0,1fr)}.se-store .se-bar{flex-direction:column;align-items:stretch}.se-store .se-search{width:100%}.se-store .se-tabs{flex-wrap:nowrap;overflow-x:auto;scrollbar-width:none}.se-store .se-tab{flex:0 0 auto}}
@media (hover:none){.se-detail .se-arrow{display:none!important}}
@media (max-width:760px){.se-detail{padding:0}.se-detail .se-dbox{min-height:100%;border-radius:0;padding:22px 18px 28px}.se-detail .se-x{margin:-8px -6px 0 12px}.se-detail .se-dcols{grid-template-columns:minmax(0,1fr)}.se-detail .se-strip{margin:20px -18px}.se-detail .se-dpics{grid-auto-columns:82%;gap:12px;padding:0 18px 8px;scroll-padding:0 18px}.se-detail .se-dpics.se-one{grid-auto-columns:100%}.se-detail .se-dhead{gap:12px}.se-detail .se-dhead h3{font-size:21px}.se-detail .se-mono{width:46px;height:46px;border-radius:13px;font-size:18px}}
</style>
""";

    // Once per page however many blocks there are. Filtering hides cards (and a group with none left); the details view is the card's own
    // hidden part, moved to <body> on opening. A picture button there clicks the card's lightbox link of that picture, so the site's
    // lightbox (gallery.js, bound at load) opens over the details view. Escape closes the lightbox first: this listener runs in the
    // capture phase, before gallery.js's, and leaves the key alone while a lightbox is open.
    private const string Script = """
<script>
(function(){
if(window.seStore)return;window.seStore=1;
var openP=null,back=null;
function close(){if(!openP)return;openP.hidden=true;document.documentElement.classList.remove('se-lock');openP=null;if(back&&back.focus)back.focus();}
function open(card,btn){var p=card.sePanel||(card.sePanel=card.querySelector('.se-detail'));if(!p)return;p.seCard=card;if(p.parentNode!==document.body)document.body.appendChild(p);
p.hidden=false;document.documentElement.classList.add('se-lock');openP=p;back=btn;var x=p.querySelector('.se-x');if(x)x.focus({preventScroll:true});
var st=p.querySelector('.se-dpics');if(st&&!st.seBound){st.seBound=1;st.addEventListener('scroll',function(){arrows(p);},{passive:true});}arrows(p);}
function arrows(p){var st=p.querySelector('.se-dpics');if(!st)return;var a=p.querySelector('.se-prev'),b=p.querySelector('.se-next');
if(a)a.hidden=st.scrollLeft<=4;if(b)b.hidden=st.scrollLeft+st.clientWidth>=st.scrollWidth-4;}
document.addEventListener('click',function(e){var t=e.target;if(!t||!t.closest)return;
var o=t.closest('[data-se-open]');if(o){e.preventDefault();open(o.closest('.se-card'),o);return;}
if(!openP)return;
if(t===openP||t.closest('[data-se-close]')){close();return;}
var sc=t.closest('[data-se-scroll]');if(sc){var st=openP.querySelector('.se-dpics');if(st)st.scrollBy({left:+sc.getAttribute('data-se-scroll')*st.clientWidth*0.85,behavior:'smooth'});return;}
var b=t.closest('[data-se-pic]');if(b&&openP.seCard){var a=openP.seCard.querySelectorAll('a[data-lightbox]')[+b.getAttribute('data-se-pic')];if(a)a.click();}});
document.addEventListener('keydown',function(e){if(e.key==='Escape'&&openP&&!document.querySelector('.lightbox.open')){e.preventDefault();close();}},true);
function init(){document.querySelectorAll('[data-se-store]').forEach(function(root){
root.querySelectorAll('[data-se-open]').forEach(function(b){b.hidden=false;});
var bar=root.querySelector('.se-bar');if(!bar)return;bar.hidden=false;
var tab='',q='',none=root.querySelector('.se-nohit'),search=bar.querySelector('.se-search');
function apply(){var n=0;root.querySelectorAll('[data-se-group]').forEach(function(g){var k=g.getAttribute('data-se-group'),v=0;
g.querySelectorAll('[data-se-text]').forEach(function(c){var ok=(!tab||tab===k)&&(!q||c.getAttribute('data-se-text').indexOf(q)>=0);c.hidden=!ok;if(ok)v++;});
g.hidden=!v;var c=g.querySelector('.se-n');if(c)c.textContent=v;n+=v;});if(none)none.hidden=n>0;}
bar.querySelectorAll('[data-se-tab]').forEach(function(b){b.addEventListener('click',function(){tab=b.getAttribute('data-se-tab');
bar.querySelectorAll('[data-se-tab]').forEach(function(x){x.setAttribute('aria-pressed',x===b?'true':'false');});apply();});});
if(search)search.addEventListener('input',function(){q=search.value.trim().toLowerCase();apply();});});}
if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',init);else init();
})();
</script>
""";

    // ---- the back office ----------------------------------------------------------------------------------------------

    public static async Task<PluginResult> StatusAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (data, error) = await CatalogAsync(ctx, ctx.Request.Q("refresh") == "1", ct);
        var count = (string key) => (data?[key] as JsonArray)?.Count ?? 0;
        var natives = (data?["plugins"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Count(p => S(p["kind"]) == "native");
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(ctx.T("se.status.sub"), ctx.T("se.nav"),
                Ui.Button(ctx.T("se.status.refresh"), "?refresh=1", UiButtonKind.Ghost, "ti-refresh", small: true)),
            data is null ? Ui.Alert(error ?? ctx.T("se.err.fetch"), UiTone.Warning) : Ui.Alert(ctx.T("se.status.ok", BaseUrl(ctx) ?? ""), UiTone.Success),
            data is null ? null : Ui.Stats(
                Ui.Stat(ctx.T("se.o.plugins"), count("plugins").ToString(CultureInfo.InvariantCulture), ctx.T("se.status.natives", natives)),
                Ui.Stat(ctx.T("se.o.templates"), count("templates").ToString(CultureInfo.InvariantCulture)),
                Ui.Stat(ctx.T("se.o.components"), count("components").ToString(CultureInfo.InvariantCulture))),
            Ui.Muted(ctx.T("se.status.how"))), ctx.T("se.nav"));
    }
}

/// <summary>The plugin's texts.</summary>
public static class SeText
{
    public static readonly Dictionary<string, string> De = new()
    {
        ["se.nav"] = "Store-Einblick", ["se.tab.status"] = "Status",
        ["se.set.url"] = "Adresse der Cloud", ["se.set.urlHelp"] = "Die MatCMS-Cloud, deren Store gezeigt wird, z. B. https://cloud2.matthix.de. Gelesen wird ihr öffentlicher Katalog – ohne Schlüssel.",
        ["se.set.source"] = "Quelle", ["se.set.sourceHelp"] = "Der Store einer MatCMS-Cloud oder der offizielle MatCMS Store (dann als Adresse https://matcms.de).", ["se.o.srcCloud"] = "Store einer Cloud", ["se.o.srcMatcms"] = "MatCMS Store (matcms.de)",
        ["se.set.cache"] = "Zwischenspeichern (Minuten)", ["se.set.cacheHelp"] = "So lange zeigt die Website den zuletzt geholten Katalog, bevor sie neu fragt.",
        ["se.block.name"] = "Store-Einblick", ["se.block.desc"] = "Zeigt die Plugins, Templates und Komponenten aus dem Store einer MatCMS-Cloud.",
        ["se.f.heading"] = "Überschrift", ["se.f.intro"] = "Einleitung", ["se.f.show"] = "Was zeigen", ["se.f.kind"] = "Art der Plugins",
        ["se.f.limit"] = "Höchstens", ["se.f.limitHelp"] = "Leer = alle.", ["se.f.columns"] = "Spalten", ["se.f.counts"] = "Anzahl zeigen",
        ["se.f.moreText"] = "Text des Knopfs darunter", ["se.f.moreUrl"] = "Ziel des Knopfs",
        ["se.o.plugins"] = "Plugins", ["se.o.templates"] = "Templates", ["se.o.components"] = "Komponenten", ["se.o.all"] = "Alles",
        ["se.o.kindAll"] = "Alle", ["se.o.kindScript"] = "Skript", ["se.o.kindNative"] = "Native", ["se.o.yes"] = "Ja", ["se.o.no"] = "Nein",
        ["se.count.plugins"] = "{0} Plugins", ["se.count.templates"] = "{0} Templates", ["se.count.components"] = "{0} Komponenten",
        ["se.version"] = "Version {0}", ["se.none"] = "Keine Einträge.", ["se.by"] = "von", ["se.pictures"] = "{0} Bilder",
        ["se.tabAll"] = "Alle", ["se.search"] = "Im Store suchen …", ["se.nothing"] = "Nichts gefunden.",
        ["se.details"] = "Details", ["se.close"] = "Schließen", ["se.prev"] = "Vorige Bilder", ["se.next"] = "Weitere Bilder",
        ["se.fact.version"] = "Version", ["se.fact.updated"] = "Aktualisiert", ["se.fact.kind"] = "Art", ["se.fact.api"] = "Plugin-API {0}",
        ["se.fact.publisher"] = "Herausgeber", ["se.fact.pictures"] = "Bilder",
        ["se.install"] = "So kommt es auf Ihre Website: im Admin unter Plugins → „Weiter durchsuchen …“ → „Installieren“.",
        ["se.installNative"] = "Ein natives Plugin läuft nur, wo „Native Plugins erlauben“ eingeschaltet ist.",
        ["se.unavailable"] = "Der Store ist gerade nicht erreichbar.",
        ["se.err.url"] = "Keine gültige Adresse der Cloud eingestellt (Einstellungen).", ["se.err.fetch"] = "Der Store-Katalog konnte nicht geladen werden.",
        ["se.status.sub"] = "Woher der Block seine Einträge holt.", ["se.status.ok"] = "Katalog geladen von {0}.", ["se.status.refresh"] = "Neu laden",
        ["se.status.natives"] = "davon {0} Native",
        ["se.status.how"] = "Den Block „Store-Einblick“ auf eine Seite setzen. Er liest den öffentlichen Katalog der Cloud (/api/public/store) und hält ihn für die eingestellte Zeit im Speicher.",
    };

    public static readonly Dictionary<string, string> En = new()
    {
        ["se.nav"] = "Store showcase", ["se.tab.status"] = "Status",
        ["se.set.source"] = "Source", ["se.set.sourceHelp"] = "The store of a MatCMS cloud or the official MatCMS Store (then the address is https://matcms.de).", ["se.o.srcCloud"] = "A cloud's store", ["se.o.srcMatcms"] = "MatCMS Store (matcms.de)",
        ["se.set.url"] = "Cloud address", ["se.set.urlHelp"] = "The MatCMS cloud whose store is shown, e.g. https://cloud2.matthix.de. Its public catalogue is read – no key needed.",
        ["se.set.cache"] = "Keep for (minutes)", ["se.set.cacheHelp"] = "How long the site shows the catalogue it fetched last before it asks again.",
        ["se.block.name"] = "Store showcase", ["se.block.desc"] = "Shows the plugins, templates and components of a MatCMS cloud's store.",
        ["se.f.heading"] = "Heading", ["se.f.intro"] = "Introduction", ["se.f.show"] = "What to show", ["se.f.kind"] = "Kind of plugins",
        ["se.f.limit"] = "At most", ["se.f.limitHelp"] = "Empty = all.", ["se.f.columns"] = "Columns", ["se.f.counts"] = "Show counts",
        ["se.f.moreText"] = "Text of the button below", ["se.f.moreUrl"] = "Button link",
        ["se.o.plugins"] = "Plugins", ["se.o.templates"] = "Templates", ["se.o.components"] = "Components", ["se.o.all"] = "Everything",
        ["se.o.kindAll"] = "All", ["se.o.kindScript"] = "Script", ["se.o.kindNative"] = "Native", ["se.o.yes"] = "Yes", ["se.o.no"] = "No",
        ["se.count.plugins"] = "{0} plugins", ["se.count.templates"] = "{0} templates", ["se.count.components"] = "{0} components",
        ["se.version"] = "Version {0}", ["se.none"] = "No entries.", ["se.by"] = "by", ["se.pictures"] = "{0} pictures",
        ["se.tabAll"] = "All", ["se.search"] = "Search the store …", ["se.nothing"] = "Nothing found.",
        ["se.details"] = "Details", ["se.close"] = "Close", ["se.prev"] = "Previous pictures", ["se.next"] = "More pictures",
        ["se.fact.version"] = "Version", ["se.fact.updated"] = "Updated", ["se.fact.kind"] = "Kind", ["se.fact.api"] = "plugin API {0}",
        ["se.fact.publisher"] = "Publisher", ["se.fact.pictures"] = "Pictures",
        ["se.install"] = "To put it on your website: in the admin, Plugins → “Browse further …” → “Install”.",
        ["se.installNative"] = "A native plugin only runs where “Allow native plugins” is switched on.",
        ["se.unavailable"] = "The store cannot be reached at the moment.",
        ["se.err.url"] = "No valid cloud address set (settings).", ["se.err.fetch"] = "The store catalogue could not be loaded.",
        ["se.status.sub"] = "Where the block gets its entries.", ["se.status.ok"] = "Catalogue loaded from {0}.", ["se.status.refresh"] = "Reload",
        ["se.status.natives"] = "{0} of them native",
        ["se.status.how"] = "Put the block “Store showcase” on a page. It reads the cloud's public catalogue (/api/public/store) and keeps it in memory for the time set.",
    };
}
