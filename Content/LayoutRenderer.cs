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
        IReadOnlyList<LangLink>? languages = null)
    {
        string Key(string slot) => menuMap.TryGetValue(slot, out var k) && !string.IsNullOrWhiteSpace(k) ? k : slot;

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
        html = html.Replace("{{languages}}", langs.Count > 1 ? DefaultLangs(langs) : "");

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
    public static string RenderLangDropdown(IReadOnlyList<LangLink> langs, bool overlay = false)
    {
        if (langs.Count < 2) return "";
        var current = langs.FirstOrDefault(l => l.IsCurrent) ?? langs[0];
        string Enc(string s) => WebUtility.HtmlEncode(s);

        var sb = new StringBuilder();
        sb.Append("<div class=\"mat-lang").Append(overlay ? " mat-lang--overlay" : "")
          .Append("\" data-mat-lang role=\"navigation\" aria-label=\"Language\">");
        sb.Append("<button type=\"button\" class=\"mat-lang-btn\" aria-haspopup=\"true\" aria-expanded=\"false\">")
          .Append("<span class=\"mat-lang-cur\">").Append(Enc(current.Locale.ToUpperInvariant())).Append("</span>")
          .Append("<svg class=\"mat-lang-chev\" viewBox=\"0 0 24 24\" width=\"14\" height=\"14\" fill=\"none\" ")
          .Append("stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">")
          .Append("<path d=\"M6 9l6 6 6-6\"/></svg>")
          .Append("</button>");
        sb.Append("<div class=\"mat-lang-menu\" hidden>");
        foreach (var l in langs)
        {
            sb.Append($"<a href=\"{Enc(l.Url)}\" hreflang=\"{Enc(l.Locale)}\"")
              .Append(l.IsCurrent ? " class=\"is-current\" aria-current=\"true\"" : "")
              .Append('>')
              .Append(Enc(MatCMS.Services.Localizer.DisplayName(l.Locale)))
              .Append(" <span class=\"mat-lang-code\">").Append(Enc(l.Locale.ToUpperInvariant())).Append("</span>")
              .Append("</a>");
        }
        sb.Append("</div></div>");
        return sb.ToString();
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
