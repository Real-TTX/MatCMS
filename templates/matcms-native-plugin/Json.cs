using System.Text.Json.Serialization;

namespace MatCmsPlugin;

/// <summary>A type the plugin serialises. Every type that goes through System.Text.Json is listed in
/// <see cref="PluginJson"/> — see below.</summary>
public sealed record Hello(string Plugin, string Message);

/// <summary>
/// The plugin's JSON, SOURCE-GENERATED. Reflection-based System.Text.Json over a plugin's own types pins the plugin's
/// load context for good, so the plugin could never be freed after unloading (docs/plugin-system-v2.md, section 7,
/// rule 1). Either list the types here and serialise with <c>PluginJson.Default.&lt;Type&gt;</c>, or work on
/// <c>JsonNode</c> and use no plugin type at all.
/// </summary>
[JsonSerializable(typeof(Hello))]
internal partial class PluginJson : JsonSerializerContext { }
