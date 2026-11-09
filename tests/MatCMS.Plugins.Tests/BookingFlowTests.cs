using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The booking plugin, phase B2: a guest's request through the website form (no token, a honeypot, a consent box), the guest's page behind the secret
/// link, the host's offer with a discount, the guest accepting it, declining and cancelling (the nights come free), running out, the booking by phone,
/// and the three page blocks. Set up like <see cref="BookingTests"/> (same fixture class, its own application).
/// </summary>
public sealed class BookingFlowTests(BookingApp app) : IClassFixture<BookingApp>
{
    private static Dictionary<string, string> Request(string unit, string arrival, string departure, string email = "gast@example.org") => new()
    {
        ["einheit"] = unit, ["anreise"] = arrival, ["abreise"] = departure, ["erwachsene"] = "2", ["kinder"] = "0",
        ["name"] = "Anna Gast", ["email"] = email, ["telefon"] = "+49 123", ["nachricht"] = "Wir kommen mit dem Rad.", ["consent"] = "1"
    };

    /// <summary>Posts the website form as a visitor would (no token — the block cannot carry one). Returns the answer.</summary>
    private async Task<HttpResponseMessage> AskAsync(Dictionary<string, string> form)
    {
        await app.AdminAsync();
        return await app.App.Browser().PostAsync("/buchung/anfrage", new FormUrlEncodedContent(form));
    }

    private async Task<(string Path, long Id)> RequestAsync(string arrival, string departure, string unit = "apartment-meerblick")
    {
        var res = await AskAsync(Request(unit, arrival, departure));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var path = res.Headers.Location!.OriginalString;
        Assert.StartsWith("/buchung/gast/", path);
        var admin = await app.AdminAsync();
        var list = await admin.GetStringAsync("/admin/p/booking/bookings?tab=alle");
        var id = Regex.Matches(list, "/admin/p/booking/bookings/(\\d+)\"").Select(m => long.Parse(m.Groups[1].Value)).Max();
        return (path.Split('?')[0], id);
    }

    private async Task<bool> FreeAsync(string unit, string a, string d)
    {
        var json = JsonNode.Parse(await app.App.Browser().GetStringAsync($"/buchung/preis?einheit={unit}&anreise={a}&abreise={d}"))!;
        return json["available"]!.GetValue<bool>();
    }

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<(script|style)[\\s\\S]*?</\\1>", " "), "<[^>]+>", " "));

    [Fact]
    public async Task A_request_from_the_website_holds_the_nights_and_opens_the_guests_own_page()
    {
        var (path, id) = await RequestAsync("2030-04-01", "2030-04-05");
        var guest = Text(await app.App.Browser().GetStringAsync(path + "?neu=1"));
        Assert.Contains("Ihre Anfrage", guest);
        Assert.Contains("vorgemerkt bis", guest);
        Assert.Matches(@"Nr\. 2030-\d{4}", guest);                                             // numbered per year of arrival
        Assert.Contains("4 Nächte Nebensaison", guest);
        Assert.False(await FreeAsync("apartment-meerblick", "2030-04-03", "2030-04-06"));
        Assert.False(await FreeAsync("ganze-villa", "2030-04-03", "2030-04-06"));
        Assert.True(await FreeAsync("apartment-garten", "2030-04-03", "2030-04-06"));

        var admin = await app.AdminAsync();
        var list = Text(await admin.GetStringAsync("/admin/p/booking/bookings"));
        Assert.Contains("Anna Gast", list);
        Assert.Contains("Anfrage", list);
        var detail = Text(await admin.GetStringAsync("/admin/p/booking/bookings/" + id));
        Assert.Contains("Wir kommen mit dem Rad.", detail);
        Assert.Contains("Anfrage über die Website", detail);
        Assert.Contains("Mail an den Gast", detail);                                            // tried — there is no mail set up in a test, which is written down too
        Assert.Equal(HttpStatusCode.NotFound, (await app.App.Browser().GetAsync("/buchung/gast/" + new string('x', 43))).StatusCode);
    }

    [Fact]
    public async Task A_request_without_consent_from_a_bot_for_taken_nights_or_against_the_rules_is_not_kept()
    {
        var noConsent = Request("apartment-garten", "2030-04-10", "2030-04-14"); noConsent.Remove("consent");
        Assert.Contains("zustimmen", Text(await (await AskAsync(noConsent)).Content.ReadAsStringAsync()));
        var bot = Request("apartment-garten", "2030-04-10", "2030-04-14"); bot["website"] = "http://spam.example";
        Assert.Contains("Danke", Text(await (await AskAsync(bot)).Content.ReadAsStringAsync()));
        Assert.True(await FreeAsync("apartment-garten", "2030-04-10", "2030-04-14"));           // neither held anything

        var tooShort = Request("apartment-garten", "2030-04-10", "2030-04-11");
        Assert.Contains("mindestens 3 Nächte", Text(await (await AskAsync(tooShort)).Content.ReadAsStringAsync()));

        await RequestAsync("2030-04-20", "2030-04-24", "apartment-garten");
        var again = await AskAsync(Request("apartment-garten", "2030-04-22", "2030-04-26", "zweiter@example.org"));
        Assert.Contains("nicht mehr frei", Text(await again.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task The_host_offers_with_a_discount_and_the_guest_books_through_the_link()
    {
        var (path, id) = await RequestAsync("2030-05-02", "2030-05-06");
        var admin = await app.AdminAsync();
        var res = await BookingApp.PostAsync(admin, "/admin/p/booking/bookings/" + id, new() { ["action"] = "offer", ["adjust"] = "-50", ["adjust_text"] = "Stammgast", ["days"] = "3" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var guest = app.App.Browser();
        var page = await guest.GetStringAsync(path);
        Assert.Contains("Unser Angebot", Text(page));
        Assert.Contains("Stammgast", Text(page));
        Assert.Contains("422 €", Text(page));                                                    // 4 × 100 + 60 + 1,50 × 2 × 4 − 50
        var accept = await guest.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "accept", ["__RequestVerificationToken"] = AppFactory.Token(page)!
        }));
        Assert.Equal(HttpStatusCode.Redirect, accept.StatusCode);
        Assert.Contains("Ihre Buchung ist bestätigt", Text(await guest.GetStringAsync(path)));
        var detail = Text(await admin.GetStringAsync("/admin/p/booking/bookings/" + id));
        Assert.Contains("bestätigt", detail);
        Assert.Contains("Angebot vom Gast angenommen", detail);
        Assert.False(await FreeAsync("apartment-meerblick", "2030-05-03", "2030-05-05"));

        // accepting twice changes nothing; an accept without the page's token is refused by the host
        Assert.Equal(HttpStatusCode.BadRequest, (await app.App.Browser().PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "accept" }))).StatusCode);
    }

    [Fact]
    public async Task Declining_a_request_and_cancelling_a_booking_free_the_nights()
    {
        var admin = await app.AdminAsync();
        var (path, id) = await RequestAsync("2030-06-02", "2030-06-06");
        await BookingApp.PostAsync(admin, "/admin/p/booking/bookings/" + id, new() { ["action"] = "decline", ["reason"] = "Schon vergeben.", ["notify"] = "true" });
        Assert.True(await FreeAsync("apartment-meerblick", "2030-06-02", "2030-06-06"));
        Assert.Contains("nicht annehmen", Text(await app.App.Browser().GetStringAsync(path)));

        var (_, id2) = await RequestAsync("2030-06-10", "2030-06-14");
        await BookingApp.PostAsync(admin, "/admin/p/booking/bookings/" + id2, new() { ["action"] = "confirm", ["notify"] = "true" });
        Assert.False(await FreeAsync("apartment-meerblick", "2030-06-10", "2030-06-14"));
        await BookingApp.PostAsync(admin, "/admin/p/booking/bookings/" + id2, new() { ["action"] = "cancel", ["reason"] = "Auf Wunsch des Gastes." });
        Assert.True(await FreeAsync("apartment-meerblick", "2030-06-10", "2030-06-14"));
        // a cancelled booking cannot be confirmed again — the one list of moves says no
        await BookingApp.PostAsync(admin, "/admin/p/booking/bookings/" + id2, new() { ["action"] = "confirm" });
        Assert.Contains("storniert", Text(await admin.GetStringAsync("/admin/p/booking/bookings/" + id2)));
        Assert.True(await FreeAsync("apartment-meerblick", "2030-06-10", "2030-06-14"));
    }

    [Fact]
    public async Task An_unanswered_request_runs_out_and_frees_the_calendar()
    {
        var (path, id) = await RequestAsync("2030-09-10", "2030-09-14");
        using (var scope = app.App.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRaw("UPDATE p_booking_bookings SET hold_until = '2020-01-01 00:00:00' WHERE id = " + id);
        using (var scope = app.App.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<PluginJobService>().RunNowAsync("booking", "expire"));
        Assert.True(await FreeAsync("apartment-meerblick", "2030-09-10", "2030-09-14"));
        Assert.Contains("abgelaufen", Text(await app.App.Browser().GetStringAsync(path)));
    }

    [Fact]
    public async Task A_booking_by_phone_may_break_the_minimum_stay_but_never_takes_a_taken_night()
    {
        var admin = await app.AdminAsync();
        var units = await admin.GetStringAsync("/admin/p/booking/units");
        var garten = Regex.Match(units, "/admin/p/booking/units/(\\d+)\">Apartment Garten").Groups[1].Value;
        var res = await BookingApp.PostAsync(admin, "/admin/p/booking/bookings/new", new()
        {
            ["unit"] = garten, ["arrival"] = "2030-10-01", ["departure"] = "2030-10-02", ["adults"] = "2", ["children"] = "0", ["name"] = "Herr Telefon", ["notify"] = "true"
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Matches("/bookings/\\d+$", res.Headers.Location!.OriginalString);                // one night in a 3-night season: the host may
        Assert.False(await FreeAsync("apartment-garten", "2030-10-01", "2030-10-02"));
        var again = await BookingApp.PostAsync(admin, "/admin/p/booking/bookings/new", new()
        {
            ["unit"] = garten, ["arrival"] = "2030-09-30", ["departure"] = "2030-10-03", ["adults"] = "2", ["name"] = "Frau Doppelt"
        });
        Assert.EndsWith("/bookings/new", again.Headers.Location!.OriginalString);               // refused, back to the form
        Assert.DoesNotContain("Frau Doppelt", await admin.GetStringAsync("/admin/p/booking/bookings?tab=alle"));
    }

    [Fact]
    public async Task The_three_blocks_draw_the_form_the_calendar_and_the_units_on_a_page()
    {
        await app.AdminAsync();
        await RequestAsync(DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd"), DateTime.UtcNow.AddDays(13).ToString("yyyy-MM-dd"), "apartment-garten");
        using (var scope = app.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var page = new Page { Title = "Anfragen", Slug = "anfragen-test", Locale = "de", IsPublished = true, TranslationGroup = Guid.NewGuid().ToString("N") };
            page.Blocks.Add(new ContentBlock { BlockType = "booking_units", DataJson = "{\"heading\":\"Wohnen bei uns\"}", SortOrder = 0 });
            page.Blocks.Add(new ContentBlock { BlockType = "booking_calendar", DataJson = "{\"unit\":\"apartment-garten\",\"months\":\"3\"}", SortOrder = 1 });
            page.Blocks.Add(new ContentBlock { BlockType = "booking_request", DataJson = "{\"heading\":\"Jetzt anfragen\"}", SortOrder = 2 });
            db.Pages.Add(page);
            await db.SaveChangesAsync();
        }
        var html = await app.App.Browser().GetStringAsync("/anfragen-test?einheit=apartment-garten");
        Assert.Contains("Wohnen bei uns", html);
        Assert.Contains("ab 70 €", WebUtility.HtmlDecode(html));                                  // the lowest of base price and season prices
        Assert.Contains("?einheit=apartment-garten#anfrage", WebUtility.HtmlDecode(html));
        Assert.Contains("class=\"bk-taken\"", html);                                              // the request above holds nights in the calendar
        Assert.Contains("action=\"/buchung/anfrage\"", html);
        Assert.Contains("<option value=\"apartment-garten\" selected>", html);                    // ?einheit= preselects the unit
        Assert.Contains("name=\"extra_", html);                                                   // the optional pet fee
        Assert.DoesNotContain("Anna Gast", html);                                                 // a calendar shows nights, never guests
    }
}
