using HtmlAgilityPack;

namespace MatCMS.Services;

/// <summary>
/// Sanitises HTML that did NOT come from a trusted admin — rich content an AI supplies (block RichText
/// fields via <see cref="BlockGenerator"/>, and <c>Post.ContentHtml</c>/excerpt via the MCP content ops),
/// which is rendered raw on the public site and would otherwise be stored XSS.
/// <para><b>Deny-by-default allow-list on a lean parser.</b> HtmlAgilityPack (a small HTML parser, no CSS
/// engine, no persistent parser state — a fresh doc per call, immediately GC'd) replaces the AngleSharp-based
/// HtmlSanitizer, whose ~30 MB one-time footprint inflated every instance's working set. Only known-safe tags
/// survive; dangerous containers (<c>script</c>, <c>style</c>, <c>iframe</c>, …) are dropped whole; every
/// event handler and non-allow-listed attribute is stripped; and <c>href</c>/<c>src</c> keep only safe URL
/// schemes. The trade-off vs a full spec parser is a theoretical mXSS edge; acceptable here because the input
/// is AI output on an operator-gated path, not a fully adversarial attacker.</para>
/// </summary>
public static class SafeHtml
{
    // Kept, with their attributes sanitised.
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "hr", "strong", "b", "em", "i", "u", "s", "small", "sub", "sup", "mark",
        "ul", "ol", "li", "a", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "code",
        "span", "div", "img", "figure", "figcaption",
        "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption",
    };

    // Removed together with their ENTIRE subtree — their content is never wanted.
    private static readonly HashSet<string> DropWholeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "object", "embed", "form", "input", "button", "textarea",
        "select", "option", "link", "meta", "base", "svg", "math", "noscript", "template",
        "frame", "frameset", "applet", "param", "canvas", "audio", "video", "source",
    };

    // Attributes allowed on an allowed element. Everything else (and every on*-handler) is stripped.
    private static readonly HashSet<string> AllowedAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "alt", "title", "class", "colspan", "rowspan", "width", "height", "target", "rel",
    };

    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        Clean(doc.DocumentNode);
        return doc.DocumentNode.InnerHtml;
    }

    private static void Clean(HtmlNode node)
    {
        // Snapshot: we mutate the tree while walking.
        foreach (var child in node.ChildNodes.ToArray())
        {
            switch (child.NodeType)
            {
                case HtmlNodeType.Text:
                    break; // text is inert; HAP emits it as-is on output
                case HtmlNodeType.Comment:
                    child.Remove();
                    break;
                case HtmlNodeType.Element:
                    var tag = child.Name;
                    if (DropWholeTags.Contains(tag))
                    {
                        child.Remove(); // element AND subtree gone
                    }
                    else if (!AllowedTags.Contains(tag))
                    {
                        // Unknown but not dangerous: clean its children, then UNWRAP (drop the tag, keep the
                        // now-clean content). The tag — and thus any attribute on it — is gone.
                        Clean(child);
                        Unwrap(child);
                    }
                    else
                    {
                        SanitizeAttributes(child);
                        Clean(child);
                    }
                    break;
                default:
                    child.Remove();
                    break;
            }
        }
    }

    private static void Unwrap(HtmlNode element)
    {
        var parent = element.ParentNode;
        foreach (var c in element.ChildNodes.ToArray())
            parent.InsertBefore(c, element);
        parent.RemoveChild(element);
    }

    private static void SanitizeAttributes(HtmlNode el)
    {
        foreach (var attr in el.Attributes.ToArray())
        {
            var name = attr.Name;
            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || !AllowedAttrs.Contains(name))
            {
                attr.Remove();
                continue;
            }
            if (name.Equals("href", StringComparison.OrdinalIgnoreCase)
                || name.Equals("src", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsSafeUrl(attr.Value)) attr.Remove();
            }
        }
    }

    /// <summary>Allows relative URLs and the http/https/mailto/tel schemes only. De-entitises and strips
    /// control characters first so an obfuscated scheme ("jav&amp;#x09;ascript:") cannot slip through.</summary>
    private static bool IsSafeUrl(string? value)
    {
        var decoded = HtmlEntity.DeEntitize(value ?? "") ?? "";
        var v = new string(decoded.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (v.Length == 0) return false;

        var lower = v.ToLowerInvariant();
        var colon = lower.IndexOf(':');
        if (colon < 0) return true; // no scheme → relative
        var sep = lower.IndexOfAny(new[] { '/', '?', '#' });
        if (sep >= 0 && sep < colon) return true; // the ':' is inside the path, not a scheme
        var scheme = lower[..colon].Trim();
        return scheme is "http" or "https" or "mailto" or "tel";
    }
}
