namespace MatCMS.Models;

/// <summary>
/// A user-authored plugin: a C# script (run via Roslyn) that can register admin menu entries,
/// access framework services/data, etc. Authored entirely in the web admin.
/// </summary>
public class Plugin
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>C# script body executed with a <c>PluginContext</c> as globals.</summary>
    public string Code { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
