using MatCMS.Data;
using MatCMS.Plugin;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Plugins;

/// <summary>One plugin compiled into the CMS.</summary>
/// <param name="Key">Its key (also the key of its row in the plugin list and of its tables).</param>
/// <param name="Name">The name shown under Plugins.</param>
/// <param name="Description">One sentence for the plugin list.</param>
/// <param name="Version">Its version; changes with the image, never by import.</param>
/// <param name="Create">Makes the plugin instance.</param>
/// <param name="EnabledOnNewSite">Whether a site that has never seen the plugin starts it. The blog is on (it is part of what a CMS is).</param>
public sealed record BuiltInPlugin(string Key, string Name, string Description, string Version, Func<IPlugin> Create, bool EnabledOnNewSite = true);

/// <summary>
/// The plugins compiled into the CMS itself — today the blog. They run through the very same API as every other plugin (the same
/// builder, registry and state; they can be switched off and have their own settings and tables) but ship in the image, so a site
/// without a cloud, a store or an import keeps working: <b>MatCMS stays a complete CMS on its own</b>.
/// <para>What sets them apart: they are <i>enabled</i> on a fresh site and the operator can switch them off but not delete,
/// export or overwrite them (a bundle or a cloud rollout with their key is refused — an update comes with the image). Their row in
/// the plugin list is created at startup if missing and never touched beyond name, description and version; the operator's choice
/// (on/off) and configuration are kept.</para>
/// </summary>
public static class BuiltInPlugins
{
    /// <summary>All built-in plugins.</summary>
    public static IReadOnlyList<BuiltInPlugin> All { get; } = new[] { Plugin.Blog.BlogPlugin.Definition };

    /// <summary>
    /// Keys of plugins that USED to ship with the CMS and left the image as native packages of the MatCMS Store (docs/builtin-extraction.md,
    /// Release B): the shop and the AI bot. Their key stays reserved — only the store's signed package takes it (<see cref="PluginPackager"/>),
    /// nobody may pose as "shop" — and a site that still has their old <c>builtin</c> row (the image was updated before the package arrived)
    /// keeps it untouched, data and settings included, and is told under Plugins where the code went (<see cref="IsGone"/>).
    /// </summary>
    public static IReadOnlyList<string> FormerKeys { get; } = new[] { "shop", "bot" };

    /// <summary>The built-in plugin with this key, or null.</summary>
    public static BuiltInPlugin? Find(string? key) => All.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the key belongs to a plugin that ships with the CMS — or once did (<see cref="FormerKeys"/>): such a key is reserved for
    /// the image or the MatCMS Store's signed package.</summary>
    public static bool IsBuiltInKey(string? key) => Find(key) is not null || IsFormerKey(key);

    /// <summary>True when the key belongs to a plugin that left the image (<see cref="FormerKeys"/>).</summary>
    public static bool IsFormerKey(string? key) => FormerKeys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for a <c>builtin</c> row whose code this image no longer carries: it waits for the store's package (a former key) or names a
    /// plugin this version of MatCMS does not know at all.</summary>
    public static bool IsGone(Models.Plugin p) => Models.PluginKind.Normalize(p.Kind) == Models.PluginKind.BuiltIn && Find(p.Key) is null;

    /// <summary>
    /// Makes sure every built-in plugin has its row: created enabled when missing, kept in line with the image otherwise. A script or
    /// native plugin that an operator created under a built-in key is left alone — and the built-in is NOT started in its place
    /// (returned as a conflict, shown in the plugin list), because silently replacing somebody's plugin would be worse than a
    /// missing blog.
    /// </summary>
    public static async Task<IReadOnlyList<string>> EnsureRowsAsync(AppDbContext db, CancellationToken ct = default)
    {
        var conflicts = new List<string>();
        foreach (var b in All)
        {
            var row = await db.Plugins.FirstOrDefaultAsync(p => p.Key == b.Key, ct);
            if (row is null)
            {
                db.Plugins.Add(new Models.Plugin
                {
                    Key = b.Key, Name = b.Name, Description = b.Description, Version = b.Version, Kind = Models.PluginKind.BuiltIn,
                    Enabled = b.EnabledOnNewSite, ConfigJson = "{}", FilesJson = "{}"
                });
            }
            // The MatCMS Store's signed package took this plugin's place (only a signed one can: PluginPackager): it runs as a native
            // plugin now, the built-in stays out of its way — that is the move out of the image, not a conflict.
            else if (Models.PluginKind.Normalize(row.Kind) == Models.PluginKind.Native) continue;
            else if (Models.PluginKind.Normalize(row.Kind) != Models.PluginKind.BuiltIn) conflicts.Add(b.Key);
            else
            {
                row.Name = b.Name; row.Description = b.Description; row.Version = b.Version;
                row.Code = ""; row.FilesJson = "{}"; row.Entry = ""; row.Sha256 = "";
            }
        }
        await db.SaveChangesAsync(ct);
        return conflicts;
    }
}
