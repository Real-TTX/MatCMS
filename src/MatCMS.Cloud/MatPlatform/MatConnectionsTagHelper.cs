// MatPlatform 2.4.0 – a copy from MatDesign, do not edit here
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatPlatform;

/// <summary>
/// The Connections area of an app (docs/PLATFORM.md "The Connections area"), the same in every app: what the app uses (and the values entered by hand when no variable
/// sets them), what it offers (the apps bound to it, connect one by hand, renew a key, remove) and what it adds to a shell. Drawn on the server with the classes of
/// MatUi, no script needed. An app puts <c>&lt;mat-connections /&gt;</c> on an admin page (with <c>@addTagHelper *, &lt;its assembly&gt;</c>) and says who may use it
/// (<see cref="MatPlatformOptions.ConnectionsAccess"/>); the forms post to <see cref="MatConnectionEndpoints.FormPath"/>.
/// </summary>
[HtmlTargetElement("mat-connections", TagStructure = TagStructure.WithoutEndTag)]
public sealed class MatConnectionsTagHelper(MatConnections connections, MatPlatformOptions options, IAntiforgery antiforgery, IDataProtectionProvider protection) : TagHelper
{
    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    /// <summary>Where a form comes back to; default: this page.</summary>
    [HtmlAttributeName("return-url")]
    public string? ReturnUrl { get; set; }

    /// <summary>
    /// The language of the area: what the app says (<see cref="MatPlatformOptions.UiLanguage"/>), else the UI culture of the request when it is one of
    /// <see cref="MatPlatformOptions.Languages"/>, else <c>Accept-Language</c>.
    /// </summary>
    public static string LanguageOf(HttpContext http, MatPlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.UiLanguage?.Invoke(http) is { } named)
        {
            return MatPlatformOptions.Languages.Contains(named) ? named : options.DefaultLanguage;
        }

        var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return MatPlatformOptions.Languages.Contains(culture) ? culture : options.LanguageOf(http);
    }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var http = ViewContext.HttpContext;
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-stack");
        output.Attributes.SetAttribute("data-connections", string.Empty);

        if (options.ConnectionsAccess is null || !await options.ConnectionsAccess(http))
        {
            output.SuppressOutput();
            return;
        }

        var language = LanguageOf(http, options);
        var view = await connections.ViewAsync(language, http.RequestAborted);
        var tokens = antiforgery.GetAndStoreTokens(http);
        var back = ReturnUrl ?? (http.Request.PathBase + http.Request.Path + http.Request.QueryString).ToString();
        var html = new Render(language, tokens.FormFieldName, tokens.RequestToken ?? string.Empty, back);

        html.Flash(ReadFlash(http));
        html.Uses(view.Uses);
        html.Offers(view.Offers);
        html.Contributions(view.Contributes);
        output.Content.SetHtmlContent(html.ToString());
    }

    private MatConnectionEndpoints.Flash? ReadFlash(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(MatConnectionEndpoints.FlashCookie, out var cookie) || string.IsNullOrEmpty(cookie))
        {
            return null;
        }

        if (!http.Response.HasStarted)
        {
            http.Response.Cookies.Delete(MatConnectionEndpoints.FlashCookie, new CookieOptions { Path = "/" });
        }

        try
        {
            return JsonSerializer.Deserialize<MatConnectionEndpoints.Flash>(protection.CreateProtector(MatConnectionEndpoints.FlashPurpose).Unprotect(cookie));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The markup, written with the encoder of HTML (every text and value of the app is encoded).</summary>
    private sealed class Render(string language, string tokenField, string token, string back)
    {
        private readonly StringBuilder _html = new();
        private readonly HtmlEncoder _encode = HtmlEncoder.Default;
        private int _form;

        public override string ToString() => _html.ToString();

        private string T(string de, string en) => language == "en" ? en : de;

        private string E(string? text) => _encode.Encode(text ?? string.Empty);

        public void Flash(MatConnectionEndpoints.Flash? flash)
        {
            if (flash is null || string.IsNullOrEmpty(flash.Message))
            {
                return;
            }

            _html.Append("<div class=\"mat-alert ").Append(flash.Error ? "mat-alert--danger" : "mat-alert--success").Append("\" role=\"").Append(flash.Error ? "alert" : "status").Append("\">")
                .Append("<div class=\"mat-alert__body\"><strong>").Append(E(flash.Message)).Append("</strong>");
            if (flash.Values is { Count: > 0 } values)
            {
                _html.Append("<dl class=\"mat-kv\">");
                var n = 0;
                foreach (var (variable, value) in values)
                {
                    var id = "mc-value-" + n++;
                    _html.Append("<dt><code>").Append(E(variable)).Append("</code></dt><dd><code id=\"").Append(id).Append("\">").Append(E(value)).Append("</code> ")
                        .Append("<button type=\"button\" class=\"mat-btn mat-btn--sm\" data-copy=\"#").Append(id).Append("\">").Append(E(T("Kopieren", "Copy"))).Append("</button></dd>");
                }

                _html.Append("</dl>");
            }

            _html.Append("</div></div>");
        }

        public void Uses(IReadOnlyList<MatUseView> uses)
        {
            if (uses.Count == 0)
            {
                return;
            }

            // "enter the values here" only where a form follows: the capabilities of the engines are set with their variables only (MatCapabilitySpec.ByHand)
            Section(T("Nutzt", "Uses"), uses.Any(u => u.ByHand)
                ? T("Was diese App von anderen Apps bekommt. Mattiverse verbindet sie von selbst; ohne Mattiverse trägst du die Werte hier ein oder setzt die Variablen.",
                    "What this app gets from other apps. Mattiverse connects them by itself; without it, enter the values here or set the variables.")
                : T("Was diese App von anderen Apps bekommt. Mattiverse verbindet sie von selbst; ohne Mattiverse setzt du die Variablen.",
                    "What this app gets from other apps. Mattiverse connects them by itself; without it, set the variables."));
            _html.Append("<table class=\"mat-table\"><thead><tr><th>").Append(E(T("Wofür", "For"))).Append("</th><th>").Append(E(T("Verbunden mit", "Connected to")))
                .Append("</th><th>").Append(E(T("Gesetzt", "Set"))).Append("</th></tr></thead><tbody>");
            foreach (var use in uses)
            {
                var target = use.Values.FirstOrDefault(v => v.Type == "url" && v.Value is string { Length: > 0 })?.Value as string
                    ?? use.Values.FirstOrDefault(v => !v.Secret && v.Value is string { Length: > 0 })?.Value as string;
                _html.Append("<tr><td data-label=\"").Append(E(T("Wofür", "For"))).Append("\">").Append(E(use.Label)).Append(" <code>").Append(E(use.Capability)).Append("</code></td>")
                    .Append("<td data-label=\"").Append(E(T("Verbunden mit", "Connected to"))).Append("\">").Append(target is null ? "–" : E(target)).Append("</td>")
                    .Append("<td data-label=\"").Append(E(T("Gesetzt", "Set"))).Append("\">").Append(SourcePill(use)).Append("</td></tr>");
            }

            _html.Append("</tbody></table>");

            foreach (var use in uses.Where(u => u.ByHand && u.Source != MatBindingSource.Variables))
            {
                UseForm(use);
            }

            foreach (var use in uses.Where(u => u.Source == MatBindingSource.Variables || !u.ByHand))
            {
                _html.Append("<p class=\"mat-muted\">").Append(E(use.Label)).Append(": ").Append(E(T("die Variablen", "the variables"))).Append(' ')
                    .Append(string.Join(", ", use.Values.Select(v => "<code>" + E(v.Variable) + "</code>")))
                    .Append(E(use.Source == MatBindingSource.Variables ? T(" setzen es; hier ist es fest.", " set it; it is fixed here.") : T(" setzen es.", " set it."))).Append("</p>");
            }

            _html.Append("</section>");
        }

        private string SourcePill(MatUseView use) => use.Source switch
        {
            MatBindingSource.Variables => "<span class=\"mat-pill mat-pill--ok\">" + E(T("Durch Variablen", "By the variables")) + "</span>",
            MatBindingSource.ByHand => "<span class=\"mat-pill mat-pill--info\">" + E(T("Hier eingetragen", "Entered here")) + "</span>",
            _ => "<span class=\"mat-pill mat-pill--plain\">" + E(use.Optional ? T("Nicht verbunden (optional)", "Not connected (optional)") : T("Nicht verbunden", "Not connected")) + "</span>",
        };

        private void UseForm(MatUseView use)
        {
            OpenForm(use.Capability);
            _html.Append("<h3>").Append(E(use.Label)).Append("</h3>");
            foreach (var value in use.Values)
            {
                var id = $"mc-{_form}-{value.Key}";
                _html.Append("<div class=\"mat-field\"><label class=\"mat-label\" for=\"").Append(id).Append("\">").Append(E(value.Label)).Append("</label>");
                if (value.Secret)
                {
                    var set = value.Value is MatSecret { IsSet: true };
                    _html.Append("<input class=\"mat-input\" id=\"").Append(id).Append("\" name=\"v.").Append(E(value.Key)).Append("\" type=\"password\" autocomplete=\"new-password\"");
                    if (set)
                    {
                        _html.Append(" placeholder=\"").Append(E(T("Unverändert", "Unchanged"))).Append('"');
                    }
                    else if (value.Required)
                    {
                        _html.Append(" required");
                    }

                    _html.Append(" />");
                }
                else
                {
                    var type = value.Type is "url" or "email" or "number" ? value.Type : "text";
                    _html.Append("<input class=\"mat-input\" id=\"").Append(id).Append("\" name=\"v.").Append(E(value.Key)).Append("\" type=\"").Append(type)
                        .Append("\" autocomplete=\"off\" value=\"").Append(E(value.Value as string)).Append('"').Append(value.Required ? " required" : string.Empty).Append(" />");
                }

                _html.Append("<p class=\"mat-hint\"><code>").Append(E(value.Variable)).Append("</code></p></div>");
            }

            _html.Append("<div class=\"mat-btn-row\"><button type=\"submit\" class=\"mat-btn mat-btn--primary\" name=\"do\" value=\"save\">").Append(E(T("Speichern", "Save"))).Append("</button>");
            if (use.Source == MatBindingSource.ByHand)
            {
                _html.Append("<button type=\"submit\" class=\"mat-btn mat-btn--danger\" name=\"do\" value=\"clear\" formnovalidate data-confirm=\"")
                    .Append(E(T("Die Verbindung trennen?", "Disconnect?"))).Append("\">").Append(E(T("Trennen", "Disconnect"))).Append("</button>");
            }

            _html.Append("</div></form>");
        }

        public void Offers(IReadOnlyList<MatOfferView> offers)
        {
            if (offers.Count == 0)
            {
                return;
            }

            Section(T("Bietet an", "Offers"), T(
                "Was diese App anderen Apps gibt. Mattiverse verbindet sie von selbst; ohne Mattiverse verbindest du eine App hier und trägst die Werte dort ein.",
                "What this app gives other apps. Mattiverse connects them by itself; without it, connect an app here and enter the values there."));
            foreach (var offer in offers)
            {
                _html.Append("<h3>").Append(E(offer.Label)).Append(" <code>").Append(E(offer.Capability)).Append("</code></h3>");
                if (offer.Bindings.Count == 0)
                {
                    _html.Append("<p class=\"mat-muted\">").Append(E(T("Noch keine App ist verbunden.", "No app is connected yet."))).Append("</p>");
                }
                else
                {
                    var app = T("App", "App");
                    var by = T("Verwaltet von", "Managed by");
                    var since = T("Seit", "Since");
                    var used = T("Zuletzt benutzt", "Last used");
                    _html.Append("<table class=\"mat-table\"><thead><tr><th>").Append(E(app)).Append("</th><th>").Append(E(by)).Append("</th><th class=\"mat-hide-sm\">").Append(E(since))
                        .Append("</th><th class=\"mat-hide-sm\">").Append(E(used)).Append("</th><th></th></tr></thead><tbody>");
                    foreach (var binding in offer.Bindings)
                    {
                        _html.Append("<tr><td data-label=\"").Append(E(app)).Append("\">").Append(E(binding.Name)).Append(" <code>").Append(E(binding.Consumer)).Append("</code></td>")
                            .Append("<td data-label=\"").Append(E(by)).Append("\">").Append(E(binding.ManagedBy == MatConnections.ManagedByHand ? T("von Hand", "by hand") : binding.ManagedBy)).Append("</td>")
                            .Append("<td class=\"mat-hide-sm\" data-label=\"").Append(E(since)).Append("\">").Append(E(Date(binding.Since))).Append("</td>")
                            .Append("<td class=\"mat-hide-sm\" data-label=\"").Append(E(used)).Append("\">").Append(E(Date(binding.LastUsed))).Append("</td>")
                            .Append("<td data-label=\"\">");
                        if (offer.ByHand)
                        {
                            OpenForm(offer.Capability, "mat-actions");
                            _html.Append("<input type=\"hidden\" name=\"consumer\" value=\"").Append(E(binding.Consumer)).Append("\" />");

                            // New credentials only for a capability that has a secret (a key, a password); a route has none.
                            if (MatCapabilities.Of(offer.Capability).Values.Any(v => v.Secret))
                            {
                                _html.Append("<button type=\"submit\" class=\"mat-btn mat-btn--sm\" name=\"do\" value=\"renew\" data-confirm=\"")
                                    .Append(E(T("Neue Zugangsdaten ausstellen? Die alten gelten sofort nicht mehr.", "Issue new credentials? The old ones stop working at once."))).Append("\">")
                                    .Append(E(T("Neu ausstellen", "Issue new"))).Append("</button>");
                            }

                            _html.Append("<button type=\"submit\" class=\"mat-btn mat-btn--sm mat-btn--danger\" name=\"do\" value=\"remove\" data-confirm=\"")
                                .Append(E(T($"Die Verbindung zu „{binding.Name}“ entfernen?", $"Remove the connection to \"{binding.Name}\"?"))).Append("\">")
                                .Append(E(T("Entfernen", "Remove"))).Append("</button></form>");
                        }

                        _html.Append("</td></tr>");
                    }

                    _html.Append("</tbody></table>");
                }

                if (offer.ByHand)
                {
                    OpenForm(offer.Capability);
                    var id = $"mc-{_form}-name";
                    _html.Append("<div class=\"mat-field\"><label class=\"mat-label\" for=\"").Append(id).Append("\">").Append(E(T("App von Hand verbinden", "Connect an app by hand"))).Append("</label>")
                        .Append("<input class=\"mat-input\" id=\"").Append(id).Append("\" name=\"name\" type=\"text\" maxlength=\"120\" autocomplete=\"off\" required placeholder=\"MatDrive\" />")
                        .Append("<p class=\"mat-hint\">").Append(E(T("Der Name der App, die verbunden wird. Ihre Werte werden einmal gezeigt.", "The name of the app to connect. Its values are shown once."))).Append("</p></div>")
                        .Append("<div class=\"mat-btn-row\"><button type=\"submit\" class=\"mat-btn mat-btn--primary\" name=\"do\" value=\"connect\">").Append(E(T("Verbinden", "Connect"))).Append("</button></div></form>");
                }
                else
                {
                    _html.Append("<p class=\"mat-muted\">").Append(E(T("Verbindungen von Hand gehen hier nicht (die App hat keinen Datenordner).", "Connections by hand are not possible here (the app has no data folder)."))).Append("</p>");
                }
            }

            _html.Append("</section>");
        }

        public void Contributions(MatContributions? contributes)
        {
            if (contributes is null || contributes.IsEmpty)
            {
                return;
            }

            Section(T("Auf dem Home-Screen", "On the home screen"), T(
                "Was diese App dem Home-Screen einer Oberfläche wie Mattiverse hinzufügt.", "What this app adds to the home screen of a shell like Mattiverse, for example."));
            _html.Append("<dl class=\"mat-kv\">");
            if (contributes.Launchers.Count > 0)
            {
                _html.Append("<dt>").Append(E(T("Icons", "Icons"))).Append("</dt><dd>").Append(E(string.Join(", ", contributes.Launchers.Select(l => l.Label)))).Append("</dd>");
            }

            if (contributes.Widgets.Count > 0)
            {
                _html.Append("<dt>").Append(E(T("Widgets", "Widgets"))).Append("</dt><dd>")
                    .Append(E(string.Join(", ", contributes.Widgets.Select(w => w.Audience == MatAudience.Admin ? w.Label + T(" (Administratoren)", " (administrators)") : w.Label)))).Append("</dd>");
            }

            if (contributes.QuickActions.Count > 0)
            {
                _html.Append("<dt>").Append(E(T("Menü der Icons", "Menu of the icons"))).Append("</dt><dd>").Append(E(string.Join(", ", contributes.QuickActions.Select(q => q.Label)))).Append("</dd>");
            }

            _html.Append("</dl></section>");
        }

        private void Section(string title, string lead)
            => _html.Append("<section class=\"mat-section\"><div class=\"mat-section__head\"><h2>").Append(E(title)).Append("</h2><p class=\"mat-section__lead\">").Append(E(lead)).Append("</p></div>");

        private void OpenForm(string capability, string css = "mat-form")
        {
            _form++;
            _html.Append("<form class=\"").Append(css).Append("\" method=\"post\" action=\"").Append(MatConnectionEndpoints.FormPath).Append("\" novalidate>")
                .Append("<input type=\"hidden\" name=\"").Append(E(tokenField)).Append("\" value=\"").Append(E(token)).Append("\" />")
                .Append("<input type=\"hidden\" name=\"capability\" value=\"").Append(E(capability)).Append("\" />")
                .Append("<input type=\"hidden\" name=\"returnUrl\" value=\"").Append(E(back)).Append("\" />");
        }

        private static string Date(DateTimeOffset? time)
            => time is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "–";
    }
}
