using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Forum;

/// <summary>
/// BBCode, the way forums write: <c>[b] [i] [u] [s] [quote] [quote=Name] [code] [url] [url=…] [img] [list] [list=1] [*]</c>, bare links become links, line
/// breaks stay. A post is kept as the member typed it and turned into HTML when it is shown.
/// <para><b>Safe by construction, not by cleaning:</b> nothing a member typed is ever passed through as markup. Every piece of text is HTML-encoded,
/// and the only tags that come out are the ones this class writes itself. A link or a picture only gets an address that is http(s), mailto or a local
/// path (never <c>javascript:</c>, <c>data:</c> or <c>//host</c>); a picture only from https or the site's own <c>/uploads/</c>. An unknown or
/// unclosed tag is shown as the text it is.</para>
/// </summary>
internal static partial class ForumMarkup
{
    [GeneratedRegex(@"\[(/?)(b|i|u|s|code|quote|url|img|list|\*)(?:=([^\]\r\n]{0,300}))?\]", RegexOptions.IgnoreCase)]
    private static partial Regex Tag();

    [GeneratedRegex(@"https?://[^\s<>\[\]""']+", RegexOptions.IgnoreCase)]
    private static partial Regex BareLink();

    private const int MaxDepth = 12;

    private sealed class Frame(string tag, string arg)
    {
        public string Tag { get; } = tag;
        public string Arg { get; } = arg;
        public StringBuilder Html { get; } = new();
        public StringBuilder Raw { get; } = new();
        public List<string> Items { get; } = new();   // a list's finished items
    }

    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>The HTML of a post.</summary>
    public static string ToHtml(string? source)
    {
        var src = (source ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var stack = new List<Frame> { new("root", "") };
        var pos = 0;
        while (pos < src.Length)
        {
            var m = Tag().Match(src, pos);
            if (!m.Success) { Text(stack, src[pos..]); break; }
            if (m.Index > pos) Text(stack, src[pos..m.Index]);
            pos = m.Index + m.Length;

            var closing = m.Groups[1].Value == "/";
            var tag = m.Groups[2].Value.ToLowerInvariant();
            var arg = m.Groups[3].Success ? m.Groups[3].Value.Trim() : "";

            if (!closing && tag == "code")
            {
                // Everything up to [/code] is shown as it is — BBCode inside a code block is the example, not markup.
                var end = src.IndexOf("[/code]", pos, StringComparison.OrdinalIgnoreCase);
                var code = end < 0 ? src[pos..] : src[pos..end];
                pos = end < 0 ? src.Length : end + "[/code]".Length;
                if (pos < src.Length && src[pos] == '\n') pos++;   // a block stands on its own line: the line break after it is not an empty line
                Append(stack, "<pre class=\"forum-code\"><code>" + E(code.Trim('\n')) + "</code></pre>", m.Value + code + "[/code]");
                continue;
            }

            if (tag == "*")
            {
                if (closing) { Literal(stack, m.Value); continue; }
                // A list item: only directly inside a list; the item before it (if any) is finished first.
                if (stack[^1].Tag == "*") CloseTop(stack);
                if (stack[^1].Tag != "list") { Literal(stack, m.Value); continue; }
                stack.Add(new Frame("*", ""));
                continue;
            }

            if (!closing)
            {
                if (stack.Count > MaxDepth) { Literal(stack, m.Value); continue; }
                stack.Add(new Frame(tag, arg));
                continue;
            }

            // A closing tag closes the nearest open tag of its kind, and everything opened inside it on the way (a forgotten [/b] does not spill out of a quote).
            var at = stack.FindLastIndex(f => f.Tag == tag);
            if (at <= 0) { Literal(stack, m.Value); continue; }
            while (stack.Count - 1 > at) CloseTop(stack);
            CloseTop(stack);
            if (tag is "quote" or "list" && pos < src.Length && src[pos] == '\n') pos++;
        }
        while (stack.Count > 1) CloseTop(stack);
        return stack[0].Html.ToString().Trim();
    }

    private static void Text(List<Frame> stack, string text)
    {
        if (text.Length == 0) return;
        var top = stack[^1];
        if (top.Tag == "list")
        {
            // Text between [list] and the first [*] (usually a line break) is not an item.
            if (string.IsNullOrWhiteSpace(text)) return;
            stack.Add(new Frame("*", ""));
            top = stack[^1];
        }
        top.Raw.Append(text);
        top.Html.Append(Linkify(text, linkify: top.Tag is not ("url" or "img")));
    }

    private static void Literal(List<Frame> stack, string text) => Append(stack, E(text), text);

    private static void Append(List<Frame> stack, string html, string raw)
    {
        if (stack[^1].Tag == "list") stack.Add(new Frame("*", ""));
        stack[^1].Html.Append(html);
        stack[^1].Raw.Append(raw);
    }

    private static void CloseTop(List<Frame> stack)
    {
        var f = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        var parent = stack[^1];
        if (f.Tag == "*")
        {
            if (parent.Tag == "list") parent.Items.Add(TrimBreaks(f.Html.ToString()));
            else { parent.Html.Append(f.Html); }
            parent.Raw.Append("[*]").Append(f.Raw);
            return;
        }
        var raw = "[" + f.Tag + (f.Arg.Length > 0 ? "=" + f.Arg : "") + "]" + f.Raw + "[/" + f.Tag + "]";
        var html = Render(f);
        if (parent.Tag == "list")
        {
            stack.Add(new Frame("*", ""));
            parent = stack[^1];
        }
        parent.Html.Append(html);
        parent.Raw.Append(raw);
    }

    private static string Render(Frame f)
    {
        var inner = f.Html.ToString();
        switch (f.Tag)
        {
            case "b": return "<strong>" + inner + "</strong>";
            case "i": return "<em>" + inner + "</em>";
            case "u": return "<u>" + inner + "</u>";
            case "s": return "<s>" + inner + "</s>";
            case "quote":
            {
                var cite = "";
                if (f.Arg.Length > 0)
                {
                    // [quote=Name] or [quote=Name|123] — 123 is the quoted post, linked so the reader can see it in its place.
                    var parts = f.Arg.Trim('"', '\'').Split('|');
                    var name = parts[0].Trim();
                    var link = parts.Length > 1 && long.TryParse(parts[1], out var pid) && pid > 0 ? "/forum/p/" + pid : null;
                    cite = "<cite>" + (link is null ? E(name) : "<a href=\"" + E(link) + "\">" + E(name) + "</a>") + "</cite>";
                }
                return "<blockquote class=\"forum-quote\">" + cite + "<div>" + TrimBreaks(inner) + "</div></blockquote>";
            }
            case "url":
            {
                var href = SafeHref(f.Arg.Length > 0 ? f.Arg : f.Raw.ToString().Trim());
                if (href is null) return E("[url" + (f.Arg.Length > 0 ? "=" + f.Arg : "") + "]") + inner + E("[/url]");
                var text = inner.Trim().Length > 0 ? inner : E(href);
                return Anchor(href, text);
            }
            case "img":
            {
                var src = SafeImage(f.Raw.ToString().Trim());
                if (src is null) return E("[img]") + inner + E("[/img]");
                return "<img class=\"forum-img\" src=\"" + E(src) + "\" alt=\"\" loading=\"lazy\" referrerpolicy=\"no-referrer\">";
            }
            case "list":
            {
                var ordered = f.Arg == "1";
                var sb = new StringBuilder(ordered ? "<ol>" : "<ul>");
                foreach (var item in f.Items.Where(i => i.Length > 0)) sb.Append("<li>").Append(item).Append("</li>");
                if (f.Html.Length > 0 && f.Items.Count == 0) sb.Append("<li>").Append(TrimBreaks(inner)).Append("</li>");
                return sb.Append(ordered ? "</ol>" : "</ul>").ToString();
            }
            default: return inner;
        }
    }

    private static string TrimBreaks(string html)
    {
        var s = html;
        while (s.StartsWith("<br>", StringComparison.Ordinal)) s = s[4..];
        while (s.EndsWith("<br>", StringComparison.Ordinal)) s = s[..^4];
        return s.Trim();
    }

    /// <summary>Encodes text, turns bare http(s) addresses into links and line breaks into &lt;br&gt;.</summary>
    private static string Linkify(string text, bool linkify)
    {
        var sb = new StringBuilder();
        var pos = 0;
        if (linkify)
        {
            foreach (Match m in BareLink().Matches(text))
            {
                var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
                sb.Append(Breaks(E(text[pos..m.Index])));
                var href = SafeHref(url);
                sb.Append(href is null ? E(url) : Anchor(href, E(url)));
                pos = m.Index + url.Length;
            }
        }
        sb.Append(Breaks(E(text[pos..])));
        return sb.ToString();
    }

    private static string Breaks(string encoded) => Regex.Replace(encoded, "\n{3,}", "\n\n").Replace("\n", "<br>");

    private static string Anchor(string href, string innerHtml)
    {
        var external = !href.StartsWith('/');
        return "<a href=\"" + E(href) + "\"" + (external ? " rel=\"nofollow ugc noopener\" target=\"_blank\"" : "") + ">" + innerHtml + "</a>";
    }

    /// <summary>An address a link may have: absolute http, https or mailto, or a local path. Null for everything else.</summary>
    public static string? SafeHref(string? candidate)
    {
        var s = (candidate ?? "").Trim().Trim('"', '\'');
        if (s.Length == 0 || s.Length > 2000 || s.Any(char.IsControl)) return null;
        if (s.StartsWith('/')) return s.StartsWith("//") || s.StartsWith("/\\") ? null : s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        return u.Scheme is "http" or "https" or "mailto" ? u.ToString() : null;
    }

    /// <summary>An address a picture may have: https, or the site's own media library. A picture over http would break an https page, and anything else could be a script in disguise.</summary>
    public static string? SafeImage(string? candidate)
    {
        var s = (candidate ?? "").Trim();
        if (s.Length == 0 || s.Length > 2000 || s.Any(char.IsControl) || s.Any(char.IsWhiteSpace)) return null;
        if (s.StartsWith("/uploads/", StringComparison.Ordinal) && !s.Contains("..")) return s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == "https" ? u.ToString() : null;
    }

    /// <summary>The post as plain text without markup and without what it quotes — for excerpts, mails and the search.</summary>
    public static string Plain(string? source, int max = 0)
    {
        var s = Regex.Replace(source ?? "", @"\[quote(=[^\]]*)?\][\s\S]*?\[/quote\]", " ", RegexOptions.IgnoreCase);
        s = Tag().Replace(s, " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (max > 0 && s.Length > max) s = s[..max].TrimEnd() + "…";
        return s;
    }

    /// <summary>The text a reply starts with when a post is quoted.</summary>
    public static string Quote(string author, long postId, string body)
    {
        var name = Regex.Replace(author ?? "", @"[\]\|\r\n]", "").Trim();
        // A quote of a quote would grow with every round; the quoted post's own quotes are left out.
        var inner = Regex.Replace(body ?? "", @"\[quote(=[^\]]*)?\][\s\S]*?\[/quote\]", "", RegexOptions.IgnoreCase).Trim();
        return "[quote=" + name + "|" + postId + "]" + inner + "[/quote]\n";
    }
}
