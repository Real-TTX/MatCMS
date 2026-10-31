using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MatCMS.Plugin.Forum.ForumHtml;

namespace MatCMS.Plugin.Forum;

/// <summary>
/// The discussion of an article (or anything else on the site) in the forum — the way news sites like Golem.de do it: ONE thread per article in one
/// board ("News"), started by the first comment; under the article its posts are shown, writing happens in the forum with the site's accounts.
/// <para>The forum knows no article. Two events tie it to whoever owns the content, both carrying a <c>source</c> like <c>blog:12</c>:
/// <c>content.discussion</c> {source, title, url} — the forum answers {html} with the discussion to show under the content; and
/// <c>content.item</c> {source} — the owner answers {title, url} when the content exists and is public (the forum asks before it starts a thread,
/// so nobody can open discussions about things that are not there). The blog is the first owner (<c>blog:{slug}</c>); a shop could add products
/// the same way.</para>
/// </summary>
internal static partial class ForumPublic
{
    /// <summary>What a source looks like: an owner (letters and digits only — the first hyphen of an address separates it) and the content's own
    /// stable name, nothing that could break out of an address or a query. The blog names a post by its SLUG, not its id: a restore inserts the posts
    /// anew with new ids, and a discussion keyed by id then pointed at the wrong post (it did, on the first demo).</summary>
    private static readonly Regex SourceRx = new("^[a-z][a-z0-9]{0,30}:[a-z0-9][a-z0-9_-]{0,119}$", RegexOptions.CultureInvariant);

    /// <summary>At most this many posts are shown under an article; a longer discussion links to the rest in the forum.</summary>
    private const int DiscussionShown = 50;

    public static bool IsSource(string? s) => s is not null && SourceRx.IsMatch(s);

    /// <summary>The address of an article's discussion before (and after) it has a thread: <c>/forum/artikel/blog-12</c> (a colon is no friend of addresses).</summary>
    public static string DiscussionUrl(string source) => "/forum/artikel/" + source.Replace(':', '-');

    private static string? SourceOf(string segment)
    {
        var i = segment.IndexOf('-');
        if (i <= 0) return null;
        var s = segment[..i] + ":" + segment[(i + 1)..];
        return IsSource(s) ? s : null;
    }

    /// <summary>Only a local address may be linked back to (never <c>//host</c> or a scheme).</summary>
    private static string LocalUrl(string? url) =>
        url is { Length: > 1 } u && u[0] == '/' && u[1] != '/' && u[1] != '\\' && u.IndexOfAny(new[] { '"', '<', '>', '\'' }) < 0 ? u : "";

    // ---- the event: the discussion under an article -------------------------------------------------------------------------

    /// <summary>Answers <c>content.discussion</c>. Drawn for a GUEST — an event knows no visitor, and the article page is the same for everybody; writing
    /// happens on the forum's own page, where the visitor is known.</summary>
    public static async Task<JsonNode?> DiscussionAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        var source = payload?["source"]?.GetValue<string>();
        if (!IsSource(source)) return null;
        var slug = ForumSettings.DiscussionBoardSlug(ctx);
        if (slug.Length == 0) return null;
        var store = new ForumStore(ctx.Data);
        var board = await store.BoardBySlugAsync(slug, ct);
        if (board is null) return null;
        var guest = new Viewer(null, false, false, false);
        var thread = await store.ThreadBySourceAsync(source!, ct);
        var write = DiscussionUrl(source!);

        var sb = new StringBuilder("<section class=\"section forum-disc-wrap\"><div class=\"container\"><div class=\"forum-disc\" id=\"diskussion\">");
        var count = thread is null ? 0 : thread.ReplyCount + 1;
        sb.Append($"<div class=\"forum-disc-head\"><h2>{E(ctx.T("forum.disc.title"))}</h2>")
          .Append(count > 0 ? $"<span class=\"forum-disc-count\">{E(ctx.T(count == 1 ? "forum.disc.one" : "forum.disc.many", N(count)))}</span>" : "")
          .Append("</div>");

        if (!ForumStore.CanRead(board, guest))
        {
            // The board is for members: say so, and lead there — the forum decides on its own page who may read.
            sb.Append($"<p class=\"forum-disc-empty\">{E(ctx.T("forum.disc.membersOnly"))}</p>");
        }
        else if (thread is null)
        {
            sb.Append($"<p class=\"forum-disc-empty\">{E(ctx.T("forum.disc.none"))}</p>");
        }
        else
        {
            var (posts, total) = await store.ThreadPostsAsync(thread.Id, 1, DiscussionShown, ct);
            sb.Append("<ol class=\"forum-disc-list\">");
            foreach (var p in posts)
                sb.Append($"<li class=\"forum-disc-post\" id=\"kommentar-{p.Id}\"><span class=\"forum-disc-avatar\" aria-hidden=\"true\">{E(Initials(p.AuthorName))}</span>")
                  .Append($"<div class=\"forum-disc-main\"><div class=\"forum-disc-meta\"><strong>{MemberLink(p.AuthorId, p.AuthorName)}</strong> <a class=\"forum-disc-time\" href=\"{E(ForumUrls.Post(p.Id))}\">{TimeTag(ctx, p.CreatedAt)}</a></div>")
                  .Append($"<div class=\"forum-disc-body\">{ForumMarkup.ToHtml(p.Body)}</div></div></li>");
            sb.Append("</ol>");
            if (total > posts.Count)
                sb.Append($"<p><a href=\"{E(ForumUrls.Thread(thread))}\">{E(ctx.T("forum.disc.all", N(total)))}</a></p>");
            if (thread.Closed) sb.Append($"<p class=\"forum-disc-empty\">{E(ctx.T("forum.threadClosed"))}</p>");
        }

        sb.Append("<div class=\"forum-disc-actions\">");
        if (thread is null || !thread.Closed) sb.Append($"<a class=\"btn\" href=\"{E(write)}\">{E(ctx.T("forum.disc.write"))}</a>");
        if (thread is not null) sb.Append($"<a class=\"btn btn-outline\" href=\"{E(ForumUrls.Thread(thread))}\">{E(ctx.T("forum.disc.open"))}</a>");
        sb.Append("</div></div></div></section>");
        // Back from writing (?kommentar=id): to the new comment — a redirect cannot carry a #fragment.
        sb.Append("<script>(function(){var k=new URLSearchParams(location.search).get('kommentar');var el=k&&/^[0-9]+$/.test(k)&&document.getElementById('kommentar-'+k);if(el)el.scrollIntoView({block:'center'});})();</script>");
        return new JsonObject { ["html"] = "<style>" + DiscussionCss + "</style>" + sb, ["count"] = count };
    }

    // ---- the page: write about an article ---------------------------------------------------------------------------------

    /// <summary>
    /// <c>/forum/artikel/{source}</c>: an article's discussion. Once it has a thread this is only a way there (straight to the reply box); before, it asks
    /// the article's owner whether the article exists (<c>content.item</c>) and lets a signed-in member write the first comment, which starts the thread —
    /// titled like the article, in the discussion board, remembering where the article is.
    /// </summary>
    public static async Task<PluginResult> ArticleDiscussionAsync(IPluginContext ctx, CancellationToken ct)
    {
        var source = SourceOf(ctx.Request.R("source"));
        var slug = ForumSettings.DiscussionBoardSlug(ctx);
        if (source is null || slug.Length == 0) return NotFound(ctx);
        var store = new ForumStore(ctx.Data);
        var board = await store.BoardBySlugAsync(slug, ct);
        var v = await ViewerAsync(ctx, store, ct);
        if (board is null || !ForumStore.CanRead(board, v))
            return board is not null && !v.SignedIn ? PluginResult.Redirect(ForumUrls.SignIn(DiscussionUrl(source))) : NotFound(ctx);

        if (await store.ThreadBySourceAsync(source, ct) is { } existing && !ctx.Request.IsPost)
            return PluginResult.Redirect(ForumUrls.Thread(existing) + "?seite=letzte");   // the reply box sits at the end (a redirect cannot carry #antworten)

        // Does the article exist (and is it public)? Its owner says so — never the address the visitor sent.
        var item = (await ctx.Events.CollectAsync("content.item", new JsonObject { ["source"] = source }, ct)).OfType<JsonObject>().FirstOrDefault();
        var title = item?["title"]?.GetValue<string>()?.Trim() ?? "";
        var url = LocalUrl(item?["url"]?.GetValue<string>());
        if (title.Length == 0) return NotFound(ctx);
        if (title.Length > MaxTitle) title = title[..MaxTitle];

        if (!v.SignedIn) return PluginResult.Redirect(ForumUrls.SignIn(DiscussionUrl(source)));
        if (!ForumStore.CanWrite(board, v))
            return Page(ctx, title, Crumbs(ctx, (board.Title, ForumUrls.Board(board))) + Notice(ctx.T(v.Banned ? "forum.err.banned" : "forum.err.noWrite"), "err"), noIndex: true);

        string? error = null;
        var body = ctx.Request.F("body");
        var subsOn = ctx.Settings.GetBool(ForumSettings.Subscriptions);
        if (ctx.Request.IsPost)
        {
            error = await CheckPostAsync(ctx, store, v, body, null, ct);
            if (error is null)
            {
                // Two first comments at the same moment: the second one becomes a reply to the thread the first one started.
                long threadId, postId;
                var thread = await store.ThreadBySourceAsync(source, ct);
                if (thread is not null)
                {
                    postId = await store.ReplyAsync(thread, v.Member!, body.Trim(), ct);
                    threadId = thread.Id;
                }
                else
                {
                    (threadId, postId) = await store.CreateThreadAsync(board, v.Member!, title, body.Trim(), ct, source, url);
                    await ForumMail.NewThreadAsync(ctx, board, threadId, title, v.Member!, body, ct);
                }
                if (subsOn && ctx.Request.F("subscribe") == "1") await store.SubscribeAsync(v.Member!.Id, threadId, true, ct);
                await store.MarkReadAsync(v.Member!.Id, threadId, postId, ct);
                return PluginResult.Redirect(url.Length > 0 ? url + "?kommentar=" + postId.ToString(CultureInfo.InvariantCulture) : ForumUrls.Thread(threadId, title));
            }
        }

        var back = url.Length > 0 ? $"<p class=\"forum-disc-source\">{E(ctx.T("forum.disc.about"))} <a href=\"{E(url)}\">{E(title)}</a></p>" : "";
        var html = Toolbar(ctx, v, DiscussionUrl(source)) + Crumbs(ctx, (board.Title, ForumUrls.Board(board)), (title, null)) + back
                   + Editor(ctx, ctx.T("forum.disc.first"), body, submit: ctx.T("forum.disc.send"), subscribeBox: subsOn,
                       subscribed: !ctx.Request.IsPost || ctx.Request.F("subscribe") == "1", error: error, cancelUrl: url.Length > 0 ? url : ForumUrls.Board(board));
        return Page(ctx, ctx.T("forum.disc.title") + " – " + title, html, noIndex: true, status: error is null ? 200 : 422);
    }

    /// <summary>Above a discussion thread: the way back to its article.</summary>
    private static string SourceLink(IPluginContext ctx, ForumThread t) =>
        LocalUrl(t.SourceUrl) is { Length: > 0 } u ? $"<p class=\"forum-disc-source\">{E(ctx.T("forum.disc.about"))} <a href=\"{E(u)}\">{E(t.Title)}</a></p>" : "";

    /// <summary>The look under an article: the site's own variables (accent, lines, fonts), so it belongs to the page it sits on.</summary>
    private const string DiscussionCss = """
        .forum-disc-wrap.section{padding-top:0}
        .forum-disc{max-width:760px;margin:0 auto;border-top:1px solid var(--line,#e5e7eb);padding-top:28px}
        .forum-disc-head{display:flex;align-items:baseline;gap:12px;flex-wrap:wrap;margin-bottom:14px}
        .forum-disc-head h2{margin:0;font-size:1.4rem}
        .forum-disc-count{color:var(--muted,#6b7280);font-size:.95rem}
        .forum-disc-empty{color:var(--muted,#6b7280);margin:6px 0 16px}
        .forum-disc-list{list-style:none;margin:0 0 18px;padding:0;display:flex;flex-direction:column;gap:14px}
        .forum-disc-post{display:flex;gap:12px;align-items:flex-start}
        .forum-disc-avatar{flex:none;width:38px;height:38px;border-radius:50%;background:var(--accent,#2563eb);color:#fff;display:inline-flex;align-items:center;justify-content:center;font-weight:700;font-size:.85rem}
        .forum-disc-main{flex:1;min-width:0;background:var(--bg-alt,#f6f7f9);border:1px solid var(--line,#e5e7eb);border-radius:10px;padding:10px 14px}
        .forum-disc-meta{font-size:.9rem;margin-bottom:4px;display:flex;gap:8px;flex-wrap:wrap;align-items:baseline}
        .forum-disc-meta a{color:inherit;text-decoration:none}
        .forum-disc-time{color:var(--muted,#6b7280);font-size:.85rem}
        .forum-disc-body{overflow-wrap:anywhere;line-height:1.6}
        .forum-disc-body p{margin:.3em 0}
        .forum-disc-body blockquote{margin:.4em 0;padding:.4em .8em;border-left:3px solid var(--accent,#2563eb);background:rgba(127,127,127,.08);font-size:.95em}
        .forum-disc-body pre{overflow:auto;background:rgba(127,127,127,.1);padding:.6em .8em;border-radius:6px}
        .forum-disc-actions{display:flex;gap:10px;flex-wrap:wrap}
        .forum-disc-source{margin:0 0 12px;color:var(--muted,#6b7280)}
        """;
}
