using MatCMS.Plugin;

namespace MatCmsPlugin;

/// <summary>
/// The plugin. Exactly one public class per assembly implements <see cref="IPlugin"/>; the host finds it by that.
/// <para><see cref="Configure"/> only REGISTERS — no database, no network. The host lists, enables, disables and
/// unloads the plugin from what it registered here.</para>
/// </summary>
public sealed class PluginEntry : IPlugin
{
    public void Configure(IPluginBuilder plugin)
    {
        // The key comes from the manifest (meta.json -> plugin.json), so the URL prefix and the identity cannot drift apart.
        var key = plugin.Info.Key;

        // Public page at /{key}, inside the site layout (and /en/{key} etc. in every active language).
        plugin.Route("/" + key, ctx => PluginResult.Page(
            "<h1>" + System.Net.WebUtility.HtmlEncode(plugin.Info.Name) + "</h1><p>Das Plugin läuft.</p>",
            plugin.Info.Name));

        // A JSON endpoint. The answer is serialised with the SOURCE-GENERATED context (Json.cs): reflection-based
        // System.Text.Json over a plugin's own types would keep the plugin in memory after it is unloaded.
        plugin.Route("/" + key + "/api/hello", ctx => PluginResult.Text(
            System.Text.Json.JsonSerializer.Serialize(new Hello(key, "Hallo aus " + plugin.Info.Name), PluginJson.Default.Hello),
            "application/json; charset=utf-8"));

        // One sidebar entry with its pages (more pages become tabs). The screen is built from the host's components, so it
        // looks like the rest of the back office — dark mode and mobile included — without any CSS of its own.
        plugin.Admin.Section(plugin.Info.Name, "ti-puzzle", AdminGroup.Plugins, 100)
            .Page("home", "Übersicht", ctx => PluginResult.Page(
                Ui.Card("Willkommen", Ui.Text("Dieses Plugin wurde aus der MatCMS-Vorlage erzeugt."),
                    Ui.Muted("Öffentliche Seite: /" + key)),
                plugin.Info.Name));
    }
}
