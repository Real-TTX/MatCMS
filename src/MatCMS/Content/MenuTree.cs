using MatCMS.Models;

namespace MatCMS.Content;

/// <summary>A menu item plus its (recursively nested) child items — the shape used to render
/// hierarchical menus (dropdowns/submenus).</summary>
public sealed record MenuNode(MenuItem Item, IReadOnlyList<MenuNode> Children)
{
    public bool HasChildren => Children.Count > 0;
}

/// <summary>Builds a nested <see cref="MenuNode"/> tree from a flat item list (by <c>ParentId</c>).
/// Items whose parent isn't in the set are treated as top-level, so a menu never loses entries.
/// Ordering follows SortOrder then Id at every level.</summary>
public static class MenuTree
{
    public static IReadOnlyList<MenuNode> Build(IEnumerable<MenuItem> flat)
    {
        var items = flat.ToList();
        var ids = items.Select(i => i.Id).ToHashSet();

        List<MenuNode> Children(int? parentId) =>
            items.Where(i => (i.ParentId is int p && ids.Contains(p) ? i.ParentId : null) == parentId)
                 .OrderBy(i => i.SortOrder).ThenBy(i => i.Id)
                 .Select(i => new MenuNode(i, Children(i.Id)))
                 .ToList();

        return Children(null);
    }

    /// <summary>The most levels a menu may have (a top entry, its dropdown, a flyout beside that, and one more). The renderer and the stylesheet handle any depth; four is what a visitor can still use.</summary>
    public const int MaxLevels = 4;

    /// <summary>
    /// The items a menu item may be put under, in tree order with their depth: any item of the menu, except the item being edited and what hangs below it (a loop), and only
    /// where the item — together with ITS OWN subtree — still fits into <see cref="MaxLevels"/>.
    /// </summary>
    public static List<(MenuItem Item, int Depth)> ParentChoices(IEnumerable<MenuItem> items, int? movingId = null)
    {
        var tree = Build(items);
        static int Height(MenuNode n) => 1 + (n.Children.Count == 0 ? 0 : n.Children.Max(Height));
        static MenuNode? Find(IReadOnlyList<MenuNode> nodes, int id)
        {
            foreach (var n in nodes) { if (n.Item.Id == id) return n; if (Find(n.Children, id) is { } f) return f; }
            return null;
        }
        var height = movingId is int m && Find(tree, m) is { } moving ? Height(moving) : 1;
        var result = new List<(MenuItem, int)>();
        void Walk(IReadOnlyList<MenuNode> nodes, int depth)
        {
            foreach (var n in nodes)
            {
                if (movingId == n.Item.Id) continue;
                if (depth + height + 1 <= MaxLevels) result.Add((n.Item, depth));
                Walk(n.Children, depth + 1);
            }
        }
        Walk(tree, 0);
        return result;
    }
}
