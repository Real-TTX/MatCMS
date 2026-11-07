using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Services;
using MatCMS.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The second factor of the site's guest-area accounts: set up at /anmelden/sicherheit, asked for at /anmelden/2fa, recovery codes spent once,
/// switched off by the member (with a code) or by an administrator (lost phone) — and never walked past by the invitation link or a plugin.
/// </summary>
public class MemberTwoFactorTests : IDisposable
{
    private readonly AppFactory _app = new();
    public void Dispose() => _app.Dispose();

    /// <summary>The code an authenticator app shows for <paramref name="offset"/> steps from now (RFC 6238, SHA-1, 6 digits, 30 s).</summary>
    private static string Code(string secret, int offset = 0)
    {
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + offset;
        var msg = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(msg, counter);
        var h = HMACSHA1.HashData(TwoFactor.Base32Decode(secret), msg);
        var o = h[^1] & 0x0F;
        var bin = ((h[o] & 0x7F) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
        return (bin % 1_000_000).ToString().PadLeft(6, '0');
    }

    private async Task<int> AccountAsync(string email)
    {
        using var scope = _app.Services.CreateScope();
        var (status, member) = await scope.ServiceProvider.GetRequiredService<MemberService>().CreateAsync(email, "correct horse", "Tessa Test");
        Assert.Equal(MemberService.CreateStatus.Created, status);
        return member!.Id;
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient c, string email, string returnUrl = "/")
    {
        var page = await c.GetStringAsync("/anmelden");
        return await c.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = email, ["password"] = "correct horse", ["returnUrl"] = returnUrl, ["__RequestVerificationToken"] = AppFactory.Token(page)!
        }));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string pageUrl, string postUrl, Dictionary<string, string> fields)
    {
        var html = await c.GetStringAsync(pageUrl);
        fields["__RequestVerificationToken"] = AppFactory.Token(html)!;
        return await c.PostAsync(postUrl, new FormUrlEncodedContent(fields));
    }

    private static string Field(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"" + name + "\" value=\"([^\"]*)\"").Groups[1].Value);

    [Fact]
    public async Task A_member_sets_up_the_second_factor_and_then_needs_a_code_or_a_recovery_code_to_sign_in()
    {
        var id = await AccountAsync("tessa@member.example");
        var c = _app.Browser();
        Assert.Equal("/", (await SignInAsync(c, "tessa@member.example")).Headers.Location!.OriginalString);   // no second factor yet

        var setup = await c.GetStringAsync("/anmelden/sicherheit?returnUrl=/konto");
        var secret = Regex.Match(setup, "class=\"member-2fa-secret\">([A-Z2-7]+)<").Groups[1].Value;
        Assert.True(secret.Length >= 16, "no secret on the page");
        Assert.Contains("otpauth://totp/", WebUtility.HtmlDecode(setup));
        var pending = Field(setup, "pending");
        Assert.DoesNotContain(secret, pending);                                                    // the pending secret travels encrypted

        var wrong = await PostAsync(c, "/anmelden/sicherheit", "/anmelden/sicherheit?handler=Enable", new() { ["pending"] = pending, ["code"] = "000000", ["returnUrl"] = "/konto" });
        Assert.Contains("Der Code stimmt nicht", WebUtility.HtmlDecode(await wrong.Content.ReadAsStringAsync()));
        var ok = await PostAsync(c, "/anmelden/sicherheit", "/anmelden/sicherheit?handler=Enable", new() { ["pending"] = pending, ["code"] = Code(secret), ["returnUrl"] = "/konto" });
        var shown = WebUtility.HtmlDecode(await ok.Content.ReadAsStringAsync());
        Assert.Contains("ist eingeschaltet", shown);
        var recovery = Regex.Matches(Regex.Match(shown, "member-2fa-codes\">(.*?)</div>", RegexOptions.Singleline).Groups[1].Value, "<span>([^<]+)</span>").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(10, recovery.Count);
        using (var scope = _app.Services.CreateScope())
        {
            var row = scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteMembers.Single(m => m.Id == id);
            Assert.True(row.TwoFactorEnabled);
            Assert.DoesNotContain(secret, row.TotpSecret);                                         // encrypted at rest
            Assert.DoesNotContain(recovery[0], row.RecoveryCodes);                                 // only hashes
        }

        // a new browser: the password alone leads to the code step, not into the account
        var d = _app.Browser();
        var first = await SignInAsync(d, "tessa@member.example", "/konto");
        Assert.Equal("/anmelden/2fa", first.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await d.GetAsync("/anmelden/sicherheit")).StatusCode);   // not signed in yet
        var bad = await PostAsync(d, "/anmelden/2fa", "/anmelden/2fa", new() { ["code"] = "123456" });
        Assert.Contains("Der Code stimmt nicht", WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync()));
        var good = await PostAsync(d, "/anmelden/2fa", "/anmelden/2fa", new() { ["code"] = recovery[0] });
        Assert.Equal("/konto", good.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await d.GetAsync("/anmelden/sicherheit")).StatusCode);

        // a recovery code works once
        var e = _app.Browser();
        await SignInAsync(e, "tessa@member.example");
        var again = await PostAsync(e, "/anmelden/2fa", "/anmelden/2fa", new() { ["code"] = recovery[0] });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(e, "/anmelden/2fa", "/anmelden/2fa", new() { ["code"] = Code(secret) })).StatusCode);

        // switching it off needs a current code (the next step's, the current one was spent)
        var off = await PostAsync(d, "/anmelden/sicherheit", "/anmelden/sicherheit?handler=Disable", new() { ["code"] = "000000" });
        Assert.Contains("Der Code stimmt nicht", WebUtility.HtmlDecode(await off.Content.ReadAsStringAsync()));
        off = await PostAsync(d, "/anmelden/sicherheit", "/anmelden/sicherheit?handler=Disable", new() { ["code"] = Code(secret, 1) });
        Assert.Contains("ist ausgeschaltet", WebUtility.HtmlDecode(await off.Content.ReadAsStringAsync()));
        Assert.Equal("/", (await SignInAsync(_app.Browser(), "tessa@member.example")).Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task An_administrator_can_only_switch_it_off_and_the_invitation_link_does_not_walk_past_it()
    {
        var id = await AccountAsync("udo@member.example");
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var m = db.SiteMembers.Single(x => x.Id == id);
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<MemberTwoFactorService>().ConfirmAsync(m, "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP", Code("JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP")));
            // an invitation for this (still unconfirmed) account: setting the password leads to the code step, not in
            var (_, _, token) = await scope.ServiceProvider.GetRequiredService<MemberService>().InviteAsync("udo@member.example", "Udo");
            Assert.NotNull(token);
            var c = _app.Browser();
            var page = await c.GetStringAsync("/anmelden/willkommen?token=" + Uri.EscapeDataString(token!) + "&returnUrl=/mitglied");
            var res = await c.PostAsync("/anmelden/willkommen", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = token!, ["returnUrl"] = "/mitglied", ["password"] = "another horse", ["repeat"] = "another horse", ["__RequestVerificationToken"] = AppFactory.Token(page)!
            }));
            Assert.Equal("/anmelden/2fa", res.Headers.Location!.OriginalString);
        }

        var admin = _app.Browser();
        await AppFactory.LoginAsync(admin);
        var edit = WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/members/edit?id=" + id));
        Assert.Contains("Zwei-Faktor-Anmeldung ausschalten", edit);
        var reset = await PostAsync(admin, "/admin/members/edit?id=" + id, "/admin/members/edit?id=" + id + "&handler=ResetTwoFactor", new());
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        using (var scope = _app.Services.CreateScope())
        {
            var m = scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteMembers.Single(x => x.Id == id);
            Assert.False(m.TwoFactorEnabled);
            Assert.Null(m.TotpSecret);
        }
        Assert.Contains("Einschalten kann sie nur das Mitglied selbst", WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/members/edit?id=" + id)));
    }
}
