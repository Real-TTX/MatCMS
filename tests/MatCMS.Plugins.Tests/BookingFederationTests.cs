using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatCMS.Data;
using Microsoft.EntityFrameworkCore;
using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Two whole sites in one test: one leads the guest card index, the other attaches with its address and a site API key.</summary>
public sealed class FederationSites : IDisposable
{
    public BookingApp Lead { get; } = new();
    public BookingApp Member { get; } = new();
    public string Key { get; set; } = "";
    public void Dispose() { Lead.Dispose(); Member.Dispose(); }
}

/// <summary>
/// The booking plugin, phase B6 — the shared guest card index ("Kundenverbund"): the attached site hands its guests and stay summaries to the leading
/// site and takes back what is known there; per field the later change wins, whichever side made it; a leading site that cannot be reached stops no
/// booking, the changes wait and go later; the API answers only a key with the personal-data right and only on a site that leads.
/// </summary>
public sealed class BookingFederationTests(FederationSites sites) : IClassFixture<FederationSites>
{
    private const string R = "/admin/p/booking/";

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<(script|style)[\\s\\S]*?</\\1>", " "), "<[^>]+>", " "));

    /// <summary>The plugin's DLL is loaded into its own load context per site — the test seam is set on every loaded copy (only the attached site calls out).</summary>
    private static void Route(Func<HttpMessageHandler> factory)
    {
        foreach (var asm in AssemblyLoadContext.All.SelectMany(c => c.Assemblies).Where(a => a.GetName().Name == "Booking"))
            asm.GetType("MatCMS.Plugin.Booking.BookingFederation")?.GetProperty("HandlerFactory")?.SetValue(null, factory);
    }

    private void ToLead() => Route(() => sites.Lead.App.Server.CreateHandler());

    private sealed class Down : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("Verbindung abgelehnt");
    }

    private static async Task SettingsAsync(HttpClient admin, Dictionary<string, string> values)
    {
        values["action"] = "save-settings";
        var res = await BookingApp.PostAsync(admin, R + "_settings?group=booking.fed.group", values);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private async Task<(HttpClient Lead, HttpClient Member)> SetUpAsync()
    {
        var lead = await sites.Lead.AdminAsync();
        var member = await sites.Member.AdminAsync();
        if (sites.Key.Length == 0)
        {
            using (var scope = sites.Lead.App.Services.CreateScope())
                sites.Key = (await scope.ServiceProvider.GetRequiredService<SiteApiKeyService>().CreateAsync("Villa Sogi", new[] { "booking:personal" }, null)).Key;
            await SettingsAsync(lead, new() { ["s_federation.role"] = "lead" });
            await SettingsAsync(member, new() { ["s_federation.role"] = "member", ["s_federation.url"] = "https://villa-nika.example", ["s_federation.key"] = sites.Key });
        }
        ToLead();
        return (lead, member);
    }

    private static async Task<long> PhoneAsync(HttpClient admin, string arrival, string departure, string name, string email)
    {
        var unit = Regex.Match(await admin.GetStringAsync(R + "units"), "/admin/p/booking/units/(\\d+)\">Apartment Garten").Groups[1].Value;
        var res = await BookingApp.PostAsync(admin, R + "bookings/new", new()
        {
            ["unit"] = unit, ["arrival"] = arrival, ["departure"] = departure, ["adults"] = "2", ["children"] = "0", ["name"] = name, ["email"] = email
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return long.Parse(Regex.Match(res.Headers.Location!.OriginalString, "(\\d+)$").Value);
    }

    private static async Task<string> SyncAsync(HttpClient member)
    {
        var res = await BookingApp.PostAsync(member, R + "federation", new() { ["action"] = "sync" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return Text(await member.GetStringAsync(R + "federation"));
    }

    private static async Task<string> GuestPageAsync(HttpClient admin, string email)
    {
        var list = await admin.GetStringAsync(R + "guests");
        var id = Regex.Match(list, "/admin/p/booking/guests/(\\d+)\">[^<]*</a>(?:(?!</tr>)[\\s\\S])*?" + Regex.Escape(email)).Groups[1].Value;
        Assert.False(id.Length == 0, email + " is not in the guest list");
        return await admin.GetStringAsync(R + "guests/" + id);
    }

    private static string Field(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"" + name + "\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
    private static string Note(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"note\"[^>]*>([^<]*)</textarea>").Groups[1].Value);

    /// <summary>The plugin's pure merge, from its built DLL (the test project builds the plugin but does not reference it).</summary>
    private static Dictionary<string, object?> Merge(IReadOnlyDictionary<string, string> here, JsonObject? hereStamps, IReadOnlyDictionary<string, string> there, JsonObject? thereStamps)
    {
        var asm = System.Reflection.Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "fixtures", "booking", "Booking.dll"));
        return (Dictionary<string, object?>)asm.GetType("MatCMS.Plugin.Booking.BookingFederation")!.GetMethod("Merge")!.Invoke(null, new object?[] { here, hereStamps, there, thereStamps })!;
    }

    [Fact]
    public void The_later_change_wins_field_by_field()
    {
        var early = new JsonObject { ["name"] = "2026-11-01T10:00:00.000Z", ["note"] = "2026-11-05T10:00:00.000Z" };
        var late = new JsonObject { ["name"] = "2026-11-03T10:00:00.000Z", ["note"] = "2026-11-02T10:00:00.000Z" };
        var here = new Dictionary<string, string> { ["name"] = "Anna", ["note"] = "Hund" };
        var there = new Dictionary<string, string> { ["name"] = "Anna Muster", ["note"] = "" };
        var changes = Merge(here, early, there, late);
        Assert.Equal("Anna Muster", changes["name"]);            // theirs is newer
        Assert.False(changes.ContainsKey("note"));                 // ours is newer — an older empty value does not wipe it
        var stamps = JsonNode.Parse((string)changes["stamps"]!)!;
        Assert.Equal("2026-11-03T10:00:00.000Z", (string)stamps["name"]!);
        Assert.Equal("2026-11-05T10:00:00.000Z", (string)stamps["note"]!);

        Assert.Empty(Merge(here, early, here, early));                                         // nothing new, nothing written
        // cards from before the federation carry no stamps: only an empty field is filled, nothing is overwritten
        var legacy = Merge(new Dictionary<string, string> { ["name"] = "Anna", ["phone"] = "" }, null, new Dictionary<string, string> { ["name"] = "Fremd", ["phone"] = "+385 1" }, null);
        Assert.False(legacy.ContainsKey("name"));
        Assert.Equal("+385 1", legacy["phone"]);
    }

    [Fact]
    public async Task Guests_and_stays_travel_between_the_sites_and_the_later_change_wins()
    {
        var (lead, member) = await SetUpAsync();

        // Nika (leading) knows Anna with a note; Sogi (attached) gets a request from her a moment later, with a shorter name, and one from Bob
        await PhoneAsync(lead, "2031-05-02", "2031-05-06", "Anna Muster", "anna@example.org");
        var annaAtLead = await GuestPageAsync(lead, "anna@example.org");
        var id = Regex.Match(await lead.GetStringAsync(R + "guests"), "/admin/p/booking/guests/(\\d+)\">Anna Muster").Groups[1].Value;
        await BookingApp.PostAsync(lead, R + "guests/" + id, new() { ["name"] = "Anna Muster", ["phone"] = "", ["country"] = "Deutschland", ["note"] = "kommt mit Hund" });
        await Task.Delay(20);
        await PhoneAsync(member, "2031-07-10", "2031-07-17", "Anna M.", "anna@example.org");
        await PhoneAsync(member, "2031-08-01", "2031-08-05", "Bob Reisender", "bob@example.org");

        var state = await SyncAsync(member);
        Assert.Contains("Zuletzt abgeglichen", state);
        Assert.DoesNotContain("ging nicht", state);
        Assert.Contains("0 Gäste, 0 Buchungen", state);

        // the card index has Bob now, and Anna's stay at Sogi; her name is the later one (Sogi's), her note stayed
        annaAtLead = await GuestPageAsync(lead, "anna@example.org");
        Assert.Equal("Anna M.", Field(annaAtLead, "name"));
        Assert.Equal("kommt mit Hund", Note(annaAtLead));
        var leadText = Text(annaAtLead);
        Assert.Contains("In anderen Häusern", leadText);
        Assert.Contains("10.07.2031 – 17.07.2031", leadText);
        await GuestPageAsync(lead, "bob@example.org");

        // Sogi took back what Nika knew: the note and the country
        var annaAtMember = await GuestPageAsync(member, "anna@example.org");
        Assert.Equal("kommt mit Hund", Note(annaAtMember));
        Assert.Equal("Deutschland", Field(annaAtMember, "country"));
        Assert.Contains("02.05.2031 – 06.05.2031", Text(annaAtMember));     // her stay at Nika, asked live from the card index

        // Nika corrects the name later — Sogi has it after the next run
        await Task.Delay(20);
        await BookingApp.PostAsync(lead, R + "guests/" + id, new() { ["name"] = "Anna Muster-Berg", ["phone"] = "", ["country"] = "Deutschland", ["note"] = "kommt mit Hund" });
        await SyncAsync(member);
        Assert.Equal("Anna Muster-Berg", Field(await GuestPageAsync(member, "anna@example.org"), "name"));

        // the leading site lists who attached, by the name of the key
        Assert.Contains("Villa Sogi", Text(await lead.GetStringAsync(R + "federation")));
    }

    [Fact]
    public async Task A_leading_site_that_is_down_stops_no_booking_and_the_changes_go_later()
    {
        var (lead, member) = await SetUpAsync();
        await SyncAsync(member);
        Route(() => new Down());
        await PhoneAsync(member, "2031-09-01", "2031-09-04", "Clara Spät", "clara@example.org");      // the booking itself goes through
        var state = await SyncAsync(member);
        Assert.Contains("Letzter Abgleich ging nicht", state);
        Assert.Contains("1 Gast, 1 Buchung", state);
        Assert.DoesNotContain("clara@example.org", await lead.GetStringAsync(R + "guests"));

        ToLead();
        state = await SyncAsync(member);
        Assert.DoesNotContain("ging nicht", state);
        await GuestPageAsync(lead, "clara@example.org");
    }

    [Fact]
    public async Task A_request_on_a_house_is_in_the_central_inbox_at_once()
    {
        var (lead, member) = await SetUpAsync();
        await SyncAsync(member);
        // a guest asks on the HOUSE's website — no run of the job, no "Jetzt abgleichen"
        await sites.Member.AdminAsync();
        var ask = await sites.Member.App.Browser().PostAsync("/buchung/anfrage", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["einheit"] = "apartment-meerblick", ["zeitraum"] = "2031-10-03..2031-10-07", ["erwachsene"] = "2", ["kinder"] = "1",
            ["name"] = "Emil Eilig", ["email"] = "emil@example.org", ["telefon"] = "+49 1", ["nachricht"] = "Mit Kinderbett bitte.", ["consent"] = "1"
        }));
        Assert.True(ask.StatusCode == HttpStatusCode.Redirect, Text(await ask.Content.ReadAsStringAsync()));

        var overview = await lead.GetStringAsync(R + "federation");
        var text = Text(overview);
        Assert.Contains("Offene Anfragen aller Häuser", text);
        Assert.Contains("Emil Eilig", text);
        Assert.Contains("03.10.2031 – 07.10.2031 (4)", text);
        Assert.Matches(@"href=""http://localhost/admin/p/booking/bookings/\d+""", overview);   // opens in the house's own admin
        var all = Text(await lead.GetStringAsync(R + "federation/bookings?tab=alle"));
        Assert.Contains("Emil Eilig", all);

        // the mirror keeps what the inbox needs, never the people of a stay
        using var scope = sites.Lead.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = db.Database.SqlQueryRaw<string>("SELECT message || '|' || adults || '|' || children AS Value FROM p_booking_remote_stays WHERE email = 'emil@example.org'").ToList();
        Assert.Equal("Mit Kinderbett bitte.|2|1", Assert.Single(row));

        // a house that is not the central site redirects its "all houses" page away
        Assert.Equal(HttpStatusCode.Redirect, (await member.GetAsync(R + "federation/bookings")).StatusCode);
    }

    [Fact]
    public async Task The_api_answers_only_a_personal_data_key_and_only_a_leading_site()
    {
        await SetUpAsync();
        string readKey;
        using (var scope = sites.Lead.App.Services.CreateScope())
            readKey = (await scope.ServiceProvider.GetRequiredService<SiteApiKeyService>().CreateAsync("Nur lesen", new[] { "booking:read" }, null)).Key;
        HttpClient C(BookingApp app, string? key)
        {
            var c = app.App.Browser();
            if (key is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return c;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await C(sites.Lead, null).GetAsync("/api/v1/booking/guests?email=anna@example.org")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await C(sites.Lead, readKey).GetAsync("/api/v1/booking/guests?email=anna@example.org")).StatusCode);

        // a remembered check-in may travel, but only with the known columns
        var put = await C(sites.Lead, sites.Key).PutAsync("/api/v1/booking/guests", new StringContent("""
            {"email":"dora@example.org","fields":{"name":"Dora","checkin":"[{\"first\":\"Dora\",\"last\":\"Test\",\"evil\":\"<script>\"}]"},"stamps":{"name":"2026-11-10T08:00:00.000Z","checkin":"2026-11-10T08:00:00.000Z"}}
            """, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var dora = JsonNode.Parse(await put.Content.ReadAsStringAsync())!;
        Assert.Equal("Dora", (string)dora["fields"]!["name"]!);
        Assert.Contains("\"first\":\"Dora\"", (string)dora["fields"]!["checkin"]!);
        Assert.DoesNotContain("evil", (string)dora["fields"]!["checkin"]!);

        // an older stamp does not overwrite
        var old = await C(sites.Lead, sites.Key).PutAsync("/api/v1/booking/guests", new StringContent(
            """{"email":"dora@example.org","fields":{"name":"Alt"},"stamps":{"name":"2020-01-01T00:00:00.000Z"}}""", Encoding.UTF8, "application/json"));
        Assert.Equal("Dora", (string)JsonNode.Parse(await old.Content.ReadAsStringAsync())!["fields"]!["name"]!);

        // a site that does not lead answers no one, whatever the key
        string memberKey;
        using (var scope = sites.Member.App.Services.CreateScope())
            memberKey = (await scope.ServiceProvider.GetRequiredService<SiteApiKeyService>().CreateAsync("Fremd", new[] { "booking:personal" }, null)).Key;
        Assert.Equal(HttpStatusCode.Conflict, (await C(sites.Member, memberKey).GetAsync("/api/v1/booking/guests?email=anna@example.org")).StatusCode);
    }
}
