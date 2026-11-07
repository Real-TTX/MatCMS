using System.Net;
using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The club plugin (plugins/verein) — NATIVE, loaded from its built DLL into the whole application: the member area lets only the club's people in,
/// a document is answered once with the proof stored, uploads are checked, and public events show in the block while internal ones do not.
/// </summary>
public sealed class VereinApp : IDisposable
{
    public AppFactory App { get; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, HttpClient> _clients = new();
    private bool _installed;

    public async Task InstallAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_installed) return;
            using var scope = App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!db.SiteSettings.Any(s => s.Key == SettingKeys.AllowNativePlugins)) db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.AllowNativePlugins, Value = "1" });
            var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
            var src = StoragePaths.PluginSrc(env, "verein", "1.0");
            Directory.CreateDirectory(src);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "verein", "Verein.dll"), Path.Combine(src, "Verein.dll"), overwrite: true);
            var row = new Models.Plugin
            {
                Key = "verein", Name = "Verein", Version = "1.0", Kind = PluginKind.Native, Entry = "Verein.dll", Enabled = true,
                Sha256 = NativePluginHost.HashOf(Path.Combine(src, "Verein.dll")), Api = typeof(IPlugin).Assembly.GetName().Version!.Major
            };
            db.Plugins.Add(row);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
            var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
            Assert.True(registry.IsLoaded("verein"), "verein did not load: " + string.Join(" | ", registry.Errors.Values));
            _installed = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>A browser per person. "admin" is the back office; any other name is a site account (created here) signed in through /mitglied's form.</summary>
    public async Task<HttpClient> AsAsync(string who)
    {
        await InstallAsync();
        await _gate.WaitAsync();
        try
        {
            if (_clients.TryGetValue(who, out var known)) return known;
            var c = App.Browser();
            if (who == "admin") await AppFactory.LoginAsync(c);
            else if (who != "guest")
            {
                var email = who + "@verein.example";
                using (var scope = App.Services.CreateScope())
                {
                    var (status, _) = await scope.ServiceProvider.GetRequiredService<MemberService>().CreateAsync(email, "correct horse", Cap(who) + " Muster");
                    Assert.Equal(MemberService.CreateStatus.Created, status);
                }
                var page = await c.GetStringAsync("/mitglied");
                Assert.Contains("action=\"/anmelden\"", page);
                var res = await c.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = email, ["password"] = "correct horse", ["returnUrl"] = "/mitglied", ["__RequestVerificationToken"] = AppFactory.Token(page)!
                }));
                Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
                Assert.Equal("/mitglied", res.Headers.Location!.OriginalString);
            }
            return _clients[who] = c;
        }
        finally { _gate.Release(); }
    }

    public static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Posts a form of a page the way a browser does (with that page's antiforgery token).</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient c, string pageUrl, Dictionary<string, string> fields, string? postUrl = null)
    {
        var html = await c.GetStringAsync(pageUrl);
        fields["__RequestVerificationToken"] = AppFactory.Token(html) ?? throw new InvalidOperationException("no token on " + pageUrl);
        return await c.PostAsync(postUrl ?? pageUrl, new FormUrlEncodedContent(fields));
    }

    public T Db<T>(Func<AppDbContext, T> f)
    {
        using var scope = App.Services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public void Dispose() { foreach (var c in _clients.Values) c.Dispose(); App.Dispose(); }
}

public class VereinTests : IClassFixture<VereinApp>
{
    private readonly VereinApp _app;
    public VereinTests(VereinApp app) => _app = app;

    private static string Text(string html) => WebUtility.HtmlDecode(html);

    /// <summary>Makes the account a club member through Admin → Verein → Mitglieder → Neu (the address links the existing account).</summary>
    private async Task<long> JoinAsync(string who, params string[] roles)
    {
        await _app.AsAsync(who);
        var admin = await _app.AsAsync("admin");
        var page = await admin.GetStringAsync("/admin/p/verein/members/new");
        var fields = new Dictionary<string, string> { ["first"] = VereinApp.Cap(who), ["last"] = "Muster", ["email"] = who + "@verein.example", ["phone"] = "0711 1" };
        // role checkboxes are role_{index} in the order of the settings
        var names = Regex.Matches(page, "name=\"(role_\\d+)\"").Select(m => m.Groups[1].Value).ToList();
        var labels = Regex.Matches(page, "name=\"role_\\d+\"[^>]*/> ([^<]+)</label>").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        foreach (var r in roles) { var i = labels.IndexOf(r); Assert.True(i >= 0, "role " + r + " not offered: " + string.Join(",", labels)); fields[names[i]] = "true"; }
        var res = await VereinApp.PostFormAsync(admin, "/admin/p/verein/members/new", fields);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Matches("/admin/p/verein/members/\\d+", res.Headers.Location!.OriginalString);
        return long.Parse(res.Headers.Location!.OriginalString.Split('/').Last().Split('?')[0]);
    }

    private async Task<long> NewDocAsync(string title, string kind, string audience = "all")
    {
        var admin = await _app.AsAsync("admin");
        var html = await admin.GetStringAsync("/admin/p/verein/docs/new");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(AppFactory.Token(html)!), "__RequestVerificationToken" }, { new StringContent(title), "title" },
            { new StringContent("Bitte lesen.\n\nZweiter Absatz <script>alert(1)</script>"), "body" }, { new StringContent(kind), "kind" },
            { new StringContent(audience), "audience" }, { new StringContent("2099-12-31"), "due" }
        };
        var res = await admin.PostAsync("/admin/p/verein/docs/new", form);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return long.Parse(Regex.Match(res.Headers.Location!.OriginalString, "docs/(\\d+)").Groups[1].Value);
    }

    [Fact]
    public async Task The_area_is_only_reachable_through_mitglied_and_only_for_the_clubs_people()
    {
        var guest = await _app.AsAsync("guest");
        var login = await guest.GetStringAsync("/mitglied");
        Assert.Contains("action=\"/anmelden\"", login);
        Assert.Contains("value=\"/mitglied\"", login);
        var res = await guest.GetAsync("/mitglied/dokumente");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/mitglied", res.Headers.Location!.OriginalString);

        // a site account the club has no record of
        var stranger = await _app.AsAsync("fremd");
        Assert.Contains("Kein Zugang", Text(await stranger.GetStringAsync("/mitglied")));
        Assert.Contains("Kein Zugang", Text(await stranger.GetStringAsync("/mitglied/dokumente")));

        await JoinAsync("anna", "Mitglied");
        var anna = await _app.AsAsync("anna");
        var home = Text(await anna.GetStringAsync("/mitglied"));
        Assert.Contains("Hallo Anna!", home);
        Assert.Contains("Mitgliederbereich", home);
    }

    [Fact]
    public async Task A_document_to_sign_needs_the_exact_name_is_answered_once_and_the_proof_is_kept()
    {
        await JoinAsync("berta", "Mitglied");
        var berta = await _app.AsAsync("berta");
        var id = await NewDocAsync("Bildfreigabe 2027", "sign");

        var list = Text(await berta.GetStringAsync("/mitglied/dokumente"));
        Assert.Contains("Bildfreigabe 2027", list);
        Assert.Contains("offen", list);
        var page = await berta.GetStringAsync("/mitglied/dokumente/" + id);
        Assert.DoesNotContain("<script>alert(1)</script>", page);

        // no tick → refused; wrong name → refused
        var r1 = await VereinApp.PostFormAsync(berta, "/mitglied/dokumente/" + id, new() { ["action"] = "accept", ["signature"] = "Berta Muster" });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Contains("Haken", Text(await r1.Content.ReadAsStringAsync()));
        var r2 = await VereinApp.PostFormAsync(berta, "/mitglied/dokumente/" + id, new() { ["action"] = "accept", ["agree"] = "1", ["signature"] = "B. Muster" });
        Assert.Contains("vollständigen Namen", Text(await r2.Content.ReadAsStringAsync()));
        var r3 = await VereinApp.PostFormAsync(berta, "/mitglied/dokumente/" + id, new() { ["action"] = "accept", ["agree"] = "1", ["signature"] = "  berta   muster " });
        Assert.Equal(HttpStatusCode.Redirect, r3.StatusCode);

        // a second answer does not replace the first
        await VereinApp.PostFormAsync(berta, "/mitglied/dokumente/" + id, new() { ["action"] = "decline" });
        var admin = await _app.AsAsync("admin");
        var detail = Text(await admin.GetStringAsync("/admin/p/verein/docs/" + id));
        Assert.Contains("unterschrieben", detail);
        Assert.Contains("berta   muster", detail);
        Assert.DoesNotContain("abgelehnt</", detail);
        var csv = await admin.GetStringAsync("/admin/p/verein/docs/" + id + "/csv");
        Assert.Contains("\"Berta Muster\"", csv);
        Assert.Contains("\"ja\"", csv);

        // the document is only for its audience
        var boardDoc = await NewDocAsync("Nur Vorstand", "confirm", "board");
        Assert.Equal(HttpStatusCode.NotFound, (await berta.GetAsync("/mitglied/dokumente/" + boardDoc)).StatusCode);
    }

    [Fact]
    public async Task Uploads_are_checked_and_served_only_to_the_club()
    {
        await JoinAsync("carla", "Mitglied");
        var carla = await _app.AsAsync("carla");
        async Task<HttpResponseMessage> UploadAsync(string name, byte[] bytes, string kind)
        {
            var html = await carla.GetStringAsync("/mitglied/dateien");
            using var form = new MultipartFormDataContent
            {
                { new StringContent(AppFactory.Token(html)!), "__RequestVerificationToken" }, { new StringContent("upload"), "action" }, { new StringContent(kind), "kind" },
                { new StringContent("Umzug"), "title" }, { new ByteArrayContent(bytes), "file", name }
            };
            return await carla.PostAsync("/mitglied/dateien", form);
        }
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        Assert.Equal(HttpStatusCode.Redirect, (await UploadAsync("bild.png", png, "photo")).StatusCode);
        Assert.Contains("geht leider nicht", Text(await (await UploadAsync("seite.html", "<h1>x</h1>"u8.ToArray(), "material")).Content.ReadAsStringAsync()));
        Assert.Contains("keine echte", Text(await (await UploadAsync("fake.png", "<script>"u8.ToArray(), "photo")).Content.ReadAsStringAsync()));

        var gallery = await carla.GetStringAsync("/mitglied/dateien");
        var fileId = Regex.Match(gallery, "/mitglied/dateien/(\\d+)").Groups[1].Value;
        var served = await carla.GetAsync("/mitglied/dateien/" + fileId);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").First());
        var guest = await _app.AsAsync("guest");
        Assert.Equal(HttpStatusCode.Redirect, (await guest.GetAsync("/mitglied/dateien/" + fileId)).StatusCode);
    }

    [Fact]
    public async Task Public_events_show_in_the_block_internal_ones_only_in_the_member_area()
    {
        var admin = await _app.AsAsync("admin");
        async Task EventAsync(string title, string day, bool isPublic)
        {
            var f = new Dictionary<string, string> { ["title"] = title, ["kind"] = "Umzug", ["starts"] = day + "T14:00", ["place"] = "Besigheim" };
            if (isPublic) f["public"] = "true";
            Assert.Equal(HttpStatusCode.Redirect, (await VereinApp.PostFormAsync(admin, "/admin/p/verein/events/new", f)).StatusCode);
        }
        var next = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd");
        await EventAsync("Umzug Remseck", next, true);
        await EventAsync("Häs-Flicken intern", next, false);
        await EventAsync("Alter Umzug", "2020-02-01", true);

        _app.Db(db =>
        {
            var page = new Page { Title = "Termine", Slug = "termine-test", Locale = "de", IsPublished = true, TranslationGroup = Guid.NewGuid().ToString("N") };
            page.Blocks.Add(new ContentBlock { BlockType = "verein_events", DataJson = "{\"heading\":\"Nächste Umzüge\",\"kind\":\"Umzug\"}", SortOrder = 0 });
            db.Pages.Add(page);
            return db.SaveChanges();
        });
        var html = Text(await (await _app.AsAsync("guest")).GetStringAsync("/termine-test"));
        Assert.Contains("Nächste Umzüge", html);
        Assert.Contains("Umzug Remseck", html);
        Assert.DoesNotContain("Häs-Flicken intern", html);
        Assert.DoesNotContain("Alter Umzug", html);

        await JoinAsync("dora", "Mitglied");
        var area = Text(await (await _app.AsAsync("dora")).GetStringAsync("/mitglied/termine"));
        Assert.Contains("Häs-Flicken intern", area);
    }

    [Fact]
    public async Task Meetings_collect_replies_and_publish_minutes_the_board_sees_who_comes()
    {
        await JoinAsync("emil", "Vorsitzende/r");
        await JoinAsync("fritz", "Mitglied");
        var admin = await _app.AsAsync("admin");
        var res = await VereinApp.PostFormAsync(admin, "/admin/p/verein/meetings/new", new()
        {
            ["title"] = "Jahreshauptversammlung", ["kind"] = "Jahreshauptversammlung", ["starts"] = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd") + "T19:30",
            ["place"] = "Vereinsheim", ["audience"] = "all", ["status"] = "planned", ["agenda"] = "1. Begrüßung"
        });
        var id = Regex.Match(res.Headers.Location!.OriginalString, "meetings/(\\d+)").Groups[1].Value;

        var fritz = await _app.AsAsync("fritz");
        Assert.Equal(HttpStatusCode.Redirect, (await VereinApp.PostFormAsync(fritz, "/mitglied/sitzungen/" + id, new() { ["action"] = "yes" })).StatusCode);
        var asMember = Text(await fritz.GetStringAsync("/mitglied/sitzungen/" + id));
        Assert.DoesNotContain("Rückmeldungen (für den Vorstand)", asMember);
        Assert.Equal(HttpStatusCode.NotFound, (await fritz.GetAsync("/mitglied/vorstand")).StatusCode);

        var emil = await _app.AsAsync("emil");
        var asBoard = Text(await emil.GetStringAsync("/mitglied/sitzungen/" + id));
        Assert.Contains("Rückmeldungen (für den Vorstand)", asBoard);
        Assert.Contains("kommt", asBoard);

        // the minutes: a draft stays in the back office, a published one shows in the member area
        var page = Text(await admin.GetStringAsync("/admin/p/verein/meetings/" + id));
        Assert.Contains("Protokoll: Jahreshauptversammlung", page);
        await VereinApp.PostFormAsync(admin, "/admin/p/verein/meetings/" + id, new() { ["action"] = "minutes", ["minutes"] = "Beschluss: Umzug wird gefahren.", ["minutes_status"] = "draft" });
        Assert.DoesNotContain("Beschluss: Umzug", Text(await fritz.GetStringAsync("/mitglied/sitzungen/" + id)));
        await VereinApp.PostFormAsync(admin, "/admin/p/verein/meetings/" + id, new() { ["action"] = "minutes", ["minutes"] = "Beschluss: Umzug wird gefahren.", ["minutes_status"] = "final" });
        Assert.Contains("Beschluss: Umzug", Text(await fritz.GetStringAsync("/mitglied/sitzungen/" + id)));
    }

    [Fact]
    public async Task Events_become_an_instagram_picture_as_a_list_or_a_poster_and_what_was_typed_stays_text()
    {
        var admin = await _app.AsAsync("admin");
        var day = DateTime.UtcNow.AddDays(12).ToString("yyyy-MM-dd");
        var res = await VereinApp.PostFormAsync(admin, "/admin/p/verein/events/new", new() { ["title"] = "Narrenball <script>x</script>", ["kind"] = "Ball", ["starts"] = day + "T19:00", ["place"] = "Bürgerhalle", ["public"] = "true" });
        var id = Regex.Match(res.Headers.Location!.OriginalString, "events/(\\d+)").Groups[1].Value;

        var list = await admin.GetStringAsync("/admin/p/verein/instagram?format=story");
        Assert.Contains("<svg", list);
        Assert.Contains("viewBox=\"0 0 1080 1920\"", list);
        Assert.Contains("Narrenball", Text(list));
        Assert.DoesNotContain("<script>x</script>", list);
        Assert.Contains("PNG herunterladen", list);

        var poster = await admin.GetStringAsync("/admin/p/verein/instagram?vorlage=plakat&event=" + id);
        Assert.Contains("viewBox=\"0 0 1080 1080\"", poster);
        Assert.Contains("Bürgerhalle", Text(poster));
    }

    [Fact]
    public async Task Templates_are_seeded_and_prefill_a_new_document()
    {
        var admin = await _app.AsAsync("admin");
        var list = Text(await admin.GetStringAsync("/admin/p/verein/templates"));
        Assert.Contains("Einwilligung Fotos und Videos", list);
        var id = Regex.Match(await admin.GetStringAsync("/admin/p/verein/docs/new"), "docs/new\\?vorlage=(\\d+)").Groups[1].Value;
        var prefilled = Text(await admin.GetStringAsync("/admin/p/verein/docs/new?vorlage=" + id));
        Assert.DoesNotContain("{{verein}}", prefilled);
    }
}
