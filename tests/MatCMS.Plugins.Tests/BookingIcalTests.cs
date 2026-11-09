using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The booking plugin, phase B4: the calendar exchange with the portals. OUT: the unit's address (no guest in it) and a portal's own address (without the
/// nights that portal sent). IN: a pasted calendar (the test cannot reach the internet — the fetcher's refusals are tested on their own) blocks nights,
/// a later version REPLACES the earlier one, an overlap with a booking is kept and reported, removing the calendar frees its nights, new addresses kill
/// the old ones, and an address into the inner network or without https is refused.
/// </summary>
public sealed class BookingIcalTests(BookingApp app) : IClassFixture<BookingApp>
{
    private const string R = "/admin/p/booking/";

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<(script|style)[\\s\\S]*?</\\1>", " "), "<[^>]+>", " "));

    private async Task<string> UnitIdAsync(HttpClient admin, string name) =>
        Regex.Match(await admin.GetStringAsync(R + "units"), "/admin/p/booking/units/(\\d+)\">" + name).Groups[1].Value;

    private async Task<bool> FreeAsync(string unit, string a, string d) =>
        JsonNode.Parse(await app.App.Browser().GetStringAsync($"/buchung/preis?einheit={unit}&anreise={a}&abreise={d}"))!["available"]!.GetValue<bool>();

    private static string Ics(params (string From, string To, string Summary)[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Airbnb Inc//Hosting Calendar//EN\r\n"
        + string.Concat(events.Select((e, i) => $"BEGIN:VEVENT\r\nDTSTAMP:20261110T080000Z\r\nDTSTART;VALUE=DATE:{e.From}\r\nDTEND;VALUE=DATE:{e.To}\r\nUID:ev{i}-{e.From}@airbnb.com\r\n" +
                                                  $"SUMMARY:{e.Summary}\r\nEND:VEVENT\r\n"))
        + "END:VCALENDAR\r\n";

    /// <summary>The export addresses on the channels page: the unit's own first in its card, then one per portal.</summary>
    private async Task<List<string>> AddressesAsync(HttpClient admin) =>
        Regex.Matches(await admin.GetStringAsync(R + "channels"), "/buchung/ical/([a-f0-9]{40})\\.ics").Select(m => "/buchung/ical/" + m.Groups[1].Value + ".ics").Distinct().ToList();

    [Fact]
    public async Task The_units_calendar_shows_its_busy_nights_without_any_guest()
    {
        var admin = await app.AdminAsync();
        await BookingApp.PostAsync(admin, R + "bookings/new", new()
        {
            ["unit"] = await UnitIdAsync(admin, "Apartment Meerblick"), ["arrival"] = "2028-02-10", ["departure"] = "2028-02-14", ["adults"] = "2", ["name"] = "Herr Geheim", ["email"] = "geheim@example.org"
        });
        var page = await admin.GetStringAsync(R + "channels");
        var meerblick = Regex.Match(page, "Apartment Meerblick[\\s\\S]*?/buchung/ical/([a-f0-9]{40})\\.ics").Groups[1].Value;
        var ics = await app.App.Browser().GetStringAsync("/buchung/ical/" + meerblick + ".ics");
        Assert.StartsWith("BEGIN:VCALENDAR", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20280210\r\nDTEND;VALUE=DATE:20280214", ics);
        Assert.DoesNotContain("Geheim", ics);
        Assert.DoesNotContain("example.org", ics);
        Assert.Equal(HttpStatusCode.NotFound, (await app.App.Browser().GetAsync("/buchung/ical/" + new string('a', 40) + ".ics")).StatusCode);

        // the whole house's calendar is busy too while one of its apartments is
        var house = Regex.Match(page, "Ganze Villa[\\s\\S]*?/buchung/ical/([a-f0-9]{40})\\.ics").Groups[1].Value;
        Assert.Contains("DTSTART;VALUE=DATE:20280210", await app.App.Browser().GetStringAsync("/buchung/ical/" + house + ".ics"));
    }

    [Fact]
    public async Task A_portals_calendar_blocks_nights_reports_an_overlap_and_a_new_version_replaces_the_old()
    {
        var admin = await app.AdminAsync();
        var garten = await UnitIdAsync(admin, "Apartment Garten");
        await BookingApp.PostAsync(admin, R + "bookings/new", new() { ["unit"] = garten, ["arrival"] = "2028-03-20", ["departure"] = "2028-03-24", ["adults"] = "2", ["name"] = "Frau Zuerst" });

        var res = await BookingApp.PostAsync(admin, R + "channels", new()
        {
            ["action"] = "add", ["unit"] = garten, ["portal"] = "Airbnb", ["url"] = "",
            ["ics"] = Ics(("20280301", "20280305", "Reserved"), ("20280322", "20280326", "Reserved"))
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.False(await FreeAsync("apartment-garten", "2028-03-02", "2028-03-04"));
        Assert.False(await FreeAsync("ganze-villa", "2028-03-02", "2028-03-04"));                       // the whole house holds the apartment
        Assert.False(await FreeAsync("apartment-garten", "2028-03-24", "2028-03-26"));                   // the free part of the overlapping one is blocked too
        var page = Text(await admin.GetStringAsync(R + "channels"));
        Assert.Contains("überschneidet sich mit Buchung", page);
        Assert.Contains("Doppelbuchung über Airbnb", Text(await admin.GetStringAsync("/admin")));
        Assert.Contains("Airbnb: Reserved", WebUtility.HtmlDecode(await admin.GetStringAsync(R + "plan?monat=2028-03")));

        // the portal's own address leaves its own nights out; the unit's address has everything
        var html = await admin.GetStringAsync(R + "channels");
        var feedId = Regex.Match(html, "name=\"action\" value=\"run\"[\\s\\S]*?name=\"id\" value=\"(\\d+)\"|name=\"id\" value=\"(\\d+)\"[\\s\\S]*?value=\"run\"") is var m && m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        var block = Regex.Match(html, "Apartment Garten[\\s\\S]*?Airbnb");
        var tokens = Regex.Matches(block.Value + html[(block.Index + block.Length)..], "/buchung/ical/([a-f0-9]{40})\\.ics").Select(x => x.Groups[1].Value).Distinct().ToList();
        var unitIcs = await app.App.Browser().GetStringAsync("/buchung/ical/" + tokens[0] + ".ics");
        var portalIcs = await app.App.Browser().GetStringAsync("/buchung/ical/" + tokens[1] + ".ics");
        Assert.Contains("DTSTART;VALUE=DATE:20280301", unitIcs);
        Assert.DoesNotContain("DTSTART;VALUE=DATE:20280301", portalIcs);
        Assert.Contains("DTSTART;VALUE=DATE:20280320", portalIcs);                                         // our own booking does go to the portal

        // Airbnb cancelled the first reservation: the next version of the calendar frees those nights
        await BookingApp.PostAsync(admin, R + "channels", new() { ["action"] = "run", ["id"] = feedId, ["ics"] = Ics(("20280322", "20280326", "Reserved")) });
        Assert.True(await FreeAsync("apartment-garten", "2028-03-02", "2028-03-04"));
        Assert.False(await FreeAsync("apartment-garten", "2028-03-24", "2028-03-26"));

        // removing the calendar frees everything it brought — the booking stays
        await BookingApp.PostAsync(admin, R + "channels", new() { ["action"] = "remove", ["id"] = feedId });
        Assert.True(await FreeAsync("apartment-garten", "2028-03-24", "2028-03-26"));
        Assert.False(await FreeAsync("apartment-garten", "2028-03-21", "2028-03-23"));
        Assert.DoesNotContain("Doppelbuchung über Airbnb", Text(await admin.GetStringAsync("/admin")));
    }

    [Fact]
    public async Task A_calendar_is_read_leniently_folded_lines_times_and_cancelled_events()
    {
        var admin = await app.AdminAsync();
        var ics = "BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nDTSTART:20280410T150000Z\nDTEND:20280413T100000Z\nUID:a@x\nSUMMARY:CLOSED - Not\n  available\nEND:VEVENT\n" +
                  "BEGIN:VEVENT\nDTSTART;VALUE=DATE:20280420\nDTEND;VALUE=DATE:20280422\nSTATUS:CANCELLED\nUID:b@x\nEND:VEVENT\nBEGIN:VEVENT\nDTSTART;VALUE=DATE:20280425\nUID:c@x\nEND:VEVENT\nEND:VCALENDAR\n";
        await BookingApp.PostAsync(admin, R + "channels", new() { ["action"] = "add", ["unit"] = await UnitIdAsync(admin, "Apartment Meerblick"), ["portal"] = "Booking.com", ["url"] = "", ["ics"] = ics });
        Assert.False(await FreeAsync("apartment-meerblick", "2028-04-11", "2028-04-12"));                // date-times: 10th to the 13th
        Assert.True(await FreeAsync("apartment-meerblick", "2028-04-13", "2028-04-15"));
        Assert.True(await FreeAsync("apartment-meerblick", "2028-04-20", "2028-04-22"));                 // cancelled
        Assert.False(await FreeAsync("apartment-meerblick", "2028-04-25", "2028-04-26"));                // no end = one night
        Assert.Contains("Booking.com: CLOSED - Not available", WebUtility.HtmlDecode(await admin.GetStringAsync(R + "plan?monat=2028-04")));

        // two events of the same calendar on the same day (Google lists some holidays twice) are no double booking
        await BookingApp.PostAsync(admin, R + "channels", new()
        {
            ["action"] = "add", ["unit"] = await UnitIdAsync(admin, "Apartment Meerblick"), ["portal"] = "Google Kalender", ["url"] = "",
            ["ics"] = Ics(("20280501", "20280502", "Tag der Arbeit"), ("20280501", "20280502", "Maifeiertag"))
        });
        Assert.DoesNotContain("Google Kalender 01.05.2028", Text(await admin.GetStringAsync(R + "channels")));
    }

    [Theory]
    [InlineData("http://www.airbnb.com/calendar/ical/1.ics", "Nur https")]
    [InlineData("https://localhost/cal.ics", "öffentliche Internet")]
    [InlineData("https://127.0.0.1/cal.ics", "öffentliche Internet")]
    [InlineData("https://10.0.0.5/cal.ics", "öffentliche Internet")]
    [InlineData("https://[::1]/cal.ics", "öffentliche Internet")]
    [InlineData("https://169.254.169.254/latest/meta-data", "öffentliche Internet")]
    public async Task An_address_without_https_or_into_the_inner_network_is_refused(string url, string reason)
    {
        var admin = await app.AdminAsync();
        var before = (await AddressesAsync(admin)).Count;
        var res = await BookingApp.PostAsync(admin, R + "channels", new() { ["action"] = "add", ["unit"] = await UnitIdAsync(admin, "Apartment Garten"), ["portal"] = "Andere", ["name"] = "Test", ["url"] = url });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains(reason, Text(await admin.GetStringAsync(R + "channels")));                          // the flash names the reason
        Assert.Equal(before, (await AddressesAsync(admin)).Count);                                       // nothing was added
    }

    [Fact]
    public async Task New_addresses_make_the_old_ones_stop_working()
    {
        var admin = await app.AdminAsync();
        var page = await admin.GetStringAsync(R + "channels");
        var token = Regex.Match(page, "Apartment Meerblick[\\s\\S]*?/buchung/ical/([a-f0-9]{40})\\.ics").Groups[1].Value;
        Assert.Equal(HttpStatusCode.OK, (await app.App.Browser().GetAsync("/buchung/ical/" + token + ".ics")).StatusCode);
        await BookingApp.PostAsync(admin, R + "channels", new() { ["action"] = "renew", ["id"] = await UnitIdAsync(admin, "Apartment Meerblick") });
        Assert.Equal(HttpStatusCode.NotFound, (await app.App.Browser().GetAsync("/buchung/ical/" + token + ".ics")).StatusCode);
    }
}
