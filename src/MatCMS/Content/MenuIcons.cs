using System.Text.RegularExpressions;

namespace MatCMS.Content;

/// <summary>
/// Menu-item icons are rendered with the self-hosted Tabler Icons webfont. A stored icon value is
/// simply a Tabler icon name (e.g. "download", "world", "brand-whatsapp"); the searchable picker in
/// the admin lets the user pick one of ~4900 icons. Legacy values from the old curated set are mapped
/// to their Tabler equivalent so existing menu items keep their icon.
/// </summary>
public static partial class MenuIcons
{
    // Old curated keys whose name differs from the Tabler icon name.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["whatsapp"] = "brand-whatsapp",
        ["mappin"] = "map-pin",
        ["chat"] = "message-circle",
        ["info"] = "info-circle",
        ["cart"] = "shopping-cart",
        ["globe"] = "world",
        ["document"] = "file-text",
        ["tools"] = "tool",
        ["help"] = "help-circle",
        // mail, phone, calendar, clock, user, download, star, link, home, lock, key, settings,
        // cloud, search, shield, folder are already valid Tabler names and pass through unchanged.
    };

    [GeneratedRegex(@"^[a-z][a-z0-9-]*$")]
    private static partial Regex NameRx();

    /// <summary>A stored icon value is valid when it looks like a Tabler icon name.</summary>
    public static bool IsValid(string? key) => !string.IsNullOrWhiteSpace(key) && NameRx().IsMatch(key.Trim());

    /// <summary>Maps a stored icon value to its Tabler icon name (applying legacy aliases).</summary>
    public static string Resolve(string? key)
    {
        var k = (key ?? "").Trim();
        return Aliases.TryGetValue(k, out var t) ? t : k;
    }

    /// <summary>Renders the icon as a Tabler webfont element, or empty markup when there is no icon.</summary>
    public static string IconMarkup(string? key)
    {
        var name = Resolve(key);
        return string.IsNullOrEmpty(name) ? "" : $"<i class=\"ti ti-{name}\" aria-hidden=\"true\"></i>";
    }
}
