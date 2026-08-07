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
}
