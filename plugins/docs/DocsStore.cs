using System.Collections.Concurrent;
using System.Globalization;

namespace MatCMS.Plugin.Docs;

/// <summary>One page of the documentation. A page without a parent (<see cref="ParentId"/> 0) is a SECTION; any page may have sub-pages.
/// A page belongs to one language and one version — the same text in English is another row of the same <see cref="GroupKey"/>.</summary>
internal sealed record DocPage
{
    public long Id { get; init; }
    public long ParentId { get; init; }
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public string Locale { get; init; } = "";
    /// <summary>The version's name, "" while the site has no versions.</summary>
    public string Version { get; init; } = "";
    /// <summary><c>md</c> (Markdown) or <c>html</c> (rich text from the editor, cleaned by the host).</summary>
    public string Format { get; init; } = "md";
    public string Body { get; init; } = "";
    public string Summary { get; init; } = "";
    /// <summary>A Tabler icon name without the prefix (<c>rocket</c>), shown on the section cards.</summary>
    public string Icon { get; init; } = "";
    public long Sort { get; init; }
    public bool Published { get; init; } = true;
    /// <summary>Joins the translations of a page; "" until it has one.</summary>
    public string GroupKey { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string UpdatedBy { get; init; } = "";
}

/// <summary>A version of the documentation ("2.0"). Without any row the site has one, unnamed version.</summary>
internal sealed record DocVersion(long Id, string Name, string Label, long Sort, bool IsDefault)
{
    public string Shown => string.IsNullOrWhiteSpace(Label) ? Name : Label;
}

/// <summary>A page in the tree: its depth, its path of slugs and its children in order.</summary>
internal sealed class DocNode
{
    public required DocPage Page { get; init; }
    public DocNode? Parent { get; init; }
    public List<DocNode> Children { get; } = new();
    public int Depth { get; init; }
    /// <summary>The slugs from the section down to this page, joined by "/" (<c>installation/docker</c>).</summary>
    public string Path => Parent is null ? Page.Slug : Parent.Path + "/" + Page.Slug;
    public IEnumerable<DocNode> Ancestors() { for (var p = Parent; p is not null; p = p.Parent) yield return p; }
}

/// <summary>The pages of one language and version as a tree — what every public page is drawn from. Built once per change and kept in memory.</summary>
internal sealed class DocTree
{
    public required IReadOnlyList<DocNode> Roots { get; init; }
    /// <summary>Every page in reading order (depth first) — the order of "previous / next".</summary>
    public required IReadOnlyList<DocNode> Flat { get; init; }
    public DocNode? Find(long id) => Flat.FirstOrDefault(n => n.Page.Id == id);

    /// <summary>The page at a path of slugs, or null.</summary>
    public DocNode? ByPath(IReadOnlyList<string> slugs)
    {
        DocNode? cur = null;
        IReadOnlyList<DocNode> level = Roots;
        foreach (var s in slugs)
        {
            cur = level.FirstOrDefault(n => string.Equals(n.Page.Slug, s, StringComparison.OrdinalIgnoreCase));
            if (cur is null) return null;
            level = cur.Children;
        }
        return cur;
    }

    /// <summary>Builds the tree. Tolerant: a page whose parent is gone (or that would make a loop) is shown at the top level instead of being lost.</summary>
    public static DocTree Build(IEnumerable<DocPage> pages)
    {
        var list = pages.ToList();
        var ids = list.Select(p => p.Id).ToHashSet();
        var byParent = list.GroupBy(p => p.ParentId != p.Id && ids.Contains(p.ParentId) ? p.ParentId : 0)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Sort).ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList());
        var flat = new List<DocNode>();
        var seen = new HashSet<long>();
        List<DocNode> Level(long parent, DocNode? parentNode, int depth)
        {
            var nodes = new List<DocNode>();
            if (!byParent.TryGetValue(parent, out var kids)) return nodes;
            foreach (var p in kids)
            {
                if (!seen.Add(p.Id)) continue;
                var n = new DocNode { Page = p, Parent = parentNode, Depth = depth };
                flat.Add(n);
                n.Children.AddRange(Level(p.Id, n, depth + 1));
                nodes.Add(n);
            }
            return nodes;
        }
        var roots = Level(0, null, 0);
        // A loop (a → b → a) is never reached from the top: such pages become sections of their own.
        foreach (var p in list.Where(p => !seen.Contains(p.Id)).OrderBy(p => p.Sort))
        {
            seen.Add(p.Id);
            var n = new DocNode { Page = p, Depth = 0 };
            flat.Add(n);
            roots.Add(n);
        }
        return new DocTree { Roots = roots, Flat = flat };
    }
}

/// <summary>The plugin's tables and every read and write of them.</summary>
internal sealed class DocsStore
{
    private readonly IPluginData _data;
    public DocsStore(IPluginData data) => _data = data;

    public IDataTable Pages => _data.Table("pages");
    public IDataTable Versions => _data.Table("versions");
    private IDataTable Meta => _data.Table("meta");

    /// <summary>Slugs a page directly under the docs' root cannot have: they are addresses of the docs themselves.</summary>
    public static readonly string[] ReservedSlugs = ["v", "suche", "search"];

    /// <summary>The deepest a page may sit (a section is level 1).</summary>
    public const int MaxDepth = 6;

    public static void Declare(IDataBuilder d)
    {
        d.Table("pages", t => t
            .Int("parent_id", required: true, def: "0")
            .Text("slug", required: true, def: "")
            .Text("title", required: true, def: "")
            .Text("locale", required: true, def: "")
            .Text("version", required: true, def: "")
            .Text("format", required: true, def: "md")
            .Text("body")
            .Text("summary")
            .Text("icon")
            .Int("sort", required: true, def: "0")
            .Bool("published", required: true, def: "1")
            .Text("group_key")
            .Timestamp("created")
            .Timestamp("updated")
            .Text("updated_by")
            .Index("locale", "version", "parent_id")
            .Index("group_key"));
        d.Table("versions", t => t
            .Text("name", required: true, unique: true)
            .Text("label")
            .Int("sort", required: true, def: "0")
            .Bool("is_default", required: true, def: "0"));
        // A counter that goes up with every change of the docs — the tree kept in memory is rebuilt when it moved.
        d.Table("meta", t => t
            .Text("name", required: true, unique: true)
            .Int("value", required: true, def: "0"));
    }

    // ---- reading -------------------------------------------------------------------------------------------------

    public static DocPage From(DataRow r) => new()
    {
        Id = r.Id, ParentId = r.Int("parent_id"), Slug = r.Text("slug"), Title = r.Text("title"), Locale = r.Text("locale"), Version = r.Text("version"),
        Format = r.Text("format") == "html" ? "html" : "md", Body = r.Text("body"), Summary = r.Text("summary"), Icon = r.Text("icon"), Sort = r.Int("sort"),
        Published = r.Bool("published"), GroupKey = r.Text("group_key"), CreatedAt = r.Timestamp("created") ?? DateTime.MinValue,
        UpdatedAt = r.Timestamp("updated") ?? DateTime.MinValue, UpdatedBy = r.Text("updated_by")
    };

    private static Dictionary<string, object?> Values(DocPage p) => new()
    {
        ["parent_id"] = p.ParentId, ["slug"] = p.Slug, ["title"] = p.Title, ["locale"] = p.Locale, ["version"] = p.Version, ["format"] = p.Format,
        ["body"] = p.Body, ["summary"] = p.Summary, ["icon"] = p.Icon, ["sort"] = p.Sort, ["published"] = p.Published, ["group_key"] = p.GroupKey,
        ["created"] = p.CreatedAt == DateTime.MinValue ? DateTime.UtcNow : p.CreatedAt, ["updated"] = p.UpdatedAt == DateTime.MinValue ? DateTime.UtcNow : p.UpdatedAt,
        ["updated_by"] = p.UpdatedBy
    };

    public async Task<DocPage?> GetAsync(long id, CancellationToken ct = default) =>
        id <= 0 ? null : await Pages.GetAsync(id, ct) is { } r ? From(r) : null;

    public async Task<IReadOnlyList<DocPage>> PagesOfAsync(string locale, string version, CancellationToken ct = default) =>
        (await Pages.QueryAsync(DataQuery.Everything.Eq("locale", locale).Eq("version", version), ct)).Select(From).ToList();

    public async Task<IReadOnlyList<DocPage>> AllPagesAsync(CancellationToken ct = default) =>
        (await Pages.QueryAsync(DataQuery.Everything, ct)).Select(From).ToList();

    public async Task<IReadOnlyList<DocPage>> TranslationsAsync(string group, CancellationToken ct = default) =>
        string.IsNullOrEmpty(group) ? Array.Empty<DocPage>() : (await Pages.QueryAsync(DataQuery.Everything.Eq("group_key", group), ct)).Select(From).ToList();

    public async Task<IReadOnlyList<DocVersion>> VersionsAsync(CancellationToken ct = default) =>
        (await Versions.QueryAsync(DataQuery.Everything, ct))
            .Select(r => new DocVersion(r.Id, r.Text("name"), r.Text("label"), r.Int("sort"), r.Bool("is_default")))
            .OrderBy(v => v.Sort).ThenByDescending(v => v.Name, StringComparer.Ordinal).ToList();

    /// <summary>The version a visitor gets without asking for one: the one marked default, else the first; "" while there are none.</summary>
    public static string DefaultVersion(IReadOnlyList<DocVersion> versions) =>
        versions.FirstOrDefault(v => v.IsDefault)?.Name ?? (versions.Count > 0 ? versions[0].Name : "");

    // ---- the tree kept in memory -----------------------------------------------------------------------------------

    // Per (locale, version): the tree and the counter it was built at. The plugin's own static — it goes with the plugin's load context when unloaded.
    private static readonly ConcurrentDictionary<string, (long Rev, DocTree Tree)> Trees = new();

    public async Task<long> RevisionAsync(CancellationToken ct = default) => (await Meta.FindByAsync("name", "rev", ct))?.Int("value") ?? 0;

    /// <summary>Marks the docs as changed (after every write).</summary>
    public async Task TouchAsync(CancellationToken ct = default) => await Meta.NextNumberAsync("rev", ct: ct);

    /// <summary>The tree of a language and version: every page, published or not (a visitor's view leaves the drafts out, an admin sees them).</summary>
    public async Task<DocTree> TreeAsync(string locale, string version, CancellationToken ct = default)
    {
        var rev = await RevisionAsync(ct);
        var key = locale + "\n" + version;
        if (Trees.TryGetValue(key, out var hit) && hit.Rev == rev && rev > 0) return hit.Tree;
        var tree = DocTree.Build(await PagesOfAsync(locale, version, ct));
        Trees[key] = (rev, tree);
        return tree;
    }

    /// <summary>The same tree without the pages a visitor may not see: a draft and everything below it.</summary>
    public static DocTree Visible(DocTree tree) =>
        tree.Flat.All(n => n.Page.Published) ? tree : DocTree.Build(tree.Flat.Where(n => n.Page.Published && n.Ancestors().All(a => a.Page.Published)).Select(n => n.Page));

    // ---- writing -------------------------------------------------------------------------------------------------

    /// <summary>A slug that is free among the page's siblings (<c>docker</c>, <c>docker-2</c>, …), never one of <see cref="ReservedSlugs"/> at the top.</summary>
    public async Task<string> FreeSlugAsync(string wanted, long parentId, string locale, string version, long selfId, CancellationToken ct = default)
    {
        var slug = PluginText.Slugify(wanted);
        if (slug.Length == 0) slug = "seite";
        if (slug.Length > 80) slug = slug[..80].Trim('-');
        var siblings = (await Pages.QueryAsync(DataQuery.Everything.Eq("parent_id", parentId).Eq("locale", locale).Eq("version", version), ct))
            .Where(r => r.Id != selfId).Select(r => r.Text("slug")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (parentId == 0) foreach (var r in ReservedSlugs) siblings.Add(r);
        var candidate = slug;
        for (var i = 2; siblings.Contains(candidate); i++) candidate = slug + "-" + i.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    public async Task<long> SaveAsync(DocPage p, CancellationToken ct = default)
    {
        var values = Values(p with { UpdatedAt = DateTime.UtcNow });
        long id;
        if (p.Id == 0) id = await Pages.InsertAsync(values, ct);
        else { values.Remove("created"); await Pages.UpdateAsync(p.Id, values, ct); id = p.Id; }
        await TouchAsync(ct);
        return id;
    }

    /// <summary>Deletes a page; its sub-pages move up to its parent (nothing written below a page is lost with it).</summary>
    public async Task DeleteAsync(DocPage p, CancellationToken ct = default)
    {
        await Pages.UpdateWhereAsync(DataQuery.Everything.Eq("parent_id", p.Id), new Dictionary<string, object?> { ["parent_id"] = p.ParentId }, ct);
        await Pages.DeleteAsync(p.Id, ct);
        await TouchAsync(ct);
    }

    /// <summary>Moves a page one place up or down among its siblings (renumbers them 10, 20, … so the order is stable).</summary>
    public async Task MoveAsync(DocPage p, int direction, CancellationToken ct = default)
    {
        var tree = DocTree.Build(await PagesOfAsync(p.Locale, p.Version, ct));
        var node = tree.Find(p.Id);
        if (node is null) return;
        var siblings = (node.Parent?.Children ?? tree.Roots.ToList()).Select(n => n.Page).ToList();
        var i = siblings.FindIndex(s => s.Id == p.Id);
        var j = i + Math.Sign(direction);
        if (i < 0 || j < 0 || j >= siblings.Count) return;
        (siblings[i], siblings[j]) = (siblings[j], siblings[i]);
        for (var k = 0; k < siblings.Count; k++)
            await Pages.UpdateAsync(siblings[k].Id, new Dictionary<string, object?> { ["sort"] = (long)(k + 1) * 10 }, ct);
        await TouchAsync(ct);
    }

    /// <summary>The next sort number at the end of a level.</summary>
    public async Task<long> NextSortAsync(long parentId, string locale, string version, CancellationToken ct = default)
    {
        var rows = await Pages.QueryAsync(DataQuery.Everything.Eq("parent_id", parentId).Eq("locale", locale).Eq("version", version), ct);
        return rows.Count == 0 ? 10 : rows.Max(r => r.Int("sort")) + 10;
    }

    /// <summary>A translation group for the page, created (and written onto it) the first time it gets a translation.</summary>
    public async Task<string> EnsureGroupAsync(DocPage p, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(p.GroupKey)) return p.GroupKey;
        var group = Guid.NewGuid().ToString("N")[..12];
        await Pages.UpdateAsync(p.Id, new Dictionary<string, object?> { ["group_key"] = group }, ct);
        await TouchAsync(ct);
        return group;
    }

    // ---- versions ------------------------------------------------------------------------------------------------

    /// <summary>The form of a version's name: letters, digits, ".", "-" and "_" (<c>2.0</c>, <c>v1</c>, <c>next</c>), at most 30.</summary>
    public static string CleanVersionName(string? name)
    {
        var s = new string((name ?? "").Trim().ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray()).Trim('.', '-', '_');
        return s.Length > 30 ? s[..30] : s;
    }

    /// <summary>
    /// Adds a version. The FIRST version takes over every page written so far (they had no version), so turning versions on loses nothing; a later one
    /// starts empty or as a copy of another version (with the tree, the order and the translations kept apart from the original's).
    /// </summary>
    public async Task<string?> AddVersionAsync(string name, string label, string copyFrom, bool makeDefault, CancellationToken ct = default)
    {
        var existing = await VersionsAsync(ct);
        if (existing.Any(v => v.Name == name)) return "exists";
        var first = existing.Count == 0;
        await Versions.InsertAsync(new Dictionary<string, object?>
        {
            ["name"] = name, ["label"] = label, ["sort"] = existing.Count == 0 ? 10L : existing.Max(v => v.Sort) + 10, ["is_default"] = first || makeDefault
        }, ct);
        if (first) await Pages.UpdateWhereAsync(DataQuery.Everything.Eq("version", ""), new Dictionary<string, object?> { ["version"] = name }, ct);
        else if (existing.Any(v => v.Name == copyFrom)) await CopyVersionAsync(copyFrom, name, ct);
        if (makeDefault && !first) await SetDefaultAsync(name, ct);
        await TouchAsync(ct);
        return null;
    }

    private async Task CopyVersionAsync(string from, string to, CancellationToken ct)
    {
        var pages = (await Pages.QueryAsync(DataQuery.Everything.Eq("version", from), ct)).Select(From).ToList();
        var newIds = new Dictionary<long, long>();
        var groups = new Dictionary<string, string>();
        // Parents first, so every child finds its parent's new id.
        foreach (var locale in pages.Select(p => p.Locale).Distinct())
            foreach (var n in DocTree.Build(pages.Where(p => p.Locale == locale)).Flat)
            {
                var p = n.Page;
                var group = p.GroupKey.Length == 0 ? "" : groups.TryGetValue(p.GroupKey, out var g) ? g : groups[p.GroupKey] = Guid.NewGuid().ToString("N")[..12];
                newIds[p.Id] = await Pages.InsertAsync(Values(p with
                {
                    Id = 0, Version = to, ParentId = p.ParentId != 0 && newIds.TryGetValue(p.ParentId, out var np) ? np : 0, GroupKey = group,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                }), ct);
            }
    }

    public async Task SetDefaultAsync(string name, CancellationToken ct = default)
    {
        foreach (var v in await VersionsAsync(ct))
            await Versions.UpdateAsync(v.Id, new Dictionary<string, object?> { ["is_default"] = v.Name == name }, ct);
        await TouchAsync(ct);
    }

    /// <summary>Deletes a version with its pages. The last version cannot go (a site with versions always has one); the default moves to another.</summary>
    public async Task<bool> DeleteVersionAsync(string name, CancellationToken ct = default)
    {
        var all = await VersionsAsync(ct);
        var v = all.FirstOrDefault(x => x.Name == name);
        if (v is null || all.Count < 2) return false;
        await Pages.DeleteWhereAsync(DataQuery.Everything.Eq("version", name), ct);
        await Versions.DeleteAsync(v.Id, ct);
        if (v.IsDefault) await SetDefaultAsync(all.First(x => x.Name != name).Name, ct);
        await TouchAsync(ct);
        return true;
    }
}
