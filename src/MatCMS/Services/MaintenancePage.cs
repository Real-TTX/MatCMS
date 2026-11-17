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
    public static string UnlockForm(Localizer t, string tokenField, string token, string? notice, bool disabled = false, int digits = 0)
    {
        var off = disabled ? " disabled" : "";
        var hidden = tokenField.Length == 0 ? "" : $"<input type=\"hidden\" name=\"{Enc(tokenField)}\" value=\"{Enc(token)}\" />";
        var msg = string.IsNullOrEmpty(notice) ? "" : $"<p class=\"m-unlock-msg\" role=\"alert\">{Enc(notice)}</p>";
        var open = string.IsNullOrEmpty(notice) ? "" : " open";
        if (digits > 0)
        {
            // A PIN: one box per digit (the script below draws them). The real field stays the one that is posted; without
            // the script it is a plain number field. "digit {0} of {1}" names each box for a screen reader.
            var digitLabel = Enc(t["maintenance.unlock.digit"]);
            var pattern = "[0-9]{" + digits + "}";   // a raw $""" string cannot carry literal braces
            return $"""
                <details class="m-unlock m-unlock--pin"{open}>
                  <summary>{Enc(t["maintenance.unlock.summary"])}</summary>
                  <form method="post" autocomplete="off">
                    {hidden}
                    <label for="m-unlock-pin">{Enc(t["maintenance.unlock.label"])}</label>
                    <div class="m-otp{(string.IsNullOrEmpty(notice) ? "" : " is-invalid")}" data-digits="{digits}" data-label="{digitLabel}">
                      <input id="m-unlock-pin" class="m-otp-input" name="{MaintenanceAccess.PinField}" type="text" inputmode="numeric" pattern="{pattern}"
                             maxlength="{digits}" required autocomplete="one-time-code"{off} />
                    </div>
                    <button type="submit"{off}>{Enc(t["maintenance.unlock.submit"])}</button>
                    {msg}
                  </form>
                </details>
                {OtpScript}
                """;
        }
        return $"""
            <details class="m-unlock"{open}>
              <summary>{Enc(t["maintenance.unlock.summaryPassword"])}</summary>
              <form method="post" autocomplete="off">
                {hidden}
                <label for="m-unlock-pin">{Enc(t["maintenance.unlock.labelPassword"])}</label>
                <div class="m-unlock-row">
                  <input id="m-unlock-pin" name="{MaintenanceAccess.PinField}" type="password" required maxlength="{MaintenanceAccess.MaxLength}" autocomplete="current-password"{off} />
                  <button type="submit"{off}>{Enc(t["maintenance.unlock.submit"])}</button>
                </div>
                {msg}
              </form>
            </details>
            """;
    }

    /// <summary>The code boxes, in the manner of MatUi's code input (FORM-19) — the public site never loads matui.js, so the
    /// maintenance page carries its own few lines: a digit per box, typing moves on, Backspace goes back, a pasted or
    /// autofilled code is spread over the boxes, and a full code sends the form. The real field keeps the joined value.</summary>
    private const string OtpScript = """
        <script>
        (function () {
          document.querySelectorAll('.m-otp[data-digits]').forEach(function (wrap) {
            var input = wrap.querySelector('.m-otp-input'), n = parseInt(wrap.getAttribute('data-digits'), 10);
            if (!input || !(n > 0)) return;
            var form = input.form, label = wrap.getAttribute('data-label') || '', row = document.createElement('div'), boxes = [];
            row.className = 'm-otp-boxes';
            for (var i = 0; i < n; i++) {
              if (i > 0 && n > 4 && i === n / 2) { var sep = document.createElement('span'); sep.className = 'm-otp-sep' + (n === 8 ? ' m-otp-sep--dash' : ''); row.appendChild(sep); }
              var b = document.createElement('input');
              b.type = 'text'; b.className = 'm-otp-box'; b.inputMode = 'numeric'; b.autocomplete = i === 0 ? 'one-time-code' : 'off';
              b.setAttribute('aria-label', label.replace('{0}', i + 1).replace('{1}', n));
              b.disabled = input.disabled;
              row.appendChild(b); boxes.push(b);
            }
            wrap.appendChild(row); wrap.classList.add('is-ready');
            input.tabIndex = -1; input.setAttribute('aria-hidden', 'true');
            function sync(byUser) {
              var v = boxes.map(function (x) { return x.value; }).join('');
              input.value = v;
              if (byUser) wrap.classList.remove('is-invalid');
              if (byUser && v.length === n && form) { if (form.requestSubmit) form.requestSubmit(); else form.submit(); }
            }
            function focus(i) { var b = boxes[Math.max(0, Math.min(n - 1, i))]; b.focus(); b.select(); }
            function fill(text, start) {
              var d = (text || '').replace(/\D/g, '');
              if (!d) return;
              if (d.length >= n) start = 0;
              for (var k = 0; k < d.length && start + k < n; k++) boxes[start + k].value = d[k];
              focus(start + d.length);
              sync(true);
            }
            boxes.forEach(function (b, i) {
              b.addEventListener('input', function (e) {
                var typed = e.data && e.data.length === 1 ? e.data : null;
                if (typed && /\d/.test(typed)) { b.value = typed; if (i < n - 1) focus(i + 1); sync(true); return; }
                var d = b.value.replace(/\D/g, '');
                b.value = '';
                if (d) fill(d, i); else sync(true);
              });
              b.addEventListener('keydown', function (e) {
                if (e.key === 'Backspace') {
                  e.preventDefault();
                  if (b.value) b.value = ''; else if (i > 0) { boxes[i - 1].value = ''; focus(i - 1); }
                  sync(true);
                } else if (e.key === 'ArrowLeft') { e.preventDefault(); focus(i - 1); }
                else if (e.key === 'ArrowRight') { e.preventDefault(); focus(i + 1); }
                else if (e.key === 'Home') { e.preventDefault(); focus(0); }
                else if (e.key === 'End') { e.preventDefault(); focus(n - 1); }
              });
              b.addEventListener('paste', function (e) {
                e.preventDefault();
                fill((e.clipboardData || window.clipboardData).getData('text'), i);
              });
              b.addEventListener('focus', function () {
                // a click past the first empty box lands on it — a code is typed from the left
                var first = boxes.findIndex(function (x) { return !x.value; });
                if (first >= 0 && first < i) { focus(first); return; }
                b.select();
              });
            });
            input.addEventListener('focus', function () { focus(0); });
            var det = wrap.closest('details');
            if (det) {
              det.addEventListener('toggle', function () { if (det.open && !boxes[0].disabled) focus(0); });
              if (det.open && !boxes[0].disabled) focus(0);
            }
          });
        })();
        </script>
        """;

    /// <summary>The form's own look, with the theme's colours written in — a template's own maintenance.html need not
    /// define the default page's CSS variables.</summary>
    private static string UnlockCss(string accent, string ink, string bg, string hairline, string ring, string strong) => $$"""
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
          .m-unlock--pin form{text-align:center}
          .m-unlock--pin label{position:absolute;width:1px;height:1px;margin:-1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap}   /* the summary already says "PIN"; the label stays for a screen reader */
          .m-unlock--pin button{display:block;width:100%;margin-top:12px}
          .m-unlock--pin .m-unlock-msg{text-align:center}
          .m-otp{position:relative;display:block;max-width:100%}
          .m-unlock .m-otp-input{display:block;width:100%;max-width:16rem;margin:0 auto;min-height:52px;text-align:center;
            font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:22px;font-weight:600;letter-spacing:.3em}
          .m-otp.is-ready > .m-otp-input{position:absolute;inset:0;width:100%;max-width:none;height:100%;min-height:0;padding:0;border:0;
            opacity:0;pointer-events:none}
          .m-otp-boxes{display:flex;align-items:center;justify-content:center;gap:8px;max-width:100%}
          .m-unlock .m-otp-box{flex:0 1 44px;width:44px;min-width:0;height:52px;margin:0;padding:0;box-sizing:border-box;
            border:1px solid {{strong}};border-radius:10px;background:{{bg}};color:{{ink}};caret-color:{{accent}};
            font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:22px;font-weight:600;line-height:1;text-align:center;
            transition:border-color .12s ease,box-shadow .12s ease}
          .m-unlock .m-otp-box:hover{border-color:{{accent}}}
          .m-unlock .m-otp-box:focus{border-color:{{accent}};box-shadow:0 0 0 3px {{ring}};outline:none}
          .m-unlock .m-otp-box:disabled{opacity:.5;cursor:default}
          .m-otp.is-invalid .m-otp-box{border-color:#b42318}
          .m-otp-sep{flex:none;align-self:stretch;display:flex;align-items:center;justify-content:center;width:12px}
          .m-otp-sep--dash{width:20px}
          .m-otp-sep--dash::before{content:"";width:8px;height:2px;border-radius:1px;background:{{hairline}}}
          @media (max-width:640px){.m-otp-boxes{gap:4px}.m-otp-sep{width:8px}.m-otp-sep--dash{width:16px} }
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
            ["maint_unlock"] = unlockForm.Length == 0 ? "" : UnlockCss(accent, ink, bg, hairline, Mix(accent, bg, 0.3), Mix(ink, bg, 0.28)) + unlockForm,
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
