using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// Every MatCMS plugin speaks German AND English. For the store's script plugins: every text key the code uses has both texts — a missing
/// one shows "ls.xyz" on a live page. Two shapes of table: rows <c>("key", "German", "English")</c> (cookie-consent, leserstimmen) and two
/// dictionaries <c>De</c>/<c>En</c> (coverage-map, store-einblick).
/// </summary>
public class ScriptPluginStringsTests
{
    private static string Plugins()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MatCMS.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "plugins");
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(Plugins(), file));

    private static Dictionary<string, (string De, string En)> Rows(string code, string prefix) =>
        Regex.Matches(code, "\\(\"(" + prefix + "\\.[a-zA-Z0-9.]+)\",\\s*\"([^\"]*)\",\\s*\"([^\"]*)\"\\)")
            .GroupBy(m => m.Groups[1].Value)     // a call with three strings (AddAdminMenu("cc.nav", url, icon)) matches too; the row comes first
            .ToDictionary(g => g.Key, g => (g.First().Groups[2].Value, g.First().Groups[3].Value));

    private static IEnumerable<string> Used(string code, string prefix) =>
        Regex.Matches(code, "\"(" + prefix + "\\.[a-zA-Z][a-zA-Z0-9.]*[a-zA-Z0-9])\"").Select(m => m.Groups[1].Value).Distinct();

    private static void AssertRows(string code, string prefix, params string[] alsoComputed)
    {
        var rows = Rows(code, prefix);
        Assert.True(rows.Count > 20, "no text rows found for " + prefix);
        var empty = rows.Where(r => r.Value.De.Length == 0 || r.Value.En.Length == 0).Select(r => r.Key).ToList();
        Assert.True(empty.Count == 0, "a language missing: " + string.Join(", ", empty));
        // A key used for the form of address ("ls.thanks") stands for its ".sie" and ".du" rows; a computed one ("ls.nav.more") is set
        // in both tables by hand.
        bool Assigned(string k) => code.Contains("De[\"" + k + "\"] =") && code.Contains("En[\"" + k + "\"] =");
        var missing = Used(code, prefix).Concat(alsoComputed)
            .Where(k => !rows.ContainsKey(k) && !(rows.ContainsKey(k + ".sie") && rows.ContainsKey(k + ".du")) && !Assigned(k)).ToList();
        Assert.True(missing.Count == 0, "without text: " + string.Join(", ", missing));
    }

    private static void AssertTables(string code, string prefix, params string[] alsoComputed)
    {
        var at = code.IndexOf("En = new()", StringComparison.Ordinal);
        Assert.True(at > 0, "no English table");
        HashSet<string> Keys(string part) => Regex.Matches(part, "\\[\"(" + prefix + "\\.[^\"]+)\"\\]\\s*=\\s*\"").Select(m => m.Groups[1].Value).ToHashSet();
        var de = Keys(code[..at]);
        var en = Keys(code[at..]);
        Assert.True(de.Count > 10, "no German table");
        Assert.Empty(de.Except(en));
        Assert.Empty(en.Except(de));
        var missing = Used(code, prefix).Concat(alsoComputed).Where(k => !de.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "without text: " + string.Join(", ", missing));
    }

    [Fact]
    public void Cookie_banner_has_every_text_in_both_languages() =>
        AssertRows(Read("cookie-consent/plugin.csx"), "cc",
            // "cc.v." + setting name
            "cc.v.title", "cc.v.text", "cc.v.accept", "cc.v.reject", "cc.v.settings", "cc.v.save", "cc.v.reopenLabel", "cc.v.privacyLabel",
            "cc.v.imprintLabel", "cc.v.necessaryLabel", "cc.v.necessaryDesc", "cc.v.phText", "cc.v.phHost", "cc.v.phOnce", "cc.v.phAlways",
            "cc.v.cat.statistik.label", "cc.v.cat.statistik.desc", "cc.v.cat.marketing.label", "cc.v.cat.marketing.desc",
            "cc.v.cat.medien.label", "cc.v.cat.medien.desc");

    [Fact]
    public void Leserstimmen_has_every_text_in_both_languages() =>
        AssertRows(Read("leserstimmen/plugin.csx"), "ls",
            // the symbol nouns are "ls.n." + symbol + ".1|.n"
            new[] { "stern", "herz", "punkt", "flamme" }.SelectMany(s => new[] { $"ls.n.{s}.1", $"ls.n.{s}.n" }).ToArray());

    [Fact]
    public void Coverage_map_has_every_text_in_both_languages() =>
        AssertTables(Read("coverage-map/coverage.csx"), "cov", "cov.s.available", "cov.s.partial", "cov.s.soon", "cov.s.planned");

    [Fact]
    public void Store_showcase_has_every_text_in_both_languages() =>
        AssertTables(Read("store-einblick/showcase.csx"), "se");
}

/// <summary>Shared set-up of the two language tests below: a site with German and English pages and a script plugin from plugins/.</summary>
internal static class ScriptPluginSite
{
    public static string Fixture(string path) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", path));

    public static async Task InstallAsync(AppFactory app, string key, string name, string code)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var languages = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.Languages);
        if (languages is null) db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.Languages, Value = "de,en" });
        else languages.Value = "de,en";
        var row = await db.Plugins.FirstOrDefaultAsync(p => p.Key == key);
        if (row is null) db.Plugins.Add(row = new Models.Plugin { Key = key, Name = name, Version = "1.0", Kind = PluginKind.Script, Enabled = true, Code = code });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
        var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
        Assert.True(registry.IsLoaded(key), key + " did not load: " + string.Join(" | ", registry.Errors.Values));
    }

    public static async Task SettingAsync(AppFactory app, string key, string value)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync();
    }

    public static async Task PageAsync(AppFactory app, string slug, string locale, string blockType = "", string data = "{}")
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Pages.AnyAsync(p => p.Slug == slug && p.Locale == locale)) return;
        var page = new Page { Title = slug, Slug = slug, Locale = locale, IsPublished = true };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        if (blockType.Length > 0)
        {
            db.ContentBlocks.Add(new ContentBlock { PageId = page.Id, BlockType = blockType, DataJson = data });
            await db.SaveChangesAsync();
        }
    }
}

/// <summary>The cookie banner (plugins/cookie-consent, a script on the older PluginContext API) in German and English.</summary>
public class CookieConsentLanguageTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public CookieConsentLanguageTests(AppFactory app) => _app = app;

    private async Task InstallAsync(JsonObject config)
    {
        await ScriptPluginSite.SettingAsync(_app, "plugin.cookie-consent.config", config.ToJsonString());
        await ScriptPluginSite.InstallAsync(_app, "cookie-consent", "Cookie-Banner", ScriptPluginSite.Fixture("cookie-consent/plugin.csx"));
        await ScriptPluginSite.PageAsync(_app, "cookies-de", "de");
        await ScriptPluginSite.PageAsync(_app, "cookies-en", "en");
    }

    private static JsonObject StatisticsOn(string? title = null)
    {
        var cfg = new JsonObject { ["cats"] = new JsonObject { ["statistik"] = new JsonObject { ["on"] = true } } };
        if (title is not null) cfg["title"] = title;
        return cfg;
    }

    [Fact]
    public async Task A_default_text_is_drawn_in_German_and_carries_the_English_one_for_English_pages()
    {
        await InstallAsync(StatisticsOn());
        var de = await _app.Browser().GetStringAsync("/cookies-de");
        // a German site looks as before
        Assert.Contains("data-mcc-t='title'>Datenschutz-Einstellungen</p>", de);
        Assert.Contains("data-mcc='accept' data-mcc-t='accept'>Alle akzeptieren</button>", de);
        // and its script has the English texts for a page under /en/
        Assert.Contains("\"en\":{", de);
        Assert.Contains("\"title\":\"Privacy settings\"", de);
        Assert.Contains("\"cat.statistik.label\":\"Statistics\"", de);
        Assert.Contains("\"phOnce\":\"Load content\"", de);

        var en = await _app.Browser().GetStringAsync("/en/cookies-en");
        Assert.Contains("lang=\"en\"", en);                               // what the banner's script reads
        Assert.Contains("\"accept\":\"Accept all\"", en);
    }

    [Fact]
    public async Task A_text_the_operator_wrote_stays_as_written_in_every_language()
    {
        await InstallAsync(StatisticsOn("Unsere Cookie-Regeln"));
        var html = await _app.Browser().GetStringAsync("/cookies-de");
        Assert.Contains("data-mcc-t='title'>Unsere Cookie-Regeln</p>", html);
        Assert.Equal(2, Regex.Matches(html, "\"title\":\"Unsere Cookie-Regeln\"").Count);          // the German table and the English one
        Assert.DoesNotContain("Privacy settings", html);
        Assert.Contains("\"accept\":\"Accept all\"", html);                                          // the texts left at their default still switch
    }

    [Fact]
    public async Task The_admin_page_follows_the_admin_language()
    {
        await InstallAsync(StatisticsOn());
        var c = _app.Browser();
        await AppFactory.LoginAsync(c);

        await c.PostAsync("/set-language?culture=en", new StringContent(""));
        var en = WebUtility.HtmlDecode(await c.GetStringAsync("/admin/plugin/cookie-consent"));
        Assert.Contains("Categories", en);
        Assert.Contains("Ask all visitors again", en);
        Assert.Contains("value='Privacy settings'", en);                   // a default text is shown (and saved) in the admin's language
        Assert.Contains("Cookie banner", en);                              // the menu entry
        Assert.DoesNotContain("Kategorien", en);
        Assert.DoesNotMatch("(?<![A-Za-z0-9])cc\\.[a-z]", Regex.Replace(en, "<(script|style)[\\s\\S]*?</\\1>", ""));     // no raw key

        await c.PostAsync("/set-language?culture=de", new StringContent(""));
        var de = WebUtility.HtmlDecode(await c.GetStringAsync("/admin/plugin/cookie-consent"));
        Assert.Contains("Kategorien", de);
        Assert.Contains("value='Datenschutz-Einstellungen'", de);
        Assert.Contains("Alle Besucher erneut fragen", de);
    }
}

/// <summary>Leserstimmen (plugins/leserstimmen, a script on the older PluginContext API): the block follows the page's language, the back
/// office the admin's.</summary>
public class LeserstimmenLanguageTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public LeserstimmenLanguageTests(AppFactory app) => _app = app;

    private async Task InstallAsync()
    {
        await ScriptPluginSite.SettingAsync(_app, "plugin.leserstimmen.default", new JsonArray(
            new JsonObject { ["id"] = 1, ["name"] = "Anna", ["text"] = "Sehr schön.", ["rating"] = 4, ["email"] = "", ["link"] = "", ["date"] = "2026-10-05T10:00:00Z", ["approved"] = true },
            new JsonObject { ["id"] = 2, ["name"] = "Ben", ["text"] = "Wartet.", ["rating"] = 5, ["email"] = "", ["link"] = "", ["date"] = "2026-10-06T10:00:00Z", ["approved"] = false }
        ).ToJsonString());
        await ScriptPluginSite.InstallAsync(_app, "leserstimmen", "Leserstimmen", ScriptPluginSite.Fixture("leserstimmen/plugin.csx"));
        await ScriptPluginSite.PageAsync(_app, "stimmen", "de", "leserstimmen");
        await ScriptPluginSite.PageAsync(_app, "reviews", "en", "leserstimmen");
    }

    [Fact]
    public async Task The_block_speaks_the_language_of_the_page()
    {
        await InstallAsync();
        var de = WebUtility.HtmlDecode(await _app.Browser().GetStringAsync("/stimmen"));
        Assert.Contains("<strong>4,0</strong> von 5 · 1 Stimme", de);
        Assert.Contains("Oktober 2026", de);
        Assert.Contains("Ihre Bewertung abgeben", de);
        Assert.Contains("<legend>Ihre Sterne</legend>", de);
        Assert.Contains("Bewertung absenden", de);

        var en = WebUtility.HtmlDecode(await _app.Browser().GetStringAsync("/en/reviews"));
        Assert.Contains("<strong>4.0</strong> out of 5 · 1 review", en);
        Assert.Contains("October 2026", en);
        Assert.Contains("Submit your review", en);
        Assert.Contains("<legend>Your stars</legend>", en);
        Assert.Contains(">Submit review</button>", en);
        Assert.DoesNotContain("Ihre Bewertung", en);
        Assert.DoesNotMatch("(?<![A-Za-z0-9])ls\\.[a-z]", Regex.Replace(en, "<(script|style)[\\s\\S]*?</\\1>", ""));     // no raw key
    }

    [Fact]
    public async Task The_back_office_and_the_menu_follow_the_admin_language()
    {
        await InstallAsync();
        var c = _app.Browser();
        await AppFactory.LoginAsync(c);

        await c.PostAsync("/set-language?culture=en", new StringContent(""));
        var en = WebUtility.HtmlDecode(await c.GetStringAsync("/admin/plugin/leserstimmen"));
        Assert.Contains("Awaiting approval (1)", en);
        Assert.Contains("Reviews (1)", en);                                // the menu entry with the number waiting
        Assert.Contains("Send test mail", en);
        Assert.DoesNotContain("Wartet auf Freigabe", en);
        Assert.DoesNotMatch("(?<![A-Za-z0-9])ls\\.[a-z]", Regex.Replace(en, "<(script|style)[\\s\\S]*?</\\1>", ""));     // no raw key (title, menu)

        await c.PostAsync("/set-language?culture=de", new StringContent(""));
        var de = WebUtility.HtmlDecode(await c.GetStringAsync("/admin/plugin/leserstimmen"));
        Assert.Contains("Wartet auf Freigabe (1)", de);
        Assert.Contains("Leserstimmen (1)", de);
        Assert.Contains("Test-Mail senden", de);
    }
}
