using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The OPTIONAL platform connections of a site (MatDesign docs/PLATFORM.md): mail through MatSMTP (<c>MATSMTP_*</c>) and the back-office
/// sign-in through MatIdentity (<c>MATIDENTITY_AUTHORITY</c>). Rule no. 1: without the variables the site behaves exactly as before — the
/// first test holds that down.
/// </summary>
public class MatPlatformSiteTests
{
    // ---------------------------------------------------------------- without any variable

    [Fact]
    public async Task Without_the_variables_nothing_changes()
    {
        using var app = new AppFactory();
        var browser = app.Browser();

        // The sign-in is the site's own form, no MatIdentity in sight.
        var login = await browser.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var html = await login.Content.ReadAsStringAsync();
        Assert.Contains("name=\"Password\"", html);
        Assert.DoesNotContain("MatIdentity", html);

        // No OpenID Connect scheme, no back-channel endpoint.
        var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Null(await schemes.GetSchemeAsync(MatIdentitySignIn.Scheme));
        Assert.False(HasEndpoint(app, MatIdentitySignIn.BackchannelLogoutPath));

        // The local login works as it always did.
        await AppFactory.LoginAsync(browser);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin")).StatusCode);

        // Mail: the site's own SMTP, untouched.
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Set(db, SettingKeys.SmtpHost, "smtp.example.de");
        Set(db, SettingKeys.SmtpPort, "465");
        Set(db, SettingKeys.SmtpFromEmail, "info@example.de");
        var mail = scope.ServiceProvider.GetRequiredService<EmailService>();
        Assert.False(mail.UsesMatSmtp);
        var cfg = await mail.GetConfigAsync();
        Assert.Equal(("smtp.example.de", 465, "info@example.de"), (cfg.Host, cfg.Port, cfg.FromEmail));
        Assert.Null(cfg.Mode);
        Assert.Null(cfg.ReplyTo);

        var settings = await browser.GetStringAsync("/admin/settings?tab=smtp");
        Assert.DoesNotContain("MatSMTP", settings);
        Assert.Contains($"name=\"Values[{SettingKeys.SmtpHost}]\"", settings);
    }

    // ---------------------------------------------------------------- MatSMTP

    [Fact]
    public async Task MatSmtp_sends_as_its_one_sender_with_the_sites_name_and_own_address_as_reply_to()
    {
        using var smtp = new FakeSmtp();
        using var h = new Harness();
        Set(h.Db, SettingKeys.SiteName, "Schalksteinnarren");
        Set(h.Db, SettingKeys.SmtpHost, "smtp.ionos.de");          // the site's own — stays stored, not used
        Set(h.Db, SettingKeys.SmtpFromEmail, "info@schalkstein.de");
        var mail = new EmailService(h.Db, h.Services, NullLogger<EmailService>.Instance, MatSmtp(smtp.Port));

        var cfg = await mail.GetConfigAsync();
        Assert.Equal(("127.0.0.1", smtp.Port, "matcms@mat.example"), (cfg.Host, cfg.Port, cfg.FromEmail));
        Assert.Equal("Schalksteinnarren", cfg.FromName);
        Assert.Equal("info@schalkstein.de", cfg.ReplyTo);

        var (ok, error) = await mail.SendAsync(["gast@example.org"], "Hallo", "Text");
        Assert.True(ok, error);
        var sent = smtp.Messages.Single();
        Assert.Contains("From: Schalksteinnarren <matcms@mat.example>", sent.Data);
        Assert.Contains("Reply-To: info@schalkstein.de", sent.Data);
        Assert.Equal("matcms@mat.example", sent.MailFrom);
        Assert.Equal(["gast@example.org"], sent.Recipients);

        // A caller's own Reply-To (a form's visitor) wins over the site's address.
        (ok, error) = await mail.SendAsync(["gast@example.org"], "Kontakt", "Text", replyTo: "besucher@example.org");
        Assert.True(ok, error);
        Assert.Contains("Reply-To: besucher@example.org", smtp.Messages.Last().Data);
        Assert.DoesNotContain("info@schalkstein.de", smtp.Messages.Last().Data);
    }

    [Fact]
    public async Task MatSmtp_shows_the_smtp_tab_as_fixed_and_a_save_keeps_the_sites_own_connection()
    {
        using var app = new AppFactory
        {
            Variables = new() { ["MATSMTP_HOST"] = "matsmtp", ["MATSMTP_PORT"] = "2025", ["MATSMTP_FROM"] = "matcms@mat.example", ["MATSMTP_PASSWORD"] = "geheim-1234" },
        };
        var browser = app.Browser();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Set(db, SettingKeys.SmtpHost, "smtp.own.de");
            Assert.True(scope.ServiceProvider.GetRequiredService<EmailService>().UsesMatSmtp);
        }
        await AppFactory.LoginAsync(browser);

        var page = await browser.GetStringAsync("/admin/settings?tab=smtp");
        Assert.Contains("through MatSMTP", page);
        Assert.Contains("value=\"matsmtp\" disabled", page);
        Assert.DoesNotContain($"name=\"Values[{SettingKeys.SmtpHost}]\"", page);
        Assert.DoesNotContain("geheim-1234", page);

        var save = await AppFactory.PostAsync(browser, "/admin/settings?handler=Smtp", new()
        {
            [$"Values[{SettingKeys.SmtpFromName}]"] = "Mein Verein",
            ["__RequestVerificationToken"] = AppFactory.Token(page) ?? "",
        });
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);

        using var check = app.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<AppDbContext>().SiteSettings.AsNoTracking().ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal("smtp.own.de", after[SettingKeys.SmtpHost]);       // not wiped by the fields that posted nothing
        Assert.Equal("Mein Verein", after[SettingKeys.SmtpFromName]);
    }

    // ---------------------------------------------------------------- MatIdentity

    /// <summary>A MatIdentity nobody can reach (a closed port): the discovery fails, so the site has to fall back to its own form.</summary>
    private static Dictionary<string, string> Identity() => new() { ["MATIDENTITY_AUTHORITY"] = "http://127.0.0.1:9" };

    /// <summary>
    /// A site with MatIdentity configured, plus a test door that plays MatIdentity's callback: <c>/test/identity?sub=…</c> hands a person to
    /// <see cref="MatIdentitySignIn.CompleteSignInAsync"/> — exactly what the OpenID Connect handler does after the code exchange.
    /// </summary>
    private static AppFactory IdentitySite() => new()
    {
        Variables = Identity(),
        ExtraServices = s => s.AddTransient<IStartupFilter, IdentityDoor>(),
    };

    private sealed class IdentityDoor : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Not app.Map: that would set a PathBase, and the pending link's cookie takes its path from it (the real callback has none).
            app.Use(async (http, rest) =>
            {
                if (http.Request.Path != "/test/identity") { await rest(); return; }
                string? Q(string k) => http.Request.Query[k].ToString() is { Length: > 0 } v ? v : null;
                var person = new MatIdentityPerson(Q("sub")!, Q("email"), Q("name"), null, Q("role"), "id-token-" + Q("sub"), new ClaimsPrincipal())
                {
                    SessionId = Q("sid"),
                };
                http.Response.Redirect(await MatIdentitySignIn.CompleteSignInAsync(http, person, "/admin"));
            });
            next(app);
        };
    }

    [Fact]
    public async Task MatIdentity_down_shows_the_local_form_and_local_1_always_works()
    {
        using var app = IdentitySite();
        var browser = app.Browser();
        var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.NotNull(await schemes.GetSchemeAsync(MatIdentitySignIn.Scheme));

        // Straight on to MatIdentity — which is not there: the local form, with a word why.
        var login = await browser.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var html = await login.Content.ReadAsStringAsync();
        Assert.Contains("name=\"Password\"", html);
        Assert.Contains("MatIdentity cannot be reached right now", html);   // the back office speaks English here (no language chosen)

        // ?local=1 is the site's own form, nothing in the way; the local password works.
        var local = await browser.GetStringAsync("/login?local=1");
        Assert.Contains("Sign in with MatIdentity", local);
        await AppFactory.LoginAsync(browser);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin")).StatusCode);

        // The back-channel logout is mapped.
        Assert.True(HasEndpoint(app, MatIdentitySignIn.BackchannelLogoutPath));
    }

    [Fact]
    public async Task A_new_person_becomes_an_admin_linked_by_sub_and_passes_the_2fa_gate()
    {
        using var app = IdentitySite();
        using (var scope = app.Services.CreateScope())
            Set(scope.ServiceProvider.GetRequiredService<AppDbContext>(), SettingKeys.Require2fa, "1");
        var browser = app.Browser();

        var res = await browser.GetAsync("/test/identity?sub=p-1&email=Neu@Example.de&name=Neue%20Person&sid=s-1");
        Assert.Equal("/admin", res.Headers.Location?.OriginalString);
        // MatIdentity has its own second step: no forced local enrolment.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin")).StatusCode);

        using var check = app.Services.CreateScope();
        var user = await check.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u => u.ExternalSubject == "p-1");
        Assert.Equal(("Neu@Example.de", "Admin", "Neue Person"), (user.Email, user.Role, user.DisplayName));

        // The same sub again is the same account, not a second one.
        await app.Browser().GetAsync("/test/identity?sub=p-1&email=other@example.de");
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync(u => u.ExternalSubject == "p-1"));
    }

    [Fact]
    public async Task An_account_with_the_same_address_is_linked_only_after_its_own_password()
    {
        using var app = IdentitySite();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Users.SingleAsync(u => u.Username == "admin")).Email = "chef@example.de";
            await db.SaveChangesAsync();
        }
        var browser = app.Browser();

        // The address matches — that proves nothing: not signed in, sent to the local form to link.
        var res = await browser.GetAsync("/test/identity?sub=p-chef&email=CHEF@example.de&sid=s-chef");
        Assert.Equal("/login?local=1&link=1", res.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/admin")).StatusCode);
        var page = await browser.GetStringAsync("/login?local=1&link=1");
        Assert.Contains("An account with your address exists here", page);

        using (var scope = app.Services.CreateScope())
            Assert.Null((await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u => u.Username == "admin")).ExternalSubject);

        // A WRONG password links nothing.
        var wrong = await AppFactory.PostAsync(browser, "/login?local=1&link=1", new()
        {
            ["Username"] = "admin", ["Password"] = "falsch", ["__RequestVerificationToken"] = AppFactory.Token(page) ?? "",
        });
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);

        // Its own password links it, and the session is a MatIdentity session from now on.
        await AppFactory.LoginAsync(browser);
        using (var scope = app.Services.CreateScope())
            Assert.Equal("p-chef", (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u => u.Username == "admin")).ExternalSubject);

        // ... so MatIdentity can end it: a back-channel logout of its sid signs the browser out here.
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AuthService>().EndIdentitySessionsAsync(null, "s-chef");
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task A_logout_of_the_subject_ends_every_session_and_a_foreign_role_is_refused()
    {
        using var app = IdentitySite();
        var browser = app.Browser();
        await browser.GetAsync("/test/identity?sub=p-2&email=zwei@example.de");
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin")).StatusCode);

        await Task.Delay(20);   // the cookie's "issued" must lie before the sign-out
        using (var scope = app.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AuthService>().EndIdentitySessionsAsync("p-2", null));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/admin")).StatusCode);

        // A role this site does not have: refused, no account made.
        var refused = await app.Browser().GetAsync("/test/identity?sub=p-3&email=drei@example.de&role=editor");
        Assert.Equal("/login?local=1&denied=1", refused.Headers.Location?.OriginalString);
        using var check = app.Services.CreateScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<AppDbContext>().Users.AnyAsync(u => u.ExternalSubject == "p-3"));
    }

    // ---------------------------------------------------------------- the cloud SSO's lookup

    [Fact]
    public async Task The_login_lookup_ignores_case_and_finds_the_address_in_Email()
    {
        using var h = new Harness();
        h.Db.Users.Add(new User { Username = "Max", Email = "Max@Example.de", PasswordHash = "x" });
        h.Db.SaveChanges();
        var auth = new AuthService(h.Db);
        Assert.Equal("Max", (await auth.FindByLoginAsync("max@example.DE"))?.Username);
        Assert.Equal("Max", (await auth.FindByLoginAsync("MAX"))?.Username);
        Assert.Null(await auth.FindByLoginAsync(""));
    }

    // ---------------------------------------------------------------- helpers

    private static bool HasEndpoint(AppFactory app, string path) =>
        app.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().Any(e => "/" + e.RoutePattern.RawText?.TrimStart('/') == path);

    private static MatSmtpSettings MatSmtp(int port) => MatSmtpSettings.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["MATSMTP_HOST"] = "127.0.0.1", ["MATSMTP_PORT"] = port.ToString(), ["MATSMTP_FROM"] = "matcms@mat.example", ["MATSMTP_TLS"] = "none",
    }).Build());

    private static void Set(AppDbContext db, string key, string value)
    {
        var row = db.SiteSettings.FirstOrDefault(s => s.Key == key);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else row.Value = value;
        db.SaveChanges();
    }

    /// <summary>Just enough SMTP for MailKit without TLS or authentication: keeps what was sent.</summary>
    private sealed class FakeSmtp : IDisposable
    {
        public sealed record Message(string MailFrom, List<string> Recipients, string Data);

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public List<Message> Messages { get; } = [];
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeSmtp()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake ESMTP");
            string from = ""; var to = new List<string>();
            while (await reader.ReadLineAsync() is { } line)
            {
                var cmd = line.ToUpperInvariant();
                if (cmd.StartsWith("EHLO") || cmd.StartsWith("HELO")) await writer.WriteLineAsync("250 fake");
                else if (cmd.StartsWith("MAIL FROM:")) { from = Address(line); await writer.WriteLineAsync("250 OK"); }
                else if (cmd.StartsWith("RCPT TO:")) { to.Add(Address(line)); await writer.WriteLineAsync("250 OK"); }
                else if (cmd == "DATA")
                {
                    await writer.WriteLineAsync("354 go");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } d && d != ".") data.AppendLine(d);
                    lock (Messages) Messages.Add(new Message(from, [.. to], data.ToString()));
                    to.Clear();
                    await writer.WriteLineAsync("250 queued");
                }
                else if (cmd == "QUIT") { await writer.WriteLineAsync("221 bye"); return; }
                else await writer.WriteLineAsync("250 OK");
            }
        }

        private static string Address(string line) => line[(line.IndexOf(':') + 1)..].Trim().Trim('<', '>').Split('>')[0];

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
