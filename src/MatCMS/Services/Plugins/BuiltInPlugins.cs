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
/// <param name="EnabledOnNewSite">Whether a site that has never seen the plugin starts it. The blog is on (it is part of what a CMS is); the shop is off — an
/// operator who wants to sell something switches it on, and a site that does not never carries a shop's pages, routes or tables.</param>
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
    public static IReadOnlyList<BuiltInPlugin> All { get; } = new[] { Plugin.Blog.BlogPlugin.Definition, Plugin.Shop.ShopPlugin.Definition, Plugin.Bot.BotPlugin.Definition };

    /// <summary>The built-in plugin with this key, or null.</summary>
    public static BuiltInPlugin? Find(string? key) => All.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the key belongs to a built-in plugin.</summary>
    public static bool IsBuiltInKey(string? key) => Find(key) is not null;

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
