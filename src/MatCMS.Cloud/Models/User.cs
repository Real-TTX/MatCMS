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
