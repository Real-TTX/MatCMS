namespace MatCMS.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Admin";
    public string? DisplayName { get; set; }

    /// <summary>Optional e-mail address — used as a selectable recipient for form notifications.</summary>
    public string? Email { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // --- Two-factor (TOTP) ----------------------------------------------------
    // Off for every existing account; turned on only after the user confirms a code during enrolment.
    // The secret is stored DataProtection-ENCRYPTED (see TwoFactorService) and recovery codes only as
    // SHA-256 hashes — neither is ever kept in the clear. All three are nullable/false so the migration
    // is purely additive and old rows upgrade untouched.

    /// <summary>Whether this account requires a second factor at login.</summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>DataProtection-encrypted TOTP secret (Base32 plaintext inside). Null until enrolled.</summary>
    public string? TotpSecret { get; set; }

    /// <summary>Newline-separated SHA-256 hashes of the still-unused single-use recovery codes; a code
    /// is consumed by removing its hash. Null/empty = none left.</summary>
    public string? RecoveryCodes { get; set; }
}
