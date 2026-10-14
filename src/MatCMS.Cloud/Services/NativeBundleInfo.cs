using System.IO.Compression;
using System.Text.Json;
using MatCMS.Shared;

namespace MatCMS.Cloud.Services;

/// <summary>
/// What the cloud shows of a NATIVE plugin bundle (<c>Kind: native</c>, compiled assemblies under <c>src/</c>). The cloud only stores and re-packs
/// bundles; it never loads one, and it must not pretend to edit one: there is no source to edit, and the manifest's <c>Code</c> is only the note
/// that tells an older instance why it cannot run the plugin. So a native bundle is shown read-only, and re-packing it leaves <c>Code</c> alone.
/// </summary>
public sealed record NativeBundleInfo(string Entry, string Sha256, string Api, IReadOnlyList<(string Path, long Size)> Files)
{
    /// <summary>The facts of a native bundle, or null when the bundle is a script (or unreadable).</summary>
    public static NativeBundleInfo? Read(byte[] zipBytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            var entry = zip.GetEntry(PluginBundle.ManifestEntry);
            if (entry is null || entry.Length > 1024 * 1024) return null;
            using var doc = JsonDocument.Parse(new StreamReader(entry.Open()).ReadToEnd());
            string Get(string name)
            {
                foreach (var candidate in new[] { name, char.ToLowerInvariant(name[0]) + name[1..] })
                    if (doc.RootElement.TryGetProperty(candidate, out var v))
                        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
                return "";
            }
            if (!string.Equals(Get("Kind"), PluginBundle.KindNative, StringComparison.OrdinalIgnoreCase)) return null;
            var files = zip.Entries
                .Where(e => e.FullName.StartsWith(PluginBundle.SourceFolder, StringComparison.OrdinalIgnoreCase) && e.Length > 0)
                .Select(e => (e.FullName[PluginBundle.SourceFolder.Length..], e.Length)).OrderBy(f => f.Item1, StringComparer.OrdinalIgnoreCase).ToList();
            return new NativeBundleInfo(Get("Entry"), Get("Sha256"), Get("Api"), files);
        }
        catch { return null; }
    }

    /// <summary>True when the manifest values (as read for a re-pack) describe a native plugin.</summary>
    public static bool IsNative(IReadOnlyDictionary<string, object?> manifest) =>
        manifest.TryGetValue("Kind", out var k) && string.Equals(k as string, PluginBundle.KindNative, StringComparison.OrdinalIgnoreCase);
}
