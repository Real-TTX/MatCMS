using System.Globalization;
using System.Text;
using static MatCMS.Plugin.Forum.ForumHtml;

namespace MatCMS.Plugin.Forum;

/// <summary>
/// The forum's public pages. Reading is open as far as each board's rights allow; writing needs a site account (the guest area's, <c>/anmelden</c>).
/// <para>Every form is antiforgery-protected (a visitor's browser must not be made to post by another page), every value that came from a member is
/// encoded where it is written, and a post's text only becomes markup through <see cref="ForumMarkup"/>.</para>
/// </summary>
internal static class ForumPublic
{
    private const int MaxBody = 20000, MaxTitle = 120, MaxSignature = 300;
    private static readonly string Icon = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z\"/></svg>";
    private static readonly string IconPin = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M15 4.5l-4 4l-4 1.5l-1.5 1.5l7 7l1.5-1.5l1.5-4l4-4\"/><path d=\"M9 15l-4.5 4.5\"/><path d=\"M14.5 4l5.5 5.5\"/></svg>";
    private static readonly string IconLock = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><rect x=\"5\" y=\"11\" width=\"14\" height=\"10\" rx=\"2\"/><path d=\"M8 11V7a4 4 0 0 1 8 0v4\"/></svg>";

    // ---- who is asking --------------------------------------------------------------------------------------------

    public static async Task<Viewer> ViewerAsync(IPluginContext ctx, ForumStore store, CancellationToken ct)
    {
        var m = ctx.Member;
        var mod = m is not null && m.InRole(ForumSettings.Moderators(ctx));
        var banned = m is not null && !mod && (await store.ProfileAsync(m.Id, ct))?.Banned == true;
        return new Viewer(m, mod, ctx.User.IsAdmin, banned);
    }

    private static int PageOf(IPluginContext ctx) =>
        int.TryParse(ctx.Request.Q("seite"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0 ? Math.Min(p, 100000) : 1;

    private static string Ok(IPluginContext ctx) => ctx.Request.Q("ok") switch
    {
        "reported" => Notice(ctx.T("forum.ok.reported"), "ok"),
        "saved" => Notice(ctx.T("forum.ok.saved"), "ok"),
        "deleted" => Notice(ctx.T("forum.ok.deleted"), "ok"),
        "subscribed" => Notice(ctx.T("forum.ok.subscribed"), "ok"),
        "unsubscribed" => Notice(ctx.T("forum.ok.unsubscribed"), "ok"),
        "read" => Notice(ctx.T("forum.ok.read"), "ok"),
        "welcome" => Notice(ctx.T("forum.ok.welcome"), "ok"),
        _ => ""
    };

    private static PluginResult NotFound(IPluginContext ctx) =>
        Page(ctx, ctx.T("forum.notFound"), $"<h1>{E(ctx.T("forum.notFound"))}</h1><p><a href=\"{ForumUrls.Home}\">{E(ctx.T("forum.backHome"))}</a></p>", status: 404, noIndex: true);

    /// <summary>Which threads the member has not read up to their last post. A thread never opened counts as new only if it moved after the account was created
    /// and within the last month — otherwise a new member would see the whole history as "new" for ever.</summary>
    private static async Task<HashSet<long>> UnreadAsync(IPluginContext ctx, ForumStore store, Viewer v, IReadOnlyList<ForumThread> threads, CancellationToken ct)
    {
        var result = new HashSet<long>();
        if (v.Member is not { } m || threads.Count == 0) return result;
        var marks = await store.ReadMarksAsync(m.Id, threads.Select(t => t.Id).ToList(), ct);
        var since = (await ctx.Members.FindAsync(m.Id, ct))?.CreatedUtc ?? DateTime.UtcNow;
        var floor = DateTime.UtcNow.AddDays(-30);
        foreach (var t in threads)
        {
            if (marks.TryGetValue(t.Id, out var seen)) { if (t.LastPostId > seen) result.Add(t.Id); }
            else if (t.LastPostAt > since && t.LastPostAt > floor && t.LastAuthorId != m.Id) result.Add(t.Id);
        }
        return result;
    }

    // ---- index ----------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> IndexAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        var boards = await store.ReadableBoardsAsync(v, ct);

        var cats = new StringBuilder();
        if (boards.Count == 0) cats.Append($"<p class=\"forum-muted\">{E(ctx.T("forum.noBoards"))}</p>");
        foreach (var group in boards.GroupBy(b => b.Category.Trim()))
        {
            cats.Append("<section class=\"forum-cat\">");
            if (group.Key.Length > 0) cats.Append($"<h2>{E(group.Key)}</h2>");
            foreach (var b in group) cats.Append(BoardRow(ctx, b));
            cats.Append("</section>");
        }

        var latest = await store.LatestThreadsAsync(boards.Select(b => b.Id).ToList(), 6, ct);
        var unread = await UnreadAsync(ctx, store, v, latest, ct);
        var latestHtml = new StringBuilder();
        if (latest.Count > 0)
        {
            latestHtml.Append($"<section class=\"forum-box\"><h2>{E(ctx.T("forum.latestPosts"))}</h2><ul>");
            foreach (var t in latest)
                latestHtml.Append($"<li>{(unread.Contains(t.Id) ? $"<span class=\"forum-tag new\">{E(ctx.T("forum.new"))}</span>" : "")}<a href=\"{E(ForumUrls.Post(t.LastPostId))}\">{E(t.Title)}</a>"
                                  + $"<div class=\"forum-small forum-muted\">{E(ctx.T("forum.by"))} {MemberLink(t.LastAuthorId, t.LastAuthor)} · {TimeTag(ctx, t.LastPostAt)} · {E(boards.First(b => b.Id == t.BoardId).Title)}</div></li>");
            latestHtml.Append("</ul></section>");
        }

        var (threads, posts, members) = await store.TotalsAsync(ct);
        var stats = $"<div class=\"forum-stats\"><span>{E(ctx.T("forum.stat.threads"))}: <b>{N(threads)}</b></span><span>{E(ctx.T("forum.stat.posts"))}: <b>{N(posts)}</b></span><span>{E(ctx.T("forum.stat.members"))}: <b>{N(members)}</b></span></div>";

        var actions = v.Member is not null && boards.Count > 0
            ? $"<form method=\"post\" class=\"forum-actions\">{ctx.Antiforgery}<input type=\"hidden\" name=\"action\" value=\"allread\"><button class=\"forum-btn ghost sm\" type=\"submit\">{E(ctx.T("forum.markAllRead"))}</button></form>"
            : "";
        if (ctx.Request.IsPost && ctx.Request.Action == "allread" && v.Member is { } me)
        {
            await store.MarkAllReadAsync(me.Id, boards.Select(b => b.Id).ToList(), ct);
            return PluginResult.Redirect(ForumUrls.Home + "?ok=read");
        }

        var html = ctx.RenderPart("index", new Dictionary<string, string>
        {
            ["title"] = ForumSettings.Title(ctx), ["intro"] = ctx.Settings.Get(ForumSettings.Intro),
            ["toolbar_html"] = Toolbar(ctx, v, ForumUrls.Home), ["actions_html"] = actions, ["notice_html"] = Ok(ctx),
            ["categories_html"] = cats.ToString(), ["latest_html"] = latestHtml.ToString(), ["stats_html"] = stats
        });
        return Page(ctx, ForumSettings.Title(ctx), html, ctx.Settings.Get(ForumSettings.Intro), noIndex: !ctx.Settings.GetBool(ForumSettings.Indexing));
    }

    private static string BoardRow(IPluginContext ctx, Board b)
    {
        var last = b.LastPostId > 0
            ? $"<a class=\"t\" href=\"{E(ForumUrls.Post(b.LastPostId))}\">{E(b.LastThreadTitle)}</a>{E(ctx.T("forum.by"))} {MemberLink(b.LastAuthorId, b.LastAuthor)}<br><span class=\"forum-muted\">{TimeTag(ctx, b.LastPostAt)}</span>"
            : $"<span class=\"forum-muted\">{E(ctx.T("forum.noPosts"))}</span>";
        var lockTag = b.Locked ? $" <span class=\"forum-tag\">{E(ctx.T("forum.locked"))}</span>" : "";
        return $"<div class=\"forum-row\"><span class=\"ico\">{(b.Locked ? IconLock : Icon)}</span>"
               + $"<div><a class=\"name\" href=\"{E(ForumUrls.Board(b))}\">{E(b.Title)}</a>{lockTag}{(b.Description.Length > 0 ? $"<p class=\"desc\">{E(b.Description)}</p>" : "")}</div>"
               + $"<div class=\"num\"><b>{N(b.ThreadCount)}</b>{E(ctx.T("forum.threads"))}</div><div class=\"num\"><b>{N(b.PostCount)}</b>{E(ctx.T("forum.posts"))}</div>"
               + $"<div class=\"last\">{last}</div></div>";
    }

    // ---- a board ----------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> BoardAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        var board = await store.BoardBySlugAsync(ctx.Request.R("board"), ct);
        if (board is null || !ForumStore.CanRead(board, v)) return NotFound(ctx);
        var size = ForumSettings.ThreadPage(ctx);
        var page = PageOf(ctx);
        var (threads, total) = await store.BoardThreadsAsync(board.Id, page, size, ct);
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        if (page > pages) return PluginResult.Redirect(ForumUrls.Board(board, pages));
        var unread = await UnreadAsync(ctx, store, v, threads, ct);
        var perPage = ForumSettings.PostPage(ctx);

        var list = new StringBuilder();
        if (threads.Count == 0) list.Append($"<p class=\"forum-muted\">{E(ctx.T("forum.noThreads"))}</p>");
        else
        {
            list.Append("<div class=\"forum-list\">");
            foreach (var t in threads)
            {
                var isNew = unread.Contains(t.Id);
                var tags = (t.Sticky ? $"<span class=\"forum-tag\">{E(ctx.T("forum.sticky"))}</span>" : "") + (t.Closed ? $"<span class=\"forum-tag\">{E(ctx.T("forum.closed"))}</span>" : "")
                           + (isNew ? $"<span class=\"forum-tag new\">{E(ctx.T("forum.new"))}</span>" : "");
                var icon = t.Closed ? IconLock : t.Sticky ? IconPin : Icon;
                list.Append($"<div class=\"forum-row{(t.Sticky ? " sticky" : "")}\"><span class=\"ico{(isNew ? " new" : "")}\">{icon}</span>")
                    .Append($"<div>{tags}<a class=\"name\" href=\"{E(ForumUrls.Thread(t))}\">{E(t.Title)}</a>{MiniPages(t, perPage)}<p class=\"desc\">{E(ctx.T("forum.by"))} {MemberLink(t.AuthorId, t.AuthorName)} · {TimeTag(ctx, t.CreatedAt)}</p></div>")
                    .Append($"<div class=\"num\"><b>{N(t.ReplyCount)}</b>{E(ctx.T("forum.replies"))}</div><div class=\"num\"><b>{N(t.ViewCount)}</b>{E(ctx.T("forum.views"))}</div>")
                    .Append($"<div class=\"last\"><a href=\"{E(ForumUrls.Post(t.LastPostId))}\">{TimeTag(ctx, t.LastPostAt)}</a><br>{E(ctx.T("forum.by"))} {MemberLink(t.LastAuthorId, t.LastAuthor)}</div></div>");
            }
            list.Append("</div>");
        }

        string actions;
        if (ForumStore.CanWrite(board, v)) actions = $"<a class=\"forum-btn\" href=\"{E(ForumUrls.NewThread(board))}\">{E(ctx.T("forum.newThread"))}</a>";
        else if (!v.SignedIn && !board.Locked) actions = $"<a class=\"forum-btn ghost\" href=\"{E(ForumUrls.SignIn(ForumUrls.NewThread(board)))}\">{E(ctx.T("forum.signinToWrite"))}</a>";
        else actions = "";

        var html = ctx.RenderPart("board", new Dictionary<string, string>
        {
            ["title"] = board.Title, ["description"] = board.Description, ["toolbar_html"] = Toolbar(ctx, v, ForumUrls.Board(board)),
            ["crumbs_html"] = Crumbs(ctx, (board.Category.Length > 0 ? board.Category : board.Title, null)), ["actions_html"] = actions,
            ["notice_html"] = Ok(ctx) + (board.Locked ? Notice(ctx.T("forum.boardLocked")) : ""), ["threads_html"] = list.ToString(),
            ["pager_html"] = Pager(ctx, page, pages, p => ForumUrls.Board(board, p))
        });
        return Page(ctx, board.Title + " – " + ForumSettings.Title(ctx), html, board.Description,
            noIndex: !ctx.Settings.GetBool(ForumSettings.Indexing) || board.ReadAccess != Access.All);
    }

    // ---- the editor ---------------------------------------------------------------------------------------------------

    /// <summary>The write box with a small BBCode bar (the buttons wrap the selection; without script the box still works).</summary>
    private static string Editor(IPluginContext ctx, string heading, string body, bool withTitle = false, string title = "", string submit = "",
        bool subscribeBox = false, bool subscribed = false, string? error = null, string? cancelUrl = null)
    {
        var id = "fe" + Guid.NewGuid().ToString("N")[..8];
        var images = ctx.Settings.GetBool(ForumSettings.Images);
        var sb = new StringBuilder($"<form method=\"post\" class=\"forum-editor\" id=\"{id}\">{ctx.Antiforgery}");
        if (heading.Length > 0) sb.Append($"<h2>{E(heading)}</h2>");
        if (error is { Length: > 0 }) sb.Append(Notice(error, "err"));
        if (withTitle) sb.Append($"<label for=\"{id}t\">{E(ctx.T("forum.field.title"))}</label><input type=\"text\" id=\"{id}t\" name=\"title\" maxlength=\"{MaxTitle}\" required value=\"{E(title)}\">");
        sb.Append($"<label for=\"{id}b\">{E(ctx.T("forum.field.body"))}</label>");
        sb.Append("<div class=\"forum-bb\" role=\"toolbar\">")
          .Append(Bb("b", "<b>B</b>", ctx.T("forum.bb.bold"))).Append(Bb("i", "<i>I</i>", ctx.T("forum.bb.italic"))).Append(Bb("u", "<u>U</u>", ctx.T("forum.bb.underline")))
          .Append(Bb("s", "<s>S</s>", ctx.T("forum.bb.strike"))).Append(Bb("quote", "„“", ctx.T("forum.bb.quote"))).Append(Bb("code", "&lt;/&gt;", ctx.T("forum.bb.code")))
          .Append(Bb("url", "URL", ctx.T("forum.bb.link"))).Append(images ? Bb("img", "IMG", ctx.T("forum.bb.image")) : "").Append(Bb("list", "≡", ctx.T("forum.bb.list")))
          .Append("</div>");
        sb.Append($"<textarea id=\"{id}b\" name=\"body\" maxlength=\"{MaxBody}\" required>{E(body)}</textarea>");
        sb.Append($"<p class=\"forum-hint\">{E(ctx.T(images ? "forum.bb.hint" : "forum.bb.hintNoImg"))}</p>");
        sb.Append("<input type=\"text\" name=\"website\" value=\"\" tabindex=\"-1\" autocomplete=\"off\" aria-hidden=\"true\" style=\"position:absolute;left:-9999px\">");
        sb.Append("<div class=\"row\">");
        sb.Append($"<button class=\"forum-btn\" type=\"submit\" name=\"action\" value=\"save\">{E(submit.Length > 0 ? submit : ctx.T("forum.send"))}</button>");
        if (subscribeBox) sb.Append($"<label class=\"forum-check\"><input type=\"checkbox\" name=\"subscribe\" value=\"1\"{(subscribed ? " checked" : "")}> {E(ctx.T("forum.subscribeBox"))}</label>");
        if (cancelUrl is not null) sb.Append($"<a class=\"forum-btn ghost\" href=\"{E(cancelUrl)}\">{E(ctx.T("forum.cancel"))}</a>");
        sb.Append("</div></form>");
        // Wraps the selection in a tag pair; [list] gets its items, [url] asks nothing and lets the member type the address between the tags.
        sb.Append("<script>(function(){var f=document.getElementById('" + id + "');if(!f)return;var ta=f.querySelector('textarea');f.querySelectorAll('[data-bb]').forEach(function(b){b.addEventListener('click',function(){var t=b.getAttribute('data-bb'),s=ta.selectionStart,e=ta.selectionEnd,sel=ta.value.substring(s,e),open='['+t+']',close='[/'+t+']';if(t==='list'){open='[list]\\n[*] ';close='\\n[/list]';}var ins=open+sel+close;ta.setRangeText(ins,s,e,'end');if(!sel){var p=s+open.length;ta.setSelectionRange(p,p);}ta.focus();});});})();</script>");
        return sb.ToString();
    }

    private static string Bb(string tag, string label, string title) =>
        $"<button type=\"button\" data-bb=\"{tag}\" title=\"{E(title)}\" aria-label=\"{E(title)}\">{label}</button>";

    /// <summary>The checks every new post goes through. Null = fine; otherwise the reason, in the reader's language.</summary>
    private static async Task<string?> CheckPostAsync(IPluginContext ctx, ForumStore store, Viewer v, string body, string? title, CancellationToken ct)
    {
        if (ctx.Request.F("website").Length > 0) return ctx.T("forum.err.spam");
        if (v.Banned) return ctx.T("forum.err.banned");
        if (title is not null && (title.Trim().Length < 3 || title.Trim().Length > MaxTitle)) return ctx.T("forum.err.title", MaxTitle);
        var len = body.Trim().Length;
        if (len < 2) return ctx.T("forum.err.empty");
        if (len > MaxBody) return ctx.T("forum.err.long", MaxBody);
        if (!ctx.Settings.GetBool(ForumSettings.Images) && body.Contains("[img", StringComparison.OrdinalIgnoreCase)) return ctx.T("forum.err.noImages");
        var flood = ForumSettings.Flood(ctx);
        if (flood > 0 && !v.IsMod && v.Member is { } m && await store.ProfileAsync(m.Id, ct) is { LastPostAt: { } lastAt }
            && DateTime.SpecifyKind(lastAt, DateTimeKind.Utc).AddSeconds(flood) > DateTime.UtcNow)
            return ctx.T("forum.err.flood", flood);
        return null;
    }

    // ---- new thread ---------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> NewThreadAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        var board = await store.BoardBySlugAsync(ctx.Request.R("board"), ct);
        if (board is null || !ForumStore.CanRead(board, v)) return NotFound(ctx);
        if (!v.SignedIn) return PluginResult.Redirect(ForumUrls.SignIn(ForumUrls.NewThread(board)));
        if (!ForumStore.CanWrite(board, v))
            return Page(ctx, board.Title, Crumbs(ctx, (board.Title, ForumUrls.Board(board))) + Notice(ctx.T(v.Banned ? "forum.err.banned" : "forum.err.noWrite"), "err"), noIndex: true);

        string? error = null;
        var title = ctx.Request.F("title").Trim();
        var body = ctx.Request.F("body");
        var subsOn = ctx.Settings.GetBool(ForumSettings.Subscriptions);
        if (ctx.Request.IsPost)
        {
            error = await CheckPostAsync(ctx, store, v, body, title, ct);
            if (error is null)
            {
                var (threadId, postId) = await store.CreateThreadAsync(board, v.Member!, title, body.Trim(), ct);
                if (subsOn && ctx.Request.F("subscribe") == "1") await store.SubscribeAsync(v.Member!.Id, threadId, true, ct);
                await store.MarkReadAsync(v.Member!.Id, threadId, postId, ct);
                await ForumMail.NewThreadAsync(ctx, board, threadId, title, v.Member!, body, ct);
                return PluginResult.Redirect(ForumUrls.Thread(threadId, title));
            }
        }
        var html = Toolbar(ctx, v, ForumUrls.NewThread(board)) + Crumbs(ctx, (board.Title, ForumUrls.Board(board)), (ctx.T("forum.newThread"), null))
                   + Editor(ctx, ctx.T("forum.newThreadIn", board.Title), body, withTitle: true, title: title, submit: ctx.T("forum.createThread"),
                       subscribeBox: subsOn, subscribed: !ctx.Request.IsPost || ctx.Request.F("subscribe") == "1", error: error, cancelUrl: ForumUrls.Board(board));
        return Page(ctx, ctx.T("forum.newThread") + " – " + board.Title, html, noIndex: true, status: error is null ? 200 : 422);
    }

    // ---- a thread ------------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> ThreadAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        var segment = ctx.Request.R("id");
        var thread = await store.ThreadAsync(ForumUrls.IdOf(segment), ct);
        var board = thread is null ? null : await store.BoardAsync(thread.BoardId, ct);
        if (thread is null || board is null || !ForumStore.CanRead(board, v)) return NotFound(ctx);

        string? error = null;
        var draft = "";
        if (ctx.Request.IsPost)
        {
            var (result, err, kept) = await ThreadPostAsync(ctx, store, v, board, thread, ct);
            if (result is not null) return result;
            error = err;
            draft = kept;
            thread = await store.ThreadAsync(thread.Id, ct) ?? thread;
        }
        else
        {
            // One address per thread: an old or mistyped slug is sent to the right one (the id is what counts).
            var canonical = ForumUrls.Thread(thread).Split('?')[0];
            if (!string.Equals("/forum/t/" + segment, canonical, StringComparison.Ordinal))
            {
                var qs = ctx.Request.Query.Count == 0 ? "" : "?" + string.Join("&", ctx.Request.Query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
                return PluginResult.RedirectPermanent(canonical + qs);
            }
            await store.CountViewAsync(thread.Id, ct);
        }

        var size = ForumSettings.PostPage(ctx);
        var pages = Math.Max(1, (int)Math.Ceiling((thread.ReplyCount + 1) / (double)size));
        var page = ctx.Request.Q("seite") == "letzte" ? pages : Math.Min(PageOf(ctx), pages);
        var (posts, _) = await store.ThreadPostsAsync(thread.Id, page, size, ct);
        if (v.Member is { } me && posts.Count > 0) await store.MarkReadAsync(me.Id, thread.Id, posts.Max(p => p.Id), ct);

        var canWrite = ForumStore.CanWrite(board, v) && (!thread.Closed || v.IsMod);
        var postsHtml = new StringBuilder();
        var profiles = new Dictionary<long, ForumProfile?>();
        var number = (page - 1) * size;
        foreach (var p in posts)
        {
            number++;
            if (p.AuthorId > 0 && !profiles.ContainsKey(p.AuthorId)) profiles[p.AuthorId] = await store.ProfileAsync(p.AuthorId, ct);
            var prof = p.AuthorId > 0 ? profiles[p.AuthorId] : null;
            postsHtml.Append(PostHtml(ctx, v, board, thread, p, number, prof, canWrite));
        }

        var quoteId = long.TryParse(ctx.Request.Q("zitat"), out var qid) ? qid : 0;
        if (draft.Length == 0 && quoteId > 0 && await store.PostAsync(quoteId, ct) is { } quoted && quoted.ThreadId == thread.Id)
            draft = ForumMarkup.Quote(quoted.AuthorName, quoted.Id, quoted.Body);

        var subsOn = ctx.Settings.GetBool(ForumSettings.Subscriptions);
        var subscribed = v.Member is not null && await store.IsSubscribedAsync(v.Member.Id, thread.Id, ct);
        string reply;
        if (canWrite)
            reply = "<div id=\"antworten\">" + Editor(ctx, ctx.T("forum.reply"), draft, submit: ctx.T("forum.sendReply"), subscribeBox: subsOn && !subscribed,
                subscribed: true, error: error) + "</div>";
        else if (!v.SignedIn) reply = Notice(ctx.T("forum.signinToReply")) + $"<p><a class=\"forum-btn\" href=\"{E(ForumUrls.SignIn(ForumUrls.Thread(thread)))}\">{E(ctx.T("forum.signin"))}</a></p>";
        else if (thread.Closed) reply = Notice(ctx.T("forum.threadClosed"));
        else reply = Notice(ctx.T(v.Banned ? "forum.err.banned" : "forum.err.noWrite"));

        var actions = new StringBuilder("<div class=\"forum-actions\">");
        if (canWrite) actions.Append($"<a class=\"forum-btn\" href=\"#antworten\">{E(ctx.T("forum.reply"))}</a>");
        if (v.Member is not null && subsOn)
            actions.Append($"<form method=\"post\">{ctx.Antiforgery}<input type=\"hidden\" name=\"action\" value=\"{(subscribed ? "unsubscribe" : "subscribe")}\"><button class=\"forum-btn ghost\" type=\"submit\">{E(ctx.T(subscribed ? "forum.unsubscribe" : "forum.subscribe"))}</button></form>");
        actions.Append("</div>");

        var badges = (thread.Sticky ? $"<span class=\"forum-tag\">{E(ctx.T("forum.sticky"))}</span>" : "") + (thread.Closed ? $"<span class=\"forum-tag\">{E(ctx.T("forum.closed"))}</span>" : "");
        var meta = ctx.T("forum.threadMeta", thread.AuthorName, When(ctx, thread.CreatedAt), N(thread.ReplyCount), N(thread.ViewCount));
        var html = ctx.RenderPart("thread", new Dictionary<string, string>
        {
            ["title"] = thread.Title, ["meta"] = meta, ["badges_html"] = badges, ["toolbar_html"] = Toolbar(ctx, v, ForumUrls.Thread(thread)),
            ["crumbs_html"] = Crumbs(ctx, (board.Title, ForumUrls.Board(board))), ["actions_html"] = actions.ToString(),
            ["notice_html"] = Ok(ctx), ["posts_html"] = postsHtml.ToString(), ["pager_html"] = Pager(ctx, page, pages, p => ForumUrls.Thread(thread, p)),
            ["reply_html"] = reply, ["mod_html"] = v.IsMod ? await ModPanelAsync(ctx, store, thread, ct) : ""
        });
        var description = posts.FirstOrDefault() is { } first && page == 1 ? ForumMarkup.Plain(first.Body, 160) : null;
        return Page(ctx, thread.Title + " – " + ForumSettings.Title(ctx), html + Jump(ctx), description,
            status: error is null ? 200 : 422, noIndex: !ctx.Settings.GetBool(ForumSettings.Indexing) || board.ReadAccess != Access.All);
    }

    private static string PostHtml(IPluginContext ctx, Viewer v, Board board, ForumThread thread, ForumPost p, int number, ForumProfile? prof, bool canWrite)
    {
        var actions = new StringBuilder();
        if (canWrite) actions.Append($"<a href=\"{E(ForumUrls.Thread(thread))}?zitat={p.Id}#antworten\">{E(ctx.T("forum.quote"))}</a>");
        if (CanEdit(ctx, v, thread, p)) actions.Append($"<a href=\"{E(ForumUrls.EditPost(p.Id))}\">{E(ctx.T("forum.edit"))}</a>");
        if (v.SignedIn && !v.Banned && v.Id != p.AuthorId) actions.Append($"<a href=\"{E(ForumUrls.ReportPost(p.Id))}\" rel=\"nofollow\">{E(ctx.T("forum.report"))}</a>");
        if (v.IsMod)
            actions.Append(Confirmed(ctx, ctx.T("forum.delete"), ctx.T(p.Id == thread.FirstPostId ? "forum.confirmDeleteThread" : "forum.confirmDeletePost"),
                "delete_post", ("post", p.Id.ToString(CultureInfo.InvariantCulture))));
        var edited = p.EditedAt is { } at ? ctx.T(p.EditedBy == "mod" ? "forum.editedByMod" : "forum.edited", When(ctx, at)) : "";
        var signature = ctx.Settings.GetBool(ForumSettings.Signatures) && prof is { Signature.Length: > 0 } ? ForumMarkup.ToHtml(prof.Signature) : "";
        var authorMeta = p.AuthorId > 0 ? ctx.T("forum.authorPosts", N(prof?.PostCount ?? 0)) : "";
        return ctx.RenderPart("post", new Dictionary<string, string>
        {
            ["id"] = p.Id.ToString(CultureInfo.InvariantCulture), ["number"] = number.ToString(CultureInfo.InvariantCulture), ["url"] = ForumUrls.Post(p.Id),
            ["initials"] = Initials(p.AuthorName), ["author_html"] = MemberLink(p.AuthorId, p.AuthorName), ["author_meta"] = authorMeta,
            ["date_html"] = TimeTag(ctx, p.CreatedAt), ["body_html"] = ForumMarkup.ToHtml(p.Body), ["edited"] = edited, ["signature_html"] = signature,
            ["actions_html"] = actions.ToString(), ["is_first"] = p.Id == thread.FirstPostId ? "1" : ""
        });
    }

    /// <summary>A member may change their own post (within the edit window, while the thread is open); a moderator any.</summary>
    private static bool CanEdit(IPluginContext ctx, Viewer v, ForumThread t, ForumPost p)
    {
        if (v.IsMod) return true;
        if (!v.SignedIn || v.Banned || p.AuthorId != v.Id || t.Closed) return false;
        var window = ForumSettings.EditWindow(ctx);
        return window == 0 || p.CreatedAt.AddMinutes(window) > DateTime.UtcNow;
    }

    /// <summary>A thread's POSTs: a reply, (un)subscribing, and the moderators' actions. Answers a redirect, or null with an error and the text to keep.</summary>
    private static async Task<(PluginResult? Result, string? Error, string Draft)> ThreadPostAsync(IPluginContext ctx, ForumStore store, Viewer v, Board board, ForumThread thread, CancellationToken ct)
    {
        var action = ctx.Request.Action;
        var url = ForumUrls.Thread(thread);
        if (v.Member is not { } me) return (PluginResult.Redirect(ForumUrls.SignIn(url)), null, "");
        switch (action)
        {
            case "subscribe" or "unsubscribe":
                await store.SubscribeAsync(me.Id, thread.Id, action == "subscribe", ct);
                return (PluginResult.Redirect(url + "?ok=" + (action == "subscribe" ? "subscribed" : "unsubscribed")), null, "");
            case "save":
            {
                var body = ctx.Request.F("body");
                if (!ForumStore.CanWrite(board, v) || thread.Closed && !v.IsMod) return (null, ctx.T("forum.err.noWrite"), body);
                var error = await CheckPostAsync(ctx, store, v, body, null, ct);
                if (error is not null) return (null, error, body);
                var postId = await store.ReplyAsync(thread, me, body.Trim(), ct);
                if (ctx.Settings.GetBool(ForumSettings.Subscriptions) && ctx.Request.F("subscribe") == "1") await store.SubscribeAsync(me.Id, thread.Id, true, ct);
                await store.MarkReadAsync(me.Id, thread.Id, postId, ct);
                await ForumMail.ReplyAsync(ctx, store, thread, postId, me, body, ct);
                return (PluginResult.Redirect(ForumUrls.Post(postId)), null, "");
            }
        }
        if (!v.IsMod) return (PluginResult.Status(403), null, "");
        switch (action)
        {
            case "delete_post" when long.TryParse(ctx.Request.F("post"), out var pid) && await store.PostAsync(pid, ct) is { } post && post.ThreadId == thread.Id:
                await store.DeletePostAsync(post, ct);
                return (PluginResult.Redirect(post.Id == thread.FirstPostId ? ForumUrls.Board(board) + "?ok=deleted" : url + "?ok=deleted"), null, "");
            case "sticky": await store.SetThreadFlagAsync(thread.Id, "sticky", !thread.Sticky, ct); break;
            case "close": await store.SetThreadFlagAsync(thread.Id, "closed", !thread.Closed, ct); break;
            case "rename":
                var title = ctx.Request.F("title").Trim();
                if (title.Length is >= 3 and <= MaxTitle) await store.RenameThreadAsync(thread.Id, title, ct);
                break;
            case "move" when long.TryParse(ctx.Request.F("board"), out var to) && await store.BoardAsync(to, ct) is { } target:
                await store.MoveThreadAsync(thread, target.Id, ct);
                break;
            case "delete_thread":
                await store.DeleteThreadAsync(thread.Id, ct);
                return (PluginResult.Redirect(ForumUrls.Board(board) + "?ok=deleted"), null, "");
        }
        var fresh = await store.ThreadAsync(thread.Id, ct) ?? thread;
        return (PluginResult.Redirect(ForumUrls.Thread(fresh) + "?ok=saved"), null, "");
    }

    /// <summary>
    /// A destructive button that asks first — without a browser dialog: the first click opens "really?" with the real button beside it (a &lt;details&gt;,
    /// so it works without script too).
    /// </summary>
    private static string Confirmed(IPluginContext ctx, string label, string question, string action, params (string Name, string Value)[] hidden)
    {
        var fields = string.Concat(hidden.Select(h => $"<input type=\"hidden\" name=\"{E(h.Name)}\" value=\"{E(h.Value)}\">"));
        return $"<details class=\"forum-confirm\"><summary>{E(label)}</summary><span>{E(question)}</span>"
               + $"<form method=\"post\">{ctx.Antiforgery}<input type=\"hidden\" name=\"action\" value=\"{E(action)}\">{fields}<button class=\"forum-btn danger sm\" type=\"submit\">{E(ctx.T("forum.confirmYes"))}</button></form></details>";
    }

    private static async Task<string> ModPanelAsync(IPluginContext ctx, ForumStore store, ForumThread thread, CancellationToken ct)
    {
        string Btn(string action, string label, string? confirm = null) => confirm is not null
            ? Confirmed(ctx, label, confirm, action)
            : $"<form method=\"post\">{ctx.Antiforgery}<input type=\"hidden\" name=\"action\" value=\"{action}\"><button class=\"forum-btn ghost sm\" type=\"submit\">{E(label)}</button></form>";
        var boards = await store.BoardsAsync(ct);
        var options = string.Concat(boards.Select(b => $"<option value=\"{b.Id}\"{(b.Id == thread.BoardId ? " selected" : "")}>{E(b.Title)}</option>"));
        return $"<section class=\"forum-mod\"><h2>{E(ctx.T("forum.mod.title"))}</h2>"
               + Btn("sticky", ctx.T(thread.Sticky ? "forum.mod.unsticky" : "forum.mod.sticky"))
               + Btn("close", ctx.T(thread.Closed ? "forum.mod.open" : "forum.mod.close"))
               + $"<form method=\"post\">{ctx.Antiforgery}<input type=\"hidden\" name=\"action\" value=\"move\"><select name=\"board\" aria-label=\"{E(ctx.T("forum.mod.moveTo"))}\">{options}</select><button class=\"forum-btn ghost sm\" type=\"submit\">{E(ctx.T("forum.mod.move"))}</button></form>"
               + $"<form method=\"post\">{ctx.Antiforgery}<input type=\"hidden\" name=\"action\" value=\"rename\"><input type=\"text\" name=\"title\" value=\"{E(thread.Title)}\" maxlength=\"{MaxTitle}\" aria-label=\"{E(ctx.T("forum.field.title"))}\" style=\"padding:7px 10px;border:1px solid var(--f-line);border-radius:8px;font:inherit;background:transparent;color:inherit\"><button class=\"forum-btn ghost sm\" type=\"submit\">{E(ctx.T("forum.mod.rename"))}</button></form>"
               + Btn("delete_thread", ctx.T("forum.mod.delete"), ctx.T("forum.confirmDeleteThread"))
               + "</section>";
    }

    // ---- a single post ---------------------------------------------------------------------------------------------

    /// <summary>/forum/p/{id}: the thread page that holds the post, scrolled to it.</summary>
    public static async Task<PluginResult> PostAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        if (!long.TryParse(ctx.Request.R("id"), out var id) || await store.PostAsync(id, ct) is not { } post) return NotFound(ctx);
        var thread = await store.ThreadAsync(post.ThreadId, ct);
        var board = thread is null ? null : await store.BoardAsync(thread.BoardId, ct);
        if (thread is null || board is null || !ForumStore.CanRead(board, v)) return NotFound(ctx);
        var position = await store.PositionAsync(post, ct);
        var page = (int)Math.Max(1, Math.Ceiling(position / (double)ForumSettings.PostPage(ctx)));
        // The post is named in the query, not as a fragment: a plugin's redirect may not carry a "#" (the host turns such a target into "/"),
        // so the thread page jumps to it itself (Jump).
        var url = ForumUrls.Thread(thread, page);
        url += (url.Contains('?') ? "&" : "?") + "p=" + post.Id.ToString(CultureInfo.InvariantCulture);
        var ok = ctx.Request.Q("ok");
        if (ok.Length > 0) url += "&ok=" + Uri.EscapeDataString(ok);
        return PluginResult.Redirect(url);
    }

    /// <summary>
    /// "?p=12" on a thread page: replaces the address with the one ending in "#p12" — without a new history entry — so the browser scrolls to the post and
    /// marks it (:target), and a reload or a copied link shows the same place.
    /// </summary>
    private static string Jump(IPluginContext ctx)
    {
        if (!long.TryParse(ctx.Request.Q("p"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) return "";
        return "<script>(function(){var s=location.search.replace(/([?&])p=\\d+&?/,'$1').replace(/[?&]$/,'');location.replace(location.pathname+s+'#p" + id.ToString(CultureInfo.InvariantCulture) + "');})();</script>";
    }

    public static async Task<PluginResult> EditPostAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        if (!long.TryParse(ctx.Request.R("id"), out var id) || await store.PostAsync(id, ct) is not { } post) return NotFound(ctx);
        var thread = await store.ThreadAsync(post.ThreadId, ct);
        var board = thread is null ? null : await store.BoardAsync(thread.BoardId, ct);
        if (thread is null || board is null || !ForumStore.CanRead(board, v)) return NotFound(ctx);
        if (!v.SignedIn) return PluginResult.Redirect(ForumUrls.SignIn(ForumUrls.EditPost(id)));
        if (!CanEdit(ctx, v, thread, post)) return Page(ctx, ctx.T("forum.edit"), Notice(ctx.T("forum.err.noEdit"), "err"), status: 403, noIndex: true);

        var isFirst = post.Id == thread.FirstPostId;
        var body = ctx.Request.IsPost ? ctx.Request.F("body") : post.Body;
        var title = ctx.Request.IsPost ? ctx.Request.F("title").Trim() : thread.Title;
        string? error = null;
        if (ctx.Request.IsPost)
        {
            if (ctx.Request.Action == "delete" && v.IsMod)
            {
                await store.DeletePostAsync(post, ct);
                return PluginResult.Redirect(isFirst ? ForumUrls.Board(board) + "?ok=deleted" : ForumUrls.Thread(thread) + "?ok=deleted");
            }
            var len = body.Trim().Length;
            if (ctx.Request.F("website").Length > 0) error = ctx.T("forum.err.spam");
            else if (len < 2) error = ctx.T("forum.err.empty");
            else if (len > MaxBody) error = ctx.T("forum.err.long", MaxBody);
            else if (isFirst && title.Length is < 3 or > MaxTitle) error = ctx.T("forum.err.title", MaxTitle);
            else if (!ctx.Settings.GetBool(ForumSettings.Images) && body.Contains("[img", StringComparison.OrdinalIgnoreCase)) error = ctx.T("forum.err.noImages");
            if (error is null)
            {
                // "mod" when a moderator changed somebody else's post — the post then says so; a member's own edit just says when.
                await store.EditPostAsync(post.Id, body.Trim(), v.IsMod && post.AuthorId != v.Id ? "mod" : "self", ct);
                if (isFirst && title != thread.Title) await store.RenameThreadAsync(thread.Id, title, ct);
                return PluginResult.Redirect(ForumUrls.Post(post.Id) + "?ok=saved");
            }
        }
        var html = Toolbar(ctx, v, ForumUrls.EditPost(id)) + Crumbs(ctx, (board.Title, ForumUrls.Board(board)), (thread.Title, ForumUrls.Thread(thread)), (ctx.T("forum.edit"), null))
                   + Editor(ctx, ctx.T("forum.editPost"), body, withTitle: isFirst, title: title, submit: ctx.T("forum.save"), error: error, cancelUrl: ForumUrls.Post(post.Id));
        if (v.IsMod)
            html += "<div class=\"forum-actions\">" + Confirmed(ctx, ctx.T(isFirst ? "forum.mod.delete" : "forum.deletePost"),
                ctx.T(isFirst ? "forum.confirmDeleteThread" : "forum.confirmDeletePost"), "delete") + "</div>";
        return Page(ctx, ctx.T("forum.editPost"), html, noIndex: true, status: error is null ? 200 : 422);
    }

    public static async Task<PluginResult> ReportAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        if (!long.TryParse(ctx.Request.R("id"), out var id) || await store.PostAsync(id, ct) is not { } post) return NotFound(ctx);
        var thread = await store.ThreadAsync(post.ThreadId, ct);
        var board = thread is null ? null : await store.BoardAsync(thread.BoardId, ct);
        if (thread is null || board is null || !ForumStore.CanRead(board, v)) return NotFound(ctx);
        if (v.Member is not { } me) return PluginResult.Redirect(ForumUrls.SignIn(ForumUrls.ReportPost(id)));
        if (v.Banned) return Page(ctx, ctx.T("forum.report"), Notice(ctx.T("forum.err.banned"), "err"), status: 403, noIndex: true);

        string? error = null;
        var reason = ctx.Request.F("reason").Trim();
        if (ctx.Request.IsPost)
        {
            if (reason.Length < 3) error = ctx.T("forum.err.reason");
            else
            {
                await store.ReportAsync(post, me, reason.Length > 1000 ? reason[..1000] : reason, ct);
                await ForumMail.ReportAsync(ctx, thread, post, me, reason, ct);
                return PluginResult.Redirect(ForumUrls.Post(post.Id) + "?ok=reported");
            }
        }
        var html = Toolbar(ctx, v, ForumUrls.Thread(thread)) + Crumbs(ctx, (board.Title, ForumUrls.Board(board)), (thread.Title, ForumUrls.Thread(thread)), (ctx.T("forum.report"), null))
                   + $"<form method=\"post\" class=\"forum-editor\">{ctx.Antiforgery}<h2>{E(ctx.T("forum.reportTitle"))}</h2>"
                   + (error is null ? "" : Notice(error, "err"))
                   + $"<blockquote class=\"forum-quote\"><cite>{E(post.AuthorName)}</cite><div>{E(ForumMarkup.Plain(post.Body, 400))}</div></blockquote>"
                   + $"<label for=\"reason\">{E(ctx.T("forum.reportReason"))}</label><textarea id=\"reason\" name=\"reason\" maxlength=\"1000\" required style=\"min-height:100px\">{E(reason)}</textarea>"
                   + $"<div class=\"row\"><button class=\"forum-btn\" type=\"submit\">{E(ctx.T("forum.reportSend"))}</button><a class=\"forum-btn ghost\" href=\"{E(ForumUrls.Post(post.Id))}\">{E(ctx.T("forum.cancel"))}</a></div></form>";
        return Page(ctx, ctx.T("forum.report"), html, noIndex: true, status: error is null ? 200 : 422);
    }

    // ---- lists across the forum ------------------------------------------------------------------------------------

    public static async Task<PluginResult> SearchAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        var q = ctx.Request.Q("q").Trim();
        if (q.Length > 100) q = q[..100];
        var boards = await store.ReadableBoardsAsync(v, ct);
        var sb = new StringBuilder(Toolbar(ctx, v, ForumUrls.Search, q) + Crumbs(ctx, (ctx.T("forum.search"), null)));
        sb.Append($"<div class=\"forum-head\"><h1>{E(q.Length > 0 ? ctx.T("forum.searchFor", q) : ctx.T("forum.search"))}</h1></div>");
        if (q.Length >= 2)
        {
            var hits = await store.SearchAsync(q, boards.Select(b => b.Id).ToList(), 50, ct);
            if (hits.Count == 0) sb.Append($"<p class=\"forum-muted\">{E(ctx.T("forum.searchNone"))}</p>");
            else
            {
                sb.Append("<div class=\"forum-list\">");
                foreach (var (t, p) in hits)
                {
                    var board = boards.First(b => b.Id == t.BoardId);
                    var href = p is null ? ForumUrls.Thread(t) : ForumUrls.Post(p.Id);
                    var text = p is null ? "" : ForumMarkup.Plain(p.Body, 220);
                    sb.Append($"<div class=\"forum-hit\"><a class=\"name\" href=\"{E(href)}\"><b>{E(t.Title)}</b></a><div class=\"forum-small forum-muted\">{E(board.Title)} · {(p is null ? MemberLink(t.AuthorId, t.AuthorName) : MemberLink(p.AuthorId, p.AuthorName))} · {TimeTag(ctx, p?.CreatedAt ?? t.CreatedAt)}</div>{(text.Length > 0 ? $"<p>{E(text)}</p>" : "")}</div>");
                }
                sb.Append("</div>");
            }
        }
        else if (q.Length > 0) sb.Append($"<p class=\"forum-muted\">{E(ctx.T("forum.searchShort"))}</p>");
        return Page(ctx, ctx.T("forum.search") + " – " + ForumSettings.Title(ctx), sb.ToString(), noIndex: true);
    }

    public static async Task<PluginResult> LatestAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        var boards = await store.ReadableBoardsAsync(v, ct);
        var threads = await store.LatestThreadsAsync(boards.Select(b => b.Id).ToList(), 40, ct);
        var unread = await UnreadAsync(ctx, store, v, threads, ct);
        var sb = new StringBuilder(Toolbar(ctx, v, ForumUrls.Latest) + Crumbs(ctx, (ctx.T("forum.latest"), null)));
        sb.Append($"<div class=\"forum-head\"><h1>{E(ctx.T("forum.latest"))}</h1></div>");
        if (threads.Count == 0) sb.Append($"<p class=\"forum-muted\">{E(ctx.T("forum.noThreads"))}</p>");
        else
        {
            sb.Append("<div class=\"forum-list\">");
            foreach (var t in threads)
            {
                var board = boards.First(b => b.Id == t.BoardId);
                var isNew = unread.Contains(t.Id);
                sb.Append($"<div class=\"forum-row\"><span class=\"ico{(isNew ? " new" : "")}\">{(t.Closed ? IconLock : Icon)}</span>")
                  .Append($"<div>{(isNew ? $"<span class=\"forum-tag new\">{E(ctx.T("forum.new"))}</span>" : "")}<a class=\"name\" href=\"{E(ForumUrls.Post(t.LastPostId))}\">{E(t.Title)}</a><p class=\"desc\"><a href=\"{E(ForumUrls.Board(board))}\">{E(board.Title)}</a></p></div>")
                  .Append($"<div class=\"num\"><b>{N(t.ReplyCount)}</b>{E(ctx.T("forum.replies"))}</div><div class=\"num\"><b>{N(t.ViewCount)}</b>{E(ctx.T("forum.views"))}</div>")
                  .Append($"<div class=\"last\">{TimeTag(ctx, t.LastPostAt)}<br>{E(ctx.T("forum.by"))} {MemberLink(t.LastAuthorId, t.LastAuthor)}</div></div>");
            }
            sb.Append("</div>");
        }
        return Page(ctx, ctx.T("forum.latest") + " – " + ForumSettings.Title(ctx), sb.ToString(), noIndex: true);
    }

    // ---- members ------------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> MemberAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        if (!long.TryParse(ctx.Request.R("id"), out var id) || id <= 0 || await ctx.Members.FindAsync(id, ct) is not { IsActive: true } account) return NotFound(ctx);
        var profile = await store.ProfileAsync(id, ct);
        var name = (account.DisplayName.Length > 0 ? account.DisplayName : account.Username).Trim();
        var self = v.Id == id;
        var notice = "";

        if (ctx.Request.IsPost && self && ctx.Request.Action == "signature" && ctx.Settings.GetBool(ForumSettings.Signatures) && !v.Banned)
        {
            var sig = ctx.Request.F("signature").Trim();
            if (sig.Length > MaxSignature) sig = sig[..MaxSignature];
            if (!ctx.Settings.GetBool(ForumSettings.Images)) sig = System.Text.RegularExpressions.Regex.Replace(sig, @"\[/?img\]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            await store.SaveProfileAsync(id, new Dictionary<string, object?> { ["signature"] = sig }, ct);
            return PluginResult.Redirect(ForumUrls.Member(id) + "?ok=saved");
        }
        notice = Ok(ctx);

        var boards = await store.ReadableBoardsAsync(v, ct);
        var posts = await store.LatestPostsAsync(boards.Select(b => b.Id).ToList(), 20, id, ct);
        var sb = new StringBuilder(Toolbar(ctx, v, ForumUrls.Member(id)) + Crumbs(ctx, (ctx.T("forum.members"), null), (name, null)) + notice);
        sb.Append($"<div class=\"forum-profile\"><span class=\"forum-avatar\" aria-hidden=\"true\">{E(Initials(name))}</span><div><h1>{E(name)}</h1>")
          .Append($"<p class=\"forum-muted forum-small\">{E(ctx.T("forum.memberSince", account.CreatedUtc.ToString(ctx.Locale == "de" ? "dd.MM.yyyy" : "yyyy-MM-dd", CultureInfo.InvariantCulture)))} · {E(ctx.T("forum.authorPosts", N(profile?.PostCount ?? 0)))}")
          .Append(profile?.LastPostAt is { } la ? $" · {E(ctx.T("forum.lastActive"))} {TimeTag(ctx, la)}" : "")
          .Append(account.Roles.Contains(ForumSettings.Moderators(ctx), StringComparer.OrdinalIgnoreCase) ? $" · <span class=\"forum-tag\">{E(ctx.T("forum.moderator"))}</span>" : "")
          .Append("</p></div></div>");

        if (self && ctx.Settings.GetBool(ForumSettings.Signatures) && !v.Banned)
            sb.Append($"<form method=\"post\" class=\"forum-editor\">{ctx.Antiforgery}<input type=\"hidden\" name=\"action\" value=\"signature\"><h2>{E(ctx.T("forum.signature"))}</h2>")
              .Append($"<textarea name=\"signature\" maxlength=\"{MaxSignature}\" style=\"min-height:80px\" aria-label=\"{E(ctx.T("forum.signature"))}\">{E(profile?.Signature ?? "")}</textarea>")
              .Append($"<p class=\"forum-hint\">{E(ctx.T("forum.signatureHint", MaxSignature))}</p><div class=\"row\"><button class=\"forum-btn\" type=\"submit\">{E(ctx.T("forum.save"))}</button></div></form>");

        sb.Append($"<section class=\"forum-box\"><h2>{E(ctx.T("forum.recentPostsOf", name))}</h2>");
        if (posts.Count == 0) sb.Append($"<p class=\"forum-muted\" style=\"padding:12px 16px;margin:0\">{E(ctx.T("forum.noPosts"))}</p>");
        else
        {
            sb.Append("<ul>");
            var titles = new Dictionary<long, string>();
            foreach (var p in posts)
            {
                if (!titles.TryGetValue(p.ThreadId, out var title)) titles[p.ThreadId] = title = (await store.ThreadAsync(p.ThreadId, ct))?.Title ?? "";
                sb.Append($"<li><a href=\"{E(ForumUrls.Post(p.Id))}\">{E(title)}</a><div class=\"forum-small forum-muted\">{TimeTag(ctx, p.CreatedAt)}</div><div class=\"forum-small\">{E(ForumMarkup.Plain(p.Body, 180))}</div></li>");
            }
            sb.Append("</ul>");
        }
        sb.Append("</section>");
        return Page(ctx, name + " – " + ForumSettings.Title(ctx), sb.ToString(), noIndex: true);
    }

    public static async Task<PluginResult> SubscriptionsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        var me = v.Member!;
        if (ctx.Request.IsPost && long.TryParse(ctx.Request.F("thread"), out var tid))
        {
            await store.SubscribeAsync(me.Id, tid, false, ct);
            return PluginResult.Redirect(ForumUrls.Subscriptions + "?ok=unsubscribed");
        }
        var boards = (await store.ReadableBoardsAsync(v, ct)).ToDictionary(b => b.Id);
        var sb = new StringBuilder(Toolbar(ctx, v, ForumUrls.Subscriptions) + Crumbs(ctx, (ctx.T("forum.subs"), null)) + Ok(ctx));
        sb.Append($"<div class=\"forum-head\"><div><h1>{E(ctx.T("forum.subs"))}</h1><p class=\"forum-muted\">{E(ctx.T("forum.subsHelp"))}</p></div></div>");
        var threads = new List<ForumThread>();
        foreach (var id in await store.SubscribedThreadsAsync(me.Id, ct))
            if (await store.ThreadAsync(id, ct) is { } t && boards.ContainsKey(t.BoardId)) threads.Add(t);
        if (threads.Count == 0) sb.Append($"<p class=\"forum-muted\">{E(ctx.T("forum.subsNone"))}</p>");
        else
        {
            sb.Append("<div class=\"forum-list\">");
            foreach (var t in threads.OrderByDescending(t => t.LastPostAt))
                sb.Append($"<div class=\"forum-row\"><span class=\"ico\">{Icon}</span><div><a class=\"name\" href=\"{E(ForumUrls.Post(t.LastPostId))}\">{E(t.Title)}</a><p class=\"desc\">{E(boards[t.BoardId].Title)} · {TimeTag(ctx, t.LastPostAt)}</p></div>")
                  .Append($"<div class=\"num\"><b>{N(t.ReplyCount)}</b>{E(ctx.T("forum.replies"))}</div><div class=\"num\"></div>")
                  .Append($"<div class=\"last\"><form method=\"post\">{ctx.Antiforgery}<input type=\"hidden\" name=\"thread\" value=\"{t.Id}\"><button class=\"forum-btn ghost sm\" type=\"submit\">{E(ctx.T("forum.unsubscribe"))}</button></form></div></div>");
            sb.Append("</div>");
        }
        return Page(ctx, ctx.T("forum.subs"), sb.ToString(), noIndex: true);
    }

    /// <summary>
    /// Joining the forum: an account of the SITE (the same one /anmelden signs in), created by the forum because the operator switched its own registration on —
    /// the site's general self-registration setting does not bind a plugin (IPluginMembers). A field bots fill and people do not see stops the simplest of them.
    /// </summary>
    public static async Task<PluginResult> RegisterAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!ForumSettings.RegistrationOpen(ctx)) return NotFound(ctx);
        var store = new ForumStore(ctx.Data);
        var v = await ViewerAsync(ctx, store, ct);
        var back = ctx.Request.Q("returnUrl") is { Length: > 1 } r && r.StartsWith('/') && !r.StartsWith("//") && !r.StartsWith("/\\") ? r : ForumUrls.Home;
        if (v.SignedIn) return PluginResult.Redirect(back);

        string? error = null;
        var name = ctx.Request.F("name").Trim();
        var email = ctx.Request.F("email").Trim();
        if (ctx.Request.IsPost)
        {
            var pw = ctx.Request.F("password");
            if (ctx.Request.F("website").Length > 0) error = ctx.T("forum.err.spam");
            else if (name.Length is < 2 or > 40) error = ctx.T("forum.err.name");
            else if (pw != ctx.Request.F("password2")) error = ctx.T("forum.err.passwordMatch");
            else if (ctx.Request.F("terms") != "1") error = ctx.T("forum.err.terms");
            else
            {
                var res = await ctx.Members.CreateAsync(email, pw, name, ct);
                switch (res.Status)
                {
                    case MemberCreateStatus.Created when res.Account is { } acc:
                        await ctx.Members.SignInAsync(acc, persistent: true, ct);
                        return PluginResult.Redirect(back + (back.Contains('?') ? "&" : "?") + "ok=welcome");
                    case MemberCreateStatus.EmailTaken: error = ctx.T("forum.err.emailTaken"); break;
                    case MemberCreateStatus.InvalidEmail: error = ctx.T("forum.err.email"); break;
                    case MemberCreateStatus.WeakPassword: error = ctx.T("forum.err.password"); break;
                    default: error = ctx.T("forum.err.register"); break;
                }
            }
        }
        var html = Toolbar(ctx, v, ForumUrls.Register) + Crumbs(ctx, (ctx.T("forum.register"), null))
                   + $"<form method=\"post\" class=\"forum-editor\" style=\"max-width:520px\">{ctx.Antiforgery}<h2>{E(ctx.T("forum.registerTitle"))}</h2>"
                   + (error is null ? "" : Notice(error, "err"))
                   + $"<label for=\"rn\">{E(ctx.T("forum.field.name"))}</label><input type=\"text\" id=\"rn\" name=\"name\" maxlength=\"40\" required autocomplete=\"nickname\" value=\"{E(name)}\">"
                   + $"<p class=\"forum-hint\">{E(ctx.T("forum.field.nameHint"))}</p>"
                   + $"<label for=\"re\">{E(ctx.T("forum.field.email"))}</label><input type=\"email\" id=\"re\" name=\"email\" required autocomplete=\"email\" value=\"{E(email)}\">"
                   + $"<label for=\"rp\">{E(ctx.T("forum.field.password"))}</label><input type=\"password\" id=\"rp\" name=\"password\" required minlength=\"8\" autocomplete=\"new-password\">"
                   + $"<label for=\"rp2\">{E(ctx.T("forum.field.password2"))}</label><input type=\"password\" id=\"rp2\" name=\"password2\" required minlength=\"8\" autocomplete=\"new-password\">"
                   + "<input type=\"text\" name=\"website\" value=\"\" tabindex=\"-1\" autocomplete=\"off\" aria-hidden=\"true\" style=\"position:absolute;left:-9999px\">"
                   + $"<div class=\"row\"><label class=\"forum-check\"><input type=\"checkbox\" name=\"terms\" value=\"1\" required> {E(ctx.T("forum.field.terms"))}</label></div>"
                   + $"<div class=\"row\"><button class=\"forum-btn\" type=\"submit\">{E(ctx.T("forum.registerSend"))}</button><a href=\"{E(ForumUrls.SignIn(back))}\">{E(ctx.T("forum.haveAccount"))}</a></div></form>";
        return Page(ctx, ctx.T("forum.register"), html, noIndex: true, status: error is null ? 200 : 422);
    }
}
