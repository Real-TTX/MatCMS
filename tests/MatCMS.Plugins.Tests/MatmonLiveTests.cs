using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>A stand-in for Matmon Cloud: answers GET paths from a dictionary, everything else with 404.</summary>
internal sealed class PathStub : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    public Dictionary<string, string> Answers { get; } = new();
    /// <summary>Binary answers (a logo), sent as they are.</summary>
    public Dictionary<string, byte[]> Bytes { get; } = new();
    public string Url => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;

    public PathStub()
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
                        var path = head.ToString().Split(' ')[1];
                        var ok = Answers.TryGetValue(path, out var answer);
                        var raw = Bytes.TryGetValue(path, out var bin);
                        ok |= raw;
                        var body = raw ? bin! : Encoding.UTF8.GetBytes(ok ? answer! : "nope");
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

/// <summary>The matmon.eu plugin (plugins/matmon-live: entry + snapshot.csx + live.csx), loaded for real against a stand-in cloud.</summary>
public class MatmonLiveTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public MatmonLiveTests(AppFactory app) => _app = app;

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "matmon-live", name));

    [Fact]
    public async Task Status_comes_from_the_cloud_and_an_older_catalogue_never_replaces_the_bundled_one()
    {
        using var cloud = new PathStub();
        cloud.Answers["/api/status"] = new JsonObject
        {
            ["overall"] = "degraded", ["checkedUtc"] = "2026-10-25T23:06:21Z",
            ["components"] = new JsonArray(
                new JsonObject { ["key"] = "cloud-general", ["name"] = "Cloud <General>", ["status"] = "operational" },
                new JsonObject { ["key"] = "email", ["name"] = "E-Mail", ["status"] = "degraded" })
        }.ToJsonString();
        // Older than the bundled snapshot (2026-10-22) and with only one type: must not shrink the page.
        cloud.Answers["/api/sensor-types"] = new JsonObject
        {
            ["generatedUtc"] = "2026-07-01T00:00:00Z",
            ["sensorTypes"] = new JsonArray(new JsonObject { ["key"] = "ping", ["name"] = "Ping", ["category"] = "Network" })
        }.ToJsonString();

        var pngLogo = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3 };
        var withLogo = Guid.NewGuid().ToString();
        cloud.Answers["/api/partners"] = new JsonObject
        {
            ["partners"] = new JsonArray(
                new JsonObject { ["id"] = withLogo, ["name"] = "Netz & <Co>", ["description"] = "Monitoring für den Mittelstand.", ["website"] = "https://netz.example",
                    ["city"] = "Stuttgart", ["country"] = "DE", ["logoUrl"] = cloud.Url + "/api/partners/" + withLogo + "/logo", ["pageUrl"] = "https://cloud.example/netz",
                    ["email"] = "hallo@netz.example", ["phone"] = "+49 711 123-456" },
                new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = "Evil", ["website"] = "javascript:alert(1)", ["country"] = "AT",
                    ["email"] = "x\"onmouseover=\"alert(1)@evil.example", ["phone"] = "javascript:alert(1)" },
                new JsonObject { ["id"] = "not-a-guid", ["name"] = "Broken" })
        }.ToJsonString();
        cloud.Bytes["/api/partners/" + withLogo + "/logo"] = pngLogo;

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = new Models.Plugin
            {
                Key = "matmon-live", Name = "Matmon Live", Version = "1.0", Kind = PluginKind.Script, Enabled = true,
                Code = Fixture("plugin.csx"),
                FilesJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["snapshot.csx"] = Fixture("snapshot.csx"), ["live.csx"] = Fixture("live.csx"), ["partners.csx"] = Fixture("partners.csx") }),
                ConfigJson = new JsonObject { ["cloudUrl"] = cloud.Url }.ToJsonString()
            };
            db.Plugins.Add(row);
            var page = new Page { Title = "Status", Slug = "mm-status", Locale = "de", IsPublished = true };
            db.Pages.Add(page);
            await db.SaveChangesAsync();
            db.ContentBlocks.Add(new ContentBlock { PageId = page.Id, BlockType = "matmon_status", SortOrder = 0, DataJson = "{\"heading\":\"Service status\"}" });
            db.ContentBlocks.Add(new ContentBlock { PageId = page.Id, BlockType = "matmon_sensors", SortOrder = 1, DataJson = "{\"heading\":\"{count} sensor types\"}" });
            db.ContentBlocks.Add(new ContentBlock { PageId = page.Id, BlockType = "matmon_partners", SortOrder = 2, DataJson = "{\"heading\":\"Find a partner\"}" });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
            var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
            Assert.True(registry.IsLoaded("matmon-live"), "did not load: " + string.Join(" | ", registry.Errors.Values));
        }

        var browser = _app.Browser();
        var html = await browser.GetStringAsync("/mm-status");
        Assert.Contains("mm-alert mm-warn", html);                                  // overall "degraded"
        Assert.Contains("Cloud &lt;General&gt;", html);                             // names from the cloud are text
        Assert.Contains("2026-10-25 23:06 UTC", html);

        // The page view above started the catalogue fetch in the background; give it a moment, then the snapshot must still be there.
        await Task.Delay(1500);
        html = await browser.GetStringAsync("/mm-status");
        Assert.Contains("42 sensor types", html);                                  // 45 in the snapshot, three internal ones hidden
        Assert.Contains("id=\"cat-network\"", html);
        Assert.DoesNotContain(">Probe Heartbeat<", html);                          // internal types are hidden
        Assert.Contains("\"count\":42", await browser.GetStringAsync("/matmon-live/count"));

        // Partners: listed with their text encoded, an unsafe "website" is no link, a bad id is left out, contact = the partner page.
        Assert.Contains("Netz &amp; &lt;Co&gt;", html);
        Assert.Contains("href=\"https://cloud.example/netz\"", html);
        Assert.Contains(">Evil<", html);
        Assert.DoesNotContain("javascript:alert", html);
        Assert.Contains("href=\"mailto:hallo@netz.example\"", html);                // the public contact, as links
        Assert.Contains("href=\"tel:+49711123456\"", html);
        Assert.DoesNotContain("onmouseover", html);                                 // a crafted address is left out, not escaped into a link
        Assert.DoesNotContain(">Broken<", html);
        Assert.Contains("/matmon-live/partner-logo/" + withLogo, html);              // the logo through this site, never the cloud's address
        Assert.DoesNotContain(cloud.Url, html);
        var logo = await browser.GetAsync("/matmon-live/partner-logo/" + withLogo);
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/png", logo.Content.Headers.ContentType?.MediaType);
        Assert.Equal(pngLogo, await logo.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/matmon-live/partner-logo/" + Guid.NewGuid())).StatusCode);   // not a listed partner   // the same number for the home page
    }
}
