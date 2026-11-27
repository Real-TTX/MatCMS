using System.Globalization;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Forum;

/// <summary>
/// The forum's operations for the site's AI assistant, the cloud and MCP: what an operator or moderator does in the back office — boards (list, read,
/// create, change, delete an EMPTY one), threads (list, read with posts, moderate: rename, pin, close, move), reports and the forum's side of the members
/// (ban, signature). Every write answers with an <c>undo</c> naming an INTERNAL operation of this plugin that puts things back.
/// <para>Everything goes through <see cref="ForumStore"/> — the same code the admin's forms use, with the same rules (title lengths, access values, a free
/// slug, counters recounted on a move). Nothing an argument names becomes a column or a table. What is NOT offered, on purpose: starting threads or
/// writing posts (a post needs an author account, and an AI writing in a member's name is not moderation), deleting threads or posts and deleting a board
/// with threads (none of them can be undone — closing a thread, locking a board or reading it to moderators only switches off without losing anything).</para>
/// <para>Reports (who reported) and members (e-mail, signature, ban) are personal data: those operations are never offered to the assistant, whose every
/// answer goes to an outside model provider.</para>
/// </summary>
internal static class ForumOps
{
    private const int BoardTitleMin = 2, BoardTitleMax = 80, ThreadTitleMin = 3, ThreadTitleMax = 120, MaxBodyOut = 4000;

    // ---- boards ---------------------------------------------------------------------------------------------------------

    public static readonly PluginOperationSpec BoardList = new("board.list",
        "Foren auflisten (Reihenfolge der Übersicht): id, Titel, Adresse, Kategorie, Beschreibung, wer lesen darf (read: all | members | role | mods), wer schreiben darf (write: members | role | mods), Rolle, gesperrt, Reihenfolge, Zahl der Themen und Beiträge, letzter Beitrag.",
        "{}", Writes: false);
    public static readonly PluginOperationSpec BoardRead = new("board.read",
        "Ein Forum lesen: alle Felder wie bei board.list, dazu seine neuesten Themen (höchstens 20).",
        """{"id": 3} oder {"slug": "allgemeines"}""", Writes: false);
    public static readonly PluginOperationSpec BoardCreate = new("board.create",
        "Neues Forum anlegen. title: 2–80 Zeichen. slug: Adresse unter /forum/ (ohne Angabe aus dem Titel; eine belegte oder reservierte Adresse bekommt eine Nummer). category: Foren gleicher Kategorie stehen in der Übersicht zusammen. read: wer lesen darf (all | members | role | mods), write: wer schreiben darf (members | role | mods); bei role muss role (Rolle der Mitgliederkonten) gesetzt sein. locked: nur Moderatoren schreiben. sort: Reihenfolge (kleiner = weiter oben; ohne Angabe ans Ende).",
        """{"title": "Ankündigungen", "slug"?: "ankuendigungen", "description"?: "…", "category"?: "Allgemein", "read"?: "all", "write"?: "mods", "role"?: "", "locked"?: false, "sort"?: 20}""");
    public static readonly PluginOperationSpec BoardUpdate = new("board.update",
        "Ein Forum ändern; nicht genannte Felder bleiben. Felder wie bei board.create. Abschalten ohne Datenverlust: locked: true (nur Moderatoren schreiben) oder read: \"mods\" (nur Moderatoren sehen es).",
        """{"id": 3, "title"?, "slug"?, "description"?, "category"?, "read"?, "write"?, "role"?, "locked"?, "sort"?}""");
    public static readonly PluginOperationSpec BoardDelete = new("board.delete",
        "Ein LEERES Forum (ohne Themen) löschen. Ein Forum mit Themen wird nicht gelöscht – zum Abschalten board.update mit locked oder read: \"mods\".",
        """{"id": 3} oder {"slug": "…"}""");
    public static readonly PluginOperationSpec BoardRemove = new("board.remove", "Entfernt ein eben angelegtes Forum, solange es keine Themen hat (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec BoardRestore = new("board.restore", "Stellt ein Forum aus einer Rückgängig-Aufzeichnung wieder her.", Internal: true);

    // ---- threads --------------------------------------------------------------------------------------------------------

    public static readonly PluginOperationSpec ThreadList = new("thread.list",
        "Themen auflisten: id, Titel, Forum, Verfasser (Anzeigename), Antworten, Aufrufe, angeheftet, geschlossen, letzter Beitrag, Adresse. board: nur dieses Forum (id oder Adresse; dann angeheftete zuerst), sonst alle nach letzter Aktivität. search: Wörter in Titel oder Text (mind. 2 Zeichen). limit ≤ 100 (Standard 30), offset zum Blättern.",
        """{"board"?: "allgemeines", "search"?: "text", "limit"?: 30, "offset"?: 0}""", Writes: false);
    public static readonly PluginOperationSpec ThreadRead = new("thread.read",
        "Ein Thema lesen mit seinen Beiträgen, ältester zuerst: Verfasser (Anzeigename), Zeit, bearbeitet, Text als BBCode (je höchstens 4000 Zeichen). Höchstens limit Beiträge (≤ 50, Standard 20) der Seite page.",
        """{"id": 42, "page"?: 1, "limit"?: 20}""", Writes: false);
    public static readonly PluginOperationSpec ThreadUpdate = new("thread.update",
        "Ein Thema moderieren; nicht genannte Felder bleiben. title: neuer Titel (3–120 Zeichen). sticky: oben anheften. closed: schließen (keine Antworten mehr). board: in ein anderes Forum verschieben (id oder Adresse). Es geht dabei keine E-Mail raus.",
        """{"id": 42, "title"?: "…", "sticky"?: true, "closed"?: false, "board"?: "ankuendigungen"}""");
    public static readonly PluginOperationSpec ThreadRestore = new("thread.restore", "Stellt Titel, Forum und Status eines Themas aus einer Rückgängig-Aufzeichnung wieder her.", Internal: true);

    // ---- reports and members (personal data) ---------------------------------------------------------------------------

    public static readonly PluginOperationSpec ReportList = new("report.list",
        "Meldungen von Beiträgen auflisten (neueste zuerst): id, Grund, wer gemeldet hat, wann, erledigt, der gemeldete Beitrag (id, Verfasser, Textauszug, Thema, Adresse). open: true = offene (Standard), false = erledigte. limit ≤ 300.",
        """{"open"?: true, "limit"?: 50}""", Writes: false, PersonalData: true);
    public static readonly PluginOperationSpec ReportUpdate = new("report.update",
        "Eine Meldung als erledigt markieren (done: true) oder wieder öffnen (done: false).", """{"id": 7, "done": true}""", PersonalData: true);
    public static readonly PluginOperationSpec ReportRestore = new("report.restore", "Setzt den Stand einer Meldung aus einer Rückgängig-Aufzeichnung zurück.", PersonalData: true, Internal: true);
    public static readonly PluginOperationSpec MemberList = new("member.list",
        "Mitglieder des Forums (Konten mit Forenprofil) auflisten: id (Kontonummer), Name, E-Mail, Konto vorhanden, Moderator, gesperrt, Sperrgrund, Beitragszahl, letzter Beitrag, Signatur. search: in Name oder E-Mail. limit ≤ 300.",
        """{"search"?: "…", "limit"?: 50}""", Writes: false, PersonalData: true);
    public static readonly PluginOperationSpec MemberUpdate = new("member.update",
        "Ein Mitglied im Forum sperren oder entsperren (banned: true | false; banReason: Grund, nur im Admin sichtbar) oder seine Signatur entfernen (clearSignature: true). Das Konto der Website bleibt unverändert.",
        """{"id": 17, "banned"?: true, "banReason"?: "…", "clearSignature"?: true}""", PersonalData: true);
    public static readonly PluginOperationSpec MemberRestore = new("member.restore", "Stellt Sperre und Signatur eines Mitglieds aus einer Rückgängig-Aufzeichnung wieder her.", PersonalData: true, Internal: true);

    public static void Register(IPluginBuilder b)
    {
        b.Operation(BoardList, BoardListAsync);
        b.Operation(BoardRead, BoardReadAsync);
        b.Operation(BoardCreate, BoardCreateAsync);
        b.Operation(BoardUpdate, BoardUpdateAsync);
        b.Operation(BoardDelete, BoardDeleteAsync);
        b.Operation(BoardRemove, BoardRemoveAsync);
        b.Operation(BoardRestore, BoardRestoreAsync);
        b.Operation(ThreadList, ThreadListAsync);
        b.Operation(ThreadRead, ThreadReadAsync);
        b.Operation(ThreadUpdate, ThreadUpdateAsync);
        b.Operation(ThreadRestore, ThreadRestoreAsync);
        b.Operation(ReportList, ReportListAsync);
        b.Operation(ReportUpdate, ReportUpdateAsync);
        b.Operation(ReportRestore, ReportRestoreAsync);
        b.Operation(MemberList, MemberListAsync);
        b.Operation(MemberUpdate, MemberUpdateAsync);
        b.Operation(MemberRestore, MemberRestoreAsync);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static JsonObject Answer(string status, string message, JsonNode? data = null, JsonNode? undo = null)
    {
        var o = new JsonObject { ["status"] = status, ["message"] = message };
        if (data is not null) o["data"] = data;
        if (undo is not null) o["undo"] = undo;
        return o;
    }

    private static JsonObject Undo(PluginOperationSpec op, JsonObject args) => new() { ["name"] = op.Name, ["args"] = args };
    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : null;
    private static bool? Flag(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    private static long? Long(JsonObject o, string key) =>
        o[key] is JsonValue v && (v.TryGetValue<long>(out var n) || long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ? n : null;
    private static int Limit(JsonObject o, string key, int def, int max) => (int)Math.Clamp(Long(o, key) ?? def, 1, max);
    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;

    /// <summary>A board named by id (a number, also as text) or by its address.</summary>
    private static async Task<Board?> BoardOfAsync(ForumStore store, JsonNode? node, CancellationToken ct)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var id)) return await store.BoardAsync(id, ct);
        var s = (v.TryGetValue<string>(out var str) ? str : v.ToString()).Trim();
        if (s.Length == 0) return null;
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) ? await store.BoardAsync(id, ct) : await store.BoardBySlugAsync(s.ToLowerInvariant(), ct);
    }

    private static Task<Board?> FindBoardAsync(ForumStore store, JsonObject o, CancellationToken ct) =>
        BoardOfAsync(store, o["id"] ?? o["slug"] ?? o["board"], ct);

    private static readonly string[] ReadValues = [Access.All, Access.Members, Access.Role, Access.Mods];
    private static readonly string[] WriteValues = [Access.Members, Access.Role, Access.Mods];

    private static JsonObject BoardJson(Board b) => new()
    {
        ["id"] = b.Id, ["title"] = b.Title, ["slug"] = b.Slug, ["url"] = ForumUrls.Board(b), ["category"] = b.Category, ["description"] = b.Description,
        ["read"] = b.ReadAccess, ["write"] = b.WriteAccess, ["role"] = b.Role, ["locked"] = b.Locked, ["sort"] = b.Sort,
        ["threads"] = b.ThreadCount, ["posts"] = b.PostCount, ["lastPostAt"] = ForumHtml.Iso(b.LastPostAt),
    };

    /// <summary>What <c>board.restore</c> takes back: the fields the admin's form edits.</summary>
    private static JsonObject BoardState(Board b) => new()
    {
        ["id"] = b.Id, ["title"] = b.Title, ["slug"] = b.Slug, ["description"] = b.Description, ["category"] = b.Category, ["sort"] = b.Sort,
        ["read"] = b.ReadAccess, ["write"] = b.WriteAccess, ["role"] = b.Role, ["locked"] = b.Locked,
    };

    private static JsonObject ThreadJson(ForumThread t, IReadOnlyList<Board> boards) => new()
    {
        ["id"] = t.Id, ["title"] = t.Title, ["boardId"] = t.BoardId, ["board"] = boards.FirstOrDefault(b => b.Id == t.BoardId)?.Title ?? "",
        ["author"] = t.AuthorName, ["created"] = ForumHtml.Iso(t.CreatedAt), ["replies"] = t.ReplyCount, ["views"] = t.ViewCount,
        ["sticky"] = t.Sticky, ["closed"] = t.Closed, ["lastPostAt"] = ForumHtml.Iso(t.LastPostAt), ["lastAuthor"] = t.LastAuthor, ["url"] = ForumUrls.Thread(t),
    };

    // ---- boards -------------------------------------------------------------------------------------------------------

    private static async Task<JsonNode?> BoardListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var boards = await new ForumStore(ctx.Data).BoardsAsync(ct);
        return Answer("applied", boards.Count + " Foren.", new JsonObject { ["boards"] = new JsonArray(boards.Select(b => (JsonNode)BoardJson(b)).ToArray()) });
    }

    private static async Task<JsonNode?> BoardReadAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        if (await FindBoardAsync(store, args as JsonObject ?? new JsonObject(), ct) is not { } b) return Answer("failed", "Forum nicht gefunden.");
        var boards = await store.BoardsAsync(ct);
        var data = BoardJson(b);
        data["latestThreads"] = new JsonArray((await store.BoardThreadsAsync(b.Id, 1, 20, ct)).Items.Select(t => (JsonNode)ThreadJson(t, boards)).ToArray());
        return Answer("applied", "Forum „" + b.Title + "“.", data);
    }

    /// <summary>The values a board op sends, checked like the admin's form checks them; null fields were not sent. Answers the refusal or null.</summary>
    private static string? ReadBoardFields(JsonObject o, Board? current, out BoardFields f)
    {
        f = new BoardFields(
            Str(o, "title")?.Trim(), Str(o, "slug")?.Trim(), Str(o, "description")?.Trim(), Str(o, "category")?.Trim(),
            Str(o, "read")?.Trim().ToLowerInvariant(), Str(o, "write")?.Trim().ToLowerInvariant(), Str(o, "role")?.Trim(), Flag(o, "locked"), Long(o, "sort"));
        if (current is null && f.Title is null) return "Bitte einen Titel angeben (2–80 Zeichen).";
        if (f.Title is { } t && t.Length is < BoardTitleMin or > BoardTitleMax) return "Der Titel eines Forums hat 2 bis 80 Zeichen.";
        if (f.Read is { } r && !ReadValues.Contains(r)) return "read ist eines von: " + string.Join(", ", ReadValues) + ".";
        if (f.Write is { } w && !WriteValues.Contains(w)) return "write ist eines von: " + string.Join(", ", WriteValues) + ".";
        if (f.Role is { Length: > 60 }) return "Ein Rollenname hat höchstens 60 Zeichen.";
        var read = f.Read ?? current?.ReadAccess ?? Access.All;
        var write = f.Write ?? current?.WriteAccess ?? Access.Members;
        var role = f.Role ?? current?.Role ?? "";
        if ((read == Access.Role || write == Access.Role) && role.Length == 0) return "Bei „role“ bitte die Rolle angeben (role).";
        if (o.ContainsKey("slug") && f.Slug is { Length: > 0 } s && ForumStore.Slugify(s).Length == 0) return "Die Adresse braucht Buchstaben oder Ziffern.";
        return null;
    }

    private sealed record BoardFields(string? Title, string? Slug, string? Description, string? Category, string? Read, string? Write, string? Role, bool? Locked, long? Sort);

    private static async Task<JsonNode?> BoardCreateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        if (ReadBoardFields(o, null, out var f) is { } error) return Answer("failed", error);
        var store = new ForumStore(ctx.Data);
        var boards = await store.BoardsAsync(ct);
        // Add-only: an address asked for that a board has, or (without an address) a board of the same title, is that board — never a second one.
        var wanted = f.Slug is { Length: > 0 } ws ? ForumStore.Slugify(ws) : "";
        var same = wanted.Length > 0 ? boards.FirstOrDefault(b => b.Slug == wanted) : boards.FirstOrDefault(b => string.Equals(b.Title, f.Title, StringComparison.OrdinalIgnoreCase));
        if (same is not null) return Answer("skipped-exists", "Das Forum „" + same.Title + "“ gibt es schon.", new JsonObject { ["id"] = same.Id, ["url"] = ForumUrls.Board(same) });
        var sort = f.Sort ?? (boards.Count + 1) * 10L;
        var id = await store.SaveBoardAsync(0, f.Title!, f.Slug ?? "", Clip(f.Description ?? "", 2000), Clip(f.Category ?? "", 80), sort,
            f.Read ?? Access.All, f.Write ?? Access.Members, f.Role ?? "", f.Locked ?? false, ct);
        var made = (await store.BoardAsync(id, ct))!;
        ctx.Log($"Forum #{id} „{made.Title}“ angelegt über eine Operation");
        return Answer("applied", "Forum „" + made.Title + "“ angelegt (" + ForumUrls.Board(made) + ").", new JsonObject { ["id"] = id, ["slug"] = made.Slug, ["url"] = ForumUrls.Board(made) },
            Undo(BoardRemove, new JsonObject { ["id"] = id }));
    }

    private static async Task<JsonNode?> BoardUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o || Long(o, "id") is not { } id) return Answer("failed", "Bitte die id des Forums angeben.");
        var store = new ForumStore(ctx.Data);
        if (await store.BoardAsync(id, ct) is not { } b) return Answer("failed", "Forum nicht gefunden.");
        if (ReadBoardFields(o, b, out var f) is { } error) return Answer("failed", error);
        if (f is { Title: null, Slug: null, Description: null, Category: null, Read: null, Write: null, Role: null, Locked: null, Sort: null })
            return Answer("skipped", "Nichts zu ändern.");
        await store.SaveBoardAsync(b.Id, f.Title ?? b.Title, f.Slug is { Length: > 0 } s ? s : b.Slug, f.Description is { } d ? Clip(d, 2000) : b.Description,
            f.Category is { } c ? Clip(c, 80) : b.Category, f.Sort ?? b.Sort, f.Read ?? b.ReadAccess, f.Write ?? b.WriteAccess, f.Role ?? b.Role, f.Locked ?? b.Locked, ct);
        var now = (await store.BoardAsync(b.Id, ct))!;
        return Answer("applied", "Forum „" + now.Title + "“ geändert.", new JsonObject { ["id"] = now.Id, ["slug"] = now.Slug, ["url"] = ForumUrls.Board(now) },
            Undo(BoardRestore, BoardState(b)));
    }

    private static async Task<JsonNode?> BoardDeleteAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new ForumStore(ctx.Data);
        if (await FindBoardAsync(store, args as JsonObject ?? new JsonObject(), ct) is not { } b) return Answer("failed", "Forum nicht gefunden.");
        var threads = await store.Threads.CountAsync(DataQuery.Everything.Eq("board_id", b.Id), ct);
        if (threads > 0)
            return Answer("failed", $"Das Forum „{b.Title}“ hat {threads} Themen und wird nicht gelöscht. Zum Abschalten: board.update mit locked: true oder read: \"mods\".");
        await store.DeleteBoardAsync(b.Id, ct);
        ctx.Log($"Forum #{b.Id} „{b.Title}“ (leer) gelöscht über eine Operation");
        return Answer("applied", "Forum „" + b.Title + "“ gelöscht.", null, Undo(BoardRestore, BoardState(b)));
    }

    private static async Task<JsonNode?> BoardRemoveAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o || Long(o, "id") is not { } id) return Answer("failed", "id fehlt.");
        var store = new ForumStore(ctx.Data);
        if (await store.BoardAsync(id, ct) is null) return Answer("skipped", "Das Forum gibt es nicht mehr.");
        // Somebody wrote in it since: removing it would take their posts along — that is no undo any more.
        if (await store.Threads.CountAsync(DataQuery.Everything.Eq("board_id", id), ct) > 0) return Answer("skipped", "Im Forum wurde inzwischen geschrieben; es bleibt stehen.");
        await store.DeleteBoardAsync(id, ct);
        return Answer("applied", "Forum entfernt.");
    }

    /// <summary>Puts a board's fields back (undo of a change), or creates it again when it is gone (undo of deleting an empty board).</summary>
    private static async Task<JsonNode?> BoardRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var store = new ForumStore(ctx.Data);
        var existing = Long(o, "id") is { } id ? await store.BoardAsync(id, ct) : null;
        var title = Str(o, "title") is { Length: > 0 } t ? t : existing?.Title ?? "Forum";
        await store.SaveBoardAsync(existing?.Id ?? 0, title, Str(o, "slug") ?? existing?.Slug ?? "", Str(o, "description") ?? "", Str(o, "category") ?? "",
            Long(o, "sort") ?? existing?.Sort ?? 0, Str(o, "read") ?? Access.All, Str(o, "write") ?? Access.Members, Str(o, "role") ?? "", Flag(o, "locked") ?? false, ct);
        return Answer("applied", "Forum „" + title + "“ wiederhergestellt.");
    }

    // ---- threads ------------------------------------------------------------------------------------------------------

    private static async Task<JsonNode?> ThreadListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        var store = new ForumStore(ctx.Data);
        var boards = await store.BoardsAsync(ct);
        Board? board = null;
        if (o["board"] is not null && (board = await BoardOfAsync(store, o["board"], ct)) is null) return Answer("failed", "Forum nicht gefunden.");
        var limit = Limit(o, "limit", 30, 100);
        var offset = (int)Math.Max(0, Long(o, "offset") ?? 0);
        var search = Clip((Str(o, "search") ?? "").Trim(), 100);
        IReadOnlyList<ForumThread> items;
        long total;
        if (search.Length > 0)
        {
            if (search.Length < 2) return Answer("failed", "Die Suche braucht mindestens 2 Zeichen.");
            var hits = await store.SearchAsync(search, board is null ? boards.Select(b => b.Id).ToList() : [board.Id], offset + limit, ct);
            total = hits.Count;
            items = hits.Select(h => h.Thread).Skip(offset).Take(limit).ToList();
        }
        else (items, total) = await store.ThreadsWindowAsync(board?.Id ?? 0, offset, limit, ct);
        return Answer("applied", $"{items.Count} von {total} Themen.", new JsonObject
        {
            ["total"] = total, ["offset"] = offset, ["threads"] = new JsonArray(items.Select(t => (JsonNode)ThreadJson(t, boards)).ToArray())
        });
    }

    private static async Task<JsonNode?> ThreadReadAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        var store = new ForumStore(ctx.Data);
        if (Long(o, "id") is not { } id || await store.ThreadAsync(id, ct) is not { } t) return Answer("failed", "Thema nicht gefunden.");
        var limit = Limit(o, "limit", 20, 50);
        var page = (int)Math.Max(1, Long(o, "page") ?? 1);
        var (posts, total) = await store.ThreadPostsAsync(t.Id, page, limit, ct);
        var data = ThreadJson(t, await store.BoardsAsync(ct));
        if (t.SourceUrl.Length > 0) data["sourceUrl"] = t.SourceUrl;
        data["postCount"] = total;
        data["page"] = page;
        data["pages"] = Math.Max(1, (total + limit - 1) / limit);
        data["posts"] = new JsonArray(posts.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id, ["author"] = p.AuthorName, ["created"] = ForumHtml.Iso(p.CreatedAt), ["edited"] = ForumHtml.Iso(p.EditedAt),
            ["first"] = p.Id == t.FirstPostId, ["body"] = p.Body.Length > MaxBodyOut ? p.Body[..MaxBodyOut] + "…" : p.Body, ["url"] = ForumUrls.Post(p.Id),
        }).ToArray());
        return Answer("applied", "Thema „" + t.Title + "“.", data);
    }

    private static JsonObject ThreadState(ForumThread t) => new() { ["id"] = t.Id, ["title"] = t.Title, ["board"] = t.BoardId, ["sticky"] = t.Sticky, ["closed"] = t.Closed };

    private static async Task<JsonNode?> ThreadUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o || Long(o, "id") is not { } id) return Answer("failed", "Bitte die id des Themas angeben.");
        var store = new ForumStore(ctx.Data);
        if (await store.ThreadAsync(id, ct) is not { } t) return Answer("failed", "Thema nicht gefunden.");
        var title = Str(o, "title")?.Trim();
        if (title is not null && title.Length is < ThreadTitleMin or > ThreadTitleMax) return Answer("failed", "Ein Thementitel hat 3 bis 120 Zeichen.");
        Board? target = null;
        if (o["board"] is not null && (target = await BoardOfAsync(store, o["board"], ct)) is null) return Answer("failed", "Das Zielforum gibt es nicht.");
        var sticky = Flag(o, "sticky");
        var closed = Flag(o, "closed");
        if (title is null && target is null && sticky is null && closed is null) return Answer("skipped", "Nichts zu ändern.");

        var done = new List<string>();
        if (title is not null && title != t.Title) { await store.RenameThreadAsync(t.Id, title, ct); done.Add("umbenannt"); }
        if (sticky is { } s && s != t.Sticky) { await store.SetThreadFlagAsync(t.Id, "sticky", s, ct); done.Add(s ? "angeheftet" : "nicht mehr angeheftet"); }
        if (closed is { } c && c != t.Closed) { await store.SetThreadFlagAsync(t.Id, "closed", c, ct); done.Add(c ? "geschlossen" : "geöffnet"); }
        if (target is not null && target.Id != t.BoardId) { await store.MoveThreadAsync(t, target.Id, ct); done.Add("verschoben nach „" + target.Title + "“"); }
        if (done.Count == 0) return Answer("skipped", "Das Thema ist schon so.");
        var now = (await store.ThreadAsync(t.Id, ct))!;
        return Answer("applied", "Thema „" + now.Title + "“: " + string.Join(", ", done) + ".", new JsonObject { ["id"] = now.Id, ["url"] = ForumUrls.Thread(now) },
            Undo(ThreadRestore, ThreadState(t)));
    }

    private static async Task<JsonNode?> ThreadRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o || Long(o, "id") is not { } id) return Answer("failed", "id fehlt.");
        var store = new ForumStore(ctx.Data);
        if (await store.ThreadAsync(id, ct) is not { } t) return Answer("skipped", "Das Thema gibt es nicht mehr.");
        if (Str(o, "title") is { Length: > 0 } title && title != t.Title) await store.RenameThreadAsync(t.Id, title, ct);
        if (Flag(o, "sticky") is { } s && s != t.Sticky) await store.SetThreadFlagAsync(t.Id, "sticky", s, ct);
        if (Flag(o, "closed") is { } c && c != t.Closed) await store.SetThreadFlagAsync(t.Id, "closed", c, ct);
        if (Long(o, "board") is { } board && board != t.BoardId && await store.BoardAsync(board, ct) is not null) await store.MoveThreadAsync(t, board, ct);
        return Answer("applied", "Thema wiederhergestellt.");
    }

    // ---- reports ------------------------------------------------------------------------------------------------------

    private static async Task<JsonNode?> ReportListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        var store = new ForumStore(ctx.Data);
        var open = Flag(o, "open") ?? true;
        var list = new JsonArray();
        foreach (var r in (await store.ReportsAsync(open, ct)).Take(Limit(o, "limit", 50, 300)))
        {
            var post = await store.PostAsync(r.PostId, ct);
            var thread = post is null ? null : await store.ThreadAsync(post.ThreadId, ct);
            list.Add(new JsonObject
            {
                ["id"] = r.Id, ["reason"] = r.Reason, ["reporter"] = r.MemberName, ["reporterId"] = r.MemberId, ["created"] = ForumHtml.Iso(r.CreatedAt), ["done"] = r.Done,
                ["post"] = post is null ? null : new JsonObject
                {
                    ["id"] = post.Id, ["author"] = post.AuthorName, ["text"] = ForumMarkup.Plain(post.Body, 300), ["thread"] = thread?.Title ?? "", ["threadId"] = post.ThreadId,
                    ["url"] = ForumUrls.Post(post.Id),
                },
            });
        }
        return Answer("applied", list.Count + (open ? " offene" : " erledigte") + " Meldungen.", new JsonObject { ["reports"] = list });
    }

    private static async Task<JsonNode?> ReportUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o || Long(o, "id") is not { } id) return Answer("failed", "Bitte die id der Meldung angeben.");
        if (Flag(o, "done") is not { } done) return Answer("failed", "Bitte done (true oder false) angeben.");
        var store = new ForumStore(ctx.Data);
        if (await store.ReportByIdAsync(id, ct) is not { } r) return Answer("failed", "Meldung nicht gefunden.");
        if (r.Done == done) return Answer("skipped", done ? "Die Meldung ist schon erledigt." : "Die Meldung ist schon offen.");
        await store.SetReportDoneAsync(id, done, ct);
        return Answer("applied", done ? "Meldung erledigt." : "Meldung wieder geöffnet.", new JsonObject { ["id"] = id },
            Undo(ReportRestore, new JsonObject { ["id"] = id, ["done"] = r.Done }));
    }

    private static async Task<JsonNode?> ReportRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o || Long(o, "id") is not { } id || Flag(o, "done") is not { } done) return Answer("failed", "id oder done fehlt.");
        var store = new ForumStore(ctx.Data);
        if (await store.ReportByIdAsync(id, ct) is null) return Answer("skipped", "Die Meldung gibt es nicht mehr.");
        await store.SetReportDoneAsync(id, done, ct);
        return Answer("applied", "Meldung zurückgesetzt.");
    }

    // ---- members ------------------------------------------------------------------------------------------------------

    private static async Task<JsonNode?> MemberListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        var store = new ForumStore(ctx.Data);
        var search = (Str(o, "search") ?? "").Trim();
        var limit = Limit(o, "limit", 50, 300);
        var modRole = ForumSettings.Moderators(ctx);
        var list = new JsonArray();
        foreach (var p in await store.ProfilesAsync(ct))
        {
            if (list.Count >= limit) break;
            // Named like the admin's list does: the account's name, else (account gone here) the name of its last post.
            var account = await ctx.Members.FindAsync(p.MemberId, ct);
            var name = account is null
                ? (await store.LatestPostsAsync(null, 1, p.MemberId, ct)).FirstOrDefault()?.AuthorName ?? ctx.T("forum.deletedMember")
                : account.DisplayName.Length > 0 ? account.DisplayName : account.Username;
            var email = account?.Email ?? "";
            if (search.Length > 0 && !(name.Contains(search, StringComparison.OrdinalIgnoreCase) || email.Contains(search, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new JsonObject
            {
                ["id"] = p.MemberId, ["name"] = name, ["email"] = email, ["account"] = account is not null, ["active"] = account?.IsActive ?? false,
                ["moderator"] = account?.Roles.Contains(modRole, StringComparer.OrdinalIgnoreCase) ?? false, ["banned"] = p.Banned, ["banReason"] = p.BanReason,
                ["posts"] = p.PostCount, ["lastPostAt"] = ForumHtml.Iso(p.LastPostAt), ["signature"] = p.Signature,
            });
        }
        return Answer("applied", list.Count + " Mitglieder.", new JsonObject { ["members"] = list });
    }

    private static JsonObject MemberState(long id, ForumProfile? p) =>
        new() { ["id"] = id, ["banned"] = p?.Banned ?? false, ["banReason"] = p?.BanReason ?? "", ["signature"] = p?.Signature ?? "" };

    private static async Task<JsonNode?> MemberUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o || Long(o, "id") is not { } id || id <= 0) return Answer("failed", "Bitte die Kontonummer (id) angeben.");
        var store = new ForumStore(ctx.Data);
        var profile = await store.ProfileAsync(id, ct);
        if (profile is null && await ctx.Members.FindAsync(id, ct) is null) return Answer("failed", "Mitglied nicht gefunden.");
        var banned = Flag(o, "banned");
        var reason = Str(o, "banReason")?.Trim();
        var clear = Flag(o, "clearSignature") == true;
        if (banned is null && reason is null && !clear) return Answer("skipped", "Nichts zu ändern.");

        // The same writes as the admin's buttons: a ban, an unban (which also clears the reason), the signature emptied.
        var values = new Dictionary<string, object?>();
        if (banned == true) { values["banned"] = true; if (reason is not null) values["ban_reason"] = Clip(reason, 300); }
        else if (banned == false) { values["banned"] = false; values["ban_reason"] = ""; }
        else if (reason is not null) values["ban_reason"] = Clip(reason, 300);
        if (clear) values["signature"] = "";
        await store.SaveProfileAsync(id, values, ct);
        var done = new List<string>();
        if (banned == true) done.Add("gesperrt");
        if (banned == false) done.Add("entsperrt");
        if (banned is null && reason is not null) done.Add("Sperrgrund gesetzt");
        if (clear) done.Add("Signatur entfernt");
        return Answer("applied", "Mitglied " + id.ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", done) + ".", new JsonObject { ["id"] = id },
            Undo(MemberRestore, MemberState(id, profile)));
    }

    private static async Task<JsonNode?> MemberRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o || Long(o, "id") is not { } id || id <= 0) return Answer("failed", "id fehlt.");
        await new ForumStore(ctx.Data).SaveProfileAsync(id, new Dictionary<string, object?>
        {
            ["banned"] = Flag(o, "banned") ?? false, ["ban_reason"] = Str(o, "banReason") ?? "", ["signature"] = Str(o, "signature") ?? "",
        }, ct);
        return Answer("applied", "Mitglied wiederhergestellt.");
    }
}
