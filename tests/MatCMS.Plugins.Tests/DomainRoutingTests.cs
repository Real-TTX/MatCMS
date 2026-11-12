using System.Net;
using System.Text;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Domain routing (Settings → Domains): the pure rules — reading the setting, the way in, the links on the way out.</summary>
public class DomainRoutesTests
{
    private static readonly DomainRoute Docs = new("docs.example.org", "/docs", true);

    [Fact]
    public void The_setting_is_read_leniently_and_a_bad_line_is_skipped()
    {
        var routes = DomainRoutes.Parse("docs.example.org | /docs | umleiten\nhttps://Hilfe.Example.org:443/x = hilfe/\n# comment\nkaputt\nadmin.example.org | /admin\nx.example.org | /\ndocs.example.org | /andere");
        Assert.Equal(2, routes.Count);
        Assert.Equal(new DomainRoute("docs.example.org", "/docs", true), routes[0]);
        Assert.Equal(new DomainRoute("hilfe.example.org", "/hilfe", false), routes[1]);
        Assert.Null(DomainRoutes.CleanPrefix("/en"));          // a language prefix is not a part of the site
        Assert.Null(DomainRoutes.CleanPrefix("/uploads"));     // nor one of the host's own areas
    }

    [Fact]
    public void The_way_in_puts_the_prefix_behind_the_language()
    {
        Assert.Equal("/docs", DomainRoutes.Inbound(Docs, "/"));
        Assert.Equal("/docs/a/b", DomainRoutes.Inbound(Docs, "/a/b"));
        Assert.Equal("/en/docs", DomainRoutes.Inbound(Docs, "/en"));
        Assert.Equal("/en/docs/a", DomainRoutes.Inbound(Docs, "/en/a"));
        Assert.Null(DomainRoutes.Inbound(Docs, "/uploads/x.png"));
        Assert.Null(DomainRoutes.Inbound(Docs, "/admin/p/docs/pages"));
        Assert.Equal("/a", DomainRoutes.Strip(Docs, "/docs/a"));
        Assert.Equal("/en", DomainRoutes.Strip(Docs, "/en/docs"));
        Assert.Null(DomainRoutes.Strip(Docs, "/docsx"));
        Assert.Same(Docs, DomainRoutes.Owning(new[] { Docs }, "/en/docs/a"));
    }

    [Fact]
    public void Links_are_written_for_the_domain()
    {
        var html = "<a href=\"/docs/a?x=1&amp;y=2#t\">a</a><a href=\"/preise\">p</a><img src=\"/uploads/x.png\"><form action=\"/docs/suche\"></form>"
                   + "<a href=\"/en/docs\">en</a><a href=\"https://www.example.org/docs/b\">abs</a><meta property=\"og:url\" content=\"https://www.example.org/docs/c\"><a href=\"//cdn.x/y\">c</a>";
        var outHtml = DomainRoutes.RewriteHtml(html, Docs, "https", "https://www.example.org");
        Assert.Contains("href=\"/a?x=1&amp;y=2#t\"", outHtml);
        Assert.Contains("href=\"https://www.example.org/preise\"", outHtml);      // the rest of the site: at the main address
        Assert.Contains("src=\"/uploads/x.png\"", outHtml);
        Assert.Contains("action=\"/suche\"", outHtml);
        Assert.Contains("href=\"/en\"", outHtml);
        Assert.Contains("href=\"https://docs.example.org/b\"", outHtml);
        Assert.Contains("content=\"https://docs.example.org/c\"", outHtml);
        Assert.Contains("href=\"//cdn.x/y\"", outHtml);
        Assert.Null(DomainRoutes.MainOrigin("https://docs.example.org", Docs));
    }
}

/// <summary>Domain routing through the whole application, with the docs plugin as the part a second domain shows.</summary>
public class DomainRoutingTests : IClassFixture<DocsApp>
{
    private readonly DocsApp _f;
    public DomainRoutingTests(DocsApp f) => _f = f;

    private async Task SetupAsync()
    {
        await _f.InstallAsync();
        using var scope = _f.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        void Set(string key, string value)
        {
            var row = db.SiteSettings.FirstOrDefault(s => s.Key == key);
            if (row is null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = value }); else row.Value = value;
        }
        Set(SettingKeys.DomainRouting, "docs.example.org | /docs | umleiten");
        Set(SettingKeys.CanonicalUrl, "https://www.example.org");
        await db.SaveChangesAsync();
        var api = await _f.ApiAsync("docs:write");
        var res = await api.PutAsync("/api/v1/docs/pages", new StringContent(
            """{"pages":[{"path":"start","title":"Start","body":"Siehe [Weiter](doc:start/weiter) und [Preise](/preise)."},{"path":"start/weiter","title":"Weiter","body":"x"},{"path":"home","title":"Home","locale":"en","body":"English"}]}""",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private HttpClient On(string host) =>
        _f.App.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://" + host), AllowAutoRedirect = false, HandleCookies = true });

    [Fact]
    public async Task The_second_domain_shows_its_part_with_short_addresses()
    {
        await SetupAsync();
        var docs = On("docs.example.org");
        var home = WebUtility.HtmlDecode(await docs.GetStringAsync("/"));
        Assert.Contains("href=\"/start\"", home);                                    // /docs/start written short
        Assert.DoesNotContain("href=\"/docs/", home);

        var page = await docs.GetStringAsync("/start");
        Assert.Contains("<h1>Start</h1>", page);
        Assert.Contains("href=\"/start/weiter\"", page);
        Assert.Contains("href=\"https://www.example.org/preise\"", page);              // the rest of the site lives at the main address
        Assert.Contains("action=\"/suche\"", page);
        Assert.Equal(HttpStatusCode.OK, (await docs.GetAsync("/suche?q=Weiter")).StatusCode);
        Assert.Contains("English", await docs.GetStringAsync("/en/home"));            // the language prefix stays in front

        // The long form on the domain becomes the short one; the host's own areas are not rewritten.
        var longForm = await docs.GetAsync("/docs/start?x=1");
        Assert.Equal(HttpStatusCode.Redirect, longForm.StatusCode);
        Assert.Equal("/start?x=1", longForm.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await docs.GetAsync("/robots.txt")).StatusCode);
        Assert.Contains("http://docs.example.org/sitemap.xml", await docs.GetStringAsync("/robots.txt"));

        // The sitemap of each host names that host's addresses only.
        var docsMap = await docs.GetStringAsync("/sitemap.xml");
        Assert.Contains("<loc>http://docs.example.org/start/weiter</loc>", docsMap);
        Assert.DoesNotContain("www.example.org", docsMap);
        var mainMap = await On("www.example.org").GetStringAsync("/sitemap.xml");
        Assert.DoesNotContain("/docs/start", mainMap);
    }

    [Fact]
    public async Task The_main_address_sends_the_part_to_its_domain_but_not_a_signed_in_admin()
    {
        await SetupAsync();
        var guest = On("www.example.org");
        var r = await guest.GetAsync("/docs/start/weiter");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("http://docs.example.org/start/weiter", r.Headers.Location!.ToString());
        var en = await guest.GetAsync("/en/docs");
        Assert.Equal("http://docs.example.org/en", en.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/")).StatusCode);       // the rest of the site stays where it is

        var admin = On("www.example.org");
        await AppFactory.LoginAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/docs/start")).StatusCode);
        var settings = WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/settings"));
        Assert.Contains("docs.example.org", settings);
    }
}
