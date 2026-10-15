using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// The PUBLIC-site login (the "guest area"), deliberately a world apart from the admin
/// <see cref="AuthService"/>: its own cookie scheme (<see cref="Scheme"/>), its own table
/// (<see cref="SiteMember"/>), its own claims. A visitor can never become an administrator by
/// logging in here, and an admin session never counts as a member session.
/// <para>Passwords use the same <see cref="PasswordHasher{TUser}"/> the admin uses — one hashing
/// story for the whole app.</para>
/// <para>An account may carry an e-mail address (which then also signs in), be confirmed through a mailed link, and have its password
/// reset the same way. Accounts an administrator created without an address keep working exactly as before.</para>
/// </summary>
public class MemberService
{
    /// <summary>The authentication scheme (and cookie) name for site members. NOT the admin scheme.</summary>
    public const string Scheme = "Member";

    /// <summary>The shortest password a visitor may choose for themselves. (An administrator setting one in the back office is not held to it — the
    /// original guest-area accounts have short, agreed words.)</summary>
    public const int MinPasswordLength = 8;

    /// <summary>How long a confirmation or reset link works, in hours.</summary>
    public const int TokenHours = 48;

    private readonly AppDbContext _db;
    private readonly IServiceProvider _services;
    private readonly PasswordHasher<SiteMember> _hasher = new();

    public MemberService(AppDbContext db, IServiceProvider services) { _db = db; _services = services; }

    public string HashPassword(string password) => _hasher.HashPassword(new SiteMember(), password);

    public Task<SiteMember?> FindAsync(string username) =>
        _db.SiteMembers.FirstOrDefaultAsync(m => m.Username == username);

    // ---- e-mail -------------------------------------------------------------------------------------------------

    /// <summary>An address as it is stored and compared: trimmed and lower-cased.</summary>
    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    /// <summary>A plausible address (one @, something on both sides, a dot after it, no blanks). Deliberately not the full RFC: the
    /// only real test of an address is the mail that arrives.</summary>
    public static bool IsValidEmail(string? email)
    {
        var e = NormalizeEmail(email);
        if (e.Length is < 5 or > 254 || e.Contains(' ') || e.Any(char.IsControl)) return false;     // no line break can slip into a mail header
        var at = e.IndexOf('@');
        return at > 0 && at == e.LastIndexOf('@') && e.IndexOf('.', at) > at + 1 && !e.EndsWith('.');
    }

    public Task<SiteMember?> FindByEmailAsync(string email)
    {
        var e = NormalizeEmail(email);
        return e.Length == 0 ? Task.FromResult<SiteMember?>(null) : _db.SiteMembers.FirstOrDefaultAsync(m => m.Email == e);
    }

    /// <summary>Verifies credentials and returns the member, or null. Only active accounts pass. The login name is either the
    /// username or — for an account that has one — the e-mail address.</summary>
    public async Task<SiteMember?> ValidateAsync(string username, string password)
    {
        var name = (username ?? "").Trim();
        if (name.Length == 0 || string.IsNullOrEmpty(password)) return null;

        var email = NormalizeEmail(name);
        var member = await _db.SiteMembers.FirstOrDefaultAsync(m => m.Username == name)
                     ?? await _db.SiteMembers.FirstOrDefaultAsync(m => m.Email != "" && m.Email == email);
        if (member is null || !member.IsActive || string.IsNullOrEmpty(member.PasswordHash)) return null;

        var result = _hasher.VerifyHashedPassword(member, member.PasswordHash, password);
        return result == PasswordVerificationResult.Failed ? null : member;
    }

    /// <summary>Whether visitors may create an account on the CMS's own page (<c>/registrieren</c>). Off unless the operator switched it on.</summary>
    public async Task<bool> SelfRegistrationEnabledAsync()
    {
        var v = await _db.SiteSettings.AsNoTracking().Where(s => s.Key == SettingKeys.MembersSelfRegistration).Select(s => s.Value).FirstOrDefaultAsync();
        return v?.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";
    }

    public enum CreateStatus { Created, EmailTaken, InvalidEmail, WeakPassword }

    /// <summary>
    /// Creates an account for somebody who gave an address and a password: the address is the login name too, the account works at once, and
    /// a confirmation mail goes out (its failure does not undo the account — the address then just stays unconfirmed). Raises
    /// <see cref="MatCMS.Plugin.HostEvents.MemberRegistered"/>.
    /// </summary>
    public async Task<(CreateStatus Status, SiteMember? Member)> CreateAsync(string email, string password, string displayName, CancellationToken ct = default)
    {
        var e = NormalizeEmail(email);
        if (!IsValidEmail(e)) return (CreateStatus.InvalidEmail, null);
        if ((password ?? "").Length < MinPasswordLength) return (CreateStatus.WeakPassword, null);
        if (await _db.SiteMembers.AnyAsync(m => m.Email == e || m.Username == e, ct)) return (CreateStatus.EmailTaken, null);

        var m = new SiteMember
        {
            Username = e, Email = e, DisplayName = (displayName ?? "").Trim(),
            PasswordHash = HashPassword(password!), IsActive = true
        };
        _db.SiteMembers.Add(m);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Two registrations with the same address at the same moment: the unique index decided, the loser is "taken".
            _db.Entry(m).State = EntityState.Detached;
            return (CreateStatus.EmailTaken, null);
        }
        await SendConfirmationAsync(m, ct);
        await RaiseAsync(MatCMS.Plugin.HostEvents.MemberRegistered, m, ct);
        return (CreateStatus.Created, m);
    }

    // ---- tokens: confirmation and reset ---------------------------------------------------------------------------

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    internal static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private string Origin()
    {
        var site = _services.GetService(typeof(SiteContext)) as SiteContext;
        var http = (_services.GetService(typeof(IHttpContextAccessor)) as IHttpContextAccessor)?.HttpContext;
        var configured = site?.Get(SettingKeys.CanonicalUrl).TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configured)) return configured!;
        return http is null || site is null ? "" : site.CanonicalBaseUrl(http.Request).TrimEnd('/');
    }

    private string SiteName => (_services.GetService(typeof(SiteContext)) as SiteContext)?.SiteName ?? "";

    private Task<(bool ok, string? error)> SendAsync(string key, SiteMember m, string link)
    {
        if (_services.GetService(typeof(EmailService)) is not EmailService mail) return Task.FromResult((false, (string?)"Der Mailversand ist nicht verfügbar."));
        return mail.SendTemplateAsync(key, new[] { m.Email }, new Dictionary<string, string>
        {
            ["name"] = string.IsNullOrWhiteSpace(m.DisplayName) ? m.Username : m.DisplayName,
            ["link"] = link, ["hours"] = TokenHours.ToString(CultureInfo.InvariantCulture), ["site_name"] = SiteName
        });
    }

    /// <summary>Issues a fresh confirmation token and mails the link. False when the account has no address or the mail did not go out.</summary>
    public async Task<bool> SendConfirmationAsync(SiteMember m, CancellationToken ct = default)
    {
        if (m.Email.Length == 0 || m.EmailConfirmedAt is not null) return false;
        var token = NewToken();
        m.ConfirmTokenHash = HashToken(token);
        m.ConfirmTokenExpiresAt = DateTime.UtcNow.AddHours(TokenHours);
        await _db.SaveChangesAsync(ct);
        var (ok, _) = await SendAsync(MatCMS.Shared.MailTemplates.MemberConfirm, m, $"{Origin()}/anmelden/bestaetigen?token={token}");
        return ok;
    }

    /// <summary>Confirms the address behind a token from the mail. Returns the account, or null for a token that is unknown or expired.</summary>
    public async Task<SiteMember?> ConfirmAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = HashToken(token.Trim());
        var m = await _db.SiteMembers.FirstOrDefaultAsync(x => x.ConfirmTokenHash == hash, ct);
        if (m is null || m.ConfirmTokenExpiresAt is null || m.ConfirmTokenExpiresAt < DateTime.UtcNow) return null;
        m.EmailConfirmedAt = DateTime.UtcNow;
        m.ConfirmTokenHash = ""; m.ConfirmTokenExpiresAt = null;
        await _db.SaveChangesAsync(ct);
        await RaiseAsync(MatCMS.Plugin.HostEvents.MemberConfirmed, m, ct);
        return m;
    }

    /// <summary>
    /// Mails a password-reset link to the account with this address, if there is one. The caller says the same thing either way — a page that
    /// answers differently tells a stranger which addresses have an account.
    /// </summary>
    public async Task RequestResetAsync(string email, CancellationToken ct = default)
    {
        var m = await FindByEmailAsync(email);
        if (m is null || !m.IsActive) return;
        var token = NewToken();
        m.ResetTokenHash = HashToken(token);
        m.ResetTokenExpiresAt = DateTime.UtcNow.AddHours(TokenHours);
        await _db.SaveChangesAsync(ct);
        await SendAsync(MatCMS.Shared.MailTemplates.MemberReset, m, $"{Origin()}/anmelden/zuruecksetzen?token={token}");
    }

    /// <summary>The account a still-valid reset token belongs to, or null. Does not use the token up.</summary>
    public async Task<SiteMember?> FindByResetTokenAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = HashToken(token.Trim());
        var m = await _db.SiteMembers.FirstOrDefaultAsync(x => x.ResetTokenHash == hash, ct);
        return m is null || m.ResetTokenExpiresAt is null || m.ResetTokenExpiresAt < DateTime.UtcNow ? null : m;
    }

    /// <summary>Sets a new password through a reset token, which is used up. A visitor's own choice must meet <see cref="MinPasswordLength"/>.</summary>
    public async Task<bool> ResetPasswordAsync(string? token, string newPassword, CancellationToken ct = default)
    {
        if ((newPassword ?? "").Length < MinPasswordLength) return false;
        var m = await FindByResetTokenAsync(token, ct);
        if (m is null) return false;
        m.PasswordHash = HashPassword(newPassword);
        m.ResetTokenHash = ""; m.ResetTokenExpiresAt = null;
        // Whoever can read the mailbox has proven the address too.
        m.EmailConfirmedAt ??= DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Changes the password after checking the current one.</summary>
    public async Task<bool> ChangePasswordAsync(long id, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if ((newPassword ?? "").Length < MinPasswordLength) return false;
        var m = await _db.SiteMembers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null || !m.IsActive || string.IsNullOrEmpty(m.PasswordHash)) return false;
        if (_hasher.VerifyHashedPassword(m, m.PasswordHash, currentPassword ?? "") == PasswordVerificationResult.Failed) return false;
        m.PasswordHash = HashPassword(newPassword);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ---- deleting -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Deletes an account. Plugins are told BEFORE the row goes (so they can still read who it was): a shop anonymises its orders, a forum its
    /// posts. A plugin that fails to do so is logged and does not keep the account alive — the person asked to be forgotten.
    /// </summary>
    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var m = await _db.SiteMembers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return false;
        await RaiseAsync(MatCMS.Plugin.HostEvents.MemberDeleted, m, ct);
        await RaiseAsync(MatCMS.Plugin.HostEvents.MemberErase, m, ct);
        _db.SiteMembers.Remove(m);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Everything the plugins hold about an account, for the data-protection export: one entry per plugin that answered.</summary>
    public async Task<JsonArray> ExportAsync(SiteMember m, CancellationToken ct = default)
    {
        var result = new JsonArray();
        if (_services.GetService(typeof(PluginRegistry)) is not PluginRegistry registry || registry.ListenersOf(MatCMS.Plugin.HostEvents.MemberExport).Count == 0) return result;
        var bus = new PluginEventBus(registry, _services, new MatCMS.Plugin.PluginUser(), PluginRequestContext.CurrentLocale());
        foreach (var answer in await bus.CollectAsync(MatCMS.Plugin.HostEvents.MemberExport, Payload(m), ct)) result.Add(answer.DeepClone());
        return result;
    }

    // Parsed from text rather than built from CLR values: a number made from an int refuses GetValue<long>() (and the other way round), so a
    // plugin that reads the id with either would break. A parsed number answers to both.
    private static JsonObject Payload(SiteMember m) =>
        (JsonObject)JsonNode.Parse(new JsonObject { ["id"] = m.Id, ["email"] = m.Email, ["username"] = m.Username }.ToJsonString())!;

    private Task RaiseAsync(string name, SiteMember m, CancellationToken ct) => PluginHostEvents.RaiseAsync(_services, name, Payload(m), ct);

    // ---- signing in ---------------------------------------------------------------------------------------------

    /// <summary>Signs a member in under the member scheme. Roles ride as role claims, so a page's
    /// <see cref="Page.RequiredRole"/> can be checked with the ordinary <c>IsInRole</c>.</summary>
    public async Task SignInAsync(HttpContext http, SiteMember member, bool persistent)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, member.Id.ToString()),
            new(ClaimTypes.Name, member.Username),
            new("DisplayName", string.IsNullOrWhiteSpace(member.DisplayName) ? member.Username : member.DisplayName),
        };
        foreach (var role in member.Roles) claims.Add(new Claim(ClaimTypes.Role, role));

        var identity = new ClaimsIdentity(claims, Scheme);
        await http.SignInAsync(Scheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = persistent });
        // The principal of this very request, too — a handler that signs in and then asks "who am I" must get the answer it just made.
        http.Items.Remove(ItemsKey);
    }

    public Task SignOutAsync(HttpContext http) { http.Items.Remove(ItemsKey); return http.SignOutAsync(Scheme); }

    /// <summary>The member behind the current request, or null. Authenticates the member scheme
    /// explicitly — the framework only auto-populates <c>HttpContext.User</c> for the DEFAULT (admin)
    /// scheme, so a members-only check must ask for this one by name.</summary>
    public static async Task<ClaimsPrincipal?> CurrentAsync(HttpContext http)
    {
        if (http.Items.TryGetValue(ItemsKey, out var cached)) return cached as ClaimsPrincipal;
        var result = await http.AuthenticateAsync(Scheme);
        var principal = result.Succeeded ? result.Principal : null;
        http.Items[ItemsKey] = principal;
        return principal;
    }

    /// <summary>The id of the signed-in member, or null.</summary>
    public static async Task<long?> CurrentIdAsync(HttpContext http)
    {
        var p = await CurrentAsync(http);
        return p?.Identity?.IsAuthenticated == true && long.TryParse(p.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    }

    private const string ItemsKey = "__member_principal";

    /// <summary>True when the current request has a member who satisfies the page's requirement:
    /// logged in, and — if the page names a role — holding it. An administrator always passes, the
    /// same courtesy the renderer already extends to unpublished pages.</summary>
    public static async Task<bool> CanViewAsync(HttpContext http, Page page)
    {
        if (page.Access != PageAccess.Members) return true;
        if (http.User.IsInRole("Admin")) return true;

        var member = await CurrentAsync(http);
        if (member?.Identity?.IsAuthenticated != true) return false;
        return string.IsNullOrWhiteSpace(page.RequiredRole) || member.IsInRole(page.RequiredRole!);
    }
}

/// <summary>
/// A small fixed-window limit on the guest-area forms that create or change something (register, ask for a reset, set a new password): 30 per minute
/// per client address. Only the POSTs are counted — opening a page costs nothing, and the tokens in the mailed links are 256 random bits, so there is
/// nothing to guess there. (The sign-in itself is not limited here; that is a separate matter.)
/// </summary>
public static class MemberAuthLimiter
{
    private const int PerMinute = 30;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Window, int Count)> Hits = new();

    public static bool Allow(HttpContext http)
    {
        var client = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var window = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        if (Hits.Count > 5000)
            foreach (var kv in Hits.Where(kv => kv.Value.Window != window).ToList()) Hits.TryRemove(kv.Key, out _);
        var now = Hits.AddOrUpdate(client, (window, 1), (_, cur) => cur.Window == window ? (window, cur.Count + 1) : (window, 1));
        return now.Count <= PerMinute;
    }
}
