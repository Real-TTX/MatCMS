namespace MatCMS.Plugin.Shop;

/// <summary>
/// The category tree. A category has at most one parent (<see cref="Category.ParentId"/>, 0 = top level); a product sits in ONE category, which may be any node. Everything that asks
/// "what is in this category" means the category AND everything below it. Pure functions over the list, tolerant on purpose: a parent that is gone makes a category top level, and a
/// loop (which the editor never writes, but a restore or the cloud might) is cut where it closes — a broken tree must never take the catalogue down.
/// </summary>
internal static class ShopCategories
{
    private static IOrderedEnumerable<Category> Ordered(IEnumerable<Category> cats) => cats.OrderBy(c => c.Sort).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    private static bool IsRoot(Category c, IReadOnlyDictionary<long, Category> byId) => c.ParentId == 0 || c.ParentId == c.Id || !byId.ContainsKey(c.ParentId);

    private static Dictionary<long, Category> ById(IEnumerable<Category> cats) => cats.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());

    /// <summary>The top-level categories, in order.</summary>
    public static IReadOnlyList<Category> Roots(IReadOnlyList<Category> cats)
    {
        var byId = ById(cats);
        return Ordered(cats.Where(c => IsRoot(c, byId))).ToList();
    }

    /// <summary>The direct children of a category, in order.</summary>
    public static IReadOnlyList<Category> Children(IReadOnlyList<Category> cats, long parentId)
    {
        var byId = ById(cats);
        return Ordered(cats.Where(c => c.ParentId == parentId && c.Id != parentId && !IsRoot(c, byId))).ToList();
    }

    /// <summary>Every category once, depth first (a parent, then its children), with its depth — the order a tree is shown in.</summary>
    public static List<(Category Cat, int Depth)> Flatten(IReadOnlyList<Category> cats)
    {
        var byId = ById(cats);
        var kids = cats.Where(c => !IsRoot(c, byId)).GroupBy(c => c.ParentId).ToDictionary(g => g.Key, g => Ordered(g).ToList());
        var result = new List<(Category, int)>();
        var seen = new HashSet<long>();
        void Walk(Category c, int depth)
        {
            if (!seen.Add(c.Id)) return;
            result.Add((c, depth));
            if (kids.TryGetValue(c.Id, out var k)) foreach (var x in k) Walk(x, depth + 1);
        }
        foreach (var r in Ordered(cats.Where(c => IsRoot(c, byId)))) Walk(r, 0);
        foreach (var c in Ordered(cats)) Walk(c, 0);          // what only a loop could hide
        return result;
    }

    /// <summary>The ids of a category and everything below it.</summary>
    public static HashSet<long> IdsUnder(IReadOnlyList<Category> cats, long id)
    {
        var result = new HashSet<long> { id };
        var queue = new Queue<long>();
        queue.Enqueue(id);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var c in cats.Where(c => c.ParentId == cur && c.Id != cur))
                if (result.Add(c.Id)) queue.Enqueue(c.Id);
        }
        return result;
    }

    /// <summary>The slugs of a category and everything below it — what a product filter or a coupon scope means. An unknown slug stands for itself (a product may still carry it).</summary>
    public static HashSet<string> SlugsUnder(IReadOnlyList<Category> cats, string slug)
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { slug };
        var self = cats.FirstOrDefault(c => c.Slug == slug);
        if (self is null) return set;
        var ids = IdsUnder(cats, self.Id);
        foreach (var c in cats.Where(c => ids.Contains(c.Id))) set.Add(c.Slug);
        return set;
    }

    /// <summary>From the top level down to the category itself; empty for an unknown slug.</summary>
    public static List<Category> Path(IReadOnlyList<Category> cats, string slug)
    {
        var byId = ById(cats);
        var path = new List<Category>();
        var cur = cats.FirstOrDefault(c => c.Slug == slug);
        var guard = 0;
        while (cur is not null && guard++ < 32)
        {
            path.Insert(0, cur);
            cur = cur.ParentId != 0 && cur.ParentId != cur.Id && byId.TryGetValue(cur.ParentId, out var up) && path.All(p => p.Id != up.Id) ? up : null;
        }
        return path;
    }

    /// <summary>"Kaffee › Bohnen" — the name with its ancestors, for a list where the tree is not drawn.</summary>
    public static string Label(IReadOnlyList<Category> cats, Category c) => string.Join(" › ", Path(cats, c.Slug).Select(p => p.Name));

    /// <summary>How many active products each category holds INCLUDING everything below it, from the per-category counts of the products themselves.</summary>
    public static Dictionary<string, int> RollUp(IReadOnlyList<Category> cats, IReadOnlyDictionary<string, int> own)
    {
        var total = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (slug, n) in own)
        {
            var path = Path(cats, slug);
            if (path.Count == 0) { total[slug] = total.GetValueOrDefault(slug) + n; continue; }
            foreach (var c in path) total[c.Slug] = total.GetValueOrDefault(c.Slug) + n;
        }
        return total;
    }
}
