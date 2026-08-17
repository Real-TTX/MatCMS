using System.Text;
using System.Text.Json;

namespace MatCMS.Services;

/// <summary>
/// Die ROLLEN der Ordner eines Plugins: Ordnerpfad → Rolle, gespeichert als JSON in
/// <c>Plugin.MappingJson</c>.
///
/// <para>Der Gedanke dahinter: Ordnernamen sind frei, die ROLLE entscheidet, was mit den Dateien
/// darin passiert. Ein Ordner, der zwingend <c>assets</c> heißen muss, ist derselbe Fehler in klein —
/// er ist Assets-Ordner, WEIL ihm die Rolle zugewiesen ist, nicht wegen seines Namens.</para>
///
/// <para>Zwei Rollen, und nur zwei, weil hinter beiden auch etwas liegt:
/// <list type="bullet">
/// <item><b>Assets</b> — die Dateien dieses Ordners liegen auf der Platte
/// (<c>appdata/plugin-assets/{Key}/</c>) und werden unter <c>/plugin-assets/{Key}/{Datei}</c>
/// ausgeliefert. Der Ordnername im Baum ist eine BESCHRIFTUNG, kein Pfad: ihn umzubenennen ändert
/// keine einzige URL. Es gibt genau ein Verzeichnis auf der Platte, also darf höchstens EIN Ordner
/// diese Rolle tragen.</item>
/// <item><b>Include</b> — alle Dateien dieses Ordners werden vor der Einstiegsdatei geladen, ohne
/// dass jemand sie von Hand mit <c>#load</c> einbindet. Alphabetisch nach vollem Pfad, damit die
/// Reihenfolge im Baum abzulesen ist und sich nicht mit der Laune der Kartenreihenfolge ändert.</item>
/// </list></para>
///
/// <para><b>Was hier bewusst FEHLT: eine Rolle „Blöcke“ (je Datei ein Block).</b> Roslyn führt alle
/// per <c>#load</c> geladenen Dateien in EINEM Skript-Gültigkeitsbereich zusammen; es gibt kein
/// „diese Datei ist ein Block“, das eine Datei von sich aus ausdrücken könnte. Man müsste eine
/// Namenskonvention erfinden (<c>Hero.csx</c> muss <c>string Hero(PluginRequest)</c> deklarieren) —
/// ein Vertipper darin legt dann nicht eine Datei lahm, sondern das ganze Plugin, und Name,
/// Beschreibung und Felder eines Blocks (<c>AddBlock(type, name, description, render, fieldsJson)</c>)
/// kann ein Dateiname ohnehin nicht tragen. Die Rolle verspräche mehr, als hinter ihr liegt; dafür
/// gibt es die Blaupause „Block registrieren“, die genau den Aufruf schreibt, den die vorhandenen
/// Plugins auch von Hand schreiben.</para>
/// </summary>
public static class PluginMapping
{
    public const string RoleAssets = "assets";
    public const string RoleInclude = "include";

    /// <summary>Der Ordnername, den die Rolle Assets vor ihrer Einführung fest verdrahtet hatte.
    /// Bestandsplugins erben ihn als Vorgabe — eine Wanderung über alle Zeilen wäre viel Aufwand für
    /// genau dieses eine Ergebnis.</summary>
    public const string LegacyAssetFolder = "assets";

    /// <summary>
    /// Liest die Rollenkarte. <b>Leer ist nicht „keine Rolle“, sondern „nie geschrieben“</b> und
    /// ergibt die Vorgabe des Bestands; ein ausdrückliches <c>{}</c> dagegen ist wirklich leer. Ohne
    /// diesen Unterschied käme eine gerade entfernte Rolle beim nächsten Laden zurück.
    /// <para>Kaputtes JSON ergibt ebenfalls die Vorgabe: der Zweig mit den hochgeladenen Dateien darf
    /// nicht dadurch unsichtbar werden, dass ein Feld daneben nicht parst.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Legacy();
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (raw is null) return Legacy();
            var clean = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (folder, role) in raw)
            {
                var f = NormalizeFolder(folder);
                var r = NormalizeRole(role);
                if (f is null || r is null) continue;
                clean[f] = r;
            }
            return clean;
        }
        catch { return Legacy(); }
    }

    private static Dictionary<string, string> Legacy() =>
        new(StringComparer.Ordinal) { [LegacyAssetFolder] = RoleAssets };

    /// <summary>Der Ordner mit der Rolle Assets, oder <c>null</c>. Bei mehreren (nur aus einer von
    /// Hand geschriebenen Karte möglich — gespeichert wird das nicht) gewinnt der erste alphabetisch,
    /// damit die Anzeige jedenfalls stabil ist.</summary>
    public static string? AssetsFolder(IReadOnlyDictionary<string, string> mapping) =>
        mapping.Where(kv => kv.Value == RoleAssets).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>Rolle eines Ordners, oder "" wenn er keine trägt.</summary>
    public static string RoleOf(IReadOnlyDictionary<string, string> mapping, string folder) =>
        mapping.TryGetValue(folder, out var r) ? r : "";

    /// <summary>Liegt <paramref name="path"/> IN <paramref name="folder"/> (oder ist er es selbst)?
    /// Vergleicht Abschnitte, nicht Zeichen: „assetsX/a.csx“ liegt nicht in „assets“.</summary>
    public static bool IsUnder(string path, string folder) =>
        folder.Length > 0
        && (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Die Dateien aller Include-Ordner, alphabetisch nach vollem Pfad — genau die Reihenfolge, in
    /// der sie geladen werden. Unterordner zählen mit: ein Ordner IN einem Include-Ordner wäre sonst
    /// eine tote Ecke, in der Dateien liegen, die nie laufen.
    /// </summary>
    public static List<string> IncludeFiles(
        IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> mapping)
    {
        var folders = mapping.Where(kv => kv.Value == RoleInclude).Select(kv => kv.Key).ToList();
        if (folders.Count == 0) return new();
        return files.Keys
            .Where(p => folders.Any(f => IsUnder(p, f)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Baut den Text, der ausgeführt wird: die <c>#load</c>-Zeilen der Include-Ordner vor der
    /// Einstiegsdatei.
    ///
    /// <para><b>Ohne Include-Ordner kommt der Code unverändert zurück</b> — Zeichen für Zeichen,
    /// samt Zeilennummern. Ein Plugin ohne gesetzte Rolle läuft damit exakt wie vorher, und das ist
    /// die Bedingung, unter der eine neue Rolle überhaupt eingebaut werden darf.</para>
    ///
    /// <para>Ein <c>#load</c> auf dieselbe Datei führt sie NICHT zweimal aus: Roslyn merkt sich die
    /// bereits geladenen Dateien an ihrem aufgelösten Pfad, und <see cref="PluginFileResolver"/> löst
    /// beide Schreibweisen auf denselben Pfad ab der Wurzel auf. Wer eine Datei aus einem
    /// Include-Ordner zusätzlich von Hand einbindet, bekommt sie einmal — nicht doppelt.</para>
    ///
    /// <para>Die eingefügten Zeilen kommen HINTER den führenden <c>#r</c>-Zeilen der Einstiegsdatei:
    /// eine Assembly-Referenz gehört an den Anfang der Datei, und sie dorthin zu verschieben, wo sie
    /// nicht mehr gilt, wäre ein Übersetzungsfehler in fremdem Code.</para>
    /// </summary>
    public static string BuildEntry(string? code, IReadOnlyList<string> includeFiles)
    {
        if (includeFiles.Count == 0) return code ?? "";

        var prologue = new StringBuilder();
        foreach (var f in includeFiles)
            prologue.Append("#load \"").Append(f.Replace("\"", "")).Append("\"\n");

        var text = code ?? "";
        var lines = text.Split('\n');
        var cut = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith("//") || t.StartsWith("#r") || t.StartsWith("#load")) { cut = i + 1; continue; }
            break;
        }
        var head = cut == 0 ? "" : string.Join("\n", lines.Take(cut)) + "\n";
        var rest = string.Join("\n", lines.Skip(cut));
        return head + prologue + rest;
    }

    /// <summary>Ein Ordnerpfad der Rollenkarte: Abschnitte durch "/", ohne "." und "..", ohne
    /// Steuerzeichen. <c>null</c>, wenn daraus kein brauchbarer Pfad wird.</summary>
    public static string? NormalizeFolder(string? raw)
    {
        if (raw is null) return null;
        var segments = new List<string>();
        foreach (var part in raw.Replace('\\', '/').Split('/'))
        {
            var s = part.Trim();
            if (s.Length == 0 || s == ".") continue;
            if (s == "..") return null;
            if (s.Any(ch => char.IsControl(ch) || ch is ':' or '*' or '?' or '"' or '<' or '>' or '|')) return null;
            segments.Add(s);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private static string? NormalizeRole(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        RoleAssets => RoleAssets,
        RoleInclude => RoleInclude,
        _ => null   // eine unbekannte Rolle fällt weg statt das Speichern zu blockieren
    };

    /// <summary>
    /// Prüft und begradigt die Rollenkarte gegen die Dateikarte.
    ///
    /// <para>Zurückgewiesen wird mit Nennung des Ordners, statt ihn stillschweigend fallenzulassen:
    /// die Rolle Assets ein zweites Mal (es gibt nur EIN Verzeichnis auf der Platte — die zweite
    /// Vergabe würde die erste entwerten, und zwar unbemerkt), und ein Assets-Ordner, in dem
    /// Skriptdateien der Karte liegen (die gehören der Karte, seine Dateien der Platte — beides in
    /// einem Ordner wären zwei Wahrheiten über denselben Pfad).</para>
    /// </summary>
    /// <returns>Das begradigte JSON, oder eine Meldung, warum nichts gespeichert wurde.</returns>
    public static (string Json, string? Error) Normalize(string? json, IReadOnlyDictionary<string, string> files)
    {
        // Nie geschrieben heißt Bestand: als ausdrückliche Karte speichern, damit ab jetzt der
        // Unterschied zwischen „keine Rolle“ und „nie gesetzt“ hält.
        if (string.IsNullOrWhiteSpace(json)) json = JsonSerializer.Serialize(Legacy());

        Dictionary<string, string>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch { return ("", "Die Rollenkarte der Ordner ist kein gültiges JSON — bitte unter „Erweitert“ korrigieren."); }
        if (raw is null) return ("{}", null);

        var clean = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? assets = null;
        foreach (var (rawFolder, rawRole) in raw)
        {
            var folder = NormalizeFolder(rawFolder);
            if (folder is null) return ("", $"„{rawFolder}“ ist kein zulässiger Ordnerpfad für eine Rolle.");
            var role = NormalizeRole(rawRole);
            if (role is null) continue;                       // unbekannte Rolle: fällt weg

            if (role == RoleAssets)
            {
                if (assets is not null && !string.Equals(assets, folder, StringComparison.Ordinal))
                    return ("", $"Die Rolle „Assets“ ist zweimal vergeben („{assets}“ und „{folder}“). "
                                + "Es gibt nur ein Verzeichnis auf der Platte — sie kann nur an einem Ordner hängen.");
                assets = folder;
                var inside = files.Keys.FirstOrDefault(k => IsUnder(k, folder));
                if (inside is not null)
                    return ("", $"„{folder}“ soll die Rolle „Assets“ tragen, enthält aber die Skriptdatei „{inside}“. "
                                + "In einem Assets-Ordner liegen Dateien der Platte, keine Dateien der Karte.");
            }
            clean[folder] = role;
        }
        return (JsonSerializer.Serialize(clean, new JsonSerializerOptions { WriteIndented = true }), null);
    }
}
