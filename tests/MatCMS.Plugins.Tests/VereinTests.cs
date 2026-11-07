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
    public async Task A_season_becomes_a_narrenfahrplan_split_over_several_pictures_when_it_does_not_fit()
    {
        var admin = await _app.AsAsync("admin");
        for (var i = 0; i < 9; i++)
            await VereinApp.PostFormAsync(admin, "/admin/p/verein/events/new", new() { ["title"] = "Saisontermin " + i, ["kind"] = "Saison", ["starts"] = $"2031-01-{10 + i}T14:00", ["place"] = "Ort " + i, ["public"] = "true" });
        var page = await admin.GetStringAsync("/admin/p/verein/instagram?vorlage=saison&von=2031-01-06&bis=2031-02-26&art=Saison&format=square");
        Assert.Contains("Narrenfahrplan 2031", Text(page));
        var pictures = Regex.Matches(page, "<svg ").Count;
        Assert.True(pictures >= 2, "square should not hold 9 dates: " + pictures);
        Assert.Contains("(1/" + pictures + ")", page);
        Assert.Contains("Saisontermin 8", Text(page));
        Assert.Single(Regex.Matches(await admin.GetStringAsync("/admin/p/verein/instagram?vorlage=saison&von=2031-01-06&bis=2031-02-26&art=Saison&format=story"), "<svg "));
    }

    [Fact]
    public async Task A_draft_stays_in_the_back_office_and_the_plan_shows_only_to_members()
    {
        var admin = await _app.AsAsync("admin");
        var day = DateTime.UtcNow.AddDays(40).ToString("yyyy-MM-dd");
        var fields = new Dictionary<string, string>
        {
            ["title"] = "Narrentreffen Entwurf", ["kind"] = "Umzug", ["starts"] = day + "T13:00", ["public"] = "true", ["status"] = "draft",
            ["plan.count"] = "2", ["plan.0.time"] = "11:00", ["plan.0.what"] = "Zunftmeisterempfang", ["plan.1.time"] = "13:30", ["plan.1.what"] = "Aufstellung <b>Nr. 34</b>",
            ["meeting"] = "Bahnhofstraße", ["parking"] = "Parkhaus am Markt"
        };
        var res = await VereinApp.PostFormAsync(admin, "/admin/p/verein/events/new", fields);
        var id = Regex.Match(res.Headers.Location!.OriginalString, "events/(\\d+)").Groups[1].Value;
        await JoinAsync("gerd", "Mitglied");
        var gerd = await _app.AsAsync("gerd");
        Assert.DoesNotContain("Narrentreffen Entwurf", Text(await gerd.GetStringAsync("/mitglied/termine")));
        Assert.Equal(HttpStatusCode.NotFound, (await gerd.GetAsync("/mitglied/termine/" + id)).StatusCode);
        Assert.DoesNotContain("Narrentreffen Entwurf", Text(await admin.GetStringAsync("/admin/p/verein/instagram")));

        fields["status"] = "published";
        await VereinApp.PostFormAsync(admin, "/admin/p/verein/events/" + id, fields);
        var detail = await gerd.GetStringAsync("/mitglied/termine/" + id);
        Assert.Contains("Zunftmeisterempfang", Text(detail));
        Assert.Contains("Parkhaus am Markt", Text(detail));
        Assert.DoesNotContain("<b>Nr. 34</b>", detail);
        _app.Db(db =>
        {
            var page = new Page { Title = "T2", Slug = "termine-plan", Locale = "de", IsPublished = true, TranslationGroup = Guid.NewGuid().ToString("N") };
            page.Blocks.Add(new ContentBlock { BlockType = "verein_events", DataJson = "{\"count\":\"50\"}", SortOrder = 0 });
            db.Pages.Add(page);
            return db.SaveChanges();
        });
        var site = Text(await (await _app.AsAsync("guest")).GetStringAsync("/termine-plan"));
        Assert.Contains("Narrentreffen Entwurf", site);
        Assert.DoesNotContain("Zunftmeisterempfang", site);
    }

    [Fact]
    public async Task An_invited_member_sets_a_password_once_and_lands_signed_in_in_the_member_area()
    {
        await _app.InstallAsync();
        var admin = await _app.AsAsync("admin");
        var res = await VereinApp.PostFormAsync(admin, "/admin/p/verein/members/new", new()
        {
            ["first"] = "Hanna", ["last"] = "Neu", ["email"] = "hanna@verein.example", ["role_6"] = "true", ["sendInvite"] = "true"
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        // an invitation, not a registration: no password the person knows, no confirmation mail of the site, a pending welcome token
        var row = _app.Db(db => db.SiteMembers.First(m => m.Email == "hanna@verein.example"));
        Assert.Null(row.EmailConfirmedAt);
        Assert.True(string.IsNullOrEmpty(row.ConfirmTokenHash));
        Assert.False(string.IsNullOrEmpty(row.ResetTokenHash));

        // the link the plugin mails (made again here, as "Einladung erneut senden" does)
        string token;
        using (var scope = _app.App.Services.CreateScope())
        {
            var (status, _, t) = await scope.ServiceProvider.GetRequiredService<MemberService>().InviteAsync("hanna@verein.example", "Hanna Neu");
            Assert.Equal(MemberService.InviteStatus.Reinvited, status);
            token = t!;
        }
        var c = _app.App.Browser();
        var url = "/anmelden/willkommen?token=" + Uri.EscapeDataString(token) + "&returnUrl=%2Fmitglied";
        var page = await c.GetStringAsync(url);
        Assert.Contains("hanna@verein.example", page);
        var bad = await VereinApp.PostFormAsync(c, url, new() { ["token"] = token, ["returnUrl"] = "/mitglied", ["password"] = "narri-narro-1", ["repeat"] = "narri-narro-2" });
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        var ok = await VereinApp.PostFormAsync(c, url, new() { ["token"] = token, ["returnUrl"] = "/mitglied", ["password"] = "narri-narro-1", ["repeat"] = "narri-narro-1" });
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("/mitglied", ok.Headers.Location!.OriginalString);
        Assert.Contains("Hallo Hanna!", Text(await c.GetStringAsync("/mitglied")));
        Assert.NotNull(_app.Db(db => db.SiteMembers.First(m => m.Email == "hanna@verein.example").EmailConfirmedAt));

        // the link works once; a confirmed account is not invited again
        Assert.Contains("abgelaufen", Text(await _app.App.Browser().GetStringAsync(url)));
        using (var scope = _app.App.Services.CreateScope())
            Assert.Equal(MemberService.InviteStatus.EmailTaken, (await scope.ServiceProvider.GetRequiredService<MemberService>().InviteAsync("hanna@verein.example", "x")).Status);
        // and a return address outside the site is never followed
        using (var scope = _app.App.Services.CreateScope())
        {
            var (_, _, t2) = await scope.ServiceProvider.GetRequiredService<MemberService>().InviteAsync("ida@verein.example", "Ida");
            var c2 = _app.App.Browser();
            var u2 = "/anmelden/willkommen?token=" + Uri.EscapeDataString(t2!) + "&returnUrl=https%3A%2F%2Fevil.example";
            var r2 = await VereinApp.PostFormAsync(c2, u2, new() { ["token"] = t2!, ["returnUrl"] = "//evil.example", ["password"] = "narri-narro-1", ["repeat"] = "narri-narro-1" });
            Assert.Equal("/", r2.Headers.Location!.OriginalString);
        }
    }

    [Fact]
    public async Task A_child_has_no_account_belongs_to_its_parent_and_counts_in_the_list_but_gets_no_documents()
    {
        var parentId = await JoinAsync("karl", "Mitglied");
        var admin = await _app.AsAsync("admin");
        Assert.Equal(HttpStatusCode.Redirect, (await VereinApp.PostFormAsync(admin, "/admin/p/verein/children/new?eltern=" + parentId,
            new() { ["first"] = "Lina", ["last"] = "Muster", ["born"] = "2016-05-04" })).StatusCode);   // no parent ticked → refused
        var res = await VereinApp.PostFormAsync(admin, "/admin/p/verein/children/new?eltern=" + parentId,
            new() { ["first"] = "Lina", ["last"] = "Muster", ["born"] = "2016-05-04", ["g_" + parentId] = "true", ["role_6"] = "true" });
        Assert.Matches("children/\\d+", res.Headers.Location!.OriginalString);
        Assert.Equal(0, _app.Db(db => db.SiteMembers.Count(m => m.DisplayName.Contains("Lina"))));

        var list = Text(await admin.GetStringAsync("/admin/p/verein/members"));
        Assert.Contains("Lina Muster", list);
        Assert.Contains("Karl Muster", list);
        var docId = await NewDocAsync("Fotoeinwilligung Kinder", "confirm");
        Assert.DoesNotContain("Lina Muster", Text(await admin.GetStringAsync("/admin/p/verein/docs/" + docId)));

        var karl = await _app.AsAsync("karl");
        var profile = Text(await karl.GetStringAsync("/mitglied/profil"));
        Assert.Contains("Meine Kinder", profile);
        Assert.Contains("Lina Muster", profile);
    }

    [Fact]
    public async Task The_board_gives_a_starting_password_and_can_set_a_new_one()
    {
        await _app.InstallAsync();
        var admin = await _app.AsAsync("admin");
        var res = await VereinApp.PostFormAsync(admin, "/admin/p/verein/members/new", new()
        {
            ["first"] = "Otto", ["last"] = "Start", ["email"] = "otto@verein.example", ["role_6"] = "true", ["initpw"] = "Fasnet2027!"
        });
        var personUrl = res.Headers.Location!.OriginalString.Split('?')[0];

        async Task<HttpStatusCode> SignInAsync(string password)
        {
            var c = _app.App.Browser();
            var page = await c.GetStringAsync("/mitglied");
            var r = await c.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "otto@verein.example", ["password"] = password, ["returnUrl"] = "/mitglied", ["__RequestVerificationToken"] = AppFactory.Token(page)!
            }));
            if (r.StatusCode == HttpStatusCode.Redirect) Assert.Contains("Hallo Otto!", Text(await c.GetStringAsync("/mitglied")));
            return r.StatusCode;
        }
        Assert.Equal(HttpStatusCode.Redirect, await SignInAsync("Fasnet2027!"));

        Assert.Contains("mindestens 8", Text(await (await admin.GetAsync((await VereinApp.PostFormAsync(admin, personUrl, new() { ["action"] = "setpw", ["newpw"] = "kurz" })).Headers.Location!.OriginalString)).Content.ReadAsStringAsync()));
        await VereinApp.PostFormAsync(admin, personUrl, new() { ["action"] = "setpw", ["newpw"] = "Neues-Passwort-1" });
        Assert.Equal(HttpStatusCode.Redirect, await SignInAsync("Neues-Passwort-1"));
        Assert.Equal(HttpStatusCode.OK, await SignInAsync("Fasnet2027!"));   // the old one no longer works (the sign-in page shows its error)
    }

    /// <summary>A minimal JPEG whose EXIF says it was taken at the given moment ("yyyy:MM:dd HH:mm:ss").</summary>
    private static byte[] JpegTakenAt(string when)
    {
        var tiff = new List<byte> { 0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0 };           // "II", 42, IFD0 at 8
        void U16(int v) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); }
        void U32(int v) { U16(v & 0xFFFF); U16(v >> 16); }
        U16(1); U16(0x8769); U16(4); U32(1); U32(26); U32(0);                        // IFD0: one entry, ExifIFD at 26
        U16(1); U16(0x9003); U16(2); U32(20); U32(44); U32(0);                       // ExifIFD: DateTimeOriginal, 20 bytes at 44
        tiff.AddRange(System.Text.Encoding.ASCII.GetBytes(when)); tiff.Add(0);
        var app1 = new List<byte> { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
        app1.AddRange(tiff);
        var len = app1.Count + 2;
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1, (byte)(len >> 8), (byte)len };
        b.AddRange(app1);
        b.AddRange(new byte[] { 0xFF, 0xD9 });
        return b.ToArray();
    }

    [Fact]
    public async Task Photos_wait_for_release_find_their_event_by_the_date_taken_and_dates_come_as_calendars()
    {
        var admin = await _app.AsAsync("admin");
        await VereinApp.PostFormAsync(admin, "/admin/p/verein/events/new", new() { ["title"] = "Umzug Kalenderstadt", ["kind"] = "Umzug", ["starts"] = "2031-02-08T13:00", ["place"] = "Kalenderstadt", ["public"] = "true" });
        await VereinApp.PostFormAsync(admin, "/admin/p/verein/events/new", new() { ["title"] = "Interner Hockete", ["kind"] = "Treffen", ["starts"] = "2031-02-10T19:00" });
        await JoinAsync("paula", "Mitglied");
        await JoinAsync("quirin", "Mitglied");
        var paula = await _app.AsAsync("paula");

        var html = await paula.GetStringAsync("/mitglied/dateien");
        using (var form = new MultipartFormDataContent
        {
            { new StringContent(AppFactory.Token(html)!), "__RequestVerificationToken" }, { new StringContent("upload"), "action" }, { new StringContent("photo"), "kind" },
            { new StringContent("auto"), "event" }, { new StringContent("1"), "ajax" }, { new ByteArrayContent(JpegTakenAt("2031:02:08 14:12:00")), "file", "IMG_1.jpg" }
        })
        {
            var r = await paula.PostAsync("/mitglied/dateien", form);
            var json = await r.Content.ReadAsStringAsync();
            Assert.Contains("\"ok\":true", json);
            Assert.Contains("\"pending\":true", json);
        }
        var up = _app.Db(db => db.Database.SqlQueryRaw<string>("select taken || '|' || event_id || '|' || approved as Value from p_verein_uploads order by id desc limit 1").ToList().First());
        Assert.StartsWith("2031-02-08T14:12|", up);
        Assert.EndsWith("|0", up);

        var quirin = await _app.AsAsync("quirin");
        Assert.DoesNotContain("IMG_1", await quirin.GetStringAsync("/mitglied/dateien"));
        var res = await VereinApp.PostFormAsync(admin, "/admin/p/verein/uploads", new() { ["action"] = "approveall" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("IMG_1", await quirin.GetStringAsync("/mitglied/dateien"));

        // calendars: the public one only the public event, a member's own one the internal one too
        var pub = await (await _app.AsAsync("guest")).GetStringAsync("/verein/termine.ics");
        Assert.Contains("BEGIN:VCALENDAR", pub);
        Assert.Contains("SUMMARY:Umzug Kalenderstadt", pub);
        Assert.DoesNotContain("Interner Hockete", pub);
        await VereinApp.PostFormAsync(paula, "/mitglied/profil", new() { ["action"] = "calendar" });
        var link = Regex.Match(await paula.GetStringAsync("/mitglied/profil"), "/verein/kalender/[0-9a-f]{40}\\.ics").Value;
        Assert.NotEmpty(link);
        var own = await (await _app.AsAsync("guest")).GetStringAsync(link);
        Assert.Contains("SUMMARY:Interner Hockete", own);
        Assert.Equal(HttpStatusCode.NotFound, (await (await _app.AsAsync("guest")).GetAsync("/verein/kalender/" + new string('0', 40) + ".ics")).StatusCode);

        // the site's menu gets "Mitglieder" for a member, not for a guest
        Assert.Contains("/mitglied/dokumente", await paula.GetStringAsync("/"));
        Assert.DoesNotContain("/mitglied/dokumente", await (await _app.AsAsync("guest")).GetStringAsync("/"));
    }

    [Fact]
    public async Task Own_fields_show_by_their_visibility()
    {
        await _app.InstallAsync();
        _app.Db(db =>
        {
            var row = db.Plugins.First(p => p.Key == "verein");
            row.ConfigJson = "{\"customFields\":\"Hexe-Nr. | alle\\nNarr-Nr. | vorstand\"}";
            return db.SaveChanges();
        });
        using (var scope = _app.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(_app.Db(db => db.Plugins.First(p => p.Key == "verein").Id));
        await _app.AsAsync("rosa");
        var admin = await _app.AsAsync("admin");
        var page = await admin.GetStringAsync("/admin/p/verein/members/new");
        Assert.Contains("Hexe-Nr.", Text(page));
        await VereinApp.PostFormAsync(admin, "/admin/p/verein/members/new", new()
        {
            ["first"] = "Rosa", ["last"] = "Muster", ["email"] = "rosa@verein.example", ["role_6"] = "true", ["x_0"] = "H-17", ["x_1"] = "N-99", ["born"] = "1990-03-04", ["street"] = "Weinsteige 1"
        });
        var cfg = _app.Db(db => db.Plugins.First(p => p.Key == "verein").ConfigJson);
        Assert.Contains("Hexe-Nr.", cfg);
        // a plain member sees the field for everybody, not the board's one, nor address and birthday
        await JoinAsync("sepp", "Mitglied");
        var dir = Text(await (await _app.AsAsync("sepp")).GetStringAsync("/mitglied/mitglieder"));
        // the list of all members is only for the board by default
        Assert.DoesNotContain("N-99", dir);
        var csv = await admin.GetStringAsync("/admin/p/verein/members/csv");
        Assert.Contains("\"Hexe-Nr.\"", csv);
        Assert.Contains("\"H-17\"", csv);
        Assert.Contains("\"N-99\"", csv);
        Assert.Contains("\"Weinsteige 1\"", csv);
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
    [Fact]
    public async Task Documents_and_meetings_print_as_pdf_on_the_letterhead_and_members_get_only_their_own()
    {
        await JoinAsync("theo", "Mitglied");
        var admin = await _app.AsAsync("admin");
        // merged into what other tests stored (they share the site)
        _app.Db(db =>
        {
            var row = db.Plugins.First(p => p.Key == "verein");
            var cfg = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrEmpty(row.ConfigJson) ? "{}" : row.ConfigJson)!.AsObject();
            cfg["printHead"] = "Narrenzunft Test e.V.\nHauptstraße 1, 74354 Besigheim"; cfg["printFoot"] = "Bank: Testbank · IBAN DE00"; cfg["printColor"] = "#013daf";
            row.ConfigJson = cfg.ToJsonString();
            return db.SaveChanges();
        });
        using (var scope = _app.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(_app.Db(db => db.Plugins.First(p => p.Key == "verein").Id));
        var docId = await NewDocAsync("Druckbare Einwilligung", "sign");
        var pdf = await admin.GetAsync("/admin/p/verein/docs/" + docId + "/pdf?nachweis=1");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("(Narrenzunft Test e.V.)", text);                       // the letterhead from the settings
        Assert.Contains("Bank: Testbank", text);                                  // the footer
        Assert.Contains("Nachweis \\(", text);                                  // "(" is escaped inside a PDF string
        Assert.Contains("Theo Muster", text);
        Assert.Contains("Druckbare-Einwilligung", pdf.Content.Headers.GetValues("Content-Disposition").Single());

        var theo = await _app.AsAsync("theo");
        var mine = await theo.GetAsync("/mitglied/dokumente/" + docId + "/pdf");
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        var own = System.Text.Encoding.Latin1.GetString(await mine.Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain("Nachweis \\(", own);                                  // no proof of the others for a member
        Assert.Contains("(Unterschrift)", own);                                    // a line to sign on paper
        var board = await NewDocAsync("Nur Vorstand", "read", "board");
        Assert.Equal(HttpStatusCode.NotFound, (await theo.GetAsync("/mitglied/dokumente/" + board + "/pdf")).StatusCode);

        var res = await VereinApp.PostFormAsync(admin, "/admin/p/verein/meetings/new", new()
        {
            ["title"] = "Ausschusssitzung", ["kind"] = "Ausschusssitzung", ["starts"] = DateTime.UtcNow.AddDays(5).ToString("yyyy-MM-dd") + "T19:00",
            ["place"] = "Vereinsheim", ["audience"] = "all", ["status"] = "planned", ["agenda"] = "1. Begrüßung\n2. Kassenbericht mit einem sehr langen Text, der umbrechen muss, weil er nicht in eine Zeile passt und weiter geht"
        });
        var mid = Regex.Match(res.Headers.Location!.OriginalString, "meetings/(\\d+)").Groups[1].Value;
        var inv = System.Text.Encoding.Latin1.GetString(await (await theo.GetAsync("/mitglied/sitzungen/" + mid + "/pdf")).Content.ReadAsByteArrayAsync());
        Assert.Contains("(Tagesordnung)", inv);
        Assert.Contains("(Kassenbericht", inv);
        // minutes: a draft prints in the back office (marked), never for a member
        await VereinApp.PostFormAsync(admin, "/admin/p/verein/meetings/" + mid, new() { ["action"] = "minutes", ["minutes"] = "Beschluss gefasst.", ["minutes_status"] = "draft" });
        Assert.Contains("Entwurf", System.Text.Encoding.Latin1.GetString(await admin.GetByteArrayAsync("/admin/p/verein/meetings/" + mid + "/pdf?teil=protokoll")));
        Assert.Equal(HttpStatusCode.NotFound, (await theo.GetAsync("/mitglied/sitzungen/" + mid + "/pdf?teil=protokoll")).StatusCode);
        await VereinApp.PostFormAsync(admin, "/admin/p/verein/meetings/" + mid, new() { ["action"] = "minutes", ["minutes"] = "Beschluss gefasst.", ["minutes_status"] = "final" });
        var minutes = System.Text.Encoding.Latin1.GetString(await theo.GetByteArrayAsync("/mitglied/sitzungen/" + mid + "/pdf?teil=protokoll"));
        Assert.Contains("(Beschluss gefasst.)", minutes);
        Assert.Contains("(Protokollf\\374hrung)", minutes);                       // ü as WinAnsi octal escape
    }

    [Fact]
    public async Task An_event_asks_who_takes_part_parents_answer_for_their_children_and_the_board_sees_the_list()
    {
        var uliId = await JoinAsync("uli", "Mitglied");
        await JoinAsync("vera", "Vorsitzende/r");
        var admin = await _app.AsAsync("admin");
        await VereinApp.PostFormAsync(admin, "/admin/p/verein/children/new?eltern=" + uliId,
            new() { ["first"] = "Willi", ["last"] = "Muster", ["g_" + uliId] = "true", ["role_6"] = "true" });
        var day = DateTime.UtcNow.AddDays(20).ToString("yyyy-MM-dd");
        var res = await VereinApp.PostFormAsync(admin, "/admin/p/verein/events/new", new()
        {
            ["title"] = "Umzug Teilnahmetest", ["kind"] = "Umzug", ["starts"] = day + "T14:00", ["public"] = "true", ["status"] = "published",
            ["ask"] = "true", ["ask_required"] = "true", ["ask_audience"] = "all", ["ask_due"] = day
        });
        var id = Regex.Match(res.Headers.Location!.OriginalString, "events/(\\d+)").Groups[1].Value;

        var uli = await _app.AsAsync("uli");
        Assert.Contains("Rückmeldung erforderlich", Text(await uli.GetStringAsync("/mitglied")));
        var page = await uli.GetStringAsync("/mitglied/termine/" + id);
        var me = Regex.Matches(page, "name=\"a\\.(\\d+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(2, me.Count);                                                  // Uli and the child
        Assert.Contains("Willi Muster", Text(page));
        Assert.Equal(HttpStatusCode.Redirect, (await VereinApp.PostFormAsync(uli, "/mitglied/termine/" + id, new()
        {
            ["action"] = "reply", ["a." + me[0]] = "yes", ["n." + me[0]] = "komme mit <b>Bus</b>", ["a." + me[1]] = "no"
        })).StatusCode);
        Assert.DoesNotContain("Rückmeldung erforderlich", Text(await uli.GetStringAsync("/mitglied")));

        var list = Text(await admin.GetStringAsync("/admin/p/verein/events/" + id));
        Assert.Contains("1 dabei", list);
        Assert.Contains("1 nicht dabei", list);
        Assert.Contains("komme mit <b>Bus</b>", list);                              // text, never markup
        Assert.Contains("(von Uli)", list);                                         // the child's reply came from its parent
        var vera = await _app.AsAsync("vera");
        Assert.Contains("Rückmeldungen (für den Vorstand)", Text(await vera.GetStringAsync("/mitglied/termine/" + id)));
        Assert.DoesNotContain("Rückmeldungen (für den Vorstand)", Text(await uli.GetStringAsync("/mitglied/termine/" + id)));

        // the board enters a reply given by phone; the list prints and exports
        Assert.Equal(HttpStatusCode.Redirect, (await VereinApp.PostFormAsync(admin, "/admin/p/verein/events/" + id, new() { ["action"] = "setreply", ["member"] = _app.Db(db => db.SiteMembers.Single(m => m.Email == "vera@verein.example").Id).ToString(), ["answer"] = "maybe" })).StatusCode);
        Assert.Contains("1 unsicher", Text(await admin.GetStringAsync("/admin/p/verein/events/" + id)));
        Assert.StartsWith("%PDF", System.Text.Encoding.Latin1.GetString(await admin.GetByteArrayAsync("/admin/p/verein/events/" + id + "/pdf")));
        var csv = await admin.GetStringAsync("/admin/p/verein/events/" + id + "/csv");
        Assert.Contains("\"Willi\"", csv);
        Assert.Contains("\"nicht dabei\"", csv);
    }

    [Fact]
    public async Task A_profile_picture_is_checked_and_shown_only_to_who_may_see_the_person()
    {
        var xaverId = await JoinAsync("xaver", "Mitglied");
        await JoinAsync("yvonne", "Mitglied");
        var xaver = await _app.AsAsync("xaver");
        async Task<HttpResponseMessage> UploadAsync(byte[] bytes, string name)
        {
            var html = await xaver.GetStringAsync("/mitglied/profil");
            using var form = new MultipartFormDataContent
            {
                { new StringContent(AppFactory.Token(html)!), "__RequestVerificationToken" }, { new StringContent("photo"), "action" },
                { new ByteArrayContent(bytes), "photo", name }
            };
            return await xaver.PostAsync("/mitglied/profil", form);
        }
        var notAPicture = await UploadAsync(System.Text.Encoding.UTF8.GetBytes("<svg onload=alert(1)>"), "bild.jpg");
        Assert.Equal(HttpStatusCode.OK, notAPicture.StatusCode);                     // the page again, with the error
        Assert.Contains("JPG, PNG oder WebP", Text(await notAPicture.Content.ReadAsStringAsync()));
        var ok = await UploadAsync(JpegTakenAt("2026:11:01 10:00:00"), "ich.jpg");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var profile = await xaver.GetStringAsync("/mitglied/profil");
        Assert.Contains("/mitglied/foto/" + xaverId, profile);
        Assert.Equal("image/jpeg", (await xaver.GetAsync("/mitglied/foto/" + xaverId)).Content.Headers.ContentType!.MediaType);

        // the list is not open to everybody here: another member does not get the picture, the board (admin page) does
        var yvonne = await _app.AsAsync("yvonne");
        Assert.Equal(HttpStatusCode.NotFound, (await yvonne.GetAsync("/mitglied/foto/" + xaverId)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await (await _app.AsAsync("guest")).GetAsync("/mitglied/foto/" + xaverId)).StatusCode);
        var admin = await _app.AsAsync("admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin/p/verein/members/" + xaverId + "/photo")).StatusCode);
        Assert.Contains("members/" + xaverId + "/photo", await admin.GetStringAsync("/admin/p/verein/members/" + xaverId));
    }
    [Fact]
    public async Task Members_taken_over_from_a_list_get_no_mail_until_access_is_set_up_with_a_real_address()
    {
        var zoraId = await JoinAsync("zora", "Mitglied");
        var admin = await _app.AsAsync("admin");
        // a list taken over: one member without an account (negative placeholder), one with an account whose address was parked as xxx_…
        _app.Db(db =>
        {
            db.Database.ExecuteSqlRaw("INSERT INTO p_verein_people (member_id, first, last, email, roles, active, child, note) VALUES (-424242, 'Liste', 'Ohnezugang', 'xxx_liste@verein.example', 'Mitglied', 0, 0, 'Ausgetreten')");
            db.Database.ExecuteSqlRaw("UPDATE p_verein_people SET email = 'xxx_zora@verein.example' WHERE id = " + zoraId);
            return 0;
        });
        var listId = _app.Db(db => db.Database.SqlQueryRaw<long>("SELECT id AS Value FROM p_verein_people WHERE member_id = -424242").AsEnumerable().Single());

        var docId = await NewDocAsync("Nur an Erreichbare", "confirm");                  // with "notify" ticked by the form's default? sent explicitly below
        var doc = Text(await admin.GetStringAsync("/admin/p/verein/docs/" + docId));
        Assert.DoesNotContain("Liste Ohnezugang", doc);                                   // no account: not in a document's audience
        await VereinApp.PostFormAsync(admin, "/admin/p/verein/docs/" + docId, new() { ["action"] = "remind" });
        var log = _app.Db(db => 0) == 0 ? LogOfVerein() : "";
        Assert.Contains("an Zora Muster nicht gesendet: kein Zugang oder gesperrte Adresse", log);
        Assert.DoesNotContain("xxx_zora@verein.example", log);                             // never handed to the mailer

        // the list opens on the active members; the inactive one is under its own tab, marked with its note
        Assert.DoesNotContain("Liste Ohnezugang", Text(await admin.GetStringAsync("/admin/p/verein/members")));
        var list = Text(await admin.GetStringAsync("/admin/p/verein/members?status=inaktiv"));
        Assert.Contains("Liste Ohnezugang", list);
        Assert.Contains("Ausgetreten", list);                                              // an inactive member is marked with the note's first line
        var page = Text(await admin.GetStringAsync("/admin/p/verein/members/" + listId));
        Assert.Contains("Zugang einrichten", page);
        Assert.Contains("keine E-Mail", page);
        // parked: setting up access is refused
        var refused = await VereinApp.PostFormAsync(admin, "/admin/p/verein/members/" + listId, new() { ["action"] = "access", ["sendInvite"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.True(_app.Db(db => db.Database.SqlQueryRaw<long>("SELECT member_id AS Value FROM p_verein_people WHERE id = " + listId).AsEnumerable().Single()) < 0);
        // the real address entered, then access: an account, no mail asked for
        await VereinApp.PostFormAsync(admin, "/admin/p/verein/members/" + listId, new() { ["first"] = "Liste", ["last"] = "Ohnezugang", ["email"] = "liste@verein.example", ["role_6"] = "true", ["active"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, (await VereinApp.PostFormAsync(admin, "/admin/p/verein/members/" + listId, new() { ["action"] = "access" })).StatusCode);
        var account = _app.Db(db => db.Database.SqlQueryRaw<long>("SELECT member_id AS Value FROM p_verein_people WHERE id = " + listId).AsEnumerable().Single());
        Assert.True(account > 0);
        Assert.Equal(account, _app.Db(db => db.SiteMembers.Single(m => m.Email == "liste@verein.example").Id));
    }

    [Fact]
    public async Task The_member_list_has_status_tabs_and_filters_by_role_and_own_fields()
    {
        var id = await JoinAsync("ottilie", "Kassierer/in");
        var admin = await _app.AsAsync("admin");
        var html = await admin.GetStringAsync("/admin/p/verein/members");
        var page = Text(html);
        foreach (var tab in new[] { "Aktiv (", "Erwachsene (", "Kinder (", "Ohne Zugang (", "Inaktiv (", "Alle (" }) Assert.Contains(tab, page);
        Assert.Contains("Ottilie Muster", page);
        Assert.Contains("Mitglied seit", page);
        Assert.Contains("Rolle: Kassierer/in", page);                                     // the role filter …
        Assert.Matches("<option value=\"\"[^>]*>Alle Rollen", html);                       // … opening on everybody
        Assert.Matches("data-tags=\"[^\"]*\\br\\d+\\b", html);                            // rows carry their role tags for it
        Assert.Contains("aktiv · Zugang", page);
        // switched off: gone from the default view, there under "Inaktiv"
        await VereinApp.PostFormAsync(admin, "/admin/p/verein/members/" + id, new() { ["first"] = "Ottilie", ["last"] = "Muster", ["note"] = "Ausgetreten" });
        Assert.DoesNotContain("Ottilie Muster", Text(await admin.GetStringAsync("/admin/p/verein/members")));
        var inactive = Text(await admin.GetStringAsync("/admin/p/verein/members?status=inaktiv"));
        Assert.Contains("Ottilie Muster", inactive);
        Assert.Contains("Ausgetreten", inactive);
    }

    private string LogOfVerein()
    {
        using var scope = _app.App.Services.CreateScope();
        return string.Join("\n", scope.ServiceProvider.GetRequiredService<PluginRegistry>().LogOf("verein"));
    }
    [Fact]
    public async Task A_required_second_factor_sends_the_member_to_set_it_up_and_the_profile_links_there()
    {
        await JoinAsync("pia", "Mitglied");
        var pia = await _app.AsAsync("pia");
        var profile = Text(await pia.GetStringAsync("/mitglied/profil"));
        Assert.Contains("Zwei-Faktor-Anmeldung", profile);
        Assert.Contains("/anmelden/sicherheit?returnUrl=", await pia.GetStringAsync("/mitglied/profil"));
        await SetConfigAsync("twoFactor", "alle");
        try
        {
            var res = await pia.GetAsync("/mitglied/dokumente");
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            Assert.StartsWith("/anmelden/sicherheit?pflicht=1", res.Headers.Location!.OriginalString);
            Assert.Contains("vorgeschrieben", Text(await pia.GetStringAsync(res.Headers.Location!.OriginalString)));
        }
        finally { await SetConfigAsync("twoFactor", ""); }
        Assert.Equal(HttpStatusCode.OK, (await pia.GetAsync("/mitglied/dokumente")).StatusCode);
    }

    /// <summary>One setting of the plugin, merged into what the other tests stored, then the plugin reloaded.</summary>
    private async Task SetConfigAsync(string key, string value)
    {
        await _app.InstallAsync();
        _app.Db(db =>
        {
            var row = db.Plugins.First(p => p.Key == "verein");
            var cfg = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrEmpty(row.ConfigJson) ? "{}" : row.ConfigJson)!.AsObject();
            cfg[key] = value;
            row.ConfigJson = cfg.ToJsonString();
            return db.SaveChanges();
        });
        using var scope = _app.App.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(_app.Db(db => db.Plugins.First(p => p.Key == "verein").Id));
    }
}
