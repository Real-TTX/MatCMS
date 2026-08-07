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
}
