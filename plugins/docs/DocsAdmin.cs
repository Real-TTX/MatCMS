using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Docs;

/// <summary>The back office: the tree of pages per language and version, the page editor, the versions. Every screen is a tree of library
/// components; the one raw piece is the indented title cell of the tree (the shop's category tree does the same).</summary>
internal static class DocsAdmin
{
    public const string Section = "/admin/p/docs";
    public static string EditUrl(long id) => $"{Section}/pages/{id.ToString(CultureInfo.InvariantCulture)}";
    private static string I(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static string E(string? s) => DocsMarkdown.E(s);
    private static string Lang(string locale) => locale.ToUpperInvariant();

    private static string ListUrl(string locale, string version) =>
        $"{Section}/pages?lang={Uri.EscapeDataString(locale)}" + (version.Length > 0 ? "&v=" + Uri.EscapeDataString(version) : "");

    /// <summary>The language and version a list or a new page is about: the query's when valid, else the defaults.</summary>
    private static (string Locale, string Version) Scope(IPluginContext ctx, IReadOnlyList<DocVersion> versions)
    {
        string Value(string key) => ctx.Request.IsPost && ctx.Request.F(key).Length > 0 ? ctx.Request.F(key) : ctx.Request.Q(key);
        var l = Value("lang").Trim().ToLowerInvariant();
        if (!DocsUrls.Languages(ctx).Contains(l)) l = DocsUrls.Default(ctx);
        var v = Value("v").Trim();
        if (!versions.Any(z => z.Name == v)) v = DocsStore.DefaultVersion(versions);
        return (l, v);
    }

    // ---- the tree --------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> PagesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new DocsStore(ctx.Data);
        var versions = await store.VersionsAsync(ct);
        var (locale, version) = Scope(ctx, versions);
        if (ctx.Request.IsPost && long.TryParse(ctx.Request.F("id"), out var pid) && await store.GetAsync(pid, ct) is { } target)
        {
            switch (ctx.Request.Action)
            {
                case "up": await store.MoveAsync(target, -1, ct); break;
                case "down": await store.MoveAsync(target, +1, ct); break;
                case "delete":
                    await store.DeleteAsync(target, ct);
                    return PluginResult.Redirect(ListUrl(target.Locale, target.Version), ctx.T("docs.admin.deleted", target.Title));
            }
            return PluginResult.Redirect(ListUrl(target.Locale, target.Version));
        }

        var tree = await store.TreeAsync(locale, version, ct);
        var defVersion = DocsStore.DefaultVersion(versions);
        var nodes = new List<UiNode?>();
        var languages = DocsUrls.Languages(ctx);
        if (languages.Count > 1)
            nodes.Add(Ui.Tabs(languages.Select(l => Ui.Tab(ctx.T("docs.admin.language", Lang(l)), ListUrl(l, version), l == locale)).ToArray()));
        if (versions.Count > 0)
            nodes.Add(Ui.Tabs(versions.Select(x => Ui.Tab(ctx.T("docs.admin.versionTab", x.Shown) + (x.IsDefault ? " ★" : ""), ListUrl(locale, x.Name), x.Name == version)).ToArray()));

        var rows = tree.Flat.Select(n =>
        {
            var p = n.Page;
            var url = DocsUrls.Page(ctx, locale, version, defVersion, n.Path);
            var arrow = n.Depth > 0 ? "<i class=\"ti ti-corner-down-right muted\" aria-hidden=\"true\"></i> " : "";
            var icon = n.Depth == 0 ? "<i class=\"ti ti-folder\" aria-hidden=\"true\"></i> " : "";
            var title = Ui.Html($"<div style=\"padding-left:{n.Depth * 24}px\">{arrow}{icon}<a href=\"{EditUrl(p.Id)}\"><strong>{E(p.Title)}</strong></a>{(p.Summary.Length > 0 ? $"<div class=\"muted\">{E(p.Summary)}</div>" : "")}</div>");
            var siblings = n.Parent?.Children ?? tree.Roots.ToList();
            var idx = siblings.IndexOf(n);
            var actions = Ui.Inline(
                idx > 0 ? Ui.Action(ctx.T("docs.admin.up"), "up", I(p.Id), icon: "ti-arrow-up") : null,
                idx < siblings.Count - 1 ? Ui.Action(ctx.T("docs.admin.down"), "down", I(p.Id), icon: "ti-arrow-down") : null,
                n.Depth < DocsStore.MaxDepth - 1
                    ? Ui.Button(ctx.T("docs.admin.addChild"), $"{EditUrl(0)}?parent={I(p.Id)}&lang={locale}&v={Uri.EscapeDataString(version)}", UiButtonKind.Ghost, "ti-plus", small: true) with { IconOnly = true }
                    : null,
                Ui.Button(ctx.T("docs.admin.edit"), EditUrl(p.Id), UiButtonKind.Ghost, "ti-pencil", small: true),
                Ui.Action(ctx.T("docs.admin.delete"), "delete", I(p.Id), UiButtonKind.Danger,
                    confirm: n.Children.Count > 0 ? ctx.T("docs.admin.confirmDeleteParent", p.Title) : ctx.T("docs.admin.confirmDelete", p.Title), icon: "ti-trash"));
            return Ui.RowFor(p.Title + " " + n.Path + " " + p.Summary, title, Ui.Link(url, url, newTab: true),
                p.Published ? Ui.Badge(ctx.T("docs.admin.published"), UiTone.Success) : Ui.Badge(ctx.T("docs.admin.draft"), UiTone.Muted), actions);
        });
        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("docs.admin.colTitle")), Ui.Column(ctx.T("docs.admin.colUrl")), Ui.Column(ctx.T("docs.admin.colStatus")), Ui.Column("", actions: true) },
            rows, searchPlaceholder: ctx.T("docs.admin.search"), emptyText: ctx.T("docs.admin.none")) with
        {
            Below = new UiNode[] { Ui.Button(ctx.T("docs.admin.newSection"), $"{EditUrl(0)}?parent=0&lang={locale}&v={Uri.EscapeDataString(version)}", icon: "ti-plus") }
        };
        nodes.Add(table);
        nodes.Add(Ui.Muted(ctx.T("docs.admin.treeHelp")));
        return PluginResult.Page(Ui.Stack(nodes.ToArray()), ctx.T("docs.admin.pages"));
    }

    // ---- the editor ------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> EditAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new DocsStore(ctx.Data);
        var versions = await store.VersionsAsync(ct);
        var id = long.TryParse(ctx.Request.R("id"), out var parsed) ? parsed : 0;
        DocPage page;
        DocPage? original = null;
        var form = ctx.Request;
        if (id == 0)
        {
            var (locale, version) = Scope(ctx, versions);
            var parent = long.TryParse(form.IsPost ? form.F("parent") : form.Q("parent"), out var pp) ? pp : 0;
            page = new DocPage { Locale = locale, Version = version, ParentId = parent, Published = true };
            // "Übersetzung anlegen": a new page in another language, filled with the original to translate, joined to its group on save.
            var of = long.TryParse(form.IsPost ? form.F("translationOf") : form.Q("translationOf"), out var o) ? o : 0;
            if (of > 0 && await store.GetAsync(of, ct) is { } source && locale != source.Locale)
            {
                original = source;
                var parentHere = 0L;
                if (source.ParentId > 0 && await store.GetAsync(source.ParentId, ct) is { GroupKey.Length: > 0 } sp)
                    parentHere = (await store.TranslationsAsync(sp.GroupKey, ct)).FirstOrDefault(t => t.Locale == locale && t.Version == source.Version)?.Id ?? 0;
                page = source with { Id = 0, Locale = locale, ParentId = parentHere, Published = false, GroupKey = "", CreatedAt = DateTime.MinValue, UpdatedAt = DateTime.MinValue };
            }
            if (page.ParentId > 0 && await store.GetAsync(page.ParentId, ct) is { } par && original is null)
                page = page with { Locale = par.Locale, Version = par.Version };
        }
        else
        {
            var found = await store.GetAsync(id, ct);
            if (found is null) return PluginResult.Redirect(Section + "/pages");
            page = found;
        }

        string? error = null;
        if (form.IsPost && form.Action == "delete" && page.Id > 0)
        {
            await store.DeleteAsync(page, ct);
            return PluginResult.Redirect(ListUrl(page.Locale, page.Version), ctx.T("docs.admin.deleted", page.Title));
        }
        if (form.IsPost && form.Action == "save")
        {
            var (saved, problem, read) = await SaveAsync(ctx, store, page, original, ct);
            if (problem is null) return PluginResult.Redirect(EditUrl(saved), ctx.T("docs.admin.saved"));
            error = problem;
            page = read;
        }
        return await EditorAsync(ctx, store, versions, page, original, error, ct);
    }

    private static async Task<(long Id, string? Error, DocPage Read)> SaveAsync(IPluginContext ctx, DocsStore store, DocPage page, DocPage? original, CancellationToken ct)
    {
        var f = ctx.Request;
        var format = f.F("format") == "html" ? "html" : "md";
        var body = f.F(page.Format == "html" ? "body_html" : "body");
        // Saved from the visual mode: the editor's blocks are written back as Markdown here (untouched ones exactly as they were).
        if (page.Format != "html" && f.F("md_mode") == "visual" && ParseState(f.F("md_visual")) is { } state) body = DocsVisual.FromEditor(state);
        // Switching the format takes the text along: Markdown becomes the HTML it draws, rich text becomes its plain text.
        if (format != page.Format && page.Id > 0)
            body = format == "html" ? DocsMarkdown.Render(body).Html : DocsMarkdown.Plain(body);
        if (format == "html") body = DocsUrls.Sanitize(ctx, body);
        var parent = long.TryParse(f.F("parent"), out var pp) ? pp : 0;
        var read = page with
        {
            Title = f.F("title").Trim(), Summary = f.F("summary").Trim(), Icon = DocsHtml.Icon(f.F("icon")), Format = format, Body = body,
            Published = f.Form.ContainsKey("published"), ParentId = parent, Slug = f.F("slug").Trim(),
            Sort = long.TryParse(f.F("sort"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : page.Sort,
            UpdatedBy = ctx.User.Name
        };
        if (read.Title.Length == 0) return (0, ctx.T("docs.admin.titleRequired"), read);
        if (read.Title.Length > 200) read = read with { Title = read.Title[..200] };

        // The parent must be a page of the same language and version, not the page itself or one below it, and not too deep.
        if (parent != 0)
        {
            var tree = await store.TreeAsync(read.Locale, read.Version, ct);
            var pn = tree.Find(parent);
            if (pn is null || pn.Page.Id == page.Id || (page.Id > 0 && pn.Ancestors().Any(a => a.Page.Id == page.Id))) return (0, ctx.T("docs.admin.badParent"), read);
            if (pn.Depth + 1 >= DocsStore.MaxDepth) return (0, ctx.T("docs.admin.tooDeep", DocsStore.MaxDepth), read);
        }
        if (page.Id == 0 || read.ParentId != page.ParentId)
            if (!f.Form.ContainsKey("sort") || f.F("sort").Trim().Length == 0) read = read with { Sort = await store.NextSortAsync(parent, read.Locale, read.Version, ct) };
        read = read with { Slug = await store.FreeSlugAsync(read.Slug.Length > 0 ? read.Slug : read.Title, parent, read.Locale, read.Version, page.Id, ct) };

        if (original is not null && page.Id == 0)
        {
            var group = await store.EnsureGroupAsync(original, ct);
            if ((await store.TranslationsAsync(group, ct)).Any(t => t.Locale == read.Locale && t.Version == read.Version))
                return (0, ctx.T("docs.admin.translationExists", Lang(read.Locale)), read);
            read = read with { GroupKey = group };
        }
        return (await store.SaveAsync(read, ct), null, read);
    }

    private static async Task<PluginResult> EditorAsync(IPluginContext ctx, DocsStore store, IReadOnlyList<DocVersion> versions, DocPage p, DocPage? original, string? error, CancellationToken ct)
    {
        var isNew = p.Id == 0;
        var tree = await store.TreeAsync(p.Locale, p.Version, ct);
        var self = tree.Find(p.Id);
        var below = self is null ? new HashSet<long>() : tree.Flat.Where(n => n.Ancestors().Any(a => a.Page.Id == p.Id)).Select(n => n.Page.Id).ToHashSet();
        var parents = new List<UiOption> { Ui.Option("0", ctx.T("docs.admin.noParent")) };
        parents.AddRange(tree.Flat.Where(n => n.Page.Id != p.Id && !below.Contains(n.Page.Id) && n.Depth < DocsStore.MaxDepth - 1)
            .Select(n => Ui.Option(I(n.Page.Id), string.Concat(Enumerable.Repeat("— ", n.Depth)) + n.Page.Title)));
        var defVersion = DocsStore.DefaultVersion(versions);

        var bodyField = p.Format == "html"
            ? (UiNode)Ui.RichText("body_html", ctx.T("docs.admin.body"), p.Body)
            : Ui.TextArea("body", ctx.T("docs.admin.body"), p.Body, 26) with { Placeholder = ctx.T("docs.admin.bodyPlaceholder"), Help = ctx.T("docs.admin.markdownHelp") };

        var main = new List<UiNode?>
        {
            error is null ? null : Ui.Alert(error, UiTone.Error),
            Ui.Card(
                Ui.Input("title", ctx.T("docs.admin.title"), p.Title) with { Required = true },
                Ui.Input("slug", ctx.T("docs.admin.slug"), p.Slug) with { Help = ctx.T("docs.admin.slugHelp") },
                Ui.TextArea("summary", ctx.T("docs.admin.summary"), p.Summary, 2) with { Help = ctx.T("docs.admin.summaryHelp") },
                bodyField),
            p.Format == "md" ? Ui.Card(ctx.T("docs.admin.cheatTitle"), Ui.Html(Cheatsheet(ctx))) : null
        };

        var side = new List<UiNode?>
        {
            Ui.Card(
                Ui.Checkbox("published", ctx.T("docs.admin.publishedLabel"), p.Published),
                Ui.Select("parent", ctx.T("docs.admin.parent"), I(p.ParentId), parents.ToArray()),
                Ui.Input("sort", ctx.T("docs.admin.sort"), isNew ? "" : I(p.Sort), UiInputType.Number) with { Help = ctx.T("docs.admin.sortHelp") },
                Ui.Select("format", ctx.T("docs.admin.format"), p.Format, Ui.Option("md", ctx.T("docs.admin.formatMd")), Ui.Option("html", ctx.T("docs.admin.formatHtml"))) with { Help = ctx.T("docs.admin.formatHelp") },
                Ui.Input("icon", ctx.T("docs.admin.icon"), p.Icon) with { Help = ctx.T("docs.admin.iconHelp") },
                Ui.KeyValue((ctx.T("docs.admin.languageLabel"), Lang(p.Locale)), (ctx.T("docs.admin.version"), versions.Count == 0 ? "–" : versions.FirstOrDefault(v => v.Name == p.Version)?.Shown ?? p.Version)),
                Ui.FormActions(Ui.Submit(ctx.T("docs.admin.save"), name: "action", value: "save", icon: "ti-device-floppy")))
        };

        if (!isNew)
        {
            var url = self is null ? "" : DocsUrls.Page(ctx, p.Locale, p.Version, defVersion, self.Path);
            side.Add(Ui.Card(ctx.T("docs.admin.view"),
                url.Length > 0 ? Ui.Link(url, url, newTab: true) : null,
                p.UpdatedAt > DateTime.MinValue ? Ui.Muted(ctx.T("docs.admin.lastChange", DocsHtml.Date(p.UpdatedAt, "de"), p.UpdatedBy.Length > 0 ? p.UpdatedBy : "–")) : null));
            // Translations: the ones there are (each opens its editor) and "create" for each language without one.
            var langs = DocsUrls.Languages(ctx);
            if (langs.Count > 1)
            {
                var translations = await store.TranslationsAsync(p.GroupKey, ct);
                var items = new List<UiNode?>();
                foreach (var l in langs.Where(l => l != p.Locale))
                {
                    var t = translations.FirstOrDefault(x => x.Locale == l && x.Version == p.Version);
                    items.Add(t is not null
                        ? Ui.Button(ctx.T("docs.admin.openTranslation", Lang(l)), EditUrl(t.Id), UiButtonKind.Ghost, "ti-language", small: true)
                        : Ui.Button(ctx.T("docs.admin.createTranslation", Lang(l)), $"{EditUrl(0)}?translationOf={I(p.Id)}&lang={l}&v={Uri.EscapeDataString(p.Version)}", UiButtonKind.Ghost, "ti-plus", small: true));
                }
                side.Add(Ui.Card(ctx.T("docs.admin.translations"), items.ToArray()));
            }
            side.Add(Ui.Card(
                Ui.Button(ctx.T("docs.admin.addChild"), $"{EditUrl(0)}?parent={I(p.Id)}&lang={p.Locale}&v={Uri.EscapeDataString(p.Version)}", UiButtonKind.Ghost, "ti-plus", small: true),
                Ui.Submit(ctx.T("docs.admin.delete"), UiButtonKind.Danger, "action", "delete",
                    confirm: self is { Children.Count: > 0 } ? ctx.T("docs.admin.confirmDeleteParent", p.Title) : ctx.T("docs.admin.confirmDelete", p.Title), icon: "ti-trash", small: true)));
        }
        else if (original is not null)
            side.Insert(0, Ui.Alert(ctx.T("docs.admin.translating", original.Title, Lang(original.Locale)), UiTone.Info));

        var hidden = new Dictionary<string, string> { ["lang"] = p.Locale, ["v"] = p.Version };
        if (original is not null) hidden["translationOf"] = I(original.Id);
        if (p.Format == "md") { hidden["md_mode"] = "md"; hidden["md_visual"] = ""; }
        var formNode = Ui.Form(Ui.Columns(main, side)) with { Hidden = hidden, Id = "docs-page-form" };
        var back = Ui.PageHead(null, null, Ui.Button(ctx.T("docs.admin.back"), ListUrl(p.Locale, p.Version), UiButtonKind.Ghost, "ti-arrow-left"));
        var title = original is not null ? ctx.T("docs.admin.newTranslation") : isNew ? (p.ParentId == 0 ? ctx.T("docs.admin.newSection") : ctx.T("docs.admin.newPage")) : ctx.T("docs.admin.editTitle", p.Title);
        return PluginResult.Page(Ui.Stack(back, formNode, p.Format == "md" ? EditorParts(ctx, tree, p) : null), title);
    }

    // ---- the visual Markdown editor ---------------------------------------------------------------------------------

    private static JsonNode? ParseState(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// What the editor's script (assets/docs-editor.js) builds on: its settings and words, the dialog that picks a page for a <c>doc:</c> link, the
    /// script. The script wraps the Markdown text area with the switch "Visuell | Markdown", the toolbar and the preview; without it the page is the
    /// plain text area it always was.
    /// </summary>
    private static UiNode EditorParts(IPluginContext ctx, DocTree tree, DocPage p)
    {
        var words = new JsonObject();
        foreach (var key in EditorWords) words[key] = ctx.T("docs.ed." + key);
        foreach (var kind in new[] { "note", "tip", "important", "warning", "caution" }) words["alert_" + kind] = ctx.T("docs.alert." + kind);
        var config = $"<div id=\"docs-ed-config\" hidden data-url=\"{E(Section + "/convert")}\" data-lang=\"{E(p.Locale)}\" data-v=\"{E(p.Version)}\" " +
                     $"data-words=\"{E(words.ToJsonString())}\"></div>";
        var pages = new List<UiOption> { Ui.Option("", ctx.T("docs.ed.docPick")) };
        pages.AddRange(tree.Flat.Select(n => Ui.Option(n.Path, string.Concat(Enumerable.Repeat("— ", n.Depth)) + n.Page.Title + "  ·  " + n.Path)));
        var docDialog = Ui.Dialog("docs-ed-doc", ctx.T("docs.ed.docTitle"),
            Ui.Select("docs_ed_doc", ctx.T("docs.ed.docPage"), "", pages.ToArray()) with { Id = "docs-ed-doc-page" },
            Ui.Input("docs_ed_anchor", ctx.T("docs.ed.docAnchor"), "") with { Id = "docs-ed-doc-anchor", Placeholder = "abschnitt", Help = ctx.T("docs.ed.docAnchorHelp") },
            Ui.Input("docs_ed_text", ctx.T("docs.ed.docText"), "") with { Id = "docs-ed-doc-text", Help = ctx.T("docs.ed.docTextHelp") },
            Ui.Button(ctx.T("docs.ed.insert"), "#", icon: "ti-link", small: true) with { Href = null, NoSubmit = true, Data = new Dictionary<string, string> { ["docs-ed-doc-go"] = "" } });
        var css = $"<style>{DocsHtml.Css}{EditorCss}</style>";
        var script = $"<script src=\"{E(ctx.AssetUrl("docs-editor.js"))}\"></script>";
        return Ui.Stack(docDialog, Ui.Html(css + config + script));
    }

    private static readonly string[] EditorWords =
    [
        "visual", "markdown", "preview", "previewEmpty", "paragraph", "h2", "h3", "h4", "bold", "italic", "strike", "code", "link", "docLink", "image",
        "ul", "ol", "task", "quote", "callout", "codeBlock", "table", "hr", "rowAdd", "colAdd", "rowDel", "colDel", "linkUrl", "imageUrl", "imageAlt",
        "language", "failed", "busy", "undo", "redo", "clear", "uploading", "uploadFailed"
    ];

    /// <summary>The editor's own look. The text inside the visual area is drawn with the PUBLIC stylesheet (<see cref="DocsHtml.Css"/>), scoped under .docs.</summary>
    private const string EditorCss = """
        .docs-ed-bar{display:flex;flex-wrap:wrap;align-items:center;gap:4px;margin:0 0 8px}
        .docs-ed-bar .docs-ed-sep{width:1px;align-self:stretch;margin:2px 4px;background:var(--line)}
        .docs-ed-bar .btn.is-on{background:var(--hover);color:var(--accent)}
        .docs-ed-bar .docs-ed-grow{flex:1}
        .docs-ed-modes{display:inline-flex;border:1px solid var(--line);border-radius:8px;overflow:hidden}
        .docs-ed-modes button{border:0;background:transparent;color:inherit;font:inherit;font-size:.86em;padding:5px 12px;cursor:pointer}
        .docs-ed-modes button.is-on{background:var(--accent);color:#fff}
        .docs-ed-split{display:grid;grid-template-columns:minmax(0,1fr);gap:14px}
        .docs-ed-split.has-preview{grid-template-columns:minmax(0,1fr) minmax(0,1fr)}
        @media(max-width:1100px){.docs-ed-split.has-preview{grid-template-columns:minmax(0,1fr)}}
        .docs-ed-visual{min-height:420px;max-height:75vh;overflow:auto;padding:14px 18px;border:1px solid var(--line);border-radius:10px;background:var(--surface);color:var(--text,inherit)}
        .docs-ed-visual:focus-within{border-color:var(--accent)}
        .docs-ed-visual .docs-content{outline:none;min-height:390px}
        .docs-ed-visual .docs-content :is(p,ul,ol,h2,h3,h4,.docs-code,.docs-table,.docs-callout,blockquote,hr){margin-top:0}
        .docs-ed-visual .docs-doclink{text-decoration:underline dashed}
        .docs-ed-visual .docs-doclink::before{content:"\2197\FE0E";font-size:.8em;margin-right:2px;opacity:.6}
        .docs-ed-visual .docs-code-lang{cursor:pointer;min-width:3em;display:inline-block}
        .docs-ed-visual .docs-code-lang:empty::before{content:var(--docs-ed-code);opacity:.6}
        .docs-ed-visual .docs-callout-title{cursor:pointer;user-select:none}
        .docs-ed-visual li.docs-task{list-style:none}
        .docs-ed-visual table td,.docs-ed-visual table th{min-width:3em}
        .docs-ed-preview{height:75vh;min-height:420px;border:1px solid var(--line);border-radius:10px;background:#fff;width:100%}
        .docs-ed-hidden{display:none!important}
        .docs-ed-pop{position:absolute;z-index:50;display:flex;flex-direction:column;gap:2px;padding:6px;border:1px solid var(--line);border-radius:10px;background:var(--surface);box-shadow:0 10px 30px rgba(0,0,0,.18)}
        .docs-ed-pop .btn{justify-content:flex-start}
        .docs-ed-status{font-size:.82em;color:var(--text-muted)}
        """;

    /// <summary>
    /// The editor's server side (POST, admin only, antiforgery as every admin form): <c>visual</c> cuts Markdown into the blocks the visual mode shows,
    /// <c>markdown</c> writes the visual mode's blocks back as Markdown, <c>preview</c> draws a text with <see cref="DocsMarkdown"/> and the public
    /// stylesheet — the one renderer, so the preview is the page.
    /// </summary>
    public static async Task<PluginResult> ConvertAsync(IPluginContext ctx, CancellationToken ct)
    {
        var f = ctx.Request;
        if (!f.IsPost) return PluginResult.Status(405);
        var store = new DocsStore(ctx.Data);
        var (locale, version) = Scope(ctx, await store.VersionsAsync(ct));
        var view = await DocsView.CreateAsync(ctx, locale, version, true, ct);
        string Markdown() => f.F("mode") == "visual" && ParseState(f.F("visual")) is { } s ? DocsVisual.FromEditor(s) : f.F("body");
        switch (f.Action)
        {
            case "visual":
                return PluginResult.Json(DocsVisual.ToJson(DocsMarkdown.ToEditor(f.F("body"), view.MarkdownOptions)));
            case "markdown":
                return ParseState(f.F("visual")) is { } state
                    ? PluginResult.Json(new JsonObject { ["markdown"] = DocsVisual.FromEditor(state) })
                    : PluginResult.Json(new JsonObject { ["error"] = "bad state" }, 400);
            case "preview":
            {
                var md = Markdown();
                var r = DocsMarkdown.Render(md, view.MarkdownOptions);
                var title = f.F("title").Trim();
                var summary = f.F("summary").Trim();
                var head = title.Length + summary.Length > 0
                    ? $"<div class=\"docs-head\">{(title.Length > 0 ? $"<h1>{E(title)}</h1>" : "")}{(summary.Length > 0 ? $"<p class=\"docs-lead\">{E(summary)}</p>" : "")}</div>"
                    : "";
                var own = (ctx.Settings.Get(DocsSettings.CustomCss) ?? "").Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase);
                var doc = "<!doctype html><html lang=\"" + E(locale) + "\"><head><meta charset=\"utf-8\"><base target=\"_blank\">" +
                          "<style>body{margin:0;padding:18px 22px;font:16px/1.6 system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;color:#1f2937;background:#fff}" +
                          "a{color:var(--accent,#2563eb)}</style><style>" + DocsHtml.Css + own + "</style></head><body><div class=\"docs\"><div class=\"docs-main\">" +
                          head + "<div class=\"docs-content\">" + r.Html + "</div></div></div></body></html>";
                return PluginResult.Json(new JsonObject { ["html"] = doc, ["markdown"] = f.F("mode") == "visual" ? md : null });
            }
        }
        return PluginResult.Status(400);
    }

    /// <summary>The short Markdown reference beside the editor (examples as code, so they are shown and not drawn).</summary>
    private static string Cheatsheet(IPluginContext ctx)
    {
        var rows = new (string Code, string Key)[]
        {
            ("## …   ### …", "docs.cheat.headings"),
            ("**…**   *…*   `…`   ~~…~~", "docs.cheat.inline"),
            ("[…](https://…)   […](doc:section/page)", "docs.cheat.links"),
            ("![…](/uploads/….png)", "docs.cheat.images"),
            ("- …   1. …   - [ ] …", "docs.cheat.lists"),
            ("```bash … ```", "docs.cheat.code"),
            ("| A | B |\n|---|---|\n| 1 | 2 |", "docs.cheat.tables"),
            ("> [!NOTE] / [!TIP] / [!WARNING]", "docs.cheat.alerts")
        };
        var sb = new StringBuilder("<table class=\"data\"><tbody>");
        foreach (var (code, key) in rows) sb.Append($"<tr><td><code style=\"white-space:pre-wrap\">{E(code)}</code></td><td>{E(ctx.T(key))}</td></tr>");
        return sb.Append("</tbody></table>").ToString();
    }

    // ---- versions --------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> VersionsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new DocsStore(ctx.Data);
        var f = ctx.Request;
        if (f.IsPost)
        {
            switch (f.Action)
            {
                case "add":
                {
                    var name = DocsStore.CleanVersionName(f.F("name"));
                    if (name.Length == 0) return PluginResult.Redirect(Section + "/versions", ctx.T("docs.ver.nameRequired"), true);
                    var problem = await store.AddVersionAsync(name, f.F("label").Trim(), f.F("copyFrom"), f.Form.ContainsKey("makeDefault"), ct);
                    return problem is null
                        ? PluginResult.Redirect(Section + "/versions", ctx.T("docs.ver.added", name))
                        : PluginResult.Redirect(Section + "/versions", ctx.T("docs.ver.exists", name), true);
                }
                case "default":
                    await store.SetDefaultAsync(f.F("name"), ct);
                    return PluginResult.Redirect(Section + "/versions", ctx.T("docs.ver.defaultSet", f.F("name")));
                case "delete":
                    return await store.DeleteVersionAsync(f.F("name"), ct)
                        ? PluginResult.Redirect(Section + "/versions", ctx.T("docs.ver.deleted", f.F("name")))
                        : PluginResult.Redirect(Section + "/versions", ctx.T("docs.ver.lastOne"), true);
            }
        }

        var versions = await store.VersionsAsync(ct);
        var pages = await store.AllPagesAsync(ct);
        var nodes = new List<UiNode?> { Ui.Muted(ctx.T(versions.Count == 0 ? "docs.ver.introNone" : "docs.ver.intro")) };
        if (versions.Count > 0)
        {
            nodes.Add(Ui.Table(
                new[] { Ui.Column(ctx.T("docs.ver.colName")), Ui.Column(ctx.T("docs.ver.colLabel")), Ui.Column(ctx.T("docs.ver.colPages"), right: true), Ui.Column("", actions: true) },
                versions.Select(v => Ui.Row(
                    Ui.Inline(Ui.Text(v.Name) with { Inline = true, Strong = true }, v.IsDefault ? Ui.Badge(ctx.T("docs.ver.default"), UiTone.Success) : null),
                    v.Label,
                    I(pages.Count(p => p.Version == v.Name)),
                    Ui.Inline(
                        v.IsDefault ? null : Ui.Action(ctx.T("docs.ver.makeDefault"), new Dictionary<string, string> { ["action"] = "default", ["name"] = v.Name }, icon: "ti-star") with { WithLabel = true },
                        Ui.Button(ctx.T("docs.admin.pages"), ListUrl(DocsUrls.Default(ctx), v.Name), UiButtonKind.Ghost, "ti-list", small: true),
                        versions.Count > 1 ? Ui.Action(ctx.T("docs.admin.delete"), new Dictionary<string, string> { ["action"] = "delete", ["name"] = v.Name }, UiButtonKind.Danger,
                            ctx.T("docs.ver.confirmDelete", v.Name, pages.Count(p => p.Version == v.Name)), "ti-trash") : null)))));
        }
        var copy = new List<UiOption> { Ui.Option("", ctx.T("docs.ver.copyNone")) };
        copy.AddRange(versions.Select(v => Ui.Option(v.Name, ctx.T("docs.ver.copyFrom", v.Shown))));
        nodes.Add(Ui.Card(ctx.T("docs.ver.new"),
            Ui.Form(
                Ui.Input("name", ctx.T("docs.ver.name"), "") with { Required = true, Placeholder = "2.0", Help = ctx.T("docs.ver.nameHelp") },
                Ui.Input("label", ctx.T("docs.ver.label"), "") with { Placeholder = ctx.T("docs.ver.labelPlaceholder") },
                versions.Count > 0 ? Ui.Select("copyFrom", ctx.T("docs.ver.copy"), DocsStore.DefaultVersion(versions), copy.ToArray()) : null,
                versions.Count > 0 ? Ui.Checkbox("makeDefault", ctx.T("docs.ver.makeDefaultNew")) : null,
                Ui.FormActions(Ui.Submit(ctx.T("docs.ver.add"), name: "action", value: "add", icon: "ti-plus"))) with { Narrow = true }));
        return PluginResult.Page(Ui.Stack(nodes.ToArray()), ctx.T("docs.admin.versions"));
    }

    // ---- dashboard -------------------------------------------------------------------------------------------------

    public static async Task<UiStat?> TileAsync(IPluginContext ctx, CancellationToken ct)
    {
        var pages = ctx.Data.Table("pages");
        var total = await pages.CountAsync(null, ct);
        var drafts = await pages.CountAsync(DataQuery.Everything.Eq("published", false), ct);
        return Ui.Stat(ctx.T("docs.tile"), I(total), drafts > 0 ? ctx.T("docs.tileDrafts", drafts) : null, href: Section + "/pages");
    }

    public static async Task<IEnumerable<RecentItem>> RecentAsync(IPluginContext ctx, CancellationToken ct) =>
        (await ctx.Data.Table("pages").QueryAsync(DataQuery.Everything.OrderBy("updated", descending: true).Page(0, 20), ct))
            .Select(DocsStore.From).Select(p => new RecentItem("ti-book-2", p.Title, p.Published, p.UpdatedAt, EditUrl(p.Id)));
}
