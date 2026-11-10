using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The booking plugin, phase B5: the online check-in with the fields of the house's country (the test house is in Croatia — eVisitor), "remember for
/// next time", the way in shown only from the day before arrival, the mails around a stay (each once, each in its window), the cleaning list derived
/// from bookings AND portal reservations with its team calendar, and the deletion of registration data after the retention period.
/// </summary>
public sealed class BookingB5Tests(BookingApp app) : IClassFixture<BookingApp>
{
    private const string R = "/admin/p/booking/";
    private static string Day(int offset) => DateTime.UtcNow.AddHours(1).Date.AddDays(offset).ToString("yyyy-MM-dd");

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<(script|style)[\\s\\S]*?</\\1>", " "), "<[^>]+>", " "));

    private async Task<string> UnitIdAsync(HttpClient admin, string name) =>
        Regex.Match(await admin.GetStringAsync(R + "units"), "/admin/p/booking/units/(\\d+)\">" + name).Groups[1].Value;

    private async Task<long> PhoneAsync(string unit, string arrival, string departure, string name, string email, string adults = "2")
    {
        var admin = await app.AdminAsync();
        var res = await BookingApp.PostAsync(admin, R + "bookings/new", new()
        {
            ["unit"] = await UnitIdAsync(admin, unit), ["arrival"] = arrival, ["departure"] = departure, ["adults"] = adults, ["children"] = "0", ["name"] = name, ["email"] = email
        });
        return long.Parse(Regex.Match(res.Headers.Location!.OriginalString, "(\\d+)$").Value);
    }

    /// <summary>A request through the website form, confirmed by the host: returns the guest's page.</summary>
    private async Task<(string Path, long Id)> GuestBookingAsync(string arrival, string departure, string email)
    {
        var admin = await app.AdminAsync();
        var res = await app.App.Browser().PostAsync("/buchung/anfrage", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["einheit"] = "apartment-meerblick", ["anreise"] = arrival, ["abreise"] = departure, ["erwachsene"] = "2", ["name"] = "Ivana Gost", ["email"] = email, ["consent"] = "1"
        }));
        var path = res.Headers.Location!.OriginalString.Split('?')[0];
        var id = Regex.Matches(await admin.GetStringAsync(R + "bookings?tab=alle"), "/admin/p/booking/bookings/(\\d+)\"").Select(m => long.Parse(m.Groups[1].Value)).Max();
        await BookingApp.PostAsync(admin, R + "bookings/" + id, new() { ["action"] = "confirm" });
        return (path, id);
    }

    private static Dictionary<string, string> Person(int i, string first, string last) => new()
    {
        ["t" + i + "_first"] = first, ["t" + i + "_last"] = last, ["t" + i + "_gender"] = "weiblich", ["t" + i + "_born"] = "1985-04-12", ["t" + i + "_nationality"] = "HR",
        ["t" + i + "_res_country"] = "Kroatien", ["t" + i + "_city"] = "Split", ["t" + i + "_doc_type"] = "Reisepass", ["t" + i + "_doc_no"] = "P" + (1000 + i)
    };

    private async Task RunJobAsync(string name)
    {
        using var scope = app.App.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<PluginJobService>().RunNowAsync("booking", name));
    }

    private void Sql(string sql)
    {
        using var scope = app.App.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRaw(sql);
    }

    [Fact]
    public async Task The_guest_checks_in_with_the_fields_of_the_country_and_the_host_gets_csv_and_form()
    {
        var (path, id) = await GuestBookingAsync(Day(20), Day(24), "ivana@example.org");
        var guest = app.App.Browser();
        var form = await guest.GetStringAsync(path + "/checkin");
        Assert.Contains("name=\"t1_doc_no\"", form);                                                    // eVisitor asks the document of everybody
        Assert.Contains("name=\"t0_gender\"", form);

        var fields = Person(0, "Ivana", "Gost").Concat(Person(1, "Marko", "Gost")).ToDictionary(x => x.Key, x => x.Value);
        fields["t1_doc_no"] = "";
        fields["action"] = "checkin"; fields["arrival_time"] = "16–18 Uhr"; fields["travel"] = "Auto, ZG-123-AB"; fields["remember"] = "1";
        fields["__RequestVerificationToken"] = AppFactory.Token(form)!;
        var bad = await guest.PostAsync(path + "/checkin", new FormUrlEncodedContent(fields));
        Assert.Contains("Person 2 „Ausweisnummer“", Text(await bad.Content.ReadAsStringAsync()));

        fields["t1_doc_no"] = "P2002";
        var ok = await guest.PostAsync(path + "/checkin", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Contains("Ihr Check-in ist bei uns angekommen", Text(await guest.GetStringAsync(path + "?checkin=1")));

        var admin = await app.AdminAsync();
        var detail = Text(await admin.GetStringAsync(R + "bookings/" + id));
        Assert.Contains("Marko Gost", detail);
        Assert.Contains("Ankunft: 16–18 Uhr", detail);
        var csv = await admin.GetStringAsync(R + "bookings/" + id + "/travellers.csv");
        Assert.Contains("\"P2002\"", csv);
        Assert.Contains("\"12.04.1985\"", csv);
        var pdf = await admin.GetAsync(R + "bookings/" + id + "/meldeschein");
        var text = Encoding.Latin1.GetString(await pdf.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("%PDF", text);
        Assert.Contains("Marko Gost", text);
        Assert.Contains("ausgefüllt (2)", Text(await admin.GetStringAsync(R + "checkins?von=" + Day(19) + "&bis=" + Day(21))));

        // remembered on the guest card: the next stay of the same guest starts filled in
        var (path2, _) = await GuestBookingAsync(Day(40), Day(43), "ivana@example.org");
        Assert.Contains("value=\"Marko\"", await app.App.Browser().GetStringAsync(path2 + "/checkin"));
    }

    [Fact]
    public async Task The_way_in_shows_only_from_the_day_before_arrival()
    {
        var admin = await app.AdminAsync();
        Sql("UPDATE p_booking_units SET access_info = 'Schlüsselbox Code 4711', wifi = 'Villa-Gast / sonne123' WHERE slug = 'apartment-meerblick'");
        var (far, _) = await GuestBookingAsync(Day(50), Day(53), "weit@example.org");
        var farPage = Text(await app.App.Browser().GetStringAsync(far));
        Assert.DoesNotContain("4711", farPage);
        Assert.Contains("ab dem Tag vor Ihrer Anreise", farPage);
        var (near, _) = await GuestBookingAsync(Day(1), Day(3), "nah@example.org");
        var nearPage = Text(await app.App.Browser().GetStringAsync(near));
        Assert.Contains("Schlüsselbox Code 4711", nearPage);
        Assert.Contains("sonne123", nearPage);
    }

    [Fact]
    public async Task Each_mail_around_a_stay_goes_once_and_only_in_its_window()
    {
        var admin = await app.AdminAsync();
        var soon = await PhoneAsync("Apartment Garten", Day(5), Day(8), "Frau Bald", "bald@example.org");
        var tomorrow = await PhoneAsync("Apartment Garten", Day(1), Day(3), "Herr Morgen", "morgen@example.org");
        var past = await PhoneAsync("Apartment Garten", Day(-6), Day(-3), "Familie Vorbei", "vorbei@example.org");
        await RunJobAsync("guest-mails");
        await RunJobAsync("guest-mails");
        string History(long id) => Text(admin.GetStringAsync(R + "bookings/" + id).Result);
        Assert.Single(Regex.Matches(History(soon), "Mail „Check-in-Link“"));
        Assert.Contains("Mail „Zugang“", History(tomorrow));
        Assert.DoesNotContain("Check-in-Link", History(tomorrow));                                     // too late for the link: the way-in mail instead
        Assert.Contains("Mail „Danke“", History(past));
        Assert.DoesNotContain("Check-in-Link", History(past));
    }

    [Fact]
    public async Task The_cleaning_list_comes_from_bookings_and_portal_reservations_and_the_team_gets_a_calendar()
    {
        var admin = await app.AdminAsync();
        await PhoneAsync("Apartment Garten", Day(60), Day(63), "Erste Familie", "erste@example.org");
        await PhoneAsync("Apartment Garten", Day(63), Day(66), "Zweite Familie", "zweite@example.org", "3");
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART;VALUE=DATE:" + Day(68).Replace("-", "") + "\r\nDTEND;VALUE=DATE:" + Day(70).Replace("-", "") +
                  "\r\nUID:x@airbnb.com\r\nSUMMARY:Reserved\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        await BookingApp.PostAsync(admin, R + "channels", new() { ["action"] = "add", ["unit"] = await UnitIdAsync(admin, "Apartment Garten"), ["portal"] = "Airbnb", ["url"] = "", ["ics"] = ics });

        var page = Text(await admin.GetStringAsync(R + "cleaning?von=" + Day(60) + "&bis=" + Day(72)));
        Assert.Contains("Wechsel", page);                                                                  // the first family leaves the day the second arrives
        Assert.Contains("3 Pers.", page);
        Assert.Contains("Airbnb: Reserved", page);                                                         // a portal's guests need a cleaning too
        Assert.DoesNotContain("Erste Familie", page);                                                      // the list names bookings, not guests

        var html = await admin.GetStringAsync(R + "cleaning?von=" + Day(60) + "&bis=" + Day(72));
        var garten = await UnitIdAsync(admin, "Apartment Garten");
        await BookingApp.PostAsync(admin, R + "cleaning?von=" + Day(60) + "&bis=" + Day(72), new() { ["action"] = "done", ["unit"] = garten, ["day"] = Day(63) });
        Assert.Contains("erledigt", Text(await admin.GetStringAsync(R + "cleaning?von=" + Day(60) + "&bis=" + Day(72))));

        var token = Regex.Match(html, "/buchung/reinigung/([a-f0-9]{40})\\.ics").Groups[1].Value;
        var cal = await app.App.Browser().GetStringAsync("/buchung/reinigung/" + token + ".ics");
        Assert.Contains("SUMMARY:Reinigung Apartment Garten (Wechsel)", cal);
        Assert.DoesNotContain("Familie", cal);
        Assert.Equal(HttpStatusCode.NotFound, (await app.App.Browser().GetAsync("/buchung/reinigung/" + new string('b', 40) + ".ics")).StatusCode);
        Assert.StartsWith("%PDF", Encoding.Latin1.GetString(await (await admin.GetAsync(R + "cleaning/pdf?von=" + Day(60) + "&bis=" + Day(72))).Content.ReadAsByteArrayAsync())[..4]);
    }

    [Fact]
    public async Task Registration_data_is_deleted_after_the_retention_period()
    {
        var (path, id) = await GuestBookingAsync(Day(30), Day(33), "alt@example.org");
        var guest = app.App.Browser();
        var form = await guest.GetStringAsync(path + "/checkin");
        var fields = Person(0, "Ana", "Alt").Concat(Person(1, "Ivo", "Alt")).ToDictionary(x => x.Key, x => x.Value);
        fields["action"] = "checkin"; fields["__RequestVerificationToken"] = AppFactory.Token(form)!;
        await guest.PostAsync(path + "/checkin", new FormUrlEncodedContent(fields));
        var admin = await app.AdminAsync();
        Assert.Contains("Ivo Alt", Text(await admin.GetStringAsync(R + "bookings/" + id)));

        Sql("UPDATE p_booking_bookings SET arrival = '2020-01-01', departure = '2020-01-04' WHERE id = " + id);
        await RunJobAsync("checkin-purge");
        var detail = Text(await admin.GetStringAsync(R + "bookings/" + id));
        Assert.DoesNotContain("Ivo Alt", detail);
        Assert.Contains("Meldedaten nach Ablauf der Frist", detail);
    }
}
