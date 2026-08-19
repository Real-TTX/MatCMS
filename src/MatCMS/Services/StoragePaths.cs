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

    /// <summary>Root of all plugin asset folders, served publicly under <c>/plugin-assets</c>.</summary>
    public static string PluginAssets(IWebHostEnvironment env) => Path.Combine(DataDir(env), "plugin-assets");

    /// <summary>One plugin's own asset folder (<c>plugin-assets/{key}</c>). The key is a trusted slug.</summary>
    public static string PluginAssetDir(IWebHostEnvironment env, string key) =>
        Path.Combine(PluginAssets(env), key);
}
