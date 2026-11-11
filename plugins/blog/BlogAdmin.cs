using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Blog;

/// <summary>The blog's back office: the list of posts, the editor and the two helpers the editor calls (a post read from a link, an AI
/// teaser). Every screen is a tree of library components — nothing here writes a class name. The two dialogs the editor opens (import, AI
/// teaser) bring their scripts from the plugin's own assets.</summary>
internal static class BlogAdmin
{
    public const string Section = "/admin/p/blog";
    private static string EditUrl(long id) => $"{Section}/posts/{id}";

    /// <summary>The name of a language as the language switcher writes it ("EN").</summary>
    private static string Lang(string locale) => locale.ToUpperInvariant();

    // ---- list ------------------------------------------------------------------------------------

    public static async Task<PluginResult> ListAsync(IPluginContext ctx, CancellationToken ct)
    {
        var posts = ctx.Data.Table("posts");
        if (ctx.Request.IsPost && ctx.Request.Action == "delete" && long.TryParse(ctx.Request.F("id"), out var del))
        {
            await posts.DeleteAsync(del, ct);
            return PluginResult.Redirect(Section, ctx.T("blog.deleted"));
        }

        var now = DateTime.UtcNow;
        var rows = await BlogStore.AllAsync(posts, ct);
        // The language column and filter only where there is more than one language to tell apart — a German-only site sees the list it always had.
        var languages = BlogUrls.Languages(ctx).Concat(rows.Select(p => p.Locale)).Distinct().ToList();
        var multi = languages.Count > 1;
        var columns = new List<UiColumn> { Ui.Column(ctx.T("pages.colTitle")), Ui.Column(ctx.T("pages.colUrl")) };
        if (multi) columns.Add(Ui.Column(ctx.T("blog.colLanguage")));
        columns.AddRange(new[] { Ui.Column(ctx.T("posts.colDate")), Ui.Column(ctx.T("media.tags")), Ui.Column(ctx.T("pages.colStatus")), Ui.Column("", actions: true) });

        var table = Ui.Table(columns,
            rows.Select(p =>
            {
                var url = BlogUrls.Post(ctx, p);
                var cells = new List<object?> { Ui.Text(p.Title) with { Inline = true, Strong = true }, Ui.Link(url, url, newTab: true) };
                if (multi) cells.Add(Ui.Badge(Lang(p.Locale), UiTone.Muted));
                cells.AddRange(new object?[]
                {
                    p.PublishedAt.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                    Ui.Chips(BlogText.SplitTags(p.Tags)),
                    !p.IsPublished ? Ui.Badge(ctx.T("posts.draft"), UiTone.Muted)
                        : p.PublishedAt > now ? Ui.Badge(ctx.T("posts.scheduled"), UiTone.Info)
                        : Ui.Badge(ctx.T("posts.published"), UiTone.Success),
                    // the URL column already links the post — the row's actions are the core's pair: edit, delete
                    Ui.Inline(
                        Ui.Button(ctx.T("action.edit"), EditUrl(p.Id), UiButtonKind.Ghost, icon: "ti-pencil", small: true),
                        Ui.Action(ctx.T("action.delete"), "delete", p.Id.ToString(CultureInfo.InvariantCulture), UiButtonKind.Danger, confirm: ctx.T("posts.confirmDelete", p.Title), icon: "ti-trash"))
                });
                return Ui.RowFor(p.Title + " " + p.Slug + " " + p.Tags, cells.ToArray()) with { Tags = "lang-" + p.Locale };
            }),
            searchPlaceholder: ctx.T("action.search"), pageSize: 25, emptyText: ctx.T("posts.none")) with
        {
            // "New …" sits below the list, like every list in the admin (Seiten, Formulare …)
            Below = new UiNode[] { Ui.Button(ctx.T("posts.new"), EditUrl(0)) },
            Filter = multi
                ? Ui.ListFilter(ctx.T("blog.colLanguage"), languages.Select(l => ("lang-" + l, Lang(l))).Prepend(("", ctx.T("blog.allLanguages"))).ToArray())
                : null
        };
        return PluginResult.Page(table, ctx.T("posts.title"));
    }

    // ---- editor ----------------------------------------------------------------------------------

    public static async Task<PluginResult> EditAsync(IPluginContext ctx, CancellationToken ct)
    {
        var id = long.TryParse(ctx.Request.R("id"), out var parsed) ? parsed : 0;
        var posts = ctx.Data.Table("posts");
        BlogPost post;
        BlogPost? original = null;
        if (id == 0)
        {
            post = new BlogPost { PublishedAt = DateTime.UtcNow, Locale = BlogUrls.Default(ctx) };
            // "Übersetzung anlegen": a new post in another language, filled with the original's content to translate, joined to its group on save.
            var of = long.TryParse(ctx.Request.IsPost ? ctx.Request.F("translationOf") : ctx.Request.Q("translationOf"), out var o) ? o : 0;
            if (of > 0 && await BlogStore.GetAsync(posts, of, ct) is { } source)
            {
                original = source;
                var wanted = (ctx.Request.IsPost ? ctx.Request.F("locale") : ctx.Request.Q("locale")).Trim().ToLowerInvariant();
                post = source with
                {
                    Id = 0, IsPublished = false, PublishedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                    Locale = BlogUrls.Publishes(ctx, wanted) && wanted != source.Locale ? wanted : BlogUrls.Default(ctx), TranslationGroup = ""
                };
            }
        }
        else
        {
            var found = await BlogStore.GetAsync(posts, id, ct);
            if (found is null) return PluginResult.Redirect(Section);
            post = found;
        }

        string? error = null;
        if (ctx.Request.IsPost && ctx.Request.Action == "save")
        {
            var form = ctx.Request;
            if (string.IsNullOrWhiteSpace(form.F("title")))
            {
                error = ctx.T("blog.titleRequired");
                post = Read(ctx, form, post, form.F("slug").Trim());         // keep what was typed
            }
            else
            {
                var (saved, problem) = await SaveAsync(ctx, posts, form, post, original, ct);
                if (problem is null) return PluginResult.Redirect(EditUrl(saved), ctx.T("blog.saved"));
                error = problem;
                post = Read(ctx, form, post, form.F("slug").Trim());
            }
        }
        return await EditorPageAsync(ctx, posts, post, original, error, ct);
    }

    /// <summary>The language a form asks for: only a NEW post chooses one (a saved post keeps its language — its address and translations hang on it),
    /// and only one the site publishes in.</summary>
    private static string LocaleFrom(IPluginContext ctx, RequestData f, BlogPost current)
    {
        if (current.Id != 0) return current.Locale;
        var wanted = f.F("locale").Trim().ToLowerInvariant();
        return wanted.Length > 0 && BlogUrls.Publishes(ctx, wanted) ? wanted : current.Locale;
    }

    /// <summary>The post as the form describes it (not saved).</summary>
    private static BlogPost Read(IPluginContext ctx, RequestData f, BlogPost current, string slug) => current with
    {
        Title = f.F("title").Trim(), Slug = slug,
        TitleImage = string.IsNullOrWhiteSpace(f.F("titleimage")) ? null : f.F("titleimage").Trim(),
        Excerpt = f.F("excerpt").Trim(), ContentHtml = f.F("content"), Tags = BlogText.NormalizeTags(f.F("tags")),
        AttachmentsJson = string.IsNullOrWhiteSpace(f.F("attachments")) ? "[]" : f.F("attachments"),
        GalleryJson = BlogText.CleanGallery(f.F("gallery")), GalleryLayout = BlogStore.NormaliseGalleryLayout(f.F("gallerylayout")),
        // Only an http(s) link is a source; anything else (javascript:, a typo) is dropped, not stored.
        SourceUrl = BlogText.NormalizeUrl(f.F("sourceurl")),
        IsPublished = f.Form.ContainsKey("published"), Locale = LocaleFrom(ctx, f, current),
        PublishedAt = DateTime.TryParse(f.F("date"), out var d) ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : DateTime.UtcNow
    };

    private static async Task<(long Id, string? Error)> SaveAsync(IPluginContext ctx, IDataTable posts, RequestData form, BlogPost post, BlogPost? original, CancellationToken ct)
    {
        var slug = PluginText.Slugify(string.IsNullOrWhiteSpace(form.F("slug")) ? form.F("title") : form.F("slug"));
        if (string.IsNullOrWhiteSpace(slug)) slug = "beitrag";
        var locale = LocaleFrom(ctx, form, post);
        slug = await BlogStore.FreeSlugAsync(posts, slug, locale, post.Id, ct);

        var p = Read(ctx, form, post, slug);
        if (original is not null && post.Id == 0)
        {
            // One post per language in a group: a second translation into the same language is refused, not stored beside the first.
            if (p.Locale == original.Locale) return (0, ctx.T("blog.translation.sameLocale"));
            var group = await BlogOps.EnsureGroupAsync(posts, original, ct);
            if ((await BlogStore.TranslationsAsync(posts, group, ct)).Any(t => t.Locale == p.Locale)) return (0, ctx.T("blog.translation.exists", Lang(p.Locale)));
            p = p with { TranslationGroup = group };
        }
        p = p with
        {
            SourceName = p.SourceUrl is null || string.IsNullOrWhiteSpace(form.F("sourcename")) ? null : form.F("sourcename").Trim(),
            UpdatedAt = DateTime.UtcNow, CreatedAt = post.Id == 0 ? DateTime.UtcNow : post.CreatedAt
        };
        var id = await BlogStore.SaveAsync(posts, p, ct);
        await ctx.Events.EmitAsync("blog.post.saved", new JsonObject { ["id"] = id, ["slug"] = p.Slug, ["published"] = p.IsPublished, ["locale"] = p.Locale }, ct);
        return (id, null);
    }

    private static async Task<PluginResult> EditorPageAsync(IPluginContext ctx, IDataTable posts, BlogPost p, BlogPost? original, string? error, CancellationToken ct)
    {
        var isNew = p.Id == 0;
        var aiOn = ctx.Site.AiEnabled;
        var tags = await BlogStore.AllTagsAsync(posts, ct);
        var title = ctx.T(original is not null ? "blog.translation.newTitle" : isNew ? "posts.newTitle" : "posts.editTitle");
        var languages = BlogUrls.Languages(ctx);

        // An editor, not a settings card: what the post SAYS in the main column, how and when it appears at the side (like a shop product).
        UiNode? aiExcerpt = !aiOn ? null : Ui.Button(ctx.T("editor.aiExcerptButton"), "#", UiButtonKind.Ghost, icon: "ti-sparkles", small: true) with
        {
            Href = null, NoSubmit = true,
            Data = new Dictionary<string, string>
            {
                ["ai-seo"] = "", ["ai-url"] = $"{Section}/ai-excerpt?id={p.Id}", ["target"] = "post-excerpt",
                ["content-from"] = "post-content-editor", ["title-from"] = "post-title", ["title"] = ctx.T("editor.aiExcerptTitle")
            }
        };
        var main = new List<UiNode?>
        {
            Ui.Card(
                Ui.Input("title", ctx.T("posts.heading"), p.Title) with { Id = "post-title", Required = true, Placeholder = ctx.T("posts.headingPlaceholder") },
                Ui.Input("slug", ctx.T("posts.slug"), p.Slug) with { Id = "post-slug", Placeholder = ctx.T("posts.slugHint") },
                Ui.TextArea("excerpt", ctx.T("posts.excerpt"), p.Excerpt, 2) with { Id = "post-excerpt", Placeholder = ctx.T("posts.excerptPlaceholder"), Help = ctx.T("posts.excerptHint") },
                aiExcerpt,
                Ui.RichText("content", ctx.T("posts.content"), p.ContentHtml) with { Id = "post-content" }),
            Ui.Card(
                Ui.MediaList("gallery", ctx.T("posts.gallery"), p.GalleryJson) with { Id = "post-gallery" },
                Ui.Select("gallerylayout", ctx.T("posts.galleryLayout"), p.GalleryLayout,
                    Ui.Option("carousel", ctx.T("posts.galleryCarousel")), Ui.Option("single", ctx.T("posts.gallerySingle")), Ui.Option("grid", ctx.T("posts.galleryGrid"))))
        };
        var side = new List<UiNode?>
        {
            Ui.Card(
                Ui.Checkbox("published", ctx.T("posts.publishedLabel"), p.IsPublished),
                Ui.Input("date", ctx.T("posts.date"), p.PublishedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), UiInputType.Date) with { Id = "post-date" }),
            await LanguageCardAsync(ctx, posts, p, original, languages, ct),
            Ui.Card(Ui.Image("titleimage", ctx.T("posts.titleImage"), p.TitleImage ?? "") with { Id = "post-titleimage" }),
            Ui.Card(
                Ui.Tags("tags", ctx.T("media.tags"), p.Tags, tags) with { Id = "post-tags" },
                Ui.MediaList("attachments", ctx.T("posts.attachments"), p.AttachmentsJson, files: true) with { Id = "post-attachments" }),
            // link and its label one above the other: side by side they never lined up (one had a help line, the other not)
            Ui.Card(
                Ui.Input("sourceurl", ctx.T("posts.source"), p.SourceUrl ?? "") with { Id = "post-sourceurl", Placeholder = "https://www.instagram.com/p/…" },
                Ui.Input("sourcename", ctx.T("posts.sourceName"), p.SourceName ?? "") with { Id = "post-sourcename", Placeholder = ctx.T("posts.sourceNamePlaceholder"), Help = ctx.T("posts.sourceHint") })
        };
        var fields = new List<UiNode?>
        {
            error is null ? null : Ui.Alert(error, UiTone.Error),
            original is null ? null : Ui.Hidden("translationOf", original.Id.ToString(CultureInfo.InvariantCulture)),
            Ui.Columns(main, side),
            Ui.FormActions(
                Ui.Submit(ctx.T("action.save"), name: "action", value: "save"),
                isNew ? null : Ui.Button(ctx.T("posts.view"), BlogUrls.Post(ctx, p), UiButtonKind.Ghost, icon: "ti-external-link") with { NewTab = true },
                Ui.Button(ctx.T("action.back"), Section, UiButtonKind.Ghost))
        };

        var importDialog = Ui.Dialog("pi-dialog", ctx.T("posts.import.heading"),
            Ui.Html($"<div id=\"pi-config\" data-url=\"{WebUtility.HtmlEncode($"{Section}/import-link")}\" data-busy=\"{WebUtility.HtmlEncode(ctx.T("posts.import.busy"))}\" " +
                    $"data-done=\"{WebUtility.HtmlEncode(ctx.T("posts.import.done"))}\" data-overwrite=\"{WebUtility.HtmlEncode(ctx.T("posts.import.overwrite"))}\" " +
                    $"data-failed=\"{WebUtility.HtmlEncode(ctx.T("posts.import.failed"))}\"></div>"),
            Ui.Inline(Ui.Input("pi-url", "", "", UiInputType.Url) with { Id = "pi-url", Placeholder = ctx.T("posts.import.placeholder") },
                      Ui.Button(ctx.T("posts.import.button"), "#", small: true) with { Href = null, NoSubmit = true, Data = new Dictionary<string, string> { ["pi-go"] = "" } }),
            Ui.Muted(ctx.T("posts.import.hint")),
            Ui.Html("<div id=\"pi-status\" class=\"post-import__status\" role=\"status\" hidden></div>"));

        var scripts = new List<UiNode?> { Ui.Html($"<script src=\"{WebUtility.HtmlEncode(ctx.AssetUrl("blog-editor.js"))}\"></script>") };
        if (aiOn) scripts.Add(Ui.Html(AiTeaserDialog(ctx)));

        var tree = Ui.Stack(
            Ui.PageHead(null, title, Ui.Button(ctx.T("posts.import.heading"), "#", UiButtonKind.Ghost, icon: "ti-link", small: true) with { Href = null, Opens = "pi-dialog" }),
            importDialog,
            new UiForm(fields.Where(f => f is not null).Select(f => f!).ToList()) { Id = "post-form" },
            Ui.Stack(scripts.ToArray()));
        return PluginResult.Page(tree, title);
    }

    /// <summary>
    /// The side card about languages — only on a site with more than one: a new post picks its language; a saved post names its language,
    /// lists its translations (each opens in the editor) and offers "Übersetzung anlegen" for every language it has none in yet.
    /// </summary>
    private static async Task<UiNode?> LanguageCardAsync(IPluginContext ctx, IDataTable posts, BlogPost p, BlogPost? original, IReadOnlyList<string> languages, CancellationToken ct)
    {
        if (languages.Count <= 1 && languages.Contains(p.Locale)) return null;
        if (p.Id == 0)
        {
            var select = Ui.Select("locale", ctx.T("blog.language"), p.Locale, languages.Select(l => Ui.Option(l, Lang(l))).ToArray());
            return original is null
                ? Ui.Card(select)
                : Ui.Card(select, Ui.Muted(ctx.T("blog.translation.of", original.Title, Lang(original.Locale))));
        }

        var items = new List<UiNode?> { Ui.Text(ctx.T("blog.language") + ": " + Lang(p.Locale)) with { Strong = true } };
        var translations = (await BlogStore.TranslationsAsync(posts, p.TranslationGroup, ct)).Where(t => t.Id != p.Id).OrderBy(t => t.Locale).ToList();
        if (translations.Count > 0)
        {
            items.Add(Ui.Muted(ctx.T("blog.translation.list")));
            items.AddRange(translations.Select(t => (UiNode?)Ui.Button(Lang(t.Locale) + " · " + t.Title, EditUrl(t.Id), UiButtonKind.Ghost, icon: "ti-language", small: true)));
        }
        var missing = languages.Where(l => l != p.Locale && translations.All(t => t.Locale != l)).ToList();
        items.AddRange(missing.Select(l => (UiNode?)Ui.Button(ctx.T("blog.translation.create", Lang(l)),
            $"{Section}/posts/0?translationOf={p.Id}&locale={Uri.EscapeDataString(l)}", UiButtonKind.Ghost, icon: "ti-plus", small: true)));
        return Ui.Card(items.ToArray());
    }

    /// <summary>The dialog of the "KI: Teaser" button (before/after, an extra instruction, apply) — the markup the core's page editor uses for
    /// its SEO fields, drawn by the plugin with its own words and its own copy of the script.</summary>
    private static string AiTeaserDialog(IPluginContext ctx)
    {
        string A(string key) => WebUtility.HtmlEncode(ctx.T(key));
        return $"<dialog id=\"ai-seo-dialog\" class=\"ai-dialog\" data-lbl-before=\"{A("blog.ai.before")}\" data-lbl-after=\"{A("blog.ai.after")}\" data-lbl-working=\"{A("blog.ai.working")}\">" +
               $"<h3 id=\"ai-seo-title\">{A("editor.aiExcerptTitle")}</h3>" +
               $"<label class=\"ai-instr-label\" for=\"ai-seo-instruction\">{A("blog.ai.instruction")}</label>" +
               $"<textarea id=\"ai-seo-instruction\" rows=\"2\" placeholder=\"{A("blog.ai.placeholder")}\"></textarea>" +
               $"<div class=\"ai-dialog-actions\"><button type=\"button\" id=\"ai-seo-propose\" class=\"btn btn-sm\">{A("blog.ai.propose")}</button><span id=\"ai-seo-status\" class=\"ai-status muted\"></span></div>" +
               "<div id=\"ai-seo-diff\" class=\"ai-diff\"></div>" +
               $"<div class=\"ai-dialog-foot\"><button type=\"button\" id=\"ai-seo-apply\" class=\"btn btn-sm\" disabled>{A("blog.ai.apply")}</button>" +
               $"<button type=\"button\" id=\"ai-seo-cancel\" class=\"btn btn-sm btn-ghost\">{A("blog.ai.cancel")}</button></div></dialog>" +
               $"<script src=\"{WebUtility.HtmlEncode(ctx.AssetUrl("ai-seo-field.js"))}\"></script>";
    }

    // ---- helpers the editor calls ----------------------------------------------------------------

    /// <summary>"Aus Link übernehmen": reads the post behind a link once and hands the editor its fields. Nothing is saved here — the
    /// images are already in the media library, everything else arrives in the form and is saved (or changed, or dropped) with the normal save.</summary>
    public static async Task<PluginResult> ImportLinkAsync(IPluginContext ctx, CancellationToken ct)
    {
        LinkImport r;
        try { r = await ImportAsync(ctx.Site, ctx.Request.F("url"), ct); }
        catch (MissingMethodException) { r = new LinkImport(false, ctx.T("blog.importUnsupported"), "", "", "", null, "", "", Array.Empty<string>(), Array.Empty<string>(), ""); }
        return PluginResult.Json(new JsonObject
        {
            ["ok"] = r.Ok, ["error"] = r.Error, ["title"] = r.Title, ["excerpt"] = r.Excerpt, ["html"] = r.Html,
            ["date"] = r.PublishedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["sourceUrl"] = r.SourceUrl, ["sourceName"] = r.SourceName,
            ["images"] = new JsonArray(r.Images.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
            ["notes"] = new JsonArray(r.Notes.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()), ["via"] = r.Via
        });
    }

    // API 2.2: on an older host the method is missing — caught above, not a crash.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<LinkImport> ImportAsync(IPluginSite site, string url, CancellationToken ct) => site.ImportFromLinkAsync(url, ct);

    /// <summary>Proposes a teaser for the post, summarised from its content by the site's AI. The current editor content is posted so unsaved
    /// edits count; falls back to the saved post. Nothing is saved — the client shows a before/after and writes the accepted teaser into the
    /// excerpt field, saved with the normal post save.</summary>
    public static async Task<PluginResult> AiExcerptAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!ctx.Site.AiEnabled) return Fail(ctx.T("blog.aiOff"));
        var html = ctx.Request.F("content");
        var heading = ctx.Request.F("title").Trim();
        if (string.IsNullOrWhiteSpace(html) && long.TryParse(ctx.Request.Q("id"), out var id) && id != 0
            && await BlogStore.GetAsync(ctx.Data.Table("posts"), id, ct) is { } saved)
        {
            html = saved.ContentHtml;
            if (heading.Length == 0) heading = saved.Title;
        }
        var text = Regex.Replace(html ?? "", "<.*?>", " ").Replace("\n", " ").Replace("&nbsp;", " ").Replace("  ", " ").Trim();
        if (text.Length < 10) return Fail(ctx.T("blog.aiTooShort"));

        // The instruction the core's SEO helper used for a teaser, word for word: the same request gives the same kind of answer.
        var instruction = ctx.Request.F("instruction");
        var extra = string.IsNullOrWhiteSpace(instruction) ? "" : $"\n\nZusätzliche Anweisung: {instruction.Trim()}";
        const string System =
            "Du bist SEO-Redakteur. Schreibe einen kurzen, einladenden Teaser für diesen Blog-Beitrag (erscheint auf Karten und in Listen). Höchstens 200 Zeichen, ein bis zwei Sätze, aktiv "
          + "und konkret. Fasse NUR den gegebenen Inhalt zusammen — nichts erfinden, keine Platzhalter. Gib "
          + "AUSSCHLIESSLICH den Text zurück: ohne Anführungszeichen, ohne Label, ohne Markdown.";
        var trimmed = text.Length > 6000 ? text[..6000] : text;
        var answer = await ctx.Site.AskAiAsync(new PluginAiCall(new[]
        {
            new PluginAiMessage("system", System),
            new PluginAiMessage("user", $"Titel: {heading}\n\nInhalt:\n{trimmed}{extra}")
        }) { MaxTokens = 2000 }, ct);
        if (!answer.Ok) return Fail(string.IsNullOrWhiteSpace(answer.Error) ? ctx.T("blog.aiFailed") : answer.Error);
        var proposed = (answer.Text ?? "").Trim();
        return proposed.Length == 0
            ? Fail(ctx.T("blog.aiNone"))
            : PluginResult.Json(new JsonObject { ["ok"] = true, ["proposed"] = proposed });

        static PluginResult Fail(string message) => PluginResult.Json(new JsonObject { ["ok"] = false, ["error"] = message });
    }
}
