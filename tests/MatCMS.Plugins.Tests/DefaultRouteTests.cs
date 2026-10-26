using System.Net;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Pages;
using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The "Standardroute" (setting i18n.rootRoute): the default language at "/" or under its own prefix, the other address redirecting.</summary>
public class DefaultRouteRuleTests
{
    [Theory]
    // Standardroute on (every site so far): the default language has the plain address, its prefix sends there.
    [InlineData(true, null, null, null)]
    [InlineData(true, "kontakt", null, null)]
    [InlineData(true, null, "de", "/")]
    [InlineData(true, "kontakt", "de", "/kontakt")]
    // Standardroute off: the plain address sends to the prefix.
    [InlineData(false, null, null, "/de")]
    [InlineData(false, "home", null, "/de")]
    [InlineData(false, "kontakt", null, "/de/kontakt")]
    [InlineData(false, "kontakt", "de", null)]
    // Other languages are never moved.
    [InlineData(true, "about", "en", null)]
    [InlineData(false, "about", "en", null)]
    public void Redirect_rule(bool atRoot, string? slug, string? culture, string? expected) =>
        Assert.Equal(expected, ViewModel.LocaleRedirect("/x", slug, culture, "de", atRoot));

    [Fact]
    public void Prefix_rule()
    {
        Assert.Equal("", Localizer.PrefixFor("de", "de", true));
        Assert.Equal("", Localizer.PrefixFor(null, "de", true));
        Assert.Equal("/de", Localizer.PrefixFor("de", "de", false));
        Assert.Equal("/de", Localizer.PrefixFor("", "de", false));
        Assert.Equal("/en", Localizer.PrefixFor("en", "de", true));
        Assert.Equal("/en", Localizer.PrefixFor("en", "de", false));
    }
}

/// <summary>The whole application with the Standardroute switched off and on again. The setting is process-wide (static), so this runs
/// alone, never next to a test that draws pages.</summary>
[Collection("default-route")]
public class DefaultRouteEndToEndTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public DefaultRouteEndToEndTests(AppFactory app) => _app = app;

    [Fact]
    public async Task Off_moves_the_default_language_under_its_prefix_and_on_brings_it_back()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Pages.Add(new Page { Title = "Kontakt-Test", Slug = "kontakt-test", Locale = Localizer.DefaultCulture, IsPublished = true });
            await db.SaveChangesAsync();
        }
        var browser = _app.Browser();
        try
        {
            Localizer.SetDefaultAtRoot("0");
            var root = await browser.GetAsync("/");
            Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
            Assert.Equal("/" + Localizer.DefaultCulture, root.Headers.Location?.OriginalString);
            var page = await browser.GetAsync("/kontakt-test?x=1");
            Assert.Equal("/" + Localizer.DefaultCulture + "/kontakt-test?x=1", page.Headers.Location?.OriginalString);
            var prefixed = await browser.GetAsync("/" + Localizer.DefaultCulture + "/kontakt-test");
            Assert.Equal(HttpStatusCode.OK, prefixed.StatusCode);
            Assert.Contains("Kontakt-Test", await prefixed.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/" + Localizer.DefaultCulture)).StatusCode);

            Localizer.SetDefaultAtRoot("1");
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/kontakt-test")).StatusCode);
            var back = await browser.GetAsync("/" + Localizer.DefaultCulture + "/kontakt-test");
            Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
            Assert.Equal("/kontakt-test", back.Headers.Location?.OriginalString);
        }
        finally { Localizer.SetDefaultAtRoot("1"); }
    }
}

[CollectionDefinition("default-route", DisableParallelization = true)]
public class DefaultRouteCollection { }
