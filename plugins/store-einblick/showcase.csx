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

/// <summary>The plugin: a block that shows the plugin store of a MatCMS cloud — plugins (with their kind: script or native), templates and
/// components — read from the cloud's public catalogue (<c>GET {cloud}/api/public/store</c>, no key needed), kept for a while in memory.</summary>
public static class SeShowcase
{
    public const string BlockType = "store_einblick";

    public static void Configure(IPluginBuilder b)
    {
        b.Strings("de", SeText.De);
        b.Strings("en", SeText.En);

        b.Settings
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
    }

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
        if (!fresh && Cache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(minutes)) return (hit.Data, null);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Store-Einblick/1.0");
            using var res = await http.GetAsync(url + "/api/public/store", ct);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException("HTTP " + (int)res.StatusCode);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (body.Length > 2_000_000 || JsonNode.Parse(body) is not JsonObject data) throw new HttpRequestException("keine gültige Antwort");
            Cache[url] = new Cached(DateTime.UtcNow, data);
            return (data, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ctx.Log("Store-Einblick: " + url + " – " + ex.Message);
            return Cache.TryGetValue(url, out var stale) ? (stale.Data, null) : (null, ctx.T("se.err.fetch"));
        }
    }

    /// <summary>The cloud's address from the settings: only http(s), no path, no trailing slash.</summary>
    public static string? BaseUrl(IPluginContext ctx)
    {
        var raw = (ctx.Settings.Get("storeUrl") ?? "").Trim().TrimEnd('/');
        return Uri.TryCreate(raw, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) && string.IsNullOrEmpty(u.Query)
            ? u.GetLeftPart(UriPartial.Authority) : null;
    }

    private static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static string E(string s) => WebUtility.HtmlEncode(s);

    // ---- the block --------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> RenderAsync(IPluginContext ctx, CancellationToken ct)
    {
        var block = ctx.Block;
        string F(string id) => block?.Get(id) ?? "";
        var show = F("show") is { Length: > 0 } sv ? sv : "plugins";
        var kind = F("kind");
        var limit = int.TryParse(F("limit"), out var l) && l > 0 ? l : int.MaxValue;
        var cols = F("columns") is "2" or "4" ? F("columns") : "3";

        var (data, error) = await CatalogAsync(ctx, false, ct);
        var sb = new StringBuilder("<section class=\"section se-store\"><div class=\"container\">");
        if (F("heading") is { Length: > 0 } h) sb.Append("<h2>").Append(E(h)).Append("</h2>");
        if (F("intro") is { Length: > 0 } intro) sb.Append("<p class=\"se-intro\">").Append(E(intro)).Append("</p>");
        if (data is null)
        {
            // A visitor sees a quiet line; the editor (preview) sees why.
            sb.Append("<p class=\"se-empty\">").Append(E(ctx.IsEditorPreview ? error ?? "" : ctx.T("se.unavailable"))).Append("</p>");
            return PluginResult.Fragment(sb.Append("</div></section>").Append(Css).ToString());
        }

        var plugins = (data["plugins"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(p => kind.Length == 0 || S(p["kind"]) == kind).ToList();
        var templates = (data["templates"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var components = (data["components"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();

        if (F("counts") != "no")
        {
            var parts = new List<string>();
            if (show is "plugins" or "all") parts.Add(ctx.T("se.count.plugins", plugins.Count));
            if (show is "templates" or "all") parts.Add(ctx.T("se.count.templates", templates.Count));
            if (show is "components" or "all") parts.Add(ctx.T("se.count.components", components.Count));
            sb.Append("<p class=\"se-counts\">").Append(E(string.Join(" · ", parts))).Append("</p>");
        }

        if (show is "plugins" or "all")
        {
            if (show == "all") sb.Append("<h3 class=\"se-sub\">").Append(E(ctx.T("se.o.plugins"))).Append("</h3>");
            sb.Append($"<div class=\"se-grid se-cols-{cols}\">");
            foreach (var p in plugins.Take(limit))
            {
                var native = S(p["kind"]) == "native";
                sb.Append("<article class=\"se-card\"><header><h3>").Append(E(S(p["name"]))).Append("</h3>")
                  .Append($"<span class=\"se-badge {(native ? "se-native" : "se-script")}\">").Append(E(ctx.T(native ? "se.o.kindNative" : "se.o.kindScript"))).Append("</span></header>");
                if (S(p["description"]) is { Length: > 0 } d) sb.Append("<p>").Append(E(Cut(d, 260))).Append("</p>");
                sb.Append("<footer>").Append(E(ctx.T("se.version", S(p["version"]))));
                if (S(p["updated"]) is { Length: 10 } up && DateTime.TryParse(up, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    sb.Append(" · ").Append(E(day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)));
                sb.Append("</footer></article>");
            }
            if (plugins.Count == 0) sb.Append("<p class=\"se-empty\">").Append(E(ctx.T("se.none"))).Append("</p>");
            sb.Append("</div>");
        }
        if (show is "templates" or "all")
        {
            if (show == "all") sb.Append("<h3 class=\"se-sub\">").Append(E(ctx.T("se.o.templates"))).Append("</h3>");
            sb.Append($"<div class=\"se-grid se-cols-{cols}\">");
            foreach (var t in templates.Take(limit))
            {
                var accent = System.Text.RegularExpressions.Regex.IsMatch(S(t["accent"]), "^#[0-9a-fA-F]{6}$") ? S(t["accent"]) : "#888888";
                sb.Append("<article class=\"se-card\"><header><h3>").Append($"<i class=\"se-dot\" style=\"background:{accent}\"></i>").Append(E(S(t["name"]))).Append("</h3></header>");
                if (S(t["description"]) is { Length: > 0 } d) sb.Append("<p>").Append(E(Cut(d, 220))).Append("</p>");
                sb.Append("</article>");
            }
            sb.Append("</div>");
        }
        if (show is "components" or "all")
        {
            if (show == "all") sb.Append("<h3 class=\"se-sub\">").Append(E(ctx.T("se.o.components"))).Append("</h3>");
            sb.Append($"<div class=\"se-grid se-cols-{cols}\">");
            foreach (var c in components.Take(limit))
            {
                sb.Append("<article class=\"se-card\"><header><h3>").Append(E(S(c["name"]))).Append("</h3></header>");
                if (S(c["description"]) is { Length: > 0 } d) sb.Append("<p>").Append(E(Cut(d, 220))).Append("</p>");
                sb.Append("</article>");
            }
            sb.Append("</div>");
        }
        var more = F("moreUrl");
        if (F("moreText") is { Length: > 0 } mt && (more.StartsWith('/') && !more.StartsWith("//") || more.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            sb.Append("<p class=\"se-more\"><a class=\"btn\" href=\"").Append(E(more)).Append("\">").Append(E(mt)).Append("</a></p>");
        return PluginResult.Fragment(sb.Append("</div></section>").Append(Css).ToString());
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + " …";

    // Theme-able: the site's own variables stand behind everything (accent, line, muted, the alternate background).
    private const string Css = "<style>.se-store .se-intro{color:var(--muted,#6b7280);max-width:720px}.se-store .se-counts{color:var(--muted,#6b7280);font-size:14px;margin:4px 0 18px}"
        + ".se-store .se-sub{margin:28px 0 12px}.se-store .se-grid{display:grid;gap:18px}.se-store .se-cols-2{grid-template-columns:repeat(2,minmax(0,1fr))}"
        + ".se-store .se-cols-3{grid-template-columns:repeat(3,minmax(0,1fr))}.se-store .se-cols-4{grid-template-columns:repeat(4,minmax(0,1fr))}"
        + ".se-store .se-card{display:flex;flex-direction:column;gap:8px;padding:20px;border:1px solid var(--line,#e5e7eb);border-radius:14px;background:var(--bg,#fff)}"
        + ".se-store .se-card header{display:flex;align-items:flex-start;justify-content:space-between;gap:10px}.se-store .se-card h3{margin:0;font-size:18px;display:flex;align-items:center;gap:8px}"
        + ".se-store .se-card p{margin:0;color:var(--muted,#4b5563);font-size:15px;line-height:1.5}.se-store .se-card footer{margin-top:auto;font-size:13px;color:var(--muted,#6b7280)}"
        + ".se-store .se-badge{flex:0 0 auto;font-size:12px;font-weight:600;padding:3px 9px;border-radius:999px;white-space:nowrap}"
        + ".se-store .se-script{background:color-mix(in srgb,var(--accent,#2563eb) 14%,transparent);color:var(--accent,#2563eb)}"
        + ".se-store .se-native{background:var(--ink,#111827);color:#fff}.se-store .se-dot{width:14px;height:14px;border-radius:50%;display:inline-block;flex:0 0 auto}"
        + ".se-store .se-empty{color:var(--muted,#6b7280)}.se-store .se-more{margin-top:22px}"
        + "@media (max-width:900px){.se-store .se-cols-3,.se-store .se-cols-4{grid-template-columns:repeat(2,minmax(0,1fr))}}"
        + "@media (max-width:600px){.se-store .se-grid{grid-template-columns:minmax(0,1fr)}}</style>";

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
        ["se.set.cache"] = "Zwischenspeichern (Minuten)", ["se.set.cacheHelp"] = "So lange zeigt die Website den zuletzt geholten Katalog, bevor sie neu fragt.",
        ["se.block.name"] = "Store-Einblick", ["se.block.desc"] = "Zeigt die Plugins, Templates und Komponenten aus dem Store einer MatCMS-Cloud.",
        ["se.f.heading"] = "Überschrift", ["se.f.intro"] = "Einleitung", ["se.f.show"] = "Was zeigen", ["se.f.kind"] = "Art der Plugins",
        ["se.f.limit"] = "Höchstens", ["se.f.limitHelp"] = "Leer = alle.", ["se.f.columns"] = "Spalten", ["se.f.counts"] = "Anzahl zeigen",
        ["se.f.moreText"] = "Text des Knopfs darunter", ["se.f.moreUrl"] = "Ziel des Knopfs",
        ["se.o.plugins"] = "Plugins", ["se.o.templates"] = "Templates", ["se.o.components"] = "Komponenten", ["se.o.all"] = "Alles",
        ["se.o.kindAll"] = "Alle", ["se.o.kindScript"] = "Skript", ["se.o.kindNative"] = "Native", ["se.o.yes"] = "Ja", ["se.o.no"] = "Nein",
        ["se.count.plugins"] = "{0} Plugins", ["se.count.templates"] = "{0} Templates", ["se.count.components"] = "{0} Komponenten",
        ["se.version"] = "Version {0}", ["se.none"] = "Keine Einträge.",
        ["se.unavailable"] = "Der Store ist gerade nicht erreichbar.",
        ["se.err.url"] = "Keine gültige Adresse der Cloud eingestellt (Einstellungen).", ["se.err.fetch"] = "Der Store-Katalog konnte nicht geladen werden.",
        ["se.status.sub"] = "Woher der Block seine Einträge holt.", ["se.status.ok"] = "Katalog geladen von {0}.", ["se.status.refresh"] = "Neu laden",
        ["se.status.natives"] = "davon {0} Native",
        ["se.status.how"] = "Den Block „Store-Einblick“ auf eine Seite setzen. Er liest den öffentlichen Katalog der Cloud (/api/public/store) und hält ihn für die eingestellte Zeit im Speicher.",
    };

    public static readonly Dictionary<string, string> En = new()
    {
        ["se.nav"] = "Store showcase", ["se.tab.status"] = "Status",
        ["se.set.url"] = "Cloud address", ["se.set.urlHelp"] = "The MatCMS cloud whose store is shown, e.g. https://cloud2.matthix.de. Its public catalogue is read – no key needed.",
        ["se.set.cache"] = "Keep for (minutes)", ["se.set.cacheHelp"] = "How long the site shows the catalogue it fetched last before it asks again.",
        ["se.block.name"] = "Store showcase", ["se.block.desc"] = "Shows the plugins, templates and components of a MatCMS cloud's store.",
        ["se.f.heading"] = "Heading", ["se.f.intro"] = "Introduction", ["se.f.show"] = "What to show", ["se.f.kind"] = "Kind of plugins",
        ["se.f.limit"] = "At most", ["se.f.limitHelp"] = "Empty = all.", ["se.f.columns"] = "Columns", ["se.f.counts"] = "Show counts",
        ["se.f.moreText"] = "Text of the button below", ["se.f.moreUrl"] = "Button link",
        ["se.o.plugins"] = "Plugins", ["se.o.templates"] = "Templates", ["se.o.components"] = "Components", ["se.o.all"] = "Everything",
        ["se.o.kindAll"] = "All", ["se.o.kindScript"] = "Script", ["se.o.kindNative"] = "Native", ["se.o.yes"] = "Yes", ["se.o.no"] = "No",
        ["se.count.plugins"] = "{0} plugins", ["se.count.templates"] = "{0} templates", ["se.count.components"] = "{0} components",
        ["se.version"] = "Version {0}", ["se.none"] = "No entries.",
        ["se.unavailable"] = "The store cannot be reached at the moment.",
        ["se.err.url"] = "No valid cloud address set (settings).", ["se.err.fetch"] = "The store catalogue could not be loaded.",
        ["se.status.sub"] = "Where the block gets its entries.", ["se.status.ok"] = "Catalogue loaded from {0}.", ["se.status.refresh"] = "Reload",
        ["se.status.natives"] = "{0} of them native",
        ["se.status.how"] = "Put the block “Store showcase” on a page. It reads the cloud's public catalogue (/api/public/store) and keeps it in memory for the time set.",
    };
}
