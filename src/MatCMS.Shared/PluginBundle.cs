namespace MatCMS.Shared;

/// <summary>
/// The plugin bundle FORMAT, as far as both sides must agree on it: where the manifest lives, what
/// the asset folder is called, and the limits a reader enforces before trusting a zip.
/// <para>The two implementations stay separate on purpose. <c>MatCMS/Services/PluginPackager</c>
/// imports into the CMS (EF, file system, plugin migration); the cloud only stores and re-packs
/// bundles, and does so by editing the manifest **field by field** so properties its editor does not
/// surface survive a save. A shared typed manifest class would quietly drop exactly those fields —
/// which is why what is shared here is the shape of the container, not a DTO for its contents.</para>
/// </summary>
public static class PluginBundle
{
    /// <summary>Manifest entry inside the zip. The identity of the format.</summary>
    public const string ManifestEntry = "plugin.json";

    /// <summary>Folder holding the plugin's static assets inside the zip.</summary>
    public const string AssetFolder = "assets/";

    /// <summary>
    /// Folder holding the plugin's FURTHER SCRIPT FILES inside the zip — the map path→content that the
    /// CMS keeps in <c>Plugin.FilesJson</c> and that the entry file reaches with <c>#load</c>. One entry
    /// per file, so a bundle's file list is visible in any zip viewer and a reader needs no second
    /// format inside the manifest.
    /// <para>Why entries and not a manifest field: the cloud edits <c>plugin.json</c> field by field and
    /// copies every OTHER entry byte for byte, so files placed here survive a cloud round trip on their
    /// own. A nested object in the manifest would be flattened to a string by that same field-wise
    /// editor and come out corrupted — the property survives, its meaning does not.</para>
    /// <para>Compatibility runs both ways and needs no version bump, which is why <c>Format</c> stays 1:
    /// a reader that predates this folder skips every entry outside <see cref="AssetFolder"/> and
    /// therefore imports the plugin with its entry file alone; a reader that knows it finds no such
    /// entries in an old bundle and reads a plugin with exactly one file. Neither case is an error.</para>
    /// </summary>
    public const string FileFolder = "files/";

    /// <summary>Name the entry file goes by in the UI. It lives in the manifest's <c>Code</c> field and
    /// never under <see cref="FileFolder"/> — two places for one file would mean one of them wins
    /// silently.</summary>
    public const string EntryFile = "plugin.csx";

    /// <summary>Largest manifest a reader will parse. A manifest is metadata; anything this big is
    /// not one.</summary>
    public const long MaxManifestBytes = 1 * 1024 * 1024;

    /// <summary>Zip-bomb guards: per asset, for all assets combined, and on the file count.</summary>
    public const long MaxAssetBytes = 5 * 1024 * 1024;
    public const long MaxTotalBytes = 25 * 1024 * 1024;
    public const int MaxAssetFiles = 200;

    /// <summary>The same guards for the script files. Smaller per file, because a script file is source
    /// text that ends up in a database column, not something streamed off disk.</summary>
    public const long MaxFileBytes = 1 * 1024 * 1024;
    public const int MaxFiles = 200;

    // ---- native plugins -------------------------------------------------------------------------
    // A bundle has two kinds. SCRIPT bundles are exactly what they always were — Format 1, the entry file in the
    // manifest's `Code`, further files under files/ — and stay that way on purpose: older instances and the cloud's
    // store editor read them, and an older reader IGNORES `Format` entirely, so a new shape for scripts would be
    // imported by them as an empty plugin without a word. NATIVE bundles are new and additive.

    /// <summary>Manifest <c>Kind</c> of a script plugin (also what a bundle without the field is).</summary>
    public const string KindScript = "script";

    /// <summary>Manifest <c>Kind</c> of a native plugin: compiled assemblies under <see cref="SourceFolder"/>.</summary>
    public const string KindNative = "native";

    /// <summary>
    /// Folder holding a NATIVE plugin's assemblies (and what they bring: strings, dependency DLLs) inside the zip and
    /// on disk (<c>appdata/plugins/{Key}/{Version}/src/</c>). For a script plugin the same word names the editor's root
    /// of source files; there the files are still stored the old way (see above).
    /// <para>Like <c>files/</c>, one zip entry each — the cloud copies every entry other than the manifest byte for
    /// byte, so a native bundle survives a cloud round trip untouched.</para>
    /// </summary>
    public const string SourceFolder = "src/";

    /// <summary>What a native bundle's <c>Code</c> says: the one thing an OLDER reader can do with it — import a plugin
    /// whose only source is this line, which then reports why it cannot run instead of being silently empty.</summary>
    public const string NativeGuardCode =
        "throw new System.NotSupportedException(\"Dies ist ein natives Plugin. Es braucht ein MatCMS mit nativen Plugins — bitte MatCMS aktualisieren.\");";

    /// <summary>A native bundle may be heavier than a script one: it carries assemblies. Per file, for all of
    /// <c>src/</c>, and on the count — the same zip-bomb guards as for assets, sized for DLLs.</summary>
    public const long MaxSourceFileBytes = 25 * 1024 * 1024;
    public const long MaxSourceTotalBytes = 60 * 1024 * 1024;
    public const int MaxSourceFiles = 300;

    /// <summary>File types allowed under <c>src/</c>: managed assemblies, their dependency and string files, and the
    /// native libraries a dependency may need. No scripts, no executables.</summary>
    public static readonly string[] AllowedSourceExt = [".dll", ".json", ".txt", ".so"];

    /// <summary>
    /// Straightens a path below <see cref="SourceFolder"/> and says whether it may be written to disk. Nested folders
    /// are fine (<c>strings/de.json</c>, <c>runtimes/linux-x64/native/x.so</c>); what is refused is anything that could
    /// leave the folder or confuse a file system: "..", control characters, the characters no file system agrees on,
    /// and a file type that is not on the list.
    /// </summary>
    /// <returns>The normalised path (forward slashes), or <c>null</c>.</returns>
    public static string? NormalizeSourcePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Replace('\\', '/').Trim();
        if (text.EndsWith('/')) return null;

        var segments = new List<string>();
        foreach (var part in text.Split('/'))
        {
            var s = part.Trim();
            if (s.Length == 0 || s == ".") continue;
            if (s == ".." || s.StartsWith('.')) return null;
            foreach (var ch in s)
                if (char.IsControl(ch) || ch is ':' or '*' or '?' or '"' or '<' or '>' or '|') return null;
            segments.Add(s);
        }
        if (segments.Count == 0) return null;

        var path = string.Join('/', segments);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return AllowedSourceExt.Contains(ext) ? path : null;
    }

    /// <summary>Asset file types a bundle may carry. SVG is excluded deliberately — served from the
    /// site's own origin it is a stored-XSS vector.</summary>
    public static readonly string[] AllowedAssetExt =
        [".js", ".mjs", ".css", ".json", ".map", ".woff", ".woff2", ".ttf", ".eot", ".png", ".jpg", ".jpeg", ".gif", ".webp"];

    /// <summary>
    /// Straightens a script-file path and says whether it may live in a bundle at all. This is part of
    /// the CONTAINER's shape — which paths a bundle can carry — not a manifest DTO, so it belongs here
    /// where both sides can see the same rule.
    /// <para>Straightened silently is anything that is only spelling: backslashes, doubled or leading
    /// "/", "./", whitespace around a segment. Refused (null) is anything ambiguous or dangerous:
    /// "..", a trailing "/" (an empty folder, which a flat map cannot carry), control characters and
    /// the characters no file system agrees on, the <see cref="AssetFolder"/> branch (that one belongs
    /// to the disk, not to this map) and the <see cref="EntryFile"/> itself.</para>
    /// <para>The same rule as the plugin editor's, on purpose: a path the editor refuses must not be
    /// creatable by importing a bundle instead. Refusing rather than sanitising is what keeps an
    /// imported path identical to what the author typed — a silently rewritten path would break the
    /// <c>#load</c> that points at it.</para>
    /// </summary>
    /// <returns>The normalised path, or <c>null</c> when it may not be stored.</returns>
    public static string? NormalizeFilePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.Replace('\\', '/').Trim();
        if (text.EndsWith('/')) return null;

        var segments = new List<string>();
        foreach (var part in text.Split('/'))
        {
            var s = part.Trim();
            if (s.Length == 0 || s == ".") continue;
            if (s == "..") return null;
            foreach (var ch in s)
                if (char.IsControl(ch) || ch is ':' or '*' or '?' or '"' or '<' or '>' or '|') return null;
            segments.Add(s);
        }
        if (segments.Count == 0) return null;

        var path = string.Join('/', segments);
        if (string.Equals(segments[0], AssetFolder.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(path, EntryFile, StringComparison.OrdinalIgnoreCase)) return null;
        return path;
    }
}
