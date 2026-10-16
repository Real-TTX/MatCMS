namespace MatCMS.Plugin.Shop;

/// <summary>
/// The shop's public addresses in ONE place, so a link to a category or a product is never assembled by hand somewhere else. A category lives at
/// <c>/shop/categories/{slug}</c>, a product at <c>/shop/{slug}</c>; what narrows a list further (keyword, search, sort, page) stays a query.
/// <c>/shop?cat=…</c> is the address the shop used to have — it still works, as a permanent redirect to the new one.
/// </summary>
internal static class ShopUrls
{
    public const string Root = "/shop";
    public const string CategoriesPage = "/shop/categories";
    public const string TagsPage = "/shop/tags";

    public static string Category(string slug) => "/shop/categories/" + Uri.EscapeDataString(slug);

    public static string Product(string slug) => "/shop/" + Uri.EscapeDataString(slug);

    /// <summary>An address of the catalogue with the filters that are set (and only those): the category is the path, the rest the query.</summary>
    public static string Catalog(string cat, string tag = "", string q = "", string sort = "", int page = 1, IEnumerable<KeyValuePair<string, string>>? extra = null)
    {
        var parts = new List<string>();
        foreach (var kv in extra ?? Array.Empty<KeyValuePair<string, string>>())
            parts.Add(Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value));
        if (tag.Length > 0) parts.Add("tag=" + Uri.EscapeDataString(tag));
        if (q.Length > 0) parts.Add("q=" + Uri.EscapeDataString(q));
        if (sort.Length > 0) parts.Add("sort=" + Uri.EscapeDataString(sort));
        if (page > 1) parts.Add("page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var path = cat.Length > 0 ? Category(cat) : Root;
        return parts.Count == 0 ? path : path + "?" + string.Join("&", parts);
    }
}
