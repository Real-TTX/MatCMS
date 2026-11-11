using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The booking plugin (plugins/booking) inside the whole application, phase B1: a house with two apartments and the whole house, three seasons that
/// overlap (low season all year, high season in summer, New Year's Eve over the turn of the year), fees and the tourist tax — set up through the admin's
/// own forms, then priced through the public price route, and occupied through blocks.
/// </summary>
public sealed class BookingApp : IDisposable
{
    public AppFactory App { get; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HttpClient? _admin;
    public Dictionary<string, long> Ids { get; } = new();

    public async Task<HttpClient> AdminAsync()
    {
        // the host's limit on anonymous plugin POSTs is per process and address — every test server here is ONE address, and the booking
        // test classes together send more than 20 a minute (the other site tests reset it the same way)
        PluginPostLimiter.Reset();
        await _gate.WaitAsync();
        try
        {
            if (_admin is not null) return _admin;
            using (var scope = App.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (!db.SiteSettings.Any(s => s.Key == SettingKeys.AllowNativePlugins)) db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.AllowNativePlugins, Value = "1" });
                var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
                var src = StoragePaths.PluginSrc(env, "booking", "0.1");
                Directory.CreateDirectory(src);
                File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "booking", "Booking.dll"), Path.Combine(src, "Booking.dll"), overwrite: true);
                var row = new Models.Plugin
                {
                    Key = "booking", Name = "Buchungen", Version = "0.1", Kind = PluginKind.Native, Entry = "Booking.dll", Enabled = true,
                    Sha256 = NativePluginHost.HashOf(Path.Combine(src, "Booking.dll")), Api = typeof(IPlugin).Assembly.GetName().Version!.Major
                };
                db.Plugins.Add(row);
                await db.SaveChangesAsync();
                await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
                var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
                Assert.True(registry.IsLoaded("booking"), "booking did not load: " + string.Join(" | ", registry.Errors.Values));
            }
            var c = App.Browser();
            await AppFactory.LoginAsync(c);
            // The back office is English until its user chooses otherwise; the texts the tests look for are German.
            await c.PostAsync("/set-language?culture=de", new StringContent(""));
            await SetUpAsync(c);
            _admin = c;
            return c;
        }
        finally { _gate.Release(); }
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient c, string pageUrl, Dictionary<string, string> fields, string? postUrl = null)
    {
        fields["__RequestVerificationToken"] = AppFactory.Token(await c.GetStringAsync(pageUrl)) ?? throw new InvalidOperationException("no token on " + pageUrl);
        return await c.PostAsync(postUrl ?? pageUrl, new FormUrlEncodedContent(fields));
    }

    private static long IdOf(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return long.Parse(Regex.Match(res.Headers.Location!.OriginalString, @"/(\d+)$").Groups[1].Value);
    }

    private async Task SetUpAsync(HttpClient c)
    {
        const string R = "/admin/p/booking/";
        Ids["house"] = IdOf(await PostAsync(c, R + "properties/new", new() { ["name"] = "Villa Test", ["country"] = "HR", ["check_in"] = "15:00", ["check_out"] = "10:00" }));
        Ids["a1"] = IdOf(await PostAsync(c, R + "units/new", new() { ["name"] = "Apartment Meerblick", ["property_id"] = Ids["house"].ToString(), ["guests_std"] = "2", ["guests_max"] = "4", ["base_price"] = "80", ["base_min_nights"] = "1", ["active"] = "true" }));
        Ids["a2"] = IdOf(await PostAsync(c, R + "units/new", new() { ["name"] = "Apartment Garten", ["property_id"] = Ids["house"].ToString(), ["guests_std"] = "2", ["guests_max"] = "2", ["base_price"] = "70", ["active"] = "true" }));
        Ids["all"] = IdOf(await PostAsync(c, R + "units/new", new()
        {
            ["name"] = "Ganze Villa", ["property_id"] = Ids["house"].ToString(), ["guests_std"] = "6", ["guests_max"] = "8", ["base_price"] = "250", ["active"] = "true",
            ["contains_" + Ids["a1"]] = "true", ["contains_" + Ids["a2"]] = "true"
        }));
        Dictionary<string, string> Season(string name, string from, string to, string rank) => new() { ["name"] = name, ["from"] = from, ["to"] = to, ["rank"] = rank, ["color"] = "#2563eb" };
        var low = Season("Nebensaison", "2030-01-01", "2030-12-31", "1");
        foreach (var (u, p) in new[] { ("a1", "100"), ("a2", "90"), ("all", "250") }) { low["price_" + Ids[u]] = p; low["min_" + Ids[u]] = "3"; }
        Ids["low"] = IdOf(await PostAsync(c, R + "seasons/new?objekt=" + Ids["house"], low));
        var high = Season("Hauptsaison", "2030-07-01", "2030-08-31", "2");
        high["price_" + Ids["a1"]] = "200"; high["min_" + Ids["a1"]] = "7"; high["days_" + Ids["a1"]] = "6"; high["week_" + Ids["a1"]] = "1.300"; high["extra_" + Ids["a1"]] = "20";
        Ids["high"] = IdOf(await PostAsync(c, R + "seasons/new?objekt=" + Ids["house"], high));
        var nye = Season("Silvester", "2030-12-28", "2031-01-01", "5");
        nye["price_" + Ids["a1"]] = "300"; nye["min_" + Ids["a1"]] = "2";
        Ids["nye"] = IdOf(await PostAsync(c, R + "seasons/new?objekt=" + Ids["house"], nye));
        Ids["clean"] = IdOf(await PostAsync(c, R + "fees/new?objekt=" + Ids["house"], new() { ["name"] = "Endreinigung", ["amount"] = "60", ["per"] = "stay", ["unit_id"] = "0" }));
        Ids["tax"] = IdOf(await PostAsync(c, R + "fees/new?objekt=" + Ids["house"], new() { ["name"] = "Kurtaxe", ["amount"] = "1,50", ["per"] = "person_night", ["unit_id"] = "0", ["tourist_tax"] = "true", ["children"] = "half" }));
        Ids["pet"] = IdOf(await PostAsync(c, R + "fees/new?objekt=" + Ids["house"], new() { ["name"] = "Haustier", ["amount"] = "10", ["per"] = "night", ["unit_id"] = "0", ["optional"] = "true" }));
    }

    public void Dispose() => App.Dispose();
}

public sealed class BookingTests(BookingApp app) : IClassFixture<BookingApp>
{
    private async Task<JsonNode> PriceAsync(string unit, string arrival, string departure, int adults = 2, int children = 0, string extras = "")
    {
        await app.AdminAsync();
        var guest = app.App.Browser();
        var res = await guest.GetAsync($"/buchung/preis?einheit={app.Ids[unit]}&anreise={arrival}&abreise={departure}&erwachsene={adults}&kinder={children}&extras={extras}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
    }

    private static string Problems(JsonNode q) => string.Join(" | ", q["problems"]!.AsArray().Select(p => p!.GetValue<string>()));
    private static long Total(JsonNode q) => q["total"]!.GetValue<long>();
    private static string Lines(JsonNode q) => string.Join(" | ", q["lines"]!.AsArray().Select(l => l!["text"]!.GetValue<string>() + "=" + l["amount"]!.GetValue<long>()));

    private static DateOnly FirstSaturday(int year, int month)
    {
        var d = new DateOnly(year, month, 1);
        while (d.DayOfWeek != DayOfWeek.Saturday) d = d.AddDays(1);
        return d;
    }

    [Fact]
    public async Task An_english_page_and_an_english_back_office_get_english_words()
    {
        await app.AdminAsync();
        // the price answer under /en/: the bill's lines and the refusals in English, the money still written the German way
        var res = await app.App.Browser().GetAsync($"/en/buchung/preis?einheit={app.Ids["a1"]}&anreise=2030-03-04&abreise=2030-03-07&erwachsene=5");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var q = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Contains("3 nights Nebensaison=30000", Lines(q));
        Assert.Contains("sleeps at most 4 people", Problems(q));
        // an admin who never chose German gets the English back office
        var en = app.App.Browser();
        await AppFactory.LoginAsync(en);
        var page = await en.GetStringAsync("/admin/p/booking/check");
        Assert.DoesNotContain("Preis prüfen", page);
        Assert.Contains("Check price", page);
    }

    [Fact]
    public async Task A_stay_in_the_low_season_costs_its_nights_the_cleaning_and_the_tourist_tax()
    {
        var q = await PriceAsync("a1", "2030-03-04", "2030-03-07");
        Assert.True(q["ok"]!.GetValue<bool>(), Problems(q));
        Assert.Equal(3, q["nights"]!.GetValue<int>());
        Assert.Equal(30000 + 6000 + 900, Total(q));                                        // 3 × 100 € + 60 € cleaning + 1,50 € × 2 persons × 3 nights
        Assert.Contains("3 Nächte Nebensaison=30000", Lines(q));
        Assert.Contains("Kurtaxe=900", Lines(q));
        Assert.Equal("369 €", q["totalText"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_week_in_the_high_season_costs_the_week_price_and_the_third_person_pays_the_surcharge()
    {
        var sat = FirstSaturday(2030, 7);
        var q = await PriceAsync("a1", sat.ToString("yyyy-MM-dd"), sat.AddDays(7).ToString("yyyy-MM-dd"), adults: 3);
        Assert.True(q["ok"]!.GetValue<bool>(), Problems(q));
        Assert.Contains("1 Woche Hauptsaison=130000", Lines(q));                              // "1.300" typed in the form = 1300 €, not 1,30 €
        Assert.Equal(130000 + 2000 * 7 + 6000 + 150 * 3 * 7, Total(q));
    }

    [Fact]
    public async Task The_high_season_wants_seven_nights_from_a_saturday_and_says_so()
    {
        var sat = FirstSaturday(2030, 7);
        var monday = await PriceAsync("a1", sat.AddDays(2).ToString("yyyy-MM-dd"), sat.AddDays(9).ToString("yyyy-MM-dd"));
        Assert.False(monday["ok"]!.GetValue<bool>());
        Assert.Contains("Anreise nur Sa", Problems(monday));
        var short5 = await PriceAsync("a1", sat.ToString("yyyy-MM-dd"), sat.AddDays(5).ToString("yyyy-MM-dd"));
        Assert.Contains("mindestens 7 Nächte", Problems(short5));
        Assert.Equal(7, short5["minNights"]!.GetValue<int>());
    }

    [Fact]
    public async Task New_years_eve_outranks_the_low_season_across_the_turn_of_the_year()
    {
        var q = await PriceAsync("a1", "2030-12-29", "2031-01-02");
        Assert.True(q["ok"]!.GetValue<bool>(), Problems(q));
        Assert.Contains("4 Nächte Silvester=120000", Lines(q));
        Assert.Equal(120000 + 6000 + 150 * 2 * 4, Total(q));
    }

    [Fact]
    public async Task A_stay_across_two_seasons_is_charged_night_by_night_and_judged_by_its_arrival()
    {
        var q = await PriceAsync("a1", "2030-06-28", "2030-07-03");                           // arrives in the low season: 3 nights minimum, any day
        Assert.True(q["ok"]!.GetValue<bool>(), Problems(q));
        Assert.Contains("3 Nächte Nebensaison=30000", Lines(q));
        Assert.Contains("2 Nächte Hauptsaison=40000", Lines(q));
        Assert.Equal(70000 + 6000 + 150 * 2 * 5, Total(q));
    }

    [Fact]
    public async Task Children_pay_half_the_tourist_tax_an_optional_fee_counts_only_when_picked_and_too_many_guests_do_not_fit()
    {
        var kids = await PriceAsync("a1", "2030-03-11", "2030-03-14", adults: 2, children: 2);
        Assert.Contains("Kurtaxe=1350", Lines(kids));                                           // (2 + 2 × ½) persons × 3 nights × 1,50 €
        var tax = kids["lines"]!.AsArray().First(l => l!["kind"]!.GetValue<string>() == "tax")!;
        Assert.Equal("13,50 €", tax["amountText"]!.GetValue<string>());                         // German by hand — the image has no culture data
        Assert.Equal("1,50 € je Person und Nacht · 2 Erw., 2 Kinder zur Hälfte · 3 Nächte", tax["detail"]!.GetValue<string>());
        Assert.DoesNotContain("Haustier", Lines(kids));
        var pet = await PriceAsync("a1", "2030-03-11", "2030-03-14", extras: app.Ids["pet"].ToString());
        Assert.Contains("Haustier=3000", Lines(pet));
        var crowd = await PriceAsync("a1", "2030-03-11", "2030-03-14", adults: 5);
        Assert.False(crowd["ok"]!.GetValue<bool>());
        Assert.Contains("höchstens 4 Personen", Problems(crowd));
    }

    [Fact]
    public async Task A_blocked_apartment_also_blocks_the_whole_house_but_not_its_neighbour()
    {
        var admin = await app.AdminAsync();
        var a1 = "/admin/p/booking/units/" + app.Ids["a1"];
        await BookingApp.PostAsync(admin, a1, new() { ["action"] = "block", ["from"] = "2030-05-10", ["to"] = "2030-05-15", ["reason"] = "Eigennutzung" });
        Assert.False((await PriceAsync("a1", "2030-05-12", "2030-05-14"))["available"]!.GetValue<bool>());
        Assert.False((await PriceAsync("all", "2030-05-12", "2030-05-15"))["available"]!.GetValue<bool>());
        Assert.True((await PriceAsync("a2", "2030-05-12", "2030-05-15"))["available"]!.GetValue<bool>());
        Assert.True((await PriceAsync("a1", "2030-05-15", "2030-05-18"))["available"]!.GetValue<bool>());     // the "to" day is free again, like a departure

        // the whole house cannot be blocked over the apartment's nights — and nothing of it is written
        var all = "/admin/p/booking/units/" + app.Ids["all"];
        await BookingApp.PostAsync(admin, all, new() { ["action"] = "block", ["from"] = "2030-05-14", ["to"] = "2030-05-17", ["reason"] = "Malerarbeiten" });
        Assert.DoesNotContain("Malerarbeiten", await admin.GetStringAsync(all));
        Assert.True((await PriceAsync("a2", "2030-05-15", "2030-05-17"))["available"]!.GetValue<bool>());

        // removing the block frees the nights for both
        var page = await admin.GetStringAsync(a1);
        var blockId = Regex.Match(page, "name=\"id\" value=\"(\\d+)\"").Groups[1].Value;
        await BookingApp.PostAsync(admin, a1, new() { ["action"] = "unblock", ["id"] = blockId });
        Assert.True((await PriceAsync("all", "2030-05-12", "2030-05-15"))["available"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_blocked_whole_house_takes_every_apartment_with_it()
    {
        var admin = await app.AdminAsync();
        await BookingApp.PostAsync(admin, "/admin/p/booking/units/" + app.Ids["all"], new() { ["action"] = "block", ["from"] = "2030-10-10", ["to"] = "2030-10-20", ["reason"] = "Hochzeit" });
        Assert.False((await PriceAsync("a1", "2030-10-12", "2030-10-15"))["available"]!.GetValue<bool>());
        Assert.False((await PriceAsync("a2", "2030-10-18", "2030-10-21"))["available"]!.GetValue<bool>());
        Assert.True((await PriceAsync("a2", "2030-10-20", "2030-10-23"))["available"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Several_blocks_of_the_same_nights_at_once_end_in_exactly_one()
    {
        var admin = await app.AdminAsync();
        var a2 = "/admin/p/booking/units/" + app.Ids["a2"];
        var token = AppFactory.Token(await admin.GetStringAsync(a2))!;
        var tasks = Enumerable.Range(0, 6).Select(i => admin.PostAsync(a2, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["action"] = "block", ["from"] = "2030-09-01", ["to"] = "2030-09-05", ["reason"] = "Parallel " + i
        }))).ToList();
        await Task.WhenAll(tasks);
        var page = await admin.GetStringAsync(a2);
        Assert.Single(Regex.Matches(page, "Parallel \\d"));
        Assert.False((await PriceAsync("a2", "2030-09-02", "2030-09-04"))["available"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_admin_shows_the_bill_and_copies_the_seasons_into_the_next_year()
    {
        var admin = await app.AdminAsync();
        var check = await admin.GetStringAsync($"/admin/p/booking/check?unit={app.Ids["a1"]}&arrival=2030-03-04&departure=2030-03-07&adults=2");
        Assert.Contains("369 €", check);
        Assert.Contains("Endreinigung", check);
        Assert.Equal(HttpStatusCode.NotFound, (await app.App.Browser().GetAsync("/buchung/preis?einheit=gibtsnicht&anreise=2030-03-04&abreise=2030-03-07")).StatusCode);

        var seasons = "/admin/p/booking/seasons?objekt=" + app.Ids["house"];
        await BookingApp.PostAsync(admin, seasons, new() { ["action"] = "copy", ["year"] = "2030" });
        Assert.Contains("01.07.2031 – 31.08.2031", Regex.Replace(await admin.GetStringAsync(seasons), "<[^>]+>", " "));
        var next = await PriceAsync("a1", "2031-03-03", "2031-03-06");
        Assert.Contains("3 Nächte Nebensaison=30000", Lines(next));                              // the copied low season with its price
    }
}
