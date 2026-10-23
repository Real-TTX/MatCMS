using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The blog through HTTP, in the real application: the pages a visitor gets, the back office an administrator uses, and what happens
/// when the plugin is switched off. One application instance for all of it (it takes seconds to start).</summary>
public class BlogEndToEndTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public BlogEndToEndTests(AppFactory app) => _app = app;

    private static Dictionary<string, string?> Post(string token, string title, string? slug = null, bool published = true) => new()
    {
        ["__RequestVerificationToken"] = token, ["action"] = "save", ["title"] = title, ["slug"] = slug ?? "", ["titleimage"] = "", ["gallery"] = "[]",
        ["gallerylayout"] = "carousel", ["excerpt"] = "Auszug", ["content"] = "<p>Inhalt</p>", ["tags"] = "x, y", ["attachments"] = "[]",
        ["published"] = published ? "true" : null, ["date"] = "2026-01-02", ["sourceurl"] = "", ["sourcename"] = ""
    };

    private static FormUrlEncodedContent Form(Dictionary<string, string?> d) =>
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

        // the plugin list shows it as a built-in plugin and its edit link leads to its own page
        var plugins = await admin.GetStringAsync("/admin/plugins");
        // The blog's own row: the list holds the other built-in plugins too, and its first link is not necessarily the blog's.
        string id;
        using (var scope = _app.Services.CreateScope())
            id = (await scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>().Plugins.FirstAsync(p => p.Key == "blog")).Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("/admin/plugins/edit/" + id, plugins, StringComparison.OrdinalIgnoreCase);
        var builtIn = await admin.GetAsync("/admin/plugins/edit/" + id);
        Assert.Equal(HttpStatusCode.Redirect, builtIn.StatusCode);
        var pageUrl = builtIn.Headers.Location!.ToString();
        Assert.Contains("BuiltIn", pageUrl, StringComparison.OrdinalIgnoreCase);

        async Task Toggle() => await AppFactory.PostAsync(admin, pageUrl + "?handler=Toggle", new() { ["__RequestVerificationToken"] = AppFactory.Token(await admin.GetStringAsync(pageUrl))! });

        await Toggle();                                                                 // off
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/blog/bleibt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/admin/p/blog")).StatusCode);
        Assert.DoesNotContain("href=\"/admin/p/blog\"", await admin.GetStringAsync("/admin/"));
        Assert.DoesNotContain("/blog/bleibt<", await visitor.GetStringAsync("/sitemap.xml"));

        await Toggle();                                                                 // on again
        var back = await visitor.GetAsync("/blog/bleibt");
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Contains("Bleibt", await back.Content.ReadAsStringAsync());

        // a built-in plugin cannot be deleted, not even by a crafted request
        var delete = await AppFactory.PostAsync(admin, "/admin/plugins?handler=Delete&id=" + id,
            new() { ["__RequestVerificationToken"] = AppFactory.Token(await admin.GetStringAsync("/admin/plugins"))! });
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/blog/bleibt")).StatusCode);
        _ = delete;
    }

    [Fact]
    public async Task The_old_back_office_addresses_lead_to_the_plugin()
    {
        var admin = await AdminAsync();
        var list = await admin.GetAsync("/admin/posts");
        Assert.Equal(HttpStatusCode.Redirect, list.StatusCode);
        Assert.Equal("/admin/p/blog", list.Headers.Location!.ToString());
        var edit = await admin.GetAsync("/admin/posts/edit/7");
        Assert.Equal("/admin/p/blog/posts/7", edit.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Browser().GetAsync("/en/blog/anything")).StatusCode);   // the blog stays German-only
    }
}
