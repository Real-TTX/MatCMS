using System.Text.RegularExpressions;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The rule of docs/plugin-system-v2.md §3.2a, checked over the SOURCES in <c>plugins/</c>: every plugin describes its content as operations
/// (a <c>PluginOperationSpec</c> in its code), or it is listed in <c>plugins/OPERATIONS.md</c> with the reason it has none. A new plugin that is
/// neither fails here — agents would not know it exists. And every write op of a plugin answers with an undo (an internal op next to it).
/// </summary>
public class PluginOperationsRuleTests
{
    private static string PluginsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatCMS.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "plugins");
    }

    private static IEnumerable<string> Sources(string folder) =>
        Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csx", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    /// <summary>The plugins listed as having no operations: rows <c>| key | … | reason |</c> whose last cell says why.</summary>
    private static Dictionary<string, string> Exemptions(string root)
    {
        var file = Path.Combine(root, "OPERATIONS.md");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(file)) return result;
        foreach (var line in File.ReadAllLines(file))
        {
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim().Trim('`').Trim()).ToList();
            if (cells.Count < 2 || cells[0].Length == 0 || cells[0].StartsWith('-')) continue;
            var reason = cells[^1];
            if (reason.Length > 3 && reason is not ("—" or "-")) result[cells[0]] = reason;
        }
        return result;
    }

    [Fact]
    public void Every_plugin_describes_operations_or_is_listed_with_its_reason()
    {
        var root = PluginsRoot();
        var exempt = Exemptions(root);
        var missing = new List<string>();
        foreach (var folder in Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, "meta.json"))))
        {
            var key = Path.GetFileName(folder);
            var hasOps = Sources(folder).Any(f => File.ReadAllText(f).Contains("PluginOperationSpec", StringComparison.Ordinal));
            if (!hasOps && !exempt.ContainsKey(key)) missing.Add(key);
        }
        Assert.True(missing.Count == 0,
            "Diese Plugins beschreiben keine Operationen und stehen nicht mit Grund in plugins/OPERATIONS.md: " + string.Join(", ", missing));
    }

    [Fact]
    public void A_plugin_with_operations_is_not_also_listed_as_an_exemption()
    {
        var root = PluginsRoot();
        foreach (var (key, _) in Exemptions(root))
        {
            var folder = Path.Combine(root, key);
            if (!Directory.Exists(folder)) continue;
            Assert.False(Sources(folder).Any(f => File.ReadAllText(f).Contains("PluginOperationSpec", StringComparison.Ordinal)),
                $"{key} hat Operationen, steht aber als Ausnahme in plugins/OPERATIONS.md.");
        }
    }

    /// <summary>A plugin that writes must be able to take it back: where a plugin declares writing ops, it declares internal ones too.</summary>
    [Fact]
    public void A_plugin_with_writing_operations_declares_internal_undo_operations()
    {
        var root = PluginsRoot();
        // A spec as the plugins write it: "PluginOperationSpec X = new(" or "new PluginOperationSpec(" — not any other new("…") (string tables).
        var spec = new Regex(@"(?:PluginOperationSpec\s+\w+\s*=\s*new|new\s+PluginOperationSpec)\s*\(\s*""[a-z][a-z0-9_.-]*""(?<rest>[^;]*);", RegexOptions.Singleline);
        foreach (var folder in Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, "meta.json"))))
        {
            var text = string.Join("\n", Sources(folder).Select(File.ReadAllText));
            if (!text.Contains("PluginOperationSpec", StringComparison.Ordinal)) continue;
            var specs = spec.Matches(text).Select(m => m.Groups["rest"].Value).ToList();
            var writes = specs.Any(r => !r.Contains("Writes: false") && !r.Contains("Internal: true"));
            if (!writes) continue;
            Assert.True(specs.Any(r => r.Contains("Internal: true")), $"{Path.GetFileName(folder)} hat schreibende Operationen, aber keine interne Undo-Operation.");
        }
    }
}
