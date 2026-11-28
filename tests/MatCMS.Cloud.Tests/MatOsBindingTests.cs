using System.Security.Claims;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Pages;
using MatCMS.Cloud.Services;
using MatCMS.Shared;
using MatPlatform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace MatCMS.Cloud.Tests;

/// <summary>
/// The cloud bound to MatOS (MATSMTP_*, MatIdentity). Two places did not follow the binding:
/// a profile with mail source "global" rolled the cloud's STORED SMTP rows out — a configuration the cloud itself no longer used
/// while MatSMTP sent its mail — and the "2FA erforderlich" gate of /oauth/authorize sent a MatIdentity session to the cloud's
/// enrolment, which the /admin gate does not.
/// </summary>
public sealed class MatOsBindingTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDbContext _db;
    private readonly SecretProtector _protector = new(new EphemeralDataProtectionProvider());

    private static readonly MatSmtpSettings Bound = new() { Host = "matsmtp", Username = "matcmscloud", Password = "geheim", FromAddress = "cloud@example.org" };
    private static readonly MatSmtpSettings Unbound = new();

    public MatOsBindingTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private (Profile profile, Instance inst) SeedGlobalMail()
    {
        // The cloud's stored SMTP rows from before MatSMTP — still in the database, no longer what the cloud sends with.
        _db.CloudSettings.AddRange(
            new CloudSetting { Key = SettingKeys.SmtpHost, Value = "smtp.alt.example" },
            new CloudSetting { Key = SettingKeys.SmtpUser, Value = "alt" },
            new CloudSetting { Key = SettingKeys.SmtpPassword, Value = _protector.Protect("altes-passwort") },
            new CloudSetting { Key = SettingKeys.SmtpFromEmail, Value = "alt@example.org" });
        var profile = new Profile { Name = "Standard", JoinCode = "A", SyncSmtp = true, MailSource = MailSources.Global };
        _db.Profiles.Add(profile);
        _db.SaveChanges();
        var inst = new Instance { Name = "site", PublicId = "p", TokenHash = "t", ProfileId = profile.Id };
        _db.Instances.Add(inst);
        _db.SaveChanges();
        return (profile, inst);
    }

    [Fact]
    public async Task Global_mail_under_MatSMTP_is_the_relay_and_rolls_no_smtp_values_out()
    {
        var (profile, inst) = SeedGlobalMail();

        var config = await new ProfileService(_db, _protector, Bound).BuildConfigAsync(profile);
        Assert.Equal("cloud", config.MailTransport);
        Assert.DoesNotContain(config.Settings ?? new(), kv => ProfileService.IsSmtpKey(kv.Key));

        // …and the relay takes the site's mail, which the cloud then sends through MatSMTP.
        var r = await new MailSpool(_db, Bound).EnqueueAsync(inst, new MailRequest { To = ["a@example.org"], Subject = "Formular", Body = "x" });
        Assert.True(r.Queued, r.Error);
    }

    [Fact]
    public async Task Global_mail_without_MatSMTP_rolls_the_stored_values_out_as_before()
    {
        var (profile, inst) = SeedGlobalMail();

        var config = await new ProfileService(_db, _protector, Unbound).BuildConfigAsync(profile);
        Assert.Equal("smtp", config.MailTransport);
        Assert.Equal("smtp.alt.example", config.Settings![SettingKeys.SmtpHost]);
        Assert.Equal("altes-passwort", config.Settings[SettingKeys.SmtpPassword]);

        // Standalone: no binding, no relay for a "global" profile — the sites send for themselves.
        var r = await new MailSpool(_db, Unbound).EnqueueAsync(inst, new MailRequest { To = ["a@example.org"], Subject = "x", Body = "y" });
        Assert.False(r.Queued);
    }

    [Fact]
    public async Task Own_smtp_below_a_global_parent_under_MatSMTP_rolls_out_only_its_own_values()
    {
        var (parent, _) = SeedGlobalMail();
        var child = new Profile { Name = "Kind", JoinCode = "B", ParentId = parent.Id, SyncSmtp = true, MailSource = MailSources.Own };
        _db.Profiles.Add(child);
        _db.SaveChanges();
        _db.ProfileSettings.AddRange(
            new ProfileSetting { ProfileId = child.Id, Key = SettingKeys.SmtpHost, Value = "smtp.kind.example" },
            new ProfileSetting { ProfileId = child.Id, Key = SettingKeys.SmtpFromEmail, Value = "kind@example.org" });
        _db.SaveChanges();

        var config = await new ProfileService(_db, _protector, Bound).BuildConfigAsync(child);
        Assert.Equal("smtp", config.MailTransport);
        Assert.Equal("smtp.kind.example", config.Settings![SettingKeys.SmtpHost]);
        // Nothing of the cloud's old rows rides along under the child's own block (above it is the relay, not global SMTP).
        Assert.False(config.Settings.ContainsKey(SettingKeys.SmtpUser));
        Assert.False(config.Settings.ContainsKey(SettingKeys.SmtpPassword));
    }

    private OauthAuthorizeModel Authorize(params Claim[] extra)
    {
        _db.CloudSettings.Add(new CloudSetting { Key = SettingKeys.Require2fa, Value = "1" });
        var user = new User { Username = "matthias", TwoFactorEnabled = false };
        _db.Users.Add(user);
        _db.SaveChanges();

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "Admin"), .. extra], "test")),
        };
        var scope = new OperatorScope(new HttpContextAccessor { HttpContext = http }, _db);
        var codes = new OAuthCodes(new MemoryCache(new MemoryCacheOptions()));
        return new OauthAuthorizeModel(_db, scope, codes, new CloudContext(_db)) { PageContext = new PageContext { HttpContext = http } };
    }

    [Fact]
    public async Task The_authorize_gate_sends_a_local_account_without_2fa_to_enrolment()
    {
        var result = await Authorize().OnGetAsync();
        Assert.Equal("/admin/account/twofactor?enrol=true", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public async Task The_authorize_gate_lets_a_MatIdentity_session_through_like_the_admin_gate()
    {
        var result = await Authorize(new Claim(AuthService.IdentitySubjectClaim, "sub-123")).OnGetAsync();
        // Past the gate: the (empty) request is then refused as malformed, on the page — not sent to enrolment.
        Assert.IsType<PageResult>(result);
    }
}
