using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The Narrenfigur (plugins/narrenfigur, a script): a figure in the corner of the public site that visitors equip. The plugin is thin — two
/// asset files and a line of body HTML — so what can break is the wiring: the script loads, the page carries the files, the route answers
/// with the settings, and every text exists in German and English (settings in the script, the figure's words in figur.js).
/// </summary>
public class NarrenfigurTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public NarrenfigurTests(AppFactory app) => _app = app;

    private static string Source(string file)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MatCMS.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "plugins", "narrenfigur", file));
    }

    [Fact]
    public void Every_text_is_German_and_English()
    {
        var code = Source("plugin.csx");
        var rows = Regex.Matches(code, "\\(\"(nf\\.[a-zA-Z0-9.]+)\",\\s*\"([^\"]*)\",\\s*\"([^\"]*)\"\\)")
            .ToDictionary(m => m.Groups[1].Value, m => (De: m.Groups[2].Value, En: m.Groups[3].Value));
        Assert.NotEmpty(rows);
        Assert.DoesNotContain(rows, r => r.Value.De.Length == 0 || r.Value.En.Length == 0);
        var used = Regex.Matches(code, "\"(nf\\.[a-zA-Z][a-zA-Z0-9.]*[a-zA-Z0-9])\"").Select(m => m.Groups[1].Value).Distinct();
        Assert.DoesNotContain(used, k => !rows.ContainsKey(k));

        // the figure's own words: the two tables of figur.js name the same things
        var js = Source(Path.Combine("assets", "figur.js"));
        var tables = Regex.Match(js, @"var T = en \? \{(?<en>[\s\S]*?)\} : \{(?<de>[\s\S]*?)\};");
        Assert.True(tables.Success, "the text tables of figur.js were not found");
        static string[] Keys(string table) => Regex.Matches(table, @"(?<![\w'])([a-zA-Z]+):").Select(m => m.Groups[1].Value).Distinct().OrderBy(k => k).ToArray();
        Assert.Equal(Keys(tables.Groups["de"].Value), Keys(tables.Groups["en"].Value));
        Assert.Contains("Hexe ausrüsten", tables.Groups["de"].Value);
        Assert.Contains("Equip the witch", tables.Groups["en"].Value);
        Assert.Contains("Narr ausrüsten", tables.Groups["de"].Value);     // the second figure (1.2) is named in both
        Assert.Contains("Equip the jester", tables.Groups["en"].Value);
        // every figure that can be drawn has its words, and every option of the setting "figure" can be drawn
        var figures = Regex.Match(js, @"var FIGURES = {([^}]*)}").Groups[1].Value;
        foreach (var k in new[] { "hexe", "narr" })
        {
            Assert.Contains(k + ":", figures);
            Assert.Contains("FieldOption(\"" + k + "\"", code);
        }
    }

    [Fact]
    public async Task The_site_carries_the_figure_and_the_route_answers_with_the_settings()
    {
        await ScriptPluginSite.InstallAsync(_app, "narrenfigur", "Narrenfigur", Source("plugin.csx"));
        await ScriptPluginSite.PageAsync(_app, "figur-de", "de");
        var c = _app.Browser();

        var page = await c.GetStringAsync("/figur-de");
        Assert.Contains("/plugin-assets/narrenfigur/figur.js", page);
        Assert.Contains("/plugin-assets/narrenfigur/figur.css", page);

        var cfg = JsonNode.Parse(await c.GetStringAsync("/narrenfigur/config"))!;
        Assert.Equal("hexe", (string?)cfg["figure"]);
        Assert.True((bool)cfg["choose"]!);                  // visitors may switch unless the operator says no
        Assert.Equal("right", (string?)cfg["side"]);      // the declared defaults while nothing was saved
        Assert.False((bool)cfg["homeOnly"]!);
        Assert.True((bool)cfg["phone"]!);
    }
}
