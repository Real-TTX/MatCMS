using System.IO.Compression;
using System.Net;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The way past the maintenance page with a PIN (<see cref="MaintenanceAccess"/>): the right PIN gives a pass cookie, a wrong one does not,
/// three wrong ones lock the address out — the right PIN included —, no PIN means no form, and the admin passes as before.
/// </summary>
public class MaintenanceAccessTests : IClassFixture<AppFactory>
{
    private const string Pin = "4711-Fasnet";
    private readonly AppFactory _app;

    public MaintenanceAccessTests(AppFactory app)
    {
        _app = app;
        _app.Services.GetRequiredService<MaintenanceAccess>().Reset();   // the test server's clients share one address
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

    private async Task<string?> GetAsync(string key)
    {
        using var scope = _app.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key))?.Value;
    }

    private async Task WithMaintenanceAsync(string? pin, Func<Task> body)
    {
        await SetAsync(SettingKeys.MaintenanceEnabled, "1");
        await SetAsync(SettingKeys.MaintenancePin, pin is null ? null : MaintenanceAccess.Hash(pin));
        try { await body(); }
        finally
        {
            await SetAsync(SettingKeys.MaintenanceEnabled, null);
            await SetAsync(SettingKeys.MaintenancePin, null);
        }
    }

    /// <summary>Opens the maintenance page at <paramref name="path"/> and posts <paramref name="pin"/> with the page's own token.</summary>
    private static async Task<HttpResponseMessage> EnterAsync(HttpClient c, string pin, string path = "/")
    {
        var page = await c.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, page.StatusCode);
        var token = AppFactory.Token(await page.Content.ReadAsStringAsync());
        Assert.NotNull(token);
        return await AppFactory.PostAsync(c, path, new Dictionary<string, string>
        {
            [MaintenanceAccess.PinField] = pin, ["__RequestVerificationToken"] = token!
        });
    }

    [Fact]
    public async Task The_right_pin_opens_the_site_for_this_browser_only()
    {
        await WithMaintenanceAsync(Pin, async () =>
        {
            var friend = _app.Browser();
            var res = await EnterAsync(friend, Pin, "/?x=1");
            Assert.Equal(HttpStatusCode.SeeOther, res.StatusCode);
            Assert.Equal("/?x=1", res.Headers.Location?.OriginalString);
            var cookie = string.Join(";", res.Headers.GetValues("Set-Cookie"));
            Assert.Contains(MaintenanceAccess.CookieName + "=", cookie);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Pin, cookie);

            Assert.Equal(HttpStatusCode.OK, (await friend.GetAsync("/")).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _app.Browser().GetAsync("/")).StatusCode);

            // A new PIN ends the passes handed out under the old one.
            await SetAsync(SettingKeys.MaintenancePin, MaintenanceAccess.Hash("neuer-pin"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await friend.GetAsync("/")).StatusCode);
        });
    }

    [Fact]
    public async Task A_wrong_pin_does_not_open_the_site()
    {
        await WithMaintenanceAsync(Pin, async () =>
        {
            var c = _app.Browser();
            var res = await EnterAsync(c, "1234");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            Assert.Contains("nicht richtig", await res.Content.ReadAsStringAsync());
            Assert.False(res.Headers.TryGetValues("Set-Cookie", out var set) && set.Any(s => s.StartsWith(MaintenanceAccess.CookieName + "=")));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c.GetAsync("/")).StatusCode);

            // Without the page's token the right PIN is not taken either.
            var forged = await AppFactory.PostAsync(_app.Browser(), "/", new Dictionary<string, string> { [MaintenanceAccess.PinField] = Pin });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, forged.StatusCode);
        });
    }

    [Fact]
    public async Task Three_wrong_entries_lock_the_address_out_even_for_the_right_pin()
    {
        await WithMaintenanceAsync(Pin, async () =>
        {
            var c = _app.Browser();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await EnterAsync(c, "1111")).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await EnterAsync(c, "2222")).StatusCode);
            var third = await EnterAsync(c, "3333");
            Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
            Assert.Contains("Zu viele Versuche", await third.Content.ReadAsStringAsync());

            var right = await EnterAsync(c, Pin);
            Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c.GetAsync("/")).StatusCode);

            // After the lock-out the right PIN works again.
            var access = _app.Services.GetRequiredService<MaintenanceAccess>();
            access.Now = () => DateTimeOffset.UtcNow + MaintenanceAccess.LockDuration + TimeSpan.FromSeconds(1);
            try { Assert.Equal(HttpStatusCode.SeeOther, (await EnterAsync(c, Pin)).StatusCode); }
            finally { access.Now = () => DateTimeOffset.UtcNow; }
        });
    }

    [Fact]
    public async Task Without_a_pin_the_page_has_no_form_and_a_posted_pin_does_nothing()
    {
        await WithMaintenanceAsync(null, async () =>
        {
            var c = _app.Browser();
            var page = await c.GetAsync("/");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.DoesNotContain(MaintenanceAccess.PinField, html);
            Assert.DoesNotContain("m-unlock", html);
            var post = await AppFactory.PostAsync(c, "/", new Dictionary<string, string> { [MaintenanceAccess.PinField] = "" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, post.StatusCode);
        });
    }

    [Fact]
    public async Task The_admin_passes_as_before_and_sets_the_pin_as_a_hash_that_no_backup_carries()
    {
        await WithMaintenanceAsync(null, async () =>
        {
            var admin = _app.Browser();
            await AppFactory.LoginAsync(admin);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/")).StatusCode);

            var settings = await admin.GetStringAsync("/admin/settings");
            var save = await AppFactory.PostAsync(admin, "/admin/settings?handler=Maintenance", new Dictionary<string, string>
            {
                [$"Values[{SettingKeys.MaintenanceEnabled}]"] = "1", ["MaintenancePin"] = Pin,
                ["__RequestVerificationToken"] = AppFactory.Token(settings) ?? ""
            });
            Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
            var stored = await GetAsync(SettingKeys.MaintenancePin);
            Assert.NotNull(stored);
            Assert.DoesNotContain(Pin, stored);
            Assert.True(MaintenanceAccess.Verify(Pin, stored));
            Assert.Equal("1", await GetAsync(SettingKeys.MaintenanceEnabled));

            // Saving again with an empty field keeps it; the settings page never shows the hash.
            settings = await admin.GetStringAsync("/admin/settings");
            Assert.DoesNotContain(stored!, settings);
            await AppFactory.PostAsync(admin, "/admin/settings?handler=Maintenance", new Dictionary<string, string>
            {
                [$"Values[{SettingKeys.MaintenanceEnabled}]"] = "1", ["MaintenancePin"] = "",
                ["__RequestVerificationToken"] = AppFactory.Token(settings) ?? ""
            });
            Assert.Equal(stored, await GetAsync(SettingKeys.MaintenancePin));

            using (var scope = _app.Services.CreateScope())
            {
                var backup = await scope.ServiceProvider.GetRequiredService<ContentTransferService>().ExportAsync(new ContentTransferService.BackupOptions());
                using var zip = new ZipArchive(new MemoryStream(backup), ZipArchiveMode.Read);
                using var r = new StreamReader(zip.GetEntry("content.json")!.Open());
                var json = await r.ReadToEndAsync();
                Assert.DoesNotContain(SettingKeys.MaintenancePin, json);
                Assert.Contains(SettingKeys.MaintenanceEnabled, json);
            }
            Assert.True(SettingKeys.IsProtected(SettingKeys.MaintenancePin));

            // Removing it takes the form off the page.
            settings = await admin.GetStringAsync("/admin/settings");
            await AppFactory.PostAsync(admin, "/admin/settings?handler=Maintenance", new Dictionary<string, string>
            {
                [$"Values[{SettingKeys.MaintenanceEnabled}]"] = "1", ["MaintenancePinClear"] = "true",
                ["__RequestVerificationToken"] = AppFactory.Token(settings) ?? ""
            });
            Assert.Equal("", await GetAsync(SettingKeys.MaintenancePin) ?? "");
            Assert.DoesNotContain(MaintenanceAccess.PinField, await (await _app.Browser().GetAsync("/")).Content.ReadAsStringAsync());
        });
    }

    [Fact]
    public async Task A_template_of_its_own_without_the_token_still_gets_the_form()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var site = scope.ServiceProvider.GetRequiredService<SiteContext>();
            var t = scope.ServiceProvider.GetRequiredService<Localizer>();
            var tpl = site.ActiveTemplate;
            var before = tpl.PartsJson;
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Templates.FirstAsync(x => x.Id == tpl.Id);
            row.PartsJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["maintenance"] = "<html><body><h1>{{maint_title}}</h1></body></html>"
            });
            await db.SaveChangesAsync();
            try
            {
                await WithMaintenanceAsync(Pin, async () =>
                {
                    var html = await (await _app.Browser().GetAsync("/")).Content.ReadAsStringAsync();
                    Assert.Contains(MaintenanceAccess.PinField, html);
                    Assert.True(html.IndexOf(MaintenanceAccess.PinField, StringComparison.Ordinal) < html.IndexOf("</body>", StringComparison.Ordinal));
                });
            }
            finally
            {
                row.PartsJson = before;
                await db.SaveChangesAsync();
            }
        }
    }
}
