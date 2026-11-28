using System.Globalization;
using System.Security.Cryptography;
using MatCMS.Data;
using MatCMS.Models;
using MatPlatform;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// The site's side of the sign-in through MatIdentity (MatPlatform <see cref="IMatIdentityUsers"/>, on only with
/// <c>MATIDENTITY_AUTHORITY</c>). It is for BACK-OFFICE accounts (<see cref="User"/>) only — the site's members (<see cref="SiteMember"/>)
/// keep their own sign-in at <c>/anmelden</c>. Which account a person is, in this order (<see cref="ResolveAsync"/>):
/// <list type="number">
/// <item>The account whose <see cref="User.ExternalSubject"/> is the person's <c>sub</c>.</item>
/// <item>An unlinked account with the person's e-mail (or user name) is <b>never</b> entered by the address alone — anybody may have typed
///   that address into their MatIdentity profile. The answer is <see cref="MatIdentitySignInResult.LinkRequired"/>: <c>/login?local=1&amp;link=1</c>
///   links it after the account's own password and second factor (<see cref="TakeLinkAsync"/>). Several matches, or a match that already
///   belongs to another person, are refused.</item>
/// <item>Else a new admin account (no usable password: it signs in through MatIdentity; the local login is still there for the others).</item>
/// </list>
/// MatCMS has ONE back-office role, so the role claim only says yes or no: none or <see cref="RoleAdmin"/> lets the person in, any other
/// value (a role this app does not have) refuses. The site's own second factor is not asked after MatIdentity (whose second step it is).
/// When MatIdentity signs the person out (back-channel logout), the cookies here end too.
/// </summary>
public sealed class SiteIdentityUsers(AppDbContext db, AuthService auth, ILogger<SiteIdentityUsers> logger) : IMatIdentityUsers, IMatIdentitySessions
{
    /// <summary>The id of MatCMS in the platform and its default client id at MatIdentity — the same as its catalog id in MatOS.</summary>
    public const string AppId = "matcms";

    /// <summary>The one role of the back office as the platform names it (the claim <c>role</c>).</summary>
    public const string RoleAdmin = "admin";

    public enum Kind { SignIn, Link, Refused }

    public sealed record Outcome(Kind Kind, User? User, string? Error);

    public async Task<MatIdentitySignInResult> SignInAsync(MatIdentityPerson person, HttpContext http, CancellationToken ct)
    {
        var outcome = await ResolveAsync(db, person.Subject, person.Email, person.Name, person.PreferredUserName, person.Role, auth.HashPassword, ct);
        switch (outcome.Kind)
        {
            case Kind.Link:
                return MatIdentitySignInResult.LinkRequired(outcome.User!.Id.ToString(CultureInfo.InvariantCulture));
            case Kind.Refused:
                logger.LogWarning("MatIdentity sign-in of {Subject} refused: {Error}", person.Subject, outcome.Error);
                return MatIdentitySignInResult.Refused(outcome.Error ?? "refused");
        }

        var user = outcome.User!;
        await auth.SignInAsync(http, user, persistent: true, identity: new AuthService.IdentitySession(person.Subject, person.SessionId, person.IdToken));
        logger.LogInformation("Admin {UserId} signed in through MatIdentity", user.Id);
        return MatIdentitySignInResult.Success();
    }

    public async Task EndSessionsAsync(string? subject, string? sessionId, CancellationToken ct)
    {
        var count = await auth.EndIdentitySessionsAsync(subject, sessionId, ct);
        logger.LogInformation("MatIdentity signed out {Subject} (sid {SessionId}); accounts signed out by subject: {Count}", subject, sessionId, count);
    }

    /// <summary>True when the role claim lets the person into the back office: none (the site decides — it has one role) or <see cref="RoleAdmin"/>.</summary>
    public static bool RoleAllowed(string? role) => string.IsNullOrWhiteSpace(role) || string.Equals(role.Trim(), RoleAdmin, StringComparison.OrdinalIgnoreCase);

    public static async Task<Outcome> ResolveAsync(AppDbContext db, string subject, string? email, string? name, string? preferredUserName, string? role,
        Func<string, string> hashPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject)) return new(Kind.Refused, null, "no subject");
        if (!RoleAllowed(role)) return new(Kind.Refused, null, "no role of this site");

        var known = await db.Users.FirstOrDefaultAsync(u => u.ExternalSubject == subject, ct);
        if (known is not null) return new(Kind.SignIn, known, null);

        var mail = email?.Trim().ToLowerInvariant();
        var login = preferredUserName?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mail)) mail = null;
        if (string.IsNullOrEmpty(login)) login = null;
        var matches = await db.Users
            .Where(u => (mail != null && ((u.Email != null && u.Email.ToLower() == mail) || u.Username.ToLower() == mail))
                        || (login != null && u.Username.ToLower() == login))
            .ToListAsync(ct);
        if (matches.Count > 1) return new(Kind.Refused, null, "several accounts match");
        if (matches.Count == 1)
            return matches[0].ExternalSubject is null
                ? new(Kind.Link, matches[0], null)
                : new(Kind.Refused, null, "the account belongs to another person");

        var user = new User
        {
            // The site's logins are e-mail addresses (the setup wizard makes the admin's address its user name).
            Username = await FreeUsernameAsync(db, email ?? preferredUserName ?? subject, ct),
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Role = "Admin",
            ExternalSubject = subject,
            // No usable local password: the account signs in through MatIdentity (it can set one under its account later).
            PasswordHash = hashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return new(Kind.SignIn, user, null);
    }

    /// <summary>
    /// After the account's own password (and second factor): a pending link of MatIdentity is taken only for exactly this account (and
    /// deleted either way), the account gets the person's subject, and the session keeps MatIdentity's session. Null when there is
    /// nothing to link, or the person meanwhile belongs to another account.
    /// </summary>
    public static async Task<AuthService.IdentitySession?> TakeLinkAsync(HttpContext http, AppDbContext db, User user, ILogger log)
    {
        if (MatIdentityPendingLink.TakeFor(http, user.Id.ToString(CultureInfo.InvariantCulture)) is not { } pending) return null;
        if (!RoleAllowed(pending.Role) || await db.Users.AnyAsync(u => u.ExternalSubject == pending.Subject && u.Id != user.Id, http.RequestAborted))
        {
            log.LogWarning("Admin {UserId} signed in, but was not linked with MatIdentity: the person belongs to another account or has no role here", user.Id);
            return null;
        }

        user.ExternalSubject = pending.Subject;
        await db.SaveChangesAsync(http.RequestAborted);
        log.LogInformation("Admin {UserId} linked with MatIdentity", user.Id);
        return new AuthService.IdentitySession(pending.Subject, pending.SessionId, pending.IdToken);
    }

    private static async Task<string> FreeUsernameAsync(AppDbContext db, string wanted, CancellationToken ct)
    {
        var name = wanted.Trim();
        if (name.Length == 0) name = "admin";
        var candidate = name;
        for (var n = 2; await db.Users.AnyAsync(u => u.Username.ToLower() == candidate.ToLower(), ct); n++)
            candidate = $"{name}-{n}";
        return candidate;
    }
}
