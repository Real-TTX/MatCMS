// Thix-Store — the ThixLauncher's store on thixapps.de.
//   /apps                  every app as a tile (the apps, then ThixOffice), the launcher first
//   /apps/{short}          one app: description, features, pictures, the current version; installed with the ThixLauncher,
//                          the app's own packages folded away under "Weitere Downloads"
//   /apps/{short}/logo     its logo, /apps/{short}/bild/{n} its n-th picture, /apps/launcher/logo the launcher's
//   blocks thix_apps (the tiles) and thix_hero (the head of the home page with the drawn launcher window)
// The catalog is the launcher's own (docs/catalog.json on the main branch of Real-TTX/ThixLauncher, written from AppCatalog.cs), the
// versions and packages come from each app's release manifest. Both are kept in memory for a while and, when GitHub cannot be reached,
// used however old they are. Pictures go through THIS site: a visitor talks to GitHub only when they download something.
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

public sealed record TsShot(string Url, string Caption);
public sealed record TsApp(string Id, string Short, string Name, string Tagline, string Description, string Manifest, string Icon, string Project, bool Cloud,
    List<TsShot> Shots, TsExtra Extra);
public sealed record TsCatalog(string LauncherManifest, string LauncherIcon, string LauncherProject, List<TsApp> Apps);

/// <summary>A build in the release channel: its version and where its packages are (absolute GitHub addresses, null = none for that system).</summary>
public sealed record TsRelease(string Version, string? Windows, string? MacArm, string? MacIntel);

public static class TsStore
{
    private const string Agent = "Thix-Store/1.0 (thixapps.de)";
    public const string DefaultCatalog = "https://raw.githubusercontent.com/Real-TTX/ThixLauncher/main/docs/catalog.json";
    private const string LauncherChannel = "https://github.com/Real-TTX/ThixLauncher/releases/download/release-channel/";
    private const string LauncherAll = "https://github.com/Real-TTX/ThixLauncher/releases";

    // Without the launcher's manifest the downloads still work: the rolling release always has these files.
    private static readonly TsRelease LauncherFallback = new("",
        LauncherChannel + "ThixLauncherSetup.exe", LauncherChannel + "ThixLauncher-macOS-arm64.dmg", LauncherChannel + "ThixLauncher-macOS-x64.dmg");

    public static void Configure(IPluginBuilder b)
    {
        b.Strings("de", TsText.Rows.ToDictionary(r => r.Key, r => r.De));
        b.Strings("en", TsText.Rows.ToDictionary(r => r.Key, r => r.En));

        b.Settings
            .Text("catalogUrl", "ts.set.catalog", DefaultCatalog, "ts.set.catalogHelp")
            .Number("cacheMinutes", "ts.set.cache", 30, 1, 1440, "ts.set.cacheHelp");

        var options = new PublicRouteOptions { Methods = PluginMethod.Get, DefaultLanguageOnly = true };
        b.Route("/apps", OverviewAsync, options);
        b.Route("/apps/{name}", AppAsync, options);
        b.Route("/apps/{name}/logo", LogoAsync, options);
        b.Route("/apps/{name}/bild/{n}", PictureAsync, options);

        b.Block(new BlockSpec("thix_apps", "ts.block.apps", "ts.block.appsDesc")
        {
            Category = "design",
            IconSvg = "<rect x=\"3\" y=\"3\" width=\"7\" height=\"7\" rx=\"2\"/><rect x=\"14\" y=\"3\" width=\"7\" height=\"7\" rx=\"2\"/><rect x=\"3\" y=\"14\" width=\"7\" height=\"7\" rx=\"2\"/><rect x=\"14\" y=\"14\" width=\"7\" height=\"7\" rx=\"2\"/>",
            Fields = new[]
            {
                new FieldSpec("kicker", "ts.f.kicker"),
                new FieldSpec("heading", "ts.f.heading") { Help = "ts.f.headingHelp" },
                new FieldSpec("intro", "ts.f.intro", FieldKind.Textarea),
                new FieldSpec("show", "ts.f.show", FieldKind.Select)
                {
                    Default = "all",
                    Options = new[] { new FieldOption("all", "ts.o.all"), new FieldOption("apps", "ts.o.apps"), new FieldOption("office", "ts.o.office") }
                },
            }
        }, AppsBlockAsync);

        b.Block(new BlockSpec("thix_hero", "ts.block.hero", "ts.block.heroDesc")
        {
            Category = "design",
            IconSvg = "<rect x=\"2\" y=\"4\" width=\"20\" height=\"16\" rx=\"2\"/><path d=\"M2 8h20\"/><rect x=\"12\" y=\"11\" width=\"3\" height=\"3\"/><rect x=\"17\" y=\"11\" width=\"3\" height=\"3\"/>",
            Fields = new[]
            {
                new FieldSpec("kicker", "ts.f.kicker"),
                new FieldSpec("heading", "ts.f.heading"),
                new FieldSpec("lead", "ts.f.lead", FieldKind.Textarea) { Help = "ts.f.leadHelp" },
            }
        }, HeroBlockAsync);

        b.Sitemap(async (ctx, ct) =>
        {
            var (catalog, _) = await CatalogAsync(ctx, ct);
            var entries = new List<SitemapEntry> { new("/apps") };
            if (catalog is not null) entries.AddRange(catalog.Apps.Select(a => new SitemapEntry("/apps/" + a.Short)));
            return entries;
        });

        b.Admin.Section("ts.nav", "ti-apps").Page("status", "ts.tab.status", StatusAsync);
    }

    // ---- catalog and releases -------------------------------------------------------------------------------------------

    private sealed record Cached<T>(DateTime At, T Value);
    private static readonly ConcurrentDictionary<string, Cached<TsCatalog>> Catalogs = new();
    private static readonly ConcurrentDictionary<string, Cached<TsRelease>> Releases = new();
    private static readonly ConcurrentDictionary<string, Task> Refreshing = new();

    private static TimeSpan MaxAge(IPluginContext ctx) =>
        TimeSpan.FromMinutes(double.TryParse(ctx.Settings.Get("cacheMinutes"), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? Math.Clamp(m, 1, 1440) : 30);

    /// <summary>The catalog address from the settings: GitHub only (raw.githubusercontent.com or github.com), else the default.</summary>
    private static string CatalogUrl(IPluginContext ctx) =>
        Trusted((ctx.Settings.Get("catalogUrl") ?? "").Trim()) is { } url ? url : DefaultCatalog;

    /// <summary>The catalog: fresh from memory, else fetched; a failed fetch keeps the last copy however old (a shop window that goes
    /// blank because GitHub hiccups is worse than one an hour old).</summary>
    public static async Task<(TsCatalog? Catalog, string? Error)> CatalogAsync(IPluginContext ctx, CancellationToken ct, bool fresh = false)
    {
        var url = CatalogUrl(ctx);
        if (!fresh && Catalogs.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < MaxAge(ctx)) return (hit.Value, null);
        try
        {
            var body = await GetTextAsync(url, ct);
            var catalog = Parse(JsonNode.Parse(body) as JsonObject ?? throw new HttpRequestException("kein JSON-Objekt"));
            Catalogs[url] = new Cached<TsCatalog>(DateTime.UtcNow, catalog);
            return (catalog, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ctx.Log("Thix-Store: Katalog " + url + " – " + ex.Message);
            return Catalogs.TryGetValue(url, out var stale) ? (stale.Value, null) : (null, ex.Message);
        }
    }

    /// <summary>The catalog as this plugin uses it: only entries with a valid short name, only GitHub addresses, in the order of
    /// <see cref="TsApps"/> (apps it does not know after them, by name).</summary>
    private static TsCatalog Parse(JsonObject root)
    {
        var launcher = root["launcher"] as JsonObject ?? new JsonObject();
        var order = TsApps.All.Select((x, i) => (x.Short, i)).ToDictionary(x => x.Short, x => x.i);
        var apps = new List<TsApp>();
        foreach (var node in (root["apps"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var shortName = S(node["shortName"]);
            if (shortName.Length is 0 or > 30 || !shortName.All(c => c is >= 'a' and <= 'z') || apps.Any(a => a.Short == shortName)) continue;
            var shots = (node["screenshots"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(s => Trusted(S(s["url"])) is { } u ? new TsShot(u, S(s["caption"])) : null)
                .Where(s => s is not null).Select(s => s!).Take(12).ToList();
            var extra = TsApps.All.FirstOrDefault(x => x.Short == shortName) ?? new TsExtra(shortName, "#2F6FED", false, false, null, Array.Empty<TsFeature>(), null);
            apps.Add(new TsApp(S(node["id"]), shortName, S(node["name"]), S(node["tagline"]), S(node["description"]),
                Trusted(S(node["manifest"])) ?? "", Trusted(S(node["icon"])) ?? "", Trusted(S(node["project"])) ?? "", node["cloud"] is JsonValue v && v.TryGetValue<bool>(out var c) && c,
                shots, extra));
        }
        apps = apps.OrderBy(a => order.TryGetValue(a.Short, out var i) ? i : int.MaxValue).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new TsCatalog(Trusted(S(launcher["manifest"])) ?? "", Trusted(S(launcher["icon"])) ?? "", Trusted(S(launcher["project"])) ?? LauncherAll, apps);
    }

    /// <summary>The release of a manifest from memory; stale or missing ones are fetched in the background for the next visitor, except
    /// when there is none at all yet — then this visitor waits (a few seconds at most, see <see cref="GetTextAsync"/>).</summary>
    public static async Task<TsRelease?> ReleaseAsync(IPluginContext ctx, string manifest, CancellationToken ct)
    {
        if (manifest.Length == 0) return null;
        var maxAge = MaxAge(ctx);
        if (Releases.TryGetValue(manifest, out var hit))
        {
            // in the background: no request context there (it may be gone when the fetch ends), only the numbers it needs
            if (DateTime.UtcNow - hit.At >= maxAge) _ = Refreshing.GetOrAdd(manifest, m => Task.Run(() => FetchReleaseAsync(m, maxAge, null)));
            return hit.Value;
        }
        await Refreshing.GetOrAdd(manifest, m => FetchReleaseAsync(m, maxAge, ctx.Log)).WaitAsync(ct);
        return Releases.TryGetValue(manifest, out var got) ? got.Value : null;
    }

    /// <summary>Every release of the catalog at once (the overview and the blocks need all of them; one slow manifest does not queue the others).</summary>
    public static async Task<Dictionary<string, TsRelease?>> ReleasesAsync(IPluginContext ctx, TsCatalog catalog, CancellationToken ct)
    {
        var manifests = catalog.Apps.Select(a => a.Manifest).Append(catalog.LauncherManifest).Where(m => m.Length > 0).Distinct().ToList();
        var results = await Task.WhenAll(manifests.Select(async m => (m, await ReleaseAsync(ctx, m, ct))));
        return results.ToDictionary(x => x.m, x => x.Item2);
    }

    private static async Task FetchReleaseAsync(string manifest, TimeSpan maxAge, Action<string>? log)
    {
        try
        {
            var root = JsonNode.Parse(await GetTextAsync(manifest, CancellationToken.None)) as JsonObject ?? throw new HttpRequestException("kein JSON-Objekt");
            var folder = manifest[..(manifest.LastIndexOf('/') + 1)];
            var packages = root["packages"] as JsonObject ?? new JsonObject();
            string? File(string platform, bool diskImage)
            {
                var name = S((packages[platform] as JsonObject)?["file"]);
                if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.StartsWith('.')) return null;
                // The manifest names the update package (.zip) of a Mac; next to it lies the disk image people download.
                if (diskImage && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name = name[..^4] + ".dmg";
                return folder + Uri.EscapeDataString(name);
            }
            Releases[manifest] = new Cached<TsRelease>(DateTime.UtcNow,
                new TsRelease(S(root["version"]), File("win-x64", false), File("osx-arm64", true), File("osx-x64", true)));
        }
        catch (Exception ex)
        {
            log?.Invoke("Thix-Store: Manifest " + manifest + " – " + ex.Message);
            // keep the old one, but do not ask again for a few minutes
            if (Releases.TryGetValue(manifest, out var old)) Releases[manifest] = old with { At = DateTime.UtcNow - maxAge + TimeSpan.FromMinutes(Math.Min(5, maxAge.TotalMinutes)) };
        }
        finally
        {
            Refreshing.TryRemove(manifest, out _);
        }
    }

    private static async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Agent);
        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException("HTTP " + (int)res.StatusCode);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (body.Length > 1_000_000) throw new HttpRequestException("zu groß");
        return body;
    }

    /// <summary>An address on GitHub (https, github.com or raw.githubusercontent.com, no user info, no port), else null: the catalog
    /// decides what this server fetches and where visitors are sent, so nothing else gets through.</summary>
    public static string? Trusted(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo.Length == 0
        && (u.Host == "github.com" || u.Host == "raw.githubusercontent.com") ? u.AbsoluteUri : null;

    // ---- pages --------------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> OverviewAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (catalog, _) = await CatalogAsync(ctx, ct);
        if (catalog is null) return Unavailable(ctx);
        var releases = await ReleasesAsync(ctx, catalog, ct);
        var launcher = releases.GetValueOrDefault(catalog.LauncherManifest) ?? LauncherFallback;
        var sb = new StringBuilder(Css);
        sb.Append("<section class=\"ta-apphero ta-apphero--plain\"><div class=\"ta-wrap ta-apphero__in\">")
          .Append("<img class=\"ta-apphero__icon\" src=\"/apps/launcher/logo\" alt=\"").Append(E(ctx.T("ts.launcherLogo"))).Append("\" width=\"112\" height=\"112\"><div>")
          .Append("<p class=\"ta-kicker\">").Append(E(ctx.T("ts.kicker"))).Append("</p>")
          .Append("<h1>").Append(E(ctx.T("ts.all.title"))).Append("</h1>")
          .Append("<p class=\"ta-lead\">").Append(E(ctx.T("ts.all.lead", catalog.Apps.Count))).Append("</p>");
        LauncherButtons(sb, ctx, launcher, "");
        sb.Append("</div></div></section>");
        Grid(sb, ctx, catalog, "all", null, null, null);
        Band(sb, ctx, launcher);
        return PluginResult.Page(sb.ToString(), ctx.T("ts.all.pageTitle"), ctx.T("ts.all.description", catalog.Apps.Count));
    }

    private static async Task<PluginResult> AppAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (catalog, _) = await CatalogAsync(ctx, ct);
        if (catalog is null) return Unavailable(ctx);
        var name = ctx.Request.R("name");
        var index = catalog.Apps.FindIndex(a => a.Short == name);
        if (index < 0) return PluginResult.NotFound();
        var app = catalog.Apps[index];
        var releases = await ReleasesAsync(ctx, catalog, ct);
        var launcher = releases.GetValueOrDefault(catalog.LauncherManifest) ?? LauncherFallback;
        var release = releases.GetValueOrDefault(app.Manifest);
        var x = app.Extra;
        var color = Hex(x.Color) ?? "#2F6FED";

        var sb = new StringBuilder(Css);
        // the head: logo, name, what it is, install with the launcher
        sb.Append("<section class=\"ta-apphero\" style=\"--app:").Append(color).Append("\"><div class=\"ta-wrap ta-apphero__in\">")
          .Append("<img class=\"ta-apphero__icon\" src=\"/apps/").Append(app.Short).Append("/logo\" alt=\"").Append(E(ctx.T("ts.logo", app.Name))).Append("\" width=\"112\" height=\"112\"><div>")
          .Append("<p class=\"ta-kicker\"><a href=\"/apps\">").Append(E(ctx.T("ts.kicker"))).Append("</a>");
        if (x.Office) sb.Append(" · ThixOffice");
        sb.Append("</p><h1>").Append(E(app.Name)).Append("</h1>")
          .Append("<p class=\"ta-apphero__tag\">").Append(E(app.Tagline)).Append("</p>");
        foreach (var para in Paragraphs(x.Lead ?? app.Description)) sb.Append("<p class=\"ta-lead\">").Append(E(para)).Append("</p>");
        sb.Append("<p class=\"ts-cta\"><a class=\"ta-btn ta-btn--primary\" href=\"#installieren\"><i class=\"ti ti-download\"></i><span><b>")
          .Append(E(ctx.T("ts.install"))).Append("</b><small>").Append(E(ctx.T("ts.systems"))).Append("</small></span></a></p>")
          .Append("<p class=\"ts-facts\">");
        if (release is { Version.Length: > 0 }) sb.Append("<span class=\"ts-chip\">").Append(E(ctx.T("ts.version", release.Version))).Append("</span>");
        sb.Append("<span><i class=\"ti ti-brand-windows\"></i> Windows</span><span><i class=\"ti ti-brand-apple\"></i> macOS</span>");
        if (app.Cloud) sb.Append("<span><i class=\"ti ti-cloud\"></i> ").Append(E(ctx.T("ts.cloud"))).Append("</span>");
        sb.Append("</p></div></div></section>");

        // the pictures and what it can do
        sb.Append("<section class=\"ta-section\" style=\"--app:").Append(color).Append("\"><div class=\"ta-wrap\" data-lightbox-group=\"ts-").Append(app.Short).Append("\">");
        if (app.Shots.Count > 0) Shot(sb, app, 0, "ta-shot ta-shot--wide");
        if (x.Features.Length > 0)
        {
            sb.Append("<div class=\"ta-features\">");
            foreach (var f in x.Features)
                sb.Append("<div class=\"ta-feature\"><i class=\"ti ").Append(Icon(f.Icon)).Append("\"></i><h3>").Append(E(f.Title)).Append("</h3><p>").Append(E(f.Text)).Append("</p></div>");
            sb.Append("</div>");
        }
        if (app.Shots.Count > 1)
        {
            sb.Append("<div class=\"ts-gallery").Append(app.Shots.Count == 2 ? " ts-gallery--one" : "").Append("\">");
            for (var i = 1; i < app.Shots.Count; i++) Shot(sb, app, i, "ta-shot");
            sb.Append("</div>");
        }
        if (x.Note is { Length: > 0 } note) sb.Append("<p class=\"ta-note\"><i class=\"ti ti-info-circle\"></i> ").Append(note).Append("</p>");
        sb.Append("</div></section>");

        // installing: the launcher first, the app's own packages folded away
        sb.Append("<section class=\"ta-section ta-section--alt ts-install\" id=\"installieren\" style=\"--app:").Append(color).Append("\"><div class=\"ta-wrap\">")
          .Append("<p class=\"ta-kicker\">").Append(E(ctx.T("ts.installKicker"))).Append("</p>")
          .Append("<h2>").Append(E(ctx.T("ts.installTitle", app.Name))).Append("</h2>")
          .Append("<ol class=\"ts-steps\"><li>").Append(E(ctx.T("ts.step1"))).Append("</li><li>").Append(E(ctx.T("ts.step2", app.Name)))
          .Append("</li><li>").Append(E(ctx.T("ts.step3", app.Name))).Append("</li></ol>");
        LauncherButtons(sb, ctx, launcher, "");
        sb.Append("<details class=\"ts-more\"><summary><i class=\"ti ti-chevron-right\" aria-hidden=\"true\"></i>").Append(E(ctx.T("ts.more", app.Name))).Append("</summary><div class=\"ts-more__in\">")
          .Append("<p>").Append(E(ctx.T("ts.moreText", app.Name))).Append("</p>");
        if (release is not null && (release.Windows ?? release.MacArm ?? release.MacIntel) is not null)
        {
            sb.Append("<ul class=\"ts-files\">");
            FileLink(sb, release.Windows, "ti-brand-windows", ctx.T("ts.win"));
            FileLink(sb, release.MacArm, "ti-brand-apple", ctx.T("ts.macArm"));
            FileLink(sb, release.MacIntel, "ti-brand-apple", ctx.T("ts.macIntel"));
            sb.Append("</ul>");
            if (release.Version.Length > 0) sb.Append("<p class=\"ts-small\">").Append(E(ctx.T("ts.version", release.Version))).Append(" · ");
            else sb.Append("<p class=\"ts-small\">");
        }
        else sb.Append("<p class=\"ts-small\">").Append(E(ctx.T("ts.noPackages"))).Append(' ');
        if (app.Project.Length > 0)
            sb.Append("<a href=\"").Append(E(app.Project)).Append("/releases\" rel=\"noopener\">").Append(E(ctx.T("ts.allVersions"))).Append("</a>");
        sb.Append("</p></div></details></div></section>");

        // to the neighbours
        var prev = catalog.Apps[(index + catalog.Apps.Count - 1) % catalog.Apps.Count];
        var next = catalog.Apps[(index + 1) % catalog.Apps.Count];
        sb.Append("<section class=\"ts-pagerband\"><div class=\"ta-wrap\"><nav class=\"ta-pager\" aria-label=\"").Append(E(ctx.T("ts.moreApps"))).Append("\">")
          .Append("<a href=\"/apps/").Append(prev.Short).Append("\"><i class=\"ti ti-arrow-left\"></i>").Append(E(prev.Name)).Append("</a>")
          .Append("<a href=\"/apps\">").Append(E(ctx.T("ts.allApps"))).Append("</a>")
          .Append("<a href=\"/apps/").Append(next.Short).Append("\">").Append(E(next.Name)).Append("<i class=\"ti ti-arrow-right\"></i></a></nav></div></section>");

        var description = (x.Lead ?? app.Description) is { Length: > 0 } d ? (d.Length > 300 ? d[..297].TrimEnd() + "…" : d) : app.Tagline;
        return PluginResult.Page(sb.ToString(), app.Name + " – " + app.Tagline, description);
    }

    private static PluginResult Unavailable(IPluginContext ctx) =>
        PluginResult.Page(Css + "<section class=\"ta-section\"><div class=\"ta-wrap ta-wrap--narrow\"><h1>" + E(ctx.T("ts.all.title")) + "</h1><p class=\"ta-lead\">"
            + E(ctx.T("ts.unavailable")) + "</p><p><a class=\"ta-btn ta-btn--primary\" href=\"/download\"><i class=\"ti ti-download\"></i><span><b>"
            + E(ctx.T("ts.getLauncher")) + "</b></span></a></p></div></section>", ctx.T("ts.all.pageTitle")).WithStatus(503);

    // ---- blocks -------------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> AppsBlockAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (catalog, error) = await CatalogAsync(ctx, ct);
        if (catalog is null)
            return PluginResult.Fragment("<section class=\"ta-section\" id=\"apps\"><div class=\"ta-wrap\"><p>" + E(ctx.IsEditorPreview ? error ?? "" : ctx.T("ts.unavailable")) + "</p></div></section>");
        string F(string id) => ctx.Block?.Get(id) ?? "";
        var show = F("show") is "apps" or "office" ? F("show") : "all";
        var sb = new StringBuilder(Css);
        Grid(sb, ctx, catalog, show, F("kicker"), F("heading"), F("intro"));
        return PluginResult.Fragment(sb.ToString());
    }

    private static async Task<PluginResult> HeroBlockAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (catalog, _) = await CatalogAsync(ctx, ct);
        var launcher = catalog is null ? LauncherFallback : (await ReleaseAsync(ctx, catalog.LauncherManifest, ct)) ?? LauncherFallback;
        string F(string id, string fallback) => ctx.Block?.Get(id) is { Length: > 0 } v ? v : ctx.T(fallback);
        var sb = new StringBuilder(Css);
        sb.Append("<section class=\"ta-hero\"><div class=\"ta-wrap ta-hero__in\"><div class=\"ta-hero__text\">")
          .Append("<p class=\"ta-kicker\">").Append(E(F("kicker", "ts.hero.kicker"))).Append("</p>")
          .Append("<h1>").Append(E(F("heading", "ts.hero.title"))).Append("</h1>")
          .Append("<p class=\"ta-lead\">").Append(Bold(E(F("lead", "ts.hero.lead")))).Append("</p>");
        LauncherButtons(sb, ctx, launcher, "");
        sb.Append("</div><div class=\"ta-hero__pic\">");
        if (catalog is not null) Window(sb, ctx, catalog);
        sb.Append("</div></div></section>");
        return PluginResult.Fragment(sb.ToString());
    }

    // ---- pieces -------------------------------------------------------------------------------------------------------

    /// <summary>The tiles: the apps, then ThixOffice under its own heading (id "thixoffice", the menu links there).</summary>
    private static void Grid(StringBuilder sb, IPluginContext ctx, TsCatalog catalog, string show, string? kicker, string? heading, string? intro)
    {
        var apps = catalog.Apps.Where(a => !a.Extra.Office).ToList();
        var office = catalog.Apps.Where(a => a.Extra.Office).ToList();
        sb.Append("<section class=\"ta-section\" id=\"apps\"><div class=\"ta-wrap\">");
        if (kicker is { Length: > 0 }) sb.Append("<p class=\"ta-kicker\">").Append(E(kicker)).Append("</p>");
        if (heading is { Length: > 0 }) sb.Append("<h2>").Append(E(heading.Replace("{n}", catalog.Apps.Count.ToString(CultureInfo.InvariantCulture)))).Append("</h2>");
        if (intro is { Length: > 0 }) sb.Append("<p class=\"ta-intro\">").Append(E(intro)).Append("</p>");
        if (show != "office" && apps.Count > 0)
        {
            if (show == "all" && heading is null) sb.Append("<h2 class=\"ts-group\">").Append(E(ctx.T("ts.group.apps"))).Append("</h2>");
            sb.Append("<div class=\"ta-grid\">");
            foreach (var a in apps) Card(sb, ctx, a);
            sb.Append("</div>");
        }
        if (show != "apps" && office.Count > 0)
        {
            sb.Append("<h3 class=\"ta-sub\" id=\"thixoffice\">ThixOffice <span>").Append(E(ctx.T("ts.group.office"))).Append("</span></h3><div class=\"ta-grid ta-grid--3\">");
            foreach (var a in office) Card(sb, ctx, a);
            sb.Append("</div>");
        }
        sb.Append("</div></section>");
    }

    private static void Card(StringBuilder sb, IPluginContext ctx, TsApp a)
    {
        sb.Append("<a class=\"ta-card\" href=\"/apps/").Append(a.Short).Append("\" style=\"--app:").Append(Hex(a.Extra.Color) ?? "#2F6FED").Append("\">")
          .Append("<img class=\"ta-card__icon\" src=\"/apps/").Append(a.Short).Append("/logo\" alt=\"\" width=\"64\" height=\"64\" loading=\"lazy\">")
          .Append("<span class=\"ta-card__name\">").Append(E(a.Name));
        if (a.Extra.New) sb.Append(" <em>").Append(E(ctx.T("ts.new"))).Append("</em>");
        sb.Append("</span><span class=\"ta-card__tag\">").Append(E(a.Tagline)).Append("</span>")
          .Append("<span class=\"ta-card__more\">").Append(E(ctx.T("ts.details"))).Append(" <i class=\"ti ti-arrow-right\"></i></span></a>");
    }

    /// <summary>The launcher's downloads: Windows and Apple Silicon as buttons, Intel and the download page as a line under them.</summary>
    private static void LauncherButtons(StringBuilder sb, IPluginContext ctx, TsRelease launcher, string cls)
    {
        var win = launcher.Windows ?? LauncherFallback.Windows!;
        var arm = launcher.MacArm ?? LauncherFallback.MacArm!;
        var intel = launcher.MacIntel ?? LauncherFallback.MacIntel!;
        sb.Append("<div class=\"ta-dl ").Append(cls).Append("\">")
          .Append("<a class=\"ta-btn ta-btn--primary\" href=\"").Append(E(win)).Append("\"><i class=\"ti ti-brand-windows\"></i><span><b>").Append(E(ctx.T("ts.forWindows")))
          .Append("</b><small>").Append(E(ctx.T("ts.launcherFor", "Windows"))).Append("</small></span></a>")
          .Append("<a class=\"ta-btn ta-btn--dark\" href=\"").Append(E(arm)).Append("\"><i class=\"ti ti-brand-apple\"></i><span><b>").Append(E(ctx.T("ts.forMac")))
          .Append("</b><small>").Append(E(ctx.T("ts.appleSilicon"))).Append("</small></span></a></div>")
          .Append("<p class=\"ta-dl-more\">").Append(E(ctx.T("ts.intelQ"))).Append(" <a href=\"").Append(E(intel)).Append("\">").Append(E(ctx.T("ts.macIntelShort"))).Append("</a> · <a href=\"/download\">")
          .Append(E(ctx.T("ts.allDownloads"))).Append("</a>");
        if (launcher.Version.Length > 0) sb.Append(" · ThixLauncher ").Append(E(launcher.Version));
        sb.Append("</p>");
    }

    private static void Band(StringBuilder sb, IPluginContext ctx, TsRelease launcher)
    {
        sb.Append("<section class=\"ta-band\" id=\"download\"><div class=\"ta-wrap ta-band__in\"><div><h2>").Append(E(ctx.T("ts.band.title")))
          .Append("</h2><p>").Append(E(ctx.T("ts.band.text"))).Append("</p></div>");
        LauncherButtons(sb, ctx, launcher, "ta-dl--light");
        sb.Append("</div></section>");
    }

    /// <summary>The launcher window of the home page, drawn: every app of the catalog as a tile with its real logo.</summary>
    private static void Window(StringBuilder sb, IPluginContext ctx, TsCatalog catalog)
    {
        sb.Append("<div class=\"ta-win\" aria-hidden=\"true\"><div class=\"ta-win__bar\"><span class=\"ta-win__logo\"><img src=\"/apps/launcher/logo\" alt=\"\"></span><span>ThixLauncher</span><i></i><i></i><i></i></div>")
          .Append("<div class=\"ta-win__body\"><div class=\"ta-win__side\"><span class=\"is-on\"><i class=\"ti ti-layout-grid\"></i>Apps</span><span><i class=\"ti ti-circle-check\"></i>")
          .Append(E(ctx.T("ts.win.installed"))).Append("</span><span><i class=\"ti ti-download\"></i>Updates</span><span><i class=\"ti ti-settings\"></i>").Append(E(ctx.T("ts.win.settings")))
          .Append("</span></div><div class=\"ta-win__grid ts-wingrid\">");
        for (var i = 0; i < catalog.Apps.Count; i++)
        {
            var a = catalog.Apps[i];
            var get = i % 3 == 1;
            sb.Append("<div class=\"ta-win__app\" style=\"--d:").Append((i * 0.05).ToString("0.00", CultureInfo.InvariantCulture)).Append("s\"><img src=\"/apps/").Append(a.Short)
              .Append("/logo\" alt=\"\"><b>").Append(E(a.Name)).Append("</b><span class=\"").Append(get ? "is-get" : "is-open").Append("\">")
              .Append(E(ctx.T(get ? "ts.win.install" : "ts.win.open"))).Append("</span></div>");
        }
        sb.Append("</div></div></div>");
    }

    private static void Shot(StringBuilder sb, TsApp app, int i, string cls)
    {
        var src = "/apps/" + app.Short + "/bild/" + (i + 1).ToString(CultureInfo.InvariantCulture);
        var caption = app.Shots[i].Caption;
        sb.Append("<figure class=\"").Append(cls).Append("\"><a href=\"").Append(src).Append("\" data-lightbox data-caption=\"").Append(E(caption)).Append("\"><img src=\"")
          .Append(src).Append("\" alt=\"").Append(E(caption.Length > 0 ? app.Name + ": " + caption : app.Name)).Append("\" loading=\"lazy\"></a>");
        if (caption.Length > 0) sb.Append("<figcaption>").Append(E(caption)).Append("</figcaption>");
        sb.Append("</figure>");
    }

    private static void FileLink(StringBuilder sb, string? url, string icon, string label)
    {
        if (url is null) return;
        var name = Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]);
        sb.Append("<li><a href=\"").Append(E(url)).Append("\"><i class=\"ti ").Append(icon).Append("\"></i><span><b>").Append(E(label)).Append("</b><small>")
          .Append(E(name)).Append("</small></span><i class=\"ti ti-download ts-files__get\"></i></a></li>");
    }

    // ---- pictures, through this site ------------------------------------------------------------------------------------

    private const int MaxPictureBytes = 8 * 1024 * 1024;
    private const long MaxCachedBytes = 96L * 1024 * 1024;
    private sealed record Pic(byte[] Bytes, string ETag, DateTime At);
    private static readonly ConcurrentDictionary<string, Pic> Pics = new();

    private static async Task<PluginResult> LogoAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (catalog, _) = await CatalogAsync(ctx, ct);
        if (catalog is null) return PluginResult.NotFound();
        var name = ctx.Request.R("name");
        var url = name == "launcher" ? catalog.LauncherIcon : catalog.Apps.FirstOrDefault(a => a.Short == name)?.Icon;
        return await ServeAsync(ctx, url, ct);
    }

    private static async Task<PluginResult> PictureAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (catalog, _) = await CatalogAsync(ctx, ct);
        var app = catalog?.Apps.FirstOrDefault(a => a.Short == ctx.Request.R("name"));
        if (app is null || !int.TryParse(ctx.Request.R("n"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > app.Shots.Count)
            return PluginResult.NotFound();
        return await ServeAsync(ctx, app.Shots[n - 1].Url, ct);
    }

    /// <summary>A PNG of the catalog: fetched once, then from memory (browsers keep it a day). The address comes from the catalog
    /// (checked by <see cref="Trusted"/>), never from the request.</summary>
    private static async Task<PluginResult> ServeAsync(IPluginContext ctx, string? url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return PluginResult.NotFound();
        if (Pics.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromDays(1)) return Answer(hit);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Agent);
            using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > MaxPictureBytes) return hit is not null ? Answer(hit) : PluginResult.NotFound();
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > MaxPictureBytes || !IsPng(bytes)) return hit is not null ? Answer(hit) : PluginResult.NotFound();
            var pic = new Pic(bytes, "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + "\"", DateTime.UtcNow);
            Pics[url] = pic;
            while (Pics.Values.Sum(p => (long)p.Bytes.Length) > MaxCachedBytes && Pics.Count > 1)
            {
                var oldest = Pics.OrderBy(p => p.Value.At).First().Key;
                if (oldest == url) break;
                Pics.TryRemove(oldest, out _);
            }
            return Answer(pic);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ctx.Log("Thix-Store: Bild " + url + " – " + ex.Message);
            return hit is not null ? Answer(hit) : PluginResult.NotFound();
        }
    }

    private static PluginResult Answer(Pic p) =>
        PluginResult.File(p.Bytes, "image/png").WithHeader("Cache-Control", "public, max-age=86400").WithHeader("ETag", p.ETag)
            .WithHeader("X-Content-Type-Options", "nosniff");

    private static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A;

    // ---- admin --------------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> StatusAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (catalog, error) = await CatalogAsync(ctx, ct, fresh: true);
        if (catalog is null)
            return PluginResult.Page(Ui.Stack(Ui.PageHead(ctx.T("ts.tab.status"), ctx.T("ts.nav")), Ui.Alert(ctx.T("ts.status.error", error ?? ""), UiTone.Warning),
                Ui.Muted(ctx.T("ts.status.source") + " " + CatalogUrl(ctx))), ctx.T("ts.nav"));
        var releases = await ReleasesAsync(ctx, catalog, ct);
        var rows = catalog.Apps.Select(a =>
        {
            var r = releases.GetValueOrDefault(a.Manifest);
            return Ui.Row(a.Name, Ui.Link("/apps/" + a.Short, "/apps/" + a.Short, newTab: true), r?.Version ?? "–", r?.Windows is null ? "–" : "✓",
                r?.MacArm is null ? "–" : "✓", TsApps.All.Any(x => x.Short == a.Short) ? "✓" : ctx.T("ts.status.noExtras"));
        });
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(ctx.T("ts.tab.status"), ctx.T("ts.nav")),
            Ui.Alert(ctx.T("ts.status.apps", catalog.Apps.Count), UiTone.Success),
            Ui.Table(new[] { Ui.Column("App"), Ui.Column(ctx.T("ts.status.page")), Ui.Column("Version"), Ui.Column("Windows"), Ui.Column("Mac"), Ui.Column(ctx.T("ts.status.extras")) }, rows),
            Ui.Muted(ctx.T("ts.status.source") + " " + CatalogUrl(ctx))), ctx.T("ts.nav"));
    }

    // ---- small helpers ------------------------------------------------------------------------------------------------

    private static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static IEnumerable<string> Paragraphs(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public static string? Hex(string v) => v.Length == 7 && v[0] == '#' && v.Skip(1).All(Uri.IsHexDigit) ? v : null;
    private static string Icon(string v) => v.StartsWith("ti-", StringComparison.Ordinal) && v.Skip(3).All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') ? v : "ti-point";

    /// <summary>**word** in an already encoded text becomes bold (the hero's lead names the launcher that way).</summary>
    private static string Bold(string encoded)
    {
        var parts = encoded.Split("**");
        var sb = new StringBuilder(parts[0]);
        for (var i = 1; i < parts.Length; i++) sb.Append(i % 2 == 1 && i < parts.Length - 1 ? "<b>" : i % 2 == 0 ? "</b>" : "**").Append(parts[i]);
        return sb.ToString();
    }

    // The site's theme (thixapps) draws the ta-* pieces; these are the few the store adds. Its own variables stand behind everything.
    private const string Css = """
<style>
.ts-cta{margin:1.4rem 0 .6rem}
.ts-facts{display:flex;flex-wrap:wrap;align-items:center;gap:8px 16px;margin:0;color:var(--ta-muted,#64748b);font-size:.92rem}
.ts-facts span{display:inline-flex;align-items:center;gap:6px}
.ts-chip{padding:4px 10px;border-radius:999px;background:color-mix(in srgb,var(--app,#2F6FED) 12%,#fff);color:var(--app,#2F6FED);font-weight:650}
.ts-group{font-size:1.4rem;margin:0 0 1.2rem}
.ts-gallery{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:28px;margin-top:8px}
.ts-gallery--one{grid-template-columns:minmax(0,1fr);max-width:980px;margin-left:auto;margin-right:auto}
.ta-shot a{display:block}
.ta-shot figcaption{margin-top:10px;font-size:.92rem;color:var(--ta-muted,#64748b);text-align:center}
.ts-install h2{margin-bottom:.6rem}
.ts-steps{margin:0 0 1.6rem;padding-left:1.3rem;line-height:1.7;max-width:62ch}
.ts-install .ta-btn--primary{background:var(--app,#2F6FED);box-shadow:0 10px 24px -10px color-mix(in srgb,var(--app,#2F6FED) 70%,transparent)}
.ts-more{margin-top:2.2rem;border-top:1px solid var(--ta-line,#e2e8f0);padding-top:1rem;max-width:760px}
.ts-more summary{display:inline-flex;align-items:center;gap:8px;cursor:pointer;list-style:none;font-weight:600;color:var(--ta-ink,#0f172a)}
.ts-more summary::-webkit-details-marker{display:none}
.ts-more summary i{transition:transform .15s;color:var(--ta-muted,#64748b)}
.ts-more[open] summary i{transform:rotate(90deg)}
.ts-more summary:hover{color:var(--app,#2F6FED)}
.ts-more__in{padding:12px 0 0 26px}
.ts-more__in>p{margin:0 0 12px;line-height:1.6}
.ts-files{list-style:none;margin:0 0 10px;padding:0;display:grid;gap:8px}
.ts-files a{display:flex;align-items:center;gap:12px;padding:10px 14px;border-radius:12px;background:#fff;border:1px solid var(--ta-line,#e2e8f0);color:var(--ta-ink,#0f172a);text-decoration:none}
.ts-files a:hover{border-color:var(--app,#2F6FED)}
.ts-files a>i{font-size:1.3rem}
.ts-files span{display:flex;flex-direction:column;flex:1;min-width:0}
.ts-files small{color:var(--ta-muted,#64748b);font-size:.82rem;overflow-wrap:anywhere}
.ts-files .ts-files__get{color:var(--ta-muted,#64748b);font-size:1.1rem}
.ts-small{font-size:.88rem;color:var(--ta-muted,#64748b);margin:0}.ts-small a{color:var(--ta-blue,#2F6FED)}
.ts-pagerband{padding:0 0 64px}
.ts-pagerband .ta-pager{margin-top:0}
.ts-wingrid .ta-win__app b{font-size:.7rem}
@media (max-width:900px){.ts-gallery{grid-template-columns:minmax(0,1fr)}}
</style>
""";
}

/// <summary>Every text a visitor or the editor sees: German and English (the site is German; the admin may run in English).</summary>
public static class TsText
{
    public sealed record Row(string Key, string De, string En);

    public static readonly Row[] Rows =
    {
        new("ts.set.catalog", "Katalog", "Catalog"),
        new("ts.set.catalogHelp", "Adresse der catalog.json des ThixLaunchers (nur GitHub).", "Address of the ThixLauncher's catalog.json (GitHub only)."),
        new("ts.set.cache", "Zwischenspeichern (Minuten)", "Keep in memory (minutes)"),
        new("ts.set.cacheHelp", "So lange gelten Katalog und Versionen, bevor GitHub wieder gefragt wird.", "How long catalog and versions are kept before GitHub is asked again."),
        new("ts.block.apps", "Thix-Apps", "Thix apps"),
        new("ts.block.appsDesc", "Alle Apps des ThixLaunchers als Kacheln, mit Link auf ihre Seite.", "Every app of the ThixLauncher as a tile, linking to its page."),
        new("ts.block.hero", "Thix-Hero", "Thix hero"),
        new("ts.block.heroDesc", "Kopf mit Text, Download des ThixLaunchers und dem gezeichneten Launcher-Fenster mit allen Apps.", "Head with text, the ThixLauncher download and the drawn launcher window with every app."),
        new("ts.f.kicker", "Dachzeile", "Kicker"),
        new("ts.f.heading", "Überschrift", "Heading"),
        new("ts.f.headingHelp", "{n} wird zur Zahl der Apps.", "{n} becomes the number of apps."),
        new("ts.f.intro", "Einleitung", "Introduction"),
        new("ts.f.lead", "Text", "Text"),
        new("ts.f.leadHelp", "**Wort** wird fett.", "**word** becomes bold."),
        new("ts.f.show", "Zeigen", "Show"),
        new("ts.o.all", "Alle Apps", "All apps"),
        new("ts.o.apps", "Nur die Apps", "The apps only"),
        new("ts.o.office", "Nur ThixOffice", "ThixOffice only"),
        new("ts.nav", "Thix-Store", "Thix store"),
        new("ts.tab.status", "Stand", "Status"),
        new("ts.status.source", "Katalog:", "Catalog:"),
        new("ts.status.error", "Der Katalog ist nicht erreichbar: {0}", "The catalog cannot be reached: {0}"),
        new("ts.status.apps", "{0} Apps im Katalog.", "{0} apps in the catalog."),
        new("ts.status.page", "Seite", "Page"),
        new("ts.status.extras", "Texte der Website", "Website texts"),
        new("ts.status.noExtras", "fehlen (apps.csx)", "missing (apps.csx)"),
        new("ts.kicker", "Thix-Apps", "Thix apps"),
        new("ts.launcherLogo", "Logo ThixLauncher", "ThixLauncher logo"),
        new("ts.logo", "Logo {0}", "{0} logo"),
        new("ts.all.title", "Alle Apps", "All apps"),
        new("ts.all.pageTitle", "Alle Thix-Apps – für Windows und macOS", "All Thix apps – for Windows and macOS"),
        new("ts.all.lead", "{0} Apps für Windows und macOS. Mit dem ThixLauncher installierst du jede mit einem Klick und hältst sie aktuell, ohne daran zu denken.",
            "{0} apps for Windows and macOS. With the ThixLauncher you install each with one click and keep them up to date without thinking about it."),
        new("ts.all.description", "Alle {0} Thix-Apps für Windows und macOS – installiert und aktuell gehalten mit dem ThixLauncher.",
            "All {0} Thix apps for Windows and macOS – installed and kept up to date with the ThixLauncher."),
        new("ts.group.apps", "Die Apps", "The apps"),
        new("ts.group.office", "Dokumente, Tabellen, Präsentationen, E-Mail und Kontakte – im Format von Microsoft Office",
            "Documents, spreadsheets, presentations, e-mail and contacts – in the formats of Microsoft Office"),
        new("ts.new", "Neu", "New"),
        new("ts.details", "Mehr erfahren", "Learn more"),
        new("ts.install", "Mit dem ThixLauncher installieren", "Install with the ThixLauncher"),
        new("ts.systems", "Windows und macOS", "Windows and macOS"),
        new("ts.version", "Version {0}", "Version {0}"),
        new("ts.cloud", "mit MatCloud", "with MatCloud"),
        new("ts.installKicker", "Installieren", "Install"),
        new("ts.installTitle", "{0} mit dem ThixLauncher installieren", "Install {0} with the ThixLauncher"),
        new("ts.step1", "Den ThixLauncher für Windows oder macOS herunterladen und starten.", "Download the ThixLauncher for Windows or macOS and start it."),
        new("ts.step2", "In der Liste bei {0} auf „Installieren“ klicken.", "Click \"Install\" next to {0} in the list."),
        new("ts.step3", "Fertig: Der Launcher prüft jedes Paket und hält {0} und alle anderen Thix-Apps aktuell.",
            "Done: the launcher checks every package and keeps {0} and every other Thix app up to date."),
        new("ts.more", "Weitere Downloads: {0} ohne Launcher", "More downloads: {0} without the launcher"),
        new("ts.moreText", "Lieber von Hand? Hier liegt das Paket von {0} für jedes System. Der ThixLauncher bleibt der einfachste Weg: ein Klick, und auch die Updates kommen von selbst.",
            "Rather by hand? Here is the package of {0} for every system. The ThixLauncher stays the easiest way: one click, and the updates come by themselves."),
        new("ts.win", "Windows (64 Bit)", "Windows (64-bit)"),
        new("ts.macArm", "macOS – Apple Silicon", "macOS – Apple silicon"),
        new("ts.macIntel", "macOS – Intel", "macOS – Intel"),
        new("ts.noPackages", "Die Pakete sind gerade nicht abrufbar.", "The packages cannot be read right now."),
        new("ts.allVersions", "Alle Versionen bei GitHub", "All versions on GitHub"),
        new("ts.moreApps", "Weitere Apps", "More apps"),
        new("ts.allApps", "Alle Apps", "All apps"),
        new("ts.unavailable", "Die Liste der Apps ist gerade nicht erreichbar. Den ThixLauncher kannst du trotzdem herunterladen – er zeigt dir alle Apps.",
            "The list of apps cannot be reached right now. You can still download the ThixLauncher – it shows you every app."),
        new("ts.getLauncher", "ThixLauncher herunterladen", "Download the ThixLauncher"),
        new("ts.forWindows", "Für Windows", "For Windows"),
        new("ts.forMac", "Für macOS", "For macOS"),
        new("ts.launcherFor", "ThixLauncher für {0}", "ThixLauncher for {0}"),
        new("ts.appleSilicon", "Apple Silicon (M1 und neuer)", "Apple silicon (M1 and later)"),
        new("ts.intelQ", "Mac mit Intel-Prozessor?", "Mac with an Intel processor?"),
        new("ts.macIntelShort", "macOS (Intel)", "macOS (Intel)"),
        new("ts.allDownloads", "Alle Downloads und Hinweise", "All downloads and notes"),
        new("ts.band.title", "Bereit? Hol dir den ThixLauncher.", "Ready? Get the ThixLauncher."),
        new("ts.band.text", "Für Windows (64 Bit) und macOS (Apple Silicon und Intel).", "For Windows (64-bit) and macOS (Apple silicon and Intel)."),
        new("ts.hero.kicker", "ThixLauncher · für Windows und macOS", "ThixLauncher · for Windows and macOS"),
        new("ts.hero.title", "Kleine Apps, die den Alltag am Computer leichter machen.", "Small apps that make everyday work at the computer easier."),
        new("ts.hero.lead", "Zwischenablage, Notizen, Kalender, Mail, Screenshots, Speicherplatz, Synchronisation und ein ganzes Office: Mit dem **ThixLauncher** installierst du jede Thix-App mit einem Klick – und hältst sie aktuell, ohne daran zu denken.",
            "Clipboard, notes, calendar, mail, screenshots, disk space, sync and a whole office: with the **ThixLauncher** you install every Thix app with one click – and keep them up to date without thinking about it."),
        new("ts.win.installed", "Installiert", "Installed"),
        new("ts.win.settings", "Einstellungen", "Settings"),
        new("ts.win.install", "Installieren", "Install"),
        new("ts.win.open", "Öffnen", "Open"),
    };
}
