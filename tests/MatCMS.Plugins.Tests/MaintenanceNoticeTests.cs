using System.Net;
using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// "Wartungsmodus geändert" (<see cref="MaintenanceNotice"/>): an admin who comes into the back office after SOMEBODY ELSE changed the maintenance
/// mode confirms it in a dialog; the one who made the change is not asked; a change from outside the back office (cloud, restore) reads as such;
/// the admins' note shows in the dialog and on the dashboard; a confirmation is for one state only.
/// </summary>
public class MaintenanceNoticeTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public MaintenanceNoticeTests(AppFactory app) => _app = app;

    private static bool Asks(string html) => html.Contains("id=\"maint-seen\"");

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

    private async Task<HttpClient> SecondAdminAsync()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.Users.AnyAsync(u => u.Username == "renate"))
            {
                db.Users.Add(new User { Username = "renate", DisplayName = "Renate", Role = "Admin",
                    PasswordHash = scope.ServiceProvider.GetRequiredService<AuthService>().HashPassword("geheim-123") });
                await db.SaveChangesAsync();
            }
        }
        var c = _app.Browser();
        var page = await c.GetStringAsync("/login");
        var res = await AppFactory.PostAsync(c, "/login", new() { ["Username"] = "renate", ["Password"] = "geheim-123", ["__RequestVerificationToken"] = AppFactory.Token(page) ?? "" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return c;
    }

    private static async Task SaveMaintenanceAsync(HttpClient c, bool on, string note)
    {
        var page = await c.GetStringAsync("/admin/settings?tab=maintenance");
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["Values[maintenance.note]"] = note,
            ["Values[maintenance.title]"] = "", ["Values[maintenance.message]"] = "" };
        if (on) form["Values[maintenance.enabled]"] = "1";
        var res = await AppFactory.PostAsync(c, "/admin/settings?handler=Maintenance", form);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    [Fact]
    public async Task Somebody_elses_change_is_confirmed_once_the_own_is_not_asked_and_one_from_outside_says_so()
    {
        var matthias = _app.Browser();
        await AppFactory.LoginAsync(matthias);
        var renate = await SecondAdminAsync();
        foreach (var c in new[] { matthias, renate }) await c.PostAsync("/set-language?culture=de", new StringContent(""));   // the wording is checked
        try
        {
            // the first look only records the state
            Assert.False(Asks(await matthias.GetStringAsync("/admin")));
            Assert.False(Asks(await renate.GetStringAsync("/admin")));

            // Matthias switches it on with a note: he is not asked, the dashboard shows the note
            await SaveMaintenanceAsync(matthias, on: true, note: "Umbau bis Freitag – bitte nichts veröffentlichen.");
            var own = await matthias.GetStringAsync("/admin");
            Assert.False(Asks(own));
            Assert.Contains("Umbau bis Freitag", own);

            // Renate is asked: who, what, the state now, the note — on every page until she confirms
            var hers = await renate.GetStringAsync("/admin/pages");
            Assert.True(Asks(hers));
            Assert.Matches(@"\S+ hat am \d{2}\.\d{2}\.\d{4} \d{2}:\d{2} den Wartungsmodus", hers);   // the seeded admin by name, and when
            Assert.Contains("Eingeschaltet", hers);
            Assert.Contains("Umbau bis Freitag", hers);
            Assert.True(Asks(await renate.GetStringAsync("/admin")));
            var fp = Regex.Match(hers, "name=\"fingerprint\" value=\"([^\"]+)\"").Groups[1].Value;
            var seen = await AppFactory.PostAsync(renate, "/admin/account?handler=MaintenanceSeen",
                new() { ["fingerprint"] = fp, ["returnUrl"] = "/admin/pages", ["__RequestVerificationToken"] = AppFactory.Token(hers)! });
            Assert.Equal("/admin/pages", seen.Headers.Location?.OriginalString);
            Assert.False(Asks(await renate.GetStringAsync("/admin")));

            // a change from outside the back office (a cloud rollout, a restore) asks both, and says where it came from
            await SetAsync(SettingKeys.MaintenanceTitle, "Bald wieder da");
            var outside = await matthias.GetStringAsync("/admin");
            Assert.True(Asks(outside));
            Assert.Contains("von außerhalb", outside);
            Assert.Contains("Überschrift der Wartungsseite", outside);

            // a confirmation is for the state the dialog showed: changed meanwhile, it is asked again
            var stale = Regex.Match(outside, "name=\"fingerprint\" value=\"([^\"]+)\"").Groups[1].Value;
            await SetAsync(SettingKeys.MaintenanceEnabled, "");
            await AppFactory.PostAsync(matthias, "/admin/account?handler=MaintenanceSeen",
                new() { ["fingerprint"] = stale, ["returnUrl"] = "/admin", ["__RequestVerificationToken"] = AppFactory.Token(outside)! });
            var again = await matthias.GetStringAsync("/admin");
            Assert.True(Asks(again));
            Assert.Contains("Ausgeschaltet", again);
        }
        finally
        {
            foreach (var k in SettingKeys.Maintenance.Append(SettingKeys.MaintenanceLastChange)) await SetAsync(k, null);
        }
    }

    [Fact]
    public void The_record_of_the_last_change_stays_out_of_backups()
    {
        Assert.True(SettingKeys.NotInBackup(SettingKeys.MaintenanceLastChange));
        Assert.False(SettingKeys.NotInBackup(SettingKeys.MaintenanceNote));   // the note is content of the site and travels
    }
}
