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

    /// <summary>Largest manifest a reader will parse. A manifest is metadata; anything this big is
    /// not one.</summary>
    public const long MaxManifestBytes = 1 * 1024 * 1024;

    /// <summary>Zip-bomb guards: per asset, for all assets combined, and on the file count.</summary>
    public const long MaxAssetBytes = 5 * 1024 * 1024;
    public const long MaxTotalBytes = 25 * 1024 * 1024;
    public const int MaxAssetFiles = 200;

    /// <summary>Asset file types a bundle may carry. SVG is excluded deliberately — served from the
    /// site's own origin it is a stored-XSS vector.</summary>
    public static readonly string[] AllowedAssetExt =
        [".js", ".mjs", ".css", ".json", ".map", ".woff", ".woff2", ".ttf", ".eot", ".png", ".jpg", ".jpeg", ".gif", ".webp"];
}
