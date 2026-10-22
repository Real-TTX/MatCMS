using System.Net;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>"Mit der Cloud anmelden" behind the cloud's edge: the request names the host address, the browser is on the customer domain —
/// the flow has to run on the canonical address, or the cloud refuses the return address and the flow cookie misses the callback.</summary>
public class SsoTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public SsoTests(AppFactory app) => _app = app;

    private void Set(string key, string value)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = db.SiteSettings.FirstOrDefault(s => s.Key == key);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = value }); else row.Value = value;
        db.SaveChanges();
    }

    [Fact]
    public async Task The_flow_moves_to_the_canonical_address_first_and_asks_the_cloud_to_come_back_there()
    {
        Set(SettingKeys.SsoEnabled, "1");
        Set(SettingKeys.CloudUrl, "https://cloud.example");
        Set(SettingKeys.CloudInstanceId, "inst-1");
        Set(SettingKeys.CanonicalUrl, "https://www.beispiel-site.de/");
        var c = _app.Browser();

        // Reached on any other address (here: the test host, as the host address behind the edge): off to the canonical one first.
        var first = await c.GetAsync("/sso/start?returnUrl=/admin/pages&via=cloud");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal("https://www.beispiel-site.de/sso/start?here=1&returnUrl=%2Fadmin%2Fpages&via=cloud", first.Headers.Location!.ToString());

        // There (the flag stops the loop the edge's Host rewrite would cause): the cloud is asked to return to the canonical callback.
        var second = await c.GetAsync("/sso/start?here=1&returnUrl=/admin/pages&via=cloud");
        var to = second.Headers.Location!.ToString();
        Assert.StartsWith("https://cloud.example/oauth/authorize?", to);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("https://www.beispiel-site.de/sso/callback"), to);
        Assert.Contains(second.Headers.GetValues("Set-Cookie"), s => s.StartsWith("matcms.ssoflow="));

        // Without a canonical address the flow stays where the request is.
        Set(SettingKeys.CanonicalUrl, "");
        var plain = await c.GetAsync("/sso/start?returnUrl=/admin");
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("http://localhost/sso/callback"), plain.Headers.Location!.ToString());
    }
}
