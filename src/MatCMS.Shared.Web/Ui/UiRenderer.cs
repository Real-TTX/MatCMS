using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MatCMS.Plugin;

namespace MatCMS.Shared.Web;

/// <summary>Where a tree is drawn.</summary>
public enum UiTarget
{
    /// <summary>The back office: the admin's own classes, scripts and antiforgery.</summary>
    Admin,
    /// <summary>A public page: the site's classes, so the template styles it.</summary>
    Public
}

/// <summary>What the renderer needs from its host.</summary>
public sealed class UiRenderOptions
{
    /// <summary>Back office or public site.</summary>
    public UiTarget Target { get; init; } = UiTarget.Admin;
    /// <summary>The hidden antiforgery input added to every form of an admin screen. Empty on public screens.</summary>
    public string Antiforgery { get; init; } = "";
    /// <summary>
    /// The library's own wording (rich-text toolbar, image picker, tag input, …), looked up by key. The renderer
    /// cannot reference an application's localiser — the same reason the other shared views take their text as
    /// strings. Unknown keys fall back to the key itself, which shows up on screen as the bug it is.
    /// </summary>
    public Func<string, string> T { get; init; } = k => k;
}

/// <summary>
/// Draws a <see cref="UiNode"/> tree as HTML. The one place that knows which classes, scripts and
/// <c>data-</c> hooks the admin (or the site) uses — a plugin names a component, never a class.
/// <para>Every text and attribute value is encoded; the exceptions are <see cref="UiHtml"/> (by contract) and
/// the antiforgery input the host passes in. Link targets are limited to local paths, http(s), mailto and tel.</para>
/// </summary>
public static class UiRenderer
{
    private static readonly Regex DataKey = new("^[a-z][a-z0-9-]*$", RegexOptions.Compiled);

    /// <summary>Renders a tree.</summary>
    public static string Render(UiNode root, UiRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var w = new Writer(options ?? new UiRenderOptions());
        w.Node(root);
        return w.ToString();
    }

    /// <summary>A link or picture target that is safe to put in <c>href</c>/<c>src</c>: a local path, http(s), mailto, tel
    /// or an in-page anchor; anything else (<c>javascript:</c>, <c>data:</c>, …) becomes "#".</summary>
    public static string SafeUrl(string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) return "#";
        if (u.StartsWith('#') || u.StartsWith('?')) return u;
        if (u.StartsWith("//") || u.StartsWith("/\\")) return "#";
        if (u.StartsWith('/')) return u;
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            return u;
        // A relative path such as "products/3": fine as long as it has no scheme.
        return Regex.IsMatch(u, @"^[a-zA-Z][a-zA-Z0-9+.-]*:") ? "#" : u;
    }

    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();
        private readonly UiRenderOptions _o;
        private bool Admin => _o.Target == UiTarget.Admin;

        public Writer(UiRenderOptions o) => _o = o;
        public override string ToString() => _sb.ToString();

        // Everything but the characters HTML itself uses is let through as it is — the pages are UTF-8, and "l&#228;uft" helps nobody.
        private static readonly System.Text.Encodings.Web.HtmlEncoder Encoder = System.Text.Encodings.Web.HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All);
        private static string E(string? s) => Encoder.Encode(s ?? "");
        private string S(string key) => E(_o.T(key));
        private string Sraw(string key) => _o.T(key);

        private void Children(IEnumerable<UiNode> nodes) { foreach (var n in nodes) Node(n); }

        private static string FieldId(UiField f) => !string.IsNullOrWhiteSpace(f.Id) ? f.Id! : "f-" + Regex.Replace(f.Name ?? "", "[^a-zA-Z0-9_-]", "-");

        private void Icon(string? icon)
        {
            if (!string.IsNullOrWhiteSpace(icon) && icon!.StartsWith("ti-", StringComparison.Ordinal) && Regex.IsMatch(icon, "^ti-[a-z0-9-]+$"))
                _sb.Append("<i class=\"ti ").Append(icon).Append("\" aria-hidden=\"true\"></i> ");
        }

        public void Node(UiNode node)
        {
            switch (node)
            {
                case UiStack s: _sb.Append("<div class=\"ui-stack\">"); Children(s.Children); _sb.Append("</div>"); break;
                case UiInline i: _sb.Append("<div class=\"ui-inline\">"); Children(i.Children); _sb.Append("</div>"); break;
                case UiGrid g:
                    _sb.Append("<div class=\"ui-grid\" style=\"grid-template-columns:repeat(auto-fill,minmax(")
                        .Append(Math.Clamp(g.MinWidth, 80, 1200)).Append("px,1fr))\">");
                    Children(g.Children); _sb.Append("</div>"); break;
                case UiSplit sp: Split(sp); break;
                case UiColumns cols:
                    _sb.Append("<div class=\"ui-cols\"><div class=\"ui-cols-main\">"); Children(cols.Main); _sb.Append("</div><aside class=\"ui-cols-side\">"); Children(cols.Side); _sb.Append("</aside></div>"); break;
                case UiCard c:
                    _sb.Append(Admin ? "<div class=\"card\">" : "<div class=\"ui-card\">");
                    if (!string.IsNullOrWhiteSpace(c.Title)) _sb.Append("<h2>").Append(E(c.Title)).Append("</h2>");
                    Children(c.Children); _sb.Append("</div>"); break;
                case UiPageHead h: PageHead(h); break;
                case UiHeading h:
                {
                    var l = Math.Clamp(h.Level, 2, 4);
                    _sb.Append("<h").Append(l).Append('>').Append(E(h.Text)).Append("</h").Append(l).Append('>'); break;
                }
                case UiText t:
                {
                    var cls = string.Join(' ', new[] { t.Muted ? "muted" : null, t.Mono ? "mono" : null }.Where(x => x is not null));
                    var tag = t.Inline ? "span" : "p";
                    _sb.Append('<').Append(tag);
                    if (cls.Length > 0) _sb.Append(" class=\"").Append(cls).Append('"');
                    _sb.Append('>');
                    if (t.Strong) _sb.Append("<strong>").Append(E(t.Text)).Append("</strong>"); else _sb.Append(E(t.Text));
                    _sb.Append("</").Append(tag).Append('>'); break;
                }
                case UiLink l:
                    _sb.Append("<a href=\"").Append(E(SafeUrl(l.Href))).Append('"');
                    if (l.NewTab) _sb.Append(" target=\"_blank\" rel=\"noopener\"");
                    _sb.Append('>').Append(E(l.Text)).Append("</a>"); break;
                case UiImg im:
                {
                    _sb.Append("<img src=\"").Append(E(SafeUrl(im.Src))).Append("\" alt=\"").Append(E(im.Alt)).Append("\" loading=\"lazy\"");
                    if (im.Size > 0)
                        _sb.Append(" width=\"").Append(im.Size).Append("\" height=\"").Append(im.Size)
                           .Append("\" style=\"object-fit:cover;border-radius:6px;vertical-align:middle;flex:none;min-width:").Append(im.Size).Append("px;max-width:none\"");
                    else _sb.Append(" style=\"max-width:100%\"");
                    _sb.Append(" />"); break;
                }
                case UiHtml raw: _sb.Append(raw.Html); break;
                case UiAlert a:
                {
                    var tone = a.Tone switch { UiTone.Success => "success", UiTone.Error => "error", UiTone.Warning => Admin ? "warning" : "info", _ => "info" };
                    _sb.Append("<div class=\"alert alert-").Append(tone).Append("\">").Append(E(a.Text)).Append("</div>"); break;
                }
                case UiBadge b:
                {
                    var cls = b.Tone switch { UiTone.Success => " badge-on", UiTone.Error => " badge-err", UiTone.Warning => " badge-warn", UiTone.Info => " badge-new", UiTone.Muted => " badge-off", _ => "" };
                    _sb.Append("<span class=\"badge").Append(cls).Append("\">").Append(E(b.Text)).Append("</span>"); break;
                }
                case UiEmpty e: _sb.Append("<p class=\"list-empty\">").Append(E(e.Text)).Append("</p>"); break;
                case UiChips ch:
                    foreach (var item in ch.Items) _sb.Append("<span class=\"mini-chip\">").Append(E(item)).Append("</span> ");
                    break;
                case UiKeyValue kv:
                    foreach (var item in kv.Items)
                        _sb.Append("<div class=\"kv\"><span class=\"kv-label\">").Append(E(item.Label)).Append("</span><span class=\"kv-value\">")
                           .Append(E(item.Value)).Append("</span></div>");
                    break;
                case UiStats st: Stats(st); break;
                case UiButton b: Button(b); break;
                case UiAction a: ActionButton(a); break;
                case UiDialog d: Dialog(d); break;
                case UiForm f: Form(f); break;
                case UiFormActions fa: _sb.Append("<div class=\"form-actions\">"); Children(fa.Children); _sb.Append("</div>"); break;
                case UiHidden hid:
                    _sb.Append("<input type=\"hidden\" name=\"").Append(E(hid.Name)).Append("\" value=\"").Append(E(hid.Value)).Append("\" />"); break;
                case UiField field: Field(field); break;
                case UiTable t: Table(t); break;
                case UiTabs tabs: Tabs(tabs); break;
                case UiPager p: Pager(p); break;
                default: throw new NotSupportedException($"The renderer does not know {node.GetType().Name}.");
            }
        }

        private static readonly Regex IconName = new("^ti-[a-z0-9-]+$", RegexOptions.Compiled);

        private void Split(UiSplit sp)
        {
            _sb.Append("<div class=\"ui-split\"><nav class=\"ui-subnav\">");
            foreach (var i in sp.Nav)
            {
                if (!string.IsNullOrWhiteSpace(i.Heading)) _sb.Append("<div class=\"ui-subnav-label\">").Append(E(i.Heading)).Append("</div>");
                _sb.Append("<a href=\"").Append(E(SafeUrl(i.Href))).Append('"').Append(i.Active ? " class=\"active\" aria-current=\"page\"" : "").Append('>');
                if (i.Icon is { } ic && IconName.IsMatch(ic)) _sb.Append("<i class=\"ti ").Append(ic).Append("\" aria-hidden=\"true\"></i> ");
                _sb.Append(E(i.Label));
                if (!string.IsNullOrWhiteSpace(i.Badge)) _sb.Append("<span class=\"ui-subnav-badge\">").Append(E(i.Badge)).Append("</span>");
                _sb.Append("</a>");
            }
            _sb.Append("</nav><div class=\"ui-split-body\">"); Children(sp.Children); _sb.Append("</div></div>");
        }

        private void PageHead(UiPageHead h)
        {
            _sb.Append("<div class=\"page-head\">");
            if (!string.IsNullOrWhiteSpace(h.Title) || !string.IsNullOrWhiteSpace(h.Subtitle))
            {
                _sb.Append("<div>");
                if (!string.IsNullOrWhiteSpace(h.Title)) _sb.Append("<h1>").Append(E(h.Title)).Append("</h1>");
                if (!string.IsNullOrWhiteSpace(h.Subtitle)) _sb.Append("<p class=\"muted\">").Append(E(h.Subtitle)).Append("</p>");
                _sb.Append("</div>");
            }
            if (h.Actions.Count > 0) { _sb.Append("<div class=\"list-toolbar\">"); Children(h.Actions); _sb.Append("</div>"); }
            _sb.Append("</div>");
        }

        private void Stats(UiStats st)
        {
            _sb.Append("<div class=\"stat-grid\">");
            foreach (var s in st.Items)
            {
                var tone = s.Tone switch { UiTone.Warning => " is-warn", UiTone.Error => " is-err", _ => "" };
                _sb.Append("<div class=\"stat").Append(tone).Append("\">");
                if (!string.IsNullOrWhiteSpace(s.Href)) _sb.Append("<a href=\"").Append(E(SafeUrl(s.Href))).Append("\">");
                _sb.Append("<div class=\"n\">").Append(E(s.Value)).Append("</div><div class=\"l\">").Append(E(s.Label)).Append("</div>");
                if (!string.IsNullOrWhiteSpace(s.Hint)) _sb.Append("<div class=\"s\">").Append(E(s.Hint)).Append("</div>");
                if (!string.IsNullOrWhiteSpace(s.Href)) _sb.Append("</a>");
                _sb.Append("</div>");
            }
            _sb.Append("</div>");
        }

        private string ButtonClass(UiButtonKind kind, bool small, bool iconOnly = false)
        {
            var sb = new StringBuilder("btn");
            if (small) sb.Append(" btn-sm");
            if (Admin)
            {
                if (kind == UiButtonKind.Ghost) sb.Append(" btn-ghost");
                else if (kind == UiButtonKind.Danger) sb.Append(" btn-danger");
            }
            else if (kind != UiButtonKind.Primary) sb.Append(" btn-outline");
            if (iconOnly) sb.Append(" btn-icon");
            return sb.ToString();
        }

        private void DataAttrs(IReadOnlyDictionary<string, string>? data)
        {
            if (data is null) return;
            foreach (var kv in data)
                if (DataKey.IsMatch(kv.Key))
                    _sb.Append(" data-").Append(kv.Key).Append("=\"").Append(E(kv.Value)).Append('"');
        }

        private void Button(UiButton b)
        {
            var iconOnly = b.IconOnly && !string.IsNullOrWhiteSpace(b.Icon);
            var cls = ButtonClass(b.Kind, b.Small, iconOnly);
            if (b.Href is not null && b.Opens is null)
            {
                _sb.Append("<a class=\"").Append(cls).Append("\" href=\"").Append(E(SafeUrl(b.Href))).Append('"');
                if (b.NewTab) _sb.Append(" target=\"_blank\" rel=\"noopener\"");
                if (iconOnly) _sb.Append(" title=\"").Append(E(b.Label)).Append("\" aria-label=\"").Append(E(b.Label)).Append('"');
                DataAttrs(b.Data);
                _sb.Append('>');
                Icon(b.Icon); if (!iconOnly) _sb.Append(E(b.Label));
                _sb.Append("</a>");
                return;
            }
            _sb.Append("<button type=\"").Append(b.Opens is null && !b.NoSubmit ? "submit" : "button").Append("\" class=\"").Append(cls).Append('"');
            if (b.Opens is not null) _sb.Append(" data-ui-opens=\"").Append(E(b.Opens)).Append('"');
            if (b.Name is not null) _sb.Append(" name=\"").Append(E(b.Name)).Append("\" value=\"").Append(E(b.Value ?? "")).Append('"');
            if (!string.IsNullOrWhiteSpace(b.Confirm)) _sb.Append(" data-confirm=\"").Append(E(b.Confirm)).Append('"');
            if (b.Kind == UiButtonKind.Danger && !string.IsNullOrWhiteSpace(b.Confirm)) _sb.Append(" data-confirm-danger");
            if (iconOnly) _sb.Append(" title=\"").Append(E(b.Label)).Append("\" aria-label=\"").Append(E(b.Label)).Append('"');
            DataAttrs(b.Data);
            _sb.Append('>');
            Icon(b.Icon); if (!iconOnly) _sb.Append(E(b.Label));
            _sb.Append("</button>");
        }

        private void ActionButton(UiAction a)
        {
            var iconOnly = !string.IsNullOrWhiteSpace(a.Icon) && !a.WithLabel;
            _sb.Append("<form method=\"post\" class=\"inline-form\"");
            if (!string.IsNullOrWhiteSpace(a.Confirm)) _sb.Append(" data-confirm=\"").Append(E(a.Confirm)).Append('"');
            if (a.Kind == UiButtonKind.Danger && !string.IsNullOrWhiteSpace(a.Confirm)) _sb.Append(" data-confirm-danger");
            _sb.Append('>').Append(_o.Antiforgery);
            foreach (var kv in a.Hidden)
                _sb.Append("<input type=\"hidden\" name=\"").Append(E(kv.Key)).Append("\" value=\"").Append(E(kv.Value)).Append("\" />");
            _sb.Append("<button type=\"submit\" class=\"").Append(ButtonClass(a.Kind, true, iconOnly)).Append('"');
            if (iconOnly) _sb.Append(" title=\"").Append(E(a.Label)).Append("\" aria-label=\"").Append(E(a.Label)).Append('"');
            _sb.Append('>');
            if (iconOnly) Icon(a.Icon); else { Icon(a.Icon); _sb.Append(E(a.Label)); }
            _sb.Append("</button></form>");
        }

        private void Dialog(UiDialog d)
        {
            _sb.Append("<dialog class=\"mat-dialog\" id=\"").Append(E(d.Id)).Append("\" data-ui-dialog><h3>").Append(E(d.Title)).Append("</h3>");
            Children(d.Children);
            _sb.Append("<div class=\"ui-dialog-foot\"><button type=\"button\" class=\"btn btn-ghost btn-sm\" data-ui-close>")
               .Append(S("ui.close")).Append("</button></div></dialog>");
        }

        private void Form(UiForm f)
        {
            _sb.Append("<form method=\"post\" class=\"form-grid").Append(f.Narrow ? " ui-form-narrow" : " ui-form").Append('"');
            if (!string.IsNullOrWhiteSpace(f.Id)) _sb.Append(" id=\"").Append(E(f.Id)).Append('"');
            if (!string.IsNullOrWhiteSpace(f.Action)) _sb.Append(" action=\"").Append(E(SafeUrl(f.Action))).Append('"');
            if (f.Upload) _sb.Append(" enctype=\"multipart/form-data\"");
            _sb.Append('>').Append(_o.Antiforgery);
            if (f.Hidden is not null)
                foreach (var kv in f.Hidden)
                    _sb.Append("<input type=\"hidden\" name=\"").Append(E(kv.Key)).Append("\" value=\"").Append(E(kv.Value)).Append("\" />");
            Children(f.Children);
            _sb.Append("</form>");
        }

        // ---- fields ---------------------------------------------------------------------------------

        private void FieldOpen(UiField f, bool labelFor = true)
        {
            _sb.Append(Admin ? "<div class=\"field\">" : "<div class=\"form-field\">");
            if (f is UiCheckbox || string.IsNullOrEmpty(f.Label)) return;   // a field with no label (a table of rows inside a card that has its title) has nothing to say above itself
            _sb.Append("<label");
            if (labelFor) _sb.Append(" for=\"").Append(E(FieldId(f))).Append('"');
            _sb.Append('>').Append(E(f.Label));
            if (f.Required) _sb.Append(Admin ? " <span class=\"muted\">*</span>" : " <span class=\"req\">*</span>");
            _sb.Append("</label>");
        }

        private void FieldClose(UiField f)
        {
            if (!string.IsNullOrWhiteSpace(f.Help)) _sb.Append("<div class=\"").Append(Admin ? "help" : "field-help").Append("\">").Append(E(f.Help)).Append("</div>");
            _sb.Append("</div>");
        }

        private void Common(UiField f)
        {
            _sb.Append(" id=\"").Append(E(FieldId(f))).Append("\" name=\"").Append(E(f.Name)).Append('"');
            if (f.Required) _sb.Append(" required");
            if (f.Disabled) _sb.Append(" disabled");
        }

        private void Field(UiField field)
        {
            switch (field)
            {
                case UiInput i:
                {
                    FieldOpen(i);
                    var type = i.Type switch
                    {
                        UiInputType.Email => "email", UiInputType.Url => "url", UiInputType.Number => "number", UiInputType.Password => "password",
                        UiInputType.Date => "date", UiInputType.DateTime => "datetime-local", UiInputType.Color => "color", UiInputType.Tel => "tel", _ => "text"
                    };
                    _sb.Append("<input type=\"").Append(type).Append('"');
                    Common(i);
                    if (i.Type != UiInputType.Password) _sb.Append(" value=\"").Append(E(i.Value)).Append('"');
                    if (!string.IsNullOrEmpty(i.Placeholder)) _sb.Append(" placeholder=\"").Append(E(i.Placeholder)).Append('"');
                    if (!string.IsNullOrEmpty(i.Min)) _sb.Append(" min=\"").Append(E(i.Min)).Append('"');
                    if (!string.IsNullOrEmpty(i.Max)) _sb.Append(" max=\"").Append(E(i.Max)).Append('"');
                    if (!string.IsNullOrEmpty(i.Step)) _sb.Append(" step=\"").Append(E(i.Step)).Append('"');
                    if (i.MaxLength > 0) _sb.Append(" maxlength=\"").Append(i.MaxLength).Append('"');
                    if (i.Type == UiInputType.Password) _sb.Append(" autocomplete=\"new-password\"");
                    _sb.Append(" />");
                    FieldClose(i); break;
                }
                case UiTextArea t:
                    FieldOpen(t);
                    _sb.Append("<textarea rows=\"").Append(Math.Clamp(t.Rows, 1, 40)).Append('"');
                    Common(t);
                    if (!string.IsNullOrEmpty(t.Placeholder)) _sb.Append(" placeholder=\"").Append(E(t.Placeholder)).Append('"');
                    _sb.Append('>').Append(E(t.Value)).Append("</textarea>");
                    FieldClose(t); break;
                case UiSelect s:
                    FieldOpen(s);
                    _sb.Append("<select");
                    Common(s);
                    _sb.Append('>');
                    foreach (var o in s.Options)
                        _sb.Append("<option value=\"").Append(E(o.Value)).Append('"').Append(o.Value == s.Value ? " selected" : "").Append('>').Append(E(o.Label)).Append("</option>");
                    _sb.Append("</select>");
                    FieldClose(s); break;
                case UiCheckbox c:
                    FieldOpen(c);
                    _sb.Append("<label class=\"check\"><input type=\"checkbox\" value=\"true\"");
                    Common(c);
                    if (c.Checked) _sb.Append(" checked");
                    _sb.Append(" /> ").Append(E(c.Label)).Append("</label>");
                    FieldClose(c); break;
                case UiFile file:
                    FieldOpen(file);
                    _sb.Append("<input type=\"file\"");
                    Common(file);
                    if (!string.IsNullOrWhiteSpace(file.Accept)) _sb.Append(" accept=\"").Append(E(file.Accept)).Append('"');
                    _sb.Append(" />");
                    FieldClose(file); break;
                case UiImage img: ImageField(img); break;
                case UiMediaList ml: MediaListField(ml); break;
                case UiRichText rt: RichTextField(rt); break;
                case UiTags tg: TagsField(tg); break;
                case UiRowEditor re: RowEditorField(re); break;
                default: throw new NotSupportedException($"The renderer does not know {field.GetType().Name}.");
            }
        }

        private void ImageField(UiImage f)
        {
            FieldOpen(f, labelFor: false);
            var id = FieldId(f);
            _sb.Append("<div class=\"image-field\" data-ui-image><div class=\"preview\"");
            if (!string.IsNullOrWhiteSpace(f.Value)) _sb.Append(" style=\"background-image:url('").Append(E(SafeUrl(f.Value).Replace("'", "%27"))).Append("')\"");
            _sb.Append("></div><div class=\"image-controls\"><input type=\"text\"");
            Common(f);
            _sb.Append(" value=\"").Append(E(f.Value)).Append("\" placeholder=\"").Append(S("ui.image.placeholder")).Append("\" data-ui-image-url />");
            if (!f.Disabled)
                _sb.Append("<div style=\"margin-top:8px;\"><button type=\"button\" class=\"btn btn-ghost btn-sm\" data-ui-image-pick>").Append(S("ui.image.pick")).Append("</button></div>");
            _sb.Append("</div></div>");
            FieldClose(f);
        }

        private void MediaListField(UiMediaList f)
        {
            FieldOpen(f, labelFor: false);
            _sb.Append("<div data-ui-medialist data-kind=\"").Append(f.Files ? "files" : "images").Append("\" data-t-left=\"").Append(S("ui.media.left"))
               .Append("\" data-t-right=\"").Append(S("ui.media.right")).Append("\" data-t-remove=\"").Append(S("ui.media.remove")).Append("\">");
            _sb.Append("<div class=\"").Append(f.Files ? "att-list" : "gal-list").Append("\" data-ui-medialist-items></div>");
            if (!f.Disabled)
                _sb.Append("<div class=\"gal-actions\"><button type=\"button\" class=\"btn btn-ghost btn-sm\" data-ui-medialist-add>")
                   .Append(S(f.Files ? "ui.media.addFiles" : "ui.media.addImages")).Append("</button></div>");
            _sb.Append("<input type=\"hidden\"");
            Common(f);
            _sb.Append(" value=\"").Append(E(f.Value)).Append("\" data-ui-medialist-value /></div>");
            FieldClose(f);
        }

        private void RichTextField(UiRichText f)
        {
            FieldOpen(f, labelFor: false);
            _sb.Append("<div data-ui-rt data-t-link-prompt=\"").Append(S("ui.rt.linkPrompt")).Append("\"><div class=\"rt-toolbar\" data-ui-rt-toolbar>");
            void Btn(string cmd, string? arg, string titleKey, string text, bool prompt = false)
            {
                _sb.Append("<button type=\"button\" data-cmd=\"").Append(cmd).Append('"');
                if (arg is not null) _sb.Append(" data-arg=\"").Append(arg).Append('"');
                if (prompt) _sb.Append(" data-prompt=\"1\"");
                _sb.Append(" title=\"").Append(S(titleKey)).Append("\">").Append(text).Append("</button>");
            }
            Btn("bold", null, "ui.rt.bold", E(Sraw("ui.rt.boldLabel")));
            Btn("italic", null, "ui.rt.italic", E(Sraw("ui.rt.italicLabel")));
            Btn("formatBlock", "H2", "ui.rt.h2", "H2");
            Btn("formatBlock", "H3", "ui.rt.h3", "H3");
            Btn("formatBlock", "P", "ui.rt.paragraph", "¶");
            Btn("insertUnorderedList", null, "ui.rt.list", E(Sraw("ui.rt.listLabel")));
            Btn("createLink", null, "ui.rt.link", E(Sraw("ui.rt.link")), prompt: true);
            Btn("removeFormat", null, "ui.rt.clear", "<i class=\"ti ti-clear-formatting\" aria-hidden=\"true\"></i>");
            _sb.Append("</div><div class=\"rt-editor\" id=\"").Append(E(FieldId(f))).Append("-editor\" contenteditable=\"").Append(f.Disabled ? "false" : "true").Append("\" data-ui-rt-editor style=\"min-height:")
               .Append(Math.Clamp(f.MinHeight, 80, 1200)).Append("px\">").Append(f.Value).Append("</div><input type=\"hidden\"");
            Common(f);
            _sb.Append(" value=\"").Append(E(f.Value)).Append("\" data-ui-rt-value /></div>");
            FieldClose(f);
        }

        /// <summary>The cells of one row (or of the blank one the script copies for a new row, its index being a placeholder).</summary>
        private void RowCells(UiRowEditor f, UiRowValue? row, string index)
        {
            _sb.Append("<tr data-ui-row>");
            for (var c = 0; c < f.Columns.Count; c++)
            {
                var col = f.Columns[c];
                var value = row is not null && c < row.Values.Count ? row.Values[c] : "";
                var name = E(f.Name) + "." + index + "." + E(col.Key);
                _sb.Append("<td>");
                // The row's identity travels in its first cell, so the cells and the id are one unit for the script.
                if (c == 0) _sb.Append("<input type=\"hidden\" data-key=\"id\" name=\"").Append(E(f.Name)).Append('.').Append(index).Append(".id\" value=\"").Append(E(row?.Id ?? "")).Append("\" />");
                if (col.Kind == UiRowCell.Image)
                {
                    _sb.Append("<div class=\"row-img\"><span class=\"preview\"").Append(string.IsNullOrWhiteSpace(value) ? "" : " style=\"background-image:url('" + E(SafeUrl(value).Replace("'", "%27")) + "')\"").Append("></span><input type=\"text\" data-key=\"").Append(E(col.Key))
                       .Append("\" name=\"").Append(name).Append("\" value=\"").Append(E(value)).Append('"');
                    if (!string.IsNullOrEmpty(col.Placeholder)) _sb.Append(" placeholder=\"").Append(E(col.Placeholder)).Append('"');
                    _sb.Append(" aria-label=\"").Append(E(col.Header)).Append('"').Append(f.Disabled ? " disabled" : "").Append(" autocomplete=\"off\" />");
                    if (!f.Disabled) _sb.Append("<button type=\"button\" class=\"icon-btn\" data-ui-rowimage-pick title=\"").Append(S("ui.image.pick")).Append("\" aria-label=\"").Append(S("ui.image.pick")).Append("\"><i class=\"ti ti-photo\" aria-hidden=\"true\"></i></button>");
                    _sb.Append("</div>");
                }
                else if (col.Kind == UiRowCell.Check)
                    _sb.Append("<label class=\"row-check\"><input type=\"checkbox\" value=\"true\" data-key=\"").Append(E(col.Key)).Append("\" name=\"").Append(name).Append('"').Append(value == "true" ? " checked" : "")
                       .Append(f.Disabled ? " disabled" : "").Append(" aria-label=\"").Append(E(col.Header)).Append("\" /></label>");
                else
                {
                    _sb.Append("<input type=\"text\"").Append(col.Kind == UiRowCell.Number ? " inputmode=\"decimal\" class=\"num\"" : "").Append(" data-key=\"").Append(E(col.Key)).Append("\" name=\"").Append(name)
                       .Append("\" value=\"").Append(E(value)).Append('"');
                    if (!string.IsNullOrEmpty(col.Placeholder)) _sb.Append(" placeholder=\"").Append(E(col.Placeholder)).Append('"');
                    _sb.Append(" aria-label=\"").Append(E(col.Header)).Append('"').Append(f.Disabled ? " disabled" : "").Append(" autocomplete=\"off\" />");
                }
                _sb.Append("</td>");
            }
            _sb.Append("<td class=\"row-end\">");
            if (!f.Disabled)
                _sb.Append("<button type=\"button\" class=\"icon-btn row-remove\" data-ui-row-remove title=\"").Append(S("ui.rows.remove")).Append("\" aria-label=\"").Append(S("ui.rows.remove")).Append("\"><i class=\"ti ti-trash\" aria-hidden=\"true\"></i></button>");
            _sb.Append("</td></tr>");
        }

        private void RowEditorField(UiRowEditor f)
        {
            FieldOpen(f, labelFor: false);
            _sb.Append("<div class=\"row-editor\" data-ui-rows data-name=\"").Append(E(f.Name)).Append("\" data-max=\"").Append(Math.Clamp(f.MaxRows, 1, 500)).Append("\"><table class=\"data\"><thead><tr>");
            foreach (var col in f.Columns)
                _sb.Append("<th").Append(col.Kind == UiRowCell.Check ? " class=\"row-check-head\"" : "").Append(string.IsNullOrEmpty(col.Width) ? "" : " style=\"width:" + E(col.Width) + "\"").Append('>').Append(E(col.Header)).Append("</th>");
            _sb.Append("<th class=\"row-end\"></th></tr></thead><tbody data-ui-rows-body>");
            for (var i = 0; i < f.Rows.Count; i++) RowCells(f, f.Rows[i], i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _sb.Append("</tbody></table><template data-ui-rows-template>");
            RowCells(f, null, "__i__");
            _sb.Append("</template>");
            if (!string.IsNullOrWhiteSpace(f.EmptyText)) _sb.Append("<p class=\"row-empty\" data-ui-rows-empty").Append(f.Rows.Count > 0 ? " hidden" : "").Append('>').Append(E(f.EmptyText)).Append("</p>");
            _sb.Append("<input type=\"hidden\" name=\"").Append(E(f.Name)).Append(".count\" value=\"").Append(f.Rows.Count).Append("\" data-ui-rows-count />");
            if (!f.Disabled)
                _sb.Append("<div class=\"row-actions\"><button type=\"button\" class=\"btn btn-ghost btn-sm\" data-ui-rows-add><i class=\"ti ti-plus\" aria-hidden=\"true\"></i> ").Append(E(f.AddLabel)).Append("</button></div>");
            _sb.Append("</div>");
            FieldClose(f);
        }

        private void TagsField(UiTags f)
        {
            FieldOpen(f, labelFor: false);
            _sb.Append("<div data-ui-tags data-t-remove=\"").Append(S("ui.tags.remove")).Append("\"><div class=\"tag-input\" data-ui-tags-box>")
               .Append("<input type=\"text\" class=\"tag-entry\" data-ui-tags-entry placeholder=\"").Append(S("ui.tags.entry")).Append("\" autocomplete=\"off\" hidden />")
               .Append("<button type=\"button\" class=\"tag-add-chip\" data-ui-tags-add title=\"").Append(S("ui.tags.add")).Append("\" aria-label=\"").Append(S("ui.tags.add")).Append("\">+</button></div>")
               .Append("<input type=\"hidden\"");
            Common(f);
            _sb.Append(" value=\"").Append(E(f.Value)).Append("\" data-ui-tags-value />");
            if (f.Suggestions.Count > 0)
            {
                _sb.Append("<div class=\"tag-suggest\" data-ui-tags-suggest>");
                foreach (var t in f.Suggestions)
                    _sb.Append("<button type=\"button\" class=\"mini-chip mini-chip-add\" data-tag=\"").Append(E(t)).Append("\">+ ").Append(E(t)).Append("</button>");
                _sb.Append("</div>");
            }
            _sb.Append("</div>");
            FieldClose(f);
        }

        // ---- lists ----------------------------------------------------------------------------------

        private void Table(UiTable t)
        {
            var listed = Admin && (!string.IsNullOrEmpty(t.SearchPlaceholder) || t.PageSize > 0 || t.Filter is not null);
            if (listed)
            {
                _sb.Append("<div data-list");
                if (t.PageSize > 0) _sb.Append(" data-list-page-size=\"").Append(t.PageSize).Append('"');
                _sb.Append('>');
                // The admin's own list shape: the framed toolbar (search, filter) sits in a page-head above the table (admin.css frames `[data-list] .page-head .list-toolbar`).
                if (!string.IsNullOrEmpty(t.SearchPlaceholder) || t.Filter is not null)
                {
                    _sb.Append("<div class=\"page-head\"><div class=\"list-toolbar\">");
                    if (!string.IsNullOrEmpty(t.SearchPlaceholder))
                        _sb.Append("<input type=\"search\" class=\"list-search\" data-list-search placeholder=\"").Append(E(t.SearchPlaceholder)).Append("\" aria-label=\"").Append(E(t.SearchPlaceholder)).Append("\" autocomplete=\"off\" />");
                    if (t.Filter is { } flt)
                    {
                        _sb.Append("<select class=\"list-search\" data-list-filter aria-label=\"").Append(E(flt.Label)).Append("\">");
                        foreach (var (value, text) in flt.Options) _sb.Append("<option value=\"").Append(E(value)).Append('"').Append(!string.IsNullOrEmpty(flt.Selected) && flt.Selected == value ? " selected" : "").Append('>').Append(E(text)).Append("</option>");
                        _sb.Append("</select>");
                    }
                    _sb.Append("</div></div>");
                }
            }
            _sb.Append(Admin ? "<table class=\"data\"" : "<table class=\"ui-table\"");
            if (listed) _sb.Append(" data-list-table");
            _sb.Append("><thead><tr>");
            foreach (var c in t.Columns)
                _sb.Append("<th").Append(c.Right ? " style=\"text-align:right\"" : "").Append('>').Append(E(c.Header)).Append("</th>");
            _sb.Append("</tr></thead><tbody>");
            if (t.Rows.Count == 0 && !string.IsNullOrWhiteSpace(t.EmptyText))
                _sb.Append("<tr class=\"list-empty-row\"><td colspan=\"99\">").Append(E(t.EmptyText)).Append("</td></tr>");
            foreach (var r in t.Rows)
            {
                _sb.Append("<tr");
                if (listed && r.Search is not null) _sb.Append(" data-search=\"").Append(E(r.Search)).Append('"');
                if (listed && !string.IsNullOrWhiteSpace(r.Tags)) _sb.Append(" data-tags=\"").Append(E(r.Tags)).Append('"');
                _sb.Append('>');
                for (var i = 0; i < r.Cells.Count; i++)
                {
                    var col = i < t.Columns.Count ? t.Columns[i] : null;
                    _sb.Append("<td").Append(col is { Right: true } ? " style=\"text-align:right\"" : "").Append('>');
                    if (col is { Actions: true }) _sb.Append("<div class=\"actions\">");
                    Node(r.Cells[i]);
                    if (col is { Actions: true }) _sb.Append("</div>");
                    _sb.Append("</td>");
                }
                _sb.Append("</tr>");
            }
            _sb.Append("</tbody></table>");
            if (listed)
            {
                if (t.PageSize > 0) _sb.Append("<div class=\"list-pager\" data-list-pager></div>");
                _sb.Append("<p class=\"list-empty\" data-list-empty hidden>").Append(E(string.IsNullOrWhiteSpace(t.EmptyText) ? "" : t.EmptyText)).Append("</p>");
            }
            // What can be done with the list sits below it, like every list in the admin.
            if (t.Below.Count > 0) { _sb.Append("<div class=\"list-actions\">"); Children(t.Below); _sb.Append("</div>"); }
            if (listed) _sb.Append("</div>");
        }

        private void Tabs(UiTabs tabs)
        {
            _sb.Append("<div class=\"tabs\" role=\"tablist\">");
            foreach (var t in tabs.Tabs)
                _sb.Append("<a class=\"tab").Append(t.Active ? " active" : "").Append("\" href=\"").Append(E(SafeUrl(t.Href))).Append("\" role=\"tab\" aria-selected=\"")
                   .Append(t.Active ? "true" : "false").Append("\">").Append(E(t.Label)).Append("</a>");
            _sb.Append("</div>");
        }

        private void Pager(UiPager p)
        {
            if (p.Pages <= 1) return;
            _sb.Append("<div class=\"list-pager\">");
            for (var i = 1; i <= p.Pages; i++)
                _sb.Append("<a class=\"pager-btn").Append(i == p.Page ? " active" : "").Append("\" href=\"")
                   .Append(E(SafeUrl(p.HrefTemplate.Replace("{0}", i.ToString(System.Globalization.CultureInfo.InvariantCulture))))).Append("\">").Append(i).Append("</a>");
            _sb.Append("</div>");
        }
    }
}
