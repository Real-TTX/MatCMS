using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Plugins;

/// <summary>
/// Puts a plugin's menu elements into the site's menus ONCE: the shop's cart and sign-in into the icon strip, its search box, "Shop" into the main menu — as ordinary menu items the editor
/// sees in Admin → Menü and may move, rename or delete. A marker per element (<c>menu.seeded.{plugin}.{element}</c>) makes deleting stick: the host never puts back what an editor took out,
/// and an element that is already in some menu (a restore) is not added a second time. Items are added for every content language the site has.
/// </summary>
internal static class PluginMenuElements
{
    public static async Task SeedAsync(AppDbContext db, IEnumerable<PluginRegistry.MenuElementEntry> elements, Func<string, string, string> label, CancellationToken ct = default)
    {
        var languages = (await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKeys.Languages, ct))?.Value;
        var locales = Localizer.ParseActive(languages);
        foreach (var e in elements)
        {
            var menu = e.Spec.DefaultMenu;
            if (string.IsNullOrEmpty(menu)) continue;
            var marker = "menu.seeded." + e.Key;
            if (await db.SiteSettings.AnyAsync(s => s.Key == marker, ct)) continue;
            // The menu does not exist (yet): try again at the next load rather than lose the element for good.
            if (!await db.Menus.AnyAsync(m => m.Key == menu, ct)) continue;
            var url = "plugin:" + e.Key;
            if (!await db.MenuItems.AnyAsync(m => m.Url == url, ct))
                foreach (var locale in locales)
                {
                    var max = await db.MenuItems.Where(m => m.Menu == menu && m.Locale == locale).Select(m => (int?)m.SortOrder).MaxAsync(ct) ?? -1;
                    db.MenuItems.Add(new MenuItem { Menu = menu, Locale = locale, Label = label(locale, e.Spec.Label), Url = url, SortOrder = max + 1 });
                    await db.SaveChangesAsync(ct);          // the next element finds this one when it looks for the end of the menu
                }
            db.SiteSettings.Add(new SiteSetting { Key = marker, Value = "1" });
            await db.SaveChangesAsync(ct);
        }
    }
}
