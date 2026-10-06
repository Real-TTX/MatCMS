using System.Text;
using System.Text.RegularExpressions;

namespace MatCMS.Content;

/// <summary>
/// A block's own custom CSS (field <c>_css</c>), confined to that ONE block. It is wrapped as
/// <c>.blk-&lt;id&gt; { … }</c> (native CSS nesting): bare declarations style the block's scope element,
/// <c>&amp; h2 { … }</c> or <c>h2 { … }</c> its descendants. Two ways out of that box are closed here:
/// an unbalanced <c>}</c> would end the scope early and turn everything after it into page-wide rules,
/// and a <c>&lt;/style</c> would end the style element and let the text through as HTML.
/// </summary>
public static partial class BlockCss
{
    public const string Field = "_css";

    public static string ScopeClass(int id) => "blk-" + id;

    /// <summary>The rule set for one block, or "" when there is nothing to apply.</summary>
    public static string Scope(int id, string? css)
    {
        var clean = Sanitize(css);
        return clean.Length == 0 ? "" : "." + ScopeClass(id) + "{" + clean + "}";
    }

    /// <summary>Removes closing braces that have no opening one and closes what is left open — so the
    /// text can never leave the block's own rule. Strings and comments are skipped, a brace inside
    /// <c>content: "}"</c> is text, not structure.</summary>
    public static string Sanitize(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return "";
        var s = StyleClose().Replace(css, "");
        var sb = new StringBuilder(s.Length + 4);
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? s.Length : end + 2;
                sb.Append(s, i, stop - i);
                if (end < 0) sb.Append("*/");
                i = stop - 1;
                continue;
            }
            if (c is '"' or '\'')
            {
                var j = i + 1;
                while (j < s.Length && s[j] != c && s[j] != '\n') { if (s[j] == '\\') j++; j++; }
                var stop = Math.Min(j + 1, s.Length);
                sb.Append(s, i, stop - i);
                if (j >= s.Length || s[j] != c) sb.Append(c);   // an unterminated string is closed here
                i = stop - 1;
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}')
            {
                if (depth == 0) continue;                        // would close the block's own scope
                depth--;
            }
            sb.Append(c);
        }
        if (depth > 0) sb.Append('}', depth);
        return sb.ToString().Trim();
    }

    /// <summary>Puts the scope class on the FIRST element of a rendered block, skipping leading
    /// <c>&lt;style&gt;</c> / <c>&lt;script&gt;</c> / <c>&lt;link&gt;</c> and comments (several partials ship their
    /// CSS first). Used for blocks inside a container, which have no wrapper of their own: the class sits
    /// on the element itself, so a bare declaration styles exactly that heading, button or column.</summary>
    public static string AddScopeClass(string html, string cls)
    {
        var pos = 0;
        while (true)
        {
            var m = FirstTag().Match(html, pos);
            if (!m.Success) return html;
            var name = m.Groups["name"].Value.ToLowerInvariant();
            if (m.Value.StartsWith("<!--", StringComparison.Ordinal)) { pos = m.Index + m.Length; continue; }
            if (name is "style" or "script")
            {
                var close = html.IndexOf("</" + name, m.Index, StringComparison.OrdinalIgnoreCase);
                if (close < 0) return html;
                pos = html.IndexOf('>', close) + 1;
                if (pos <= 0) return html;
                continue;
            }
            if (name is "link" or "meta") { pos = m.Index + m.Length; continue; }

            var tag = m.Value;
            var withClass = ClassAttr().IsMatch(tag)
                ? ClassAttr().Replace(tag, mm => mm.Groups[1].Value + mm.Groups[2].Value + cls + " " + mm.Groups[3].Value, 1)
                : tag.Insert(1 + m.Groups["name"].Length, " class=\"" + cls + "\"");
            return html[..m.Index] + withClass + html[(m.Index + tag.Length)..];
        }
    }

    [GeneratedRegex("</\\s*style", RegexOptions.IgnoreCase)]
    private static partial Regex StyleClose();

    [GeneratedRegex("<!--[\\s\\S]*?-->|<(?<name>[a-zA-Z][\\w-]*)\\b[^>]*>")]
    private static partial Regex FirstTag();

    [GeneratedRegex("(\\sclass\\s*=\\s*)([\"'])([^\"']*)")]
    private static partial Regex ClassAttr();
}
