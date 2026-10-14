using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatCMS.Plugin;

namespace MatCMS.Services.Plugins;

/// <summary>
/// A plugin's language files: <c>strings/de.json</c>, <c>strings/en.json</c> … — each a flat JSON object <c>{ "key": "text" }</c>. The same
/// convention for both kinds: a script keeps them among its files, a native plugin ships them in <c>src/strings/</c>. They are read when the
/// plugin loads and before it configures, so what the code registers with <c>Plugin.Strings(...)</c> takes precedence — a file is the
/// default, the code the override.
/// </summary>
internal static class PluginStringFiles
{
    private static readonly Regex FileName = new(@"^strings/([a-z]{2,3}(?:-[A-Za-z]{2,4})?)\.json$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Reads the language files out of a script's file map. A file that is not valid JSON is skipped and reported through
    /// <paramref name="problem"/>; one broken translation must not keep the plugin from loading.</summary>
    public static void FromFiles(PluginRegistry.Slice slice, IReadOnlyDictionary<string, string> files, Action<string>? problem = null)
    {
        foreach (var (path, content) in files)
            if (FileName.Match(path.Replace('\\', '/')) is { Success: true } m) Add(slice, m.Groups[1].Value, content, path, problem);
    }

    /// <summary>Reads <c>strings/*.json</c> out of a native plugin's source folder.</summary>
    public static void FromDirectory(PluginRegistry.Slice slice, string srcDir, Action<string>? problem = null)
    {
        var dir = Path.Combine(srcDir, "strings");
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            if (FileName.Match("strings/" + Path.GetFileName(file)) is { Success: true } m)
                Add(slice, m.Groups[1].Value, File.ReadAllText(file), "strings/" + Path.GetFileName(file), problem);
    }

    private static void Add(PluginRegistry.Slice slice, string culture, string json, string path, Action<string>? problem)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) { problem?.Invoke($"{path}: kein JSON-Objekt"); return; }
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in o) if (kv.Value is not null) table[kv.Key] = kv.Value.ToString();
            slice.AddStrings(culture.ToLowerInvariant(), table);
        }
        catch (System.Text.Json.JsonException ex) { problem?.Invoke($"{path}: {ex.Message}"); }
    }
}
