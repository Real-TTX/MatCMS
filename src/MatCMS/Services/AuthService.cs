using System.Collections.Concurrent;
using System.Security.Claims;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

public class AuthService
{
    /// <summary>Cookie claim: the subject of the MatIdentity person this session was signed in as (only a sign-in through MatIdentity has it).</summary>
    public const string IdentitySubjectClaim = "mat_sub";

    /// <summary>Cookie claim: the <c>sid</c> of the MatIdentity session this cookie belongs to (the back-channel logout names it).</summary>
    public const string IdentitySessionClaim = "mat_sid";

    /// <summary>Cookie claim: the ID token of MatIdentity, handed back to its end-session endpoint at sign-out.</summary>
    public const string IdentityTokenClaim = "mat_id_token";

    /// <summary>Cookie claim: when the cookie was issued (UTC ticks), compared with <see cref="User.IdentitySignedOutAt"/>.</summary>
    public const string IssuedClaim = "mat_issued";

    /// <summary>
    /// The MatIdentity sessions ended by a back-channel logout with a <c>sid</c>, kept as long as a cookie can live (7 days, sliding).
    /// In memory: one site process, and after a restart the subject-wide "signed out at" on the user still holds.
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTime> EndedSessions = new(StringComparer.Ordinal);

    private readonly AppDbContext _db;
    private readonly PasswordHasher<User> _hasher = new();

    public AuthService(AppDbContext db) => _db = db;

    public string HashPassword(string password) => _hasher.HashPassword(new User(), password);

    public bool VerifyPassword(User user, string password)
    {
        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result is PasswordVerificationResult.Success
            or PasswordVerificationResult.SuccessRehashNeeded;
    }

    /// <summary>Validates a login by e-mail (primary) or, as a fallback, the legacy username —
    /// so pre-existing accounts (e.g. the seeded "admin") keep working after the switch to e-mail login.</summary>
    public async Task<User?> ValidateAsync(string identifier, string password)
    {
        var user = await FindByLoginAsync(identifier);
        if (user is null) return null;
        return VerifyPassword(user, password) ? user : null;
    }

    /// <summary>
    /// The account a login name means: by e-mail first, then by user name — both without regard to case. The cloud SSO's callback
    /// uses it too: it used to look for <c>Username == email</c> exactly, so "Max@Example.de" from the cloud and an account
    /// "max@example.de" (or one whose address only lived in <c>Email</c>) became a SECOND admin account instead of the same person.
    /// </summary>
    public async Task<User?> FindByLoginAsync(string? identifier)
    {
        var id = (identifier ?? "").Trim().ToLower();
        if (id.Length == 0) return null;
        return await _db.Users.OrderBy(u => u.Id).FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == id)
               ?? await _db.Users.OrderBy(u => u.Id).FirstOrDefaultAsync(u => u.Username.ToLower() == id);
    }

    /// <param name="amr">Optional "authentication method" marker for the session. The SSO callback
    /// passes "sso" so the "2FA required" gate can tell a federated login — whose second factor the
    /// cloud already enforced — from a local password login that still owes a local TOTP.</param>
    /// <param name="identity">Set for a sign-in through MatIdentity (or an account that was just linked with it): the cookie keeps the
    /// subject, the session id and the ID token, so the back-channel logout can end it and the sign-out ends the session at
    /// MatIdentity too.</param>
    public async Task SignInAsync(HttpContext http, User user, bool persistent, string? amr = null, string? via = null, IdentitySession? identity = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new(IssuedClaim, DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
            claims.Add(new Claim("DisplayName", user.DisplayName));
        if (!string.IsNullOrWhiteSpace(amr))
            claims.Add(new Claim("amr", amr));
        // "cloud" = signed in through the cloud's switcher; the admin bar is then always shown.
        if (!string.IsNullOrWhiteSpace(via))
            claims.Add(new Claim("via", via));
        if (identity is not null)
        {
            claims.Add(new Claim(IdentitySubjectClaim, identity.Subject));
            if (!string.IsNullOrEmpty(identity.SessionId)) claims.Add(new Claim(IdentitySessionClaim, identity.SessionId));
            if (!string.IsNullOrEmpty(identity.IdToken)) claims.Add(new Claim(IdentityTokenClaim, identity.IdToken));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        var props = new AuthenticationProperties { IsPersistent = persistent };

        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            props);
    }

    public Task SignOutAsync(HttpContext http) =>
        http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    /// <summary>The MatIdentity part of a session: who, which session at MatIdentity, and its ID token for the sign-out.</summary>
    public sealed record IdentitySession(string Subject, string? SessionId, string? IdToken);

    /// <summary>True when the signed-in session came through MatIdentity (its second step is MatIdentity's, not this site's).</summary>
    public static bool IsIdentitySession(ClaimsPrincipal? principal) => principal?.FindFirst(IdentitySubjectClaim) is not null;

    /// <summary>The ID token of MatIdentity of the signed-in session, or null.</summary>
    public static string? IdentityTokenOf(ClaimsPrincipal? principal) => principal?.FindFirst(IdentityTokenClaim)?.Value;

    /// <summary>
    /// The back-channel logout of MatIdentity: a <paramref name="sessionId"/> ends the cookies of that MatIdentity session; a
    /// <paramref name="subject"/> alone ends every session of the person (a cookie issued before now is rejected). Returns how many
    /// accounts were signed out by subject (0 for an unknown one: no error).
    /// </summary>
    public async Task<int> EndIdentitySessionsAsync(string? subject, string? sessionId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        foreach (var old in EndedSessions.Where(e => now - e.Value > TimeSpan.FromDays(8)).Select(e => e.Key).ToList())
            EndedSessions.TryRemove(old, out _);

        if (!string.IsNullOrEmpty(sessionId))
        {
            EndedSessions[sessionId] = now;
            return 0;
        }

        if (string.IsNullOrEmpty(subject)) return 0;
        var users = await _db.Users.Where(u => u.ExternalSubject == subject).ToListAsync(ct);
        foreach (var u in users) u.IdentitySignedOutAt = now;
        await _db.SaveChangesAsync(ct);
        return users.Count;
    }

    /// <summary>True when MatIdentity ended the session with this <c>sid</c> (a back-channel logout).</summary>
    public static bool IsEndedSession(string? sessionId) => !string.IsNullOrEmpty(sessionId) && EndedSessions.ContainsKey(sessionId);

    /// <summary>
    /// The admin cookie's <c>OnValidatePrincipal</c>: a session that came through MatIdentity is rejected when MatIdentity ended it (its
    /// <c>sid</c>) or signed the person out everywhere after the cookie was issued. A local or cloud-SSO session is not looked at (no
    /// database call per request), so a site without MatIdentity pays nothing for it.
    /// </summary>
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (!IsIdentitySession(principal)) return;

        if (IsEndedSession(principal!.FindFirst(IdentitySessionClaim)?.Value))
        {
            await RejectAsync(context);
            return;
        }

        if (!int.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)) return;
        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var signedOutAt = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.IdentitySignedOutAt).FirstOrDefaultAsync(context.HttpContext.RequestAborted);
        if (signedOutAt is null) return;

        var issued = long.TryParse(principal.FindFirst(IssuedClaim)?.Value, out var ticks) ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
        if (issued <= signedOutAt.Value)
            await RejectAsync(context);
    }

    private static Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
