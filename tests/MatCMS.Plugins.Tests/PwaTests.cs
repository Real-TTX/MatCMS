using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The back office as an installable app: manifest, service worker and offline page, reachable before signing in (the
/// sign-in page is part of the app), and the pages that link them.</summary>
public class PwaTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public PwaTests(AppFactory app) => _app = app;

    [Fact]
    public async Task The_manifest_the_worker_and_the_offline_page_are_served_to_anybody()
    {
        var c = _app.Browser();

        var res = await c.GetAsync("/pwa/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/manifest+json", res.Content.Headers.ContentType!.MediaType);
        var m = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal("/admin", m["start_url"]!.GetValue<string>());
        Assert.Equal("standalone", m["display"]!.GetValue<string>());
        var icons = m["icons"]!.AsArray();
        Assert.Contains(icons, i => i!["sizes"]!.GetValue<string>() == "192x192");
        Assert.Contains(icons, i => i!["purpose"]!.GetValue<string>() == "maskable");
        // Every icon the manifest names really exists — in the shared library's wwwroot (the test host does not serve a
        // library's static files; the app does, under /_content/MatCMS.Shared.Web/).
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MatCMS.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var i in icons)
        {
            var src = i!["src"]!.GetValue<string>();
            Assert.StartsWith("/_content/MatCMS.Shared.Web/", src);
            Assert.True(File.Exists(Path.Combine(root!.FullName, "src", "MatCMS.Shared.Web", "wwwroot", src["/_content/MatCMS.Shared.Web/".Length..])), src);
        }

        var sw = await c.GetAsync("/pwa/sw.js");
        Assert.Equal(HttpStatusCode.OK, sw.StatusCode);
        Assert.Equal("/", sw.Headers.GetValues("Service-Worker-Allowed").Single());   // scope "/" so /login is inside the app
        var js = await sw.Content.ReadAsStringAsync();
        Assert.DoesNotContain("__VERSION__", js);
        Assert.Contains("const NAV = \"/admin\"", js);   // a site's worker leaves its public pages alone

        var offline = await c.GetAsync("/pwa/offline");
        Assert.Equal(HttpStatusCode.OK, offline.StatusCode);
        Assert.Contains("Keine Verbindung", await offline.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_sign_in_page_and_the_back_office_link_the_app_and_the_public_site_does_not()
    {
        var c = _app.Browser();
        Assert.Contains("rel=\"manifest\"", await c.GetStringAsync("/login"));
        Assert.DoesNotContain("rel=\"manifest\"", await c.GetStringAsync("/"));   // the visitors' site is not the back office's app
        await AppFactory.LoginAsync(c);
        var admin = await c.GetStringAsync("/admin");
        Assert.Contains("rel=\"manifest\"", admin);
        Assert.Contains("apple-touch-icon", admin);
        Assert.Contains("name=\"theme-color\"", admin);
    }
}
