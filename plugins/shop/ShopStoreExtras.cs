using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>The words of a product or a category in one language other than the main one. An empty field means "use the main language's".</summary>
internal sealed record Translation(string Title = "", string Summary = "", string Description = "", string SeoTitle = "", string SeoDescription = "")
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Summary) && string.IsNullOrWhiteSpace(Description) && string.IsNullOrWhiteSpace(SeoTitle) && string.IsNullOrWhiteSpace(SeoDescription);
}

/// <summary>Which categories a product stands in: its own, the further ones it was put into, and the automatic collections whose rule it meets — each also counted for every category above.</summary>
internal static class ShopMembership
{
    public static List<string> SplitSlugs(string? text) =>
        (text ?? "").Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The categories of <paramref name="cats"/> whose own rule is not empty and that lie in <paramref name="slugs"/>.</summary>
    public static List<Category> RuleCategories(IReadOnlyList<Category> cats, IReadOnlySet<string> slugs) => cats.Where(c => slugs.Contains(c.Slug) && ShopCollectionRule.Any(c.Rule)).ToList();

    /// <summary>True when the product belongs to a category of <paramref name="slugs"/> (a category and everything below it): by its main category, by a further one, or by a rule.</summary>
    public static bool In(Product p, IReadOnlySet<string> slugs, IReadOnlyList<Category> ruleCats, DateTime nowUtc) =>
        (p.Category.Length > 0 && slugs.Contains(p.Category))
        || (p.ExtraCategories.Length > 0 && SplitSlugs(p.ExtraCategories).Any(slugs.Contains))
        || ruleCats.Any(c => ShopCollectionRule.Matches(c.Rule, p, nowUtc));

    /// <summary>How many of <paramref name="products"/> each category holds — below it, further and by rule included (a product counts once per category).</summary>
    public static Dictionary<string, int> Counts(IReadOnlyList<Category> cats, IReadOnlyList<Product> products, DateTime nowUtc)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in cats)
        {
            var slugs = ShopCategories.SlugsUnder(cats, c.Slug).ToHashSet(StringComparer.Ordinal);
            var rules = RuleCategories(cats, slugs);
            var n = products.Count(p => In(p, slugs, rules, nowUtc));
            if (n > 0) counts[c.Slug] = n;
        }
        return counts;
    }
}

internal sealed partial class ShopStore
{
    // ---- the visitor's language -------------------------------------------------------------------------

    private readonly string? _locale;

    /// <summary>A store for what a visitor sees: products and categories come with their words in the visitor's language where a translation exists.</summary>
    public ShopStore(IPluginData data, string? locale) { _data = data; _locale = string.IsNullOrWhiteSpace(locale) ? null : locale; }

    /// <summary>The store for a request's public pages (language of the request).</summary>
    public static ShopStore Public(IPluginContext ctx) => new(ctx.Data, ctx.Locale);

    private Dictionary<(string Kind, long Id), Translation>? _translations;

    private async Task<Dictionary<(string Kind, long Id), Translation>> TranslationsAsync(CancellationToken ct)
    {
        if (_translations is not null) return _translations;
        _translations = new();
        if (_locale is null) return _translations;
        foreach (var r in await T("translations").QueryAsync(DataQuery.Everything.Eq("locale", _locale), ct))
            _translations[(r.Text("kind"), r.Int("ref_id"))] = TranslationOf(r);
        return _translations;
    }

    private static Translation TranslationOf(DataRow r) => new(r.Text("title"), r.Text("summary"), r.Text("description"), r.Text("seo_title"), r.Text("seo_description"));

    private static string Pick(string own, string main) => string.IsNullOrWhiteSpace(own) ? main : own;

    private async Task<IReadOnlyList<Product>> LocalizeAsync(IReadOnlyList<Product> list, CancellationToken ct)
    {
        if (_locale is null || list.Count == 0) return list;
        var tr = await TranslationsAsync(ct);
        if (tr.Count == 0) return list;
        return list.Select(p => tr.TryGetValue(("product", p.Id), out var t)
            ? p with { Title = Pick(t.Title, p.Title), Summary = Pick(t.Summary, p.Summary), DescriptionHtml = Pick(t.Description, p.DescriptionHtml), SeoTitle = Pick(t.SeoTitle, p.SeoTitle), SeoDescription = Pick(t.SeoDescription, p.SeoDescription) }
            : p).ToList();
    }

    private async Task<Product?> LocalizeAsync(Product? p, CancellationToken ct) => p is null ? null : (await LocalizeAsync(new[] { p }, ct))[0];

    private async Task<IReadOnlyList<Category>> LocalizeAsync(IReadOnlyList<Category> list, CancellationToken ct)
    {
        if (_locale is null || list.Count == 0) return list;
        var tr = await TranslationsAsync(ct);
        if (tr.Count == 0) return list;
        return list.Select(c => tr.TryGetValue(("category", c.Id), out var t) ? c with { Name = Pick(t.Title, c.Name), Description = Pick(t.Description, c.Description) } : c).ToList();
    }

    private async Task<Category?> LocalizeAsync(Category? c, CancellationToken ct) => c is null ? null : (await LocalizeAsync(new[] { c }, ct))[0];

    // ---- translations (back office) ---------------------------------------------------------------------

    /// <summary>The translations of one product or category, by language.</summary>
    public async Task<Dictionary<string, Translation>> TranslationsOfAsync(string kind, long refId, CancellationToken ct = default) =>
        (await T("translations").QueryAsync(DataQuery.Everything.Eq("kind", kind).Eq("ref_id", refId), ct)).ToDictionary(r => r.Text("locale"), TranslationOf, StringComparer.OrdinalIgnoreCase);

    /// <summary>Saves the words of one language; a translation with nothing in it is removed.</summary>
    public async Task SaveTranslationAsync(string kind, long refId, string locale, Translation t, CancellationToken ct = default)
    {
        var existing = await T("translations").FindAsync(DataQuery.Everything.Eq("kind", kind).Eq("ref_id", refId).Eq("locale", locale), ct);
        if (t.IsEmpty) { if (existing is not null) await T("translations").DeleteAsync(existing.Id, ct); return; }
        var values = new Dictionary<string, object?>
        {
            ["kind"] = kind, ["ref_id"] = refId, ["locale"] = locale, ["title"] = t.Title.Trim(), ["summary"] = t.Summary.Trim(), ["description"] = t.Description, ["seo_title"] = t.SeoTitle.Trim(), ["seo_description"] = t.SeoDescription.Trim()
        };
        if (existing is null) await T("translations").InsertAsync(values, ct);
        else await T("translations").UpdateAsync(existing.Id, values, ct);
    }

    // ---- addresses that moved ---------------------------------------------------------------------------

    /// <summary>Remembers that <paramref name="oldSlug"/> used to be this product's address (and forgets a redirect that stands in the way of a product now using that address).</summary>
    public async Task RememberSlugAsync(string oldSlug, long productId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(oldSlug) || productId <= 0) return;
        var row = await T("slug_redirects").FindByAsync("old_slug", oldSlug, ct);
        if (row is null) await T("slug_redirects").InsertAsync(new Dictionary<string, object?> { ["old_slug"] = oldSlug, ["product_id"] = productId }, ct);
        else await T("slug_redirects").UpdateAsync(row.Id, new Dictionary<string, object?> { ["product_id"] = productId }, ct);
    }

    /// <summary>A product that now uses <paramref name="slug"/> takes the address back: a redirect from it away is deleted.</summary>
    public async Task ForgetRedirectAsync(string slug, CancellationToken ct = default)
    {
        if (await T("slug_redirects").FindByAsync("old_slug", slug, ct) is { } r) await T("slug_redirects").DeleteAsync(r.Id, ct);
    }

    /// <summary>The product an old address belonged to (its page is at the product's present address), or null.</summary>
    public async Task<Product?> ProductByOldSlugAsync(string slug, CancellationToken ct = default) =>
        await T("slug_redirects").FindByAsync("old_slug", slug, ct) is { } r ? await ProductAsync(r.Int("product_id"), ct) : null;

    // ---- copying a product ------------------------------------------------------------------------------

    /// <summary>
    /// Copies a product into a new one that is switched OFF (so a half-finished copy never reaches the shop): title with " (Kopie)", a free address, its variants, its attribute values, its
    /// translations. Digital files are not copied (they belong to the storage of the original); the caller says so. Returns the new id, or 0 when there is no such product.
    /// </summary>
    public async Task<long> DuplicateProductAsync(long id, string copyWord, CancellationToken ct = default)
    {
        var p = await ProductAsync(id, ct);
        if (p is null) return 0;
        var baseSlug = (p.Slug.Length > 0 ? p.Slug : "produkt") + "-kopie"; var slug = baseSlug; var n = 2;
        while (await SlugTakenAsync(slug, 0, ct)) slug = baseSlug + "-" + n++;
        var newId = await SaveProductAsync(p with { Id = 0, Slug = slug, Title = p.Title + " (" + copyWord + ")", Active = false, Featured = false, Sku = "" }, ct);
        foreach (var v in await VariantsAsync(id, true, ct)) await SaveVariantAsync(v with { Id = 0, ProductId = newId, Sku = "" }, ct);
        foreach (var (attr, values) in await ProductValuesAsync(id, ct)) await SetProductAttrAsync(newId, attr, values, ct);
        foreach (var (locale, t) in await TranslationsOfAsync("product", id, ct)) await SaveTranslationAsync("product", newId, locale, t, ct);
        return newId;
    }
}
