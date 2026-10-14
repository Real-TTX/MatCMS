using MatCMS.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Plugins;

/// <summary>
/// Removing a plugin for good. <b>Deactivating never deletes data</b> — only this does, and only after the admin was told what
/// goes with it: the plugin's row, its files, and the tables it created (a table a built-in plugin merely adopted belongs to the
/// CMS and stays). An update or a restore never calls this.
/// </summary>
public static class PluginUninstall
{
    /// <summary>The text of the confirmation for deleting a plugin: the usual question plus, when the plugin owns data, what is lost.</summary>
    public static async Task<string> ConfirmTextAsync(AppDbContext db, PluginDataService data, Localizer t, string pluginKey, CancellationToken ct = default)
    {
        var text = t["plugins.confirmDelete"].ToString();
        var own = (await data.DescribeAsync(db, pluginKey, ct)).Where(x => !x.Adopted).ToList();
        if (own.Count == 0) return text;
        return text + " " + t["plugins.confirmDeleteData", own.Count, own.Sum(x => x.Rows)];
    }

    /// <summary>Deletes the plugin: row, loaded state, files and its own tables.</summary>
    public static async Task DeleteAsync(AppDbContext db, PluginRunner runner, PluginDataService data, IWebHostEnvironment env, Models.Plugin plugin)
    {
        var key = plugin.Key;
        db.Plugins.Remove(plugin);
        await db.SaveChangesAsync();
        await runner.UnloadAsync(key);
        PluginPackager.RemoveFiles(env, key);
        await data.DropAsync(db, key);
    }
}
