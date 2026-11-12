using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Plugin.Docs;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Docs / Wiki (plugins/docs), a NATIVE plugin loaded from its built DLL into the whole application, on a site that publishes in German and English.</summary>
public sealed class DocsApp : IDisposable
{
    public AppFactory App { get; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _installed;
    private HttpClient? _admin;

    private static string Fixture(string path) => Path.Combine(AppContext.BaseDirectory, "fixtures", path);

    public async Task InstallAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_installed) return;
            using var scope = App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!db.SiteSettings.Any(s => s.Key == SettingKeys.AllowNativePlugins)) db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.AllowNativePlugins, Value = "1" });
            var languages = db.SiteSettings.FirstOrDefault(s => s.Key == SettingKeys.Languages);
            if (languages is null) db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.Languages, Value = "de,en" });
            else languages.Value = "de,en";
            if (!db.Pages.Any(p => p.Locale == "en")) db.Pages.Add(new Page { Title = "Home", Slug = "home", Locale = "en", IsPublished = true });
            var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
            var src = StoragePaths.PluginSrc(env, "docs", "1.0");
            Directory.CreateDirectory(src);
            File.Copy(Fixture("docs/Docs.dll"), Path.Combine(src, "Docs.dll"), overwrite: true);
            var row = new Models.Plugin
            {
                Key = "docs", Name = "Docs / Wiki", Version = "1.0", Kind = PluginKind.Native, Entry = "Docs.dll", Enabled = true,
                Sha256 = NativePluginHost.HashOf(Path.Combine(src, "Docs.dll")), Api = typeof(IPlugin).Assembly.GetName().Version!.Major
            };
            db.Plugins.Add(row);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
            var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
            Assert.True(registry.IsLoaded("docs"), "docs did not load: " + string.Join(" | ", registry.Errors.Values));
            _installed = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<HttpClient> AdminAsync()
    {
        await InstallAsync();
        if (_admin is not null) return _admin;
        var c = App.Browser();
        await AppFactory.LoginAsync(c);
        return _admin = c;
    }

    public async Task<HttpClient> ApiAsync(params string[] rights)
    {
        await InstallAsync();
        using var scope = App.Services.CreateScope();
        var (_, key) = await scope.ServiceProvider.GetRequiredService<SiteApiKeyService>().CreateAsync("Docs Test " + Guid.NewGuid().ToString("N")[..6], rights, null);
        var c = App.Browser();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return c;
    }

    public void Dispose() => App.Dispose();
}

public class DocsTests : IClassFixture<DocsApp>
{
    private readonly DocsApp _f;
    public DocsTests(DocsApp f) => _f = f;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
    private static async Task<JsonNode> JsonOf(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    /// <summary>Writes pages through the API (one call, top down) and checks every one was taken.</summary>
    private async Task PutAsync(string json)
    {
        var api = await _f.ApiAsync("docs:write");
        var res = await api.PutAsync("/api/v1/docs/pages", Body(json));
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK, text);
        Assert.True(JsonNode.Parse(text)!["ok"]!.GetValue<bool>(), text);
    }

    [Fact]
    public async Task A_fresh_docs_site_answers_with_an_empty_start_page_in_both_languages()
    {
        await _f.InstallAsync();
        var guest = _f.App.Browser();
        var de = await guest.GetAsync("/docs");
        Assert.Equal(HttpStatusCode.OK, de.StatusCode);
        var deHtml = Decode(await de.Content.ReadAsStringAsync());
        Assert.Contains("Dokumentation", deHtml);
        Assert.Contains("action=\"/docs/suche\"", deHtml);

        var en = Decode(await guest.GetStringAsync("/en/docs"));
        Assert.Contains("Documentation", en);
        Assert.Contains("action=\"/en/docs/search\"", en);
        Assert.DoesNotContain("docs.", Regex.Replace(en, "docs\\.js|class=\"[^\"]*\"|/docs[^\"]*|--docs-[a-z-]+", ""));
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/fr/docs")).StatusCode);
    }

    [Fact]
    public async Task Pages_written_through_the_API_form_a_tree_with_navigation_contents_and_previous_next()
    {
        await PutAsync("""
            {"pages":[
              {"path":"erste-schritte","title":"Erste Schritte","summary":"Von null zum laufenden System.","icon":"rocket","sort":10,"body":"Willkommen."},
              {"path":"erste-schritte/installation","title":"Installation","sort":10,"body":"## Voraussetzungen\n\nDocker.\n\n## Start\n\n```bash\ndocker compose up -d\n```\n\n> [!WARNING]\n> Erst sichern.\n\nWeiter mit [Konfiguration](doc:erste-schritte/konfiguration)."},
              {"path":"erste-schritte/konfiguration","title":"Konfiguration","sort":20,"body":"| Name | Wert |\n|---|---|\n| port | 9101 |"},
              {"path":"referenz","title":"Referenz","sort":20,"body":"Alles im Detail."}
            ]}
            """);
        var guest = _f.App.Browser();
        var home = Decode(await guest.GetStringAsync("/docs"));
        Assert.Contains("href=\"/docs/erste-schritte\"", home);
        Assert.Contains("Von null zum laufenden System.", home);
        Assert.Contains("ti-rocket", home);

        var page = Decode(await guest.GetStringAsync("/docs/erste-schritte/installation"));
        Assert.Contains("<h1>Installation</h1>", page);
        Assert.Contains("<h2 id=\"voraussetzungen\">", page);
        Assert.Contains("href=\"#voraussetzungen\"", page);                                  // the table of contents
        Assert.Contains("class=\"language-bash\">docker compose up -d</code>", page);
        Assert.Contains("docs-callout warning", page);
        Assert.Contains("href=\"/docs/erste-schritte/konfiguration\">Konfiguration</a>", page);    // a doc: link, resolved
        Assert.Contains("aria-current=\"page\">Installation", page);                         // marked in the sidebar
        Assert.Contains("class=\"prev\" href=\"/docs/erste-schritte\"", page);
        Assert.Contains("class=\"next\" href=\"/docs/erste-schritte/konfiguration\"", page);
        Assert.Matches("docs-crumbs.*href=\"/docs\".*href=\"/docs/erste-schritte\">Erste Schritte", page.Replace("\n", " "));

        var config = Decode(await guest.GetStringAsync("/docs/erste-schritte/konfiguration"));
        Assert.Contains("<th>Name</th>", config);
        Assert.Contains("<td>9101</td>", config);
        var section = Decode(await guest.GetStringAsync("/docs/erste-schritte"));
        Assert.Contains("In diesem Abschnitt", section);                                       // its sub-pages listed below it
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/docs/erste-schritte/gibt-es-nicht")).StatusCode);

        // The parent must exist first; a path that would be an address of the docs themselves is refused.
        var api = await _f.ApiAsync("docs:write");
        Assert.Equal(HttpStatusCode.Conflict, (await api.PutAsync("/api/v1/docs/pages", Body("""{"path":"fehlt/kind","title":"Kind"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/v1/docs/pages", Body("""{"path":"suche","title":"Suche"}"""))).StatusCode);
        // A key with read only may not write.
        var reader = await _f.ApiAsync("docs:read");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsync("/api/v1/docs/pages", Body("""{"path":"x","title":"X"}"""))).StatusCode);
        var list = await JsonOf(await reader.GetAsync("/api/v1/docs/pages?locale=de"));
        Assert.Contains(list["pages"]!.AsArray(), p => p!["path"]!.GetValue<string>() == "erste-schritte/installation");
    }

    [Fact]
    public async Task What_an_author_types_never_becomes_markup()
    {
        await PutAsync("""{"path":"sicherheit","title":"<b>Sicher</b>","body":"<script>alert(1)</script>\n\n[klick](javascript:alert(2)) ![x](data:image/png;base64,AAAA) <img src=x onerror=alert(3)>"}""");
        var page = await _f.App.Browser().GetStringAsync("/docs/sicherheit");
        Assert.DoesNotContain("<script>alert", page);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page);
        Assert.DoesNotContain("javascript:alert", page);
        Assert.DoesNotContain("src=\"data:", page);
        Assert.DoesNotContain("<img src=x", page);
        Assert.Contains("&lt;b&gt;Sicher&lt;/b&gt;", page);
    }

    [Fact]
    public async Task Drafts_are_for_administrators_only()
    {
        await PutAsync("""{"pages":[{"path":"intern","title":"Intern","published":false,"body":"Geheimer Plan."},{"path":"intern/kind","title":"Kind","body":"Darunter."}]}""");
        var guest = _f.App.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/docs/intern")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/docs/intern/kind")).StatusCode);     // below a draft = a draft
        Assert.DoesNotContain("Geheimer Plan", await guest.GetStringAsync("/docs/suche?q=Geheimer"));
        var admin = await _f.AdminAsync();
        var page = Decode(await admin.GetStringAsync("/docs/intern"));
        Assert.Contains("Geheimer Plan.", page);
        Assert.Contains("Entwurf – nur für Administratoren sichtbar.", page);
    }

    [Fact]
    public async Task The_search_finds_pages_by_title_and_text_and_marks_the_terms()
    {
        await PutAsync("""{"pages":[{"path":"suchtest","title":"Sensoren anlernen","body":"Ein Funksensor wird mit der Taste am Gateway angelernt."}]}""");
        var guest = _f.App.Browser();
        var hits = Decode(await guest.GetStringAsync("/docs/suche?q=gateway%20taste"));
        Assert.Contains("href=\"/docs/suchtest\"", hits);
        Assert.Contains("<mark>Taste</mark>", hits);
        Assert.Contains("1 Treffer", hits);
        Assert.Contains("Keine Treffer", Decode(await guest.GetStringAsync("/docs/suche?q=quantenschaum")));
        Assert.Contains("noindex", await guest.GetStringAsync("/docs/suche?q=x"));

        // The site-wide search event (the visitors' chat bot) gets the same pages.
        using var scope = _f.App.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var events = (IPluginEvents)new PluginEventBus(sp.GetRequiredService<PluginRegistry>(), sp, new PluginUser(), "de");
        var answers = await events.CollectAsync("site.search", new JsonObject { ["q"] = "Funksensor", ["max"] = 3, ["locale"] = "de" });
        Assert.Contains(answers, a => a["items"]!.AsArray().Any(i => i!["url"]!.GetValue<string>() == "/docs/suchtest"));
    }

    [Fact]
    public async Task An_English_translation_lives_under_en_and_is_linked_to_its_original()
    {
        await PutAsync("""
            {"pages":[
              {"path":"handbuch","title":"Handbuch","body":"Deutsch."},
              {"path":"manual","title":"Manual","locale":"en","translationOf":"handbuch","body":"## Getting started\n\nEnglish.\n\n## More\n\nText."}
            ]}
            """);
        var guest = _f.App.Browser();
        var en = Decode(await guest.GetStringAsync("/en/docs/manual"));
        Assert.Contains("<h1>Manual</h1>", en);
        Assert.Contains("On this page", en);
        Assert.Contains("href=\"/en/docs\"", en);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/docs/manual")).StatusCode);   // the German docs have no such page
        // the German page's language switch leads to the translation
        Assert.Contains("/en/docs/manual", await guest.GetStringAsync("/docs/handbuch"));
    }

    [Fact]
    public async Task The_back_office_lists_edits_and_speaks_both_languages()
    {
        var admin = await _f.AdminAsync();
        await admin.PostAsync("/set-language?culture=de", new StringContent(""));
        var list = await admin.GetStringAsync("/admin/p/docs/pages");
        var token = AppFactory.Token(list);
        Assert.NotNull(token);

        // a new section through the editor
        var form = await admin.GetStringAsync("/admin/p/docs/pages/0?parent=0&lang=de");
        var res = await admin.PostAsync("/admin/p/docs/pages/0", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AppFactory.Token(form)!, ["action"] = "save", ["title"] = "Über das Admin", ["slug"] = "", ["summary"] = "",
            ["body"] = "## Eins\n\nText", ["format"] = "md", ["parent"] = "0", ["sort"] = "", ["icon"] = "ti-settings", ["published"] = "on", ["lang"] = "de", ["v"] = ""
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Matches("^/admin/p/docs/pages/\\d+$", res.Headers.Location!.ToString());
        var page = Decode(await _f.App.Browser().GetStringAsync("/docs/ueber-das-admin"));
        Assert.Contains("<h1>Über das Admin</h1>", page);

        var editor = Decode(await admin.GetStringAsync(res.Headers.Location!.ToString()));
        Assert.Contains("Übersetzung anlegen (EN)", editor);
        Assert.DoesNotMatch("[>\"]docs\\.[a-z]", editor.Replace("group=docs.", ""));

        await admin.PostAsync("/set-language?culture=en", new StringContent(""));
        try
        {
            foreach (var url in new[] { "pages", "versions", "pages/0?parent=0" })
            {
                var en = Decode(await admin.GetStringAsync("/admin/p/docs/" + url));
                Assert.DoesNotMatch("[>\"]docs\\.[a-z]", en.Replace("group=docs.", ""));
                foreach (var german in new[] { "Speichern", "Neuer Bereich", "Versionen", "Veröffentlicht" })
                    Assert.False(en.Contains(">" + german + "<"), url + " still shows " + german);
            }
        }
        finally { await admin.PostAsync("/set-language?culture=de", new StringContent("")); }
    }

    [Fact]
    public async Task The_sitemap_and_the_link_picker_know_the_pages()
    {
        await PutAsync("""{"path":"karte","title":"Karte","body":"x"}""");
        var guest = _f.App.Browser();
        var sitemap = await guest.GetStringAsync("/sitemap.xml");
        Assert.Contains("/docs/karte", sitemap);
        var admin = await _f.AdminAsync();
        var targets = await admin.GetStringAsync("/admin/api/link-targets");
        Assert.Contains("/docs/karte", targets);
    }
}

/// <summary>Versions in their own application: the first version takes over every page, a copy keeps the tree, the default lives at /docs.</summary>
public class DocsVersionTests : IClassFixture<DocsApp>
{
    private readonly DocsApp _f;
    public DocsVersionTests(DocsApp f) => _f = f;

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string url, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = AppFactory.Token(await c.GetStringAsync(url))!;
        return await c.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task Versions_keep_their_own_pages_and_the_default_has_the_plain_addresses()
    {
        var api = await _f.ApiAsync("docs:write");
        var put = await api.PutAsync("/api/v1/docs/pages", new StringContent("""{"pages":[{"path":"api","title":"API","body":"Alte API."}]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var admin = await _f.AdminAsync();

        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, "/admin/p/docs/versions", new() { ["action"] = "add", ["name"] = "1.0", ["label"] = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, "/admin/p/docs/versions",
            new() { ["action"] = "add", ["name"] = "2.0", ["label"] = "2.0", ["copyFrom"] = "1.0", ["makeDefault"] = "on" })).StatusCode);

        // change the copy in 2.0 only
        var write = await api.PutAsync("/api/v1/docs/pages", new StringContent("""{"path":"api","version":"2.0","title":"API","body":"Neue API."}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);

        var guest = _f.App.Browser();
        var current = WebUtility.HtmlDecode(await guest.GetStringAsync("/docs/api"));
        Assert.Contains("Neue API.", current);
        Assert.Contains("docs-versions", current);
        var old = WebUtility.HtmlDecode(await guest.GetStringAsync("/docs/v/1.0/api"));
        Assert.Contains("Alte API.", old);
        Assert.Contains("Version 1.0", old);                                    // "you are reading the documentation for version 1.0"
        Assert.Contains("noindex", old);
        var redirect = await guest.GetAsync("/docs/v/2.0/api");
        Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        Assert.Equal("/docs/api", redirect.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/docs/v/9.9/api")).StatusCode);
    }
}

/// <summary>The Markdown of the docs on its own: what it draws and what it never lets through.</summary>
public class DocsMarkdownTests
{
    private static string H(string md) => DocsMarkdown.Render(md).Html;

    [Fact]
    public void Headings_get_unique_ids_and_the_second_and_third_level_make_the_contents()
    {
        var r = DocsMarkdown.Render("# Titel\n\n## Über uns\n\ntext\n\n### Details ###\n\n## Über uns\n\n#### tief");
        Assert.Contains("<h2 id=\"ueber-uns\">Über uns", r.Html);
        Assert.Contains("<h2 id=\"ueber-uns-2\">", r.Html);
        Assert.Contains("<h3 id=\"details\">Details<a", r.Html);
        Assert.Equal(new[] { "ueber-uns", "details", "ueber-uns-2" }, r.Toc.Select(t => t.Id));
    }

    [Fact]
    public void Inline_formatting_links_and_code_spans()
    {
        var html = H("Das ist **fett**, *kursiv*, ~~weg~~ und `<code> & **nicht fett**`. Siehe [Seite](https://example.org \"Titel\") und https://matmon.eu/x.");
        Assert.Contains("<strong>fett</strong>", html);
        Assert.Contains("<em>kursiv</em>", html);
        Assert.Contains("<del>weg</del>", html);
        Assert.Contains("<code>&lt;code&gt; &amp; **nicht fett**</code>", html);
        Assert.Contains("<a href=\"https://example.org\" title=\"Titel\" rel=\"noopener\">Seite</a>", html);
        Assert.Contains("<a href=\"https://matmon.eu/x\" rel=\"noopener\">https://matmon.eu/x</a>.", html);
        Assert.Contains("snake_case_name", H("snake_case_name"));
    }

    [Fact]
    public void Lists_nest_and_tasks_are_boxes()
    {
        var html = H("- eins\n- zwei\n  - zwei a\n  - zwei b\n- drei\n\n1. erst\n2. dann\n\n- [x] erledigt\n- [ ] offen");
        Assert.Contains("<ul><li>eins</li><li>zwei<ul><li>zwei a</li><li>zwei b</li></ul></li><li>drei</li></ul>", html);
        Assert.Contains("<ol><li>erst</li><li>dann</li></ol>", html);
        Assert.Contains("<li class=\"task\"><input type=\"checkbox\" disabled checked> erledigt</li>", html);
    }

    [Fact]
    public void Code_blocks_keep_their_text_and_tables_their_alignment()
    {
        var html = H("```js\nif (a < b && c) { x = \"<b>\"; }\n```\n\n| L | R |\n|:--|--:|\n| a \\| b | `x|y` |");
        Assert.Contains("<code class=\"language-js\">if (a &lt; b &amp;&amp; c) { x = &quot;&lt;b&gt;&quot;; }</code>", html);
        Assert.Contains("<th style=\"text-align:left\">L</th><th style=\"text-align:right\">R</th>", html);
        Assert.Contains("<td style=\"text-align:left\">a | b</td><td style=\"text-align:right\"><code>x|y</code></td>", html);
    }

    [Fact]
    public void Alerts_and_quotes()
    {
        var html = DocsMarkdown.Render("> [!TIP]\n> Erst **lesen**.\n\n> Ein Zitat.", new DocsMarkdown.Options { AlertTitle = k => k == "tip" ? "Tipp" : k }).Html;
        Assert.Contains("<div class=\"docs-callout tip\" role=\"note\"><p class=\"docs-callout-title\">Tipp</p><p>Erst <strong>lesen</strong>.</p></div>", html);
        Assert.Contains("<blockquote><p>Ein Zitat.</p></blockquote>", html);
    }

    [Fact]
    public void Nothing_unsafe_gets_through()
    {
        var html = H("<script>alert(1)</script>\n\n[a](javascript:alert(1)) [b](JAVASCRIPT:x) [c](//evil.example) [d](data:text/html,x) ![e](vbscript:x)\n\n<a href=\"x\" onclick=\"y\">z</a>");
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("href=\"javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"//", html);
        Assert.DoesNotContain("data:text", html.Replace("&lt;", ""), StringComparison.Ordinal);   // the text "data:" may remain only as plain text …
        Assert.DoesNotContain("href=\"data:", html);                                              // … never as an address
        Assert.DoesNotContain("<a href=\"x\"", html);
        Assert.Contains("&lt;a href=&quot;x&quot; onclick=&quot;y&quot;&gt;", html);
        Assert.True(DocsMarkdown.SafeUrl("/uploads/a.png") && DocsMarkdown.SafeUrl("#x") && DocsMarkdown.SafeUrl("mailto:a@b.c") && DocsMarkdown.SafeUrl("seite/unter"));
        Assert.False(DocsMarkdown.SafeUrl(" javascript:x") || DocsMarkdown.SafeUrl("java\tscript:x") || DocsMarkdown.SafeUrl("ftp://x"));
    }

    [Fact]
    public void Doc_links_are_resolved_and_a_missing_page_is_marked()
    {
        var o = new DocsMarkdown.Options { ResolveDoc = p => p == "a/b" ? "/docs/a/b" : null };
        var html = DocsMarkdown.Render("[B](doc:a/b#teil) und [weg](doc:x/y)", o).Html;
        Assert.Contains("<a href=\"/docs/a/b#teil\">B</a>", html);
        Assert.Contains("<span class=\"docs-broken\">weg</span>", html);
    }

    [Fact]
    public void Rich_text_headings_get_ids_for_the_contents()
    {
        var r = DocsMarkdown.FromHtml("<p>x</p><h2 id=\"alt\">Erster Teil</h2><p>y</p><h3>Unterpunkt <em>a</em></h3>");
        Assert.Contains("<h2 id=\"erster-teil\">Erster Teil</h2>", r.Html);
        Assert.Equal(new[] { "erster-teil", "unterpunkt-a" }, r.Toc.Select(t => t.Id));
    }

    [Fact]
    public void A_tree_with_a_lost_parent_or_a_loop_keeps_every_page()
    {
        var pages = new[]
        {
            new DocPage { Id = 1, Title = "A", Slug = "a", Sort = 10 },
            new DocPage { Id = 2, ParentId = 1, Title = "A1", Slug = "a1" },
            new DocPage { Id = 3, ParentId = 99, Title = "Lost", Slug = "lost", Sort = 20 },
            new DocPage { Id = 4, ParentId = 5, Title = "L1", Slug = "l1" },
            new DocPage { Id = 5, ParentId = 4, Title = "L2", Slug = "l2" }
        };
        var tree = DocTree.Build(pages);
        Assert.Equal(5, tree.Flat.Count);
        Assert.Equal("a/a1", tree.Find(2)!.Path);
        Assert.Equal("lost", tree.Find(3)!.Path);
        Assert.NotNull(tree.ByPath(new[] { "A", "A1" }));
    }
}

/// <summary>Every text key the docs plugin uses has its German and English text, and no key is there twice.</summary>
public class DocsStringsTests
{
    private static string Dir()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MatCMS.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "plugins", "docs");
    }

    [Fact]
    public void Every_key_the_code_uses_has_both_languages()
    {
        var rows = DocsStrings.All;
        var keys = rows.Select(r => r.Key).ToHashSet();
        var used = Directory.GetFiles(Dir(), "*.cs").Where(f => Path.GetFileName(f) != "DocsStrings.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"""(docs\.[a-z][a-zA-Z0-9.]*[a-zA-Z0-9])""").Select(m => m.Groups[1].Value))
            .Where(k => k != "docs.js")                                                    // the script's file name, not a text
            .ToHashSet();
        foreach (var kind in new[] { "note", "tip", "important", "warning", "caution" }) used.Add("docs.alert." + kind);
        var missing = used.Where(k => !keys.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "without text: " + string.Join(", ", missing));
    }

    [Fact]
    public void No_key_twice_and_no_empty_text()
    {
        var rows = DocsStrings.All;
        var twice = rows.GroupBy(r => r.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(twice.Count == 0, "defined twice: " + string.Join(", ", twice));
        var empty = rows.Where(r => r.De.Length == 0 || r.En.Length == 0).Select(r => r.Key).ToList();
        Assert.True(empty.Count == 0, "empty text: " + string.Join(", ", empty));
        // every placeholder of the template parts is named in both languages (not just its own name)
        Assert.All(rows.Where(r => r.Key.StartsWith("docs.ph.", StringComparison.Ordinal)), r => Assert.NotEqual(r.Key[8..], r.De));
    }
}
