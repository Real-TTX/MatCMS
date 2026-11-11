using System.Net;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The blog through HTTP, in the real application, as the store's signed package: the pages a visitor gets, the back office an
/// administrator uses, what happens when the plugin is switched off — and the blog in several languages. One application instance for all of
/// it (it takes seconds to start).</summary>
public class BlogEndToEndTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public BlogEndToEndTests(AppFactory app)
    {
        _app = app;
        BlogFixture.InstallAsync(app.Services).GetAwaiter().GetResult();
    }

    internal static Dictionary<string, string?> Post(string token, string title, string? slug = null, bool published = true) => new()
    {
        ["__RequestVerificationToken"] = token, ["action"] = "save", ["title"] = title, ["slug"] = slug ?? "", ["titleimage"] = "", ["gallery"] = "[]",
        ["gallerylayout"] = "carousel", ["excerpt"] = "Auszug", ["content"] = "<p>Inhalt</p>", ["tags"] = "x, y", ["attachments"] = "[]",
        ["published"] = published ? "true" : null, ["date"] = "2026-01-02", ["sourceurl"] = "", ["sourcename"] = ""
    };

    internal static FormUrlEncodedContent Form(Dictionary<string, string?> d) =>
        new(d.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!));

    private async Task<HttpClient> AdminAsync() { var c = _app.Browser(); await AppFactory.LoginAsync(c); return c; }

    [Fact]
    public async Task The_back_office_writes_posts_the_public_sees_them_and_drafts_stay_hidden()
    {
        var admin = await AdminAsync();
        var visitor = _app.Browser();

        // anonymous: no way into the back office
        var denied = await visitor.GetAsync("/admin/p/blog");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("login", denied.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);

        // create
        var editor = await admin.GetStringAsync("/admin/p/blog/posts/0");
        Assert.Contains("id=\"post-title\"", editor);
        Assert.Contains("/plugin-assets/blog/blog-editor.js", editor);               // the editor's script comes with the package
        var token = AppFactory.Token(editor)!;
        var saved = await admin.PostAsync("/admin/p/blog/posts/0", Form(Post(token, "End-to-End Beitrag")));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var location = saved.Headers.Location!.ToString();
        Assert.Matches(@"/admin/p/blog/posts/\d+", location);
        var after = await admin.GetStringAsync(location);
        Assert.Contains("value=\"end-to-end-beitrag\"", after);                      // slug made from the title

        var page = await visitor.GetAsync("/blog/end-to-end-beitrag");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("End-to-End Beitrag", html);
        Assert.Contains("<p>Inhalt</p>", html);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/plugin-assets/blog/blog-editor.js")).StatusCode);

        // the same title again: the slug is made unique
        token = AppFactory.Token(await admin.GetStringAsync("/admin/p/blog/posts/0"))!;
        await admin.PostAsync("/admin/p/blog/posts/0", Form(Post(token, "End-to-End Beitrag")));
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/blog/end-to-end-beitrag-2")).StatusCode);

        // unticking "published" makes it a draft: 404 for a visitor, still there for the administrator
        var id = location[(location.LastIndexOf('/') + 1)..];
        token = AppFactory.Token(await admin.GetStringAsync(location))!;
        await admin.PostAsync(location, Form(Post(token, "End-to-End Beitrag", "end-to-end-beitrag", published: false)));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/blog/end-to-end-beitrag")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/blog/end-to-end-beitrag")).StatusCode);
        Assert.DoesNotContain("/blog/end-to-end-beitrag<", await visitor.GetStringAsync("/sitemap.xml"));
        Assert.Contains("/blog/end-to-end-beitrag-2<", await visitor.GetStringAsync("/sitemap.xml"));   // the live one is in the sitemap

        // an empty title is refused, with a message, and nothing changes
        token = AppFactory.Token(await admin.GetStringAsync(location))!;
        var refused = await admin.PostAsync(location, Form(Post(token, "   ")));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("title is required", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // antiforgery: a POST without a valid token is refused
        var forged = await admin.PostAsync(location, Form(Post("not-a-token", "Hacked")));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        // delete through the list
        var list = await admin.GetStringAsync("/admin/p/blog");
        var delete = await admin.PostAsync("/admin/p/blog", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = AppFactory.Token(list)!, ["action"] = "delete", ["id"] = id }));
        Assert.Equal(HttpStatusCode.Redirect, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/blog/end-to-end-beitrag")).StatusCode);
    }

    [Fact]
    public async Task A_title_is_encoded_everywhere_it_is_shown()
    {
        var admin = await AdminAsync();
        var visitor = _app.Browser();
        var token = AppFactory.Token(await admin.GetStringAsync("/admin/p/blog/posts/0"))!;
        await admin.PostAsync("/admin/p/blog/posts/0", Form(Post(token, "<script>alert(1)</script>", "xss")));

        var list = await admin.GetStringAsync("/admin/p/blog");
        Assert.DoesNotContain("<script>alert(1)</script>", list);
        Assert.Contains("&lt;script&gt;", list);
        var page = await visitor.GetStringAsync("/blog/xss");
        Assert.DoesNotContain("<script>alert(1)</script>", page);
    }

    [Fact]
    public async Task Switching_the_blog_off_removes_its_pages_and_entry_and_keeps_the_posts_and_back_on_they_are_there()
    {
        var admin = await AdminAsync();
        var visitor = _app.Browser();
        var token = AppFactory.Token(await admin.GetStringAsync("/admin/p/blog/posts/0"))!;
        await admin.PostAsync("/admin/p/blog/posts/0", Form(Post(token, "Bleibt", "bleibt")));
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/blog/bleibt")).StatusCode);
        Assert.Contains("href=\"/admin/p/blog\"", await admin.GetStringAsync("/admin/"));

        // the plugin list shows it as a native plugin, signed by the store
        string id;
        using (var scope = _app.Services.CreateScope())
            id = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Plugins.FirstAsync(p => p.Key == "blog")).Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var plugins = await admin.GetStringAsync("/admin/plugins");
        Assert.Contains("ti-rosette-discount-check", plugins);

        async Task Toggle() => await AppFactory.PostAsync(admin, "/admin/plugins?handler=Toggle&id=" + id,
            new() { ["__RequestVerificationToken"] = AppFactory.Token(await admin.GetStringAsync("/admin/plugins"))! });

        try
        {
            await Toggle();                                                                 // off
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/blog/bleibt")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/admin/p/blog")).StatusCode);
            Assert.DoesNotContain("href=\"/admin/p/blog\"", await admin.GetStringAsync("/admin/"));
            Assert.DoesNotContain("/blog/bleibt<", await visitor.GetStringAsync("/sitemap.xml"));
        }
        finally { await Toggle(); }                                                         // on again (the other tests of this class need it)
        var back = await visitor.GetAsync("/blog/bleibt");
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Contains("Bleibt", await back.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_old_back_office_addresses_lead_to_the_plugin_and_a_language_the_site_does_not_publish_in_has_no_blog()
    {
        var admin = await AdminAsync();
        var list = await admin.GetAsync("/admin/posts");
        Assert.Equal(HttpStatusCode.Redirect, list.StatusCode);
        Assert.Equal("/admin/p/blog", list.Headers.Location!.ToString());
        var edit = await admin.GetAsync("/admin/posts/edit/7");
        Assert.Equal("/admin/p/blog/posts/7", edit.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Browser().GetAsync("/fr/blog/anything")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Browser().GetAsync("/fr/blog")).StatusCode);
    }
}

/// <summary>The blog in several languages, through HTTP: its own application, because the site's languages are switched on for it.</summary>
public class BlogMultilingualTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;

    public BlogMultilingualTests(AppFactory app)
    {
        _app = app;
        BlogFixture.InstallAsync(app.Services).GetAwaiter().GetResult();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var languages = db.SiteSettings.FirstOrDefault(s => s.Key == SettingKeys.Languages);
        if (languages is null) db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.Languages, Value = "de,en" });
        else languages.Value = "de,en";
        // the switcher only offers a language that has a published page
        if (!db.Pages.Any(p => p.Locale == "en")) db.Pages.Add(new Page { Title = "Home", Slug = "home", Locale = "en", IsPublished = true });
        db.SaveChanges();
    }

    private async Task<HttpClient> AdminAsync() { var c = _app.Browser(); await AppFactory.LoginAsync(c); return c; }

    private static async Task<string> SaveAsync(HttpClient admin, string url, Dictionary<string, string?> fields)
    {
        var res = await admin.PostAsync(url, BlogEndToEndTests.Form(fields));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return res.Headers.Location!.ToString();
    }

    [Fact]
    public async Task A_post_and_its_translation_live_at_their_own_addresses_and_the_switcher_leads_from_one_to_the_other()
    {
        var admin = await AdminAsync();
        var visitor = _app.Browser();

        // the original, in the default language
        var editor = await admin.GetStringAsync("/admin/p/blog/posts/0");
        Assert.Contains("name=\"locale\"", editor);                                        // a site with two languages: a new post picks one
        var deUrl = await SaveAsync(admin, "/admin/p/blog/posts/0", BlogEndToEndTests.Post(AppFactory.Token(editor)!, "Sommerfest", "sommerfest"));
        var deId = deUrl[(deUrl.LastIndexOf('/') + 1)..];

        // "Übersetzung anlegen (EN)": a new post filled with the original, joined to it on save
        var deEditor = await admin.GetStringAsync(deUrl);
        Assert.Contains($"/admin/p/blog/posts/0?translationOf={deId}&amp;locale=en", deEditor);
        var tr = await admin.GetStringAsync($"/admin/p/blog/posts/0?translationOf={deId}&locale=en");
        Assert.Contains("value=\"sommerfest\"", tr);                                           // filled with the original
        var fields = BlogEndToEndTests.Post(AppFactory.Token(tr)!, "Summer party", "summer-party");
        fields["translationOf"] = deId;
        fields["locale"] = "en";
        var enUrl = await SaveAsync(admin, $"/admin/p/blog/posts/0?translationOf={deId}&locale=en", fields);

        // each at its own address; the default language's exactly where it always was
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/blog/sommerfest")).StatusCode);
        var en = await visitor.GetAsync("/en/blog/summer-party");
        Assert.Equal(HttpStatusCode.OK, en.StatusCode);
        var enHtml = await en.Content.ReadAsStringAsync();
        Assert.Contains("Summer party", enHtml);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/blog/summer-party")).StatusCode);       // not a German post
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/en/blog/sommerfest")).StatusCode);      // not an English one

        // the language switcher leads from a post to its translation (and back)
        var deHtml = await visitor.GetStringAsync("/blog/sommerfest");
        Assert.Contains("href=\"/en/blog/summer-party\"", deHtml);
        Assert.Contains("href=\"/blog/sommerfest\"", enHtml);

        // the overview per language
        var enOverview = await visitor.GetStringAsync("/en/blog");
        Assert.Contains("href=\"/en/blog/summer-party\"", enOverview);
        Assert.DoesNotContain("/blog/sommerfest\"", enOverview);
        var deOverview = await visitor.GetStringAsync("/blog");
        Assert.Contains("href=\"/blog/sommerfest\"", deOverview);
        Assert.DoesNotContain("summer-party", deOverview);

        // sitemap: both, each under its language
        var sitemap = await visitor.GetStringAsync("/sitemap.xml");
        Assert.Contains("/blog/sommerfest<", sitemap);
        Assert.Contains("/en/blog/summer-party<", sitemap);
        Assert.Contains("/en/blog<", sitemap);

        // the editor of the original lists the translation and offers no second English one
        deEditor = await admin.GetStringAsync(deUrl);
        Assert.Contains(enUrl, deEditor);
        Assert.DoesNotContain("translationOf=" + deId + "&amp;locale=en", deEditor);

        // a second English translation is refused
        tr = await admin.GetStringAsync($"/admin/p/blog/posts/0?translationOf={deId}&locale=en");
        fields = BlogEndToEndTests.Post(AppFactory.Token(tr)!, "Summer party 2", "summer-party-2");
        fields["translationOf"] = deId;
        fields["locale"] = "en";
        var again = await admin.PostAsync($"/admin/p/blog/posts/0?translationOf={deId}&locale=en", BlogEndToEndTests.Form(fields));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/en/blog/summer-party-2")).StatusCode);

        // the list says which language a post is in and filters by it
        var list = await admin.GetStringAsync("/admin/p/blog");
        Assert.Contains("lang-en", list);
        Assert.Contains("lang-de", list);
    }

    [Fact]
    public async Task The_cloud_writes_a_translation_in_another_language_and_the_search_answers_per_language()
    {
        using var scope = _app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var ops = sp.GetRequiredService<MatCMS.Services.Plugins.PluginContributions>();
        var original = (System.Text.Json.Nodes.JsonObject)(await ops.RunOperationAsync(sp, "post.create",
            new System.Text.Json.Nodes.JsonObject { ["title"] = "Weinlese Bericht", ["contentHtml"] = "<p>Trauben gelesen</p>" }))!;
        var id = original["data"]!["id"]!.GetValue<long>();
        var tr = (System.Text.Json.Nodes.JsonObject)(await ops.RunOperationAsync(sp, "post.create",
            new System.Text.Json.Nodes.JsonObject { ["title"] = "Harvest report", ["contentHtml"] = "<p>Grapes picked</p>", ["locale"] = "en", ["translationOf"] = id }))!;
        Assert.Equal("applied", tr["status"]!.GetValue<string>());
        Assert.Equal("/en/blog/harvest-report", tr["data"]!["url"]!.GetValue<string>());

        var read = (System.Text.Json.Nodes.JsonObject)(await ops.RunOperationAsync(sp, "post.read", new System.Text.Json.Nodes.JsonObject { ["id"] = id }))!;
        Assert.Equal("en", read["data"]!["translations"]!.AsArray().Single()!["locale"]!.GetValue<string>());

        var events = (MatCMS.Plugin.IPluginEvents)new MatCMS.Services.Plugins.PluginEventBus(sp.GetRequiredService<MatCMS.Services.PluginRegistry>(), sp, new PluginUser(), "de");
        var enHits = await events.CollectAsync("site.search", new System.Text.Json.Nodes.JsonObject { ["q"] = "harvest grapes", ["locale"] = "en" });
        Assert.Contains(enHits, a => a?["items"]?.AsArray().Any(i => i!["url"]!.GetValue<string>() == "/en/blog/harvest-report") == true);
        var deHits = await events.CollectAsync("site.search", new System.Text.Json.Nodes.JsonObject { ["q"] = "harvest grapes", ["locale"] = "de" });
        Assert.DoesNotContain(deHits, a => a?["items"]?.AsArray().Any(i => i!["url"]!.GetValue<string>().Contains("harvest")) == true);
    }
}

/// <summary>"Standardsprache unter /" switched off: the default language has its own prefix — the blog's links follow. Not in parallel with
/// anything: the switch is process-wide.</summary>
[Collection("default-route")]
public class BlogDefaultRouteTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public BlogDefaultRouteTests(AppFactory app)
    {
        _app = app;
        BlogFixture.InstallAsync(app.Services).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task With_the_default_language_under_its_own_prefix_the_blog_answers_there_and_links_there()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<MatCMS.Services.Plugins.PluginContributions>().RunOperationAsync(sp, "post.create",
                new System.Text.Json.Nodes.JsonObject { ["title"] = "Mit Praefix", ["contentHtml"] = "<p>x</p>" });
        }
        var visitor = _app.Browser();
        Localizer.SetDefaultAtRoot("0");
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/de/blog/mit-praefix")).StatusCode);
            var overview = await visitor.GetStringAsync("/de/blog");
            Assert.Contains("href=\"/de/blog/mit-praefix\"", overview);
        }
        finally { Localizer.SetDefaultAtRoot("1"); }
        // and back at the root: the addresses the blog always had
        Assert.Contains("href=\"/blog/mit-praefix\"", await visitor.GetStringAsync("/blog"));
        Assert.NotEqual(HttpStatusCode.OK, (await visitor.GetAsync("/de/blog/mit-praefix")).StatusCode);   // "/de/…" is no address of the blog then
    }
}
