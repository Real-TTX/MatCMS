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

    /// <summary>Plugin asset files (JS/CSS libraries etc.), served publicly under <c>/plugin-assets</c>.</summary>
    public static string PluginAssets(IWebHostEnvironment env) => Path.Combine(DataDir(env), "plugin-assets");
}
