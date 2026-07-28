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

        // 1) Per-item loops with custom markup.
        var html = LoopRx.Replace(layoutHtml, m =>
        {
            var inner = m.Groups[2].Value;
            var sb = new StringBuilder();
            foreach (var it in menuItems(Key(m.Groups[1].Value)))
                sb.Append(RenderItem(inner, it));
            return sb.ToString();
        });

        // 2) Whole-menu default render.
        html = MenuRx.Replace(html, m =>
        {
            var sb = new StringBuilder();
            foreach (var it in menuItems(Key(m.Groups[1].Value)))
                sb.Append(DefaultLink(it));
            return sb.ToString();
        });

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
