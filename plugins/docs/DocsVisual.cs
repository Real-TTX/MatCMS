using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Docs;

/// <summary>
/// The way back from the visual editor to Markdown. The editor shows the blocks <see cref="DocsMarkdown.ToEditor"/> drew; a block nobody touched
/// comes back as the Markdown it was (so a switch back and forth changes nothing), a touched or new block comes back as its HTML and is written
/// as Markdown HERE — on the server, in C#, testable, and the only place that knows the syntax besides <see cref="DocsMarkdown"/>.
/// <para>The HTML is what a browser's <c>innerHTML</c> gives for a contenteditable area: balanced, attributes quoted, entities encoded. The small
/// reader below builds the tree exactly as it stands (no HTML repair rules) and the writer knows the elements the editor makes, plus what a
/// browser produces on its own (<c>b</c>, <c>i</c>, <c>div</c>, <c>span</c>, <c>&amp;nbsp;</c>). Anything else gives its text.</para>
/// </summary>
public static partial class DocsVisual
{
    /// <summary>
    /// The Markdown of the editor's state: <c>{"lead":"…","blocks":[{"raw":"…","i":n}|{"html":"…"}]}</c> — raw (with its place <c>i</c> in the text it
    /// came from) for a block that is as it was, html for one that changed or is new. Two untouched blocks that were neighbours keep exactly what stood
    /// between them (even no blank line); anywhere else — next to a written block, or where a block was deleted between them — a blank line is made sure.
    /// </summary>
    public static string FromEditor(JsonNode? state)
    {
        var sb = new StringBuilder(Str(state?["lead"]));
        var any = false;
        var lastWritten = false;
        var lastIndex = -2;
        // A written block takes the text's line ends: a browser posts a form's text with "\r\n", and the untouched blocks keep what they had.
        var nl = state?["blocks"] is JsonArray all && all.OfType<JsonObject>().Any(b => Str(b["raw"]).Contains("\r\n")) ? "\r\n" : "\n";
        if (state?["blocks"] is JsonArray blocks)
            foreach (var b in blocks.OfType<JsonObject>())
            {
                string piece;
                var index = -2;
                var adjacent = false;
                if (b["raw"] is JsonValue)
                {
                    piece = Str(b["raw"]);
                    index = b["i"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : -2;
                    adjacent = !lastWritten && index >= 0 && index == lastIndex + 1;
                    lastWritten = false;
                }
                else
                {
                    var md = ToMarkdown(Str(b["html"]));
                    if (md.Trim().Length == 0) continue;
                    piece = (md + "\n\n").Replace("\n", nl);
                    lastWritten = true;
                }
                if (piece.Length == 0) continue;
                if (any && !adjacent && !EndsWithBlankLine(sb)) sb.Append(sb.Length > 0 && sb[^1] == '\n' ? nl : nl + nl);
                sb.Append(piece);
                any = true;
                lastIndex = index;
            }
        if (lastWritten) { while (sb.Length > 0 && sb[^1] is '\n' or '\r') sb.Length--; sb.Append(nl); }
        return sb.ToString();
    }

    /// <summary>The editor's state as JSON for the browser: the lead and every block with its raw Markdown, its place and its HTML.</summary>
    public static JsonObject ToJson(DocsMarkdown.EditorDoc doc) => new()
    {
        ["lead"] = doc.Lead,
        ["blocks"] = new JsonArray(doc.Blocks.Select((b, i) => (JsonNode)new JsonObject { ["raw"] = b.Raw, ["i"] = i, ["html"] = b.Html }).ToArray())
    };

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static bool EndsWithBlankLine(StringBuilder sb) => BlankEndRx().IsMatch(sb.ToString(Math.Max(0, sb.Length - 64), Math.Min(64, sb.Length)));

    /// <summary>HTML of one or more blocks → Markdown (blocks set apart by a blank line).</summary>
    public static string ToMarkdown(string? html) => string.Join("\n\n", new Writer().Blocks(Parse(html ?? "").Kids));

    // ---- the reader ------------------------------------------------------------------------------------------------

    internal sealed class Node
    {
        public string? Tag;                       // null = text
        public string Text = "";
        public readonly Dictionary<string, string> Attr = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<Node> Kids = new();
        public Node? Parent;
        public string A(string name) => Attr.TryGetValue(name, out var v) ? v : "";
        public bool Has(string name) => Attr.ContainsKey(name);
        public bool HasClass(string c) => A("class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(c);
        public string? ClassStarting(string prefix) => A("class").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        public IEnumerable<Node> Elements => Kids.Where(k => k.Tag is not null);
        public Node? First(string tag) => Kids.Select(k => k.Tag == tag ? k : k.First(tag)).FirstOrDefault(k => k is not null);
    }

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal) { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr" };
    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal) { "script", "style", "template", "noscript", "iframe", "object", "svg", "math", "button", "select", "textarea" };

    /// <summary>The tree of an HTML piece, as written: an end tag closes the nearest open element of its name, an unknown end tag is skipped.</summary>
    internal static Node Parse(string html)
    {
        var root = new Node { Tag = "#root" };
        var cur = root;
        var i = 0;
        var text = new StringBuilder();
        void FlushText()
        {
            if (text.Length == 0) return;
            var t = WebUtility.HtmlDecode(text.ToString());
            text.Clear();
            if (cur.Kids.Count > 0 && cur.Kids[^1].Tag is null) cur.Kids[^1].Text += t;
            else cur.Kids.Add(new Node { Text = t, Parent = cur });
        }
        while (i < html.Length)
        {
            var c = html[i];
            if (c == '<' && i + 1 < html.Length)
            {
                var n = html[i + 1];
                if (html.AsSpan(i).StartsWith("<!--"))
                {
                    FlushText();
                    var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = end < 0 ? html.Length : end + 3;
                    continue;
                }
                if (n is '!' or '?')
                {
                    FlushText();
                    var end = html.IndexOf('>', i);
                    i = end < 0 ? html.Length : end + 1;
                    continue;
                }
                if (n == '/' && i + 2 < html.Length && char.IsAsciiLetter(html[i + 2]))
                {
                    FlushText();
                    var end = html.IndexOf('>', i);
                    var name = html[(i + 2)..(end < 0 ? html.Length : end)].Trim().ToLowerInvariant();
                    i = end < 0 ? html.Length : end + 1;
                    for (var o = cur; o is not null && o != root; o = o.Parent)
                        if (o.Tag == name) { cur = o.Parent ?? root; break; }
                    continue;
                }
                if (char.IsAsciiLetter(n))
                {
                    FlushText();
                    var (node, next, selfClosing) = StartTag(html, i);
                    i = next;
                    if (Dropped.Contains(node.Tag!))
                    {
                        // its content is skipped with it
                        if (!selfClosing)
                        {
                            var close = html.IndexOf("</" + node.Tag, i, StringComparison.OrdinalIgnoreCase);
                            var gt = close < 0 ? -1 : html.IndexOf('>', close);
                            i = gt < 0 ? html.Length : gt + 1;
                        }
                        continue;
                    }
                    node.Parent = cur;
                    cur.Kids.Add(node);
                    if (!selfClosing && !Void.Contains(node.Tag!)) cur = node;
                    continue;
                }
            }
            text.Append(c);
            i++;
        }
        FlushText();
        return root;
    }

    private static (Node Node, int Next, bool SelfClosing) StartTag(string html, int i)
    {
        var k = i + 1;
        while (k < html.Length && (char.IsAsciiLetterOrDigit(html[k]) || html[k] == '-')) k++;
        var node = new Node { Tag = html[(i + 1)..k].ToLowerInvariant() };
        var selfClosing = false;
        while (k < html.Length)
        {
            var c = html[k];
            if (c == '>') { k++; break; }
            if (c == '/' && k + 1 < html.Length && html[k + 1] == '>') { selfClosing = true; k += 2; break; }
            if (char.IsWhiteSpace(c) || c == '/') { k++; continue; }
            var s = k;
            while (k < html.Length && !char.IsWhiteSpace(html[k]) && html[k] is not ('=' or '>' or '/')) k++;
            var name = html[s..k].ToLowerInvariant();
            var value = "";
            while (k < html.Length && char.IsWhiteSpace(html[k])) k++;
            if (k < html.Length && html[k] == '=')
            {
                k++;
                while (k < html.Length && char.IsWhiteSpace(html[k])) k++;
                if (k < html.Length && html[k] is '"' or '\'')
                {
                    var q = html[k];
                    var e = html.IndexOf(q, k + 1);
                    if (e < 0) e = html.Length;
                    value = html[(k + 1)..e];
                    k = Math.Min(html.Length, e + 1);
                }
                else
                {
                    var vs = k;
                    while (k < html.Length && !char.IsWhiteSpace(html[k]) && html[k] != '>') k++;
                    value = html[vs..k];
                }
            }
            if (name.Length > 0) node.Attr[name] = WebUtility.HtmlDecode(value);
        }
        return (node, k, selfClosing);
    }

    // ---- the writer ------------------------------------------------------------------------------------------------

    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "pre", "blockquote", "table", "hr", "section", "article", "header", "footer",
        "aside", "nav", "main", "figure", "figcaption", "details", "summary", "dl", "dt", "dd", "address", "center"
    };

    private static readonly string[] AlertKinds = ["note", "tip", "important", "warning", "caution"];
    private const char Nbsp = (char)0xA0;
    private static readonly string Zwsp = ((char)0x200B).ToString();

    private static bool IsBlock(Node n) => n.Tag is { } t && BlockTags.Contains(t);

    private sealed class Writer
    {
        /// <summary>A sequence of nodes as Markdown blocks: block elements each on their own, the inline nodes between them as paragraphs.</summary>
        public List<string> Blocks(IEnumerable<Node> nodes)
        {
            var list = new List<string>();
            var run = new List<Node>();
            void Flush()
            {
                if (run.Count == 0) return;
                var p = Paragraph(run);
                if (p.Length > 0) list.Add(p);
                run.Clear();
            }
            foreach (var n in nodes)
            {
                if (IsBlock(n)) { Flush(); list.AddRange(Block(n)); }
                else run.Add(n);
            }
            Flush();
            return list;
        }

        private IEnumerable<string> Block(Node n)
        {
            switch (n.Tag)
            {
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                {
                    var text = OneLine(Inline(n.Kids, inLine: true));
                    if (text.Length == 0) return [];
                    if (text.EndsWith('#')) text = text[..^1] + "\\#";          // or it would be read as closing hashes
                    return [new string('#', n.Tag[1] - '0') + " " + text];
                }
                case "p":
                    if (n.HasClass("docs-callout-title")) return [];
                    var para = Paragraph(n.Kids);
                    return para.Length == 0 ? [] : [para];
                case "hr": return ["---"];
                case "pre": return [Code(n, n.Parent?.A("data-lang"))];
                case "blockquote": return Quoted(Blocks(n.Kids), null);
                case "ul": case "ol": return List(n);
                case "table": return Table(n) is { } t ? [t] : [];
                case "div":
                    if (n.HasClass("docs-code-head")) return [];
                    if (n.HasClass("docs-callout") || n.Has("data-kind"))
                    {
                        var kind = n.A("data-kind").ToLowerInvariant();
                        if (!AlertKinds.Contains(kind)) kind = n.ClassStarting("docs-callout-") is { } k && AlertKinds.Contains(k) ? k : "note";
                        return Quoted(Blocks(n.Kids), kind);
                    }
                    if (n.HasClass("docs-code") && n.First("pre") is { } pre) return [Code(pre, n.A("data-lang"))];
                    // A plain div: what a browser makes for a new line — a paragraph, unless it holds blocks of its own.
                    return n.Kids.Any(IsBlock) ? Blocks(n.Kids) : Paragraph(n.Kids) is { Length: > 0 } dp ? [dp] : [];
                default:
                    return n.Kids.Any(IsBlock) ? Blocks(n.Kids) : Paragraph(n.Kids) is { Length: > 0 } other ? [other] : [];
            }
        }

        private static IEnumerable<string> Quoted(List<string> blocks, string? alert)
        {
            var inner = string.Join("\n\n", blocks);
            var lines = new List<string>();
            if (alert is not null) lines.Add("> [!" + alert.ToUpperInvariant() + "]");
            else if (inner.Trim().Length == 0) return [];
            if (inner.Length > 0) lines.AddRange(inner.Split('\n').Select(l => l.Length == 0 ? ">" : "> " + l));
            return [string.Join("\n", lines)];
        }

        /// <summary>A paragraph: its inline Markdown, every line start that a reader would take for a block escaped, no line indented.</summary>
        private string Paragraph(IEnumerable<Node> nodes)
        {
            var text = Inline(nodes, inLine: false);
            var lines = text.Split('\n').Select(l => EscapeLineStart(l.Trim(' ', '\t'))).ToList();
            // no empty lines inside one paragraph (they would end it), no hard break at its very end
            var s = string.Join("\n", lines.Where(l => l.Length > 0)).Trim();
            while (s.EndsWith('\\') && !s.EndsWith("\\\\", StringComparison.Ordinal)) s = s[..^1].TrimEnd();
            return s;
        }

        private static string EscapeLineStart(string line)
        {
            if (line.Length == 0) return line;
            if (line[0] is '#' or '>' or '|' or '=') return "\\" + line;
            if (line[0] is '-' or '+') return "\\" + line;
            var m = OrderedStartRx().Match(line);
            if (m.Success) return m.Groups[1].Value + "\\" + line[m.Groups[1].Length..];
            return line;
        }

        private static string OneLine(string s) => SpacesRx().Replace(s.Replace("\\\n", " ").Replace('\n', ' '), " ").Trim();

        private string Code(Node pre, string? lang)
        {
            var code = pre.First("code");
            var language = (lang ?? "").Trim();
            if (language.Length == 0) language = code?.ClassStarting("language-") ?? pre.A("data-lang");
            language = new string(language.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '+' or '#' or '.').ToArray()).ToLowerInvariant();
            var sb = new StringBuilder();
            PlainText(code ?? pre, sb);
            var text = sb.ToString().Replace("\r\n", "\n").Replace(Nbsp, ' ').Replace(Zwsp, "");
            if (text.EndsWith('\n')) text = text[..^1];
            var longest = 0;
            foreach (var line in text.Split('\n'))
            {
                var t = line.TrimStart();
                var run = 0;
                while (run < t.Length && t[run] == '`') run++;
                longest = Math.Max(longest, run);
            }
            var fence = new string('`', Math.Max(3, longest + 1));
            return fence + language + "\n" + text + "\n" + fence;
        }

        /// <summary>The text of an element as it stands, a line break for every br and between block children (a browser splits a pre into divs).</summary>
        private static void PlainText(Node n, StringBuilder sb)
        {
            foreach (var k in n.Kids)
            {
                if (k.Tag is null) sb.Append(k.Text);
                else if (k.Tag == "br") sb.Append('\n');
                else if (IsBlock(k))
                {
                    if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                    PlainText(k, sb);
                    if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                }
                else PlainText(k, sb);
            }
        }

        private IEnumerable<string> List(Node list)
        {
            var ordered = list.Tag == "ol";
            var no = ordered && int.TryParse(list.A("start"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s >= 0 ? s : 1;
            // The items; a list a browser put straight into the list (indenting) belongs to the item before it.
            var items = new List<List<Node>>();
            foreach (var k in list.Kids)
            {
                if (k.Tag == "li") items.Add(new List<Node>(k.Kids) { });
                else if (k.Tag is "ul" or "ol" && items.Count > 0) items[^1].Add(k);
                else if (k.Tag is null && k.Text.Trim().Length == 0) continue;
                else if (k.Tag is "ul" or "ol") items.Add(new List<Node> { k });
                else items.Add(new List<Node> { k });
            }
            var taskClass = list.Kids.Where(k => k.Tag == "li").Select(k => k.HasClass("docs-task")).ToList();
            var written = new List<(string Marker, string Task, List<string> Blocks, bool Loose)>();
            var index = 0;
            foreach (var item in items)
            {
                var kids = item;
                // a task: the item's first element is a checkbox
                var task = "";
                var box = kids.FirstOrDefault(k => !(k.Tag is null && k.Text.Trim().Length == 0));
                if (box is { Tag: "input" } && box.A("type").Equals("checkbox", StringComparison.OrdinalIgnoreCase))
                {
                    task = box.Has("checked") ? "[x] " : "[ ] ";
                    kids = kids.Where(k => k != box).ToList();
                    if (kids.FirstOrDefault() is { Tag: null } lead) lead.Text = lead.Text.TrimStart();
                }
                else if (index < taskClass.Count && taskClass[index]) task = "[ ] ";
                index++;
                // loose = the item's text is a paragraph of its own (a tight item may still hold a paragraph AFTER a nested block)
                var loose = kids.FirstOrDefault(k => !(k.Tag is null && k.Text.Trim().Length == 0)) is { Tag: "p" };
                // the text before the first block is the item's first line(s); the blocks follow
                var inline = kids.TakeWhile(k => !IsBlock(k)).ToList();
                var rest = kids.Skip(inline.Count).ToList();
                var blocks = new List<string>();
                var first = Paragraph(inline);
                if (loose && first.Length == 0 && rest.FirstOrDefault() is { Tag: "p" } p1)
                {
                    first = Paragraph(p1.Kids);
                    rest = rest.Skip(1).ToList();
                }
                blocks.Add(first);
                blocks.AddRange(Blocks(rest));
                var marker = ordered ? (no++).ToString(CultureInfo.InvariantCulture) + "." : "-";
                written.Add((marker, task, blocks, loose));
            }
            var anyLoose = written.Any(w => w.Loose);
            var sb = new StringBuilder();
            foreach (var (marker, task, blocks, _) in written)
            {
                if (sb.Length > 0) sb.Append(anyLoose ? "\n\n" : "\n");
                var pad = new string(' ', marker.Length + 1);
                var body = string.Join(anyLoose ? "\n\n" : "\n", blocks.Where((b, k) => k == 0 || b.Length > 0));
                var lines = body.Split('\n');
                sb.Append(marker).Append(' ').Append(task).Append(lines[0]);
                foreach (var l in lines.Skip(1)) sb.Append('\n').Append(l.Length == 0 ? "" : pad + l);
            }
            var md = sb.ToString().TrimEnd();
            return md.Length == 0 ? [] : [md];
        }

        private string? Table(Node table)
        {
            var rows = new List<Node>();
            void Collect(Node n) { foreach (var k in n.Elements) { if (k.Tag == "tr") rows.Add(k); else if (k.Tag is "thead" or "tbody" or "tfoot") Collect(k); } }
            Collect(table);
            if (rows.Count == 0) return null;
            var cells = rows.Select(r => r.Elements.Where(c => c.Tag is "td" or "th").ToList()).ToList();
            var width = cells.Max(c => c.Count);
            if (width == 0) return null;
            string Cell(Node c)
            {
                var t = OneLine(Inline(c.Kids, inLine: true)).Replace("|", "\\|");
                return t.Length == 0 ? " " : t;
            }
            string Align(Node? c)
            {
                var style = (c?.A("style") ?? "").Replace(" ", "").ToLowerInvariant();
                var a = c?.A("align").ToLowerInvariant() ?? "";
                if (style.Contains("text-align:center") || a == "center") return ":---:";
                if (style.Contains("text-align:right") || a == "right") return "---:";
                if (style.Contains("text-align:left") || a == "left") return ":---";
                return "---";
            }
            var sb = new StringBuilder();
            for (var r = 0; r < cells.Count; r++)
            {
                sb.Append('|');
                for (var k = 0; k < width; k++) sb.Append(' ').Append(k < cells[r].Count ? Cell(cells[r][k]) : " ").Append(" |");
                sb.Append('\n');
                if (r == 0)
                {
                    sb.Append('|');
                    for (var k = 0; k < width; k++) sb.Append(' ').Append(Align(k < cells[0].Count ? cells[0][k] : null)).Append(" |");
                    sb.Append('\n');
                }
            }
            return sb.ToString().TrimEnd('\n');
        }

        // ---- inline ---------------------------------------------------------------------------------------------------

        /// <summary>Inline Markdown of nodes. <paramref name="inLine"/>: in a heading or a table cell, where a line break can only be a space.</summary>
        private string Inline(IEnumerable<Node> nodes, bool inLine)
        {
            var sb = new StringBuilder();
            foreach (var n in nodes)
            {
                switch (n.Tag)
                {
                    // a no-break space is what a browser types for a second space; the zero-width one is the editor's caret helper
                    case null: sb.Append(Escape(n.Text.Replace(Nbsp, ' ').Replace(Zwsp, ""))); break;
                    case "br": sb.Append(inLine ? " " : "\\\n"); break;
                    case "strong": case "b": sb.Append(Wrap("**", Inline(n.Kids, inLine))); break;
                    case "em": case "i": sb.Append(Wrap("_", Inline(n.Kids, inLine))); break;
                    case "del": case "s": case "strike": sb.Append(Wrap("~~", Inline(n.Kids, inLine))); break;
                    case "code": sb.Append(CodeSpan(n)); break;
                    case "a": sb.Append(Link(n, inLine)); break;
                    case "img": sb.Append(Image(n)); break;
                    case "input": break;
                    default:
                        if (n.HasClass("docs-code-head") || n.HasClass("docs-callout-title")) break;
                        var inner = Inline(n.Kids, inLine);
                        if (IsBlock(n) && !inLine && inner.Length > 0) { if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n'); sb.Append(inner).Append('\n'); }
                        else sb.Append(inner);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Markers around text, the spaces at its ends moved outside (a marker next to a space is not one).</summary>
        private static string Wrap(string marker, string inner)
        {
            var core = inner.Trim(' ', '\n');
            if (core.Length == 0) return inner;
            var lead = inner[..inner.IndexOf(core, StringComparison.Ordinal)];
            var trail = inner[(lead.Length + core.Length)..];
            return lead + marker + core + marker + trail;
        }

        private static string CodeSpan(Node n)
        {
            var sb = new StringBuilder();
            PlainText(n, sb);
            var t = sb.ToString().Replace('\n', ' ').Replace(Nbsp, ' ').Replace(Zwsp, "");
            if (t.Trim().Length == 0) return t;
            t = KeepBackslashes(t);
            var longest = 0;
            for (int k = 0, run = 0; k < t.Length; k++) { run = t[k] == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
            var fence = new string('`', longest + 1);
            return longest > 0 ? fence + " " + t + " " + fence : fence + t + fence;
        }

        /// <summary>The reader takes a backslash before a punctuation mark as an escape even in code and addresses: such a backslash is doubled.</summary>
        private static string KeepBackslashes(string t)
        {
            if (!t.Contains('\\')) return t;
            var sb = new StringBuilder(t.Length + 4);
            for (var k = 0; k < t.Length; k++)
            {
                sb.Append(t[k]);
                if (t[k] == '\\' && k + 1 < t.Length && Escapable.Contains(t[k + 1])) sb.Append('\\');
            }
            return sb.ToString();
        }

        private const string Escapable = "\\`*_{}[]()#+-.!|~<>";

        private string Link(Node a, bool inLine)
        {
            var href = a.A("href").Trim();
            var label = OneLine(Inline(a.Kids, inLine: true));
            var isDoc = href.StartsWith("doc:", StringComparison.OrdinalIgnoreCase);
            if (href.Length == 0 || (!isDoc && !DocsMarkdown.SafeUrl(href))) return label;
            var title = a.A("title").Replace("\"", "").Replace('\n', ' ');
            var plainLabel = WebUtility.HtmlDecode(PlainOf(a)).Trim();
            if (!isDoc && title.Length == 0 && plainLabel == href && AutoLinkRx().IsMatch("<" + href + ">")) return "<" + href + ">";
            if (label.Length == 0) label = Escape(href);
            return "[" + label + "](" + Address(href) + (title.Length > 0 ? " \"" + title + "\"" : "") + ")";
        }

        private static string Image(Node img)
        {
            var src = img.A("src").Trim();
            var alt = img.A("alt").Replace("]", "").Replace("[", "").Replace('\n', ' ');
            if (src.Length == 0 || !DocsMarkdown.SafeUrl(src)) return "";
            var title = img.A("title").Replace("\"", "").Replace('\n', ' ');
            return "![" + KeepBackslashes(alt) + "](" + Address(src) + (title.Length > 0 ? " \"" + title + "\"" : "") + ")";
        }

        /// <summary>An address as the reader reads it: no space, no ")" (both would end it), backslashes kept.</summary>
        private static string Address(string url) => KeepBackslashes(url.Replace(" ", "%20").Replace(")", "%29"));

        private static string PlainOf(Node n)
        {
            var sb = new StringBuilder();
            PlainText(n, sb);
            return sb.ToString();
        }

        /// <summary>Text as Markdown: every character that would start markup is escaped (an underscore inside a word is left alone).</summary>
        private static string Escape(string t)
        {
            var sb = new StringBuilder(t.Length + 8);
            for (var k = 0; k < t.Length; k++)
            {
                var c = t[k];
                var prev = k > 0 ? t[k - 1] : ' ';
                var next = k + 1 < t.Length ? t[k + 1] : ' ';
                switch (c)
                {
                    case '\\' or '`' or '*' or '[' or ']': sb.Append('\\').Append(c); break;
                    case '_': sb.Append(char.IsLetterOrDigit(prev) && char.IsLetterOrDigit(next) ? "_" : "\\_"); break;
                    case '~': sb.Append(prev == '~' || next == '~' ? "\\~" : "~"); break;
                    case '<': sb.Append(t.AsSpan(k + 1).StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "\\<" : "<"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }

    [GeneratedRegex(@"\n[ \t]*\r?\n[ \t]*$")]
    private static partial Regex BlankEndRx();
    [GeneratedRegex(@"^(\d{1,9})[.)](?:[ \t]|$)")]
    private static partial Regex OrderedStartRx();
    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpacesRx();
    [GeneratedRegex(@"^<(https?://[^\s<>""]+)>$")]
    private static partial Regex AutoLinkRx();
}
