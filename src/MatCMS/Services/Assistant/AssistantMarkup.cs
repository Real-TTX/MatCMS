using HtmlAgilityPack;

namespace MatCMS.Services.Assistant;

/// <summary>
/// The check for markup the assistant writes where markup is the whole point — a template's layout, a component's HTML. These are not
/// sanitised into a few safe tags (that would take a layout apart) but REFUSED when they carry anything that runs: script and the other
/// executing elements, event handlers, <c>javascript:</c>/<c>data:</c> addresses, <c>srcdoc</c>, CSS expressions. The refusal names what and
/// where, so the model rewrites it. Placeholders (<c>{{content}}</c>, <c>{{#menu:header}}…</c>) are text and pass.
/// <para>Why refuse instead of clean: a model steered by an attached document must not be able to put a script onto every page of the site,
/// and silently dropping part of a layout would leave a broken page nobody understands.</para>
/// </summary>
public static class AssistantMarkup
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "frame", "frameset", "object", "embed", "applet", "base", "meta", "link", "portal",
    };

    private static readonly HashSet<string> UrlAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "action", "formaction", "xlink:href", "data", "poster", "background", "srcset", "ping", "cite",
    };

    /// <summary>What is not allowed in this markup; empty = fine.</summary>
    public static List<string> Problems(string? html)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(html)) return list;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        foreach (var n in doc.DocumentNode.Descendants())
        {
            if (n.NodeType != HtmlNodeType.Element) continue;
            if (Forbidden.Contains(n.Name)) { list.Add($"<{n.Name}> ist nicht erlaubt"); continue; }
            if (n.Name.Equals("style", StringComparison.OrdinalIgnoreCase) && BadCss(n.InnerText)) list.Add("<style> enthält expression()/javascript:");
            foreach (var a in n.Attributes)
            {
                var name = a.Name;
                var value = HtmlEntity.DeEntitize(a.Value ?? "").Trim();
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)) list.Add($"Ereignis-Attribut {name}=\"…\" an <{n.Name}> ist nicht erlaubt");
                else if (name.Equals("srcdoc", StringComparison.OrdinalIgnoreCase)) list.Add($"srcdoc an <{n.Name}> ist nicht erlaubt");
                else if (name.Equals("style", StringComparison.OrdinalIgnoreCase) && BadCss(value)) list.Add($"style an <{n.Name}> enthält expression()/javascript:");
                else if (UrlAttrs.Contains(name) && BadUrl(value, name)) list.Add($"{name}=\"{(value.Length > 40 ? value[..40] + "…" : value)}\" an <{n.Name}>: nur /pfad, #anker, https://, mailto:, tel: oder {{{{platzhalter}}}}");
            }
            if (list.Count >= 12) break;
        }
        return list.Distinct().ToList();
    }

    private static bool BadCss(string css)
    {
        var c = new string(css.Where(ch => !char.IsWhiteSpace(ch) && ch != '\\').ToArray()).ToLowerInvariant();
        return c.Contains("expression(") || c.Contains("javascript:") || c.Contains("vbscript:") || c.Contains("-moz-binding") || c.Contains("behavior:");
    }

    private static bool BadUrl(string value, string attr)
    {
        // Control characters and spaces inside a scheme are how "java\tscript:" gets past a naive check.
        var v = new string(value.Where(ch => !char.IsControl(ch) && !char.IsWhiteSpace(ch)).ToArray()).ToLowerInvariant();
        if (v.Length == 0 || v.StartsWith("{{") || v.StartsWith("/") || v.StartsWith("#") || v.StartsWith("?") || v.StartsWith("./") || v.StartsWith("../")) return false;
        if (v.StartsWith("https://") || v.StartsWith("http://") || v.StartsWith("mailto:") || v.StartsWith("tel:")) return false;
        if (v.StartsWith("data:image/") && attr.Equals("src", StringComparison.OrdinalIgnoreCase) && !v.StartsWith("data:image/svg")) return false;
        // Anything with a scheme of its own (javascript:, data:, vbscript:, …) is refused; a bare relative name ("bild.png") is fine.
        var colon = v.IndexOf(':');
        var slash = v.IndexOfAny(['/', '?', '#']);
        return colon >= 0 && (slash < 0 || colon < slash);
    }
}
