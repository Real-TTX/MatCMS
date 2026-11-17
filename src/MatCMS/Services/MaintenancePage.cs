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
    /// <summary>The PIN form (<see cref="MaintenanceAccess"/>): folded away behind one line so the page looks as before to
    /// everybody else, open when it carries an answer. Posts to the address it is shown on (no action), so the reply comes
    /// in that address's language. <paramref name="disabled"/> = the admin's preview, where there is nothing to post to.</summary>
    public static string UnlockForm(Localizer t, string tokenField, string token, string? notice, bool disabled = false)
    {
        var off = disabled ? " disabled" : "";
        var hidden = tokenField.Length == 0 ? "" : $"<input type=\"hidden\" name=\"{Enc(tokenField)}\" value=\"{Enc(token)}\" />";
        var msg = string.IsNullOrEmpty(notice) ? "" : $"<p class=\"m-unlock-msg\" role=\"alert\">{Enc(notice)}</p>";
        return $"""
            <details class="m-unlock"{(string.IsNullOrEmpty(notice) ? "" : " open")}>
              <summary>{Enc(t["maintenance.unlock.summary"])}</summary>
              <form method="post" autocomplete="off">
                {hidden}
                <label for="m-unlock-pin">{Enc(t["maintenance.unlock.label"])}</label>
                <div class="m-unlock-row">
                  <input id="m-unlock-pin" name="{MaintenanceAccess.PinField}" type="password" required maxlength="{MaintenanceAccess.MaxLength}" autocomplete="current-password"{off} />
                  <button type="submit"{off}>{Enc(t["maintenance.unlock.submit"])}</button>
                </div>
                {msg}
              </form>
            </details>
            """;
    }

    /// <summary>The form's own look, with the theme's colours written in — a template's own maintenance.html need not
    /// define the default page's CSS variables.</summary>
    private static string UnlockCss(string accent, string ink, string bg, string hairline) => $$"""
        <style>
          .m-unlock{margin:26px auto 0;max-width:340px;text-align:left;font-size:14px}
          .m-unlock summary{cursor:pointer;text-align:center;opacity:.6;list-style:none}
          .m-unlock summary::-webkit-details-marker{display:none}
          .m-unlock summary:hover,.m-unlock[open] summary{opacity:1}
          .m-unlock form{margin-top:12px}
          .m-unlock label{display:block;font-weight:600;margin-bottom:6px;color:{{ink}}}
          .m-unlock-row{display:flex;gap:8px}
          .m-unlock input{flex:1;min-width:0;font:inherit;font-size:16px;padding:9px 12px;border:1px solid {{hairline}};
            border-radius:10px;background:{{bg}};color:{{ink}}}
          .m-unlock button{font:inherit;font-weight:600;padding:9px 16px;border:0;border-radius:10px;cursor:pointer;
            background:{{accent}};color:#fff}
          .m-unlock button[disabled]{opacity:.5;cursor:default}
          .m-unlock-msg{margin:10px 0 0;font-size:14px;opacity:1;color:#b42318}
        </style>
        """;

    public static string Render(SiteContext site, Localizer t, string unlockForm = "")
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
            ["maint_unlock"] = unlockForm.Length == 0 ? "" : UnlockCss(accent, ink, bg, hairline) + unlockForm,
        };

        var part = TemplateSchema.EffectivePart(tpl, TemplateSchema.PartMaintenance);
        // A template's own maintenance.html from before the form existed has no {{maint_unlock}}: the form goes in before
        // </body> there, or the PIN the operator handed out could not be entered on that site.
        if (unlockForm.Length > 0 && !part.Contains("{{maint_unlock}}", StringComparison.Ordinal))
        {
            var end = part.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            const string box = "<div style=\"position:fixed;left:0;right:0;bottom:16px;display:flex;justify-content:center;padding:0 16px\"><div style=\"max-width:360px;width:100%\">{{maint_unlock}}</div></div>";
            part = end >= 0 ? part.Insert(end, box) : part + box;
        }
        return MatCMS.Shared.Web.LocalFonts.Rewrite(TemplateSchema.RenderTokens(part, tokens));
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
        return "/fonts/css2?" + string.Join("&", families.Select(f => "family=" + Q(f))) + "&display=swap";
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
