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

    // --- MatIdentity (optional, MATIDENTITY_AUTHORITY) -------------------------
    // Only back-office accounts are ever linked; the site's members (SiteMember) stay local.

    /// <summary>The MatIdentity person (<c>sub</c>) this account belongs to; null = not linked. Set when the person is first created
    /// through MatIdentity or after the account's own password proved a match by address — never from the address alone.</summary>
    public string? ExternalSubject { get; set; }

    /// <summary>When MatIdentity last signed this person out everywhere (back-channel logout without a <c>sid</c>): a MatIdentity
    /// session cookie issued before it is rejected.</summary>
    public DateTime? IdentitySignedOutAt { get; set; }

    /// <summary>The admin bar over the public site for THIS user: null = follow the site setting
    /// (site.adminBar), "on" / "off" = the user's own choice. A login through the cloud's switcher shows
    /// it regardless (see SiteContext.ShowAdminBar).</summary>
    public string? AdminBar { get; set; }

    /// <summary>Colour scheme of the back office for this user: "light" (also null), "dark" or "system"
    /// (follows the operating system). Read by the admin layout into &lt;html data-theme&gt;.</summary>
    public string? Theme { get; set; }

    /// <summary>What the dashboard shows for THIS user and in which order, as JSON (<see cref="MatCMS.Services.DashboardLayout"/>); null = follow the site's default layout.</summary>
    public string? DashboardJson { get; set; }

    /// <summary>The state of the maintenance mode this user last saw and confirmed (JSON, <see cref="MatCMS.Services.MaintenanceNotice"/>);
    /// null = never looked yet (the first visit only records it). A different state that this user did not make asks for a confirmation.</summary>
    public string? MaintenanceSeen { get; set; }

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
