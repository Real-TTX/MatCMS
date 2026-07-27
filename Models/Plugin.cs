namespace MatCMS.Models;

/// <summary>
/// A user-authored plugin: a C# script (run via Roslyn) that can register admin menu entries,
/// access framework services/data, etc. Authored entirely in the web admin.
/// </summary>
public class Plugin
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>
    /// Stable, filesystem/URL-safe slug identifying this plugin. Assigned once at creation and never
    /// changed. Names the plugin's own asset folder (<c>appdata/plugin-assets/{Key}/</c>, served at
    /// <c>/plugin-assets/{Key}/…</c>) so every plugin is a self-contained bundle (code + its assets).
    /// </summary>
    public string Key { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Author-declared version of this plugin (e.g. "1" or "1.2"). Shown in the UI and
    /// carried in the export bundle; a same-Key import replaces the plugin with this version.</summary>
    public string Version { get; set; } = "";

    /// <summary>The version this plugin's stored data was last migrated to (see <c>Migrate</c> in the
    /// plugin API). Lets a plugin evolve its data idempotently across updates.</summary>
    public string DataVersion { get; set; } = "";

    /// <summary>C# script body executed with a <c>PluginContext</c> as globals.</summary>
    public string Code { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
