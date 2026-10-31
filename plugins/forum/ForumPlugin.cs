using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Forum;

/// <summary>
/// A discussion forum for a MatCMS site — the classic board: categories with boards, threads with posts, pinned and closed threads, quotes, BBCode,
/// subscriptions by mail, search, profiles, reports and moderation. Members are the SITE's accounts (the guest area behind <c>/anmelden</c>); the forum
/// keeps only what is its own about them.
/// <para>A native plugin: it runs where the operator switched "Native Plugins erlauben" on. Written against <c>MatCMS.Plugin.Abstractions</c> only —
/// no host type, no reflection-based JSON over its own types, nothing that would keep it from being unloaded.</para>
/// </summary>
public sealed class ForumPlugin : IPlugin
{
    /// <inheritdoc />
    public void Configure(IPluginBuilder b)
    {
        ForumStrings.Register(b);
        ForumStore.Declare(b.Data);
        ForumSettings.Declare(b.Settings);
        foreach (var spec in ForumMail.Specs) b.MailTemplate(spec);
        ForumHtml.RegisterParts(b);

        // ---- public pages ------------------------------------------------------------------------------------------
        // The forum does not publish per language: one address per page (a prefixed /en/forum would show the same threads again).
        // Pages with a form carry the antiforgery token (and are therefore never cached); a page only to read needs none. Several members may share one
        // address (an office, a school, a mobile network), so the POST limit per address is generous; the flood setting slows down each member.
        var read = new PublicRouteOptions { Methods = PluginMethod.Get, DefaultLanguageOnly = true };
        var form = new PublicRouteOptions { Antiforgery = true, DefaultLanguageOnly = true, PostsPerMinute = 120 };
        b.Route("/forum", ForumPublic.IndexAsync, form);
        b.Route("/forum/suche", ForumPublic.SearchAsync, read);
        b.Route("/forum/aktuell", ForumPublic.LatestAsync, read);
        b.Route("/forum/registrieren", ForumPublic.RegisterAsync, form);
        b.Route("/forum/abos", ForumPublic.SubscriptionsAsync, form with { RequireMember = true });
        b.Route("/forum/mitglied/{id}", ForumPublic.MemberAsync, form);
        b.Route("/forum/t/{id}", ForumPublic.ThreadAsync, form);
        b.Route("/forum/p/{id}", ForumPublic.PostAsync, read);
        b.Route("/forum/p/{id}/bearbeiten", ForumPublic.EditPostAsync, form);
        b.Route("/forum/p/{id}/melden", ForumPublic.ReportAsync, form);
        b.Route("/forum/{board}", ForumPublic.BoardAsync, read);
        b.Route("/forum/{board}/neu", ForumPublic.NewThreadAsync, form);

        // ---- back office ----------------------------------------------------------------------------------------------
        b.Admin.Section("forum.nav", "ti-messages", AdminGroup.Content, 30)
            .Page("dashboard", null, ForumAdmin.DashboardAsync)
            .Page("boards", "forum.admin.boards", ForumAdmin.BoardsAsync)
            .Page("boards/{id}", null, ForumAdmin.BoardAsync)
            .Page("threads", "forum.admin.threads", ForumAdmin.ThreadsAsync)
            .Page("reports", "forum.admin.reports", ForumAdmin.ReportsAsync)
            .Page("members", "forum.admin.members", ForumAdmin.MembersAsync)
            .Badge(ForumAdmin.BadgeAsync)
            .Link("forum.admin.overview", "dashboard", "ti-layout-dashboard")
            .Link("forum.admin.boards", "boards", "ti-folders")
            .Link("forum.admin.threads", "threads", "ti-messages")
            .Link("forum.admin.reports", "reports", "ti-flag", badge: true)
            .Link("forum.admin.members", "members", "ti-users")
            .Link("forum.admin.settings", "_settings", "ti-settings");
        b.Admin.Tile(ForumAdmin.TileAsync);
        b.Admin.Attention(ForumAdmin.AttentionAsync);
        b.Admin.DashboardCard(ForumAdmin.CardAsync);

        // ---- the rest of the site ------------------------------------------------------------------------------------
        b.Sitemap(SitemapAsync);
        b.LinkTargets("forum.links.group", LinksAsync);
        b.On(HostEvents.MemberErase, async (ctx, payload) =>
        {
            var id = IdOf(payload);
            if (id > 0) await new ForumStore(ctx.Data).EraseMemberAsync(id, ctx.T("forum.deletedMember"));
        });
        b.On(HostEvents.MemberExport, ExportAsync);
        b.On("site.search", SiteSearchAsync);

        // A fresh forum is not an empty page: one category with one board to start in. Only on the first load ever — a board deleted later stays deleted.
        b.Lifecycle.OnInstall(async ctx =>
        {
            var store = new ForumStore(ctx.Data);
            if (await store.Boards.CountAsync(null) > 0) return;
            await store.SaveBoardAsync(0, ctx.T("forum.seed.board"), "allgemeines", ctx.T("forum.seed.boardDesc"), ctx.T("forum.seed.category"), 10,
                Access.All, Access.Members, "", false);
        });
    }

    private static long IdOf(JsonNode? payload) => payload?["id"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : 0;

    /// <summary>The boards everybody may read, and their threads (a board for members only is not for search engines).</summary>
    private static async Task<IEnumerable<SitemapEntry>> SitemapAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!ctx.Settings.GetBool(ForumSettings.Indexing)) return Array.Empty<SitemapEntry>();
        var store = new ForumStore(ctx.Data);
        var boards = (await store.BoardsAsync(ct)).Where(b => b.ReadAccess == Access.All).ToList();
        var list = new List<SitemapEntry> { new(ForumUrls.Home) };
        foreach (var board in boards)
        {
            list.Add(new SitemapEntry(ForumUrls.Board(board), board.LastPostAt));
            foreach (var t in (await store.BoardThreadsAsync(board.Id, 1, 1000, ct)).Items) list.Add(new SitemapEntry(ForumUrls.Thread(t), t.LastPostAt));
        }
        return list;
    }

    private static async Task<IEnumerable<LinkTarget>> LinksAsync(IPluginContext ctx, CancellationToken ct)
    {
        var list = new List<LinkTarget> { new(ForumSettings.Title(ctx), ForumUrls.Home) };
        list.AddRange((await new ForumStore(ctx.Data).BoardsAsync(ct)).Select(b => new LinkTarget(ctx.T("forum.links.board", b.Title), ForumUrls.Board(b))));
        return list;
    }

    /// <summary>The data-protection export: everything the forum holds about the account — its posts with their threads, its profile, its subscriptions.</summary>
    private static async Task<JsonNode?> ExportAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        var id = IdOf(payload);
        if (id <= 0) return null;
        var store = new ForumStore(ctx.Data);
        var posts = new JsonArray();
        var titles = new Dictionary<long, string>();
        foreach (var p in await store.PostsOfAsync(id, ct))
        {
            if (!titles.TryGetValue(p.ThreadId, out var title)) titles[p.ThreadId] = title = (await store.ThreadAsync(p.ThreadId, ct))?.Title ?? "";
            posts.Add(new JsonObject { ["thread"] = title, ["created"] = ForumHtml.Iso(p.CreatedAt), ["text"] = p.Body, ["url"] = ForumUrls.Post(p.Id) });
        }
        var profile = await store.ProfileAsync(id, ct);
        var subs = new JsonArray();
        foreach (var tid in await store.SubscribedThreadsAsync(id, ct)) subs.Add((await store.ThreadAsync(tid, ct))?.Title ?? tid.ToString());
        return new JsonObject
        {
            ["plugin"] = "forum",
            ["data"] = new JsonObject
            {
                ["posts"] = posts, ["signature"] = profile?.Signature ?? "", ["postCount"] = profile?.PostCount ?? 0, ["banned"] = profile?.Banned ?? false, ["subscriptions"] = subs
            }
        };
    }

    /// <summary>
    /// The site's search (and the visitors' chat bot) asks every plugin: <c>{q,max}</c> → <c>{items:[{title,url,text}]}</c>. Only boards EVERYBODY may read
    /// answer here — the question carries no visitor, so nothing for members only may come out.
    /// </summary>
    private static async Task<JsonNode?> SiteSearchAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        var q = payload?["q"]?.GetValue<string>() ?? "";
        var max = payload?["max"] is JsonValue m && m.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, 20) : 5;
        if (q.Trim().Length < 2) return null;
        var store = new ForumStore(ctx.Data);
        var open = (await store.BoardsAsync(ct)).Where(b => b.ReadAccess == Access.All).Select(b => b.Id).ToList();
        var hits = await store.SearchAsync(q, open, max, ct);
        if (hits.Count == 0) return null;
        var items = new JsonArray();
        foreach (var (t, p) in hits)
            items.Add(new JsonObject
            {
                ["title"] = t.Title, ["url"] = p is null ? ForumUrls.Thread(t) : ForumUrls.Post(p.Id), ["kind"] = ctx.T("forum.search.kind"),
                ["text"] = p is null ? "" : ForumMarkup.Plain(p.Body, 300), ["score"] = 1.0
            });
        return new JsonObject { ["items"] = items };
    }
}
