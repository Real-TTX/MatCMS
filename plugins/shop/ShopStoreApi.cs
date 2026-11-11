using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

internal sealed partial class ShopStore
{
    // ---- what the API needs: articles by number, a page of products, stock by line --------------------------------------

    public async Task<Product?> ProductBySkuAsync(string sku, CancellationToken ct = default) =>
        sku.Length > 0 && await T("products").FindAsync(DataQuery.Everything.Eq("sku", sku).OrderBy("id"), ct) is { } r ? await ProductAsync(r.Id, ct) : null;

    public async Task<Variant?> VariantBySkuAsync(string sku, CancellationToken ct = default) =>
        sku.Length > 0 && await T("variants").FindAsync(DataQuery.Everything.Eq("sku", sku).OrderBy("id"), ct) is { } r ? await VariantAsync(r.Id, ct) : null;

    /// <summary>The product an article number names — its own, or one of its variants' (then the variant too).</summary>
    public async Task<(Product? Product, Variant? Variant)> BySkuAsync(string sku, CancellationToken ct = default)
    {
        if (await ProductBySkuAsync(sku, ct) is { } p) return (p, null);
        if (await VariantBySkuAsync(sku, ct) is { } v && await ProductAsync(v.ProductId, ct) is { } owner) return (owner, v);
        return (null, null);
    }

    public async Task<(IReadOnlyList<Product> Items, long Total)> ProductsForApiAsync(DateTime? updatedSince, bool? active, int skip, int take, CancellationToken ct = default)
    {
        var q = DataQuery.Everything;
        if (updatedSince is { } since) q = q.Where("updated", DataOp.Ge, since);
        if (active is { } a) q = q.Eq("active", a);
        var total = await T("products").CountAsync(q, ct);
        var rows = await T("products").QueryAsync(q.OrderBy("id").Page(skip, take), ct);
        return (rows.Select(ProductOf).ToList(), total);
    }

    /// <summary>Sets a product's or a variant's count; null stops counting.</summary>
    public async Task SetStockAsync(Product p, Variant? v, long? stock, CancellationToken ct = default)
    {
        var values = new Dictionary<string, object?> { ["track_stock"] = stock is not null, ["stock"] = stock ?? 0 };
        if (v is not null) await T("variants").UpdateAsync(v.Id, values, ct);
        else { values["updated"] = DateTime.UtcNow; await T("products").UpdateAsync(p.Id, values, ct); }
    }

    /// <summary>Changes a counted stock by <paramref name="delta"/>, atomically and never below 0; null when it is not counted or would fall below 0.</summary>
    public async Task<long?> AddStockAsync(Product p, Variant? v, long delta, CancellationToken ct = default)
    {
        if (v is not null) return v.TrackStock ? await T("variants").IncrementAsync(v.Id, "stock", delta, 0, ct) : null;
        if (!p.TrackStock) return null;
        var now = await T("products").IncrementAsync(p.Id, "stock", delta, 0, ct);
        if (now is not null) await T("products").UpdateAsync(p.Id, new Dictionary<string, object?> { ["updated"] = DateTime.UtcNow }, ct);
        return now;
    }
}
