using System.Globalization;

namespace MatCMS.Plugin.Forum;

/// <summary>
/// The forum's back office: an overview, the boards, all threads, the reports and the members — built from the host's components only, so it looks and
/// behaves like the rest of the admin (dark mode, phone, dialogs) without a line of CSS of its own.
/// </summary>
internal static class ForumAdmin
{
    private const string Base = "/admin/p/forum/";
    private static string S(long n) => n.ToString("#,0", CultureInfo.GetCultureInfo("de-DE"));

    private static string AccessLabel(IPluginContext ctx, string access, string role) => access switch
    {
        Access.All => ctx.T("forum.access.all"),
        Access.Members => ctx.T("forum.access.members"),
        Access.Role => ctx.T("forum.access.role") + (role.Length > 0 ? " „" + role + "“" : ""),
        _ => ctx.T("forum.access.mods")
    };

    private static UiOption[] AccessOptions(IPluginContext ctx, bool forWrite) => forWrite
        ? [Ui.Option(Access.Members, ctx.T("forum.access.members")), Ui.Option(Access.Role, ctx.T("forum.access.role")), Ui.Option(Access.Mods, ctx.T("forum.access.mods"))]
        : [Ui.Option(Access.All, ctx.T("forum.access.all")), Ui.Option(Access.Members, ctx.T("forum.access.members")), Ui.Option(Access.Role, ctx.T("forum.access.role")), Ui.Option(Access.Mods, ctx.T("forum.access.mods"))];

    private static string When(IPluginContext ctx, DateTime? utc) => ForumHtml.When(ctx, utc);

    // ---- overview -----------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> DashboardAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        var (threads, posts, members) = await store.TotalsAsync(ct);
        var open = await store.OpenReportCountAsync(ct);
        var boards = await store.BoardsAsync(ct);
        var latest = await store.LatestPostsAsync(null, 12, ct: ct);
        var titles = new Dictionary<long, string>();
        var rows = new List<UiTableRow>();
        foreach (var p in latest)
        {
            if (!titles.TryGetValue(p.ThreadId, out var title)) titles[p.ThreadId] = title = (await store.ThreadAsync(p.ThreadId, ct))?.Title ?? "";
            rows.Add(Ui.Row(Ui.Stack(Ui.Link(title, ForumUrls.Post(p.Id), newTab: true), Ui.Muted(ForumMarkup.Plain(p.Body, 110))),
                p.AuthorName, boards.FirstOrDefault(b => b.Id == p.BoardId)?.Title ?? "", When(ctx, p.CreatedAt)));
        }

        UiNode start = boards.Count == 0
            ? Ui.Alert(ctx.T("forum.admin.startHint"), UiTone.Info)
            : Ui.Stats(
                Ui.Stat(ctx.T("forum.stat.threads"), S(threads), href: Base + "threads"),
                Ui.Stat(ctx.T("forum.stat.posts"), S(posts)),
                Ui.Stat(ctx.T("forum.stat.members"), S(members), href: Base + "members"),
                Ui.Stat(ctx.T("forum.admin.openReports"), S(open), tone: open > 0 ? UiTone.Warning : UiTone.Default, href: Base + "reports"));

        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(ctx.T("forum.admin.dashboardSub"), null,
                Ui.Button(ctx.T("forum.admin.viewForum"), ForumUrls.Home, UiButtonKind.Ghost, "ti-external-link") with { NewTab = true },
                boards.Count == 0 ? Ui.Button(ctx.T("forum.admin.newBoard"), Base + "boards/new", icon: "ti-plus") : null),
            start,
            Ui.Card(ctx.T("forum.latestPosts"),
                Ui.Table(new[] { Ui.Column(ctx.T("forum.admin.col.post")), Ui.Column(ctx.T("forum.admin.col.author")), Ui.Column(ctx.T("forum.admin.col.board")), Ui.Column(ctx.T("forum.admin.col.when")) },
                    rows, emptyText: ctx.T("forum.noPosts")))), ForumSettings.Title(ctx));
    }

    // ---- boards ---------------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> BoardsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        if (ctx.Request.IsPost && ctx.Request.Action is "up" or "down" && long.TryParse(ctx.Request.F("id"), out var mid))
        {
            // Moving swaps a board with its neighbour in the visible order and renumbers all, so equal sort numbers cannot keep it stuck.
            var list = (await store.BoardsAsync(ct)).ToList();
            var i = list.FindIndex(b => b.Id == mid);
            var j = ctx.Request.Action == "up" ? i - 1 : i + 1;
            if (i >= 0 && j >= 0 && j < list.Count) (list[i], list[j]) = (list[j], list[i]);
            for (var k = 0; k < list.Count; k++) await store.Boards.UpdateAsync(list[k].Id, new Dictionary<string, object?> { ["sort"] = (long)(k + 1) * 10 }, ct);
            return PluginResult.Redirect(Base + "boards");
        }

        var boards = await store.BoardsAsync(ct);
        var rows = boards.Select((b, i) => Ui.RowFor(b.Title + " " + b.Category + " " + b.Slug,
            Ui.Stack(Ui.Link(b.Title, Base + "boards/" + b.Id), Ui.Muted("/forum/" + b.Slug)),
            b.Category,
            AccessLabel(ctx, b.ReadAccess, b.Role),
            b.Locked ? (object)Ui.Badge(ctx.T("forum.locked"), UiTone.Warning) : AccessLabel(ctx, b.WriteAccess, b.Role),
            S(b.ThreadCount), S(b.PostCount),
            Ui.Inline(
                i > 0 ? Ui.Action(ctx.T("forum.admin.up"), "up", b.Id.ToString(CultureInfo.InvariantCulture), icon: "ti-arrow-up") : null,
                i < boards.Count - 1 ? Ui.Action(ctx.T("forum.admin.down"), "down", b.Id.ToString(CultureInfo.InvariantCulture), icon: "ti-arrow-down") : null,
                Ui.Button(ctx.T("forum.edit"), Base + "boards/" + b.Id, UiButtonKind.Ghost, "ti-pencil", small: true) with { IconOnly = true },
                Ui.Button(ctx.T("forum.admin.open"), ForumUrls.Board(b), UiButtonKind.Ghost, "ti-external-link", small: true) with { IconOnly = true, NewTab = true })));

        return PluginResult.Page(Ui.Table(
            new[] { Ui.Column(ctx.T("forum.admin.col.board")), Ui.Column(ctx.T("forum.admin.col.category")), Ui.Column(ctx.T("forum.admin.col.read")), Ui.Column(ctx.T("forum.admin.col.write")),
                Ui.Column(ctx.T("forum.threads"), right: true), Ui.Column(ctx.T("forum.posts"), right: true), Ui.Column("", actions: true) },
            rows, ctx.T("forum.admin.searchBoards"), emptyText: ctx.T("forum.admin.noBoards")) with
        {
            Below = [Ui.Button(ctx.T("forum.admin.newBoard"), Base + "boards/new", icon: "ti-plus")]
        }, ctx.T("forum.admin.boards"));
    }

    public static async Task<PluginResult> BoardAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        var raw = ctx.Request.R("id");
        var isNew = raw == "new";
        Board? board = null;
        if (!isNew && (!long.TryParse(raw, out var id) || (board = await store.BoardAsync(id, ct)) is null)) return PluginResult.NotFound();

        string? error = null;
        if (ctx.Request.IsPost)
        {
            if (ctx.Request.Action == "delete" && board is not null)
            {
                await store.DeleteBoardAsync(board.Id, ct);
                return PluginResult.Redirect(Base + "boards", ctx.T("forum.admin.boardDeleted", board.Title));
            }
            var title = ctx.Request.F("title").Trim();
            var read = ctx.Request.F("read");
            var write = ctx.Request.F("write");
            var role = ctx.Request.F("role").Trim();
            if (title.Length is < 2 or > 80) error = ctx.T("forum.admin.err.title");
            else if ((read == Access.Role || write == Access.Role) && role.Length == 0) error = ctx.T("forum.admin.err.role");
            else
            {
                var sort = long.TryParse(ctx.Request.F("sort"), out var s) ? s : (board?.Sort ?? ((await store.BoardsAsync(ct)).Count + 1) * 10);
                var saved = await store.SaveBoardAsync(board?.Id ?? 0, title, ctx.Request.F("slug").Trim(), ctx.Request.F("description").Trim(),
                    ctx.Request.F("category").Trim(), sort, read, write, role, ctx.Request.F("locked") is "on" or "true" or "1", ct);
                return PluginResult.Redirect(Base + "boards/" + saved, ctx.T("forum.ok.saved"));
            }
        }

        var categories = (await store.BoardsAsync(ct)).Select(b => b.Category).Where(c => c.Length > 0).Distinct().ToList();
        var f = ctx.Request.IsPost;
        string V(string field, string current) => f ? ctx.Request.F(field) : current;
        var form = Ui.Form(
            error is null ? null : Ui.Alert(error, UiTone.Error),
            Ui.Input("title", ctx.T("forum.admin.field.title"), V("title", board?.Title ?? "")) with { Required = true, MaxLength = 80 },
            Ui.Input("slug", ctx.T("forum.admin.field.slug"), V("slug", board?.Slug ?? "")) with { Help = ctx.T("forum.admin.field.slugHelp"), Placeholder = ctx.T("forum.admin.field.slugPlaceholder") },
            Ui.TextArea("description", ctx.T("forum.admin.field.description"), V("description", board?.Description ?? ""), 3),
            Ui.Input("category", ctx.T("forum.admin.field.category"), V("category", board?.Category ?? "")) with
            {
                Help = categories.Count > 0 ? ctx.T("forum.admin.field.categoryHelpList", string.Join(", ", categories)) : ctx.T("forum.admin.field.categoryHelp")
            },
            Ui.Select("read", ctx.T("forum.admin.field.read"), V("read", board?.ReadAccess ?? Access.All), AccessOptions(ctx, false)),
            Ui.Select("write", ctx.T("forum.admin.field.write"), V("write", board?.WriteAccess ?? Access.Members), AccessOptions(ctx, true)),
            Ui.Input("role", ctx.T("forum.admin.field.role"), V("role", board?.Role ?? "")) with { Help = ctx.T("forum.admin.field.roleHelp") },
            Ui.Input("sort", ctx.T("forum.admin.field.sort"), V("sort", board is null ? "" : board.Sort.ToString(CultureInfo.InvariantCulture)), UiInputType.Number),
            Ui.Checkbox("locked", ctx.T("forum.admin.field.locked"), f ? ctx.Request.F("locked") is "on" or "true" or "1" : board?.Locked == true) with { Help = ctx.T("forum.admin.field.lockedHelp") },
            Ui.FormActions(Ui.Submit(ctx.T("forum.save")), Ui.Button(ctx.T("forum.cancel"), Base + "boards", UiButtonKind.Ghost)));

        var side = board is null ? Array.Empty<UiNode>() : new UiNode[]
        {
            Ui.Card(ctx.T("forum.admin.figures"), Ui.KeyValue((ctx.T("forum.threads"), S(board.ThreadCount)), (ctx.T("forum.posts"), S(board.PostCount)),
                (ctx.T("forum.admin.lastPost"), board.LastPostAt is null ? "–" : When(ctx, board.LastPostAt))),
                Ui.Button(ctx.T("forum.admin.open"), ForumUrls.Board(board), UiButtonKind.Ghost, "ti-external-link") with { NewTab = true }),
            Ui.Card(ctx.T("forum.admin.danger"), Ui.Muted(ctx.T("forum.admin.deleteBoardHelp")),
                Ui.Action(ctx.T("forum.admin.deleteBoard"), "delete", board.Id.ToString(CultureInfo.InvariantCulture), UiButtonKind.Danger,
                    confirm: ctx.T("forum.admin.confirmDeleteBoard", board.Title, S(board.ThreadCount), S(board.PostCount))) with { WithLabel = true })
        };
        return PluginResult.Page(Ui.Columns(new UiNode?[] { Ui.Card(form) }, side), board?.Title ?? ctx.T("forum.admin.newBoard"));
    }

    // ---- threads --------------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> ThreadsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        if (ctx.Request.IsPost && long.TryParse(ctx.Request.F("id"), out var tid) && await store.ThreadAsync(tid, ct) is { } t)
        {
            switch (ctx.Request.Action)
            {
                case "sticky": await store.SetThreadFlagAsync(t.Id, "sticky", !t.Sticky, ct); break;
                case "close": await store.SetThreadFlagAsync(t.Id, "closed", !t.Closed, ct); break;
                case "delete":
                    await store.DeleteThreadAsync(t.Id, ct);
                    return PluginResult.Redirect(Base + "threads", ctx.T("forum.admin.threadDeleted", t.Title));
            }
            return PluginResult.Redirect(Base + "threads", ctx.T("forum.ok.saved"));
        }

        var boards = await store.BoardsAsync(ct);
        var all = new List<ForumThread>();
        foreach (var b in boards) all.AddRange((await store.BoardThreadsAsync(b.Id, 1, 2000, ct)).Items);
        var rows = all.OrderByDescending(x => x.LastPostAt).Select(x =>
        {
            var board = boards.First(b => b.Id == x.BoardId);
            var id = x.Id.ToString(CultureInfo.InvariantCulture);
            var status = new List<UiNode>();
            if (x.Sticky) status.Add(Ui.Badge(ctx.T("forum.sticky"), UiTone.Info));
            if (x.Closed) status.Add(Ui.Badge(ctx.T("forum.closed"), UiTone.Warning));
            return Ui.RowFor(x.Title + " " + x.AuthorName + " " + board.Title,
                Ui.Link(x.Title, ForumUrls.Thread(x), newTab: true), board.Title, x.AuthorName, S(x.ReplyCount), When(ctx, x.LastPostAt),
                Ui.Inline(status.ToArray()),
                Ui.Inline(
                    Ui.Action(ctx.T(x.Sticky ? "forum.mod.unsticky" : "forum.mod.sticky"), "sticky", id, icon: x.Sticky ? "ti-pinned-off" : "ti-pin"),
                    Ui.Action(ctx.T(x.Closed ? "forum.mod.open" : "forum.mod.close"), "close", id, icon: x.Closed ? "ti-lock-open" : "ti-lock"),
                    Ui.Action(ctx.T("forum.mod.delete"), "delete", id, UiButtonKind.Danger, ctx.T("forum.admin.confirmDeleteThread", x.Title, S(x.ReplyCount + 1)), "ti-trash"))) with
            {
                Tags = "b" + board.Id.ToString(CultureInfo.InvariantCulture)
            };
        }).ToList();

        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("forum.admin.col.thread")), Ui.Column(ctx.T("forum.admin.col.board")), Ui.Column(ctx.T("forum.admin.col.author")),
                Ui.Column(ctx.T("forum.replies"), right: true), Ui.Column(ctx.T("forum.admin.lastPost")), Ui.Column(ctx.T("forum.admin.col.status")), Ui.Column("", actions: true) },
            rows, ctx.T("forum.admin.searchThreads"), pageSize: 50, emptyText: ctx.T("forum.noThreads")) with
        {
            Filter = boards.Count > 1 ? Ui.ListFilter(ctx.T("forum.admin.allBoards"), boards.Select(b => ("b" + b.Id.ToString(CultureInfo.InvariantCulture), b.Title)).ToArray()) : null
        };
        return PluginResult.Page(table, ctx.T("forum.admin.threads"));
    }

    // ---- reports ---------------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> ReportsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        if (ctx.Request.IsPost && long.TryParse(ctx.Request.F("id"), out var rid) && await store.Reports.GetAsync(rid, ct) is { } report)
        {
            switch (ctx.Request.Action)
            {
                case "done":
                    await store.Reports.UpdateAsync(rid, new Dictionary<string, object?> { ["done"] = true }, ct);
                    return PluginResult.Redirect(Base + "reports", ctx.T("forum.admin.reportDone"));
                case "reopen":
                    await store.Reports.UpdateAsync(rid, new Dictionary<string, object?> { ["done"] = false }, ct);
                    return PluginResult.Redirect(Base + "reports?show=done", ctx.T("forum.ok.saved"));
                case "deletepost" when await store.PostAsync(report.Int("post_id"), ct) is { } post:
                    await store.DeletePostAsync(post, ct);
                    return PluginResult.Redirect(Base + "reports", ctx.T("forum.admin.postDeleted"));
            }
            return PluginResult.Redirect(Base + "reports");
        }

        var showDone = ctx.Request.Q("show") == "done";
        var reports = await store.ReportsAsync(!showDone, ct);
        var rows = new List<UiTableRow>();
        foreach (var r in reports)
        {
            var post = await store.PostAsync(r.PostId, ct);
            var thread = post is null ? null : await store.ThreadAsync(post.ThreadId, ct);
            var id = r.Id.ToString(CultureInfo.InvariantCulture);
            UiNode what = post is null
                ? Ui.Muted(ctx.T("forum.admin.postGone"))
                : Ui.Stack(Ui.Link(thread?.Title ?? "", ForumUrls.Post(post.Id), newTab: true), Ui.Muted(post.AuthorName + ": " + ForumMarkup.Plain(post.Body, 160)));
            rows.Add(Ui.Row(what, r.Reason, r.MemberName, When(ctx, r.CreatedAt),
                Ui.Inline(
                    showDone ? Ui.Action(ctx.T("forum.admin.reopen"), "reopen", id, icon: "ti-arrow-back-up")
                             : Ui.Action(ctx.T("forum.admin.markDone"), "done", id, icon: "ti-check"),
                    post is null ? null : Ui.Action(ctx.T("forum.deletePost"), "deletepost", id, UiButtonKind.Danger, ctx.T("forum.confirmDeletePost"), "ti-trash"))));
        }
        return PluginResult.Page(Ui.Stack(
            Ui.Tabs(Ui.Tab(ctx.T("forum.admin.reportsOpen"), Base + "reports", !showDone), Ui.Tab(ctx.T("forum.admin.reportsDone"), Base + "reports?show=done", showDone)),
            Ui.Table(new[] { Ui.Column(ctx.T("forum.admin.col.post")), Ui.Column(ctx.T("forum.admin.col.reason")), Ui.Column(ctx.T("forum.admin.col.reporter")),
                Ui.Column(ctx.T("forum.admin.col.when")), Ui.Column("", actions: true) }, rows, emptyText: ctx.T(showDone ? "forum.admin.noReportsDone" : "forum.admin.noReports"))),
            ctx.T("forum.admin.reports"));
    }

    // ---- members -------------------------------------------------------------------------------------------------------

    public static async Task<PluginResult> MembersAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        if (ctx.Request.IsPost && long.TryParse(ctx.Request.F("id"), out var mid) && mid > 0)
        {
            switch (ctx.Request.Action)
            {
                case "ban": await store.SaveProfileAsync(mid, new Dictionary<string, object?> { ["banned"] = true }, ct); break;
                case "unban": await store.SaveProfileAsync(mid, new Dictionary<string, object?> { ["banned"] = false, ["ban_reason"] = "" }, ct); break;
                case "clearsig": await store.SaveProfileAsync(mid, new Dictionary<string, object?> { ["signature"] = "" }, ct); break;
            }
            return PluginResult.Redirect(Base + "members", ctx.T("forum.ok.saved"));
        }

        var modRole = ForumSettings.Moderators(ctx);
        var rows = new List<UiTableRow>();
        foreach (var p in await store.ProfilesAsync(ct))
        {
            var account = await ctx.Members.FindAsync(p.MemberId, ct);
            if (account is null) continue;
            var name = account.DisplayName.Length > 0 ? account.DisplayName : account.Username;
            var id = p.MemberId.ToString(CultureInfo.InvariantCulture);
            var status = new List<UiNode>();
            if (account.Roles.Contains(modRole, StringComparer.OrdinalIgnoreCase)) status.Add(Ui.Badge(ctx.T("forum.moderator"), UiTone.Info));
            if (p.Banned) status.Add(Ui.Badge(ctx.T("forum.admin.banned"), UiTone.Error));
            if (!account.IsActive) status.Add(Ui.Badge(ctx.T("forum.admin.inactive"), UiTone.Warning));
            rows.Add(Ui.RowFor(name + " " + account.Email,
                Ui.Stack(Ui.Link(name, ForumUrls.Member(p.MemberId), newTab: true), Ui.Muted(account.Email)),
                S(p.PostCount), p.LastPostAt is null ? "–" : When(ctx, p.LastPostAt), Ui.Inline(status.ToArray()),
                Ui.Inline(
                    p.Banned ? Ui.Action(ctx.T("forum.admin.unban"), "unban", id, icon: "ti-lock-open")
                             : Ui.Action(ctx.T("forum.admin.ban"), "ban", id, UiButtonKind.Danger, ctx.T("forum.admin.confirmBan", name), "ti-ban"),
                    p.Signature.Length > 0 ? Ui.Action(ctx.T("forum.admin.clearSignature"), "clearsig", id, icon: "ti-eraser") : null)));
        }
        return PluginResult.Page(Ui.Stack(
            Ui.Alert(ctx.T("forum.admin.modHint", modRole), UiTone.Info),
            Ui.Table(new[] { Ui.Column(ctx.T("forum.admin.col.member")), Ui.Column(ctx.T("forum.posts"), right: true), Ui.Column(ctx.T("forum.admin.lastPost")),
                Ui.Column(ctx.T("forum.admin.col.status")), Ui.Column("", actions: true) }, rows, ctx.T("forum.admin.searchMembers"), pageSize: 50,
                emptyText: ctx.T("forum.admin.noMembers"))), ctx.T("forum.admin.members"));
    }

    // ---- dashboard contributions ------------------------------------------------------------------------------------

    public static async Task<UiStat?> TileAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        if (await store.Boards.CountAsync(null, ct) == 0) return null;
        var (threads, posts, _) = await store.TotalsAsync(ct);
        var open = await store.OpenReportCountAsync(ct);
        return Ui.Stat(ctx.T("forum.stat.posts"), S(posts),
            open > 0 ? ctx.T("forum.admin.tileReports", S(open)) : ctx.T("forum.admin.tileThreads", S(threads)),
            open > 0 ? UiTone.Warning : UiTone.Default, Base + (open > 0 ? "reports" : "dashboard"));
    }

    public static async Task<IEnumerable<AttentionNote>> AttentionAsync(IPluginContext ctx, CancellationToken ct)
    {
        var open = await new ForumStore(ctx.Data).OpenReportCountAsync(ct);
        return open == 0
            ? Array.Empty<AttentionNote>()
            : new[] { new AttentionNote(ctx.T("forum.admin.attentionTitle"), ctx.T("forum.admin.attentionText", S(open)), Base + "reports") { Tone = UiTone.Warning, Icon = "ti-flag" } };
    }

    public static async Task<DashboardCard?> CardAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        if (await store.Boards.CountAsync(null, ct) == 0) return null;
        var rows = new List<DashboardRow>();
        foreach (var t in await store.LatestThreadsAsync((await store.BoardsAsync(ct)).Select(b => b.Id).ToList(), 6, ct))
            rows.Add(new DashboardRow(t.Title + " · " + t.LastAuthor, ForumUrls.Post(t.LastPostId)) { Icon = "ti-messages", Meta = When(ctx, t.LastPostAt) });
        return new DashboardCard(ctx.T("forum.admin.cardTitle"), rows) { AllUrl = Base + "threads", AllLabel = ctx.T("forum.admin.allThreads"), EmptyText = ctx.T("forum.noPosts") };
    }

    public static async Task<string?> BadgeAsync(IPluginContext ctx, CancellationToken ct)
    {
        var open = await new ForumStore(ctx.Data).OpenReportCountAsync(ct);
        return open > 0 ? open.ToString(CultureInfo.InvariantCulture) : null;
    }
}
