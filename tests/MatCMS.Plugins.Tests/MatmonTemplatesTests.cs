using System.Net;
using System.Net.Http.Headers;
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

/// <summary>The matmon.eu template store (plugins/matmon-templates), loaded for real: a member submits, the admin approves, a guest downloads.</summary>
public class MatmonTemplatesTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public MatmonTemplatesTests(AppFactory app) => _app = app;

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "matmon-templates", name));

    private static readonly string[] Parts = { "text.csx", "css.csx", "file.csx", "store.csx", "mail.csx", "public.csx", "admin.csx" };

    private static string Location(HttpResponseMessage r) => r.Headers.Location?.OriginalString ?? "";

    private async Task InstallAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Plugins.AnyAsync(p => p.Key == "matmon-templates")) return;
        var row = new Models.Plugin
        {
            Key = "matmon-templates", Name = "Matmon Template-Store", Version = "1.0", Kind = PluginKind.Script, Enabled = true,
            Code = Fixture("plugin.csx"),
            FilesJson = System.Text.Json.JsonSerializer.Serialize(Parts.ToDictionary(p => p, Fixture)),
            ConfigJson = new JsonObject { ["notifyEmail"] = "owner@example.test" }.ToJsonString()
        };
        db.Plugins.Add(row);
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
            var (status, _) = await scope.ServiceProvider.GetRequiredService<MemberService>().CreateAsync(email, "correct horse", "Anna " + who);
            Assert.Equal(MemberService.CreateStatus.Created, status);
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

    private static string TemplateFile(string name) => new JsonObject
    {
        ["format"] = "matmon-templates", ["version"] = 1, ["exportedBy"] = "Matmon test",
        ["templates"] = new JsonArray(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["key"] = "builtin-ping", ["name"] = name, ["targetKind"] = "Any", ["sensorTypeKey"] = "http",
            ["target"] = "https://admin:hunter2@intranet.example/health",
            ["settings"] = new JsonObject
            {
                ["dependencySensorId"] = Guid.NewGuid().ToString(),
                ["parameters"] = new JsonObject { ["http.method"] = "GET", ["http.basicPassword"] = "hunter2", ["http.header"] = "enc:dp1:CfDJ8secret" },
                ["credentials"] = new JsonArray(new JsonObject { ["name"] = "Intranet", ["values"] = new JsonObject { ["user"] = "admin", ["password"] = "hunter2" }, ["protectedValues"] = "xyz" })
            },
            ["tags"] = new JsonArray("web")
        })
    }.ToJsonString();

    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient c, string title, string file, string? entry = null)
    {
        var page = await c.GetStringAsync("/templates/submit" + (entry is null ? "" : "?entry=" + entry));
        var form = new MultipartFormDataContent
        {
            { new StringContent(AppFactory.Token(page)!), "__RequestVerificationToken" },
            { new StringContent(title), "title" }, { new StringContent("Checks a web app."), "summary" },
            { new StringContent("Line one\n\n<script>alert(1)</script>"), "description" }, { new StringContent("Web, HTTP, web"), "tags" },
            { new StringContent(entry is null ? "1.0" : "1.1"), "version" }, { new StringContent("1"), "terms" }
        };
        if (entry is not null) form.Add(new StringContent(entry), "entry");
        var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(file));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(bytes, "file", "export.json");
        return await c.PostAsync("/templates/submit", form);
    }

    [Fact]
    public async Task A_member_submits_the_admin_approves_and_everybody_downloads_a_clean_file()
    {
        await InstallAsync();
        var guest = _app.Browser();
        Assert.Contains("Noch keine Templates", await guest.GetStringAsync("/templates"));
        var anon = await guest.GetAsync("/templates/submit");
        Assert.StartsWith("/anmelden?returnUrl=", Location(anon));

        var anna = await MemberAsync("anna");

        // Not a template file: refused with a reason, nothing stored.
        var bad = await SubmitAsync(anna, "Broken", "{\"hello\":1}");
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("keine Matmon-Template-Datei", await bad.Content.ReadAsStringAsync());

        var ok = await SubmitAsync(anna, "Web <health> check", TemplateFile("Intranet health"));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.StartsWith("/templates/mine?sent=", Location(ok));
        var mine = await anna.GetStringAsync(Location(ok));
        Assert.Contains("1.0</span>", mine);
        Assert.Contains("Web &lt;health&gt; check", mine);

        // Pending: not on the site yet.
        Assert.Contains("Noch keine Templates", await guest.GetStringAsync("/templates"));
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/templates/web-health-check")).StatusCode);

        // The admin reviews it.
        var admin = _app.Browser();
        await AppFactory.LoginAsync(admin);
        var queue = await admin.GetStringAsync("/admin/p/matmon-templates/queue");
        Assert.Contains("Web &lt;health&gt; check", queue);
        var versionId = System.Text.RegularExpressions.Regex.Match(queue, "/admin/p/matmon-templates/versions/(\\d+)").Groups[1].Value;
        var review = await admin.GetStringAsync("/admin/p/matmon-templates/versions/" + versionId);
        Assert.Contains("Intranet health", review);
        Assert.Contains("http.basicPassword", review);   // listed as removed
        var approve = await admin.PostAsync("/admin/p/matmon-templates/versions/" + versionId, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "approve", ["__RequestVerificationToken"] = AppFactory.Token(review)!
        }));
        Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);

        // Live now.
        var list = await guest.GetStringAsync("/templates");
        Assert.Contains("/templates/web-health-check", list);
        var detail = await guest.GetStringAsync("/templates/web-health-check");
        Assert.Contains("Intranet health", detail);
        Assert.Contains("&lt;script&gt;", detail);
        Assert.DoesNotContain("<script>alert", detail);
        Assert.Contains("von Anna anna", detail);

        var download = await guest.GetAsync("/templates/web-health-check/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var json = await download.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("enc:dp1:", json);
        Assert.DoesNotContain("builtin-ping", json);
        Assert.DoesNotContain("dependencySensorId", json);
        Assert.Contains("\"http.method\": \"GET\"", json);
        Assert.Contains("https://intranet.example/health", json);
        Assert.Contains("1 Downloads", await guest.GetStringAsync("/templates"));

        // A tag filter and a search find it, a wrong one does not.
        Assert.Contains("/templates/web-health-check", await guest.GetStringAsync("/templates?tag=http"));
        Assert.DoesNotContain("/templates/web-health-check", await guest.GetStringAsync("/templates?tag=htt"));
        Assert.Contains("/templates/web-health-check", await guest.GetStringAsync("/templates?q=health"));

        // An update waits for review; the live version stays until then.
        var upd = await SubmitAsync(anna, "Web health check", TemplateFile("Intranet health v2"), entry: "web-health-check");
        Assert.Equal(HttpStatusCode.Redirect, upd.StatusCode);
        Assert.DoesNotContain("Intranet health v2", await guest.GetStringAsync("/templates/web-health-check"));

        // Somebody else cannot upload into Anna's entry.
        var bert = await MemberAsync("bert");
        Assert.Equal(HttpStatusCode.NotFound, (await bert.GetAsync("/templates/submit?entry=web-health-check")).StatusCode);

        // Anna's account is deleted: the pending update goes, the published template stays without her name.
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var annaId = (await db.SiteMembers.FirstAsync(m => m.Email == "anna@templates.example")).Id;
            Assert.True(await scope.ServiceProvider.GetRequiredService<MemberService>().DeleteAsync(annaId));
        }
        var after = await guest.GetStringAsync("/templates/web-health-check");
        Assert.DoesNotContain("Anna", after);
        Assert.Contains("von einem ehemaligen Mitglied", after);
        var queueAfter = await admin.GetStringAsync("/admin/p/matmon-templates/queue");
        Assert.DoesNotContain("v1.1", queueAfter);
    }
}
