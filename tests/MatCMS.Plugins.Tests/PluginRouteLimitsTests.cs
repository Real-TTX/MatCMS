using System.Net;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// Two route options the MatCMS Store needed (docs/official-store.md §10): <see cref="PublicRouteOptions.DuringMaintenance"/> — a catalogue other servers
/// fetch keeps answering while the site shows its maintenance page — and <see cref="PublicRouteOptions.MaxBodyMegabytes"/>, which the builder allows
/// only for a signed-in member's form.
/// </summary>
public class PluginRouteLimitsTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public PluginRouteLimitsTests(AppFactory app) => _app = app;

    private async Task<(bool Loaded, string Error)> InstallAsync(string key, string code)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Plugins.FirstOrDefaultAsync(p => p.Key == key);
        if (row is null) db.Plugins.Add(row = new Models.Plugin { Key = key, Name = key, Version = "1.0", Kind = PluginKind.Script, Enabled = true, Code = code });
        else row.Code = code;
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
        var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
        return (registry.IsLoaded(key), string.Join(" | ", registry.Errors.Values));
    }

    private async Task SetAsync(string key, string? value)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (value is null) { if (row is not null) db.SiteSettings.Remove(row); }
        else if (row is null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_route_marked_DuringMaintenance_keeps_answering_while_the_rest_of_the_site_shows_the_maintenance_page()
    {
        var (loaded, error) = await InstallAsync("rt-maint", """
            Plugin.Route("/rt-catalog", (ctx, ct) => Task.FromResult(PluginResult.Json(new System.Text.Json.Nodes.JsonObject { ["ok"] = true })),
                new PublicRouteOptions { Methods = PluginMethod.Get, DuringMaintenance = true });
            Plugin.Route("/rt-page", (ctx, ct) => Task.FromResult(PluginResult.Page("<p>Seite</p>")), new PublicRouteOptions { Methods = PluginMethod.Get });
            """);
        Assert.True(loaded, error);
        var visitor = _app.Browser();
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/rt-page")).StatusCode);

        await SetAsync(SettingKeys.MaintenanceEnabled, "1");
        try
        {
            var catalog = await visitor.GetAsync("/rt-catalog");
            Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
            Assert.Contains("\"ok\"", await catalog.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await visitor.GetAsync("/rt-page")).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await visitor.GetAsync("/")).StatusCode);
        }
        finally { await SetAsync(SettingKeys.MaintenanceEnabled, null); }
    }

    [Fact]
    public async Task A_bigger_body_is_refused_for_a_route_that_is_not_a_signed_in_members_form()
    {
        var (loaded, error) = await InstallAsync("rt-body", """
            Plugin.Route("/rt-upload", (ctx, ct) => Task.FromResult(PluginResult.Page("<p>x</p>")), new PublicRouteOptions { MaxBodyMegabytes = 100 });
            """);
        Assert.False(loaded);
        Assert.Contains("MaxBodyMegabytes", error);

        (loaded, error) = await InstallAsync("rt-body", """
            Plugin.Route("/rt-upload", (ctx, ct) => Task.FromResult(PluginResult.Page("<p>x</p>")),
                new PublicRouteOptions { MaxBodyMegabytes = 100, RequireMember = true, Antiforgery = true });
            """);
        Assert.True(loaded, error);
        // Nobody signed out gets as far as the bigger limit: the member check comes first.
        var post = await _app.Browser().PostAsync("/rt-upload", new FormUrlEncodedContent(new Dictionary<string, string> { ["x"] = "1" }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.StartsWith("/anmelden", post.Headers.Location?.OriginalString);
    }
}
