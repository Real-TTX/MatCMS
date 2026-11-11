using System.Text.RegularExpressions;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// Every text key the club plugin's code uses has its German and English text (VereinStrings*.cs) — a missing one would show "verein.xyz" on a live
/// page — and no key is defined twice (the table is split over partial files; a duplicate would make the plugin's Configure throw).
/// </summary>
public class VereinStringsTests
{
    private static readonly Regex Row = new(@"\(""(verein\.[a-zA-Z0-9.]+)"",\s*""((?:[^""\\]|\\.)*)"",\s*""((?:[^""\\]|\\.)*)""\)", RegexOptions.Compiled);

    private static string Dir()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MatCMS.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "plugins", "verein");
    }

    private static List<(string Key, string De, string En)> Table() =>
        Directory.GetFiles(Dir(), "VereinStrings*.cs").SelectMany(f => Row.Matches(File.ReadAllText(f)))
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)).ToList();

    [Fact]
    public void Every_key_the_code_uses_has_both_languages()
    {
        var rows = Table();
        Assert.True(rows.Count > 500, "table rows found: " + rows.Count);
        var keys = rows.Select(r => r.Key).ToHashSet();
        // VereinMail.cs is left out: its "verein.welcome" … are mail TEMPLATE keys (operator-editable rows, German on purpose), not text keys
        var used = Directory.GetFiles(Dir(), "*.cs").Where(f => !Path.GetFileName(f).StartsWith("VereinStrings", StringComparison.Ordinal) && Path.GetFileName(f) != "VereinMail.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"""(verein\.[a-z][a-zA-Z0-9.]*[a-zA-Z0-9])""").Select(m => m.Groups[1].Value))
            .ToHashSet();
        // keys the code puts together at run time (a literal prefix + one of a fixed list)
        foreach (var c in new[] { "title", "by", "event", "size", "date" }) used.Add("verein.pub.col." + c);
        foreach (var k in new[] { "sign", "read", "confirm" }) { used.Add("verein.pub.task." + k); used.Add("verein.pub.task." + k + "By"); }
        foreach (var k in new[] { "left1", "leftN", "done1", "doneN", "pending", "thanks", "bad1", "badN", "error", "failed" }) used.Add("verein.upload.js." + k);
        foreach (var k in new[] { "date", "weekday", "start", "end", "event", "kind", "place", "meetingPoint", "visible", "description" }) used.Add("verein.export.csv." + k);
        var missing = used.Where(k => !keys.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "without text: " + string.Join(", ", missing));
    }

    [Fact]
    public void No_key_twice_and_no_empty_text()
    {
        var rows = Table();
        var twice = rows.GroupBy(r => r.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(twice.Count == 0, "defined twice: " + string.Join(", ", twice));
        var empty = rows.Where(r => r.De.Length == 0 || r.En.Length == 0).Select(r => r.Key).ToList();
        Assert.True(empty.Count == 0, "empty text: " + string.Join(", ", empty));
        Assert.Contains(rows, r => r.Key == "verein.lang" && r.De == "de" && r.En == "en");
    }
}

/// <summary>The club's back office speaks the admin's language: German as before, English when the admin switched to it.</summary>
public class VereinLanguageTests : IClassFixture<VereinApp>
{
    private readonly VereinApp _app;
    public VereinLanguageTests(VereinApp app) => _app = app;

    [Fact]
    public async Task The_admin_pages_follow_the_admin_language()
    {
        await _app.InstallAsync();
        var c = _app.App.Browser();
        await AppFactory.LoginAsync(c);

        await c.PostAsync("/set-language?culture=en", new StringContent(""));
        var en = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/admin/p/verein/dashboard"));
        Assert.Contains("Quick add", en);
        Assert.DoesNotContain("Schnell anlegen", en);
        Assert.DoesNotContain("\"verein.", en);
        // every main page of the section: no raw key and none of the German words the pages used to carry
        foreach (var url in new[] { "members", "members/new", "children/new", "docs", "docs/new", "meetings", "meetings/new", "events", "events/new",
                     "events/export", "events/export?tab=kalender", "events/export?tab=instagram", "uploads", "templates", "calendar", "calendar?typ=geburtstage" })
        {
            var page = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/admin/p/verein/" + url));
            Assert.DoesNotMatch(@"[>""]verein\.[a-z]", page.Replace("group=verein.", ""));
            foreach (var german in new[] { "Speichern", "Mitglied anlegen", "Termine", "Sitzungen", "Dokumente", "Vorlagen", "Zurück" })
                Assert.False(page.Contains(">" + german + "<") || page.Contains("> " + german + "<"), url + " still shows " + german);
        }
        var settings = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/admin/p/verein/_settings?group=verein.set.groupRoles"));
        Assert.Contains("Own fields", settings);
        Assert.DoesNotContain("verein.set.", settings.Replace("group=verein.set.", ""));

        await c.PostAsync("/set-language?culture=de", new StringContent(""));
        var de = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/admin/p/verein/dashboard"));
        Assert.Contains("Schnell anlegen", de);
        Assert.Contains("Eigene Felder", System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/admin/p/verein/_settings?group=verein.set.groupRoles")));
    }
}
