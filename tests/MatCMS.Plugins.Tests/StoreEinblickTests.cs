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

/// <summary>A tiny HTTP server that answers every request with the same JSON — the cloud's public store catalogue, without a cloud.</summary>
internal sealed class CatalogStub : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    public string Body { get; set; } = "{}";
    public int Requests;
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
                        var ok = head.ToString().StartsWith("GET /api/public/store ", StringComparison.Ordinal);
                        var body = Encoding.UTF8.GetBytes(ok ? Body : "nope");
                        var header = $"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
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
            new JsonObject { ["key"] = "leserstimmen", ["name"] = "Leserstimmen", ["version"] = "1.3", ["description"] = "Rezensionen mit Sternen.", ["kind"] = "script", ["updated"] = "2026-10-20" },
            new JsonObject { ["key"] = "demo-native", ["name"] = "Demo <b>Native</b>", ["version"] = "2.0", ["description"] = "Kompiliert.", ["kind"] = "native", ["updated"] = "2026-10-21" }),
        ["templates"] = new JsonArray(new JsonObject { ["name"] = "Klar", ["description"] = "Ruhig.", ["accent"] = "#2563eb" }),
        ["components"] = new JsonArray()
    }.ToJsonString();

    private async Task<int> PageWithBlockAsync(string slug)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var page = new Page { Title = "Store", Slug = slug, Locale = "de", IsPublished = true };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        db.ContentBlocks.Add(new ContentBlock { PageId = page.Id, BlockType = "store_einblick", DataJson = "{\"heading\":\"Aus dem Store\",\"show\":\"all\",\"counts\":\"yes\"}" });
        await db.SaveChangesAsync();
        return page.Id;
    }

    private async Task<int> InstallAsync(Func<IServiceProvider, Models.Plugin> create, string storeUrl)
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
        row.ConfigJson = new JsonObject { ["storeUrl"] = storeUrl, ["cacheMinutes"] = "1" }.ToJsonString();
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
        Assert.Contains("2 Plugins · 1 Templates · 0 Komponenten", WebUtility.HtmlDecode(fromScript));
        Assert.Contains("Demo &lt;b&gt;Native&lt;/b&gt;", fromScript);                    // what the catalogue says is text, never markup
        Assert.Contains("background:#2563eb", fromScript);

        await InstallAsync(Native, stub.Url);
        var fromNative = Section(await visitor.GetStringAsync("/store-test"));
        Assert.Equal(fromScript, fromNative);
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
