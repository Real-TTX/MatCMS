using System.Net;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The store plugin "Abdeckungskarte" (plugins/coverage-map: entry + two #load files, one of them the generated outlines), loaded
/// for real and drawn on a public page.</summary>
public class CoverageMapTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public CoverageMapTests(AppFactory app) => _app = app;

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "coverage-map", name));

    private async Task InstallAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Plugins.FirstOrDefaultAsync(p => p.Key == "coverage-map");
        if (row is null)
        {
            db.Plugins.Add(row = new Models.Plugin
            {
                Key = "coverage-map", Name = "Abdeckungskarte", Version = "1.0", Kind = PluginKind.Script, Enabled = true,
                Code = Fixture("plugin.csx"),
                FilesJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["map.csx"] = Fixture("map.csx"), ["coverage.csx"] = Fixture("coverage.csx") })
            });
            await db.SaveChangesAsync();
        }
        await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
        var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
        Assert.True(registry.IsLoaded("coverage-map"), "did not load: " + string.Join(" | ", registry.Errors.Values));
    }

    private async Task PageAsync(string slug, JsonObject data)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var page = new Page { Title = "Abdeckung", Slug = slug, Locale = "de", IsPublished = true };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        db.ContentBlocks.Add(new ContentBlock { PageId = page.Id, BlockType = "coverage_map", DataJson = data.ToJsonString() });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_map_marks_the_listed_places_as_links_with_their_status_and_lists_them()
    {
        await InstallAsync();
        await PageAsync("abdeckung", new JsonObject
        {
            ["heading"] = "Wo die Karten reichen",
            ["list"] = "DE | available | Ganz Deutschland\nIT-N | partial | Alpen & <Po-Ebene>\nXX | available\nES | planned\nde | soon",
            ["view"] = "fit", ["asOf"] = "Stand: Oktober 2026"
        });
        var html = await _app.Browser().GetStringAsync("/abdeckung");
        var at = html.IndexOf("<section class=\"section cov\"", StringComparison.Ordinal);
        Assert.True(at >= 0, "no coverage block on the page");
        var block = html[at..];

        Assert.Contains("class=\"cov-area cov-available\" data-cov=\"DE\"", block);
        Assert.Contains("class=\"cov-area cov-partial\" data-cov=\"IT-N\"", block);
        Assert.Contains("class=\"cov-area cov-planned\" data-cov=\"ES\"", block);
        Assert.DoesNotContain("data-cov=\"XX\"", block);                          // an unknown code is skipped
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(block, "class=\"cov-area[^\"]*\" data-cov=\"DE\""));   // listed twice = first line
        Assert.Contains("Norditalien", block);
        Assert.Contains("Alpen &amp; &lt;Po-Ebene&gt;", block);                   // a note is text, never markup
        Assert.Contains("Stand: Oktober 2026", block);
        Assert.Contains(">Verfügbar<", WebUtility.HtmlDecode(block));
        Assert.Contains("fill:url(#", block);                                       // planned = hatched
        Assert.Contains("<path class=\"cov-land\"", block);                        // the rest of Europe as background
    }

    [Fact]
    public async Task A_block_without_a_list_shows_the_default_coverage()
    {
        await InstallAsync();
        await PageAsync("abdeckung-leer", new JsonObject { ["heading"] = "Abdeckung" });
        var html = await _app.Browser().GetStringAsync("/abdeckung-leer");
        foreach (var code in new[] { "DE", "AT", "CH", "FR", "IT-N" }) Assert.Contains($"data-cov=\"{code}\"", html);
    }
}
