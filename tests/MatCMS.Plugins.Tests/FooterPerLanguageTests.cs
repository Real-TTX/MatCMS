using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The footer text per language: Einstellungen → Allgemein has one field per further active language; the site shows the
/// language's own text and falls back to the general one (matcms.de showed its English footer on the German pages).</summary>
public class FooterPerLanguageTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public FooterPerLanguageTests(AppFactory app) => _app = app;

    private async Task SetupAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var languages = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.Languages);
        if (languages is null) db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.Languages, Value = "de,en" });
        else languages.Value = "de,en";
        if (!await db.Pages.AnyAsync(p => p.Slug == "home" && p.Locale == "en"))
            db.Pages.Add(new Page { Title = "Home", Slug = "home", Locale = "en", IsPublished = true });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Each_language_shows_its_own_footer_and_an_empty_one_falls_back_to_the_general_text()
    {
        await SetupAsync();
        var admin = _app.Browser();
        await AppFactory.LoginAsync(admin);

        var settings = await admin.GetStringAsync("/admin/settings");
        Assert.Contains($"name=\"Values[{SettingKeys.FooterTextFor("en")}]\"", settings);
        Assert.DoesNotContain($"name=\"Values[{SettingKeys.FooterTextFor("de")}]\"", settings); // the default language uses the general field

        async Task SaveAsync(string general, string english)
        {
            var res = await AppFactory.PostAsync(admin, "/admin/settings", new()
            {
                [$"Values[{SettingKeys.FooterText}]"] = general,
                [$"Values[{SettingKeys.FooterTextFor("en")}]"] = english,
                ["__RequestVerificationToken"] = AppFactory.Token(settings)!,
            });
            Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
        }

        await SaveAsync("© Fußzeile deutsch", "© Footer in English");
        var visitor = _app.Browser();
        var de = await visitor.GetStringAsync("/");
        var en = await visitor.GetStringAsync("/en");
        Assert.Contains("© Fußzeile deutsch", System.Net.WebUtility.HtmlDecode(de));
        Assert.DoesNotContain("© Footer in English", de);
        Assert.Contains("© Footer in English", en);
        Assert.DoesNotContain("Fußzeile deutsch", System.Net.WebUtility.HtmlDecode(en));
        Assert.Contains("© Footer in English", await admin.GetStringAsync("/admin/settings"));

        await SaveAsync("© Für alle", "");
        Assert.Contains("© Für alle", System.Net.WebUtility.HtmlDecode(await visitor.GetStringAsync("/en")));
    }
}
