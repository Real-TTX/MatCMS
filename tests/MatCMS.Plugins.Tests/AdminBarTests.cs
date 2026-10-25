using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The admin bar over the live site: there for a signed-in admin looking at the site, never inside a preview of it.</summary>
public class AdminBarTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public AdminBarTests(AppFactory app) => _app = app;

    [Fact]
    public async Task The_bar_is_over_the_site_for_a_signed_in_admin_but_not_in_a_frame_that_previews_it()
    {
        var c = _app.Browser();
        Assert.DoesNotContain("class=\"mat-adminbar\"", await c.GetStringAsync("/"));          // a visitor never sees it

        await AppFactory.LoginAsync(c);
        var page = await c.GetStringAsync("/");
        Assert.Contains("class=\"mat-adminbar\"", page);
        Assert.Contains("has-adminbar", page);

        // The cloud's tiles and switcher, the assistant's live view: the browser loads the page into a frame and says so.
        var framed = new HttpRequestMessage(HttpMethod.Get, "/");
        framed.Headers.Add("Sec-Fetch-Dest", "iframe");
        var html = await (await c.SendAsync(framed)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("class=\"mat-adminbar\"", html);
        Assert.DoesNotContain("has-adminbar", html);

        // The editor's own preview stays without it, as before.
        Assert.DoesNotContain("class=\"mat-adminbar\"", await c.GetStringAsync("/?editor=1"));
    }
}
