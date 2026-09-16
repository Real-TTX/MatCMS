namespace MatCMS.Cloud.Models;

/// <summary>An operator of the cloud itself (not an instance user). Login is by e-mail, with the
/// legacy username kept as a fallback identifier for the seeded "admin".</summary>
public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    /// <summary>"Admin" = full access to the whole cloud. "Operator" = may only see and manage the
    /// instances assigned in <see cref="Instances"/>; blocked from profiles, store, users, settings,
    /// API keys and from creating/deleting instances. An Operator with no assigned instances can reach
    /// nothing — deliberately inert, mirroring a scoped API key.</summary>
    public string Role { get; set; } = "Admin";
    public string? DisplayName { get; set; }
    public string? Email { get; set; }

    /// <summary>Instances this (Operator) user is scoped to. Ignored for Admins (they see everything).</summary>
    public List<UserInstance> Instances { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // --- Two-factor (TOTP) ----------------------------------------------------
    // Off for every existing account; turned on only after the user confirms a code during enrolment.
    // The secret is stored DataProtection-ENCRYPTED (see TwoFactorService) and recovery codes only as
    // SHA-256 hashes — neither is ever kept in the clear. All three are nullable/false so the migration
    // is purely additive and old rows upgrade untouched. Never rolled out to instances (ConfigUser
    // carries no 2FA field): a cloud account's second factor is a fact about the cloud login only.

    /// <summary>Whether this cloud account requires a second factor at login.</summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>DataProtection-encrypted TOTP secret (Base32 plaintext inside). Null until enrolled.</summary>
    public string? TotpSecret { get; set; }

    /// <summary>Newline-separated SHA-256 hashes of the still-unused single-use recovery codes; a code
    /// is consumed by removing its hash. Null/empty = none left.</summary>
    public string? RecoveryCodes { get; set; }

    public const string RoleAdmin = "Admin";
    public const string RoleOperator = "Operator";
}

/// <summary>One instance an Operator user is scoped to. Stored by internal <see cref="InstanceId"/>
/// (a cloud-side link, never sent to a site). Cascades from both ends — deleting the user or the
/// instance removes only the link. Mirrors <see cref="ApiKeyInstance"/>.</summary>
public class UserInstance
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public int InstanceId { get; set; }
    public Instance? Instance { get; set; }
}
