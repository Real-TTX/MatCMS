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

    /// <summary>
    /// How the plugin is built: <c>script</c> (C# script, source in <see cref="Code"/>/<see cref="FilesJson"/>,
    /// editable) or <c>native</c> (compiled DLLs under <c>appdata/plugins/{Key}/{Version}/src/</c>, not editable).
    /// Everything else — states, export/import, cloud, the plugin API — is the same for both.
    /// <para>Every row from before this column existed is a script: the default is the only migration.</para>
    /// </summary>
    public string Kind { get; set; } = PluginKind.Script;

    /// <summary>Native only: file name of the main assembly inside <c>src/</c> (e.g. <c>Shop.dll</c>). Empty for scripts.</summary>
    public string Entry { get; set; } = "";

    /// <summary>Native only: SHA-256 (hex) of the main assembly as imported, shown in the UI so an operator can compare
    /// what runs with what a store published. Empty for scripts.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>The plugin API major version this plugin was built against. <c>0</c> = a plugin written before the
    /// versioned API existed (treated as API 1, the <c>PluginContext</c> members).</summary>
    public int Api { get; set; }

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

    /// <summary>
    /// Die ROLLEN der Ordner dieses Plugins als JSON-Objekt Ordnerpfad → Rolle, z. B.
    /// <c>{"Bilder": "assets", "Gemeinsam": "include"}</c>. Siehe <see cref="MatCMS.Services.PluginMapping"/>.
    /// <para>Ordnernamen sind frei; die ROLLE entscheidet, was mit den Dateien darin geschieht. Deshalb
    /// steht sie hier am Plugin und nicht in der Dateikarte: ein Ordner ohne eigene Datei — der
    /// Assets-Ordner ist genau das — hätte in einer flachen Karte Pfad→Inhalt keinen Platz.</para>
    /// <para><b>LEER heißt Bestand, nicht „keine Rolle“:</b> ein Plugin, das dieses Feld nie
    /// geschrieben hat, bekommt die alte Festverdrahtung als Vorgabe (der Ordner <c>assets</c> trägt
    /// die Rolle Assets) und verhält sich damit exakt wie vorher. <c>{}</c> dagegen ist ausdrücklich
    /// „keine Rolle“ — sonst käme eine gerade entfernte Rolle beim nächsten Laden zurück.</para>
    /// </summary>
    public string MappingJson { get; set; } = "";

    /// <summary>Admin-editable configuration as a JSON object of string key→value pairs. Kept separate
    /// from the code so a plugin can be configured without editing it; read at runtime via <c>Config("key")</c>.</summary>
    public string ConfigJson { get; set; } = "{}";

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The values of <see cref="Plugin.Kind"/>.</summary>
public static class PluginKind
{
    /// <summary>A C# script, editable in the admin.</summary>
    public const string Script = "script";
    /// <summary>Compiled assemblies, loaded into a context of their own.</summary>
    public const string Native = "native";
    /// <summary>Compiled into the CMS itself (the blog): same API and state, but shipped with the image, enabled on a new site,
    /// and neither deletable, exportable nor replaceable by a bundle.</summary>
    public const string BuiltIn = "builtin";

    /// <summary>True for a value this host knows.</summary>
    public static bool IsKnown(string? kind) => kind is Script or Native or BuiltIn;

    /// <summary>An empty or unknown stored value is a script: rows written before the column existed (and a
    /// restore of such a backup) carry no kind, and a script is what they are.</summary>
    public static string Normalize(string? kind) => kind == Native ? Native : kind == BuiltIn ? BuiltIn : Script;
}
