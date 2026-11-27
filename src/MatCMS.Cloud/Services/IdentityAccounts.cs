using System.Security.Cryptography;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Which cloud account a person of MatIdentity is (MatDesign docs/PLATFORM.md "Sign-in with MatIdentity"). The rules, in order:
/// <list type="number">
/// <item>An account with the person's subject (<c>sub</c>) is that person; it follows the role MatIdentity sends.</item>
/// <item>An account without a subject whose e-mail (or user name) is the person's is <b>never</b> entered by the address alone: the answer is
///   <see cref="Outcome.Link"/> and the sign-in page links it after the account's own password (and second factor). Several matches, or a
///   match that already belongs to another person, are refused.</item>
/// <item>Else a new account: MatIdentity's role, else <c>Admin</c> for the very first person who comes through MatIdentity (whoever MatIdentity
///   lets in first is the owner of this cloud) and <c>Operator</c> for everyone after (scoped to no instance until an admin assigns some).</item>
/// </list>
/// </summary>
public static class IdentityAccounts
{
    /// <summary>The roles of the cloud as the platform names them (<c>roles</c> of <c>/api</c>, the claim <c>role</c>), the most powerful first.</summary>
    public const string RoleAdmin = "admin";
    public const string RoleOperator = "operator";

    public sealed record Person(string Subject, string? Email, string? Name, string? PreferredUserName, string? Role);

    public enum Kind { SignIn, Link, Refused }

    public sealed record Outcome(Kind Kind, User? User, string? Error)
    {
        public static Outcome Signed(User user) => new(Kind.SignIn, user, null);
        public static Outcome Link(User user) => new(Kind.Link, user, null);
        public static Outcome Refuse(string error) => new(Kind.Refused, null, error);
    }

    /// <summary>The cloud's role for the claim <c>role</c> of MatIdentity; null when there is none (the cloud decides).</summary>
    public static string? RoleOf(string? claim) => claim?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        RoleAdmin => User.RoleAdmin,
        _ => User.RoleOperator,
    };

    public static async Task<Outcome> ResolveAsync(AppDbContext db, Person person, Func<string, string> hashPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(person.Subject)) return Outcome.Refuse("no subject");
        var role = RoleOf(person.Role);

        var known = await db.Users.FirstOrDefaultAsync(u => u.ExternalSubject == person.Subject, ct);
        if (known is not null)
        {
            if (role is not null && known.Role != role)
            {
                known.Role = role;
                await db.SaveChangesAsync(ct);
            }
            return Outcome.Signed(known);
        }

        var email = person.Email?.Trim().ToLowerInvariant();
        var name = person.PreferredUserName?.Trim().ToLowerInvariant();
        var matches = await db.Users
            .Where(u => (email != null && u.Email != null && u.Email.ToLower() == email) || (name != null && u.Username.ToLower() == name))
            .ToListAsync(ct);
        if (matches.Count > 1) return Outcome.Refuse("several accounts match");
        if (matches.Count == 1)
        {
            return matches[0].ExternalSubject is null
                ? Outcome.Link(matches[0])
                : Outcome.Refuse("the account belongs to another person");
        }

        var firstThroughIdentity = !await db.Users.AnyAsync(u => u.ExternalSubject != null, ct);
        var user = new User
        {
            Username = await FreeUsernameAsync(db, person.PreferredUserName ?? person.Email ?? person.Subject, ct),
            Email = person.Email?.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(person.Name) ? null : person.Name.Trim(),
            Role = role ?? (firstThroughIdentity ? User.RoleAdmin : User.RoleOperator),
            ExternalSubject = person.Subject,
            // No usable local password: the account signs in through MatIdentity (an admin may set one on its page).
            PasswordHash = hashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return Outcome.Signed(user);
    }

    /// <summary>Links <paramref name="user"/> (who just proved it with the account's password) with the MatIdentity person; null when the
    /// person meanwhile belongs to another account.</summary>
    public static async Task<User?> LinkAsync(AppDbContext db, User user, Person person, CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(u => u.ExternalSubject == person.Subject && u.Id != user.Id, ct)) return null;
        user.ExternalSubject = person.Subject;
        if (RoleOf(person.Role) is { } role) user.Role = role;
        await db.SaveChangesAsync(ct);
        return user;
    }

    private static async Task<string> FreeUsernameAsync(AppDbContext db, string wanted, CancellationToken ct)
    {
        var name = wanted.Trim();
        if (name.Length == 0) name = "person";
        var candidate = name;
        for (var n = 2; await db.Users.AnyAsync(u => u.Username.ToLower() == candidate.ToLower(), ct); n++)
            candidate = $"{name}-{n}";
        return candidate;
    }
}
