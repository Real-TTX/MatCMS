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
    /// <para>Bleibt die EINSTIEGSDATEI, auch wenn ein Plugin weitere Dateien mitbringt. Das Feld
    /// nicht abzulösen ist Absicht: Bundle-Format und Datenmodell werden von der Cloud mitgelesen,
    /// und ein alter Leser bekommt so weiterhin etwas Gültiges statt einer leeren Hülle.</para>
    public string Code { get; set; } = "";

    /// <summary>
    /// Weitere Skriptdateien des Plugins als JSON-Objekt Pfad → Inhalt, z. B.
    /// <c>{"menu.csx": "…"}</c>. Erreichbar aus <see cref="Code"/> über <c>#load "menu.csx"</c>.
    /// <para>Leer bei jedem heutigen Plugin — dann verhält sich alles exakt wie vorher.</para>
    /// </summary>
    public string FilesJson { get; set; } = "{}";

    /// <summary>Admin-editable configuration as a JSON object of string key→value pairs. Kept separate
    /// from the code so a plugin can be configured without editing it; read at runtime via <c>Config("key")</c>.</summary>
    public string ConfigJson { get; set; } = "{}";

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
