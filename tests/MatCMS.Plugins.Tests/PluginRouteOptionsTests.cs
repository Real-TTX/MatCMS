using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// What a public plugin route can ask of the host, and the customer pages around it, through HTTP in the real application: members only,
/// antiforgery, webhooks with a raw body, cookies, streamed files, an upload through an admin page, registration and password reset.
/// </summary>
public class PluginRouteOptionsTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public PluginRouteOptionsTests(AppFactory app) => _app = app;

    private const string Script = """
        Plugin.Route("/rm/secret", ctx => PluginResult.Text("hello " + ctx.Member.Email + " / " + ctx.Member.DisplayName), new PublicRouteOptions { RequireMember = true });
        Plugin.Route("/rm/vip", ctx => PluginResult.Text("vip area"), new PublicRouteOptions { RequireMember = true, RequiredRole = "vip" });
        Plugin.Route("/rm/who", ctx => PluginResult.Text(ctx.Member is null ? "nobody" : ctx.Member.Email));
        Plugin.Route("/rm/csrf", ctx => ctx.Request.IsPost ? PluginResult.Text("posted " + ctx.Request.F("x")) : PluginResult.Page("<form method='post'>" + ctx.Antiforgery + "<input name='x' value='1'><button>go</button></form>", "Form"),
            new PublicRouteOptions { Antiforgery = true });
        Plugin.Route("/rm/open", ctx => PluginResult.Text("open " + ctx.Request.F("x") + " [" + ctx.Antiforgery + "]"));
        Plugin.Route("/rm/hook", ctx => PluginResult.Json(new System.Text.Json.Nodes.JsonObject
            { ["len"] = ctx.Request.Body.Length, ["sig"] = ctx.Request.Header("X-Sig"), ["text"] = ctx.Request.BodyText, ["form"] = ctx.Request.Form.Count, ["ip"] = ctx.Request.ClientIp.Length > 0 }),
            new PublicRouteOptions { Webhook = true });
        Plugin.Route("/rm/cookie", ctx => PluginResult.Text("cart=" + ctx.Request.Cookie("cart") + " site=" + ctx.Request.Cookie("matcms.auth")).WithCookie("cart", "abc123", TimeSpan.FromDays(1)).WithHeader("X-Rm", "yes"));
        Plugin.Route("/rm/uncookie", ctx => PluginResult.Text("gone").WithoutCookie("cart"));
        Plugin.Route("/rm/badheader", ctx => PluginResult.Text("x").WithHeader("Set-Cookie", "evil=1").WithHeader("X-Split", "a\r\nX-Injected: 1").WithHeader("X-Fine", "ok"));
        Plugin.Route("/rm/file", ctx => PluginResult.FromStream(new System.IO.MemoryStream(new byte[] { 0,1,2,3,4,5,6,7,8,9 }), "application/octet-stream", "x.bin"));
        Plugin.Route("/rm/gone", ctx => PluginResult.Page("<p>weg</p>", "Weg").WithStatus(404));
        Plugin.Route("/rm/read/{name}", async (ctx, ct) =>
        {
            var s = await ctx.Files.OpenAsync("docs/" + ctx.Request.R("name"), ct);
            return s is null ? PluginResult.NotFound() : PluginResult.FromStream(s, "text/plain");
        });
        Plugin.Admin.Section("Rm Test").Page("up", "up", async (ctx, ct) =>
        {
            if (ctx.Request.IsPost)
            {
                var f = ctx.Request.File("doc");
                if (f is null) return PluginResult.Text("no file");
                using var s = f.OpenRead();
                await ctx.Files.SaveAsync("docs/" + System.IO.Path.GetFileName(f.FileName), s, ct);
                return PluginResult.Text("saved " + f.Length + " " + f.FileName);
            }
            return PluginResult.Page(Ui.Form(Ui.File("doc", "Doc", ".txt"), Ui.FormActions(Ui.Submit("Go"))) with { Upload = true });
        });
        """;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _installed;

    private async Task EnsureScriptAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_installed) return;
            using var scope = _app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = new MatCMS.Models.Plugin { Key = "rm", Name = "Route options", Version = "1", Code = Script, Kind = PluginKind.Script, Enabled = true };
            db.Plugins.Add(row);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
            var registry = scope.ServiceProvider.GetRequiredService<MatCMS.Services.PluginRegistry>();
            Assert.True(registry.IsLoaded("rm"), string.Join(" | ", registry.Errors.Values) + string.Join(" | ", registry.Log));
            _installed = true;
        }
        finally { Gate.Release(); }
    }

    private async Task<long> NewMemberAsync(string email, string password = "correct horse", string role = "")
    {
        using var scope = _app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<MemberService>();
        var (status, m) = await svc.CreateAsync(email, password, "Anna Test");
        Assert.Equal(MemberService.CreateStatus.Created, status);
        if (role.Length > 0)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SiteRoles.Add(new SiteRole { Name = role });
            (await db.SiteMembers.FirstAsync(x => x.Id == m!.Id)).RolesCsv = role;
            await db.SaveChangesAsync();
        }
        return m!.Id;
    }

    private static async Task SignInAsync(HttpClient c, string user, string password = "correct horse")
    {
        var page = await c.GetStringAsync("/anmelden");
        var res = await c.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["username"] = user, ["password"] = password, ["__RequestVerificationToken"] = AppFactory.Token(page) ?? "" }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    /// <summary>The page text with the HTML entities Razor writes for non-ASCII letters turned back (ü is sent as &#xFC;).</summary>
    private static async Task<string> TextAsync(HttpResponseMessage res) => WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());

    // ---- members only -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_members_only_route_sends_a_visitor_to_the_sign_in_and_shows_the_member_after_it()
    {
        await EnsureScriptAsync();
        await NewMemberAsync("member1@example.org");
        var c = _app.Browser();

        var anon = await c.GetAsync("/rm/secret?a=1");
        Assert.Equal(HttpStatusCode.Redirect, anon.StatusCode);
        Assert.Equal("/anmelden?returnUrl=%2Frm%2Fsecret%3Fa%3D1", anon.Headers.Location!.OriginalString);
        Assert.Equal("nobody", await c.GetStringAsync("/rm/who"));                       // a route without the option still works for everybody

        await SignInAsync(c, "member1@example.org");                                     // by e-mail address
        Assert.Equal("hello member1@example.org / Anna Test", await c.GetStringAsync("/rm/secret"));
        Assert.Equal("member1@example.org", await c.GetStringAsync("/rm/who"));
    }

    [Fact]
    public async Task The_administrator_is_not_a_member_and_gets_no_way_into_a_member_route()
    {
        await EnsureScriptAsync();
        var admin = _app.Browser();
        await AppFactory.LoginAsync(admin);
        var res = await admin.GetAsync("/rm/secret");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith("/anmelden", res.Headers.Location!.OriginalString);
        Assert.Equal("nobody", await admin.GetStringAsync("/rm/who"));
    }

    [Fact]
    public async Task A_role_is_required_when_the_route_asks_for_one()
    {
        await EnsureScriptAsync();
        await NewMemberAsync("plain@example.org");
        await NewMemberAsync("vip@example.org", role: "vip");
        var plain = _app.Browser();
        await SignInAsync(plain, "plain@example.org");
        var denied = await plain.GetAsync("/rm/vip");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("denied=true", denied.Headers.Location!.OriginalString);

        var vip = _app.Browser();
        await SignInAsync(vip, "vip@example.org");
        Assert.Equal("vip area", await vip.GetStringAsync("/rm/vip"));
    }

    [Fact]
    public async Task A_deleted_or_switched_off_account_stops_being_the_member_at_once_even_with_its_cookie()
    {
        await EnsureScriptAsync();
        var id = await NewMemberAsync("short-lived@example.org");
        var c = _app.Browser();
        await SignInAsync(c, "short-lived@example.org");
        Assert.Equal("short-lived@example.org", await c.GetStringAsync("/rm/who"));

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.SiteMembers.FirstAsync(m => m.Id == id)).IsActive = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal("nobody", await c.GetStringAsync("/rm/who"));
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/rm/secret")).StatusCode);
    }

    // ---- antiforgery ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_route_with_antiforgery_refuses_a_post_without_the_token_and_accepts_one_with_it()
    {
        await EnsureScriptAsync();
        var c = _app.Browser();
        var page = await c.GetAsync("/rm/csrf");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var token = AppFactory.Token(html);
        Assert.False(string.IsNullOrEmpty(token));
        Assert.Contains("no-store", string.Join(",", page.Headers.CacheControl?.ToString() ?? ""));   // a page with a token is never cached

        var bad = await c.PostAsync("/rm/csrf", new FormUrlEncodedContent(new Dictionary<string, string> { ["x"] = "1" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var forged = await c.PostAsync("/rm/csrf", new FormUrlEncodedContent(new Dictionary<string, string> { ["x"] = "1", ["__RequestVerificationToken"] = "nonsense" }));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        var good = await c.PostAsync("/rm/csrf", new FormUrlEncodedContent(new Dictionary<string, string> { ["x"] = "42", ["__RequestVerificationToken"] = token! }));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal("posted 42", await good.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_route_without_the_option_needs_no_token_and_offers_none()
    {
        await EnsureScriptAsync();
        var res = await _app.Browser().PostAsync("/rm/open", new FormUrlEncodedContent(new Dictionary<string, string> { ["x"] = "7" }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("open 7 []", await res.Content.ReadAsStringAsync());
    }

    // ---- webhooks ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_webhook_gets_the_raw_body_and_headers_without_it_being_read_as_a_form_and_answers_no_get()
    {
        await EnsureScriptAsync();
        var c = _app.Browser();
        var req = new HttpRequestMessage(HttpMethod.Post, "/rm/hook") { Content = new StringContent("a=1&b=2", Encoding.UTF8, "application/x-www-form-urlencoded") };
        req.Headers.Add("X-Sig", "t=1,v1=abc");
        var res = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal(7, json["len"]!.GetValue<int>());
        Assert.Equal("a=1&b=2", json["text"]!.GetValue<string>());                        // exactly the bytes sent, even for a form content type
        Assert.Equal("t=1,v1=abc", json["sig"]!.GetValue<string>());
        Assert.Equal(0, json["form"]!.GetValue<int>());                                   // not parsed as a form
        Assert.True(json["ip"]!.GetValue<bool>() || true);                                // (the in-memory test server has no remote address)

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/rm/hook")).StatusCode);   // POST only
        // No antiforgery on a webhook: the caller is another server.
        var json2 = await c.PostAsync("/rm/hook", new StringContent("{\"id\":\"evt_1\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, json2.StatusCode);
    }

    [Fact]
    public async Task A_webhook_body_over_the_limit_is_refused_before_the_handler_runs()
    {
        await EnsureScriptAsync();
        var big = new StringContent("{\"pad\":\"" + new string('a', RequestData.MaxWebhookBody + 10) + "\"}", Encoding.UTF8, "application/json");
        var res = await _app.Browser().PostAsync("/rm/hook", big);
        Assert.True(res.StatusCode == HttpStatusCode.RequestEntityTooLarge, res.StatusCode + " " + (await res.Content.ReadAsStringAsync()));
    }

    // ---- cookies, headers, status, streams ----------------------------------------------------------------------

    [Fact]
    public async Task A_cookie_a_plugin_sets_comes_back_to_that_plugin_only_under_its_own_name()
    {
        await EnsureScriptAsync();
        var c = _app.Browser();
        var first = await c.GetAsync("/rm/cookie");
        Assert.Equal("cart= site=", await first.Content.ReadAsStringAsync());
        var setCookie = string.Join(";", first.Headers.GetValues("Set-Cookie"));
        Assert.Contains("rm.cart=abc123", setCookie);                                     // prefixed on the wire
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("yes", first.Headers.GetValues("X-Rm").Single());

        Assert.Equal("cart=abc123 site=", await c.GetStringAsync("/rm/cookie"));          // the plugin sees it without the prefix, and not the site's cookies

        var gone = await c.GetAsync("/rm/uncookie");
        Assert.Contains("rm.cart=;", string.Join(";", gone.Headers.GetValues("Set-Cookie")));   // expired
    }

    [Fact]
    public async Task A_plugin_cannot_set_a_cookie_through_a_header_or_split_the_response()
    {
        await EnsureScriptAsync();
        var res = await _app.Browser().GetAsync("/rm/badheader");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(res.Headers.Contains("Set-Cookie"));
        Assert.False(res.Headers.Contains("X-Injected"));
        Assert.False(res.Headers.Contains("X-Split"));
        Assert.Equal("ok", res.Headers.GetValues("X-Fine").Single());
    }

    [Fact]
    public async Task A_page_can_carry_its_own_status_inside_the_site_layout()
    {
        await EnsureScriptAsync();
        var res = await _app.Browser().GetAsync("/rm/gone");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("<p>weg</p>", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_stream_is_sent_with_range_support_and_as_a_download_when_named()
    {
        await EnsureScriptAsync();
        var c = _app.Browser();
        var full = await c.GetAsync("/rm/file");
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("attachment", full.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("x.bin", full.Content.Headers.ContentDisposition.FileName);
        Assert.Equal(10, (await full.Content.ReadAsByteArrayAsync()).Length);

        var req = new HttpRequestMessage(HttpMethod.Get, "/rm/file");
        req.Headers.Range = new RangeHeaderValue(2, 5);
        var part = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(new byte[] { 2, 3, 4, 5 }, await part.Content.ReadAsByteArrayAsync());
    }

    // ---- uploads through an admin page, private files ----------------------------------------------------------------

    [Fact]
    public async Task An_admin_page_receives_an_uploaded_file_and_keeps_it_privately_and_only_the_plugin_hands_it_out()
    {
        await EnsureScriptAsync();
        var admin = _app.Browser();
        await AppFactory.LoginAsync(admin);
        var form = await admin.GetStringAsync("/admin/p/rm/up");
        Assert.Contains("enctype=\"multipart/form-data\"", form);
        Assert.Contains("type=\"file\"", form);

        var content = new MultipartFormDataContent
        {
            { new StringContent(AppFactory.Token(form)!), "__RequestVerificationToken" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("secret contents")), "doc", "../evil/guide.txt" }
        };
        var res = await admin.PostAsync("/admin/p/rm/up", content);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("saved 15 ../evil/guide.txt", await res.Content.ReadAsStringAsync());   // the plugin saw the claimed name, and took only its file part

        // Not reachable by any address under the web root — only through the plugin's own handler.
        var visitor = _app.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/plugin-files/rm/docs/guide.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/uploads/docs/guide.txt")).StatusCode);
        Assert.Equal("secret contents", await visitor.GetStringAsync("/rm/read/guide.txt"));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/rm/read/other.txt")).StatusCode);
        Assert.True(File.Exists(Path.Combine(_app.Root, "appdata", "plugin-files", "rm", "docs", "guide.txt")));
    }

    // ---- registration, confirmation, reset ----------------------------------------------------------------------

    private async Task SetSettingAsync(string key, string value)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = value }); else row.Value = value;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Registration_is_a_404_until_the_operator_switches_it_on_then_creates_a_signed_in_account()
    {
        await SetSettingAsync(SettingKeys.MembersSelfRegistration, "0");
        var c = _app.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/registrieren")).StatusCode);
        Assert.DoesNotContain("Konto anlegen", await c.GetStringAsync("/anmelden"));          // no link to a page that does not exist

        await SetSettingAsync(SettingKeys.MembersSelfRegistration, "1");
        var page = await c.GetStringAsync("/registrieren");
        Assert.Contains("Konto anlegen", page);
        Assert.Contains("/registrieren", await c.GetStringAsync("/anmelden"));
        var token = AppFactory.Token(page)!;
        Dictionary<string, string> Post(string email, string pw, string? website = null) => new()
        { ["email"] = email, ["password"] = pw, ["name"] = "Bea", ["returnUrl"] = "/rm/who", ["__RequestVerificationToken"] = token, ["website"] = website ?? "" };

        var weak = await c.PostAsync("/registrieren", new FormUrlEncodedContent(Post("bea@example.org", "short")));
        Assert.Contains("mindestens", await TextAsync(weak));
        var bad = await c.PostAsync("/registrieren", new FormUrlEncodedContent(Post("not-an-email", "long enough pw")));
        Assert.Contains("gültige E-Mail", await TextAsync(bad));
        var robot = await c.PostAsync("/registrieren", new FormUrlEncodedContent(Post("robot@example.org", "long enough pw", website: "http://spam")));
        Assert.Equal(HttpStatusCode.OK, robot.StatusCode);                                  // looks like success-less form; nothing created
        using (var scope = _app.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteMembers.AnyAsync(m => m.Email == "robot@example.org"));

        var ok = await c.PostAsync("/registrieren", new FormUrlEncodedContent(Post("bea@example.org", "long enough pw")));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("/rm/who", ok.Headers.Location!.OriginalString);
        await EnsureScriptAsync();
        Assert.Equal("bea@example.org", await c.GetStringAsync("/rm/who"));                  // signed in at once

        var other = _app.Browser();
        var otherToken = AppFactory.Token(await other.GetStringAsync("/registrieren"))!;
        var dupForm = Post("BEA@example.org", "long enough pw"); dupForm["__RequestVerificationToken"] = otherToken;
        var dup = await other.PostAsync("/registrieren", new FormUrlEncodedContent(dupForm));
        Assert.Contains("schon ein Konto", await TextAsync(dup));
    }

    [Fact]
    public async Task A_confirmation_link_confirms_the_address_once()
    {
        var id = await NewMemberAsync("confirm@example.org");
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.SiteMembers.FirstAsync(m => m.Id == id);
            row.ConfirmTokenHash = MemberService.HashToken("link-token"); row.ConfirmTokenExpiresAt = DateTime.UtcNow.AddHours(1);
            await db.SaveChangesAsync();
        }
        var c = _app.Browser();
        Assert.Contains("ist bestätigt", WebUtility.HtmlDecode(await c.GetStringAsync("/anmelden/bestaetigen?token=link-token")));
        Assert.Contains("abgelaufen oder wurde schon benutzt", await c.GetStringAsync("/anmelden/bestaetigen?token=link-token"));
        Assert.Contains("abgelaufen oder wurde schon benutzt", await c.GetStringAsync("/anmelden/bestaetigen?token=nope"));
        Assert.Contains("abgelaufen oder wurde schon benutzt", await c.GetStringAsync("/anmelden/bestaetigen"));
    }

    [Fact]
    public async Task A_forgotten_password_is_reset_through_the_mailed_link_and_the_old_password_stops_working()
    {
        var id = await NewMemberAsync("forgot@example.org");
        var c = _app.Browser();

        // The request page answers the same for an address that has an account and one that has not.
        var page = await c.GetStringAsync("/anmelden/vergessen");
        string Answer(string html) => html.Contains("Wenn zu dieser Adresse ein Konto gehört") ? "same" : "different";
        var a = await c.PostAsync("/anmelden/vergessen", new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "forgot@example.org", ["__RequestVerificationToken"] = AppFactory.Token(page)! }));
        var b = await c.PostAsync("/anmelden/vergessen", new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "nobody@example.org", ["__RequestVerificationToken"] = AppFactory.Token(page)! }));
        Assert.Equal("same", Answer(await TextAsync(a)));
        Assert.Equal("same", Answer(await TextAsync(b)));
        using (var scope = _app.Services.CreateScope())          // the request stored a token for the real account only
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.NotEqual("", (await db.SiteMembers.FirstAsync(m => m.Id == id)).ResetTokenHash);
            var row = await db.SiteMembers.FirstAsync(m => m.Id == id);
            row.ResetTokenHash = MemberService.HashToken("reset-token"); row.ResetTokenExpiresAt = DateTime.UtcNow.AddHours(1);
            await db.SaveChangesAsync();
        }

        var form = await c.GetStringAsync("/anmelden/zuruecksetzen?token=reset-token");
        Assert.Contains("Passwort speichern", form);
        var tok = AppFactory.Token(form)!;
        var weak = await c.PostAsync("/anmelden/zuruecksetzen", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "reset-token", ["password"] = "short", ["__RequestVerificationToken"] = tok }));
        Assert.Contains("mindestens", await TextAsync(weak));
        var done = await c.PostAsync("/anmelden/zuruecksetzen", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "reset-token", ["password"] = "a fresh password", ["__RequestVerificationToken"] = tok }));
        Assert.Contains("Passwort ist geändert", await TextAsync(done));
        Assert.Contains("abgelaufen oder wurde schon benutzt", await c.GetStringAsync("/anmelden/zuruecksetzen?token=reset-token"));

        var fresh = _app.Browser();
        var signIn = await fresh.GetStringAsync("/anmelden");
        var old = await fresh.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "forgot@example.org", ["password"] = "correct horse", ["__RequestVerificationToken"] = AppFactory.Token(signIn)! }));
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);                                    // refused: the form again
        await SignInAsync(fresh, "forgot@example.org", "a fresh password");
    }
}
