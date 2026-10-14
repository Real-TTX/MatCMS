namespace MatCMS.Plugin;

/// <summary>
/// The entry point of a NATIVE plugin: exactly one public, non-abstract class per plugin assembly
/// implements this. A SCRIPT plugin has no such class — its script receives the same
/// <see cref="IPluginBuilder"/> as the global variable <c>Plugin</c>.
/// <para><see cref="Configure"/> only REGISTERS. It runs once per load, before any request, and must
/// not touch the database or the network: the host lists, enables, disables and unloads a plugin from
/// what it registered, and that only works if registering has no side effects.</para>
/// </summary>
public interface IPlugin
{
    /// <summary>Declares everything this plugin contributes.</summary>
    void Configure(IPluginBuilder plugin);
}

/// <summary>Identity of a plugin, read from its manifest (<c>plugin.json</c>).</summary>
/// <param name="Key">Stable slug — the identity; also names the asset folder and the URL prefix.</param>
/// <param name="Name">Display name.</param>
/// <param name="Version">The plugin's own version.</param>
/// <param name="Description">One line for the plugin list.</param>
/// <param name="Api">The API major version the plugin was built against.</param>
public sealed record PluginInfo(string Key, string Name, string Version, string Description, int Api);
