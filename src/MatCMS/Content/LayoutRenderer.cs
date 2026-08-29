using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatCMS.Models;

namespace MatCMS.Content;

/// <summary>
/// Renders a template's custom body layout. Supports:
///   {{#menu:slot}} … per-item markup ({{label}} {{url}} {{icon}} {{target}}) … {{/menu:slot}}
///   {{menu:slot}}         → the menu rendered as default &lt;a&gt; links
///   {{content}} {{logo}} {{nav}} {{footer_nav}} {{toolbar}} {{site_name}} {{footer_text}} {{year}}
/// Slots are resolved to a menu key via the template's slot→menu map (falling back to the slot name).
/// </summary>
public static class LayoutRenderer
{
    private static readonly Regex LoopRx =
        new(@"\{\{#menu:([a-zA-Z0-9_-]+)\}\}(.*?)\{\{/menu:\1\}\}", RegexOptions.Singleline | RegexOptions.Compiled);
    // Parameter conditionals: {{#if:name}} / {{#if:name=value}} … {{/if:name}} and the negated
    // {{#ifnot:…}} … {{/ifnot:name}}. The closing tag repeats the parameter name so blocks nest and
    // two conditions on different parameters can't cross-match.
    private static readonly Regex IfRx =
        new(@"\{\{#if:([a-zA-Z0-9_-]+)(?:=([^}]*))?\}\}(.*?)\{\{/if:\1\}\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex IfNotRx =
        new(@"\{\{#ifnot:([a-zA-Z0-9_-]+)(?:=([^}]*))?\}\}(.*?)\{\{/ifnot:\1\}\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MenuRx =
        new(@"\{\{menu:([a-zA-Z0-9_-]+)\}\}", RegexOptions.Compiled);
    private static readonly Regex LangLoopRx =
        new(@"\{\{#languages\}\}(.*?)\{\{/languages\}\}", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>One language-switcher target for the <c>{{#languages}}</c> layout loop.</summary>
    public sealed record LangLink(string Locale, string Url, bool IsCurrent);

    public static string Render(
        string layoutHtml,
        IReadOnlyDictionary<string, string> globals,
        IReadOnlyDictionary<string, string> menuMap,
        Func<string, IReadOnlyList<MenuItem>> menuItems,
        IReadOnlyList<LangLink>? languages = null,
        IReadOnlyDictionary<string, string>? paramValues = null)
    {
        string Key(string slot) => menuMap.TryGetValue(slot, out var k) && !string.IsNullOrWhiteSpace(k) ? k : slot;

        // 0) Parameter conditionals — evaluated FIRST so a dropped branch takes its {{#menu}} loops and
        //    {{param:…}} tokens with it (they are never rendered). This is what lets ONE template serve
        //    several variants (e.g. a public vs. members area, switched by a per-page parameter value).
        //    Two language PSEUDO-params ride along so a template can branch on how many languages a site
        //    offers: {{#if:multilingual}}…{{/if:multilingual}} and {{#if:langcount=3}}… — no real
        //    parameter needed, it comes from the switcher data itself.
        var langCount = (languages ?? Array.Empty<LangLink>()).Count;
        var condVals = new Dictionary<string, string>(StringComparer.Ordinal);
        if (paramValues is not null) foreach (var kv in paramValues) condVals[kv.Key] = kv.Value;
        condVals["langcount"] = langCount.ToString();
        condVals["multilingual"] = langCount > 1 ? "1" : "";
        layoutHtml = ApplyConditionals(layoutHtml, condVals);

        // 1) Per-item loops with custom markup (hierarchy-aware: a node with children is wrapped so
        //    its sub-items render as a dropdown next to the parent — see .mat-hassub/.mat-sub CSS).
        var html = LoopRx.Replace(layoutHtml, m =>
        {
            var inner = m.Groups[2].Value;
            var sb = new StringBuilder();
            foreach (var node in MenuTree.Build(menuItems(Key(m.Groups[1].Value))))
                AppendInline(sb, it => RenderItem(inner, it), node);
            return sb.ToString();
        });

        // 2) Whole-menu default render → inline links, with children as .mat-sub dropdowns.
        html = MenuRx.Replace(html, m =>
            RenderInlineMenu(menuItems(Key(m.Groups[1].Value))));

        // 2b) Language switcher: per-item loop ({{#languages}} … {{locale}}/{{label}}/{{url}}/{{current}} …)
        //     plus a whole-switcher default {{languages}}. Both render empty on a single-language site.
        var langs = languages ?? Array.Empty<LangLink>();
        html = LangLoopRx.Replace(html, m =>
        {
            if (langs.Count <= 1) return "";
            var inner = m.Groups[1].Value;
            var sb = new StringBuilder();
            foreach (var l in langs) sb.Append(RenderLang(inner, l));
            return sb.ToString();
        });
        // Dropdown variants a template can place anywhere (both render empty on single-language
        // sites). Replaced BEFORE the plain {{languages}} token (exact-match Replace would not touch
        // them, but keeping the order explicit avoids surprises).
        html = html.Replace("{{languages:flags}}", langs.Count > 1 ? RenderLangDropdown(langs, flags: true) : "");
        html = html.Replace("{{languages:dropdown}}", langs.Count > 1 ? RenderLangDropdown(langs) : "");
        html = html.Replace("{{languages}}", langs.Count > 1 ? DefaultLangs(langs) : "");
        // A flag BUTTON that opens a DIALOG (centered on desktop, full-screen on mobile). Touch-friendly
        // and — unlike a dropdown — never clipped by a header/overflow. The TRIGGER's look is chosen by
        // a template parameter "langstyle": "flag" (flag only) / "code" (DE/EN…) / else flag+code.
        var langStyle = condVals.TryGetValue("langstyle", out var lst) ? lst : "";
        html = html.Replace("{{languages:modal}}", langs.Count > 1 ? RenderLangModal(langs, langStyle) : "");
        // The number of available languages — for a template that wants to show/decide something itself
        // ("… in 4 Sprachen"). Rendered even for a single-language site (then "1").
        html = html.Replace("{{languages:count}}", langs.Count.ToString());

        // 3) Global placeholders — everything except {{content}} first, then the page content last
        //    (so block HTML isn't itself scanned for placeholders).
        foreach (var kv in globals)
        {
            if (kv.Key == "content") continue;
            html = html.Replace("{{" + kv.Key + "}}", kv.Value);
        }
        if (globals.TryGetValue("content", out var content))
            html = html.Replace("{{content}}", content);
        return html;
    }

    /// <summary>Resolves {{#if:…}} / {{#ifnot:…}} blocks against the resolved parameter values, keeping
    /// or dropping each block's body. <c>{{#if:name}}</c> keeps its body when the parameter is "truthy"
    /// (non-empty and not one of 0/false/off/no); <c>{{#if:name=value}}</c> keeps it on an exact,
    /// case-insensitive value match. Unknown parameters count as empty/false.</summary>
    public static string ApplyConditionals(string html, IReadOnlyDictionary<string, string>? paramValues)
    {
        if (string.IsNullOrEmpty(html) || html.IndexOf("{{#if", StringComparison.Ordinal) < 0)
            return html;
        var vals = paramValues ?? EmptyParams;
        // Innermost-first isn't required (regex is non-greedy and the closing tag is name-scoped), but a
        // couple of passes let a kept outer block reveal inner conditionals for evaluation.
        for (var pass = 0; pass < 4; pass++)
        {
            var before = html;
            html = IfRx.Replace(html, m => Matches(vals, m.Groups[1].Value, m.Groups[2]) ? m.Groups[3].Value : "");
            html = IfNotRx.Replace(html, m => Matches(vals, m.Groups[1].Value, m.Groups[2]) ? "" : m.Groups[3].Value);
            if (ReferenceEquals(before, html) || before == html) break;
        }
        return html;
    }

    private static readonly Dictionary<string, string> EmptyParams = new(StringComparer.Ordinal);

    private static bool Matches(IReadOnlyDictionary<string, string> vals, string name, Group value)
    {
        var actual = vals.TryGetValue(name, out var v) ? (v ?? "") : "";
        if (value.Success) // {{#if:name=value}} — exact (case-insensitive) match
            return string.Equals(actual.Trim(), value.Value.Trim(), StringComparison.OrdinalIgnoreCase);
        // {{#if:name}} — truthy test
        var t = actual.Trim();
        return t.Length > 0 && t is not ("0" or "false" or "off" or "no");
    }

    /// <summary>Renders a flat item list as an inline menu: leaf items render as a plain link
    /// (backward-compatible with flat menus); a node with children is wrapped in a <c>.mat-hassub</c>
    /// span carrying a <c>.mat-sub</c> dropdown with the child items. Used for the default menu render
    /// and by the built-in layout header/footer, so one CSS ruleset covers every menu.</summary>
    public static string RenderInlineMenu(IReadOnlyList<MenuItem> flat)
    {
        var sb = new StringBuilder();
        foreach (var node in MenuTree.Build(flat)) AppendInline(sb, DefaultLink, node);
        return sb.ToString();
    }

    /// <summary>Appends one node (recursively) using <paramref name="link"/> to render each item;
    /// nodes with children get a <c>.mat-hassub</c>/<c>.mat-sub</c> dropdown wrapper.</summary>
    private static void AppendInline(StringBuilder sb, Func<MenuItem, string> link, MenuNode node)
    {
        if (!node.HasChildren) { sb.Append(link(node.Item)); return; }
        sb.Append("<span class=\"mat-hassub\">");
        sb.Append(link(node.Item));
        sb.Append("<span class=\"mat-sub\">");
        foreach (var c in node.Children) AppendInline(sb, link, c);
        sb.Append("</span></span>");
    }

    private static string RenderItem(string tpl, MenuItem it)
    {
        var target = it.OpenInNewTab ? " target=\"_blank\" rel=\"noopener\"" : "";
        var icon = MenuIcons.IconMarkup(it.Icon);
        return tpl
            .Replace("{{label}}", WebUtility.HtmlEncode(it.Label))
            .Replace("{{url}}", WebUtility.HtmlEncode(it.Url))
            .Replace("{{icon}}", icon)
            .Replace("{{target}}", target);
    }

    private static string DefaultLink(MenuItem it)
    {
        var target = it.OpenInNewTab ? " target=\"_blank\" rel=\"noopener\"" : "";
        return $"<a href=\"{WebUtility.HtmlEncode(it.Url)}\"{target}>{WebUtility.HtmlEncode(it.Label)}</a>";
    }

    private static string RenderLang(string tpl, LangLink l) =>
        tpl.Replace("{{locale}}", WebUtility.HtmlEncode(l.Locale))
           .Replace("{{label}}", WebUtility.HtmlEncode(l.Locale.ToUpperInvariant()))
           .Replace("{{url}}", WebUtility.HtmlEncode(l.Url))
           // The flag as an inline-SVG image, so a template's own {{#languages}} loop can show flags
           // (include {{flag}} to show them, leave it out for text-only). Same artwork as the dropdown.
           .Replace("{{flag}}", FlagSvg(l.Locale))
           .Replace("{{current}}", l.IsCurrent ? " class=\"lang-current\" aria-current=\"true\"" : "");

    private static string DefaultLangs(IReadOnlyList<LangLink> langs)
    {
        var sb = new StringBuilder("<span class=\"lang-switch\">");
        foreach (var l in langs)
            sb.Append($"<a href=\"{WebUtility.HtmlEncode(l.Url)}\" hreflang=\"{WebUtility.HtmlEncode(l.Locale)}\"")
              .Append(l.IsCurrent ? " class=\"lang-current\" aria-current=\"true\"" : "")
              .Append('>').Append(WebUtility.HtmlEncode(l.Locale.ToUpperInvariant())).Append("</a>");
        sb.Append("</span>");
        return sb.ToString();
    }

    /// <summary>
    /// The FALLBACK language switcher: a closed dropdown showing the current language code; clicking
    /// it opens the list (toggled by wwwroot/js/lang-switch.js, styled in site.css). Used only when
    /// the layout does not place its own switcher — both in the built-in default header and as the
    /// floating top-right overlay on custom layouts without a <c>{{languages}}</c> placeholder.
    /// </summary>
    public static string RenderLangDropdown(IReadOnlyList<LangLink> langs, bool overlay = false, bool flags = false)
    {
        if (langs.Count < 2) return "";
        var current = langs.FirstOrDefault(l => l.IsCurrent) ?? langs[0];
        string Enc(string s) => WebUtility.HtmlEncode(s);

        var sb = new StringBuilder();
        sb.Append("<div class=\"mat-lang").Append(overlay ? " mat-lang--overlay" : "")
          .Append("\" data-mat-lang role=\"navigation\" aria-label=\"Language\">");
        sb.Append("<button type=\"button\" class=\"mat-lang-btn\" aria-haspopup=\"true\" aria-expanded=\"false\">");
        if (flags) sb.Append(FlagSvg(current.Locale));
        sb.Append("<span class=\"mat-lang-cur\">").Append(Enc(current.Locale.ToUpperInvariant())).Append("</span>")
          .Append("<svg class=\"mat-lang-chev\" viewBox=\"0 0 24 24\" width=\"14\" height=\"14\" fill=\"none\" ")
          .Append("stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">")
          .Append("<path d=\"M6 9l6 6 6-6\"/></svg>")
          .Append("</button>");
        sb.Append("<div class=\"mat-lang-menu\" hidden>");
        foreach (var l in langs)
        {
            sb.Append($"<a href=\"{Enc(l.Url)}\" hreflang=\"{Enc(l.Locale)}\"")
              .Append(l.IsCurrent ? " class=\"is-current\" aria-current=\"true\"" : "")
              .Append('>');
            sb.Append("<span class=\"mat-lang-name\">");
            if (flags) sb.Append(FlagSvg(l.Locale));
            sb.Append(Enc(MatCMS.Services.Localizer.DisplayName(l.Locale))).Append("</span>");
            sb.Append("<span class=\"mat-lang-code\">").Append(Enc(l.Locale.ToUpperInvariant())).Append("</span>")
              .Append("</a>");
        }
        sb.Append("</div></div>");
        return sb.ToString();
    }

    /// <summary>
    /// A flag button that opens a DIALOG with the language list (centered on desktop, full-screen on
    /// mobile via CSS). Toggled by wwwroot/js/lang-switch.js. Rendered empty on a single-language site.
    /// The clean answer to "the floating switcher collides / the dropdown gets clipped": a modal never
    /// does, and on touch it is far easier to hit than a dropdown item.
    /// </summary>
    /// <param name="style">Trigger look: "flag" (flag only) · "code" (DE/EN…, no flag) · anything else =
    /// flag + code. The dialog LIST always shows flag + name + code, so the choice is only about the
    /// current-language button.</param>
    public static string RenderLangModal(IReadOnlyList<LangLink> langs, string style = "")
    {
        if (langs.Count < 2) return "";
        var current = langs.FirstOrDefault(l => l.IsCurrent) ?? langs[0];
        string Enc(string s) => WebUtility.HtmlEncode(s);
        style = (style ?? "").Trim().ToLowerInvariant();
        var showFlag = style != "code";
        var showCode = style != "flag";

        var sb = new StringBuilder();
        sb.Append("<div class=\"mat-langm").Append(showCode ? "" : " mat-langm--flagonly").Append("\" data-mat-langm>");
        sb.Append("<button type=\"button\" class=\"mat-langm-btn\" data-mat-langm-open aria-haspopup=\"dialog\" aria-expanded=\"false\" aria-label=\"Language\">");
        if (showFlag) sb.Append(FlagSvg(current.Locale));
        if (showCode) sb.Append("<span class=\"mat-langm-cur\">").Append(Enc(current.Locale.ToUpperInvariant())).Append("</span>");
        sb.Append("</button>");
        sb.Append("<div class=\"mat-langm-overlay\" data-mat-langm-overlay hidden>");
        sb.Append("<div class=\"mat-langm-dialog\" role=\"dialog\" aria-modal=\"true\">");
        sb.Append("<button type=\"button\" class=\"mat-langm-close\" data-mat-langm-close aria-label=\"×\">")
          .Append("<svg viewBox=\"0 0 24 24\" width=\"22\" height=\"22\" fill=\"none\" stroke=\"currentColor\" ")
          .Append("stroke-width=\"2\" stroke-linecap=\"round\" aria-hidden=\"true\"><path d=\"M6 6l12 12M18 6L6 18\"/></svg></button>");
        sb.Append("<div class=\"mat-langm-list\">");
        foreach (var l in langs)
        {
            sb.Append("<a href=\"").Append(Enc(l.Url)).Append('"')
              .Append(l.IsCurrent ? " class=\"is-current\" aria-current=\"true\"" : "").Append('>');
            sb.Append(FlagSvg(l.Locale)); // the list always shows flags; only the trigger style varies
            sb.Append("<span class=\"mat-langm-name\">").Append(Enc(MatCMS.Services.Localizer.DisplayName(l.Locale))).Append("</span>");
            sb.Append("<span class=\"mat-langm-code\">").Append(Enc(l.Locale.ToUpperInvariant())).Append("</span>")
              .Append("</a>");
        }
        sb.Append("</div></div></div></div>");
        return sb.ToString();
    }

    /// <summary>
    /// Tiny inline SVG flags (24×16) for the supported locales. Inline SVG instead of emoji flags
    /// because Windows browsers don't render emoji flags (they fall back to bare letter pairs), and
    /// instead of an image CDN so pages stay self-contained. Slightly simplified heraldry at 16 px.
    /// </summary>
    public static string FlagSvg(string locale)
    {
        var body = locale.ToLowerInvariant() switch
        {
            "de" => "<rect width='24' height='5.33' y='0' fill='#000'/><rect width='24' height='5.33' y='5.33' fill='#DD0000'/><rect width='24' height='5.34' y='10.66' fill='#FFCE00'/>",
            "en" => "<rect width='24' height='16' fill='#012169'/><path d='M0 0l24 16M24 0L0 16' stroke='#fff' stroke-width='3.2'/><path d='M0 0l24 16M24 0L0 16' stroke='#C8102E' stroke-width='1.6'/><path d='M12 0v16M0 8h24' stroke='#fff' stroke-width='5'/><path d='M12 0v16M0 8h24' stroke='#C8102E' stroke-width='3'/>",
            "hr" => "<rect width='24' height='5.33' fill='#FF0000'/><rect width='24' height='5.33' y='5.33' fill='#fff'/><rect width='24' height='5.34' y='10.66' fill='#171796'/><g><rect x='9.6' y='4.4' width='1.6' height='1.6' fill='#FF0000'/><rect x='12.8' y='4.4' width='1.6' height='1.6' fill='#FF0000'/><rect x='11.2' y='4.4' width='1.6' height='1.6' fill='#fff'/><rect x='9.6' y='6' width='1.6' height='1.6' fill='#fff'/><rect x='11.2' y='6' width='1.6' height='1.6' fill='#FF0000'/><rect x='12.8' y='6' width='1.6' height='1.6' fill='#fff'/><rect x='10.4' y='7.6' width='1.6' height='1.6' fill='#FF0000'/><rect x='12' y='7.6' width='1.6' height='1.6' fill='#fff'/></g>",
            "sk" => "<rect width='24' height='5.33' fill='#fff'/><rect width='24' height='5.33' y='5.33' fill='#0B4EA2'/><rect width='24' height='5.34' y='10.66' fill='#EE1C25'/><path d='M7 4.5h4.4v4.2c0 1.9-1.3 2.9-2.2 3.3-.9-.4-2.2-1.4-2.2-3.3z' fill='#EE1C25' stroke='#fff' stroke-width='.5'/><path d='M8.6 6h1.2v1h1v1.1h-1v2h-1.2v-2h-1V7h1z' fill='#fff'/>",
            "fr" => "<rect width='8' height='16' fill='#002395'/><rect width='8' height='16' x='8' fill='#fff'/><rect width='8' height='16' x='16' fill='#ED2939'/>",
            "it" => "<rect width='8' height='16' fill='#009246'/><rect width='8' height='16' x='8' fill='#fff'/><rect width='8' height='16' x='16' fill='#CE2B37'/>",
            "es" => "<rect width='24' height='16' fill='#AA151B'/><rect width='24' height='8' y='4' fill='#F1BF00'/>",
            "nl" => "<rect width='24' height='5.33' fill='#AE1C28'/><rect width='24' height='5.33' y='5.33' fill='#fff'/><rect width='24' height='5.34' y='10.66' fill='#21468B'/>",
            "pl" => "<rect width='24' height='8' fill='#fff'/><rect width='24' height='8' y='8' fill='#DC143C'/>",
            _ => "<rect width='24' height='16' fill='#ccc'/>"
        };
        return "<svg class=\"mat-lang-flag\" viewBox=\"0 0 24 16\" width=\"21\" height=\"14\" aria-hidden=\"true\">" + body + "</svg>";
    }

    /// <summary>Parse the stored slot→menu map.</summary>
    public static Dictionary<string, string> ParseMap(string? json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json ?? "{}") ?? new(); }
        catch { return new(); }
    }

    /// <summary>Distinct menu slots referenced by the layout (for the mapping UI).</summary>
    public static List<string> ExtractSlots(string? layoutHtml)
    {
        var slots = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(layoutHtml))
        {
            foreach (Match m in LoopRx.Matches(layoutHtml)) slots.Add(m.Groups[1].Value);
            foreach (Match m in MenuRx.Matches(layoutHtml)) slots.Add(m.Groups[1].Value);
        }
        return slots.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }
}
