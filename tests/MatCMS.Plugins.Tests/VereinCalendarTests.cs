using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The club plugin's calendar view (1.18): Admin → Verein → Kalender as MatUi's schedule with a filter by type, /mitglied/kalender with only what the member
/// may see, the feeds without personal data — and the member area's pages in the block editor (roles "area" and "signin"), which look as before until an
/// editor changes them and fall back to the old frame when switched off.
/// </summary>
public class VereinCalendarTests : IClassFixture<VereinApp>
{
    private readonly VereinApp _app;
    public VereinCalendarTests(VereinApp app) => _app = app;

    private static string Text(string html) => WebUtility.HtmlDecode(html);

    /// <summary>A club member through Admin → Verein → Mitglieder → Neu, with date of birth and "Mitglied seit".</summary>
    private async Task JoinAsync(string who, string role, string born = "", string joined = "")
    {
        await _app.AsAsync(who);
        var admin = await _app.AsAsync("admin");
        var res = await VereinApp.PostFormAsync(admin, "/admin/p/verein/members/new", new()
        {
            ["first"] = VereinApp.Cap(who), ["last"] = "Kalender", ["email"] = who + "@verein.example", ["roles"] = role, ["access"] = "true", ["born"] = born, ["joined"] = joined
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private async Task<string> NewEventAsync(string title, DateTime day, bool isPublic, string status = "published", string kind = "Umzug")
    {
        var admin = await _app.AsAsync("admin");
        var f = new Dictionary<string, string> { ["title"] = title, ["kind"] = kind, ["starts"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T14:00", ["place"] = "Kalenderdorf", ["status"] = status };
        if (isPublic) f["public"] = "true";
        var res = await VereinApp.PostFormAsync(admin, "/admin/p/verein/events/new", f);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return Regex.Match(res.Headers.Location!.OriginalString, "events/(\\d+)").Groups[1].Value;
    }

    [Fact]
    public async Task The_calendar_shows_events_meetings_birthdays_and_anniversaries_filtered_by_type_and_members_only_what_they_may_see()
    {
        var day = DateTime.UtcNow.Date.AddDays(6);
        var month = day.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var born = day.AddYears(-40).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var joined = day.AddYears(-25).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await JoinAsync("kalle", "Mitglied", born, joined);
        await JoinAsync("vera", "Vorsitzende/r");
        await NewEventAsync("Kalenderumzug öffentlich", day, true);
        await NewEventAsync("Kalenderprobe intern", day, false);
        await NewEventAsync("Kalenderentwurf", day, true, "draft");
        var admin = await _app.AsAsync("admin");
        Assert.Equal(HttpStatusCode.Redirect, (await VereinApp.PostFormAsync(admin, "/admin/p/verein/meetings/new", new()
        {
            ["title"] = "Kalendersitzung", ["kind"] = "Vorstandssitzung", ["starts"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T19:30", ["place"] = "Vereinsheim",
            ["audience"] = "board", ["status"] = "planned", ["agenda"] = "1. Begrüßung"
        })).StatusCode);

        // the back office: a menu entry, MatUi's schedule with everything, the type tabs filter it
        var cal = Text(await admin.GetStringAsync("/admin/p/verein/calendar"));
        Assert.Contains("/admin/p/verein/calendar", await admin.GetStringAsync("/admin/p/verein/dashboard"));
        Assert.Contains("data-schedule", cal);
        foreach (var s in new[] { "Kalenderumzug öffentlich", "Kalenderprobe intern", "Kalenderentwurf", "Kalendersitzung", "Kalle Kalender wird 40", "Kalle Kalender: 25 Jahre im Verein", "Entwurf" })
            Assert.Contains(s, cal);
        var births = Text(await admin.GetStringAsync("/admin/p/verein/calendar?typ=geburtstage"));
        Assert.Contains("Kalle Kalender wird 40", births);
        Assert.DoesNotContain("Kalenderumzug öffentlich", births);
        Assert.DoesNotContain("Kalendersitzung", births);

        // a plain member: events that are not drafts, no board meeting, no birthdays (the club shows dates of birth only to the board), no anniversaries
        var kalle = await _app.AsAsync("kalle");
        var mine = Text(await kalle.GetStringAsync("/mitglied/kalender?monat=" + month));
        Assert.Contains("Kalenderumzug öffentlich", mine);
        Assert.Contains("Kalenderprobe intern", mine);
        Assert.DoesNotContain("Kalenderentwurf", mine);
        Assert.DoesNotContain("Kalendersitzung", mine);
        Assert.DoesNotContain("wird 40", mine);
        Assert.DoesNotContain("Jahre im Verein", mine);
        Assert.DoesNotContain("typ=geburtstage", mine);                                    // the type is not even offered
        Assert.DoesNotContain("wird 40", Text(await kalle.GetStringAsync("/mitglied/kalender?monat=" + month + "&typ=geburtstage")));
        Assert.Contains("class=\"vm-cal-grid\"", mine);
        Assert.Contains("/mitglied/kalender", await kalle.GetStringAsync("/mitglied/termine"));
        Assert.Contains("/mitglied/kalender", await kalle.GetStringAsync("/"));            // in the site's "Mitglieder" menu

        // the board: the meeting, the birthday and the anniversary; the list view and the type filter
        var vera = await _app.AsAsync("vera");
        var board = Text(await vera.GetStringAsync("/mitglied/kalender?monat=" + month));
        Assert.Contains("Kalendersitzung", board);
        Assert.Contains("Kalle Kalender wird 40", board);
        Assert.Contains("25 Jahre im Verein", board);
        var list = Text(await vera.GetStringAsync("/mitglied/kalender?monat=" + month + "&typ=sitzungen&ansicht=liste"));
        Assert.Contains("Kalendersitzung", list);
        Assert.DoesNotContain("Kalenderumzug öffentlich", list);
        Assert.DoesNotContain("class=\"vm-cal-grid\"", list);
        // a guest is sent to the sign-in
        var guest = await (await _app.AsAsync("guest")).GetAsync("/mitglied/kalender");
        Assert.Equal(HttpStatusCode.Redirect, guest.StatusCode);

        // the feeds: public events only (and only one kind with ?art=), never a birthday or a meeting
        var pub = await (await _app.AsAsync("guest")).GetStringAsync("/verein/termine.ics");
        Assert.Contains("Kalenderumzug", pub);
        Assert.DoesNotContain("Kalenderprobe intern", pub);
        Assert.DoesNotContain("Kalle", pub);
        Assert.DoesNotContain("Kalendersitzung", pub);
        await NewEventAsync("Kalenderball öffentlich", day, true, kind: "Ball");
        var balls = await (await _app.AsAsync("guest")).GetStringAsync("/verein/termine.ics?art=Ball");
        Assert.Contains("Kalenderball", balls);
        Assert.DoesNotContain("Kalenderumzug", balls);
        await VereinApp.PostFormAsync(vera, "/mitglied/profil", new() { ["action"] = "calendar" });
        var feed = Regex.Match(await vera.GetStringAsync("/mitglied/kalender"), "/verein/kalender/[0-9a-f]{40}\\.ics").Value;
        Assert.NotEmpty(feed);
        var meetingsOnly = await (await _app.AsAsync("guest")).GetStringAsync(feed + "?typ=sitzungen");
        Assert.Contains("Kalendersitzung", meetingsOnly);
        Assert.DoesNotContain("Kalenderprobe", meetingsOnly);
        Assert.DoesNotContain("wird 40", await (await _app.AsAsync("guest")).GetStringAsync(feed));
    }

    [Fact]
    public async Task The_member_area_is_a_block_page_that_looks_as_before_and_an_editor_can_lay_it_out()
    {
        await JoinAsync("bruno", "Mitglied");
        var pages = _app.Db(db => db.Pages.Include(p => p.Blocks).AsNoTracking().Where(p => p.OwnerPlugin == "verein").ToList());
        Assert.Equal(new[] { "area", "signin" }, pages.Where(p => p.Locale == "de").Select(p => p.PluginRole!).OrderBy(x => x).ToArray());
        Assert.All(pages, p => { Assert.Equal("_verein-" + p.PluginRole, p.Slug); Assert.True(p.IsPublished); Assert.Single(p.Blocks); Assert.Equal("verein_slot", p.Blocks.Single().BlockType); });

        // untouched: the same frame as before — one band, the head with the member's chip, the content
        var bruno = await _app.AsAsync("bruno");
        var profile = await bruno.GetStringAsync("/mitglied/profil");
        Assert.Single(Regex.Matches(profile, "<section class=\"section vm\"><div class=\"container\"><header class=\"vm-head\">"));
        Assert.Contains("class=\"vm-me\"", profile);
        var signin = await _app.App.Browser().GetStringAsync("/mitglied");
        Assert.Contains("<section class=\"section vm\"><div class=\"container\"><div class=\"vm-card vm-login\">", signin);
        Assert.Contains("action=\"/anmelden\"", signin);

        // an editor puts a heading with a variable above the area's frame, and lays the sign-in out with the form as a piece of its own
        var area = pages.First(p => p.PluginRole == "area" && p.Locale == "de");
        var login = pages.First(p => p.PluginRole == "signin" && p.Locale == "de");
        _app.Db(db =>
        {
            db.ContentBlocks.Add(new ContentBlock { PageId = area.Id, BlockType = "el-heading", SortOrder = -1, DataJson = "{\"text\":\"Servus {{area.first_name}} ({{area.title}})\",\"level\":\"h2\"}" });
            var slot = db.ContentBlocks.First(b => b.PageId == login.Id);
            slot.DataJson = "{\"slot\":\"signin.form_html\"}";
            db.ContentBlocks.Add(new ContentBlock { PageId = login.Id, BlockType = "el-heading", SortOrder = -1, DataJson = "{\"text\":\"Willkommen beim {{signin.club}}\",\"level\":\"h1\"}" });
            return db.SaveChanges();
        });
        var laidOut = Text(await bruno.GetStringAsync("/mitglied/profil"));
        Assert.Contains("Servus Bruno (Mein Profil)", laidOut);
        Assert.Contains("class=\"vm-me\"", laidOut);
        var newSignin = await _app.App.Browser().GetStringAsync("/mitglied");
        Assert.Contains("Willkommen beim", Text(newSignin));
        Assert.Contains("action=\"/anmelden\"", newSignin);                                   // the form stays whole, with its token
        Assert.Contains("__RequestVerificationToken", newSignin);
        Assert.Contains("class=\"vm verein-slot\"", newSignin);                               // a piece in the member area's look
        // a slot naming something that is no piece of the plugin draws nothing
        _app.Db(db => { db.ContentBlocks.First(b => b.PageId == login.Id && b.BlockType == "verein_slot").DataJson = "{\"slot\":\"site.name\"}"; return db.SaveChanges(); });
        Assert.DoesNotContain("action=\"/anmelden\"", await _app.App.Browser().GetStringAsync("/mitglied"));

        // switched off: the route draws its own frame again
        _app.Db(db => { foreach (var p in db.Pages.Where(p => p.OwnerPlugin == "verein")) p.IsPublished = false; return db.SaveChanges(); });
        var off = await bruno.GetStringAsync("/mitglied/profil");
        Assert.DoesNotContain("Servus Bruno", Text(off));
        Assert.Contains("<section class=\"section vm\"><div class=\"container\"><header class=\"vm-head\">", off);
        Assert.Contains("action=\"/anmelden\"", await _app.App.Browser().GetStringAsync("/mitglied"));
        // the page has no address of its own
        Assert.Equal(HttpStatusCode.NotFound, (await _app.App.Browser().GetAsync("/_verein-area")).StatusCode);
    }
}
