using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// Parcel tracking: an order carries a carrier (a key from <see cref="All"/>, or "custom") and the tracking number, and the number becomes a link to the carrier's tracking page.
/// The address is only ever built from a fixed https template (the carrier's own, or the operator's one for "custom") with the number escaped into it — nothing a visitor sends is
/// ever part of it, and a number that is itself an https address (some carriers send a full link) is used as it is.
/// </summary>
internal static class ShopTracking
{
    /// <summary>The key of the operator's own template (setting <c>track.url</c>).</summary>
    public const string Custom = "custom";

    internal sealed record Carrier(string Key, string Name, string Url);

    public static readonly IReadOnlyList<Carrier> All = new Carrier[]
    {
        new("dhl", "DHL", "https://www.dhl.de/de/privatkunden/pakete-empfangen/verfolgen.html?piececode={n}"),
        new("dhl-express", "DHL Express", "https://www.dhl.com/de-de/home/tracking/tracking-express.html?submit=1&tracking-id={n}"),
        new("dpd", "DPD", "https://tracking.dpd.de/parcelstatus?query={n}&locale=de_DE"),
        new("hermes", "Hermes", "https://www.myhermes.de/empfangen/sendungsverfolgung/sendungsinformation/#{n}"),
        new("gls", "GLS", "https://gls-group.com/DE/de/paketverfolgung?match={n}"),
        new("ups", "UPS", "https://www.ups.com/track?loc=de_DE&tracknum={n}"),
        new("fedex", "FedEx", "https://www.fedex.com/fedextrack/?trknbr={n}"),
        new("post-at", "Österreichische Post", "https://www.post.at/sv/sendungsdetails?snr={n}"),
        new("post-ch", "Die Post (Schweiz)", "https://service.post.ch/ekp-web/ui/entry/search/{n}")
    };

    /// <summary>A key that is known, or "" — a form field is never trusted to be one of ours.</summary>
    public static string Clean(string? key)
    {
        var k = (key ?? "").Trim().ToLowerInvariant();
        return k == Custom || All.Any(c => c.Key == k) ? k : "";
    }

    /// <summary>The carrier's name for a customer ("" when none was chosen or it is the operator's own link).</summary>
    public static string NameOf(string key) => All.FirstOrDefault(c => c.Key == key)?.Name ?? "";

    /// <summary>A custom template is usable when it is https and says where the number goes.</summary>
    public static bool ValidTemplate(string? t) => t is not null && t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && t.Contains("{n}", StringComparison.Ordinal) && t.Length <= 500;

    /// <summary>The link to follow the parcel, or "" when there is nothing to link to (no number, no known carrier, no usable template).</summary>
    public static string Url(string carrier, string number, string customTemplate = "")
    {
        var n = string.Concat((number ?? "").Where(c => !char.IsWhiteSpace(c)));
        if (n.Length == 0) return "";
        if (n.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return Uri.TryCreate(n, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u.AbsoluteUri : "";
        var template = carrier == Custom ? (ValidTemplate(customTemplate) ? customTemplate : "") : All.FirstOrDefault(c => c.Key == carrier)?.Url ?? "";
        return template.Length == 0 ? "" : template.Replace("{n}", Uri.EscapeDataString(n), StringComparison.Ordinal);
    }

    /// <summary>The link for an order, with the operator's default carrier standing in when the order has none of its own.</summary>
    public static string UrlOf(ShopConfig cfg, Order o) => Url(o.Carrier.Length > 0 ? o.Carrier : cfg.Carrier, o.Tracking, cfg.TrackingUrl);

    public static string NameOf(ShopConfig cfg, Order o) => NameOf(o.Carrier.Length > 0 ? o.Carrier : cfg.Carrier);
}
