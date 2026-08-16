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

    private static string Normalise(string path) => path.Replace('\\', '/').TrimStart('.', '/');

    public override string NormalizePath(string path, string? baseFilePath) => Normalise(path);

    public override string? ResolveReference(string path, string? baseFilePath)
        => _files.ContainsKey(Normalise(path)) ? Normalise(path) : null;

    public override Stream OpenRead(string resolvedPath)
        => _files.TryGetValue(Normalise(resolvedPath), out var content)
            ? new MemoryStream(Encoding.UTF8.GetBytes(content))
            : throw new FileNotFoundException(resolvedPath);

    public override bool Equals(object? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => _files.Count;
}
