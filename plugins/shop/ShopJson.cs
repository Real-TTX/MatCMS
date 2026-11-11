using System.Text.Json.Serialization;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The shop's own types as JSON, source-generated. A native plugin must not run the reflection-based <c>System.Text.Json</c> over its own types:
/// its caches hold them and keep the plugin's load context from ever being released (CLAUDE.md, "The plugin system"). Property names stay as
/// they were (PascalCase, read without regard to case), so addresses stored by the built-in shop read unchanged.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Address))]
[JsonSerializable(typeof(Product))]
[JsonSerializable(typeof(Brand))]
[JsonSerializable(typeof(Category))]
internal sealed partial class ShopJson : JsonSerializerContext
{
}
