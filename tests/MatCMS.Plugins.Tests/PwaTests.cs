using System.Net;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task A_site_with_MatCMS_s_own_logo_gets_the_mark_as_favicon_and_the_dark_logo_in_the_back_office()
    {
        var c = _app.Browser();
        // the word mark is unreadable at 16 px: the mark alone is the tab icon, on the site and on the sign-in page
        Assert.Contains("rel=\"icon\" href=\"/img/favicon.svg\"", await c.GetStringAsync("/"));
        Assert.Contains("rel=\"icon\" href=\"/img/favicon.svg\"", await c.GetStringAsync("/login"));
        await AppFactory.LoginAsync(c);
        var admin = await c.GetStringAsync("/admin");
        Assert.Contains("class=\"admin-logo logo-light\" src=\"/img/logo.svg\"", admin);
        Assert.Contains("class=\"admin-logo logo-dark\" src=\"/img/logo-dark.svg\"", admin);
    }

    [Fact]
    public async Task A_site_s_own_logo_gets_its_own_dark_twin_in_the_back_office_or_none()
    {
        var c = _app.Browser();
        await AppFactory.LoginAsync(c);
        try
        {
            Set(SettingKeys.LogoUrl, "/uploads/own.png");
            var admin = await c.GetStringAsync("/admin");
            // a logo of its own and no dark one: shown as it is, MatCMS's dark logo must not stand in for it
            Assert.Contains("class=\"admin-logo\" src=\"/uploads/own.png\"", admin);
            Assert.DoesNotContain("logo-dark", admin);

            Set(SettingKeys.LogoDarkUrl, "/uploads/own-dark.png");
            admin = await c.GetStringAsync("/admin");
            Assert.Contains("class=\"admin-logo logo-light\" src=\"/uploads/own.png\"", admin);
            Assert.Contains("class=\"admin-logo logo-dark\" src=\"/uploads/own-dark.png\"", admin);
            Assert.Contains("name=\"Values[LogoDarkUrl]\" value=\"/uploads/own-dark.png\"", await c.GetStringAsync("/admin/settings"));
        }
        finally
        {
            Set(SettingKeys.LogoUrl, null);
            Set(SettingKeys.LogoDarkUrl, null);
        }
    }

    private void Set(string key, string? value)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SiteSettings.RemoveRange(db.SiteSettings.Where(s => s.Key == key));
        if (value is not null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        db.SaveChanges();
    }
}
