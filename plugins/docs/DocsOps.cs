using System.Globalization;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Docs;

/// <summary>
/// The docs for other programs: operations (the cloud, MCP, the site's AI assistant) and the same functions as an API under <c>/api/v1/docs/…</c>
/// (a site API key with the right <c>docs:read</c> / <c>docs:write</c>). A page is named by its PATH of slugs (<c>installation/docker</c>) in a language
/// and version — what a writer of documentation has in mind, and stable where a row's id is not (a restore gives new ids). An upsert creates or
/// changes the page at that path; its parent must exist, so a whole documentation is written section by section, top down.
/// </summary>
internal static class DocsOps
{
    public static readonly PluginOperationSpec List = new("page.list",
        "Die Doku-Seiten einer Sprache und Version als Baum in Lesereihenfolge: id, Pfad, Titel, veröffentlicht, Tiefe, Kurztext, Adresse.",
        """{"locale":"de","version":""}""", Writes: false);
    public static readonly PluginOperationSpec Read = new("page.read",
        "Eine Doku-Seite mit ihrem Markdown-Text lesen, nach Pfad (und Sprache/Version) oder id.",
        """{"path":"installation/docker","locale":"de"}""", Writes: false);
    public static readonly PluginOperationSpec Upsert = new("page.upsert",
        "Legt die Doku-Seite unter einem Pfad (Slugs mit '/' verbunden) an oder ändert sie. Der Elternpfad muss existieren; ein Pfad aus einem Teil ist ein Bereich. " +
        "body ist Markdown (## Überschriften, ```Code```, Tabellen, > [!NOTE]-Kästen, [Text](doc:anderer/pfad) als Link zwischen Seiten). Optional: summary, icon (Tabler-Name), sort, published, " +
        "locale, version, translationOf (Pfad derselben Seite in der Standardsprache, verknüpft die Übersetzung). Nicht genannte Felder bleiben.",
        """{"path":"installation/docker","title":"Docker","summary":"Im Container starten.","body":"## Start\n\n```bash\ndocker compose up -d\n```","sort":20,"published":true,"locale":"de"}""");
    public static readonly PluginOperationSpec Delete = new("page.delete",
        "Löscht eine Doku-Seite nach Pfad oder id; ihre Unterseiten rücken eine Ebene auf (Rückgängig stellt sie wieder her).", """{"path":"installation/docker","locale":"de"}""");
    public static readonly PluginOperationSpec VersionList = new("version.list", "Die Versionen der Doku (leer, solange sie keine hat).", "{}", Writes: false);
    public static readonly PluginOperationSpec PageRestore = new("page.restore", "Stellt eine Doku-Seite wieder her, wie sie vor einer Änderung oder dem Löschen war (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec PageRemove = new("page.remove", "Entfernt eine eben angelegte Doku-Seite (Rückgängig).", Internal: true);

    public static void Register(IPluginBuilder b)
    {
        // The operations answer in the shape every caller of an operation reads ({status, message, data, undo}); the API below keeps its own.
        b.Operation(List, async (ctx, args, ct) => Op(await ListAsync(ctx, args as JsonObject, ct), d => $"{(d["pages"] as JsonArray)?.Count ?? 0} Seite(n)"));
        b.Operation(Read, async (ctx, args, ct) => Op(await ReadAsync(ctx, args as JsonObject, ct), d => d["title"]?.GetValue<string>() ?? ""));
        b.Operation(Upsert, (ctx, args, ct) => UpsertOpAsync(ctx, args as JsonObject ?? new JsonObject(), ct));
        b.Operation(Delete, (ctx, args, ct) => DeleteOpAsync(ctx, args as JsonObject, ct));
        b.Operation(VersionList, async (ctx, _, ct) => Op(await VersionsJsonAsync(ctx, ct), d => $"{(d["versions"] as JsonArray)?.Count ?? 0} Version(en)"));
        b.Operation(PageRestore, (ctx, args, ct) => RestoreOpAsync(ctx, args as JsonObject, ct));
        b.Operation(PageRemove, (ctx, args, ct) => RemoveOpAsync(ctx, args as JsonObject, ct));

        var read = new ApiRouteOptions { Access = ApiAccess.Read };
        var write = new ApiRouteOptions { Access = ApiAccess.Write };
        b.Api("GET", "pages", async (ctx, ct) => Json(await ListAsync(ctx, QueryArgs(ctx), ct)),
            read with { Summary = "Seiten einer Sprache/Version als Baum · pages of a language/version as a tree (?locale=de&version=)" });
        b.Api("GET", "pages/{id}", async (ctx, ct) => Json(await ReadAsync(ctx, new JsonObject { ["id"] = ctx.Request.R("id") }, ct)),
            read with { Summary = "Eine Seite mit Markdown · one page with its Markdown" });
        b.Api("PUT", "pages", PutAsync,
            write with
            {
                Summary = "Seite(n) anlegen oder ändern, nach Pfad · create or update page(s) by path; body: one page or {\"pages\":[…]} (≤ 500, top down)",
                Example = """{"pages":[{"path":"start","title":"Erste Schritte","body":"## …"},{"path":"start/installation","title":"Installation","body":"…"}]}"""
            });
        b.Api("DELETE", "pages/{id}", async (ctx, ct) => Json(await DeleteAsync(ctx, new JsonObject { ["id"] = ctx.Request.R("id") }, ct)),
            write with { Summary = "Seite löschen, Unterseiten rücken auf · delete a page, its sub-pages move up" });
        b.Api("GET", "versions", async (ctx, ct) => Json(await VersionsJsonAsync(ctx, ct)), read with { Summary = "Versionen · versions" });
    }

    private static PluginResult Json(JsonNode? node) =>
        node is JsonObject o && o["error"] is not null ? PluginResult.Json(o, o["status"]?.GetValue<int>() ?? 400) : PluginResult.Json(node);

    private static JsonObject QueryArgs(IPluginContext ctx) => new() { ["locale"] = ctx.Request.Q("locale"), ["version"] = ctx.Request.Q("version") };

    private static JsonObject Error(string text, int status = 400) => new() { ["error"] = text, ["status"] = status };

    private static string Str(JsonObject? a, string key) => a?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : a?[key] is JsonValue n ? n.ToJsonString().Trim('"') : "";

    /// <summary>The language and version an operation is about: the argument's when the site has it, else the defaults. Null + error when not.</summary>
    private static async Task<(string Locale, string Version, IReadOnlyList<DocVersion> Versions, JsonObject? Error)> ScopeAsync(IPluginContext ctx, JsonObject? a, CancellationToken ct)
    {
        var versions = await new DocsStore(ctx.Data).VersionsAsync(ct);
        var locale = Str(a, "locale").Trim().ToLowerInvariant();
        if (locale.Length == 0) locale = DocsUrls.Default(ctx);
        if (!DocsUrls.Publishes(ctx, locale)) return (locale, "", versions, Error($"The site does not publish in '{locale}'."));
        var version = Str(a, "version").Trim();
        if (version.Length == 0) version = DocsStore.DefaultVersion(versions);
        else if (!versions.Any(v => v.Name == version)) return (locale, version, versions, Error($"There is no version '{version}'."));
        return (locale, version, versions, null);
    }

    private static JsonObject Summary(IPluginContext ctx, DocNode n, string locale, string version, string defVersion) => new()
    {
        ["id"] = n.Page.Id, ["path"] = n.Path, ["title"] = n.Page.Title, ["depth"] = n.Depth, ["published"] = n.Page.Published, ["sort"] = n.Page.Sort,
        ["summary"] = n.Page.Summary, ["url"] = DocsUrls.Page(ctx, locale, version, defVersion, n.Path), ["updated"] = n.Page.UpdatedAt.ToString("o", CultureInfo.InvariantCulture)
    };

    public static async Task<JsonNode?> ListAsync(IPluginContext ctx, JsonObject? a, CancellationToken ct)
    {
        var (locale, version, versions, error) = await ScopeAsync(ctx, a, ct);
        if (error is not null) return error;
        var tree = await new DocsStore(ctx.Data).TreeAsync(locale, version, ct);
        var def = DocsStore.DefaultVersion(versions);
        return new JsonObject
        {
            ["locale"] = locale, ["version"] = version,
            ["pages"] = new JsonArray(tree.Flat.Select(n => (JsonNode)Summary(ctx, n, locale, version, def)).ToArray())
        };
    }

    /// <summary>The page an argument names: <c>id</c>, or <c>path</c> in its language and version.</summary>
    private static async Task<(DocNode? Node, string Locale, string Version, string Def, JsonObject? Error)> FindAsync(IPluginContext ctx, JsonObject? a, CancellationToken ct)
    {
        var store = new DocsStore(ctx.Data);
        if (long.TryParse(Str(a, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            var p = await store.GetAsync(id, ct);
            if (p is null) return (null, "", "", "", Error("No such page.", 404));
            var versions = await store.VersionsAsync(ct);
            var tree = await store.TreeAsync(p.Locale, p.Version, ct);
            return (tree.Find(id), p.Locale, p.Version, DocsStore.DefaultVersion(versions), null);
        }
        var (locale, version, vs, error) = await ScopeAsync(ctx, a, ct);
        if (error is not null) return (null, locale, version, "", error);
        var segs = Segments(Str(a, "path"));
        if (segs.Count == 0) return (null, locale, version, "", Error("A path or an id is needed."));
        var node = (await store.TreeAsync(locale, version, ct)).ByPath(segs);
        return node is null ? (null, locale, version, "", Error($"No page at '{string.Join("/", segs)}'.", 404)) : (node, locale, version, DocsStore.DefaultVersion(vs), null);
    }

    private static List<string> Segments(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => PluginText.Slugify(s)).ToList();

    public static async Task<JsonNode?> ReadAsync(IPluginContext ctx, JsonObject? a, CancellationToken ct)
    {
        var (node, locale, version, def, error) = await FindAsync(ctx, a, ct);
        if (error is not null || node is null) return error ?? Error("No such page.", 404);
        var o = Summary(ctx, node, locale, version, def);
        o["locale"] = locale;
        o["version"] = version;
        o["format"] = node.Page.Format;
        o["icon"] = node.Page.Icon;
        o["body"] = node.Page.Body;
        o["children"] = new JsonArray(node.Children.Select(c => (JsonNode)c.Path).ToArray());
        return o;
    }

    /// <summary>Creates or changes the page at a path. <paramref name="by"/> names the writer in "last changed by" (default: the API key).</summary>
    public static async Task<JsonObject> UpsertAsync(IPluginContext ctx, JsonObject a, CancellationToken ct, string? by = null)
    {
        var (locale, version, versions, error) = await ScopeAsync(ctx, a, ct);
        if (error is not null) return error;
        var segs = Segments(Str(a, "path"));
        if (segs.Count == 0 || segs.Any(s => s.Length == 0)) return Error("A path is needed (slugs joined by '/').");
        if (segs.Count > DocsStore.MaxDepth) return Error($"A page may sit at most {DocsStore.MaxDepth} levels deep.");
        if (DocsStore.ReservedSlugs.Contains(segs[0])) return Error($"'{segs[0]}' is an address of the docs themselves and cannot be a section.");
        var store = new DocsStore(ctx.Data);
        var tree = await store.TreeAsync(locale, version, ct);
        var parent = segs.Count == 1 ? null : tree.ByPath(segs.Take(segs.Count - 1).ToList());
        if (segs.Count > 1 && parent is null) return Error($"The parent '{string.Join("/", segs.Take(segs.Count - 1))}' does not exist yet — write it first.", 409);
        var existing = tree.ByPath(segs);

        var title = Str(a, "title").Trim();
        if (existing is null && title.Length == 0) return Error("A new page needs a title.");
        var format = a.ContainsKey("format") ? (Str(a, "format") == "html" ? "html" : "md") : existing?.Page.Format ?? "md";
        var body = a.ContainsKey("body") ? Str(a, "body") : existing?.Page.Body ?? "";
        if (format == "html") body = DocsUrls.Sanitize(ctx, body);
        var p = (existing?.Page ?? new DocPage { Locale = locale, Version = version, ParentId = parent?.Page.Id ?? 0, Slug = segs[^1] }) with
        {
            Format = format, Body = body, UpdatedBy = by ?? (ctx.Request.ApiCaller.Length > 0 ? "api:" + ctx.Request.ApiCaller : "api")
        };
        if (title.Length > 0) p = p with { Title = title.Length > 200 ? title[..200] : title };
        if (a.ContainsKey("summary")) p = p with { Summary = Str(a, "summary").Trim() };
        if (a.ContainsKey("icon")) p = p with { Icon = DocsHtml.Icon(Str(a, "icon")) };
        if (a["published"] is JsonValue pv && pv.TryGetValue<bool>(out var pub)) p = p with { Published = pub };
        if (long.TryParse(Str(a, "sort"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sort)) p = p with { Sort = sort };
        else if (existing is null) p = p with { Sort = await store.NextSortAsync(p.ParentId, locale, version, ct) };

        // The translation: the same page in the default language (same version), found by its path there.
        var of = Str(a, "translationOf").Trim();
        if (of.Length > 0 && p.GroupKey.Length == 0)
        {
            var def = DocsUrls.Default(ctx);
            var source = (await store.TreeAsync(def, version, ct)).ByPath(Segments(of));
            if (source is null) return Error($"No page '{of}' in '{def}' to link the translation to.", 409);
            if (source.Page.Locale == locale) return Error("A translation needs another language than its original.");
            var group = await store.EnsureGroupAsync(source.Page, ct);
            if ((await store.TranslationsAsync(group, ct)).Any(t => t.Locale == locale && t.Version == version && t.Id != p.Id))
                return Error($"'{of}' already has a translation in '{locale}'.", 409);
            p = p with { GroupKey = group };
        }
        var id = await store.SaveAsync(p, ct);
        var defVersion = DocsStore.DefaultVersion(versions);
        var path = string.Join("/", segs);
        return new JsonObject
        {
            ["ok"] = true, ["id"] = id, ["path"] = path, ["created"] = existing is null, ["url"] = DocsUrls.Page(ctx, locale, version, defVersion, path)
        };
    }

    private static async Task<PluginResult> PutAsync(IPluginContext ctx, CancellationToken ct)
    {
        var body = ctx.Request.Json();
        var list = body is JsonObject o && o["pages"] is JsonArray arr ? arr.OfType<JsonObject>().ToList() : body is JsonObject one ? new List<JsonObject> { one } : body is JsonArray bare ? bare.OfType<JsonObject>().ToList() : null;
        if (list is null || list.Count == 0) return PluginResult.Json(Error("Send one page or {\"pages\":[…]}."), 400);
        if (list.Count > 500) return PluginResult.Json(Error("At most 500 pages per call."), 400);
        if (list.Count == 1 && body is JsonObject single && single["pages"] is null) return Json(await UpsertAsync(ctx, single, ct));
        var results = new JsonArray();
        var failed = 0;
        foreach (var item in list)
        {
            var r = await UpsertAsync(ctx, item, ct);
            if (r["error"] is not null) { failed++; r["path"] = Str(item, "path"); }
            results.Add(r);
        }
        return PluginResult.Json(new JsonObject { ["ok"] = failed == 0, ["failed"] = failed, ["results"] = results });
    }

    public static async Task<JsonNode?> DeleteAsync(IPluginContext ctx, JsonObject? a, CancellationToken ct)
    {
        var (node, _, _, _, error) = await FindAsync(ctx, a, ct);
        if (error is not null || node is null) return error ?? Error("No such page.", 404);
        await new DocsStore(ctx.Data).DeleteAsync(node.Page, ct);
        return new JsonObject { ["ok"] = true, ["deleted"] = node.Path };
    }

    public static async Task<JsonNode?> VersionsJsonAsync(IPluginContext ctx, CancellationToken ct) => new JsonObject
    {
        ["versions"] = new JsonArray((await new DocsStore(ctx.Data).VersionsAsync(ct))
            .Select(v => (JsonNode)new JsonObject { ["name"] = v.Name, ["label"] = v.Label, ["default"] = v.IsDefault }).ToArray())
    };

    // ---- the operations' answers and their undo ------------------------------------------------------------------

    private static JsonObject Answer(string status, string message, JsonNode? data = null, JsonNode? undo = null)
    {
        var o = new JsonObject { ["status"] = status, ["message"] = message };
        if (data is not null) o["data"] = data;
        if (undo is not null) o["undo"] = undo;
        return o;
    }

    private static JsonObject Undo(string name, JsonNode args) => new() { ["name"] = name, ["args"] = args };

    /// <summary>An API answer as an operation's: an error (<c>{error, status: 404}</c>) is "failed" with its text, anything else "applied" with the answer as data.
    /// (The API's numeric <c>status</c> must never reach a caller that reads <c>status</c> as the outcome.)</summary>
    private static JsonObject Op(JsonNode? answer, Func<JsonObject, string> message)
    {
        if (answer is not JsonObject o) return Answer("failed", "Keine Antwort.");
        if (o["error"] is { } e) return Answer("failed", e.GetValue<string>());
        return Answer("applied", message(o), o.DeepClone());
    }

    /// <summary>A page row as the undo carries it — every stored field, so a restore writes back exactly what was there.</summary>
    private static JsonObject PageJson(DocPage p) => new()
    {
        ["id"] = p.Id, ["parentId"] = p.ParentId, ["slug"] = p.Slug, ["title"] = p.Title, ["locale"] = p.Locale, ["version"] = p.Version, ["format"] = p.Format,
        ["body"] = p.Body, ["summary"] = p.Summary, ["icon"] = p.Icon, ["sort"] = p.Sort, ["published"] = p.Published, ["groupKey"] = p.GroupKey,
        ["created"] = p.CreatedAt.ToString("o", CultureInfo.InvariantCulture), ["updatedBy"] = p.UpdatedBy
    };

    private static DocPage? PageFrom(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        long L(string k) => o[k] is JsonValue v && v.TryGetValue<long>(out var x) ? x : o[k] is JsonValue w && w.TryGetValue<int>(out var i) ? i : 0;
        string S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        return new DocPage
        {
            Id = L("id"), ParentId = L("parentId"), Slug = S("slug"), Title = S("title"), Locale = S("locale"), Version = S("version"), Format = S("format") == "html" ? "html" : "md",
            Body = S("body"), Summary = S("summary"), Icon = S("icon"), Sort = L("sort"), Published = o["published"] is JsonValue pv && pv.TryGetValue<bool>(out var pub) && pub,
            GroupKey = S("groupKey"), UpdatedBy = S("updatedBy"),
            CreatedAt = DateTime.TryParse(S("created"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var c) ? c : DateTime.MinValue
        };
    }

    /// <summary><c>page.upsert</c>: the page at the path as it is BEFORE the write is kept for the undo (a new page is removed again, a changed one written back).</summary>
    private static async Task<JsonNode?> UpsertOpAsync(IPluginContext ctx, JsonObject a, CancellationToken ct)
    {
        DocPage? before = null;
        var (locale, version, _, scopeError) = await ScopeAsync(ctx, a, ct);
        var segs = Segments(Str(a, "path"));
        if (scopeError is null && segs.Count > 0) before = (await new DocsStore(ctx.Data).TreeAsync(locale, version, ct)).ByPath(segs)?.Page;
        var r = await UpsertAsync(ctx, a, ct);
        if (r["error"] is { } e) return Answer("failed", e.GetValue<string>());
        var id = r["id"]!.GetValue<long>();
        var path = r["path"]?.GetValue<string>() ?? "";
        return before is null
            ? Answer("applied", $"Seite „{path}“ angelegt.", r.DeepClone(), Undo("page.remove", new JsonObject { ["id"] = id }))
            : Answer("applied", $"Seite „{path}“ geändert.", r.DeepClone(), Undo("page.restore", new JsonObject { ["page"] = PageJson(before) }));
    }

    /// <summary><c>page.delete</c>: the page and the sub-pages that move up are remembered, so the undo puts the page back and its sub-pages under it.</summary>
    private static async Task<JsonNode?> DeleteOpAsync(IPluginContext ctx, JsonObject? a, CancellationToken ct)
    {
        var (node, _, _, _, error) = await FindAsync(ctx, a, ct);
        if (error is not null || node is null) return Answer("failed", error?["error"]?.GetValue<string>() ?? "Keine solche Seite.");
        await new DocsStore(ctx.Data).DeleteAsync(node.Page, ct);
        return Answer("applied", $"Seite „{node.Path}“ gelöscht.", new JsonObject { ["deleted"] = node.Path },
            Undo("page.restore", new JsonObject
            {
                ["page"] = PageJson(node.Page), ["children"] = new JsonArray(node.Children.Select(c => (JsonNode)c.Page.Id).ToArray())
            }));
    }

    /// <summary>
    /// The undo of a change or a deletion. A page that still exists gets its stored fields back; a deleted one is written anew (a new id — the row's id
    /// was never what names a page) under its old parent where that is still there, with a free slug, and the sub-pages that moved up when it was deleted
    /// go back under it.
    /// </summary>
    private static async Task<JsonNode?> RestoreOpAsync(IPluginContext ctx, JsonObject? a, CancellationToken ct)
    {
        if (PageFrom(a?["page"]) is not { } p) return Answer("failed", "page fehlt.");
        var store = new DocsStore(ctx.Data);
        if (p.Id > 0 && await store.GetAsync(p.Id, ct) is not null)
        {
            await store.SaveAsync(p, ct);
            return Answer("applied", $"Seite „{p.Title}“ wiederhergestellt.");
        }
        var parent = p.ParentId > 0 && await store.GetAsync(p.ParentId, ct) is { } pp && pp.Locale == p.Locale && pp.Version == p.Version ? pp.Id : 0;
        var slug = await store.FreeSlugAsync(p.Slug, parent, p.Locale, p.Version, 0, ct);
        var newId = await store.SaveAsync(p with { Id = 0, ParentId = parent, Slug = slug }, ct);
        foreach (var child in (a?["children"] as JsonArray ?? new JsonArray()).Select(c => c is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0))
            if (await store.GetAsync(child, ct) is { } kid && kid.ParentId == p.ParentId && kid.Locale == p.Locale && kid.Version == p.Version)
                await store.SaveAsync(kid with { ParentId = newId }, ct);
        return Answer("applied", $"Seite „{p.Title}“ wiederhergestellt.", new JsonObject { ["id"] = newId });
    }

    private static async Task<JsonNode?> RemoveOpAsync(IPluginContext ctx, JsonObject? a, CancellationToken ct)
    {
        var store = new DocsStore(ctx.Data);
        if (!long.TryParse(Str(a, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || await store.GetAsync(id, ct) is not { } p)
            return Answer("applied", "Die Seite gibt es schon nicht mehr.");
        await store.DeleteAsync(p, ct);
        return Answer("applied", $"Seite „{p.Title}“ entfernt.");
    }
}
