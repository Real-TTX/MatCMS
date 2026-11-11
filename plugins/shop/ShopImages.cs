using System.Globalization;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// How the shop shows its pictures smaller than they are: a stored smaller copy (<see cref="IPluginSite.ImageUrl"/>) and the point a cropping tile keeps
/// in view. The focus is, in this order: the product's own (product form), the picture's focal point from the media library, the shop's default
/// (Einstellungen → Bilder). Links, the lightbox, share tags and structured data keep the ORIGINAL address.
/// </summary>
internal static partial class ShopImages
{
    /// <summary>A tile in a list or cart line (≤ 170 px on screen).</summary>
    public const int Small = 320;
    /// <summary>A card in a grid, a category tile, a logo.</summary>
    public const int Card = 640;
    /// <summary>A product's main picture on its page.</summary>
    public const int Large = 1200;
    /// <summary>A header picture across the page.</summary>
    public const int Wide = 1920;

    /// <summary>The nine positions the default can take (value = CSS position, label = string key).</summary>
    public static readonly (string Value, string Label)[] Positions =
    {
        ("50% 50%", "set.focus.center"), ("50% 0%", "set.focus.top"), ("50% 100%", "set.focus.bottom"),
        ("0% 50%", "set.focus.left"), ("100% 50%", "set.focus.right"),
        ("0% 0%", "set.focus.topLeft"), ("100% 0%", "set.focus.topRight"), ("0% 100%", "set.focus.bottomLeft"), ("100% 100%", "set.focus.bottomRight"),
    };

    [GeneratedRegex(@"^(\d{1,3}(?:\.\d+)?)%\s+(\d{1,3}(?:\.\d+)?)%$")]
    private static partial Regex FocusRx();

    /// <summary>A CSS position "x% y%" (whole numbers 0–100) or "" — the value lands in a style attribute and is never anything else.</summary>
    public static string Clean(string? v)
    {
        var m = FocusRx().Match((v ?? "").Trim());
        if (!m.Success) return "";
        static string P(string s) => Math.Clamp((int)Math.Round(double.Parse(s, CultureInfo.InvariantCulture)), 0, 100).ToString(CultureInfo.InvariantCulture);
        return P(m.Groups[1].Value) + "% " + P(m.Groups[2].Value) + "%";
    }

    /// <summary>The focus of a picture: <paramref name="own"/> (a product's), else the media library's focal point, else the shop's default.</summary>
    public static string Focus(IPluginContext ctx, ShopConfig cfg, string? url, string? own = null)
    {
        if (Clean(own) is { Length: > 0 } o) return o;
        var u = ShopPublic.SafeUrl(url ?? "");
        if (u.Length > 0 && Clean(ctx.Site.ImageFocus(u)) is { Length: > 0 } m) return m;
        return cfg.ImageFocus;
    }

    /// <summary>The address of a smaller stored copy for showing the picture at about <paramref name="width"/> pixels ("" when the address is not usable).</summary>
    public static string Url(IPluginContext ctx, string? url, int width)
    {
        var u = ShopPublic.SafeUrl(url ?? "");
        return u.Length == 0 ? "" : ShopPublic.SafeUrl(ctx.Site.ImageUrl(u, width));
    }

    /// <summary><c> style="background-image:…;background-position:…"</c> for a cropping tile, or "" without a picture.</summary>
    public static string Style(IPluginContext ctx, ShopConfig cfg, string? url, int width, string? own = null)
    {
        var src = Url(ctx, url, width);
        if (src.Length == 0) return "";
        return $" style=\"background-image:url('{System.Net.WebUtility.HtmlEncode(src)}');background-position:{Focus(ctx, cfg, url, own)}\"";
    }

    /// <summary>The tile style of a product's main picture (its own focus first).</summary>
    public static string Style(IPluginContext ctx, ShopConfig cfg, Product p, int width) => Style(ctx, cfg, p.Image, width, p.ImageFocus);
}
