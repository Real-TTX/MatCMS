using System.Collections.Concurrent;
using System.Security.Claims;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

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
    /// The MatIdentity sessions that were ended by a back-channel logout with a <c>sid</c>, kept as long as a cookie can live (7 days, sliding).
    /// In memory: one cloud process, and after a restart the subject-wide "signed out at" on the user still holds.
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTime> EndedSessions = new(StringComparer.Ordinal);

    private readonly AppDbContext _db;
    private readonly PasswordHasher<User> _hasher = new();

    public AuthService(AppDbContext db) => _db = db;

    /// <summary>A cloud user by id, or null.</summary>
    public async Task<User?> FindUserAsync(int id) => await _db.Users.FindAsync(id);

    public string HashPassword(string password) => _hasher.HashPassword(new User(), password);

    public bool VerifyPassword(User user, string password)
    {
        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result is PasswordVerificationResult.Success
            or PasswordVerificationResult.SuccessRehashNeeded;
    }

    /// <summary>Validates a login by e-mail (primary) or, as a fallback, the username — so the
    /// seeded "admin" keeps working before an e-mail has been set.</summary>
    public async Task<User?> ValidateAsync(string identifier, string password)
    {
        var id = (identifier ?? "").Trim().ToLower();
        if (id.Length == 0) return null;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == id)
                   ?? await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == id);
        if (user is null) return null;
        return VerifyPassword(user, password) ? user : null;
    }

    /// <summary>Signs the user in with the cloud's own cookie. <paramref name="identity"/> is set for a sign-in through MatIdentity (or an
    /// account that was just linked with it): the cookie then keeps the subject, the session id and the ID token, so the back-channel
    /// logout can end it and the sign-out ends the session at MatIdentity too.</summary>
    public async Task SignInAsync(HttpContext http, User user, bool persistent, IdentitySession? identity = null)
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

    /// <summary>The MatIdentity part of a session: who, which session at MatIdentity, and its ID token for the sign-out.</summary>
    public sealed record IdentitySession(string Subject, string? SessionId, string? IdToken);

    /// <summary>True when the signed-in session came through MatIdentity (its second step is MatIdentity's, not the cloud's).</summary>
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
    /// The cookie's <c>OnValidatePrincipal</c>: a session that came through MatIdentity is rejected when MatIdentity ended it (its <c>sid</c>)
    /// or signed the person out everywhere after the cookie was issued. A local session is not looked at (no database call per request).
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

    /// <summary>The signed-in user's colour scheme for the back office: "dark", "system" or "light"
    /// (the default, also when nobody is signed in).</summary>
    public async Task<string> ThemeAsync(ClaimsPrincipal principal)
    {
        if (!int.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) || id == 0) return "light";
        var theme = await _db.Users.Where(u => u.Id == id).Select(u => u.Theme).FirstOrDefaultAsync();
        return theme is "dark" or "system" ? theme : "light";
    }

    public Task SignOutAsync(HttpContext http) =>
        http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
}
