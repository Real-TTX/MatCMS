using System.Net;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The template store block of matmon.eu (plugins/matmon-live, templates.csx), loaded for real against a stand-in cloud: it shows
/// what the cloud's public store API publishes - texts encoded, details from the set's own address, unsafe slugs left out - and
/// hands the file out through the site itself, only for a set of the list. Submitting and reviewing live in the cloud.
/// </summary>
public class MatmonTemplatesBlockTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public MatmonTemplatesBlockTests(AppFactory app) => _app = app;

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "matmon-live", name));

    [Fact]
    public async Task The_block_shows_the_clouds_store_and_passes_the_download_through()
    {
        using var cloud = new PathStub();
        cloud.Answers["/api/store/templates?sort=new&page=1"] = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject
                {
                    ["slug"] = "hornetsecurity-status", ["title"] = "Hornetsecurity <status>", ["summary"] = "All services of status.hornetsecurity.com.",
                    ["tags"] = new JsonArray("Status page", "E-mail security"), ["author"] = "Matmon", ["version"] = "1.0", ["templateCount"] = 3,
                    ["sensorTypes"] = new JsonArray("http-advanced"), ["downloads"] = 7, ["updated"] = "2026-11-05T15:00:00+00:00"
                },
                new JsonObject { ["slug"] = "../admin", ["title"] = "Escape" },
                new JsonObject { ["slug"] = "no-title", ["title"] = "" }),
            ["total"] = 3, ["page"] = 1, ["pageSize"] = 24, ["allTags"] = new JsonArray("Status page")
        }.ToJsonString();
        cloud.Answers["/api/store/templates/hornetsecurity-status"] = new JsonObject
        {
            ["slug"] = "hornetsecurity-status", ["description"] = "Line one\nline <two>\n\nSecond paragraph",
            ["templates"] = new JsonArray(new JsonObject { ["name"] = "Hornetsecurity - services", ["sensorType"] = "http-advanced", ["scope"] = "Sensor" }),
            ["versions"] = new JsonArray(new JsonObject { ["version"] = "1.0", ["notes"] = "First <release>", ["date"] = "2026-11-05T15:00:00+00:00" })
        }.ToJsonString();
        cloud.Answers["/api/store/templates/hornetsecurity-status/download"] = """{"format":"matmon-templates","version":1,"templates":[]}""";

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var files = new[] { "snapshot.csx", "live.csx", "partners.csx", "changelog.csx", "templates.csx" }.ToDictionary(f => f, Fixture);
            var row = new Models.Plugin
            {
                Key = "matmon-live", Name = "Matmon Live", Version = "1.6", Kind = PluginKind.Script, Enabled = true,
                Code = Fixture("plugin.csx"),
                FilesJson = System.Text.Json.JsonSerializer.Serialize(files),
                ConfigJson = new JsonObject { ["cloudUrl"] = cloud.Url, ["githubApi"] = cloud.Url }.ToJsonString()
            };
            db.Plugins.Add(row);
            var page = new Page { Title = "Templates", Slug = "mm-templates", Locale = "de", IsPublished = true };
            db.Pages.Add(page);
            await db.SaveChangesAsync();
            db.ContentBlocks.Add(new ContentBlock { PageId = page.Id, BlockType = "matmon_templates", SortOrder = 0, DataJson = "{\"heading\":\"Templates\",\"submitText\":\"Submit a template\"}" });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
            var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
            Assert.True(registry.IsLoaded("matmon-live"), "did not load: " + string.Join(" | ", registry.Errors.Values));
        }

        var browser = _app.Browser();
        var html = await browser.GetStringAsync("/mm-templates");
        Assert.Contains("Hornetsecurity &lt;status&gt;", html);                   // texts from the cloud are text
        Assert.Contains("data-tag=\"status page\"", html);
        Assert.Contains("Line one<br>line &lt;two&gt;</p><p>Second paragraph", html);  // line breaks kept, everything encoded
        Assert.Contains("Hornetsecurity - services", html);
        Assert.Contains("First &lt;release&gt;", html);
        Assert.DoesNotContain("Escape", html);                                   // an unsafe slug is left out
        Assert.Contains(cloud.Url + "/templates/submit", html);                  // "Submit" leads to the cloud's own page
        Assert.Contains("href=\"/matmon-live/template/hornetsecurity-status\"", html);

        var file = await browser.GetAsync("/matmon-live/template/hornetsecurity-status");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("matmon-templates-hornetsecurity-status-1.0.json", file.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Contains("matmon-templates", await file.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/matmon-live/template/not-listed")).StatusCode);
    }
}
