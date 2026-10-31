using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Forum;

// The forum's data, in the plugin's own tables (p_forum_*). Accounts are the site's (IPluginMembers); the forum only keeps, per member, what is
// its own: the post count, a signature, a ban, what was read, what is subscribed.
//
// Counters (threads and posts per board, replies per thread, posts per member, the "last post" of a board and a thread) are written in the SAME
// transaction as the post that changes them, so a list never disagrees with the thread it shows. Deleting and moving recount from the rows
// themselves (RecountAsync) instead of subtracting — a recount cannot drift.

internal sealed record Board(long Id, string Title, string Slug, string Description, string Category, long Sort,
    string ReadAccess, string WriteAccess, string Role, bool Locked,
    long ThreadCount, long PostCount, long LastPostId, DateTime? LastPostAt, long LastThreadId, string LastThreadTitle, string LastAuthor, long LastAuthorId);

internal sealed record ForumThread(long Id, long BoardId, string Title, long AuthorId, string AuthorName, DateTime CreatedAt,
    DateTime LastPostAt, long LastPostId, string LastAuthor, long LastAuthorId, long ReplyCount, long ViewCount, bool Sticky, bool Closed, long FirstPostId);

internal sealed record ForumPost(long Id, long ThreadId, long BoardId, long AuthorId, string AuthorName, string Body, DateTime CreatedAt, DateTime? EditedAt, string EditedBy);

internal sealed record ForumProfile(long MemberId, long PostCount, string Signature, DateTime? LastPostAt, bool Banned, string BanReason);

internal sealed record ForumReport(long Id, long PostId, long ThreadId, long MemberId, string MemberName, string Reason, DateTime CreatedAt, bool Done);

/// <summary>Who reads or writes: the signed-in member (or nobody), whether the forum treats them as a moderator, and whether they are banned.</summary>
internal sealed record Viewer(PluginMember? Member, bool IsMod, bool IsAdmin, bool Banned)
{
    public long Id => Member?.Id ?? 0;
    public bool SignedIn => Member is not null;
}

internal static class Access
{
    public const string All = "all", Members = "members", Role = "role", Mods = "mods";
}

internal sealed class ForumStore
{
    private readonly IPluginData _data;
    public ForumStore(IPluginData data) => _data = data;

    public IDataTable Boards => _data.Table("boards");
    public IDataTable Threads => _data.Table("threads");
    public IDataTable Posts => _data.Table("posts");
    public IDataTable Profiles => _data.Table("profiles");
    public IDataTable Reads => _data.Table("reads");
    public IDataTable Subs => _data.Table("subs");
    public IDataTable Reports => _data.Table("reports");

    /// <summary>The tables. The host creates and extends them on load; it never drops or retypes a column.</summary>
    public static void Declare(IDataBuilder d)
    {
        d.Table("boards", t => t
            .Text("title", required: true).Text("slug", required: true, unique: true).Text("description").Text("category", def: "")
            .Int("sort", def: "0").Text("read_access", def: Access.All).Text("write_access", def: Access.Members).Text("role", def: "")
            .Bool("locked", def: "0").Int("thread_count", def: "0").Int("post_count", def: "0")
            .Int("last_post_id", def: "0").Timestamp("last_post_at").Int("last_thread_id", def: "0").Text("last_thread_title", def: "")
            .Text("last_author", def: "").Int("last_author_id", def: "0")
            .Index("sort"));
        d.Table("threads", t => t
            .Int("board_id", required: true).Text("title", required: true).Int("author_id", def: "0").Text("author_name", def: "")
            .Timestamp("created_at").Timestamp("last_post_at").Int("last_post_id", def: "0").Text("last_author", def: "").Int("last_author_id", def: "0")
            .Int("reply_count", def: "0").Int("view_count", def: "0").Bool("sticky", def: "0").Bool("closed", def: "0").Int("first_post_id", def: "0")
            .Index("board_id", "last_post_at").Index("author_id").Index("last_post_at"));
        d.Table("posts", t => t
            .Int("thread_id", required: true).Int("board_id", required: true).Int("author_id", def: "0").Text("author_name", def: "")
            .Text("body", required: true).Timestamp("created_at").Timestamp("edited_at").Text("edited_by", def: "")
            .Index("thread_id").Index("author_id").Index("created_at"));
        d.Table("profiles", t => t
            .Int("member_id", required: true).Int("post_count", def: "0").Text("signature", def: "").Timestamp("last_post_at")
            .Bool("banned", def: "0").Text("ban_reason", def: "")
            .UniqueIndex("member_id"));
        d.Table("reads", t => t.Int("member_id", required: true).Int("thread_id", required: true).Int("last_post_id", def: "0").UniqueIndex("member_id", "thread_id"));
        d.Table("subs", t => t.Int("member_id", required: true).Int("thread_id", required: true).UniqueIndex("member_id", "thread_id").Index("thread_id"));
        d.Table("reports", t => t
            .Int("post_id", required: true).Int("thread_id", def: "0").Int("member_id", def: "0").Text("member_name", def: "")
            .Text("reason", def: "").Timestamp("created_at").Bool("done", def: "0").Index("done"));
    }

    // ---- mapping ------------------------------------------------------------------------------------------------

    private static DateTime Utc(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : DateTime.MinValue;
    private static DateTime? UtcN(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private static Board ToBoard(DataRow r) => new(r.Id, r.Text("title"), r.Text("slug"), r.Text("description"), r.Text("category"), r.Int("sort"),
        Norm(r.Text("read_access"), Access.All), Norm(r.Text("write_access"), Access.Members), r.Text("role"), r.Bool("locked"),
        r.Int("thread_count"), r.Int("post_count"), r.Int("last_post_id"), UtcN(r.Timestamp("last_post_at")), r.Int("last_thread_id"),
        r.Text("last_thread_title"), r.Text("last_author"), r.Int("last_author_id"));

    private static string Norm(string v, string fallback) => v is Access.All or Access.Members or Access.Role or Access.Mods ? v : fallback;

    private static ForumThread ToThread(DataRow r) => new(r.Id, r.Int("board_id"), r.Text("title"), r.Int("author_id"), r.Text("author_name"),
        Utc(r.Timestamp("created_at")), Utc(r.Timestamp("last_post_at")), r.Int("last_post_id"), r.Text("last_author"), r.Int("last_author_id"),
        r.Int("reply_count"), r.Int("view_count"), r.Bool("sticky"), r.Bool("closed"), r.Int("first_post_id"));

    private static ForumPost ToPost(DataRow r) => new(r.Id, r.Int("thread_id"), r.Int("board_id"), r.Int("author_id"), r.Text("author_name"), r.Text("body"),
        Utc(r.Timestamp("created_at")), UtcN(r.Timestamp("edited_at")), r.Text("edited_by"));

    private static ForumProfile ToProfile(DataRow r) => new(r.Int("member_id"), r.Int("post_count"), r.Text("signature"), UtcN(r.Timestamp("last_post_at")),
        r.Bool("banned"), r.Text("ban_reason"));

    private static ForumReport ToReport(DataRow r) => new(r.Id, r.Int("post_id"), r.Int("thread_id"), r.Int("member_id"), r.Text("member_name"), r.Text("reason"),
        Utc(r.Timestamp("created_at")), r.Bool("done"));

    // ---- boards -------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Board>> BoardsAsync(CancellationToken ct = default) =>
        (await Boards.QueryAsync(DataQuery.Everything.OrderBy("sort").OrderBy("title"), ct)).Select(ToBoard).ToList();

    public async Task<Board?> BoardAsync(long id, CancellationToken ct = default) => await Boards.GetAsync(id, ct) is { } r ? ToBoard(r) : null;

    public async Task<Board?> BoardBySlugAsync(string slug, CancellationToken ct = default) =>
        await Boards.FindByAsync("slug", slug, ct) is { } r ? ToBoard(r) : null;

    /// <summary>Addresses below /forum that are pages of the forum itself, so no board may take them.</summary>
    public static readonly string[] ReservedSlugs = ["t", "p", "suche", "aktuell", "mitglied", "registrieren", "neu", "abos", "u", "new", "search", "latest"];

    public static string Slugify(string text)
    {
        var s = (text ?? "").Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 60) slug = slug[..60].Trim('-');
        return slug;
    }

    /// <summary>A free slug for a board: the wish, cleaned, not reserved and not taken by another board (a counter is added).</summary>
    public async Task<string> FreeSlugAsync(string wish, long exceptId, CancellationToken ct = default)
    {
        var baseSlug = Slugify(wish);
        if (baseSlug.Length == 0) baseSlug = "forum";
        var slug = baseSlug;
        for (var i = 2; ; i++)
        {
            var taken = ReservedSlugs.Contains(slug) || await Boards.FindByAsync("slug", slug, ct) is { } r && r.Id != exceptId;
            if (!taken) return slug;
            slug = baseSlug + "-" + i.ToString(CultureInfo.InvariantCulture);
        }
    }

    public async Task<long> SaveBoardAsync(long id, string title, string slug, string description, string category, long sort,
        string read, string write, string role, bool locked, CancellationToken ct = default)
    {
        var values = new Dictionary<string, object?>
        {
            ["title"] = title, ["slug"] = await FreeSlugAsync(slug.Length > 0 ? slug : title, id, ct), ["description"] = description, ["category"] = category,
            ["sort"] = sort, ["read_access"] = Norm(read, Access.All), ["write_access"] = Norm(write, Access.Members), ["role"] = role.Trim(), ["locked"] = locked
        };
        if (id > 0) { await Boards.UpdateAsync(id, values, ct); return id; }
        return await Boards.InsertAsync(values, ct);
    }

    /// <summary>Deletes a board WITH its threads and posts (and what hangs off them). The caller asked twice.</summary>
    public async Task DeleteBoardAsync(long id, CancellationToken ct = default)
    {
        var threadIds = (await Threads.QueryAsync(DataQuery.Everything.Eq("board_id", id), ct)).Select(t => (object?)t.Id).ToList();
        await _data.InTransactionAsync(async d =>
        {
            if (threadIds.Count > 0)
            {
                await d.Table("reads").DeleteWhereAsync(DataQuery.Everything.Where("thread_id", DataOp.In, threadIds), ct);
                await d.Table("subs").DeleteWhereAsync(DataQuery.Everything.Where("thread_id", DataOp.In, threadIds), ct);
                await d.Table("reports").DeleteWhereAsync(DataQuery.Everything.Where("thread_id", DataOp.In, threadIds), ct);
            }
            await d.Table("posts").DeleteWhereAsync(DataQuery.Everything.Eq("board_id", id), ct);
            await d.Table("threads").DeleteWhereAsync(DataQuery.Everything.Eq("board_id", id), ct);
            await d.Table("boards").DeleteAsync(id, ct);
        }, ct);
        await RecountMembersAsync(ct);
    }

    // ---- access -------------------------------------------------------------------------------------------------

    /// <summary>May this viewer see the board (and its threads)? Moderators and the back office's administrator see everything.</summary>
    public static bool CanRead(Board b, Viewer v) => v.IsMod || v.IsAdmin || b.ReadAccess switch
    {
        Access.All => true,
        Access.Members => v.SignedIn,
        Access.Role => v.Member is { } m && b.Role.Length > 0 && m.InRole(b.Role),
        _ => false
    };

    /// <summary>May this viewer start a thread or reply here? Never without an account, never while banned; a locked board only for moderators.</summary>
    public static bool CanWrite(Board b, Viewer v)
    {
        if (!v.SignedIn || v.Banned || !CanRead(b, v)) return false;
        if (v.IsMod) return true;
        if (b.Locked) return false;
        return b.WriteAccess switch
        {
            Access.Members or Access.All => true,
            Access.Role => v.Member is { } m && b.Role.Length > 0 && m.InRole(b.Role),
            _ => false
        };
    }

    public async Task<IReadOnlyList<Board>> ReadableBoardsAsync(Viewer v, CancellationToken ct = default) =>
        (await BoardsAsync(ct)).Where(b => CanRead(b, v)).ToList();

    // ---- threads and posts --------------------------------------------------------------------------------------

    public async Task<ForumThread?> ThreadAsync(long id, CancellationToken ct = default) => await Threads.GetAsync(id, ct) is { } r ? ToThread(r) : null;
    public async Task<ForumPost?> PostAsync(long id, CancellationToken ct = default) => await Posts.GetAsync(id, ct) is { } r ? ToPost(r) : null;

    /// <summary>One page of a board: the pinned threads first, then by last activity.</summary>
    public async Task<(IReadOnlyList<ForumThread> Items, long Total)> BoardThreadsAsync(long boardId, int page, int size, CancellationToken ct = default)
    {
        var q = DataQuery.Everything.Eq("board_id", boardId);
        var total = await Threads.CountAsync(q, ct);
        var rows = await Threads.QueryAsync(q.OrderBy("sticky", true).OrderBy("last_post_at", true).Page((page - 1) * size, size), ct);
        return (rows.Select(ToThread).ToList(), total);
    }

    public async Task<(IReadOnlyList<ForumPost> Items, long Total)> ThreadPostsAsync(long threadId, int page, int size, CancellationToken ct = default)
    {
        var q = DataQuery.Everything.Eq("thread_id", threadId);
        var total = await Posts.CountAsync(q, ct);
        var rows = await Posts.QueryAsync(q.OrderBy("created_at").OrderBy("id").Page((page - 1) * size, size), ct);
        return (rows.Select(ToPost).ToList(), total);
    }

    /// <summary>The 1-based position of a post in its thread (for "jump to post" links onto the right page).</summary>
    public async Task<long> PositionAsync(ForumPost p, CancellationToken ct = default) =>
        await Posts.CountAsync(DataQuery.Everything.Eq("thread_id", p.ThreadId).Where("id", DataOp.Le, p.Id), ct);

    public async Task<IReadOnlyList<ForumThread>> LatestThreadsAsync(IReadOnlyCollection<long> boardIds, int take, CancellationToken ct = default)
    {
        if (boardIds.Count == 0) return Array.Empty<ForumThread>();
        var rows = await Threads.QueryAsync(DataQuery.Everything.Where("board_id", DataOp.In, boardIds.Cast<object?>().ToList())
            .OrderBy("last_post_at", true).Page(0, take), ct);
        return rows.Select(ToThread).ToList();
    }

    public async Task<IReadOnlyList<ForumPost>> LatestPostsAsync(IReadOnlyCollection<long>? boardIds, int take, long authorId = 0, CancellationToken ct = default)
    {
        var q = DataQuery.Everything;
        if (boardIds is not null)
        {
            if (boardIds.Count == 0) return Array.Empty<ForumPost>();
            q = q.Where("board_id", DataOp.In, boardIds.Cast<object?>().ToList());
        }
        if (authorId > 0) q = q.Eq("author_id", authorId);
        return (await Posts.QueryAsync(q.OrderBy("created_at", true).OrderBy("id", true).Page(0, take), ct)).Select(ToPost).ToList();
    }

    /// <summary>Searches thread titles and post texts of the given boards. Every word must occur (title or text); newest first.</summary>
    public async Task<IReadOnlyList<(ForumThread Thread, ForumPost? Post)>> SearchAsync(string query, IReadOnlyCollection<long> boardIds, int take, CancellationToken ct = default)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length >= 2).Select(w => w.Length > 40 ? w[..40] : w).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
        if (words.Count == 0 || boardIds.Count == 0) return Array.Empty<(ForumThread, ForumPost?)>();
        var inBoards = boardIds.Cast<object?>().ToList();
        // The database narrows by the first word; the others are checked here (ASCII case-insensitive like the database's Contains, plus Unicode).
        var first = words[0];
        var postRows = await Posts.QueryAsync(DataQuery.Everything.Where("board_id", DataOp.In, inBoards).Where("body", DataOp.Contains, first)
            .OrderBy("created_at", true).Page(0, 400), ct);
        var threadRows = await Threads.QueryAsync(DataQuery.Everything.Where("board_id", DataOp.In, inBoards).Where("title", DataOp.Contains, first)
            .OrderBy("last_post_at", true).Page(0, 200), ct);
        bool All(string text) => words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        var hits = new List<(ForumThread, ForumPost?)>();
        var seen = new HashSet<long>();
        foreach (var t in threadRows.Select(ToThread).Where(t => All(t.Title)))
            if (seen.Add(t.Id)) hits.Add((t, null));
        var threadCache = new Dictionary<long, ForumThread?>();
        foreach (var p in postRows.Select(ToPost))
        {
            if (!threadCache.TryGetValue(p.ThreadId, out var t)) threadCache[p.ThreadId] = t = await ThreadAsync(p.ThreadId, ct);
            if (t is null || !All(t.Title + " " + p.Body)) continue;
            if (seen.Add(t.Id)) hits.Add((t, p));
            if (hits.Count >= take) break;
        }
        return hits.OrderByDescending(h => h.Item2?.CreatedAt ?? h.Item1.LastPostAt).Take(take).ToList();
    }

    /// <summary>Starts a thread with its first post — one transaction with every counter it changes.</summary>
    public async Task<(long ThreadId, long PostId)> CreateThreadAsync(Board board, PluginMember author, string title, string body, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var name = AuthorName(author);
        return await _data.InTransactionAsync(async d =>
        {
            // A write first, so the transaction queues for the write lock instead of being refused half-way (SQLite: BEGIN IMMEDIATE semantics).
            await d.Table("boards").IncrementAsync(board.Id, "thread_count", 1, null, ct);
            var threadId = await d.Table("threads").InsertAsync(new Dictionary<string, object?>
            {
                ["board_id"] = board.Id, ["title"] = title, ["author_id"] = author.Id, ["author_name"] = name, ["created_at"] = now, ["last_post_at"] = now,
                ["last_author"] = name, ["last_author_id"] = author.Id
            }, ct);
            var postId = await d.Table("posts").InsertAsync(new Dictionary<string, object?>
            {
                ["thread_id"] = threadId, ["board_id"] = board.Id, ["author_id"] = author.Id, ["author_name"] = name, ["body"] = body, ["created_at"] = now
            }, ct);
            await d.Table("threads").UpdateAsync(threadId, new Dictionary<string, object?> { ["first_post_id"] = postId, ["last_post_id"] = postId }, ct);
            await d.Table("boards").IncrementAsync(board.Id, "post_count", 1, null, ct);
            await d.Table("boards").UpdateAsync(board.Id, new Dictionary<string, object?>
            {
                ["last_post_id"] = postId, ["last_post_at"] = now, ["last_thread_id"] = threadId, ["last_thread_title"] = title, ["last_author"] = name, ["last_author_id"] = author.Id
            }, ct);
            await CountMemberPostAsync(d, author.Id, now, ct);
            return (threadId, postId);
        }, ct);
    }

    /// <summary>A reply — one transaction with the thread's and the board's counters.</summary>
    public async Task<long> ReplyAsync(ForumThread thread, PluginMember author, string body, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var name = AuthorName(author);
        return await _data.InTransactionAsync(async d =>
        {
            await d.Table("threads").IncrementAsync(thread.Id, "reply_count", 1, null, ct);
            var postId = await d.Table("posts").InsertAsync(new Dictionary<string, object?>
            {
                ["thread_id"] = thread.Id, ["board_id"] = thread.BoardId, ["author_id"] = author.Id, ["author_name"] = name, ["body"] = body, ["created_at"] = now
            }, ct);
            await d.Table("threads").UpdateAsync(thread.Id, new Dictionary<string, object?>
            {
                ["last_post_at"] = now, ["last_post_id"] = postId, ["last_author"] = name, ["last_author_id"] = author.Id
            }, ct);
            await d.Table("boards").IncrementAsync(thread.BoardId, "post_count", 1, null, ct);
            await d.Table("boards").UpdateAsync(thread.BoardId, new Dictionary<string, object?>
            {
                ["last_post_id"] = postId, ["last_post_at"] = now, ["last_thread_id"] = thread.Id, ["last_thread_title"] = thread.Title, ["last_author"] = name, ["last_author_id"] = author.Id
            }, ct);
            await CountMemberPostAsync(d, author.Id, now, ct);
            return postId;
        }, ct);
    }

    private static async Task CountMemberPostAsync(IPluginData d, long memberId, DateTime now, CancellationToken ct)
    {
        var profiles = d.Table("profiles");
        var row = await profiles.FindByAsync("member_id", memberId, ct);
        if (row is null)
        {
            await profiles.InsertAsync(new Dictionary<string, object?> { ["member_id"] = memberId, ["post_count"] = 1L, ["last_post_at"] = now }, ct);
            return;
        }
        await profiles.IncrementAsync(row.Id, "post_count", 1, null, ct);
        await profiles.UpdateAsync(row.Id, new Dictionary<string, object?> { ["last_post_at"] = now }, ct);
    }

    public static string AuthorName(PluginMember m) => (m.DisplayName.Length > 0 ? m.DisplayName : m.Username).Trim() is { Length: > 0 } n ? (n.Length > 60 ? n[..60] : n) : "Mitglied " + m.Id;

    public async Task EditPostAsync(long postId, string body, string editedBy, CancellationToken ct = default) =>
        await Posts.UpdateAsync(postId, new Dictionary<string, object?> { ["body"] = body, ["edited_at"] = DateTime.UtcNow, ["edited_by"] = editedBy }, ct);

    public async Task RenameThreadAsync(long threadId, string title, CancellationToken ct = default)
    {
        await Threads.UpdateAsync(threadId, new Dictionary<string, object?> { ["title"] = title }, ct);
        await Boards.UpdateWhereAsync(DataQuery.Everything.Eq("last_thread_id", threadId), new Dictionary<string, object?> { ["last_thread_title"] = title }, ct);
    }

    public Task SetThreadFlagAsync(long threadId, string column, bool on, CancellationToken ct = default) =>
        Threads.UpdateAsync(threadId, new Dictionary<string, object?> { [column] = on }, ct);

    public Task CountViewAsync(long threadId, CancellationToken ct = default) => Threads.IncrementAsync(threadId, "view_count", 1, null, ct);

    /// <summary>Deletes ONE post. The first post of a thread stands for the thread: deleting it deletes the thread.</summary>
    public async Task DeletePostAsync(ForumPost post, CancellationToken ct = default)
    {
        var thread = await ThreadAsync(post.ThreadId, ct);
        if (thread is not null && thread.FirstPostId == post.Id) { await DeleteThreadAsync(thread.Id, ct); return; }
        await _data.InTransactionAsync(async d =>
        {
            await d.Table("posts").DeleteAsync(post.Id, ct);
            await d.Table("reports").DeleteWhereAsync(DataQuery.Everything.Eq("post_id", post.Id), ct);
        }, ct);
        await RecountThreadAsync(post.ThreadId, ct);
        await RecountBoardAsync(post.BoardId, ct);
        if (post.AuthorId > 0) await RecountMemberAsync(post.AuthorId, ct);
    }

    public async Task DeleteThreadAsync(long threadId, CancellationToken ct = default)
    {
        var thread = await ThreadAsync(threadId, ct);
        if (thread is null) return;
        var authors = (await Posts.QueryAsync(DataQuery.Everything.Eq("thread_id", threadId), ct)).Select(r => r.Int("author_id")).Where(a => a > 0).Distinct().ToList();
        await _data.InTransactionAsync(async d =>
        {
            await d.Table("posts").DeleteWhereAsync(DataQuery.Everything.Eq("thread_id", threadId), ct);
            await d.Table("reads").DeleteWhereAsync(DataQuery.Everything.Eq("thread_id", threadId), ct);
            await d.Table("subs").DeleteWhereAsync(DataQuery.Everything.Eq("thread_id", threadId), ct);
            await d.Table("reports").DeleteWhereAsync(DataQuery.Everything.Eq("thread_id", threadId), ct);
            await d.Table("threads").DeleteAsync(threadId, ct);
        }, ct);
        await RecountBoardAsync(thread.BoardId, ct);
        foreach (var a in authors) await RecountMemberAsync(a, ct);
    }

    public async Task MoveThreadAsync(ForumThread thread, long toBoard, CancellationToken ct = default)
    {
        if (thread.BoardId == toBoard) return;
        await _data.InTransactionAsync(async d =>
        {
            await d.Table("threads").UpdateAsync(thread.Id, new Dictionary<string, object?> { ["board_id"] = toBoard }, ct);
            await d.Table("posts").UpdateWhereAsync(DataQuery.Everything.Eq("thread_id", thread.Id), new Dictionary<string, object?> { ["board_id"] = toBoard }, ct);
        }, ct);
        await RecountBoardAsync(thread.BoardId, ct);
        await RecountBoardAsync(toBoard, ct);
    }

    /// <summary>Counts a thread again from its posts: replies and the last post.</summary>
    public async Task RecountThreadAsync(long threadId, CancellationToken ct = default)
    {
        var q = DataQuery.Everything.Eq("thread_id", threadId);
        var count = await Posts.CountAsync(q, ct);
        var last = await Posts.FindAsync(q.OrderBy("created_at", true).OrderBy("id", true), ct);
        var first = await Posts.FindAsync(q.OrderBy("created_at").OrderBy("id"), ct);
        if (last is null) { await Threads.DeleteAsync(threadId, ct); return; }
        await Threads.UpdateAsync(threadId, new Dictionary<string, object?>
        {
            ["reply_count"] = Math.Max(0, count - 1), ["last_post_id"] = last.Id, ["last_post_at"] = last.Timestamp("created_at"),
            ["last_author"] = last.Text("author_name"), ["last_author_id"] = last.Int("author_id"), ["first_post_id"] = first?.Id ?? last.Id
        }, ct);
    }

    /// <summary>Counts a board again from its threads and posts: both counters and the last post.</summary>
    public async Task RecountBoardAsync(long boardId, CancellationToken ct = default)
    {
        var threads = await Threads.CountAsync(DataQuery.Everything.Eq("board_id", boardId), ct);
        var posts = await Posts.CountAsync(DataQuery.Everything.Eq("board_id", boardId), ct);
        var last = await Posts.FindAsync(DataQuery.Everything.Eq("board_id", boardId).OrderBy("created_at", true).OrderBy("id", true), ct);
        var lastThread = last is null ? null : await ThreadAsync(last.Int("thread_id"), ct);
        await Boards.UpdateAsync(boardId, new Dictionary<string, object?>
        {
            ["thread_count"] = threads, ["post_count"] = posts, ["last_post_id"] = last?.Id ?? 0L, ["last_post_at"] = last?.Timestamp("created_at"),
            ["last_thread_id"] = lastThread?.Id ?? 0L, ["last_thread_title"] = lastThread?.Title ?? "", ["last_author"] = last?.Text("author_name") ?? "",
            ["last_author_id"] = last?.Int("author_id") ?? 0L
        }, ct);
    }

    public async Task RecountMemberAsync(long memberId, CancellationToken ct = default)
    {
        var count = await Posts.CountAsync(DataQuery.Everything.Eq("author_id", memberId), ct);
        if (await Profiles.FindByAsync("member_id", memberId, ct) is { } row)
            await Profiles.UpdateAsync(row.Id, new Dictionary<string, object?> { ["post_count"] = count }, ct);
    }

    private async Task RecountMembersAsync(CancellationToken ct)
    {
        foreach (var p in await Profiles.QueryAsync(DataQuery.Everything, ct)) await RecountMemberAsync(p.Int("member_id"), ct);
    }

    // ---- members ------------------------------------------------------------------------------------------------

    public async Task<ForumProfile?> ProfileAsync(long memberId, CancellationToken ct = default) =>
        await Profiles.FindByAsync("member_id", memberId, ct) is { } r ? ToProfile(r) : null;

    public async Task<IReadOnlyList<ForumProfile>> ProfilesAsync(CancellationToken ct = default) =>
        (await Profiles.QueryAsync(DataQuery.Everything.OrderBy("post_count", true), ct)).Select(ToProfile).ToList();

    public async Task SaveProfileAsync(long memberId, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
    {
        if (await Profiles.FindByAsync("member_id", memberId, ct) is { } row) await Profiles.UpdateAsync(row.Id, values, ct);
        else
        {
            var v = new Dictionary<string, object?>(values) { ["member_id"] = memberId };
            await Profiles.InsertAsync(v, ct);
        }
    }

    // ---- unread, subscriptions -----------------------------------------------------------------------------------

    /// <summary>The last post id this member has seen per thread, for the given threads.</summary>
    public async Task<Dictionary<long, long>> ReadMarksAsync(long memberId, IReadOnlyCollection<long> threadIds, CancellationToken ct = default)
    {
        if (memberId <= 0 || threadIds.Count == 0) return new();
        var rows = await Reads.QueryAsync(DataQuery.Everything.Eq("member_id", memberId).Where("thread_id", DataOp.In, threadIds.Cast<object?>().ToList()), ct);
        return rows.GroupBy(r => r.Int("thread_id")).ToDictionary(g => g.Key, g => g.Max(r => r.Int("last_post_id")));
    }

    public async Task MarkReadAsync(long memberId, long threadId, long lastPostId, CancellationToken ct = default)
    {
        if (memberId <= 0) return;
        var row = await Reads.FindAsync(DataQuery.Everything.Eq("member_id", memberId).Eq("thread_id", threadId), ct);
        if (row is null)
        {
            try { await Reads.InsertAsync(new Dictionary<string, object?> { ["member_id"] = memberId, ["thread_id"] = threadId, ["last_post_id"] = lastPostId }, ct); }
            catch { /* two tabs at once: the other one wrote it */ }
        }
        else if (row.Int("last_post_id") < lastPostId)
            await Reads.UpdateAsync(row.Id, new Dictionary<string, object?> { ["last_post_id"] = lastPostId }, ct);
    }

    /// <summary>"Everything read": the member's mark for every thread of these boards moves to its last post.</summary>
    public async Task MarkAllReadAsync(long memberId, IReadOnlyCollection<long> boardIds, CancellationToken ct = default)
    {
        if (memberId <= 0 || boardIds.Count == 0) return;
        var threads = await Threads.QueryAsync(DataQuery.Everything.Where("board_id", DataOp.In, boardIds.Cast<object?>().ToList()), ct);
        foreach (var t in threads) await MarkReadAsync(memberId, t.Id, t.Int("last_post_id"), ct);
    }

    public async Task<bool> IsSubscribedAsync(long memberId, long threadId, CancellationToken ct = default) =>
        memberId > 0 && await Subs.FindAsync(DataQuery.Everything.Eq("member_id", memberId).Eq("thread_id", threadId), ct) is not null;

    public async Task SubscribeAsync(long memberId, long threadId, bool on, CancellationToken ct = default)
    {
        var q = DataQuery.Everything.Eq("member_id", memberId).Eq("thread_id", threadId);
        if (!on) { await Subs.DeleteWhereAsync(q, ct); return; }
        if (await Subs.FindAsync(q, ct) is null)
        {
            try { await Subs.InsertAsync(new Dictionary<string, object?> { ["member_id"] = memberId, ["thread_id"] = threadId }, ct); }
            catch { /* already there */ }
        }
    }

    public async Task<IReadOnlyList<long>> SubscribersAsync(long threadId, CancellationToken ct = default) =>
        (await Subs.QueryAsync(DataQuery.Everything.Eq("thread_id", threadId), ct)).Select(r => r.Int("member_id")).Distinct().ToList();

    public async Task<IReadOnlyList<long>> SubscribedThreadsAsync(long memberId, CancellationToken ct = default) =>
        (await Subs.QueryAsync(DataQuery.Everything.Eq("member_id", memberId), ct)).Select(r => r.Int("thread_id")).ToList();

    // ---- reports ------------------------------------------------------------------------------------------------

    public async Task<long> ReportAsync(ForumPost post, PluginMember by, string reason, CancellationToken ct = default) =>
        await Reports.InsertAsync(new Dictionary<string, object?>
        {
            ["post_id"] = post.Id, ["thread_id"] = post.ThreadId, ["member_id"] = by.Id, ["member_name"] = AuthorName(by),
            ["reason"] = reason, ["created_at"] = DateTime.UtcNow
        }, ct);

    public async Task<IReadOnlyList<ForumReport>> ReportsAsync(bool open, CancellationToken ct = default) =>
        (await Reports.QueryAsync(DataQuery.Everything.Eq("done", !open ? true : false).OrderBy("created_at", true).Page(0, 300), ct)).Select(ToReport).ToList();

    public Task<long> OpenReportCountAsync(CancellationToken ct = default) => Reports.CountAsync(DataQuery.Everything.Eq("done", false), ct);

    // ---- data protection ----------------------------------------------------------------------------------------

    /// <summary>
    /// An account is deleted: its posts STAY (a thread would lose its sense if replies disappeared mid-conversation) but are no longer linked to it —
    /// the name becomes "deleted member", the id 0 — and everything the forum kept only about that person goes: profile, signature, read marks, subscriptions,
    /// the reports they filed.
    /// </summary>
    public async Task EraseMemberAsync(long memberId, string deletedName, CancellationToken ct = default)
    {
        if (memberId <= 0) return;
        await _data.InTransactionAsync(async d =>
        {
            var anon = new Dictionary<string, object?> { ["author_id"] = 0L, ["author_name"] = deletedName };
            await d.Table("posts").UpdateWhereAsync(DataQuery.Everything.Eq("author_id", memberId), anon, ct);
            await d.Table("threads").UpdateWhereAsync(DataQuery.Everything.Eq("author_id", memberId), anon, ct);
            await d.Table("threads").UpdateWhereAsync(DataQuery.Everything.Eq("last_author_id", memberId),
                new Dictionary<string, object?> { ["last_author_id"] = 0L, ["last_author"] = deletedName }, ct);
            await d.Table("boards").UpdateWhereAsync(DataQuery.Everything.Eq("last_author_id", memberId),
                new Dictionary<string, object?> { ["last_author_id"] = 0L, ["last_author"] = deletedName }, ct);
            await d.Table("posts").UpdateWhereAsync(DataQuery.Everything.Eq("edited_by", "member:" + memberId), new Dictionary<string, object?> { ["edited_by"] = "" }, ct);
            await d.Table("profiles").DeleteWhereAsync(DataQuery.Everything.Eq("member_id", memberId), ct);
            await d.Table("reads").DeleteWhereAsync(DataQuery.Everything.Eq("member_id", memberId), ct);
            await d.Table("subs").DeleteWhereAsync(DataQuery.Everything.Eq("member_id", memberId), ct);
            await d.Table("reports").DeleteWhereAsync(DataQuery.Everything.Eq("member_id", memberId), ct);
        }, ct);
    }

    public async Task<IReadOnlyList<ForumPost>> PostsOfAsync(long memberId, CancellationToken ct = default) =>
        (await Posts.QueryAsync(DataQuery.Everything.Eq("author_id", memberId).OrderBy("created_at"), ct)).Select(ToPost).ToList();

    // ---- figures ------------------------------------------------------------------------------------------------

    public async Task<(long Threads, long Posts, long Members)> TotalsAsync(CancellationToken ct = default) =>
        (await Threads.CountAsync(null, ct), await Posts.CountAsync(null, ct), await Profiles.CountAsync(DataQuery.Everything.Where("post_count", DataOp.Gt, 0L), ct));
}
