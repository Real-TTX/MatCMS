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
/// The booking plugin, phase B3: the payment plan (deposit rounded up to whole euros, everything at once when the arrival is close), payments entered and
/// removed, ONE reminder per due date, the confirmation PDF (admin and the guest's link, never before the booking is confirmed), the guest card index
/// (erasing anonymises the stays and kills the links) and the occupancy chart.
/// </summary>
public sealed class BookingB3Tests(BookingApp app) : IClassFixture<BookingApp>
{
    private const string R = "/admin/p/booking/";

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<(script|style)[\\s\\S]*?</\\1>", " "), "<[^>]+>", " "));

    private async Task<string> UnitIdAsync(HttpClient admin, string name) =>
        Regex.Match(await admin.GetStringAsync(R + "units"), "/admin/p/booking/units/(\\d+)\">" + name).Groups[1].Value;

    /// <summary>A booking by phone (confirmed at once). Returns its id.</summary>
    private async Task<long> PhoneAsync(string unit, string arrival, string departure, string name, string email = "")
    {
        var admin = await app.AdminAsync();
        var res = await BookingApp.PostAsync(admin, R + "bookings/new", new()
        {
            ["unit"] = await UnitIdAsync(admin, unit), ["arrival"] = arrival, ["departure"] = departure, ["adults"] = "2", ["children"] = "0", ["name"] = name, ["email"] = email
        });
        Assert.Matches("/bookings/\\d+$", res.Headers.Location!.OriginalString);
        return long.Parse(Regex.Match(res.Headers.Location!.OriginalString, "(\\d+)$").Value);
    }

    [Fact]
    public async Task A_confirmed_booking_has_a_deposit_rounded_up_and_the_rest_and_payments_move_it_to_paid()
    {
        var admin = await app.AdminAsync();
        var id = await PhoneAsync("Apartment Meerblick", "2030-03-04", "2030-03-07", "Frau Zahlt");      // 300 + 60 + 9 = 369 €
        var page = Text(await admin.GetStringAsync(R + "bookings/" + id));
        Assert.Matches(@"Anzahlung\s+111 € bis", page);                                                  // 30 % of 369 € = 110,70 € → 111 €
        Assert.Matches(@"Rest\s+258 € bis 02\.02\.2030", page);                                          // 30 days before arrival
        Assert.Contains("unbezahlt", Text(await admin.GetStringAsync(R + "bookings?tab=alle")));

        await BookingApp.PostAsync(admin, R + "bookings/" + id, new() { ["action"] = "pay", ["amount"] = "111", ["day"] = "2026-11-10", ["method"] = "Überweisung" });
        Assert.Contains("angezahlt", Text(await admin.GetStringAsync(R + "bookings/" + id)));
        await BookingApp.PostAsync(admin, R + "bookings/" + id, new() { ["action"] = "pay", ["amount"] = "258", ["day"] = "2026-11-11", ["method"] = "Bar" });
        page = Text(await admin.GetStringAsync(R + "bookings/" + id));
        Assert.Contains("bezahlt", page);
        Assert.Contains("369 € von 369 € bezahlt", page);
        Assert.Contains("Zahlung eingetragen: 258 €", page);

        var html = await admin.GetStringAsync(R + "bookings/" + id);
        var payId = Regex.Matches(html, "name=\"action\" value=\"unpay\"[\\s\\S]*?name=\"id\" value=\"(\\d+)\"|name=\"id\" value=\"(\\d+)\"[\\s\\S]*?value=\"unpay\"")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).First();
        await BookingApp.PostAsync(admin, R + "bookings/" + id, new() { ["action"] = "unpay", ["id"] = payId });
        Assert.Contains("angezahlt", Text(await admin.GetStringAsync(R + "bookings/" + id)));
    }

    [Fact]
    public async Task A_booking_close_to_its_arrival_is_due_at_once()
    {
        var admin = await app.AdminAsync();
        var a = DateTime.UtcNow.AddDays(12);
        var id = await PhoneAsync("Apartment Garten", a.ToString("yyyy-MM-dd"), a.AddDays(4).ToString("yyyy-MM-dd"), "Herr Kurzfristig");
        var page = Text(await admin.GetStringAsync(R + "bookings/" + id));
        Assert.Contains("Gesamtbetrag", page);
        Assert.DoesNotContain("Anzahlung ", page);
    }

    [Fact]
    public async Task An_overdue_deposit_is_reminded_once_and_never_twice()
    {
        var id = await PhoneAsync("Apartment Garten", "2030-08-01", "2030-08-05", "Frau Vergesslich", "vergesslich@example.org");
        using (var scope = app.App.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRaw("UPDATE p_booking_bookings SET confirmed_at = '2026-01-01 00:00:00' WHERE id = " + id);
        for (var i = 0; i < 2; i++)
            using (var scope = app.App.Services.CreateScope())
                Assert.True(await scope.ServiceProvider.GetRequiredService<PluginJobService>().RunNowAsync("booking", "payment-reminders"));
        var page = Text(await (await app.AdminAsync()).GetStringAsync(R + "bookings/" + id));
        Assert.Single(Regex.Matches(page, "Zahlungserinnerung \\(Anzahlung\\)"));
    }

    [Fact]
    public async Task The_confirmation_is_a_pdf_for_the_admin_and_for_the_guest_once_the_booking_is_confirmed()
    {
        await app.AdminAsync();
        var guest = app.App.Browser();
        var res = await guest.PostAsync("/buchung/anfrage", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["einheit"] = "apartment-meerblick", ["anreise"] = "2030-10-20", ["abreise"] = "2030-10-24", ["erwachsene"] = "2", ["name"] = "Pia PDF", ["email"] = "pia@example.org", ["consent"] = "1"
        }));
        var path = res.Headers.Location!.OriginalString.Split('?')[0];
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(path + "/pdf")).StatusCode);              // a request has no confirmation yet

        var admin = await app.AdminAsync();
        var id = Regex.Matches(await admin.GetStringAsync(R + "bookings?tab=alle"), "/admin/p/booking/bookings/(\\d+)\"").Select(m => long.Parse(m.Groups[1].Value)).Max();
        await BookingApp.PostAsync(admin, R + "bookings/" + id, new() { ["action"] = "confirm" });
        foreach (var pdf in new[] { await admin.GetAsync(R + "bookings/" + id + "/pdf"), await guest.GetAsync(path + "/pdf") })
        {
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
            var text = Encoding.Latin1.GetString(await pdf.Content.ReadAsByteArrayAsync());
            Assert.StartsWith("%PDF", text);
            Assert.Contains("Pia PDF", text);
            Assert.Contains("Anzahlung", text);
        }
        Assert.Contains("Buchungsbestätigung (PDF)", Text(await guest.GetStringAsync(path)));
        Assert.Contains("Noch offen", Text(await guest.GetStringAsync(path)));
    }

    [Fact]
    public async Task The_guest_card_lists_the_stays_and_erasing_waits_for_the_last_stay_then_anonymises_it()
    {
        var admin = await app.AdminAsync();
        var id = await PhoneAsync("Apartment Garten", "2030-11-02", "2030-11-05", "Gerd Gast", "gerd@example.org");
        var guests = await admin.GetStringAsync(R + "guests");
        Assert.Contains("Gerd Gast", Text(guests));
        var guestId = Regex.Match(guests, "/admin/p/booking/guests/(\\d+)\">Gerd Gast").Groups[1].Value;
        Assert.Contains("Apartment Garten", Text(await admin.GetStringAsync(R + "guests/" + guestId)));

        await BookingApp.PostAsync(admin, R + "guests/" + guestId, new() { ["action"] = "erase" });
        Assert.Contains("Gerd Gast", Text(await admin.GetStringAsync(R + "guests")));                      // refused: the stay is still to come
        await BookingApp.PostAsync(admin, R + "bookings/" + id, new() { ["action"] = "cancel" });
        await BookingApp.PostAsync(admin, R + "guests/" + guestId, new() { ["action"] = "erase" });
        Assert.DoesNotContain("Gerd Gast", Text(await admin.GetStringAsync(R + "guests")));
        var booking = Text(await admin.GetStringAsync(R + "bookings/" + id));
        Assert.Contains("(gelöscht)", booking);
        Assert.DoesNotContain("gerd@example.org", booking);
    }

    [Fact]
    public async Task The_occupancy_chart_shows_bookings_blocks_and_free_days_to_book()
    {
        var admin = await app.AdminAsync();
        await PhoneAsync("Apartment Garten", "2030-12-10", "2030-12-14", "Familie Plan");
        await BookingApp.PostAsync(admin, R + "units/" + await UnitIdAsync(admin, "Apartment Meerblick"), new() { ["action"] = "block", ["from"] = "2030-12-20", ["to"] = "2030-12-23", ["reason"] = "Malerarbeiten" });
        var html = await admin.GetStringAsync(R + "plan?monat=2030-12");
        Assert.Contains("Belegungsplan Dezember 2030", Text(html));
        Assert.Matches("colspan=\"4\" class=\"bar confirmed\"[^>]*><a[^>]*>Familie Plan", html);           // four nights, one bar
        Assert.Contains("Malerarbeiten", html);
        Assert.Contains("Teil belegt", html);                                                               // the whole villa is not bookable while an apartment is taken
        Assert.Contains("bookings/new?unit=", html);
        await PhoneAsync("Ganze Villa", "2030-12-26", "2030-12-29", "Großfamilie");
        html = await admin.GetStringAsync(R + "plan?monat=2030-12");
        Assert.Equal(2, Regex.Matches(WebUtility.HtmlDecode(html), "über Ganze Villa").Count);           // the whole-house stay shows in each of its two apartments
        var form = await admin.GetStringAsync(R + "bookings/new?unit=" + await UnitIdAsync(admin, "Apartment Garten") + "&arrival=2030-12-02");
        Assert.Contains("value=\"2030-12-02\"", form);
        Assert.Contains("value=\"2030-12-09\"", form);
    }
}
