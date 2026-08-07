namespace MatCMS.Cloud.Models;

/// <summary>An operator of the cloud itself (not an instance user). Login is by e-mail, with the
/// legacy username kept as a fallback identifier for the seeded "admin".</summary>
public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Admin";
    public string? DisplayName { get; set; }
    public string? Email { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
