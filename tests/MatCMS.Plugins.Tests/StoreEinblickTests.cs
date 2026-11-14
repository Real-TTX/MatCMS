using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>A tiny HTTP server that answers every request with the same JSON — the cloud's public store catalogue, without a cloud — and
/// a picture of the store (a 1×1 PNG) at <c>/api/public/store/plugins/{key}/image/{id}</c>.</summary>
internal sealed class CatalogStub : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    public string Body { get; set; } = "{}";
    public int Requests;
    public int PictureRequests;
    public int OfficialRequests;
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    public string Url => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;

    public CatalogStub()
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buf = new byte[8192]; var head = new StringBuilder();
                        while (!head.ToString().Contains("\r\n\r\n")) { var n = await stream.ReadAsync(buf); if (n == 0) return; head.Append(Encoding.ASCII.GetString(buf, 0, n)); }
                        Interlocked.Increment(ref Requests);
                        var ok = head.ToString().StartsWith("GET /api/public/store ", StringComparison.Ordinal) || head.ToString().StartsWith("GET /store/api/catalog ", StringComparison.Ordinal);
                        if (head.ToString().StartsWith("GET /store/api/", StringComparison.Ordinal)) Interlocked.Increment(ref OfficialRequests);
                        var picture = head.ToString().StartsWith("GET /api/public/store/plugins/leserstimmen/image/", StringComparison.Ordinal)
                            || head.ToString().StartsWith("GET /store/api/plugins/leserstimmen/images/", StringComparison.Ordinal);
                        if (picture) Interlocked.Increment(ref PictureRequests);
                        var body = picture ? Png : Encoding.UTF8.GetBytes(ok ? Body : "nope");
                        var header = $"HTTP/1.1 {(ok || picture ? "200 OK" : "404 Not Found")}\r\nContent-Type: {(picture ? "image/png" : "application/json")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                        await stream.WriteAsync(body);
                    }
                });
            }
        });
    }

    public void Dispose() { _stop.Cancel(); _listener.Stop(); }
}

/// <summary>
/// Store-Einblick, the store's shop window — and the proof that ONE plugin source runs as a script AND as a native plugin: the script variant
/// (plugins/store-einblick, entry + <c>#load "showcase.csx"</c>) and the native one (plugins/store-einblick-native, the same file compiled) are
/// switched on one after the other in the real application, against the same catalogue, and must draw the same block.
/// </summary>
public class StoreEinblickTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public StoreEinblickTests(AppFactory app) => _app = app;

    private static string Fixture(string path) => Path.Combine(AppContext.BaseDirectory, "fixtures", path);

    private static readonly string Catalog = new JsonObject
    {
        ["format"] = 1,
        ["plugins"] = new JsonArray(
            new JsonObject
            {
                ["key"] = "leserstimmen", ["name"] = "Leserstimmen", ["version"] = "1.3", ["description"] = "Rezensionen mit Sternen.", ["kind"] = "script", ["updated"] = "2026-10-20",
                ["publisher"] = "MATTHIX", ["publisherUrl"] = "https://matthix.de",
                // the MatCMS Store sends English texts beside the German ones; an empty field keeps the German
                ["translations"] = new JsonObject { ["en"] = new JsonObject { ["name"] = "Testimonials", ["summary"] = "", ["description"] = "Reviews with stars." } },
                ["images"] = new JsonArray(
                    new JsonObject { ["id"] = 7, ["caption"] = "Karten <mit> Sternen", ["url"] = "/api/public/store/plugins/leserstimmen/image/7" },
                    new JsonObject { ["id"] = 8, ["caption"] = "Admin", ["url"] = "/api/public/store/plugins/leserstimmen/image/8" })
            },
            new JsonObject { ["key"] = "demo-native", ["name"] = "Demo <b>Native</b>", ["version"] = "2.0", ["description"] = "Kompiliert.", ["kind"] = "native", ["api"] = "2", ["updated"] = "2026-10-21",
                ["publisher"] = "Böse", ["publisherUrl"] = "javascript:alert(1)" }),
        // "Klar" as an older cloud sends it (accent only); "Küste" with its whole look — and two values that try to leave the style attribute.
        ["templates"] = new JsonArray(
            new JsonObject { ["name"] = "Klar", ["description"] = "Ruhig.", ["accent"] = "#2563eb" },
            new JsonObject
            {
                ["name"] = "Küste", ["description"] = "Blau und Sand.", ["accent"] = "#2F6F8F", ["secondary"] = "#d9c3a0", ["alt"] = "#f2f6f8",
                ["heading"] = "#14303d", ["text"] = "#1a1a1a", ["background"] = "#ffffff", ["headerBg"] = "red;}</style><b>", ["headerText"] = "#14303d",
                ["headingFont"] = "Geologica", ["bodyFont"] = "Evil';}</style><script>", ["buttonRadius"] = "6"
            }),
        ["components"] = new JsonArray(
            new JsonObject { ["type"] = "image-text", ["name"] = "Bild mit Text", ["description"] = "Bild links.", ["icon"] = "photo" },
            new JsonObject { ["type"] = "quote", ["name"] = "Zitat", ["description"] = "Eine Stimme.", ["icon"] = "x\" onmouseover=\"alert(1)" })
    }.ToJsonString();

    private async Task<int> PageWithBlockAsync(string slug, string locale = "de")
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var page = new Page { Title = "Store", Slug = slug, Locale = locale, IsPublished = true };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        db.ContentBlocks.Add(new ContentBlock { PageId = page.Id, BlockType = "store_einblick", DataJson = "{\"heading\":\"Aus dem Store\",\"show\":\"all\",\"counts\":\"yes\"}" });
        await db.SaveChangesAsync();
        return page.Id;
    }

    private async Task<int> InstallAsync(Func<IServiceProvider, Models.Plugin> create, string storeUrl, string source = "cloud")
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var other in db.Plugins.Where(p => p.Key.StartsWith("store-einblick"))) other.Enabled = false;      // one variant at a time: the same block type
        await db.SaveChangesAsync();
        foreach (var other in db.Plugins.Where(p => p.Key.StartsWith("store-einblick")).ToList())
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(other.Id);
        var fresh = create(scope.ServiceProvider);
        var row = await db.Plugins.FirstOrDefaultAsync(p => p.Key == fresh.Key);
        if (row is null) db.Plugins.Add(row = fresh);
        else
        {
            row.Code = fresh.Code; row.FilesJson = fresh.FilesJson; row.Kind = fresh.Kind; row.Entry = fresh.Entry; row.Sha256 = fresh.Sha256;
            row.Api = fresh.Api; row.Version = fresh.Version;
        }
        row.ConfigJson = new JsonObject { ["storeUrl"] = storeUrl, ["cacheMinutes"] = "1", ["source"] = source }.ToJsonString();
        row.Enabled = true;
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
        var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
        Assert.True(registry.IsLoaded(row.Key), row.Key + " did not load: " + string.Join(" | ", registry.Errors.Values));
        return row.Id;
    }

    private static Models.Plugin Script(IServiceProvider _) => new()
    {
        Key = "store-einblick", Name = "Store-Einblick", Version = "1.0", Kind = PluginKind.Script,
        Code = File.ReadAllText(Fixture("store-einblick/plugin.csx")),
        FilesJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["showcase.csx"] = File.ReadAllText(Fixture("store-einblick/showcase.csx")) })
    };

    private static Models.Plugin Native(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        if (!db.SiteSettings.Any(s => s.Key == SettingKeys.AllowNativePlugins))
            db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.AllowNativePlugins, Value = "1" });
        var env = sp.GetRequiredService<IWebHostEnvironment>();
        var src = StoragePaths.PluginSrc(env, "store-einblick-native", "1.0");
        Directory.CreateDirectory(src);
        File.Copy(Fixture("store-einblick-native/StoreEinblick.dll"), Path.Combine(src, "StoreEinblick.dll"), overwrite: true);
        return new Models.Plugin
        {
            Key = "store-einblick-native", Name = "Store-Einblick (Native)", Version = "1.0", Kind = PluginKind.Native, Entry = "StoreEinblick.dll",
            Sha256 = NativePluginHost.HashOf(Path.Combine(src, "StoreEinblick.dll")), Api = typeof(IPlugin).Assembly.GetName().Version!.Major
        };
    }

    private static string Section(string html)
    {
        var at = html.IndexOf("<section class=\"section se-store\"", StringComparison.Ordinal);
        Assert.True(at >= 0, "no store block on the page");
        return html[at..html.IndexOf("</style>", at, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task The_script_and_the_native_variant_draw_the_same_shop_window_from_the_public_catalogue()
    {
        using var stub = new CatalogStub { Body = Catalog };
        await PageWithBlockAsync("store-test");
        var visitor = _app.Browser();

        await InstallAsync(Script, stub.Url);
        var fromScript = Section(await visitor.GetStringAsync("/store-test"));
        Assert.Contains("Leserstimmen", fromScript);
        Assert.Contains("se-badge se-native", fromScript);
        // "everything": a heading per kind with its number, and the tabs (shown by the script) to filter by kind
        Assert.Contains("<h3 class=\"se-sub\">Templates <span class=\"se-n\">2</span></h3>", fromScript);
        Assert.Contains("data-se-tab=\"components\" aria-pressed=\"false\">Komponenten<span>2</span>", fromScript);
        Assert.Contains("<div class=\"se-bar\" hidden>", fromScript);
        Assert.Contains("Demo &lt;b&gt;Native&lt;/b&gt;", fromScript);                    // what the catalogue says is text, never markup
        Assert.Contains("background:#2563eb", fromScript);                                // an older cloud's template: its accent as a swatch
        // a template is drawn in its own colours and heading font — validated values only; anything else falls back
        Assert.Contains("--ta:#2f6f8f;--tb:#ffffff;--th:#14303d;--tx:#1a1a1a;--thb:#ffffff;--tht:#14303d;--tl:#f2f6f8;--tr:3px;--tf:&#39;Geologica&#39;,sans-serif", fromScript);
        Assert.DoesNotContain("Evil", fromScript);
        Assert.DoesNotContain("red;", fromScript);
        // a component with its icon; an icon name that is not one becomes the puzzle piece
        Assert.Contains("<i class=\"ti ti-photo\"></i>", fromScript);
        Assert.Contains("<i class=\"ti ti-puzzle\"></i>", fromScript);
        Assert.DoesNotContain("onmouseover", fromScript);
        // pictures through THIS site, one lightbox group per plugin; the publisher with a link only when the address is http(s)
        Assert.Contains("data-lightbox-group=\"se-leserstimmen\"", fromScript);
        Assert.Contains("href=\"/store-einblick/bild/leserstimmen/7\" data-lightbox data-caption=\"Karten &lt;mit&gt; Sternen\"", fromScript);
        Assert.Contains("href=\"/store-einblick/bild/leserstimmen/8\"", fromScript);
        Assert.Contains("2 Bilder", fromScript);
        // the details view: every picture once more as a button (it clicks the card's link, so the lightbox has each picture ONCE)
        Assert.Contains("<div class=\"se-detail\" role=\"dialog\" aria-modal=\"true\" aria-label=\"Leserstimmen\" hidden>", fromScript);
        Assert.Contains("data-se-pic=\"1\"", fromScript);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(fromScript, "href=\"/store-einblick/bild/leserstimmen/\\d+\" data-lightbox").Count);
        Assert.Contains("<dt>Art</dt><dd>Native · Plugin-API 2</dd>", WebUtility.HtmlDecode(fromScript));
        Assert.Contains("Native Plugins erlauben", WebUtility.HtmlDecode(fromScript));
        Assert.Contains("<a href=\"https://matthix.de/\" rel=\"noopener\" target=\"_blank\">MATTHIX</a>", fromScript);
        Assert.Contains("<strong>B&#246;se</strong>", fromScript);
        Assert.DoesNotContain("javascript:", fromScript);
        Assert.DoesNotContain(stub.Url, fromScript);                                     // the visitor's browser never talks to the cloud

        await InstallAsync(Native, stub.Url);
        var fromNative = Section(await visitor.GetStringAsync("/store-test"));
        Assert.Equal(fromScript, fromNative);
    }

    [Fact]
    public async Task On_an_English_page_the_shop_window_speaks_English()
    {
        using var stub = new CatalogStub { Body = Catalog };
        await ScriptPluginSite.SettingAsync(_app, SettingKeys.Languages, "de,en");
        await PageWithBlockAsync("store-en", "en");
        await PageWithBlockAsync("store-test-de");
        await InstallAsync(Script, stub.Url);
        var en = WebUtility.HtmlDecode(Section(await _app.Browser().GetStringAsync("/en/store-en")));
        Assert.Contains(">Components<span>2</span>", en);
        Assert.Contains("2 pictures", en);
        Assert.Contains("<dt>Kind</dt><dd>Native · plugin API 2</dd>", en);
        Assert.Contains("20 Oct 2026", en);                                // a day as English writes it
        Assert.DoesNotContain("20.10.2026", en);
        Assert.DoesNotContain("Komponenten", en);
        Assert.Contains("Testimonials", en);                              // the catalogue's English texts
        Assert.Contains("Reviews with stars.", en);
        Assert.DoesNotContain("Rezensionen mit Sternen.", en);

        var de = WebUtility.HtmlDecode(Section(await _app.Browser().GetStringAsync("/store-test-de")));
        Assert.Contains("20.10.2026", de);
        Assert.Contains("Rezensionen mit Sternen.", de);
        Assert.DoesNotContain("Testimonials", de);
    }

    [Fact]
    public async Task A_picture_is_passed_through_only_when_the_catalogue_lists_it_and_only_once()
    {
        using var stub = new CatalogStub { Body = Catalog };
        await InstallAsync(Script, stub.Url);
        var visitor = _app.Browser();

        var res = await visitor.GetAsync("/store-einblick/bild/leserstimmen/7");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(CatalogStub.Png, await res.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/store-einblick/bild/leserstimmen/7")).StatusCode);
        Assert.Equal(1, stub.PictureRequests);                                             // the second answer came from memory

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/store-einblick/bild/leserstimmen/999")).StatusCode);   // not in the catalogue
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/store-einblick/bild/demo-native/7")).StatusCode);     // another plugin's id
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/store-einblick/bild/leserstimmen/abc")).StatusCode);
        Assert.Equal(1, stub.PictureRequests);                                             // none of those reached the cloud
    }

    [Fact]
    public async Task The_official_MatCMS_Store_is_read_at_its_own_addresses()
    {
        using var stub = new CatalogStub { Body = Catalog };
        await PageWithBlockAsync("store-official");
        await InstallAsync(Script, stub.Url, source: "matcms");
        var html = WebUtility.HtmlDecode(Section(await _app.Browser().GetStringAsync("/store-official")));
        Assert.Contains("Leserstimmen", html);
        Assert.Equal(HttpStatusCode.OK, (await _app.Browser().GetAsync("/store-einblick/bild/leserstimmen/8")).StatusCode);
        Assert.True(stub.OfficialRequests >= 2);    // catalogue and picture both at /store/api/…, never at a cloud's /api/public/store
    }

    [Fact]
    public async Task A_cloud_that_cannot_be_reached_leaves_a_quiet_line_not_a_broken_page()
    {
        await PageWithBlockAsync("store-down");
        await InstallAsync(Script, "http://127.0.0.1:1");                // nothing listens there
        var res = await _app.Browser().GetAsync("/store-down");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Der Store ist gerade nicht erreichbar.", WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync()));
    }
}
