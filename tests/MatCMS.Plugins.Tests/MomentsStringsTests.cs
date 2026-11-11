using System.Text.RegularExpressions;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// Every text key the code of Moments (plugins/moments) uses has its German and English text (MomentsStrings) — a missing one would show "moments.xyz"
/// on a live page. Keys read through <c>M.Count</c> (a prefix ending in a dot) need both forms, ".1" and ".n".
/// </summary>
public class MomentsStringsTests
{
    private static string Dir()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MatCMS.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "plugins", "moments");
    }

    private static HashSet<string> Rows(string dir)
    {
        var table = File.ReadAllText(Path.Combine(dir, "MomentsStrings.cs"));
        // a row may wrap after the German text
        return Regex.Matches(table, @"\(""(moments\.[a-zA-Z0-9.]+)"",\s*""[^""]+"",\s*""[^""]+""\)").Select(m => m.Groups[1].Value).ToHashSet();
    }

    private static IEnumerable<string> Code(string dir) =>
        Directory.GetFiles(dir, "*.cs").Where(f => !f.EndsWith("MomentsStrings.cs")).Select(File.ReadAllText);

    [Fact]
    public void Every_key_the_code_uses_has_both_languages()
    {
        var dir = Dir();
        var rows = Rows(dir);
        var missing = Code(dir).SelectMany(t => Regex.Matches(t, @"""(moments\.[a-z][a-zA-Z0-9.]*[a-zA-Z0-9])""").Select(m => m.Groups[1].Value))
            .Where(k => !rows.Contains(k)).Distinct().ToList();
        Assert.True(missing.Count == 0, "without text: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_counted_key_has_one_and_many()
    {
        var dir = Dir();
        var rows = Rows(dir);
        // the prefixes handed to M.Count ("moments.count.photo."); month and day names are numbered, not counted
        var missing = Code(dir).SelectMany(t => Regex.Matches(t, @"M\.Count\([^;]*?""(moments\.[a-zA-Z0-9.]+\.)""").Select(m => m.Groups[1].Value))
            .Distinct().SelectMany(p => new[] { p + "1", p + "n" }).Where(k => !rows.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "without text: " + string.Join(", ", missing));
        for (var i = 1; i <= 12; i++) Assert.Contains("moments.month." + i, rows);
        for (var i = 0; i <= 6; i++) { Assert.Contains("moments.day." + i, rows); Assert.Contains("moments.dayShort." + i, rows); }
    }
}

/// <summary>The back office of Moments speaks the admin's language: German as before, English when the admin switched to it.</summary>
public class MomentsLanguageTests : IClassFixture<MomentsApp>
{
    private readonly MomentsApp _app;
    public MomentsLanguageTests(MomentsApp app) => _app = app;

    [Fact]
    public async Task The_admin_pages_follow_the_admin_language()
    {
        await _app.InstallAsync();
        var c = _app.App.Browser();
        await AppFactory.LoginAsync(c);

        await c.PostAsync("/set-language?culture=en", new StringContent(""));
        var en = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/admin/p/moments/overview"));
        Assert.Contains("Open guest area", en);
        Assert.DoesNotContain("Gästebereich öffnen", en);
        var settings = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/admin/p/moments/_settings?group=moments.set.groupGeneral"));
        Assert.Contains("Name of the occasion", settings);
        Assert.DoesNotContain("moments.set.", settings.Replace("group=moments.set.", ""));

        await c.PostAsync("/set-language?culture=de", new StringContent(""));
        var de = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/admin/p/moments/overview"));
        Assert.Contains("Gästebereich öffnen", de);
        Assert.Contains("Name des Anlasses", System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/admin/p/moments/_settings?group=moments.set.groupGeneral")));
    }
}
