using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace MatCMS.Services;

/// <summary>
/// Löst <c>#load "datei.csx"</c> aus den Dateien AUF, die im Plugin selbst liegen — nicht auf der
/// Festplatte.
///
/// <para>Das ist der Punkt: Roslyn löst <c>#load</c> sonst gegen das Dateisystem auf, und ein Plugin
/// könnte damit beliebige Dateien des Servers einlesen. Dieser Resolver kennt nur die Dateien seines
/// eigenen Plugins; alles andere findet er nicht.</para>
///
/// <para>Ein Plugin ohne weitere Dateien bekommt einen leeren Resolver und verhält sich damit exakt
/// wie vorher — das Feld <c>Code</c> bleibt die Einstiegsdatei.</para>
/// </summary>
public sealed class PluginFileResolver : SourceReferenceResolver
{
    private readonly IReadOnlyDictionary<string, string> _files;

    public PluginFileResolver(IReadOnlyDictionary<string, string> files) => _files = files;

    /// <summary>Liest die Dateikarte eines Plugins. Ungültiges JSON ergibt eine leere Karte statt einer
    /// Ausnahme: ein kaputtes Feld darf höchstens dieses eine Plugin lahmlegen, nicht den Start.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Ein Pfad der Karte: Abschnitte durch "/", ohne "." und "..", ohne führendes "/".
    /// Ein ".." am Anfang kann nicht aus dem Plugin herausführen — es gibt nichts außerhalb der
    /// Karte, und der Abschnitt fällt weg statt eine Ebene über der Wurzel zu landen.</summary>
    private static string Normalise(string path)
    {
        var segments = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            var s = part.Trim();
            if (s.Length == 0 || s == ".") continue;
            if (s == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(s);
        }
        return string.Join('/', segments);
    }

    public override string NormalizePath(string path, string? baseFilePath) => Normalise(path);

    /// <summary>
    /// Sucht ZUERST ab der Wurzel des Plugins — so bleibt jedes bestehende <c>#load "menu.csx"</c>
    /// genau das, was es war —, und erst danach neben der ladenden Datei.
    ///
    /// <para>Ohne den zweiten Schritt wäre ein <c>#load "Helper.csx"</c> aus
    /// <c>Elements/Hero.csx</c> heraus ins Leere gelaufen, obwohl die Datei direkt daneben liegt:
    /// mit Ordnern ist genau das die Schreibweise, die man erwartet.</para>
    /// </summary>
    public override string? ResolveReference(string path, string? baseFilePath)
    {
        var direct = Normalise(path);
        if (_files.ContainsKey(direct)) return direct;

        // baseFilePath ist der aufgelöste Pfad der Datei, in der das #load steht (bei der
        // Einstiegsdatei leer — dort gibt es kein "daneben", sie liegt selbst an der Wurzel).
        if (!string.IsNullOrEmpty(baseFilePath))
        {
            var baseDir = Normalise(baseFilePath);
            var cut = baseDir.LastIndexOf('/');
            if (cut > 0)
            {
                var relative = Normalise(baseDir[..cut] + "/" + path);
                if (_files.ContainsKey(relative)) return relative;
            }
        }
        return null;
    }

    public override Stream OpenRead(string resolvedPath)
        => _files.TryGetValue(Normalise(resolvedPath), out var content)
            ? new MemoryStream(Encoding.UTF8.GetBytes(content))
            : throw new FileNotFoundException(resolvedPath);

    public override bool Equals(object? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => _files.Count;
}
