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

    /// <summary>
    /// Every class the docs write inside their own markup starts with "docs-" — a site template styles generic names (<c>.section</c> got 84 px of padding on
    /// matcms.de and tore the sidebar apart in 1.0; <c>.active</c>, <c>.next</c>, <c>.page</c> are as common). Allowed: the code block's <c>language-x</c> and Tabler's <c>ti</c>.
    /// </summary>
    private static void AssertOwnClassesOnly(string page)
    {
        var start = page.IndexOf("<div class=\"docs\">", StringComparison.Ordinal);
        var end = page.IndexOf("docs.js", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "docs markup not found");
        var foreign = Regex.Matches(page[start..end], "class=\"([^\"]*)\"").SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(c => c != "docs" && !c.StartsWith("docs-", StringComparison.Ordinal) && !c.StartsWith("language-", StringComparison.Ordinal) && c != "ti" && !c.StartsWith("ti-", StringComparison.Ordinal))
            .Distinct().ToList();
        Assert.True(foreign.Count == 0, "classes without docs- prefix: " + string.Join(", ", foreign));
    }

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
        Assert.Contains("docs-callout docs-callout-warning", page);
        Assert.Contains("href=\"/docs/erste-schritte/konfiguration\">Konfiguration</a>", page);    // a doc: link, resolved
        Assert.Contains("aria-current=\"page\">Installation", page);                         // marked in the sidebar
        Assert.Contains("class=\"docs-prev\" href=\"/docs/erste-schritte\"", page);
        Assert.Contains("class=\"docs-next\" href=\"/docs/erste-schritte/konfiguration\"", page);
        Assert.Matches("docs-crumbs.*href=\"/docs\".*href=\"/docs/erste-schritte\">Erste Schritte", page.Replace("\n", " "));
        AssertOwnClassesOnly(page);

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
        Assert.Contains("<li class=\"docs-task\"><input type=\"checkbox\" disabled checked> erledigt</li>", html);
    }

    [Fact]
    public void Code_blocks_keep_their_text_and_tables_their_alignment()
    {
        var html = H("```js\nif (a < b && c) { x = \"<b>\"; }\n```\n\n| L | R |\n|:--|--:|\n| a \\| b | `x|y` |");
        Assert.Contains("<code class=\"language-js\">if (a &lt; b &amp;&amp; c) { x = &quot;&lt;b&gt;&quot;; }</code>", html);
        Assert.Contains("<th style=\"text-align:left\">L</th><th style=\"text-align:right\">R</th>", html);
        Assert.Contains("<td style=\"text-align:left\">a | b</td><td style=\"text-align:right\"><code>x|y</code></td>", html);
        // language and copy button sit in a head row of their own above the code (laid over it, they covered each other — 1.0)
        var withCopy = DocsMarkdown.Render("```yaml\na: 1\n```", new DocsMarkdown.Options { CopyLabel = "Kopieren", CopiedLabel = "Kopiert" }).Html;
        Assert.Matches("<div class=\"docs-code docs-code-has-head\"><div class=\"docs-code-head\"><span class=\"docs-code-lang\">yaml</span><button [^>]*class=\"docs-copy\"[^>]*>Kopieren</button></div><pre>", withCopy);
    }

    [Fact]
    public void Alerts_and_quotes()
    {
        var html = DocsMarkdown.Render("> [!TIP]\n> Erst **lesen**.\n\n> Ein Zitat.", new DocsMarkdown.Options { AlertTitle = k => k == "tip" ? "Tipp" : k }).Html;
        Assert.Contains("<div class=\"docs-callout docs-callout-tip\" role=\"note\"><p class=\"docs-callout-title\">Tipp</p><p>Erst <strong>lesen</strong>.</p></div>", html);
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

/// <summary>
/// The visual editor's conversion (DocsMarkdown.ToEditor → the browser → DocsVisual): untouched blocks come back byte for byte, written blocks draw the
/// same page, and the editor's own pieces (alert boxes, doc: links, code languages, tasks) and what a browser makes on its own become Markdown again.
/// </summary>
public class DocsVisualTests
{
    private static readonly DocsMarkdown.Options Opts = new() { ResolveDoc = p => p == "fehlt" ? null : "/docs/" + p, AlertTitle = k => k };

    private const string Sample = """
        ## Überschrift mit `code` und **fett**

        Text mit *kursiv*, _auch kursiv_, ~~weg~~, \*Sternchen\*, snake_case_name und 2*3.
        Link [Docker](doc:installation/docker#ports "Titel"), [extern](https://example.com/a_b?x=1&y=2) und <https://matmon.eu>.
        Harte Zeile\
        danach.

        ### Liste
        - eins
        - zwei mit [Seite](doc:start)
          - verschachtelt
          - noch **eins**
        - [ ] offen
        - [x] erledigt

        1. erster
        2. zweiter
           ```bash
           docker compose up -d
           ```
           Danach weiter.
        3. dritter

        5. ab fünf
        6. sechs

        > [!WARNING]
        > Achtung, **wichtig**.
        >
        > Zweiter Absatz mit [Link](doc:fehlt).

        > [!TIP]
        > Kurz.

        > Ein Zitat
        > mit zwei Zeilen.

        | Spalte | Mitte | Rechts |
        |:--|:-:|--:|
        | `a\|b` | **x** | 3 |
        | [l](doc:a) | ![bild](/uploads/x.png) | |

        ```yaml
        key: "value"
        list:
          - a
        ```

        ~~~
        ohne Sprache ``` drin
        ~~~

        ---

        ![Ein Bild](/uploads/2026/bild.png "mit Titel")

        Absatz, der kein Block ist:
        1984 war ein Jahr.
        \- kein Listenpunkt
        \# kein Titel

        Ein <b>Tag</b> als Text & Zeichen < > "Zitat".
        """;

    private static string Roundtrip(string md) => DocsVisual.FromEditor(JsonNode.Parse(DocsVisual.ToJson(DocsMarkdown.ToEditor(md, Opts)).ToJsonString()));

    [Fact]
    public void Switching_to_visual_and_back_without_editing_gives_the_text_byte_for_byte()
    {
        foreach (var md in new[]
                 {
                     Sample, Sample.Replace("\n", "\r\n"), "\n\n  \n" + Sample + "\n\n\n", "", "   \n\n", "nur ein Satz", "## Titel\nText direkt darunter\n- Liste direkt\n",
                     "\tmit Tab\n\n```\n\tcode mit Tab\n```", "a\rb\r\rc"
                 })
            Assert.Equal(md, Roundtrip(md));
    }

    [Fact]
    public void Every_block_written_from_its_HTML_draws_the_same_page()
    {
        var doc = DocsMarkdown.ToEditor(Sample, Opts);
        Assert.True(doc.Blocks.Count >= 12);
        foreach (var b in doc.Blocks)
        {
            var written = DocsVisual.ToMarkdown(b.Html);
            Assert.True(DocsMarkdown.Render(b.Raw, Opts).Html == DocsMarkdown.Render(written, Opts).Html, "block:\n" + b.Raw + "\nwritten:\n" + written);
        }
        // the whole page with every block "edited"
        var all = new JsonObject { ["lead"] = "", ["blocks"] = new JsonArray(doc.Blocks.Select(b => (JsonNode)new JsonObject { ["html"] = b.Html }).ToArray()) };
        Assert.Equal(DocsMarkdown.Render(Sample, Opts).Html, DocsMarkdown.Render(DocsVisual.FromEditor(all), Opts).Html);
    }

    [Fact]
    public void One_edited_block_is_written_the_others_stay_as_they_were()
    {
        const string md = "## A\n\nerster   Absatz  \nmit Leerzeichen\n\n\n\nzweiter\n\n* stern\n* liste\n";
        var doc = DocsMarkdown.ToEditor(md, Opts);
        Assert.Equal(4, doc.Blocks.Count);
        var state = DocsVisual.ToJson(doc);
        var blocks = (JsonArray)state["blocks"]!;
        ((JsonObject)blocks[2]!).Remove("raw");
        blocks[2]!["html"] = "<p>zweiter <b>neu</b></p>";
        Assert.Equal("## A\n\nerster   Absatz  \nmit Leerzeichen\n\n\n\nzweiter **neu**\n\n* stern\n* liste\n", DocsVisual.FromEditor(state));
        // a new block at the end
        blocks.Add(new JsonObject { ["html"] = "<h3>Neu</h3>" });
        Assert.EndsWith("* liste\n\n### Neu\n", DocsVisual.FromEditor(state));
        // a deleted block takes its own blank lines with it
        blocks.RemoveAt(1);
        Assert.StartsWith("## A\n\nzweiter **neu**\n\n* stern", DocsVisual.FromEditor(state));

        // a text a browser posted ("\r\n") gets "\r\n" in the written block too — no mixed line ends
        var crlf = DocsVisual.ToJson(DocsMarkdown.ToEditor("## A\r\n\r\nalt\r\n\r\n- x\r\n", Opts));
        var cb = (JsonArray)crlf["blocks"]!;
        ((JsonObject)cb[1]!).Remove("raw");
        cb[1]!["html"] = "<p>neu<br>zwei</p>";
        Assert.Equal("## A\r\n\r\nneu\\\r\nzwei\r\n\r\n- x\r\n", DocsVisual.FromEditor(crlf));
    }

    [Fact]
    public void Alert_boxes_doc_links_code_languages_and_tasks_have_their_own_markup_in_the_editor()
    {
        var doc = DocsMarkdown.ToEditor("> [!CAUTION]\n> Vorsicht mit [Ports](doc:netz/ports#tcp) und [weg](doc:fehlt).\n\n```json\n{}\n```\n\n- [x] fertig\n", Opts);
        var callout = doc.Blocks[0].Html;
        Assert.Contains("data-kind=\"caution\"", callout);
        Assert.Contains("<p class=\"docs-callout-title\" contenteditable=\"false\">caution</p>", callout);
        Assert.Contains("<a href=\"doc:netz/ports#tcp\" class=\"docs-doclink\">Ports</a>", callout);
        Assert.Contains("class=\"docs-doclink docs-broken\">weg</a>", callout);
        Assert.Contains("data-lang=\"json\"", doc.Blocks[1].Html);
        Assert.Contains("<input type=\"checkbox\" checked>", doc.Blocks[2].Html);
        Assert.DoesNotContain("docs-anchor", DocsMarkdown.ToEditor("## Kopf", Opts).Blocks[0].Html);

        // what the editor's buttons make
        Assert.Equal("> [!WARNING]\n> Text mit [Link](doc:a/b#c)\n>\n> zweiter",
            DocsVisual.ToMarkdown("<div class=\"docs-callout docs-callout-warning\" data-kind=\"warning\"><p class=\"docs-callout-title\" contenteditable=\"false\">Achtung</p><p>Text mit <a href=\"doc:a/b#c\" class=\"docs-doclink docs-broken\">Link</a></p><p>zweiter</p></div>"));
        Assert.Equal("```sql\nselect 1;\n  -- eingerückt\n```",
            DocsVisual.ToMarkdown("<div class=\"docs-code docs-code-has-head\" data-lang=\"sql\"><div class=\"docs-code-head\" contenteditable=\"false\"><span class=\"docs-code-lang\">sql</span></div><pre><code>select 1;<br>  -- eingerückt\n</code></pre></div>"));
        Assert.Equal("- [x] erledigt\n- [ ] offen",
            DocsVisual.ToMarkdown("<ul><li class=\"docs-task\"><input type=\"checkbox\" checked=\"\"> erledigt</li><li class=\"docs-task\"><input type=\"checkbox\"> offen</li></ul>"));
        Assert.Equal("| A | B |\n| --- | ---: |\n| 1 | x\\|y |",
            DocsVisual.ToMarkdown("<div class=\"docs-table\"><table><thead><tr><th>A</th><th style=\"text-align:right\">B</th></tr></thead><tbody><tr><td>1</td><td>x|y</td></tr></tbody></table></div>"));
    }

    [Fact]
    public void What_a_browser_makes_while_typing_becomes_plain_Markdown()
    {
        Assert.Equal("Hallo **Welt** und _so_\n\nneue Zeile\n\n- a\n  - b\n- c",
            DocsVisual.ToMarkdown("<p>Hallo <b>Welt</b>&nbsp;und <i>so</i></p><div>neue Zeile</div><ul><li>a</li><ul><li>b</li></ul><li>c</li></ul>"));
        Assert.Equal("Zeile eins\\\nZeile zwei", DocsVisual.ToMarkdown("<p>Zeile eins<br>Zeile zwei<br></p>"));
        Assert.Equal("## Titel", DocsVisual.ToMarkdown("<h2><span style=\"color:red\">Titel</span></h2>"));
        Assert.Equal("**fett** Rest", DocsVisual.ToMarkdown("<p><strong>fett </strong>Rest</p>"));
        Assert.Equal("", DocsVisual.ToMarkdown("<p><br></p><div>&nbsp;</div>"));
        var code = DocsVisual.ToMarkdown("<p>Code <code>a``b</code> und <code>x</code></p>");
        Assert.Equal("<p>Code <code>a``b</code> und <code>x</code></p>", DocsMarkdown.Render(code).Html);
    }

    [Fact]
    public void Text_that_looks_like_markup_stays_text_and_nothing_unsafe_gets_through()
    {
        foreach (var text in new[] { "# kein Titel", "- kein Punkt", "1. keine Nummer", "> kein Zitat", "*nicht kursiv*", "[kein](link)", "`kein code`", "a\\b", "| x |", "---", "~~nein~~", "<b>kein Tag</b>", "x_y_z und _a_" })
        {
            var md = DocsVisual.ToMarkdown("<p>" + WebUtility.HtmlEncode(text) + "</p>");
            var html = DocsMarkdown.Render(md).Html;
            Assert.True(html == "<p>" + DocsMarkdown.E(text) + "</p>", text + " → " + md + " → " + html);
        }
        var bad = DocsVisual.ToMarkdown("<p><a href=\"javascript:alert(1)\">klick</a><script>alert(2)</script><img src=\"data:x\" alt=\"b\"><span onclick=\"x()\">ok</span></p>");
        Assert.Equal("klickok", bad);
        Assert.DoesNotContain("<script", DocsMarkdown.Render(DocsVisual.ToMarkdown("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>")).Html);
    }
}

/// <summary>The front matter of an exported page file: optional, read leniently, written so that it reads back the same.</summary>
public class DocsFrontMatterTests
{
    [Fact]
    public void A_file_without_front_matter_is_all_text()
    {
        var (meta, body) = DocsTransfer.Split("## Start\n\nText\n");
        Assert.Null(meta.Title);
        Assert.Null(meta.Draft);
        Assert.Equal("## Start\n\nText\n", body);
        Assert.Equal("---\nnicht zu", DocsTransfer.Split("---\nnicht zu").Body);                 // no closing line: not front matter
        Assert.Equal("---\n\nEin Trenner oben", DocsTransfer.Split("---\n\nEin Trenner oben").Body);
    }

    [Fact]
    public void Front_matter_is_read_with_quotes_comments_and_line_ends_of_any_kind()
    {
        var bom = ((char)0xFEFF).ToString();
        var text = bom + "---\r\ntitle: \"Über \\\"Docker\\\"\"\r\nsummary: 'It''s easy'\r\nicon: ti-brand-docker  # the whale\r\nsort: 20\r\ntranslationOf: getting-started/docker\r\ndraft: yes\r\nunknown: kept out\r\n---\r\n## Body\r\n";
        var (meta, body) = DocsTransfer.Split(text);
        Assert.Equal("Über \"Docker\"", meta.Title);
        Assert.Equal("It's easy", meta.Summary);
        Assert.Equal("ti-brand-docker", meta.Icon);
        Assert.Equal(20, meta.Sort);
        Assert.Equal("getting-started/docker", meta.TranslationOf);
        Assert.True(meta.Draft);
        Assert.Null(meta.Format);
        Assert.Equal("## Body\r\n", body);
        Assert.False(DocsTransfer.Split("---\npublished: true\n---\nx").Meta.Draft);
        Assert.Equal("html", DocsTransfer.Split("---\nformat: html\n---\n<p>x</p>").Meta.Format);
    }

    [Fact]
    public void Written_front_matter_reads_back_the_same()
    {
        var meta = new FrontMatter { Title = "A \"quoted\" title: with colon # and hash", Summary = "Zeile 1\nZeile 2\\Ende", Icon = "rocket", Sort = -5, TranslationOf = "a/b", Draft = false, Format = "html" };
        var file = DocsTransfer.Write(meta, "---\nbody starts with a rule\n");
        var (back, body) = DocsTransfer.Split(file);
        Assert.Equal(meta.Title, back.Title);
        Assert.Equal(meta.Summary, back.Summary);
        Assert.Equal(meta.Icon, back.Icon);
        Assert.Equal(meta.Sort, back.Sort);
        Assert.Equal(meta.TranslationOf, back.TranslationOf);
        Assert.Equal(meta.Draft, back.Draft);
        Assert.Equal("html", back.Format);
        Assert.Equal("---\nbody starts with a rule\n", body);
        Assert.Equal("de/v/1.0/a/b.md", DocsTransfer.EntryName("de", "1.0", "2.0", "a/b"));
        Assert.Equal("en/a.md", DocsTransfer.EntryName("en", "2.0", "2.0", "a"));
        Assert.Equal("en/a.md", DocsTransfer.EntryName("en", "", "", "a"));
    }
}

/// <summary>Import and export in the back office, and the editor's server side, in the whole application.</summary>
public class DocsTransferTests : IClassFixture<DocsApp>
{
    private readonly DocsApp _f;
    public DocsTransferTests(DocsApp f) => _f = f;

    private async Task PutAsync(string json)
    {
        var api = await _f.ApiAsync("docs:write");
        var res = await api.PutAsync("/api/v1/docs/pages", new StringContent(json, Encoding.UTF8, "application/json"));
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK && JsonNode.Parse(text)!["ok"]!.GetValue<bool>(), text);
    }

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            foreach (var (name, text) in files)
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        return ms.ToArray();
    }

    private static Dictionary<string, string> Unzip(byte[] bytes)
    {
        using var z = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        return z.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open()).ReadToEnd());
    }

    /// <summary>Uploads a ZIP for the dry run; returns the plan page.</summary>
    private static async Task<string> CheckAsync(HttpClient admin, byte[] zip)
    {
        var token = AppFactory.Token(await admin.GetStringAsync("/admin/p/docs/transfer"))!;
        var file = new ByteArrayContent(zip);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" }, { new StringContent("check"), "action" }, { file, "zip", "docs.zip" } };
        var res = await admin.PostAsync("/admin/p/docs/transfer", form);
        var html = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK, html);
        return WebUtility.HtmlDecode(html);
    }

    private static async Task<string> ApplyAsync(HttpClient admin, string plan)
    {
        var hash = Regex.Match(plan, "name=\"hash\" value=\"([0-9a-f]+)\"").Groups[1].Value;
        var res = await admin.PostAsync("/admin/p/docs/transfer", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AppFactory.Token(plan)!, ["action"] = "apply", ["hash"] = hash
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/p/docs/transfer"));
    }

    [Fact]
    public async Task An_export_imported_again_changes_nothing_and_an_edited_file_changes_only_its_page()
    {
        await PutAsync("""
            {"pages":[
              {"path":"ex","title":"Export \"Bereich\"","summary":"Kurz: gut","icon":"rocket","sort":30,"body":"## Eins\r\n\r\nText\r\n"},
              {"path":"ex/kind","title":"Kind","body":"> [!NOTE]\n> Hinweis","published":false},
              {"path":"ex","locale":"en","title":"Export area","translationOf":"ex","body":"Hello"}
            ]}
            """);
        var admin = await _f.AdminAsync();
        await admin.PostAsync("/set-language?culture=de", new StringContent(""));
        var res = await admin.GetAsync("/admin/p/docs/transfer?export=1");
        Assert.Equal("application/zip", res.Content.Headers.ContentType!.MediaType);
        var zip = await res.Content.ReadAsByteArrayAsync();
        var files = Unzip(zip);
        Assert.Contains("de/ex.md", files.Keys);
        Assert.Contains("de/ex/kind.md", files.Keys);
        Assert.Contains("en/ex.md", files.Keys);
        Assert.StartsWith("---\ntitle: \"Export \\\"Bereich\\\"\"\nsummary: \"Kurz: gut\"\nicon: \"rocket\"\nsort: 30\ndraft: false\n---\n## Eins\r\n", files["de/ex.md"]);
        Assert.Contains("draft: true", files["de/ex/kind.md"]);
        Assert.Contains("translationOf: \"ex\"", files["en/ex.md"]);

        // the same ZIP again: nothing to do
        var plan = await CheckAsync(admin, zip);
        Assert.Matches(@"0 neu · 0 geändert · \d+ unverändert · 0 mit Fehler", plan);
        Assert.DoesNotContain("value=\"apply\"", plan);

        // one text edited, one page new (front matter optional: the title comes from the file name), one without its parent
        files["de/ex/kind.md"] = files["de/ex/kind.md"].Replace("Hinweis", "Neuer Hinweis");
        files["de/ex/kind/enkel-seite.md"] = "Nur Text.\n";
        files["de/ex/kind/unter/ohne-eltern.md"] = "x";
        plan = await CheckAsync(admin, Zip(files.Select(kv => (kv.Key, kv.Value)).ToArray()));
        Assert.Matches(@"1 neu · 1 geändert · \d+ unverändert · 1 mit Fehler", plan);
        Assert.Contains("Enkel seite", plan);
        Assert.Contains("Die Elternseite „ex/kind/unter“ fehlt", plan);
        var after = await ApplyAsync(admin, plan);
        Assert.Contains("Import fertig: 2 geschrieben", after);

        Assert.Contains("Neuer Hinweis", WebUtility.HtmlDecode(await admin.GetStringAsync("/docs/ex/kind")));
        Assert.Contains("Nur Text.", WebUtility.HtmlDecode(await admin.GetStringAsync("/docs/ex/kind/enkel-seite")));

        // and a fresh export imported once more: idempotent
        var again = await (await admin.GetAsync("/admin/p/docs/transfer?export=1")).Content.ReadAsByteArrayAsync();
        Assert.Matches(@"0 neu · 0 geändert · \d+ unverändert · 0 mit Fehler", await CheckAsync(admin, again));
    }

    [Fact]
    public async Task A_folder_of_plain_Markdown_files_imports_like_the_API_wrote_it()
    {
        // the layout of a docs project: content/<locale>/<path>.md, no front matter; titles live elsewhere
        await PutAsync("""{"pages":[{"path":"plain","title":"Schlichter Titel","body":"Alt\n"}]}""");
        var admin = await _f.AdminAsync();
        await admin.PostAsync("/set-language?culture=de", new StringContent(""));
        var plan = await CheckAsync(admin, Zip(
            ("content/de/plain.md", "Alt\n\n\n"), ("content/de/plain/neu.md", "## Neu\n"), ("content/de/v/9.9/x.md", "x"), ("README.md", "nicht dabei"),
            ("content/xx/egal.md", "x"), ("content/de/suche.md", "reserviert")));
        Assert.Contains("1 neu · 0 geändert · 1 unverändert · 2 mit Fehler", plan);      // trailing blank lines are no change
        Assert.Contains("Schlichter Titel", plan);                                         // the existing title stays
        Assert.Contains("Es gibt keine Version „9.9“", plan);
        Assert.Contains("„suche“ ist eine Adresse der Doku selbst", plan);
        Assert.Contains("2 Dateien liegen nicht in einem Sprachordner", plan);
        await ApplyAsync(admin, plan);
        Assert.Contains("<h2 id=\"neu\">Neu", WebUtility.HtmlDecode(await _f.App.Browser().GetStringAsync("/docs/plain/neu")));
        Assert.Contains("Schlichter Titel", WebUtility.HtmlDecode(await _f.App.Browser().GetStringAsync("/docs/plain")));
    }

    [Fact]
    public async Task The_editor_converts_and_previews_on_the_server_and_saves_from_the_visual_mode()
    {
        const string md = "## Kopf\n\nText [Link](doc:edit)\n\n> [!TIP]\n> Tipp\n";
        await PutAsync("""{"pages":[{"path":"edit","title":"Edit","body":"## Kopf\n\nText [Link](doc:edit)\n\n> [!TIP]\n> Tipp\n"}]}""");
        var admin = await _f.AdminAsync();
        await admin.PostAsync("/set-language?culture=de", new StringContent(""));
        var list = await admin.GetStringAsync("/admin/p/docs/pages?lang=de");
        var id = Regex.Match(list, "/admin/p/docs/pages/(\\d+)\"><strong>Edit<").Groups[1].Value;
        Assert.NotEqual("", id);
        var editor = await admin.GetStringAsync("/admin/p/docs/pages/" + id);
        Assert.Contains("docs-editor.js", editor);
        Assert.Contains("id=\"docs-ed-config\"", editor);
        Assert.Contains("name=\"md_mode\"", editor);
        var token = AppFactory.Token(editor)!;

        async Task<JsonNode> Convert(Dictionary<string, string> fields)
        {
            fields["__RequestVerificationToken"] = token;
            fields["lang"] = "de";
            var r = await admin.PostAsync("/admin/p/docs/convert", new FormUrlEncodedContent(fields));
            var text = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.OK, text);
            return JsonNode.Parse(text)!;
        }
        var state = await Convert(new() { ["action"] = "visual", ["body"] = md });
        Assert.Equal(3, state["blocks"]!.AsArray().Count);
        Assert.Contains("href=\\u0022doc:edit\\u0022", state.ToJsonString());
        var back = await Convert(new() { ["action"] = "markdown", ["visual"] = state.ToJsonString() });
        Assert.Equal(md, back["markdown"]!.GetValue<string>());
        var preview = await Convert(new() { ["action"] = "preview", ["mode"] = "md", ["body"] = md, ["title"] = "Edit" });
        var html = preview["html"]!.GetValue<string>();
        Assert.Contains("href=\"/docs/edit\"", html);                                         // the doc: link resolved like on the page
        Assert.Contains("docs-callout docs-callout-tip", html);
        Assert.Contains(".docs-callout{", html);                                                // the public stylesheet
        Assert.Contains("<h1>Edit</h1>", html);

        // saving from the visual mode: the second block edited, the rest exactly as it was
        var blocks = state["blocks"]!.AsArray();
        ((JsonObject)blocks[1]!).Remove("raw");
        blocks[1]!["html"] = "<p>Text <a href=\"doc:edit#kopf\" class=\"docs-doclink\">Link</a> <b>neu</b></p>";
        var save = await admin.PostAsync("/admin/p/docs/pages/" + id, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["action"] = "save", ["title"] = "Edit", ["slug"] = "edit", ["summary"] = "", ["body"] = "alt – wird ersetzt",
            ["format"] = "md", ["parent"] = "0", ["sort"] = "", ["icon"] = "", ["published"] = "on", ["lang"] = "de", ["v"] = "",
            ["md_mode"] = "visual", ["md_visual"] = state.ToJsonString()
        }));
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        var api = await _f.ApiAsync("docs:read");
        var read = JsonNode.Parse(await api.GetStringAsync("/api/v1/docs/pages/" + id))!;
        Assert.Equal("## Kopf\n\nText [Link](doc:edit#kopf) **neu**\n\n> [!TIP]\n> Tipp\n", read["body"]!.GetValue<string>());
    }
}
