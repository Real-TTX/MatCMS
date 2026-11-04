using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>The brands' table (see <see cref="Brand"/>). A product refers to its brand by slug, so the slug never changes once the brand exists, and deleting a brand takes it off its products.</summary>
internal sealed partial class ShopStore
{
    private static Brand BrandOf(DataRow r) => new()
    {
        Id = r.Id, Slug = r.Text("slug"), Name = r.Text("name"), Logo = r.Text("logo"), Image = r.Text("image"), Summary = r.Text("summary"), DetailsHtml = r.Text("details"),
        Website = r.Text("website"), SeoTitle = r.Text("seo_title"), SeoDescription = r.Text("seo_description"), Featured = r.Bool("featured"), Active = r.Bool("active"),
        Sort = (int)r.Int("sort"), UpdatedUtc = r.Timestamp("updated") ?? DateTime.UtcNow
    };

    /// <summary>The brands in their order: highlighted first, then by sort number and name.</summary>
    public async Task<IReadOnlyList<Brand>> BrandsAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        var q = DataQuery.Everything;
        if (activeOnly) q = q.Eq("active", true);
        return (await T("brands").QueryAsync(q.OrderBy("featured", true).OrderBy("sort").OrderBy("name"), ct)).Select(BrandOf).ToList();
    }

    public async Task<Brand?> BrandAsync(long id, CancellationToken ct = default) =>
        id > 0 && await T("brands").GetAsync(id, ct) is { } r ? BrandOf(r) : null;

    public async Task<Brand?> BrandBySlugAsync(string slug, CancellationToken ct = default) =>
        slug.Length > 0 && await T("brands").FindByAsync("slug", slug, ct) is { } r ? BrandOf(r) : null;

    /// <summary>Writes a brand: new when <c>Id</c> is 0, else everything but the slug (products refer to it). Returns the id.</summary>
    public async Task<long> SaveBrandAsync(Brand b, CancellationToken ct = default)
    {
        var values = new Dictionary<string, object?>
        {
            ["name"] = b.Name, ["logo"] = b.Logo, ["image"] = b.Image, ["summary"] = b.Summary, ["details"] = b.DetailsHtml, ["website"] = b.Website, ["seo_title"] = b.SeoTitle,
            ["seo_description"] = b.SeoDescription, ["featured"] = b.Featured, ["active"] = b.Active, ["sort"] = b.Sort, ["updated"] = DateTime.UtcNow
        };
        if (b.Id > 0) { await T("brands").UpdateAsync(b.Id, values, ct); return b.Id; }
        values["slug"] = b.Slug;
        return await T("brands").InsertAsync(values, ct);
    }

    /// <summary>Deletes a brand and takes it off its products (they stay, without a brand).</summary>
    public async Task DeleteBrandAsync(long id, CancellationToken ct = default)
    {
        if (await T("brands").GetAsync(id, ct) is not { } r) return;
        await T("products").UpdateWhereAsync(DataQuery.Everything.Eq("brand", r.Text("slug")), new Dictionary<string, object?> { ["brand"] = "" }, ct);
        await T("brands").DeleteAsync(id, ct);
    }

    /// <summary>Gives one product its brand (and nothing else of it changes).</summary>
    public Task<int> SetProductBrandAsync(long productId, string brandSlug, CancellationToken ct = default) =>
        T("products").UpdateAsync(productId, new Dictionary<string, object?> { ["brand"] = brandSlug }, ct);

    /// <summary>How many products each brand has (only active products when <paramref name="activeOnly"/>).</summary>
    public async Task<Dictionary<string, int>> BrandCountsAsync(bool activeOnly = true, CancellationToken ct = default)
    {
        var q = DataQuery.Everything.Where("brand", DataOp.Ne, "");
        if (activeOnly) q = q.Eq("active", true);
        return (await T("products").QueryAsync(q, ct)).Select(r => r.Text("brand")).Where(s => s.Length > 0)
            .GroupBy(s => s, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }

    /// <summary>A free slug for a new brand made from <paramref name="wanted"/> (a name or an address): "-2", "-3" … when it is taken. Empty when nothing usable is left.</summary>
    public async Task<string> FreeBrandSlugAsync(string wanted, CancellationToken ct = default)
    {
        var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(wanted);
        if (slug.Length == 0) return "";
        var baseSlug = slug; var n = 2;
        while (await T("brands").FindByAsync("slug", slug, ct) is not null) slug = baseSlug + "-" + n++;
        return slug;
    }

    /// <summary>
    /// The brand a text names — its slug or its name (any case) — or, with <paramref name="create"/>, a new one by that name. Null when there is none (and none was to be made). This is how the CSV import,
    /// the ERP API and the assistant take "Marke: Transrotor" without anybody having to know a slug.
    /// </summary>
    public async Task<Brand?> ResolveBrandAsync(string text, bool create, CancellationToken ct = default)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        if (await BrandBySlugAsync(t, ct) is { } bySlug) return bySlug;
        var all = await BrandsAsync(false, ct);
        if (all.FirstOrDefault(b => string.Equals(b.Name, t, StringComparison.OrdinalIgnoreCase)) is { } byName) return byName;
        if (all.FirstOrDefault(b => b.Slug == MatCMS.Pages.Admin.Pages.IndexModel.Slugify(t)) is { } bySlugOfName) return bySlugOfName;
        if (!create) return null;
        var slug = await FreeBrandSlugAsync(t, ct);
        if (slug.Length == 0) return null;
        var name = t.Length > 120 ? t[..120] : t;
        var id = await SaveBrandAsync(new Brand { Slug = slug, Name = name, Sort = all.Count }, ct);
        return await BrandAsync(id, ct);
    }
}
