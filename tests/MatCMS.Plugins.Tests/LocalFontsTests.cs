using MatCMS.Shared.Web;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Google Fonts from this server: no page, template or head code may still send a visitor's browser to Google.</summary>
public class LocalFontsTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public LocalFontsTests(AppFactory app) => _app = app;

    [Fact]
    public void Template_code_that_links_google_is_rewritten_to_this_server()
    {
        var css = "@import url('https://fonts.googleapis.com/css2?family=Fraunces:wght@400&display=swap'); body{font-family:Fraunces}";
        Assert.Equal("@import url('/fonts/css2?family=Fraunces:wght@400&display=swap'); body{font-family:Fraunces}", LocalFonts.Rewrite(css));

        var head = "<link rel=\"preconnect\" href=\"https://fonts.googleapis.com\">\n<link rel=\"preconnect\" href=\"https://fonts.gstatic.com\" crossorigin>\n"
                 + "<link href=\"//fonts.googleapis.com/css?family=Roboto\" rel=\"stylesheet\">";
        var rewritten = LocalFonts.Rewrite(head);
        Assert.DoesNotContain("fonts.g", rewritten);
        Assert.Contains("href=\"/fonts/css?family=Roboto\"", rewritten);
        Assert.Equal("no fonts here", LocalFonts.Rewrite("no fonts here"));
    }

    [Fact]
    public async Task No_page_of_the_site_or_the_admin_links_google_fonts()
    {
        var c = _app.Browser();
        foreach (var path in new[] { "/", "/login" })
        {
            var html = await c.GetStringAsync(path);
            Assert.DoesNotContain("fonts.googleapis.com", html);
            Assert.DoesNotContain("fonts.gstatic.com", html);
            Assert.Contains("/fonts/css2?family=", html);
        }
        await AppFactory.LoginAsync(c);
        var admin = await c.GetStringAsync("/admin");
        Assert.DoesNotContain("fonts.googleapis.com", admin);
    }

    [Fact]
    public async Task A_font_file_name_from_outside_is_refused()
    {
        var c = _app.Browser();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await c.GetAsync("/fonts/f/..%2F..%2Fappdata%2Fmatcms.db")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await c.GetAsync("/fonts/css2?x=1")).StatusCode);
    }
}
