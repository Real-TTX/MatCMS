using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Content;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Blog;

/// <summary>
/// What the visitor sees of the blog: the post page (<c>/blog/{slug}</c>) and the posts block. This is the markup the Razor views
/// <c>Pages/Post.cshtml</c> and <c>Blocks/_Posts.cshtml</c> produced, written as code so the plugin can answer without Razor — the
/// classes, the structure and the inline styles are the same, because a template and a site's own CSS are written against them.
/// <para>Two encoders on purpose: the post page encoded with <c>WebUtility</c> (its own <c>Enc</c>), the block with the encoder Razor used —
/// the application's registered <see cref="HtmlEncoder"/>, which lets non-ASCII characters through. They differ for such characters;
/// keeping each as it was keeps the output as it was.</para>
/// </summary>
internal static class BlogPublic
{
    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string SafeImg(string? s) =>
        !string.IsNullOrWhiteSpace(s) && (s.StartsWith('/') || s.StartsWith("http")) && s.IndexOfAny(new[] { '"', '\'', '<', '>' }) < 0 ? s! : "";

    // ---- the post page --------------------------------------------------------------------------

    public static async Task<PluginResult> PostPageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var slug = ctx.Request.R("slug").Trim().ToLowerInvariant();
        var post = await BlogStore.BySlugAsync(ctx.Data.Table("posts"), slug, "de", ct);
        // Scheduling: the public sees a post only once it is published AND its PublishedAt has arrived; an admin can always
        // open it (preview of drafts + scheduled posts).
        if (post is null || (!ctx.User.IsAdmin && (!post.IsPublished || post.PublishedAt > DateTime.UtcNow))) return PluginResult.NotFound();

        // The configured canonical address wins over the request: behind a proxy the request says http.
        var origin = ctx.Site.Origin.TrimEnd('/');
        var meta = new List<KeyValuePair<string, string>>
        {
            new("og:type", "article"),
            new("og:title", post.Title),
            new("og:url", origin + "/blog/" + post.Slug),
            new("article:published_time", DateTime.SpecifyKind(post.PublishedAt, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ")),
        };
        if (!string.IsNullOrWhiteSpace(post.Excerpt)) meta.Add(new("og:description", post.Excerpt));
        if (!string.IsNullOrWhiteSpace(post.TitleImage) && (post.TitleImage.StartsWith('/') || post.TitleImage.StartsWith("http")))
            meta.Add(new("og:image", post.TitleImage.StartsWith('/') ? origin + post.TitleImage : post.TitleImage));

        var attachments = ParseList(post.AttachmentsJson, "name");
        var gallery = ParseList(post.GalleryJson, "alt");
        var titleImg = SafeImg(post.TitleImage);

        // Gallery (1:N images, e.g. an Instagram carousel), drawn by the gallery BLOCK itself so swiping, desktop arrows, the lightbox
        // and thumbnails are the ones the rest of the site uses — a second gallery implementation would only drift from the first.
        var galleryHtml = "";
        if (gallery.Count > 0)
        {
            var data = new JsonObject
            {
                ["layout"] = BlogStore.NormaliseGalleryLayout(post.GalleryLayout),
                ["columns"] = gallery.Count >= 3 ? "3" : gallery.Count.ToString(CultureInfo.InvariantCulture),
                ["carouselFit"] = "vary",
                ["showFilter"] = "no",
                ["images"] = new JsonArray(gallery.Select(g => (JsonNode)new JsonObject
                {
                    ["image"] = g.Url, ["alt"] = string.IsNullOrWhiteSpace(g.Extra) ? post.Title : g.Extra, ["caption"] = ""
                }).ToArray())
            };
            galleryHtml = "<div class=\"post-gallery\">" + await ctx.Site.RenderBlockAsync("gallery", data, ct) + "</div>";
            // The title image opening the gallery would be shown twice in a row — the gallery has it.
            if (string.Equals(titleImg, gallery[0].Url, StringComparison.Ordinal)) titleImg = "";
        }

        var sourceHtml = "";
        var srcUrl = SafeImg(post.SourceUrl);
        if (srcUrl.StartsWith("http"))
        {
            var label = string.IsNullOrWhiteSpace(post.SourceName)
                ? (Uri.TryCreate(srcUrl, UriKind.Absolute, out var su) ? su.Host.Replace("www.", "") : srcUrl)
                : post.SourceName!;
            sourceHtml = $"<p class=\"post-source\">{Enc(ctx.T("posts.sourceLabel"))}: <a href=\"{Enc(srcUrl)}\" target=\"_blank\" rel=\"noopener nofollow\">{Enc(label)} ↗</a></p>";
        }

        // Token values for the template's "post" layout part. Everything dynamic is HTML-encoded here; only the post body
        // (trusted stored HTML) is inserted raw.
        var heroHtml = titleImg.Length > 0 ? $"<div class=\"post-hero\"><img src=\"{Enc(titleImg)}\" alt=\"{Enc(post.Title)}\" /></div>" : "";
        var excerptHtml = !string.IsNullOrWhiteSpace(post.Excerpt) ? $"<p class=\"post-lead\">{Enc(post.Excerpt)}</p>" : "";

        var tags = (post.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tagsHtml = "";
        if (tags.Length > 0)
        {
            var tb = new StringBuilder("<div class=\"post-tags\">");
            foreach (var t in tags) tb.Append($"<span class=\"post-tag\">{Enc(t)}</span>");
            tb.Append("</div>");
            tagsHtml = tb.ToString();
        }

        var attHtml = "";
        if (attachments.Count > 0)
        {
            var ab = new StringBuilder($"<div class=\"post-attachments\"><h3>{Enc(ctx.T("posts.attachmentsHeading"))}</h3><ul class=\"att-list\">");
            foreach (var a in attachments)
            {
                var url = Enc(a.Url);
                var name = string.IsNullOrWhiteSpace(a.Extra) ? a.Url : a.Extra;
                var ext = Path.GetExtension(a.Url).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")
                    ab.Append($"<li><a href=\"{url}\" target=\"_blank\" rel=\"noopener\"><img src=\"{url}\" alt=\"{Enc(name)}\" style=\"max-width:220px;height:auto;border-radius:8px;\" /></a></li>");
                else
                    ab.Append($"<li><a href=\"{url}\" target=\"_blank\" rel=\"noopener\"><svg class=\"post-att-ic\" viewBox=\"0 0 24 24\" width=\"15\" height=\"15\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"M15 7l-6.5 6.5a1.5 1.5 0 0 0 3 3l6.5 -6.5a3 3 0 0 0 -6 -6l-6.5 6.5a4.5 4.5 0 0 0 9 9l6.5 -6.5\"/></svg> {Enc(name)}</a></li>");
            }
            ab.Append("</ul></div>");
            attHtml = ab.ToString();
        }

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["post_image"] = heroHtml,
            ["post_date"] = Enc(GermanDate.Long(post.PublishedAt.ToLocalTime())),
            ["post_title"] = Enc(post.Title),
            ["post_excerpt"] = excerptHtml,
            ["post_body"] = post.ContentHtml ?? "",
            ["post_tags"] = tagsHtml,
            ["post_attachments"] = attHtml,
            ["post_gallery"] = galleryHtml,
            ["post_source"] = sourceHtml,
        };

        // The active template may override the blog-detail layout ("post" part); otherwise the built-in default is used. A template
        // whose post layout predates the gallery and the source has no place for them; they follow the body there instead of silently
        // not appearing.
        var partHtml = ctx.Site.TemplatePart("post");
        if (!partHtml.Contains("{{post_gallery}}")) tokens["post_body"] += galleryHtml;
        if (!partHtml.Contains("{{post_source}}")) tokens["post_body"] += sourceHtml;
        var rendered = ctx.Site.RenderTemplatePart("post", tokens);

        return PluginResult.Page(rendered + PostPageStyle, post.Title, post.Excerpt, meta);
    }

    private static List<(string Url, string Extra)> ParseList(string? json, string extraField)
    {
        var list = new List<(string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var url = el.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)) continue;
                list.Add((url!, el.TryGetProperty(extraField, out var x) ? x.GetString() ?? "" : ""));
            }
        }
        catch (JsonException) { /* a malformed list shows as no entries */ }
        return list;
    }

    private const string PostPageStyle = """

        <style>
            .post-single { padding: 40px 0 64px; }
            .post-single .narrow { max-width: 760px; }
            .post-hero img { width: 100%; height: auto; border-radius: 14px; margin-bottom: 22px; box-shadow: 0 14px 34px rgba(0,0,0,.14); }
            .post-meta { font-size: 14px; letter-spacing: .04em; text-transform: uppercase; margin: 0 0 6px; }
            .post-title { font-family: var(--font-head); margin: 0 0 14px; }
            .post-lead { font-size: 1.15rem; line-height: 1.6; color: var(--ink); opacity: .85; margin: 0 0 22px; }
            .post-body { line-height: 1.75; }
            .post-body img { max-width: 100%; height: auto; border-radius: 8px; }
            .post-tags { display: flex; flex-wrap: wrap; gap: 8px; margin: 0 0 22px; }
            .post-tag { font-size: 12px; letter-spacing: .03em; text-transform: uppercase; background: var(--bg-alt); color: var(--ink); border-radius: 999px; padding: 4px 12px; }
            .post-attachments { margin-top: 34px; border-top: 1px solid #e7ddce; padding-top: 20px; }
            .post-attachments .att-list { list-style: none; padding: 0; display: flex; flex-wrap: wrap; gap: 16px; }
            .post-attachments a { text-decoration: none; color: var(--accent); font-weight: 600; }
            /* The gallery block brings its own section + container; inside a post it sits in the text column. */
            .post-gallery { margin: 26px 0; }
            .post-gallery .section { padding: 0; }
            .post-gallery .container { padding: 0; max-width: none; }
            .post-source { margin-top: 26px; font-size: 14px; opacity: .85; }
            .post-source a { color: var(--accent); text-decoration: none; }
            .post-source a:hover { text-decoration: underline; }
        </style>

        """;

    // ---- the posts block ------------------------------------------------------------------------

    /// <summary>What a posts block asks for: its own settings plus what the visitor clicked.</summary>
    internal sealed record ListRequest(string Tag, int Limit, int PerPage, string ActiveTag, int PageNumber, bool IncludeScheduled, string Locale);

    /// <summary>One page of posts, the tags to offer as filter chips and the page number after clamping to what exists.</summary>
    internal sealed record ListResult(IReadOnlyList<BlogPost> Posts, IReadOnlyList<string> FilterTags, int PageNumber, int TotalPages);

    /// <summary>The posts of the page's language, filtered, paged and ready to show.</summary>
    internal static async Task<ListResult> ListAsync(IDataTable posts, ListRequest request, CancellationToken ct = default)
    {
        // Base set: published in the CURRENT page language. Scheduling: a future PublishedAt hides the post from the public until that
        // date; admins still see it (preview) so they can check a scheduled post before it goes live.
        var nowUtc = DateTime.UtcNow;
        var q = DataQuery.Everything.Eq("IsPublished", true).Eq("Locale", request.Locale);
        if (!request.IncludeScheduled) q = q.Where("PublishedAt", DataOp.Le, nowUtc);
        q = q.OrderBy("PublishedAt", descending: true).OrderBy("Id", descending: true);
        var all = (await posts.QueryAsync(q, ct)).Select(BlogStore.From).ToList();

        var tag = request.Tag;
        if (!string.IsNullOrWhiteSpace(tag))
            all = all.Where(p => TagUtil.Split(p.Tags).Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
        if (request.Limit > 0) all = all.Take(request.Limit).ToList();

        // Filter chips = tags across the base set (minus the admin pre-filter tag).
        var filterTags = all.SelectMany(p => TagUtil.Split(p.Tags))
            .Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

        // Active (clicked) tag filter, then paginate.
        var activeTag = request.ActiveTag;
        var filtered = string.IsNullOrEmpty(activeTag)
            ? all
            : all.Where(p => TagUtil.Split(p.Tags).Contains(activeTag, StringComparer.OrdinalIgnoreCase)).ToList();

        var perPage = request.PerPage;
        var pageNum = request.PageNumber;
        var totalPages = perPage > 0 ? (int)Math.Ceiling(filtered.Count / (double)perPage) : 1;
        if (totalPages < 1) totalPages = 1;
        if (pageNum > totalPages) pageNum = totalPages;
        var page = perPage > 0 ? filtered.Skip((pageNum - 1) * perPage).Take(perPage).ToList() : filtered;
        return new ListResult(page, filterTags, pageNum, totalPages);
    }

    public static async Task<PluginResult> PostsBlockAsync(IPluginContext ctx, CancellationToken ct)
    {
        var block = ctx.Block!;
        var encoder = ctx.Unsafe.Services.GetService(typeof(HtmlEncoder)) as HtmlEncoder ?? HtmlEncoder.Default;
        string H(string? s) => encoder.Encode(s ?? "");
        string StrOr(string field, string fallback) => block.Get(field) is { Length: > 0 } v ? v : fallback;
        var heading = block.Get("heading");
        var display = StrOr("display", "cards");           // "cards" | "blog"
        var isBlog = display == "blog";
        var tag = block.Get("tag").Trim();                 // admin pre-filter
        var columns = StrOr("columns", "3");
        var showFilter = StrOr("showFilter", "no") == "yes";
        var limit = int.TryParse(block.Get("limit"), out var lim) && lim > 0 ? lim : 0;
        var perPage = int.TryParse(block.Get("perPage"), out var pp) && pp > 0 ? pp : 0;

        // Server-side filter + paging (works with paging, unlike the client-side gallery filter).
        var path = string.IsNullOrEmpty(ctx.Request.Path) ? "/" : ctx.Request.Path;
        var activeTag = ctx.Request.Q("ptag").Trim();
        var pageNum = int.TryParse(ctx.Request.Q("pseite"), out var pn) && pn > 1 ? pn : 1;

        string TagUrl(string t) => string.IsNullOrEmpty(t) ? path : path + "?ptag=" + Uri.EscapeDataString(t);
        string PageUrl(int pg)
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(activeTag)) qs.Add("ptag=" + Uri.EscapeDataString(activeTag));
            if (pg > 1) qs.Add("pseite=" + pg);
            return qs.Count > 0 ? path + "?" + string.Join("&", qs) : path;
        }

        // Admins also see scheduled posts (preview); the rest (language, tags, limit, paging) is applied by ListAsync.
        var locale = string.IsNullOrEmpty(ctx.Locale) ? MatCMS.Services.Localizer.DefaultCulture : ctx.Locale;
        var list = await ListAsync(ctx.Data.Table("posts"), new ListRequest(tag, limit, perPage, activeTag, pageNum, ctx.User.IsAdmin, locale), ct);
        var posts = list.Posts;
        pageNum = list.PageNumber;
        var totalPages = list.TotalPages;

        var sb = new StringBuilder();
        sb.Append("<section class=\"section posts-block\">\n    <div class=\"container").Append(isBlog ? " narrow" : "").Append("\">");
        if (!string.IsNullOrWhiteSpace(heading)) sb.Append("<div class=\"section__intro\"><h2>").Append(H(heading)).Append("</h2></div>");

        if (showFilter && list.FilterTags.Count > 0)
        {
            sb.Append("<div class=\"posts-filter\">")
              .Append("<a class=\"pfilter ").Append(string.IsNullOrEmpty(activeTag) ? "active" : "").Append("\" href=\"").Append(H(TagUrl(""))).Append("\">").Append(H(ctx.T("posts.filterAll"))).Append("</a>");
            foreach (var t in list.FilterTags)
                sb.Append("<a class=\"pfilter ").Append(string.Equals(activeTag, t, StringComparison.OrdinalIgnoreCase) ? "active" : "").Append("\" href=\"").Append(H(TagUrl(t))).Append("\">").Append(H(t)).Append("</a>");
            sb.Append("</div>");
        }

        if (posts.Count == 0)
        {
            sb.Append("<p class=\"muted\">").Append(H(ctx.T("posts.noneFound"))).Append("</p>");
        }
        else if (isBlog)
        {
            sb.Append("<div class=\"blog-list\">");
            foreach (var p in posts)
            {
                var img = SafeImg(p.TitleImage);
                sb.Append("<article class=\"blog-post\">");
                if (img.Length > 0)
                    sb.Append("<a href=\"/blog/").Append(H(p.Slug)).Append("\" class=\"blog-post__imglink\"><div class=\"blog-post__img\" style=\"background-image:url('").Append(H(img)).Append("')\"></div></a>");
                sb.Append("<p class=\"blog-post__meta\">").Append(H(GermanDate.Long(p.PublishedAt.ToLocalTime()))).Append("</p>");
                sb.Append("<h2 class=\"blog-post__title\"><a href=\"/blog/").Append(H(p.Slug)).Append("\">").Append(H(p.Title)).Append("</a></h2>");
                if (!string.IsNullOrWhiteSpace(p.Tags))
                {
                    sb.Append("<div class=\"post-card__tags\">");
                    foreach (var t in TagUtil.Split(p.Tags)) sb.Append("<a class=\"post-tag\" href=\"").Append(H(TagUrl(t))).Append("\">").Append(H(t)).Append("</a>");
                    sb.Append("</div>");
                }
                sb.Append("<div class=\"rich blog-post__body\">").Append(p.ContentHtml).Append("</div>");
                sb.Append("<a class=\"blog-post__more\" href=\"/blog/").Append(H(p.Slug)).Append("\">").Append(H(ctx.T("posts.toPost"))).Append("</a>");
                sb.Append("</article>");
            }
            sb.Append("</div>");
        }
        else
        {
            sb.Append("<div class=\"posts-grid cols-").Append(H(columns)).Append("\">");
            foreach (var p in posts)
            {
                var img = SafeImg(p.TitleImage);
                sb.Append("<article class=\"post-card\"><a class=\"post-card__link\" href=\"/blog/").Append(H(p.Slug)).Append("\">");
                if (img.Length > 0) sb.Append("<div class=\"post-card__img\" style=\"background-image:url('").Append(H(img)).Append("')\"></div>");
                sb.Append("<div class=\"post-card__body\">");
                if (!string.IsNullOrWhiteSpace(p.Tags))
                {
                    sb.Append("<div class=\"post-card__tags\">");
                    foreach (var t in TagUtil.Split(p.Tags)) sb.Append("<span class=\"post-tag\">").Append(H(t)).Append("</span>");
                    sb.Append("</div>");
                }
                sb.Append("<h3 class=\"post-card__title\">").Append(H(p.Title)).Append("</h3>");
                if (!string.IsNullOrWhiteSpace(p.Excerpt)) sb.Append("<p class=\"post-card__excerpt\">").Append(H(p.Excerpt)).Append("</p>");
                sb.Append("<span class=\"post-card__more\">").Append(H(ctx.T("posts.readMore"))).Append("</span></div></a></article>");
            }
            sb.Append("</div>");
        }

        if (totalPages > 1)
        {
            sb.Append("<nav class=\"posts-pager\" aria-label=\"").Append(H(ctx.T("posts.pagerNav"))).Append("\">");
            if (pageNum > 1) sb.Append("<a class=\"pager-btn\" href=\"").Append(H(PageUrl(pageNum - 1))).Append("\" rel=\"prev\">").Append(H(ctx.T("posts.pagerPrev"))).Append("</a>");
            for (var i = 1; i <= totalPages; i++)
                sb.Append("<a class=\"pager-num ").Append(i == pageNum ? "active" : "").Append("\" href=\"").Append(H(PageUrl(i))).Append("\">").Append(i).Append("</a>");
            if (pageNum < totalPages) sb.Append("<a class=\"pager-btn\" href=\"").Append(H(PageUrl(pageNum + 1))).Append("\" rel=\"next\">").Append(H(ctx.T("posts.pagerNext"))).Append("</a>");
            sb.Append("</nav>");
        }
        sb.Append("</div>\n</section>\n").Append(PostsBlockStyle);
        return PluginResult.Fragment(sb.ToString());
    }

    private const string PostsBlockStyle = """
        <style>
            .posts-grid { display: grid; gap: 26px; grid-template-columns: repeat(3, 1fr); }
            .posts-grid.cols-2 { grid-template-columns: repeat(2, 1fr); }
            @media (max-width: 820px) { .posts-grid, .posts-grid.cols-2 { grid-template-columns: 1fr; } }
            /* The card is always white — so it brings its own dark text. Inheriting the theme's colours put a dark theme's
               light headings and body text on white (N.S. Libera: pale titles nobody could read). Class selectors, so they
               win over a template's plain h3/p colours. */
            .post-card { background: #fff; border: 1px solid #ece3d5; border-radius: 14px; overflow: hidden; color: #3b342e;
                box-shadow: 0 2px 10px rgba(26,21,18,.05); transition: transform .15s ease, box-shadow .15s ease; }
            .post-card:hover { transform: translateY(-3px); box-shadow: 0 14px 30px rgba(26,21,18,.12); }
            .post-card__link { text-decoration: none; color: inherit; display: block; }
            .post-card__img { height: 190px; background-size: cover; background-position: center; }
            .post-card__body { padding: 18px 20px 20px; }
            .post-card__tags { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 8px; }
            .post-tag { font-size: 12px; font-weight: 600; color: var(--accent); background: var(--bg-alt, #f5f0e8);
                padding: 3px 9px; border-radius: 999px; text-decoration: none; }
            a.post-tag:hover { background: var(--accent); color: #fff; }
            .post-card__title { font-family: var(--font-head); margin: 0 0 8px; font-size: 1.25rem; color: #1a1512; }
            .post-card__excerpt { margin: 0 0 12px; line-height: 1.6; color: #4a423b; }
            .post-card__more { font-weight: 700; color: var(--accent); }

            /* Filter chips (server-side links) */
            .posts-filter { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 26px; }
            .pfilter { text-decoration: none; font-size: 14px; font-weight: 600; padding: 6px 14px; border-radius: 999px;
                border: 1px solid #e2d6c3; color: var(--ink); background: #fff; }
            .pfilter:hover { border-color: var(--accent); color: var(--accent); }
            .pfilter.active { background: var(--accent); color: #fff; border-color: var(--accent); }

            /* Blog (full post) list */
            .blog-list { display: flex; flex-direction: column; gap: 56px; }
            .blog-post__imglink { display: block; }
            .blog-post__img { height: 340px; background-size: cover; background-position: center; border-radius: 14px;
                box-shadow: 0 12px 30px rgba(26,21,18,.14); margin-bottom: 18px; }
            .blog-post__meta { font-size: 13px; letter-spacing: .05em; text-transform: uppercase; color: #8a7f72; margin: 0 0 6px; }
            .blog-post__title { font-family: var(--font-head); margin: 0 0 12px; font-size: clamp(1.6rem, 3vw, 2.2rem); }
            .blog-post__title a { color: inherit; text-decoration: none; }
            .blog-post__title a:hover { color: var(--accent); }
            .blog-post__body { line-height: 1.75; margin: 12px 0 14px; }
            .blog-post__body img { max-width: 100%; height: auto; border-radius: 8px; }
            .blog-post__more { font-weight: 700; color: var(--accent); text-decoration: none; }

            /* Pagination */
            .posts-pager { display: flex; flex-wrap: wrap; gap: 6px; justify-content: center; align-items: center; margin-top: 44px; }
            .posts-pager a { text-decoration: none; min-width: 40px; text-align: center; padding: 8px 12px; border-radius: 8px;
                border: 1px solid #e2d6c3; color: var(--ink); background: #fff; font-weight: 600; }
            .posts-pager a:hover { border-color: var(--accent); color: var(--accent); }
            .posts-pager .pager-num.active { background: var(--accent); color: #fff; border-color: var(--accent); }
        </style>
        """;
}
