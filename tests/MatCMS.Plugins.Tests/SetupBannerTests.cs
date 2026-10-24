using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The setup wizard's "Welcome! Set up …" is for a site somebody installed by hand. A site a cloud set up got the admin, theme, mail
/// and settings from its profile — it is not asked to go through steps that were already done.</summary>
public class SetupBannerTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public SetupBannerTests(AppFactory app) => _app = app;

    [Fact]
    public async Task A_hand_installed_site_is_invited_to_the_setup_and_a_site_a_cloud_profile_set_up_is_not()
    {
        var admin = _app.Browser();
        await AppFactory.LoginAsync(admin);
        Assert.Contains("setup-banner", await admin.GetStringAsync("/admin"));

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.CloudAppliedRevision, Value = "3" });
            await db.SaveChangesAsync();
        }
        Assert.DoesNotContain("setup-banner", await admin.GetStringAsync("/admin"));
        Assert.True(SettingKeys.IsSetupDone(k => k == SettingKeys.SetupComplete ? "1" : null));
        Assert.False(SettingKeys.IsSetupDone(k => k == SettingKeys.CloudAppliedRevision ? "0" : null));
    }
}
