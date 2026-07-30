using System.Net;
using MatCMS.Content;

namespace MatCMS.Services;

/// <summary>
/// Renders the public maintenance page (served site-wide when maintenance mode is on). The layout is
/// the active template's <c>maintenance</c> part — the built-in default, or an admin override edited as
/// the "maintenance.html" template file. All theme colours/fonts and the editable text are supplied as
/// <c>{{tokens}}</c>, so the standard page automatically matches the site's colours while the title/
/// message stay editable in Settings → Wartung.
/// </summary>
public static class MaintenancePage
{
    public static string Render(SiteContext site, Localizer t)
    {
        var tpl = site.ActiveTemplate;

        // --- Colours (mirror the public _Layout resolution so the page matches the live site) ---
        var accent = ColorOr(tpl.AccentColor, "#de7e11");
        var bg = ColorOr(tpl.BackgroundColor, "#ffffff");
        var ink = ColorOr(tpl.TextColor, "#1a1a1a");
        var heading = ColorOr(tpl.HeadingColor, "#010101");
        var accentDark = Darken(accent, 0.85);
        var accentSoft = Mix(accent, bg, 0.14);   // ~14% accent over the background → soft tint
        var hairline = Mix(ink, bg, 0.12);         // faint border

        var headFont = string.IsNullOrWhiteSpace(tpl.HeadingFont) ? "Geologica" : tpl.HeadingFont.Trim();
        var bodyFont = string.IsNullOrWhiteSpace(tpl.BodyFont) ? "Inter" : tpl.BodyFont.Trim();
        var fontsHref = FontsHref(headFont, bodyFont);

        // --- Text (admin values, else localized defaults) ---
        var title = FirstNonEmpty(site.MaintenanceTitle, t["maintenance.defaultTitle"]);
        var message = FirstNonEmpty(site.MaintenanceMessage, t["maintenance.defaultMessage"]);
        var badge = t["maintenance.badge"];

        // --- Logo / favicon ---
        var logoUrl = SafeUrl(site.LogoUrl);
        var faviconUrl = SafeUrl(site.FaviconUrl);
        var logoTag = logoUrl.Length > 0
            ? $"<img class=\"m-logo\" src=\"{Enc(logoUrl)}\" alt=\"{Enc(site.SiteName)}\" />"
            : "";
        var faviconTag = faviconUrl.Length > 0
            ? $"<link rel=\"icon\" href=\"{Enc(faviconUrl)}\" />"
            : "";

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lang"] = Enc(site.CurrentLocale),
            ["site_name"] = Enc(site.SiteName),
            ["favicon"] = faviconTag,
            ["logo"] = logoTag,
            ["maint_badge"] = Enc(badge),
            ["maint_title"] = Enc(title),
            ["maint_message"] = Enc(message),
            ["year"] = DateTime.UtcNow.Year.ToString(),
            ["accent"] = accent,
            ["accent_dark"] = accentDark,
            ["accent_soft"] = accentSoft,
            ["bg"] = bg,
            ["ink"] = ink,
            ["heading_color"] = heading,
            ["hairline"] = hairline,
            ["heading_font"] = headFont,
            ["body_font"] = bodyFont,
            ["fonts_href"] = Enc(fontsHref),
        };

        var part = TemplateSchema.EffectivePart(tpl, TemplateSchema.PartMaintenance);
        return TemplateSchema.RenderTokens(part, tokens);
    }

    // --- helpers -----------------------------------------------------------

    private static string FirstNonEmpty(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a!.Trim() : (b ?? "").Trim();

    private static string ColorOr(string? v, string fallback) =>
        string.IsNullOrWhiteSpace(v) ? fallback : v!.Trim();

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>Only allow root-relative or http(s) URLs, and none carrying quote/angle characters.</summary>
    private static string SafeUrl(string? s) =>
        !string.IsNullOrWhiteSpace(s) && (s.StartsWith('/') || s.StartsWith("http"))
            && s.IndexOfAny(new[] { '"', '\'', '<', '>' }) < 0 ? s!.Trim() : "";

    private static string FontsHref(string headFont, string bodyFont)
    {
        string Q(string family) => Uri.EscapeDataString(family.Trim()).Replace("%20", "+") + ":wght@400;500;600;700";
        var families = new List<string> { headFont };
        if (!string.Equals(bodyFont, headFont, StringComparison.OrdinalIgnoreCase))
            families.Add(bodyFont);
        return "https://fonts.googleapis.com/css2?" + string.Join("&", families.Select(f => "family=" + Q(f))) + "&display=swap";
    }

    private static (int r, int g, int b)? ParseHex(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length != 6) return null;
        var st = System.Globalization.NumberStyles.HexNumber;
        if (int.TryParse(h.AsSpan(0, 2), st, null, out var r) &&
            int.TryParse(h.AsSpan(2, 2), st, null, out var g) &&
            int.TryParse(h.AsSpan(4, 2), st, null, out var b))
            return (r, g, b);
        return null;
    }

    /// <summary>Multiply each channel by <paramref name="factor"/> (0..1) → a darker shade.</summary>
    private static string Darken(string hex, double factor)
    {
        var c = ParseHex(hex);
        if (c is null) return hex;
        var (r, g, b) = c.Value;
        return $"#{(int)(r * factor):x2}{(int)(g * factor):x2}{(int)(b * factor):x2}";
    }

    /// <summary>Blend <paramref name="a"/> over <paramref name="b"/> with weight <paramref name="ta"/>
    /// (0..1 fraction of a). Precomputed here so the page needs no CSS color-mix support.</summary>
    private static string Mix(string a, string b, double ta)
    {
        var ca = ParseHex(a); var cb = ParseHex(b);
        if (ca is null || cb is null) return a;
        int Ch(int x, int y) => (int)Math.Round(x * ta + y * (1 - ta));
        return $"#{Ch(ca.Value.r, cb.Value.r):x2}{Ch(ca.Value.g, cb.Value.g):x2}{Ch(ca.Value.b, cb.Value.b):x2}";
    }
}
