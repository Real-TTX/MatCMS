using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Docs;

/// <summary>One entry of a page's table of contents.</summary>
public sealed record TocEntry(int Level, string Id, string Text);

/// <summary>A page's text drawn as HTML, with the headings it carries (for the table of contents).</summary>
public sealed record RenderedDoc(string Html, IReadOnlyList<TocEntry> Toc);

/// <summary>
/// Markdown to HTML for the documentation — written here, without a library, so the plugin stays one small DLL. It covers what technical documentation
/// is written with (CommonMark's everyday part plus GitHub's tables, task lists, strikethrough and alerts) and is SAFE by construction: every character
/// of the source is encoded, and only the markup this class writes itself reaches the page — raw HTML in the source is shown as text, and a link or a
/// picture only keeps an address that is local, <c>https</c>/<c>http</c>, <c>mailto:</c> or <c>tel:</c>.
/// <para><c>[Text](doc:installation/docker)</c> links another page of the docs by its path; the resolver turns it into the address in the reader's
/// language and version.</para>
/// </summary>
public static partial class DocsMarkdown
{
    /// <summary>How links between pages and the titles of the alert boxes are made.</summary>
    public sealed class Options
    {
        /// <summary>Turns the path after <c>doc:</c> into an address; null keeps the link out (the text stays).</summary>
        public Func<string, string?>? ResolveDoc { get; init; }
        /// <summary>The title of an alert box (<c>note</c>, <c>tip</c>, <c>important</c>, <c>warning</c>, <c>caution</c>).</summary>
        public Func<string, string>? AlertTitle { get; init; }
        /// <summary>The text of a code block's copy button; empty = no button.</summary>
        public string CopyLabel { get; init; } = "";
        /// <summary>What the copy button says for a moment after copying.</summary>
        public string CopiedLabel { get; init; } = "";
    }

    /// <summary>Encodes the five characters that matter in HTML and leaves every other letter as it is (WebUtility would write "ä" as a number).</summary>
    public static string E(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(Special) < 0) return s;
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#39;", _ => c.ToString() });
        return sb.ToString();
    }

    private static readonly char[] Special = ['&', '<', '>', '"', '\''];

    public static RenderedDoc Render(string? markdown, Options? options = null)
    {
        var r = new Renderer(options ?? new Options());
        var lines = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n');
        var html = r.Blocks(lines);
        return new RenderedDoc(html, r.Toc);
    }

    /// <summary>The page as plain text (for the search and the summaries): the HTML drawn, its tags removed.</summary>
    public static string Plain(string html)
    {
        var s = TagRx().Replace(BlockEndRx().Replace(html ?? "", " "), "");
        s = WebUtility.HtmlDecode(s);
        return SpaceRx().Replace(s, " ").Trim();
    }

    /// <summary>
    /// Rich text from the editor (already cleaned by the host when it was saved): ids on its h2/h3 headings for the table of contents, the headings
    /// collected. Nothing else is changed.
    /// </summary>
    public static RenderedDoc FromHtml(string? html)
    {
        var toc = new List<TocEntry>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var outHtml = HeadingRx().Replace(html ?? "", m =>
        {
            var level = m.Groups[1].Value[0] - '0';
            var inner = m.Groups[3].Value;
            var text = Plain(inner);
            var id = UniqueId(text, used);
            if (level is 2 or 3 && text.Length > 0) toc.Add(new TocEntry(level, id, text));
            var attrs = IdAttrRx().Replace(m.Groups[2].Value, "");
            return $"<h{level}{attrs} id=\"{id}\">{inner}</h{level}>";
        });
        return new RenderedDoc(outHtml, toc);
    }

    /// <summary>An anchor id from a heading's text: lower case, letters and digits joined by "-", unique on the page.</summary>
    internal static string UniqueId(string text, HashSet<string> used)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch switch { 'ä' => "ae", 'ö' => "oe", 'ü' => "ue", 'ß' => "ss", _ => ch.ToString() });
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var id = sb.ToString().Trim('-');
        if (id.Length == 0) id = "abschnitt";
        if (id.Length > 60) id = id[..60].Trim('-');
        if (char.IsDigit(id[0])) id = "h-" + id;
        var candidate = id;
        for (var i = 2; !used.Add(candidate); i++) candidate = id + "-" + i.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    /// <summary>True for an address a link or a picture may carry: local (<c>/x</c>, <c>#x</c>, <c>x/y</c>, <c>./x</c>), http(s), mailto or tel.
    /// Never <c>javascript:</c>, <c>data:</c>, a protocol-relative <c>//host</c> or anything with another scheme.</summary>
    public static bool SafeUrl(string url)
    {
        url = url.Trim();
        if (url.Length == 0 || url.StartsWith("//", StringComparison.Ordinal) || url.StartsWith("\\", StringComparison.Ordinal)) return false;
        if (url.Any(c => char.IsControl(c) || c is '"' or '<' or '>' or '`')) return false;
        var colon = url.IndexOf(':');
        var cut = url.IndexOfAny(new[] { '/', '?', '#' });
        if (colon < 0 || (cut >= 0 && cut < colon)) return true;               // no scheme: a relative address
        var scheme = url[..colon].ToLowerInvariant();
        return scheme is "http" or "https" or "mailto" or "tel";
    }

    [GeneratedRegex(@"<h([1-6])(\s[^>]*)?>(.*?)</h\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadingRx();
    [GeneratedRegex(@"\s+id\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex IdAttrRx();
    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagRx();
    [GeneratedRegex(@"</(p|h[1-6]|li|pre|td|th|div|blockquote|tr)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRx();
    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRx();

    private sealed partial class Renderer
    {
        private readonly Options _o;
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        public List<TocEntry> Toc { get; } = new();
        private int _depth;

        public Renderer(Options o) => _o = o;

        // ---- blocks ---------------------------------------------------------------------------------------------------

        public string Blocks(IReadOnlyList<string> lines)
        {
            // A source nested deeper than this (quotes in lists in quotes …) is not a document but an attack on the stack.
            if (++_depth > 12) { _depth--; return "<p>" + E(string.Join(" ", lines)) + "</p>"; }
            var sb = new StringBuilder();
            var i = 0;
            while (i < lines.Count)
            {
                var line = lines[i];
                if (line.Trim().Length == 0) { i++; continue; }
                var t = line.TrimStart();
                var indent = line.Length - t.Length;

                if (indent < 4 && FenceRx().Match(t) is { Success: true } fence) { i = Code(lines, i, indent, fence, sb); continue; }
                if (indent < 4 && AtxRx().Match(t) is { Success: true } h) { Heading(h.Groups[1].Value.Length, h.Groups[2].Value, sb); i++; continue; }
                if (indent < 4 && HrRx().IsMatch(t)) { sb.Append("<hr>"); i++; continue; }
                if (indent < 4 && t.StartsWith('>')) { i = Quote(lines, i, sb); continue; }
                if (indent < 4 && ListRx().Match(line) is { Success: true }) { i = List(lines, i, sb); continue; }
                if (indent < 4 && t.StartsWith('|') || (i + 1 < lines.Count && t.Contains('|') && TableSepRx().IsMatch(lines[i + 1].Trim())))
                {
                    var end = Table(lines, i, sb);
                    if (end > i) { i = end; continue; }
                }
                i = Paragraph(lines, i, sb);
            }
            _depth--;
            return sb.ToString();
        }

        private void Heading(int level, string raw, StringBuilder sb)
        {
            var text = ClosingHashesRx().Replace(raw, "").Trim();
            var inner = Inline(text);
            var plain = Plain(inner);
            var id = UniqueId(plain, _ids);
            if (level is 2 or 3 && plain.Length > 0) Toc.Add(new TocEntry(level, id, plain));
            sb.Append($"<h{level} id=\"{id}\">{inner}<a class=\"docs-anchor\" href=\"#{id}\" aria-hidden=\"true\" tabindex=\"-1\">#</a></h{level}>");
        }

        private int Code(IReadOnlyList<string> lines, int start, int indent, Match fence, StringBuilder sb)
        {
            var marker = fence.Groups[1].Value;
            var lang = new string(fence.Groups[2].Value.Trim().Split(' ', 2)[0].Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '+' or '#' or '.').ToArray()).ToLowerInvariant();
            var body = new List<string>();
            var i = start + 1;
            for (; i < lines.Count; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith(marker, StringComparison.Ordinal) && t.Trim().All(c => c == marker[0]) && t.Trim().Length >= marker.Length) { i++; break; }
                var l = lines[i];
                var cut = 0;
                while (cut < indent && cut < l.Length && l[cut] == ' ') cut++;
                body.Add(l[cut..]);
            }
            var langAttr = lang.Length > 0 ? $" class=\"language-{E(lang)}\"" : "";
            var label = lang.Length > 0 ? $"<span class=\"docs-code-lang\">{E(lang)}</span>" : "";
            var copy = _o.CopyLabel.Length > 0 ? $"<button type=\"button\" class=\"docs-copy\" data-docs-copy data-copied=\"{E(_o.CopiedLabel)}\" hidden>{E(_o.CopyLabel)}</button>" : "";
            // Language and copy button in a head row of their own ABOVE the code: laid over the code they covered each other and its first line.
            var head = label.Length + copy.Length > 0 ? $"<div class=\"docs-code-head\">{(label.Length > 0 ? label : "<span></span>")}{copy}</div>" : "";
            sb.Append($"<div class=\"docs-code{(head.Length > 0 ? " docs-code-has-head" : "")}\">{head}<pre><code{langAttr}>{E(string.Join("\n", body))}</code></pre></div>");
            return i;
        }

        private int Quote(IReadOnlyList<string> lines, int start, StringBuilder sb)
        {
            var inner = new List<string>();
            var i = start;
            for (; i < lines.Count; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith('>')) inner.Add(t.Length > 1 && t[1] == ' ' ? t[2..] : t[1..]);
                else if (t.Length > 0 && inner.Count > 0 && inner[^1].Trim().Length > 0 && !FenceRx().IsMatch(t) && !AtxRx().IsMatch(t)) inner.Add(t);   // a lazy continuation line
                else break;
            }
            // GitHub's alerts: "> [!NOTE]" as the first line makes a box of its own kind.
            if (inner.Count > 0 && AlertRx().Match(inner[0].Trim()) is { Success: true } a)
            {
                var kind = a.Groups[1].Value.ToLowerInvariant();
                var rest = inner.Skip(1).ToList();
                var title = _o.AlertTitle?.Invoke(kind) ?? kind;
                sb.Append($"<div class=\"docs-callout docs-callout-{kind}\" role=\"note\"><p class=\"docs-callout-title\">{E(title)}</p>{Blocks(rest)}</div>");
                return i;
            }
            sb.Append("<blockquote>").Append(Blocks(inner)).Append("</blockquote>");
            return i;
        }

        private int List(IReadOnlyList<string> lines, int start, StringBuilder sb)
        {
            var first = ListRx().Match(lines[start]);
            var ordered = char.IsDigit(first.Groups[2].Value[0]);
            var baseIndent = first.Groups[1].Value.Length;
            var items = new List<List<string>>();
            var loose = false;
            var i = start;
            var startNo = ordered && int.TryParse(first.Groups[2].Value.TrimEnd('.', ')'), out var n) ? n : 1;
            while (i < lines.Count)
            {
                var line = lines[i];
                var m = ListRx().Match(line);
                if (m.Success && m.Groups[1].Value.Length <= baseIndent + 1 && char.IsDigit(m.Groups[2].Value[0]) == ordered)
                {
                    var content = m.Groups[3].Value;
                    items.Add(new List<string> { content });
                    i++;
                    continue;
                }
                if (line.Trim().Length == 0)
                {
                    // A blank line ends the list unless the next line still belongs to it (indented, or the next item).
                    var next = i + 1 < lines.Count ? lines[i + 1] : "";
                    var nm = ListRx().Match(next);
                    var nextIndent = next.Length - next.TrimStart().Length;
                    if (next.Trim().Length > 0 && (nextIndent > baseIndent + 1 || (nm.Success && nm.Groups[1].Value.Length <= baseIndent + 1 && char.IsDigit(nm.Groups[2].Value[0]) == ordered)))
                    {
                        if (!(nm.Success && nm.Groups[1].Value.Length > baseIndent + 1)) loose = true;
                        items[^1].Add("");
                        i++;
                        continue;
                    }
                    break;
                }
                var ind = line.Length - line.TrimStart().Length;
                if (ind > baseIndent + 1 || (items.Count > 0 && items[^1][^1].Trim().Length > 0 && !BlockStartRx().IsMatch(line.TrimStart())))
                {
                    // Inside the item: strip the item's indentation (as much as the line has of it).
                    var cut = Math.Min(ind, baseIndent + Math.Max(2, first.Groups[2].Value.Length + 1));
                    items[^1].Add(line[cut..]);
                    i++;
                    continue;
                }
                break;
            }
            var tag = ordered ? "ol" : "ul";
            sb.Append(ordered && startNo != 1 ? $"<ol start=\"{startNo}\">" : $"<{tag}>");
            foreach (var item in items)
            {
                var text = item[0];
                var task = TaskRx().Match(text);
                var check = "";
                if (task.Success)
                {
                    check = $"<input type=\"checkbox\" disabled{(task.Groups[1].Value.Trim().Length > 0 ? " checked" : "")}> ";
                    item[0] = text[task.Length..];
                }
                var tight = !loose && !item.Skip(1).Any(l => l.Trim().Length == 0);
                string inner;
                if (tight && item.Count == 1) inner = Inline(item[0]);
                else if (tight)
                {
                    // First line(s) of text, then nested blocks (a sub-list) — no paragraph around the text of a tight item.
                    var textLines = item.TakeWhile(l => !BlockStartRx().IsMatch(l.TrimStart())).ToList();
                    inner = Inline(string.Join("\n", textLines)) + Blocks(item.Skip(textLines.Count).ToList());
                }
                else inner = Blocks(item);
                sb.Append(task.Success ? "<li class=\"docs-task\">" : "<li>").Append(check).Append(inner).Append("</li>");
            }
            sb.Append($"</{tag}>");
            return i;
        }

        private int Table(IReadOnlyList<string> lines, int start, StringBuilder sb)
        {
            if (start + 1 >= lines.Count || !TableSepRx().IsMatch(lines[start + 1].Trim())) return start;
            var head = Cells(lines[start]);
            var aligns = Cells(lines[start + 1]).Select(c =>
            {
                var s = c.Trim();
                return s.StartsWith(':') && s.EndsWith(':') ? "center" : s.EndsWith(':') ? "right" : s.StartsWith(':') ? "left" : "";
            }).ToList();
            string Align(int k) => k < aligns.Count && aligns[k].Length > 0 ? $" style=\"text-align:{aligns[k]}\"" : "";
            sb.Append("<div class=\"docs-table\"><table><thead><tr>");
            for (var k = 0; k < head.Count; k++) sb.Append($"<th{Align(k)}>{Inline(head[k].Trim())}</th>");
            sb.Append("</tr></thead><tbody>");
            var i = start + 2;
            for (; i < lines.Count && lines[i].Trim().Length > 0 && lines[i].Contains('|'); i++)
            {
                var cells = Cells(lines[i]);
                sb.Append("<tr>");
                for (var k = 0; k < head.Count; k++) sb.Append($"<td{Align(k)}>{(k < cells.Count ? Inline(cells[k].Trim()) : "")}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table></div>");
            return i;
        }

        /// <summary>The cells of a table row: split at "|" outside of code spans, "\|" is a "|" in a cell.</summary>
        private static List<string> Cells(string row)
        {
            var s = row.Trim();
            if (s.StartsWith('|')) s = s[1..];
            if (s.EndsWith('|') && !s.EndsWith("\\|", StringComparison.Ordinal)) s = s[..^1];
            var cells = new List<string>();
            var cur = new StringBuilder();
            var inCode = false;
            for (var k = 0; k < s.Length; k++)
            {
                var c = s[k];
                if (c == '\\' && k + 1 < s.Length && s[k + 1] == '|') { cur.Append('|'); k++; continue; }
                if (c == '`') inCode = !inCode;
                if (c == '|' && !inCode) { cells.Add(cur.ToString()); cur.Clear(); continue; }
                cur.Append(c);
            }
            cells.Add(cur.ToString());
            return cells;
        }

        private int Paragraph(IReadOnlyList<string> lines, int start, StringBuilder sb)
        {
            var text = new List<string>();
            var i = start;
            for (; i < lines.Count; i++)
            {
                var l = lines[i];
                if (l.Trim().Length == 0) break;
                if (i > start && BlockStartRx().IsMatch(l.TrimStart()) && (l.Length - l.TrimStart().Length) < 4) break;
                text.Add(l);
            }
            if (text.Count == 0) { text.Add(lines[i]); i++; }
            sb.Append("<p>").Append(Inline(string.Join("\n", text.Select(l => l.TrimStart())))).Append("</p>");
            return i;
        }

        // ---- inline -----------------------------------------------------------------------------------------------------

        public string Inline(string text)
        {
            var slots = new List<string>();
            string Slot(string html) { slots.Add(html); return "\u0001" + (slots.Count - 1).ToString(CultureInfo.InvariantCulture) + "\u0002"; }

            // 1. backslash escapes and code spans: their content is never read as markup
            var s = EscapeRx().Replace(text, m => Slot(E(m.Groups[1].Value)));
            s = CodeSpanRx().Replace(s, m => Slot("<code>" + E(m.Groups[2].Value.Trim()) + "</code>"));
            // 2. pictures, links and <autolinks>, drawn now (the link text goes through this same method)
            s = ImageRx().Replace(s, m =>
            {
                var src = m.Groups[2].Value.Trim();
                if (!SafeUrl(src)) return Slot(E(m.Groups[1].Value));
                var title = m.Groups[3].Success ? $" title=\"{E(m.Groups[3].Value)}\"" : "";
                return Slot($"<img src=\"{E(src)}\" alt=\"{E(m.Groups[1].Value)}\"{title} loading=\"lazy\">");
            });
            s = LinkRx().Replace(s, m =>
            {
                var label = Inline(m.Groups[1].Value);
                var href = m.Groups[2].Value.Trim();
                if (href.StartsWith("doc:", StringComparison.OrdinalIgnoreCase))
                {
                    var target = href[4..];
                    var hash = "";
                    var cut = target.IndexOf('#');
                    if (cut >= 0) { hash = target[cut..]; target = target[..cut]; }
                    var resolved = _o.ResolveDoc?.Invoke(target.Trim('/'));
                    if (resolved is null) return Slot($"<span class=\"docs-broken\">{label}</span>");
                    href = resolved + hash;
                }
                if (!SafeUrl(href)) return Slot(label);
                var title = m.Groups[3].Success ? $" title=\"{E(m.Groups[3].Value)}\"" : "";
                var external = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? " rel=\"noopener\"" : "";
                return Slot($"<a href=\"{E(href)}\"{title}{external}>{label}</a>");
            });
            s = AutoLinkRx().Replace(s, m => Slot($"<a href=\"{E(m.Groups[1].Value)}\" rel=\"noopener\">{E(m.Groups[1].Value)}</a>"));
            // 3. everything that is left is text
            s = E(s);
            // 4. emphasis on the encoded text (the markers are not touched by encoding)
            s = StrongRx().Replace(s, "<strong>$2</strong>");
            s = EmRx().Replace(s, "<em>$2</em>");
            s = DelRx().Replace(s, "<del>$1</del>");
            s = BareUrlRx().Replace(s, m => $"<a href=\"{m.Value}\" rel=\"noopener\">{m.Value}</a>");
            // 5. hard line breaks (two spaces or a backslash at the end of a line), other line breaks stay soft
            s = HardBreakRx().Replace(s, "<br>\n");
            // 6. the drawn pieces back in (a slot may hold another slot: link text with code)
            for (var k = 0; k < 4 && s.Contains('\u0001'); k++)
                s = SlotRx().Replace(s, m => slots[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);
            return s;
        }

        [GeneratedRegex(@"^(`{3,}|~{3,})(.*)$")]
        private static partial Regex FenceRx();
        [GeneratedRegex(@"^(#{1,6})(?:[ \t]+(.*))?$")]
        private static partial Regex AtxRx();
        [GeneratedRegex(@"[ \t]+#+[ \t]*$|^#+[ \t]*$")]
        private static partial Regex ClosingHashesRx();
        [GeneratedRegex(@"^(?:(?:\*[ \t]*){3,}|(?:-[ \t]*){3,}|(?:_[ \t]*){3,})$")]
        private static partial Regex HrRx();
        [GeneratedRegex(@"^( {0,12})([-*+]|\d{1,9}[.)])(?:[ \t]+(.*)|$)")]
        private static partial Regex ListRx();
        [GeneratedRegex(@"^\[( |x|X)\][ \t]+")]
        private static partial Regex TaskRx();
        [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
        private static partial Regex TableSepRx();
        [GeneratedRegex(@"^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]$", RegexOptions.IgnoreCase)]
        private static partial Regex AlertRx();
        [GeneratedRegex(@"^(?:`{3,}|~{3,}|#{1,6}(?:\s|$)|>|[-*+][ \t]|\d{1,9}[.)][ \t])")]
        private static partial Regex BlockStartRx();
        [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!|~<>])")]
        private static partial Regex EscapeRx();
        [GeneratedRegex(@"(`+)(.+?)\1", RegexOptions.Singleline)]
        private static partial Regex CodeSpanRx();
        [GeneratedRegex(@"!\[([^\]\n]*)\]\(\s*([^\s)]+)(?:\s+""([^""]*)"")?\s*\)")]
        private static partial Regex ImageRx();
        [GeneratedRegex(@"\[([^\]\n]+)\]\(\s*([^\s)]+)(?:\s+""([^""]*)"")?\s*\)")]
        private static partial Regex LinkRx();
        [GeneratedRegex(@"<(https?://[^\s<>""]+)>")]
        private static partial Regex AutoLinkRx();
        [GeneratedRegex(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", RegexOptions.Singleline)]
        private static partial Regex StrongRx();
        [GeneratedRegex(@"(?<![\w*])(\*|_)(?=\S)(.+?)(?<=\S)\1(?![\w*])", RegexOptions.Singleline)]
        private static partial Regex EmRx();
        [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Singleline)]
        private static partial Regex DelRx();
        [GeneratedRegex(@"(?<![\w/=""'\u0001])https?://[^\s<\u0001\u0002]+[^\s<.,;:!?)\]'""\u0001\u0002]")]
        private static partial Regex BareUrlRx();
        [GeneratedRegex(@"(?: {2,}|\\)\n")]
        private static partial Regex HardBreakRx();
        [GeneratedRegex(@"\u0001(\d+)\u0002")]
        private static partial Regex SlotRx();
    }
}
