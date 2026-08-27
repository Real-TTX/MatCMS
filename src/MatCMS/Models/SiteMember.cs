namespace MatCMS.Models;

/// <summary>
/// A PUBLIC-site visitor account — the "guest area" login, entirely separate from the back-office
/// <see cref="User"/>. Two different audiences (site visitors vs. administrators), two different
/// cookies, two different tables: a member must never gain access to the admin, and an admin account
/// must never be needed to read a members-only page.
/// <para>Accounts are created by an administrator (no self-registration), like the original wedding
/// site's <c>GuestArea:Accounts</c>.</para>
/// </summary>
public class SiteMember
{
    public int Id { get; set; }

    /// <summary>Login name, unique. What the visitor types.</summary>
    public string Username { get; set; } = "";

    /// <summary>Shown on the site once logged in (e.g. "Familie Jehle"). Falls back to the username.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Hashed with the same <see cref="Microsoft.AspNetCore.Identity.PasswordHasher{TUser}"/>
    /// the admin login uses — never stored in the clear.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>
    /// The roles this member holds, as a comma-separated list of <see cref="SiteRole.Name"/> values.
    /// A page requires at most one role (<see cref="Page.RequiredRole"/>); a member is granted any
    /// page whose required role is empty ("any logged-in member") or is one of these.
    /// <para>CSV rather than a join table on purpose: the set is tiny, edited as checkboxes, and never
    /// queried relationally — a join table would add an editor and a migration for no gain here.</para>
    /// </summary>
    public string RolesCsv { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The role names as a set, split from <see cref="RolesCsv"/>.</summary>
    public IReadOnlyCollection<string> Roles =>
        (RolesCsv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>A named group a <see cref="SiteMember"/> can belong to and a <see cref="Page"/> can
/// require (e.g. "Familie", "Trauzeugen"). Managed by an administrator; the set is small and picked,
/// not typed, so a page can only require a role that actually exists.</summary>
public class SiteRole
{
    public int Id { get; set; }

    /// <summary>Unique, human-readable. This exact string is what lives in <see cref="SiteMember.RolesCsv"/>
    /// and <see cref="Page.RequiredRole"/>, so renaming a role would orphan those references — it is
    /// treated as the stable identity, like a menu key.</summary>
    public string Name { get; set; } = "";
}
