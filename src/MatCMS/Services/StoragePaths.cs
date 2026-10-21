namespace MatCMS.Services;

/// <summary>
/// Central filesystem locations. Everything the app persists lives under a single data directory
/// (<c>appdata/</c>) so one Docker volume mounted at <c>/app/appdata</c> holds the database,
/// data-protection keys, scheduled backups AND uploaded media.
/// </summary>
public static class StoragePaths
{
    /// <summary>The single persisted data directory (mounted as one volume in production).</summary>
    public static string DataDir(IWebHostEnvironment env) => Path.Combine(env.ContentRootPath, "appdata");

    /// <summary>Uploaded media files, served publicly under <c>/uploads</c>.</summary>
    public static string Uploads(IWebHostEnvironment env) => Path.Combine(DataDir(env), "uploads");

    /// <summary>
    /// Cached thumbnails of uploaded images, one sub-folder per width. Deliberately a SIBLING of
    /// <see cref="Uploads"/> and not a folder inside it: the backup walks <c>uploads/</c> flat and
    /// would otherwise pack every thumbnail into <c>assets/</c> — doubling a customer's backup weight
    /// against their cloud quota for files that are pure derivatives and cost one request to rebuild.
    /// Nothing here is content; the folder may be deleted at any time and refills itself on demand.
    /// </summary>
    public static string Thumbs(IWebHostEnvironment env) => Path.Combine(DataDir(env), "thumbs");

    /// <summary>What the AI assistant keeps of an attachment for the model: the scaled-down copy of a picture
    /// (<c>ai-files/{conversation}/…</c>). Not served, not in the backup; deleted with its conversation. The picture
    /// itself is in the media library like any other upload.</summary>
    public static string AiFiles(IWebHostEnvironment env) => Path.Combine(DataDir(env), "ai-files");

    /// <summary>Root of all plugin asset folders, served publicly under <c>/plugin-assets</c>.</summary>
    public static string PluginAssets(IWebHostEnvironment env) => Path.Combine(DataDir(env), "plugin-assets");

    /// <summary>Root of the plugins' PRIVATE files (<c>plugin-files/{key}/…</c>): product downloads, invoices. Not under the web root and not
    /// served by address — only a handler of the plugin hands a file out. Carried by the backup.</summary>
    public static string PluginFiles(IWebHostEnvironment env) => Path.Combine(DataDir(env), "plugin-files");

    /// <summary>One plugin's private file folder. The key is a trusted slug.</summary>
    public static string PluginFilesDir(IWebHostEnvironment env, string key) => Path.Combine(PluginFiles(env), SafeSegment(key));

    /// <summary>Root of the native plugins' files: <c>plugins/{key}/{version}/src/…</c>. A new version gets a folder
    /// NEXT TO the old one and is never written over it, so an update that fails to load leaves the running
    /// version's files untouched.</summary>
    public static string Plugins(IWebHostEnvironment env) => Path.Combine(DataDir(env), "plugins");

    /// <summary>The <c>src/</c> folder of one version of a native plugin. Key and version become path segments, so
    /// anything outside <c>[A-Za-z0-9._-]</c> is replaced — a version label can never walk out of the folder.</summary>
    public static string PluginSrc(IWebHostEnvironment env, string key, string version) =>
        Path.Combine(Plugins(env), SafeSegment(key), SafeSegment(version), "src");

    /// <summary>One path segment from free text: letters, digits, dot, dash, underscore; never empty, never "..".</summary>
    public static string SafeSegment(string? value)
    {
        var s = new string((value ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray()).Trim('.');
        return s.Length == 0 ? "_" : s;
    }

    /// <summary>One plugin's own asset folder (<c>plugin-assets/{key}</c>). The key is a trusted slug.</summary>
    public static string PluginAssetDir(IWebHostEnvironment env, string key) =>
        Path.Combine(PluginAssets(env), key);
}
