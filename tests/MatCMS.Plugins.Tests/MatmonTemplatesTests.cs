using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The matmon.eu template store (plugins/matmon-templates), loaded for real against a stand-in for Matmon Cloud's store API (a local
/// Kestrel on a free port): the site lists and downloads what the cloud publishes, forwards a member's submission with the key and
/// the member's reference, shows "my submissions" from the cloud, and erases the member there when the account goes.
/// </summary>
public class MatmonTemplatesTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private WebApplication? _cloud;
    private string _cloudUrl = "";
    private readonly List<(string Method, string Path, string? Auth, string Body)> _calls = [];

    public MatmonTemplatesTests(AppFactory app) => _app = app;

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "matmon-templates", name));

    private static readonly string[] Parts = { "text.csx", "css.csx", "cloud.csx", "public.csx", "admin.csx" };

    private static string Location(HttpResponseMessage r) => r.Headers.Location?.OriginalString ?? "";

    private const string File1 = """{"format":"matmon-templates","version":1,"templates":[{"name":"Hornetsecurity - services","sensorTypeKey":"http-advanced"}]}""";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        _cloud = builder.Build();

        async Task<string> Record(HttpContext http)
        {
            using var reader = new StreamReader(http.Request.Body);
            var body = await reader.ReadToEndAsync();
            lock (_calls) _calls.Add((http.Request.Method, http.Request.Path + http.Request.QueryString, http.Request.Headers.Authorization.ToString(), body));
            return body;
        }
        bool Keyed(HttpContext http) => http.Request.Headers.Authorization.ToString() == "Bearer mts_test";

        _cloud.MapGet("/api/store/templates", async (HttpContext http) =>
        {
            await Record(http);
            var q = http.Request.Query["q"].ToString();
            var items = q is "" or "hornet"
                ? """[{"slug":"hornetsecurity-status","title":"Hornetsecurity <status>","summary":"Services and incidents","tags":["status page","email"],"author":"Matmon","version":"1.0","templateCount":3,"sensorTypes":["http-advanced","rss-feed"],"downloads":7,"updated":"2026-11-05T10:00:00+00:00"}]"""
                : "[]";
            return Results.Text($$"""{"items":{{items}},"total":{{(items == "[]" ? 0 : 1)}},"page":1,"pageSize":24,"allTags":["status page","email"]}""", "application/json");
        });
        _cloud.MapGet("/api/store/templates/{slug}", async (string slug, HttpContext http) =>
        {
            await Record(http);
            return slug != "hornetsecurity-status" ? Results.NotFound() : Results.Text("""
                {"slug":"hornetsecurity-status","title":"Hornetsecurity <status>","summary":"Services and incidents","description":"Line one\n\n<script>alert(1)</script>",
                 "tags":["status page"],"author":"Matmon","version":"1.1","templateCount":1,"sensorTypes":["http-advanced"],"downloads":7,"updated":"2026-11-05T10:00:00+00:00",
                 "templates":[{"name":"Hornetsecurity - services","sensorType":"http-advanced","scope":"Sensor"}],
                 "versions":[{"version":"1.1","notes":"More channels","date":"2026-11-05T10:00:00+00:00"},{"version":"1.0","notes":"","date":"2026-11-04T10:00:00+00:00"}]}
                """, "application/json");
        });
        _cloud.MapGet("/api/store/templates/{slug}/download", async (string slug, HttpContext http) =>
        {
            await Record(http);
            return slug == "hornetsecurity-status" ? Results.File(Encoding.UTF8.GetBytes(File1), "application/json", "matmon-templates-hornetsecurity-status-1.1.json") : Results.NotFound();
        });
        _cloud.MapPost("/api/store/submissions", async (HttpContext http) =>
        {
            var body = await Record(http);
            if (!Keyed(http)) return Results.Unauthorized();
            var json = JsonNode.Parse(body)!;
            return json["file"]!.GetValue<string>().Contains("matmon-templates")
                ? Results.Json(new { id = Guid.NewGuid(), slug = "web-check", status = "pending", removed = new[] { "x: http.basicPassword" } })
                : Results.Json(new { error = "This is not a Matmon template file (Matmon: Monitoring > Templates > Export)." }, statusCode: 400);
        });
        _cloud.MapGet("/api/store/submissions", async (HttpContext http) =>
        {
            await Record(http);
            return !Keyed(http) ? Results.Unauthorized() : Results.Text("""
                {"items":[{"slug":"web-check","title":"Web <check>","version":"1.0","status":"rejected","reason":"Please describe it.","live":false,"created":"2026-11-05T10:00:00+00:00"}]}
                """, "application/json");
        });
        _cloud.MapGet("/api/store/authors/{author}/entries/{slug}", async (string author, string slug, HttpContext http) =>
        {
            await Record(http);
            return Keyed(http) && slug == "web-check" && author.EndsWith(":" + _annaId)
                ? Results.Json(new { slug, title = "Web check", summary = "s", description = "d", tags = "web" })
                : Results.NotFound();
        });
        _cloud.MapDelete("/api/store/authors/{author}", async (string author, HttpContext http) =>
        {
            await Record(http);
            return Keyed(http) ? Results.Json(new { changed = 2 }) : Results.Unauthorized();
        });

        await _cloud.StartAsync();
        _cloudUrl = _cloud.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_cloud is not null) await _cloud.DisposeAsync();
    }

    private long _annaId;

    private async Task InstallAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Plugins.FirstOrDefaultAsync(p => p.Key == "matmon-templates");
        if (row is null)
        {
            row = new Models.Plugin { Key = "matmon-templates", Name = "Matmon Template-Store", Version = "2.0", Kind = PluginKind.Script, Enabled = true };
            db.Plugins.Add(row);
        }
        row.Code = Fixture("plugin.csx");
        row.FilesJson = System.Text.Json.JsonSerializer.Serialize(Parts.ToDictionary(p => p, Fixture));
        row.ConfigJson = new JsonObject { ["cloudUrl"] = _cloudUrl, ["apiKey"] = "mts_test" }.ToJsonString();
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
        var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
        Assert.True(registry.IsLoaded("matmon-templates"), "did not load: " + string.Join(" | ", registry.Errors.Values));
    }

    private async Task<HttpClient> MemberAsync(string who)
    {
        var email = who + "@templates.example";
        using (var scope = _app.Services.CreateScope())
        {
            var (status, member) = await scope.ServiceProvider.GetRequiredService<MemberService>().CreateAsync(email, "correct horse", "Anna " + who);
            Assert.Equal(MemberService.CreateStatus.Created, status);
            _annaId = member!.Id;
        }
        var c = _app.Browser();
        var page = await c.GetStringAsync("/anmelden");
        var res = await c.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = email, ["password"] = "correct horse", ["__RequestVerificationToken"] = AppFactory.Token(page)!
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return c;
    }

    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient c, string title, string file, string? entry = null)
    {
        var page = await c.GetStringAsync("/templates/submit" + (entry is null ? "" : "?entry=" + entry));
        var form = new MultipartFormDataContent
        {
            { new StringContent(AppFactory.Token(page)!), "__RequestVerificationToken" },
            { new StringContent(title), "title" }, { new StringContent("Checks a web app."), "summary" },
            { new StringContent("Line one"), "description" }, { new StringContent("Web, HTTP"), "tags" },
            { new StringContent("1.0"), "version" }, { new StringContent("1"), "terms" }
        };
        if (entry is not null) form.Add(new StringContent(entry), "entry");
        var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(file));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(bytes, "file", "export.json");
        return await c.PostAsync("/templates/submit", form);
    }

    [Fact]
    public async Task The_site_shows_the_clouds_store_and_forwards_a_members_submission()
    {
        await InstallAsync();
        var guest = _app.Browser();

        // The catalogue and a set's page come from the cloud, encoded.
        var list = await guest.GetStringAsync("/templates");
        Assert.Contains("/templates/hornetsecurity-status", list);
        Assert.Contains("Hornetsecurity &lt;status&gt;", list);
        Assert.Contains("?tag=status%20page", list);
        Assert.Contains("Kein Template passt zur Suche", await guest.GetStringAsync("/templates?q=nothing"));
        Assert.Contains(_calls, c => c.Path == "/api/store/templates?q=nothing" && c.Auth == "");   // reading needs no key

        var detail = await guest.GetStringAsync("/templates/hornetsecurity-status");
        Assert.Contains("Hornetsecurity - services", detail);
        Assert.Contains("&lt;script&gt;", detail);
        Assert.DoesNotContain("<script>alert", detail);
        Assert.Contains("/templates/hornetsecurity-status/download?v=1.0", detail);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/templates/no-such-set")).StatusCode);

        var download = await guest.GetAsync("/templates/hornetsecurity-status/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(File1, await download.Content.ReadAsStringAsync());
        Assert.Equal("matmon-templates-hornetsecurity-status-1.1.json", download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        // Submitting needs an account.
        Assert.StartsWith("/anmelden?returnUrl=", Location(await guest.GetAsync("/templates/submit")));
        var anna = await MemberAsync("anna");

        // The cloud's refusal reaches the member in the cloud's words.
        var bad = await SubmitAsync(anna, "Broken", "{\"hello\":1}");
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("This is not a Matmon template file", await bad.Content.ReadAsStringAsync());

        var ok = await SubmitAsync(anna, "Web check", File1);
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("/templates/mine?sent=1", Location(ok));
        var sent = JsonNode.Parse(_calls.Last(c => c.Method == "POST").Body)!;
        Assert.Equal("Bearer mts_test", _calls.Last(c => c.Method == "POST").Auth);
        Assert.Equal("Web check", sent["title"]!.GetValue<string>());
        Assert.Equal("anna@templates.example", sent["authorEmail"]!.GetValue<string>());
        Assert.EndsWith(":" + _annaId, sent["authorRef"]!.GetValue<string>());
        Assert.Equal(File1, sent["file"]!.GetValue<string>());

        var mine = await anna.GetStringAsync("/templates/mine?sent=1");
        Assert.Contains("Web &lt;check&gt;", mine);
        Assert.Contains("Please describe it.", mine);

        // An update goes only into one's own entry: the cloud says which ones that is.
        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync("/templates/submit?entry=web-check")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anna.GetAsync("/templates/submit?entry=hornetsecurity-status")).StatusCode);

        // The account is deleted: the cloud is told.
        using (var scope = _app.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<MemberService>().DeleteAsync(_annaId));
        Assert.Contains(_calls, c => c.Method == "DELETE" && c.Path.EndsWith(":" + _annaId) && c.Auth == "Bearer mts_test");
    }
}
