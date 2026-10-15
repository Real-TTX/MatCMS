using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// Everything that reads and writes the shop's tables — the only place that knows the column names. It wraps an <see cref="IPluginData"/>, so inside a
/// transaction (<c>ctx.Data.InTransactionAsync(d =&gt; new ShopStore(d)…)</c>) the same methods take part in it.
/// </summary>
internal sealed class ShopStore
{
    private readonly IPluginData _data;
    public ShopStore(IPluginData data) => _data = data;

    public IPluginData Data => _data;
    private IDataTable T(string name) => _data.Table(name);

    // ---- the declaration -------------------------------------------------------------------------------

    /// <summary>The tables of the shop. The host creates them, adds columns later and never drops or renames anything.</summary>
    public static void Declare(IDataBuilder d)
    {
        d.Table("products", t => t.Text("slug", required: true, def: "").Text("title", required: true, def: "").Text("summary").Text("description").Text("image")
            .Json("gallery", def: "[]").Text("kind", def: "physical").Int("price_cents", def: "0").Int("tax_bp", def: "1900").Text("sku")
            .Bool("track_stock", def: "0").Int("stock", def: "0").Int("weight_g", def: "0").Text("category").Int("max_per_order", def: "0")
            .Bool("active", def: "1").Int("sort", def: "0").Timestamp("created").Timestamp("updated").Text("tags").Bool("featured", def: "0").Int("compare_cents", def: "0")
            .UniqueIndex("slug").Index("active", "sort").Index("category"));
        d.Table("variants", t => t.Int("product_id", required: true, def: "0").Text("name", required: true, def: "").Text("sku").Int("price_delta", def: "0")
            .Bool("track_stock", def: "0").Int("stock", def: "0").Bool("active", def: "1").Int("sort", def: "0").Index("product_id"));
        d.Table("product_files", t => t.Int("product_id", required: true, def: "0").Text("file_name", required: true, def: "").Text("storage_name", required: true, def: "")
            .Int("size", def: "0").Int("sort", def: "0").Index("product_id"));
        d.Table("categories", t => t.Text("slug", required: true, def: "").Text("name", required: true, def: "").Int("sort", def: "0").UniqueIndex("slug"));
        d.Table("carts", t => t.Text("token", required: true, def: "").Int("member_id", def: "0").Json("items", def: "[]").Timestamp("updated").Timestamp("expires")
            .Text("coupon").UniqueIndex("token").Index("expires"));
        d.Table("coupons", t => t.Text("code", required: true, def: "").Text("kind", required: true, def: "percent").Int("value", def: "0").Int("min_order", def: "0").Int("max_uses", def: "0")
            .Int("used", def: "0").Bool("per_customer", def: "0").Timestamp("starts").Timestamp("expires").Text("category").Bool("active", def: "1").Text("note").Timestamp("created")
            .UniqueIndex("code"));
        d.Table("orders", t => t.Text("number", required: true, def: "").Text("status", required: true, def: "awaiting_payment").Text("cancel_reason")
            .Int("member_id", def: "0").Text("email").Text("name").Text("phone").Json("billing", def: "{}").Json("shipping", def: "{}").Text("shipping_method")
            .Int("subtotal", def: "0").Int("shipping_cents", def: "0").Int("tax", def: "0").Int("total", def: "0").Int("refunded", def: "0").Text("currency", def: "EUR")
            .Text("payment_provider").Text("payment_ref").Text("payment_intent").Timestamp("paid").Text("access_token").Text("terms_version").Timestamp("terms_accepted")
            .Bool("waiver", def: "0").Text("note").Text("tracking").Text("invoice_number").Text("locale", def: "de").Bool("small_business", def: "0")
            .Timestamp("reserved_until").Timestamp("created").Timestamp("updated").Int("discount", def: "0").Text("coupon_code").Text("tax_note").Int("shipping_tax_bp", def: "-1")
            .UniqueIndex("number").Index("status").Index("member_id").Index("email").Index("payment_ref").Index("payment_intent").Index("created"));
        d.Table("order_items", t => t.Int("order_id", required: true, def: "0").Int("product_id", def: "0").Int("variant_id", def: "0").Text("title").Text("variant_title")
            .Text("sku").Text("kind", def: "physical").Int("qty", def: "1").Int("unit", def: "0").Int("tax_bp", def: "0").Int("tax", def: "0").Int("line", def: "0").Int("discount", def: "0").Index("order_id"));
        d.Table("order_events", t => t.Int("order_id", required: true, def: "0").Text("kind", required: true, def: "note").Text("text").Json("data").Text("actor")
            .Timestamp("created").Index("order_id"));
        d.Table("payments", t => t.Int("order_id", required: true, def: "0").Text("provider").Text("ref").Int("amount", def: "0").Text("currency").Text("status")
            .Timestamp("created").Index("order_id").Index("ref"));
        d.Table("webhook_events", t => t.Text("event_id", required: true, def: "").Text("provider").Timestamp("received").UniqueIndex("event_id"));
        d.Table("addresses", t => t.Int("member_id", required: true, def: "0").Text("label").Text("name").Text("line1").Text("line2").Text("zip").Text("city")
            .Text("country", def: "DE").Text("phone").Bool("is_default", def: "0").Index("member_id"));
        d.Table("shipping_rates", t => t.Text("name", required: true, def: "").Text("countries", def: "*").Int("price", def: "0").Int("free_from", def: "0")
            .Int("max_weight", def: "0").Bool("active", def: "1").Int("sort", def: "0"));
        d.Table("invoices", t => t.Int("order_id", required: true, def: "0").Text("number", required: true, def: "").Timestamp("issued").Text("html").Text("kind", def: "invoice").Text("pdf").Int("amount", def: "0")
            .UniqueIndex("number").Index("order_id"));
        d.Table("downloads", t => t.Int("order_id", required: true, def: "0").Int("file_id", def: "0").Text("token_hash", required: true, def: "").Timestamp("expires")
            .Int("max_downloads", def: "0").Int("count", def: "0").Bool("revoked", def: "0").Timestamp("created").UniqueIndex("token_hash").Index("order_id"));
        d.Table("sequences", t => t.Text("name", required: true, def: "").Int("value", required: true, def: "0").UniqueIndex("name"));
    }

    // ---- small helpers ---------------------------------------------------------------------------------

    private static string Js(JsonNode? n) => n?.ToJsonString() ?? "null";

    private static T? Parse<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    // ---- products --------------------------------------------------------------------------------------

    private static Product ProductOf(DataRow r) => new()
    {
        Id = r.Id, Slug = r.Text("slug"), Title = r.Text("title"), Summary = r.Text("summary"), DescriptionHtml = r.Text("description"), Image = r.Text("image"),
        GalleryJson = r.Text("gallery") is { Length: > 0 } g ? g : "[]", Kind = ProductKind.Normalise(r.Text("kind")),
        PriceCents = r.Int("price_cents"), TaxBp = (int)r.Int("tax_bp"), Sku = r.Text("sku"), TrackStock = r.Bool("track_stock"), Stock = r.Int("stock"),
        WeightGrams = (int)r.Int("weight_g"), Category = r.Text("category"), Tags = r.Text("tags"), Featured = r.Bool("featured"), CompareCents = r.Int("compare_cents"), MaxPerOrder = (int)r.Int("max_per_order"), Active = r.Bool("active"), Sort = (int)r.Int("sort"),
        CreatedUtc = r.Timestamp("created") ?? DateTime.UtcNow, UpdatedUtc = r.Timestamp("updated") ?? DateTime.UtcNow
    };

    private static Dictionary<string, object?> Values(Product p) => new()
    {
        ["slug"] = p.Slug, ["title"] = p.Title, ["summary"] = p.Summary, ["description"] = p.DescriptionHtml, ["image"] = p.Image, ["gallery"] = p.GalleryJson,
        ["kind"] = p.Kind, ["price_cents"] = p.PriceCents, ["tax_bp"] = p.TaxBp, ["sku"] = p.Sku, ["track_stock"] = p.TrackStock, ["stock"] = p.Stock,
        ["weight_g"] = p.WeightGrams, ["category"] = p.Category, ["tags"] = ShopTags.Normalise(p.Tags), ["featured"] = p.Featured, ["compare_cents"] = Math.Max(0, p.CompareCents), ["max_per_order"] = p.MaxPerOrder, ["active"] = p.Active, ["sort"] = p.Sort, ["updated"] = DateTime.UtcNow
    };

    public async Task<Product?> ProductAsync(long id, CancellationToken ct = default) =>
        id > 0 && await T("products").GetAsync(id, ct) is { } r ? ProductOf(r) : null;

    public async Task<Product?> ProductBySlugAsync(string slug, CancellationToken ct = default) =>
        await T("products").FindByAsync("slug", slug, ct) is { } r ? ProductOf(r) : null;

    public async Task<IReadOnlyList<Product>> ProductsByIdsAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return Array.Empty<Product>();
        return (await T("products").QueryAsync(DataQuery.Everything.Where("id", DataOp.In, list), ct)).Select(ProductOf).ToList();
    }

    /// <summary>Products for the catalogue or the admin list, with the total count for paging.</summary>
    public async Task<(IReadOnlyList<Product> Items, long Total)> ProductsAsync(bool activeOnly, string? category = null, string? search = null, int skip = 0, int take = 0, CancellationToken ct = default)
    {
        var q = DataQuery.Everything;
        if (activeOnly) q = q.Eq("active", true);
        if (!string.IsNullOrWhiteSpace(category)) q = q.Eq("category", category);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Either(new DataFilter("title", DataOp.Contains, search), new DataFilter("summary", DataOp.Contains, search), new DataFilter("sku", DataOp.Contains, search));
        var total = await T("products").CountAsync(q, ct);
        q = q.OrderBy("sort").OrderBy("title");
        if (take > 0) q = q.Page(skip, take);
        return ((await T("products").QueryAsync(q, ct)).Select(ProductOf).ToList(), total);
    }

    /// <summary>Products by a <see cref="ProductQuery"/>, with the total before paging. A keyword is matched in memory (the column holds a list), everything else in the database.</summary>
    public async Task<(IReadOnlyList<Product> Items, long Total)> FindProductsAsync(ProductQuery pq, CancellationToken ct = default)
    {
        if (pq.Ids is { Count: 0 }) return (Array.Empty<Product>(), 0);
        var q = DataQuery.Everything;
        if (pq.ActiveOnly) q = q.Eq("active", true);
        if (pq.FeaturedOnly) q = q.Eq("featured", true);
        if (pq.OnSaleOnly) q = q.Where("compare_cents", DataOp.Gt, 0L);
        if (pq.ExceptId > 0) q = q.Where("id", DataOp.Ne, pq.ExceptId);
        if (pq.Ids is { } ids) q = q.Where("id", DataOp.In, ids.ToList());
        if (!string.IsNullOrWhiteSpace(pq.Category)) q = q.Eq("category", pq.Category);
        if (!string.IsNullOrWhiteSpace(pq.Tag)) q = q.Where("tags", DataOp.Contains, pq.Tag);
        if (!string.IsNullOrWhiteSpace(pq.Search))
            q = q.Either(new DataFilter("title", DataOp.Contains, pq.Search), new DataFilter("summary", DataOp.Contains, pq.Search), new DataFilter("sku", DataOp.Contains, pq.Search), new DataFilter("tags", DataOp.Contains, pq.Search));
        q = pq.Sort switch
        {
            "new" => q.OrderBy("created", true).OrderBy("title"),
            "price_asc" => q.OrderBy("price_cents").OrderBy("title"),
            "price_desc" => q.OrderBy("price_cents", true).OrderBy("title"),
            "name" => q.OrderBy("title"),
            _ => q.OrderBy("sort").OrderBy("title")
        };
        if (!string.IsNullOrWhiteSpace(pq.Tag) || pq.OnSaleOnly)
        {
            // The database only narrowed it down ("bio" is also inside "biodynamisch"; an earlier price is not yet a higher one); the exact test decides.
            var all = (await T("products").QueryAsync(q, ct)).Select(ProductOf).Where(p => (string.IsNullOrWhiteSpace(pq.Tag) || ShopTags.Has(p, pq.Tag!)) && (!pq.OnSaleOnly || p.OnSale)).ToList();
            var paged = pq.Take > 0 ? all.Skip(pq.Skip).Take(pq.Take).ToList() : all;
            return (paged, all.Count);
        }
        var total = await T("products").CountAsync(q, ct);
        if (pq.Take > 0) q = q.Page(pq.Skip, pq.Take);
        return ((await T("products").QueryAsync(q, ct)).Select(ProductOf).ToList(), total);
    }

    /// <summary>What a visitor can filter by: how many ACTIVE products each category and each keyword has.</summary>
    public async Task<(Dictionary<string, int> Categories, List<(string Tag, int Count)> Tags)> FacetsAsync(CancellationToken ct = default)
    {
        var rows = (await T("products").QueryAsync(DataQuery.Everything.Eq("active", true), ct)).Select(ProductOf).ToList();
        var cats = rows.Where(p => p.Category.Length > 0).GroupBy(p => p.Category).ToDictionary(g => g.Key, g => g.Count());
        var tags = new Dictionary<string, (string Tag, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in rows.SelectMany(p => ShopTags.Split(p.Tags)))
            tags[t] = tags.TryGetValue(t, out var cur) ? (cur.Tag, cur.Count + 1) : (t, 1);
        return (cats, tags.Values.OrderByDescending(x => x.Count).ThenBy(x => x.Tag, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task<long> SaveProductAsync(Product p, CancellationToken ct = default)
    {
        var values = Values(p);
        if (p.Id == 0) { values["created"] = DateTime.UtcNow; return await T("products").InsertAsync(values, ct); }
        await T("products").UpdateAsync(p.Id, values, ct);
        return p.Id;
    }

    public async Task DeleteProductAsync(long id, CancellationToken ct = default)
    {
        await T("variants").DeleteWhereAsync(DataQuery.Everything.Eq("product_id", id), ct);
        await T("product_files").DeleteWhereAsync(DataQuery.Everything.Eq("product_id", id), ct);
        await T("products").DeleteAsync(id, ct);
    }

    public async Task<bool> SlugTakenAsync(string slug, long exceptId, CancellationToken ct = default) =>
        await T("products").FindByAsync("slug", slug, ct) is { } r && r.Id != exceptId;

    // ---- variants --------------------------------------------------------------------------------------

    private static Variant VariantOf(DataRow r) => new()
    {
        Id = r.Id, ProductId = r.Int("product_id"), Name = r.Text("name"), Sku = r.Text("sku"), PriceDeltaCents = r.Int("price_delta"),
        TrackStock = r.Bool("track_stock"), Stock = r.Int("stock"), Active = r.Bool("active"), Sort = (int)r.Int("sort")
    };

    public async Task<IReadOnlyList<Variant>> VariantsAsync(long productId, bool activeOnly = false, CancellationToken ct = default)
    {
        var q = DataQuery.Everything.Eq("product_id", productId);
        if (activeOnly) q = q.Eq("active", true);
        return (await T("variants").QueryAsync(q.OrderBy("sort").OrderBy("id"), ct)).Select(VariantOf).ToList();
    }

    public async Task<IReadOnlyList<Variant>> VariantsOfAsync(IEnumerable<long> productIds, CancellationToken ct = default)
    {
        var list = productIds.Distinct().ToList();
        if (list.Count == 0) return Array.Empty<Variant>();
        return (await T("variants").QueryAsync(DataQuery.Everything.Where("product_id", DataOp.In, list), ct)).Select(VariantOf).ToList();
    }

    public async Task<Variant?> VariantAsync(long id, CancellationToken ct = default) =>
        id > 0 && await T("variants").GetAsync(id, ct) is { } r ? VariantOf(r) : null;

    public async Task<long> SaveVariantAsync(Variant v, CancellationToken ct = default)
    {
        var values = new Dictionary<string, object?>
        {
            ["product_id"] = v.ProductId, ["name"] = v.Name, ["sku"] = v.Sku, ["price_delta"] = v.PriceDeltaCents, ["track_stock"] = v.TrackStock,
            ["stock"] = v.Stock, ["active"] = v.Active, ["sort"] = v.Sort
        };
        if (v.Id == 0) return await T("variants").InsertAsync(values, ct);
        await T("variants").UpdateAsync(v.Id, values, ct);
        return v.Id;
    }

    public Task<int> DeleteVariantAsync(long id, CancellationToken ct = default) => T("variants").DeleteAsync(id, ct);

    // ---- files of digital products ------------------------------------------------------------------------

    private static ProductFile FileOf(DataRow r) => new()
    {
        Id = r.Id, ProductId = r.Int("product_id"), FileName = r.Text("file_name"), StorageName = r.Text("storage_name"), Size = r.Int("size"), Sort = (int)r.Int("sort")
    };

    public async Task<IReadOnlyList<ProductFile>> FilesOfProductAsync(long productId, CancellationToken ct = default) =>
        (await T("product_files").QueryAsync(DataQuery.Everything.Eq("product_id", productId).OrderBy("sort").OrderBy("id"), ct)).Select(FileOf).ToList();

    public async Task<ProductFile?> FileAsync(long id, CancellationToken ct = default) =>
        id > 0 && await T("product_files").GetAsync(id, ct) is { } r ? FileOf(r) : null;

    public Task<long> AddFileAsync(ProductFile f, CancellationToken ct = default) => T("product_files").InsertAsync(new Dictionary<string, object?>
    {
        ["product_id"] = f.ProductId, ["file_name"] = f.FileName, ["storage_name"] = f.StorageName, ["size"] = f.Size, ["sort"] = f.Sort
    }, ct);

    public Task<int> DeleteFileAsync(long id, CancellationToken ct = default) => T("product_files").DeleteAsync(id, ct);

    // ---- categories ------------------------------------------------------------------------------------

    private static Category CategoryOf(DataRow r) => new() { Id = r.Id, Slug = r.Text("slug"), Name = r.Text("name"), Sort = (int)r.Int("sort") };

    public async Task<IReadOnlyList<Category>> CategoriesAsync(CancellationToken ct = default) =>
        (await T("categories").QueryAsync(DataQuery.Everything.OrderBy("sort").OrderBy("name"), ct)).Select(CategoryOf).ToList();

    public async Task<Category?> CategoryBySlugAsync(string slug, CancellationToken ct = default) =>
        await T("categories").FindByAsync("slug", slug, ct) is { } r ? CategoryOf(r) : null;

    public Task<long> AddCategoryAsync(string slug, string name, int sort, CancellationToken ct = default) =>
        T("categories").InsertAsync(new Dictionary<string, object?> { ["slug"] = slug, ["name"] = name, ["sort"] = sort }, ct);

    public Task<int> UpdateCategoryAsync(long id, string name, int sort, CancellationToken ct = default) =>
        T("categories").UpdateAsync(id, new Dictionary<string, object?> { ["name"] = name, ["sort"] = sort }, ct);

    public async Task DeleteCategoryAsync(long id, CancellationToken ct = default)
    {
        var c = await T("categories").GetAsync(id, ct);
        if (c is null) return;
        // The products keep existing; they just lose the category.
        await T("products").UpdateWhereAsync(DataQuery.Everything.Eq("category", c.Text("slug")), new Dictionary<string, object?> { ["category"] = "" }, ct);
        await T("categories").DeleteAsync(id, ct);
    }

    // ---- carts -----------------------------------------------------------------------------------------

    public sealed record CartRow(long Id, string Token, long MemberId, IReadOnlyList<CartLine> Lines, string Coupon = "");

    private static CartRow CartOf(DataRow r)
    {
        var lines = new List<CartLine>();
        if (r.Json("items") is JsonArray arr)
            foreach (var n in arr)
                if (n is JsonObject o && o["p"] is JsonValue p && o["q"] is JsonValue q && p.TryGetValue<long>(out var pid) && q.TryGetValue<int>(out var qty) && pid > 0 && qty > 0)
                    lines.Add(new CartLine(pid, o["v"] is JsonValue v && v.TryGetValue<long>(out var vid) ? vid : 0, qty));
        return new CartRow(r.Id, r.Text("token"), r.Int("member_id"), lines, r.Text("coupon"));
    }

    public static string LinesJson(IEnumerable<CartLine> lines) =>
        new JsonArray(lines.Select(l => (JsonNode)new JsonObject { ["p"] = l.ProductId, ["v"] = l.VariantId, ["q"] = l.Qty }).ToArray()).ToJsonString();

    public async Task<CartRow?> CartAsync(string token, CancellationToken ct = default) =>
        string.IsNullOrEmpty(token) ? null : await T("carts").FindAsync(DataQuery.Everything.Eq("token", token).Where("expires", DataOp.Gt, DateTime.UtcNow), ct) is { } r ? CartOf(r) : null;

    public async Task SaveCartAsync(string token, long memberId, IReadOnlyList<CartLine> lines, CancellationToken ct = default)
    {
        var values = new Dictionary<string, object?>
        {
            ["member_id"] = memberId, ["items"] = LinesJson(lines), ["updated"] = DateTime.UtcNow, ["expires"] = DateTime.UtcNow.AddDays(30)
        };
        var existing = await T("carts").FindByAsync("token", token, ct);
        if (existing is null) { values["token"] = token; await T("carts").InsertAsync(values, ct); }
        else await T("carts").UpdateAsync(existing.Id, values, ct);
    }

    /// <summary>Remembers (or, with an empty code, forgets) the coupon of a cart. False when there is no such cart.</summary>
    public async Task<bool> SetCartCouponAsync(string token, string code, CancellationToken ct = default)
    {
        var existing = await T("carts").FindByAsync("token", token, ct);
        if (existing is null) return false;
        await T("carts").UpdateAsync(existing.Id, new Dictionary<string, object?> { ["coupon"] = code }, ct);
        return true;
    }

    public Task<int> DeleteCartAsync(string token, CancellationToken ct = default) => T("carts").DeleteWhereAsync(DataQuery.Everything.Eq("token", token), ct);
    public Task<int> DeleteExpiredCartsAsync(CancellationToken ct = default) => T("carts").DeleteWhereAsync(DataQuery.Everything.Where("expires", DataOp.Lt, DateTime.UtcNow), ct);
    public Task<int> DeleteCartsOfMemberAsync(long memberId, CancellationToken ct = default) => T("carts").DeleteWhereAsync(DataQuery.Everything.Eq("member_id", memberId), ct);

    // ---- orders ----------------------------------------------------------------------------------------

    private static Order OrderOf(DataRow r) => new()
    {
        Id = r.Id, Number = r.Text("number"), Status = r.Text("status"), CancelReason = r.Text("cancel_reason"), MemberId = r.Int("member_id"),
        Email = r.Text("email"), Name = r.Text("name"), Phone = r.Text("phone"),
        Billing = Parse<Address>(r.Text("billing")) ?? new Address(), Shipping = Parse<Address>(r.Text("shipping")) ?? new Address(),
        ShippingMethod = r.Text("shipping_method"), SubtotalCents = r.Int("subtotal"), ShippingCents = r.Int("shipping_cents"), TaxCents = r.Int("tax"),
        TotalCents = r.Int("total"), RefundedCents = r.Int("refunded"), Currency = r.Text("currency") is { Length: > 0 } c ? c : "EUR",
        PaymentProvider = r.Text("payment_provider"), PaymentRef = r.Text("payment_ref"), PaymentIntent = r.Text("payment_intent"), PaidUtc = r.Timestamp("paid"),
        AccessToken = r.Text("access_token"), TermsVersion = r.Text("terms_version"), TermsAcceptedUtc = r.Timestamp("terms_accepted"), WithdrawalWaiver = r.Bool("waiver"),
        Note = r.Text("note"), Tracking = r.Text("tracking"), InvoiceNumber = r.Text("invoice_number"), Locale = r.Text("locale") is { Length: > 0 } l ? l : "de",
        SmallBusiness = r.Bool("small_business"), ReservedUntilUtc = r.Timestamp("reserved_until"),
        DiscountCents = r.Int("discount"), CouponCode = r.Text("coupon_code"), TaxNote = r.Text("tax_note"), ShippingTaxBp = (int)r.Int("shipping_tax_bp"),
        CreatedUtc = r.Timestamp("created") ?? DateTime.UtcNow, UpdatedUtc = r.Timestamp("updated") ?? DateTime.UtcNow
    };

    public async Task<Order?> OrderAsync(long id, CancellationToken ct = default) =>
        id > 0 && await T("orders").GetAsync(id, ct) is { } r ? OrderOf(r) : null;

    public async Task<Order?> OrderByNumberAsync(string number, CancellationToken ct = default) =>
        await T("orders").FindByAsync("number", number, ct) is { } r ? OrderOf(r) : null;

    public async Task<Order?> OrderByPaymentRefAsync(string paymentRef, CancellationToken ct = default) =>
        string.IsNullOrEmpty(paymentRef) ? null : await T("orders").FindByAsync("payment_ref", paymentRef, ct) is { } r ? OrderOf(r) : null;

    public async Task<Order?> OrderByPaymentIntentAsync(string intent, CancellationToken ct = default) =>
        string.IsNullOrEmpty(intent) ? null : await T("orders").FindByAsync("payment_intent", intent, ct) is { } r ? OrderOf(r) : null;

    public async Task<(IReadOnlyList<Order> Items, long Total)> OrdersAsync(string? status = null, string? search = null, long memberId = 0, string? email = null,
        int skip = 0, int take = 0, CancellationToken ct = default)
    {
        var q = DataQuery.Everything;
        if (!string.IsNullOrWhiteSpace(status)) q = q.Eq("status", status);
        if (memberId > 0 && !string.IsNullOrWhiteSpace(email))
            q = q.Either(new DataFilter("member_id", DataOp.Eq, memberId), new DataFilter("email", DataOp.Eq, email));
        else if (memberId > 0) q = q.Eq("member_id", memberId);
        else if (!string.IsNullOrWhiteSpace(email)) q = q.Eq("email", email);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Either(new DataFilter("number", DataOp.Contains, search), new DataFilter("email", DataOp.Contains, search), new DataFilter("name", DataOp.Contains, search),
                new DataFilter("invoice_number", DataOp.Contains, search));
        var total = await T("orders").CountAsync(q, ct);
        q = q.OrderBy("created", descending: true).OrderBy("id", descending: true);
        if (take > 0) q = q.Page(skip, take);
        return ((await T("orders").QueryAsync(q, ct)).Select(OrderOf).ToList(), total);
    }

    public Task<long> CountOrdersAsync(string status, CancellationToken ct = default) => T("orders").CountAsync(DataQuery.Everything.Eq("status", status), ct);

    public async Task<IReadOnlyList<Order>> OrdersWhereAsync(DataQuery q, CancellationToken ct = default) =>
        (await T("orders").QueryAsync(q, ct)).Select(OrderOf).ToList();

    public async Task<long> InsertOrderAsync(Order o, CancellationToken ct = default)
    {
        var values = new Dictionary<string, object?>
        {
            ["number"] = o.Number, ["status"] = o.Status, ["cancel_reason"] = o.CancelReason, ["member_id"] = o.MemberId, ["email"] = o.Email, ["name"] = o.Name, ["phone"] = o.Phone,
            ["billing"] = ToJson(o.Billing), ["shipping"] = ToJson(o.Shipping), ["shipping_method"] = o.ShippingMethod, ["subtotal"] = o.SubtotalCents,
            ["shipping_cents"] = o.ShippingCents, ["tax"] = o.TaxCents, ["total"] = o.TotalCents, ["refunded"] = o.RefundedCents, ["currency"] = o.Currency,
            ["payment_provider"] = o.PaymentProvider, ["payment_ref"] = o.PaymentRef, ["payment_intent"] = o.PaymentIntent, ["paid"] = o.PaidUtc,
            ["access_token"] = o.AccessToken, ["terms_version"] = o.TermsVersion, ["terms_accepted"] = o.TermsAcceptedUtc, ["waiver"] = o.WithdrawalWaiver,
            ["note"] = o.Note, ["tracking"] = o.Tracking, ["invoice_number"] = o.InvoiceNumber, ["locale"] = o.Locale, ["small_business"] = o.SmallBusiness,
            ["reserved_until"] = o.ReservedUntilUtc, ["created"] = DateTime.UtcNow, ["updated"] = DateTime.UtcNow,
            ["discount"] = o.DiscountCents, ["coupon_code"] = o.CouponCode, ["tax_note"] = o.TaxNote, ["shipping_tax_bp"] = o.ShippingTaxBp
        };
        return await T("orders").InsertAsync(values, ct);
    }

    /// <summary>Changes columns of an order and stamps <c>updated</c>. Only for the fields that are NOT the status — the status moves through <see cref="ShopOrders"/>.</summary>
    public Task<int> UpdateOrderAsync(long id, IDictionary<string, object?> changes, CancellationToken ct = default)
    {
        var values = new Dictionary<string, object?>(changes) { ["updated"] = DateTime.UtcNow };
        return T("orders").UpdateAsync(id, values, ct);
    }

    /// <summary>Writes the status — the one statement that does. Called by the state machine and by nobody else.</summary>
    internal Task<int> SetStatusAsync(long id, string status, string cancelReason, CancellationToken ct = default) =>
        T("orders").UpdateAsync(id, new Dictionary<string, object?> { ["status"] = status, ["cancel_reason"] = cancelReason, ["updated"] = DateTime.UtcNow }, ct);

    /// <summary>Takes the order's customer data out of the account link: the account was deleted. The order itself stays — invoices must be kept.</summary>
    public Task<int> UnlinkMemberAsync(long memberId, CancellationToken ct = default) =>
        T("orders").UpdateWhereAsync(DataQuery.Everything.Eq("member_id", memberId), new Dictionary<string, object?> { ["member_id"] = 0L }, ct);

    public Task<int> LinkGuestOrdersAsync(string email, long memberId, CancellationToken ct = default) =>
        T("orders").UpdateWhereAsync(DataQuery.Everything.Eq("email", email).Eq("member_id", 0L), new Dictionary<string, object?> { ["member_id"] = memberId }, ct);

    // ---- order items, events, payments -------------------------------------------------------------------

    private static OrderItem ItemOf(DataRow r) => new()
    {
        Id = r.Id, OrderId = r.Int("order_id"), ProductId = r.Int("product_id"), VariantId = r.Int("variant_id"), Title = r.Text("title"), VariantTitle = r.Text("variant_title"),
        Sku = r.Text("sku"), Kind = ProductKind.Normalise(r.Text("kind")), Qty = (int)r.Int("qty"), UnitCents = r.Int("unit"), TaxBp = (int)r.Int("tax_bp"),
        TaxCents = r.Int("tax"), LineCents = r.Int("line"), DiscountCents = r.Int("discount")
    };

    public async Task<IReadOnlyList<OrderItem>> ItemsAsync(long orderId, CancellationToken ct = default) =>
        (await T("order_items").QueryAsync(DataQuery.Everything.Eq("order_id", orderId).OrderBy("id"), ct)).Select(ItemOf).ToList();

    public Task<long> AddItemAsync(OrderItem i, CancellationToken ct = default) => T("order_items").InsertAsync(new Dictionary<string, object?>
    {
        ["order_id"] = i.OrderId, ["product_id"] = i.ProductId, ["variant_id"] = i.VariantId, ["title"] = i.Title, ["variant_title"] = i.VariantTitle, ["sku"] = i.Sku,
        ["kind"] = i.Kind, ["qty"] = i.Qty, ["unit"] = i.UnitCents, ["tax_bp"] = i.TaxBp, ["tax"] = i.TaxCents, ["line"] = i.LineCents, ["discount"] = i.DiscountCents
    }, ct);

    public sealed record OrderEvent(long Id, long OrderId, string Kind, string Text, string Actor, DateTime CreatedUtc);

    public async Task<IReadOnlyList<OrderEvent>> EventsAsync(long orderId, CancellationToken ct = default) =>
        (await T("order_events").QueryAsync(DataQuery.Everything.Eq("order_id", orderId).OrderBy("created").OrderBy("id"), ct))
            .Select(r => new OrderEvent(r.Id, r.Int("order_id"), r.Text("kind"), r.Text("text"), r.Text("actor"), r.Timestamp("created") ?? DateTime.UtcNow)).ToList();

    public Task<long> AddEventAsync(long orderId, string kind, string text, string actor = "system", JsonNode? data = null, CancellationToken ct = default) =>
        T("order_events").InsertAsync(new Dictionary<string, object?>
        {
            ["order_id"] = orderId, ["kind"] = kind, ["text"] = text.Length > 2000 ? text[..2000] : text, ["actor"] = actor, ["data"] = data, ["created"] = DateTime.UtcNow
        }, ct);

    public Task<long> AddPaymentAsync(long orderId, string provider, string reference, long amount, string currency, string status, CancellationToken ct = default) =>
        T("payments").InsertAsync(new Dictionary<string, object?>
        {
            ["order_id"] = orderId, ["provider"] = provider, ["ref"] = reference, ["amount"] = amount, ["currency"] = currency, ["status"] = status, ["created"] = DateTime.UtcNow
        }, ct);

    /// <summary>True when this provider event was handled before.</summary>
    public async Task<bool> HasSeenWebhookAsync(string eventId, CancellationToken ct = default) => await T("webhook_events").FindByAsync("event_id", eventId, ct) is not null;

    /// <summary>Records a webhook event id; false when it was seen before (a provider delivers at least once, so the second time is a no-op).</summary>
    public async Task<bool> TryRecordWebhookAsync(string eventId, string provider, CancellationToken ct = default)
    {
        if (await T("webhook_events").FindByAsync("event_id", eventId, ct) is not null) return false;
        try { await T("webhook_events").InsertAsync(new Dictionary<string, object?> { ["event_id"] = eventId, ["provider"] = provider, ["received"] = DateTime.UtcNow }, ct); return true; }
        catch { return false; }          // another delivery of the same event inserted it between the look and the insert
    }

    // ---- addresses -------------------------------------------------------------------------------------

    public sealed record SavedAddress(long Id, long MemberId, string Label, Address Address, bool IsDefault);

    private static SavedAddress AddressOf(DataRow r) => new(r.Id, r.Int("member_id"), r.Text("label"),
        new Address { Name = r.Text("name"), Line1 = r.Text("line1"), Line2 = r.Text("line2"), Zip = r.Text("zip"), City = r.Text("city"), Country = r.Text("country"), Phone = r.Text("phone") },
        r.Bool("is_default"));

    public async Task<IReadOnlyList<SavedAddress>> AddressesAsync(long memberId, CancellationToken ct = default) =>
        (await T("addresses").QueryAsync(DataQuery.Everything.Eq("member_id", memberId).OrderBy("is_default", descending: true).OrderBy("id"), ct)).Select(AddressOf).ToList();

    public async Task<SavedAddress?> AddressAsync(long id, long memberId, CancellationToken ct = default) =>
        await T("addresses").FindAsync(DataQuery.Everything.Eq("id", id).Eq("member_id", memberId), ct) is { } r ? AddressOf(r) : null;

    public async Task<long> SaveAddressAsync(long id, long memberId, string label, Address a, bool isDefault, CancellationToken ct = default)
    {
        var values = new Dictionary<string, object?>
        {
            ["member_id"] = memberId, ["label"] = label, ["name"] = a.Name, ["line1"] = a.Line1, ["line2"] = a.Line2, ["zip"] = a.Zip, ["city"] = a.City,
            ["country"] = a.Country, ["phone"] = a.Phone, ["is_default"] = isDefault
        };
        long saved;
        if (id == 0) saved = await T("addresses").InsertAsync(values, ct);
        else { await T("addresses").UpdateWhereAsync(DataQuery.Everything.Eq("id", id).Eq("member_id", memberId), values, ct); saved = id; }
        if (isDefault)
            await T("addresses").UpdateWhereAsync(DataQuery.Everything.Eq("member_id", memberId).Where("id", DataOp.Ne, saved), new Dictionary<string, object?> { ["is_default"] = false }, ct);
        return saved;
    }

    public Task<int> DeleteAddressAsync(long id, long memberId, CancellationToken ct = default) =>
        T("addresses").DeleteWhereAsync(DataQuery.Everything.Eq("id", id).Eq("member_id", memberId), ct);

    public Task<int> DeleteAddressesOfMemberAsync(long memberId, CancellationToken ct = default) => T("addresses").DeleteWhereAsync(DataQuery.Everything.Eq("member_id", memberId), ct);

    // ---- shipping --------------------------------------------------------------------------------------

    private static ShippingRate RateOf(DataRow r) => new()
    {
        Id = r.Id, Name = r.Text("name"), Countries = r.Text("countries") is { Length: > 0 } c ? c : "*", PriceCents = r.Int("price"), FreeFromCents = r.Int("free_from"),
        MaxWeightGrams = (int)r.Int("max_weight"), Active = r.Bool("active"), Sort = (int)r.Int("sort")
    };

    public async Task<IReadOnlyList<ShippingRate>> RatesAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        var q = DataQuery.Everything;
        if (activeOnly) q = q.Eq("active", true);
        return (await T("shipping_rates").QueryAsync(q.OrderBy("sort").OrderBy("id"), ct)).Select(RateOf).ToList();
    }

    public async Task<ShippingRate?> RateAsync(long id, CancellationToken ct = default) =>
        id > 0 && await T("shipping_rates").GetAsync(id, ct) is { } r ? RateOf(r) : null;

    public async Task<long> SaveRateAsync(ShippingRate s, CancellationToken ct = default)
    {
        var values = new Dictionary<string, object?>
        {
            ["name"] = s.Name, ["countries"] = s.Countries, ["price"] = s.PriceCents, ["free_from"] = s.FreeFromCents, ["max_weight"] = s.MaxWeightGrams, ["active"] = s.Active, ["sort"] = s.Sort
        };
        if (s.Id == 0) return await T("shipping_rates").InsertAsync(values, ct);
        await T("shipping_rates").UpdateAsync(s.Id, values, ct);
        return s.Id;
    }

    public Task<int> DeleteRateAsync(long id, CancellationToken ct = default) => T("shipping_rates").DeleteAsync(id, ct);

    // ---- invoices and downloads --------------------------------------------------------------------------

    /// <summary>A document of an order: its invoice, or a credit note written when money was given back. Both are kept as HTML and as PDF, exactly as they were issued.</summary>
    public sealed record InvoiceRow(long Id, long OrderId, string Number, string Kind, DateTime IssuedUtc, string Html, string PdfBase64, long AmountCents)
    {
        public bool IsCredit => Kind == InvoiceKinds.Credit;
        public byte[] Pdf => PdfBase64.Length == 0 ? Array.Empty<byte>() : Convert.FromBase64String(PdfBase64);
    }

    private static InvoiceRow InvoiceOf(DataRow r) => new(r.Id, r.Int("order_id"), r.Text("number"), r.Text("kind") is { Length: > 0 } k ? k : InvoiceKinds.Invoice,
        r.Timestamp("issued") ?? DateTime.UtcNow, r.Text("html"), r.Text("pdf"), r.Int("amount"));

    /// <summary>The invoice of an order (never a credit note).</summary>
    public async Task<InvoiceRow?> InvoiceOfOrderAsync(long orderId, CancellationToken ct = default) =>
        (await T("invoices").QueryAsync(DataQuery.Everything.Eq("order_id", orderId).OrderBy("id"), ct)).Select(InvoiceOf).FirstOrDefault(i => !i.IsCredit);

    /// <summary>The credit notes of an order, oldest first.</summary>
    public async Task<IReadOnlyList<InvoiceRow>> CreditNotesOfOrderAsync(long orderId, CancellationToken ct = default) =>
        (await T("invoices").QueryAsync(DataQuery.Everything.Eq("order_id", orderId).OrderBy("id"), ct)).Select(InvoiceOf).Where(i => i.IsCredit).ToList();

    /// <summary>One document of an order by its number (the invoice's or a credit note's).</summary>
    public async Task<InvoiceRow?> DocumentOfOrderAsync(long orderId, string number, CancellationToken ct = default) =>
        await T("invoices").FindByAsync("number", number, ct) is { } r && r.Int("order_id") == orderId ? InvoiceOf(r) : null;

    public Task<long> AddInvoiceAsync(long orderId, string number, string kind, string html, byte[] pdf, long amountCents, CancellationToken ct = default) => T("invoices").InsertAsync(new Dictionary<string, object?>
    {
        ["order_id"] = orderId, ["number"] = number, ["kind"] = kind, ["issued"] = DateTime.UtcNow, ["html"] = html, ["pdf"] = Convert.ToBase64String(pdf), ["amount"] = amountCents
    }, ct);

    // ---- coupons ---------------------------------------------------------------------------------------

    private static Coupon CouponOf(DataRow r) => new()
    {
        Id = r.Id, Code = r.Text("code"), Kind = CouponKind.Normalise(r.Text("kind")), Value = r.Int("value"), MinOrderCents = r.Int("min_order"), MaxUses = (int)r.Int("max_uses"),
        Used = (int)r.Int("used"), OncePerCustomer = r.Bool("per_customer"), StartsUtc = r.Timestamp("starts"), ExpiresUtc = r.Timestamp("expires"), Category = r.Text("category"),
        Active = r.Bool("active"), Note = r.Text("note"), CreatedUtc = r.Timestamp("created") ?? DateTime.UtcNow
    };

    public async Task<IReadOnlyList<Coupon>> CouponsAsync(CancellationToken ct = default) =>
        (await T("coupons").QueryAsync(DataQuery.Everything.OrderBy("created", descending: true).OrderBy("id", descending: true), ct)).Select(CouponOf).ToList();

    public async Task<Coupon?> CouponAsync(long id, CancellationToken ct = default) => id > 0 && await T("coupons").GetAsync(id, ct) is { } r ? CouponOf(r) : null;

    public async Task<Coupon?> CouponByCodeAsync(string code, CancellationToken ct = default) =>
        string.IsNullOrEmpty(code) ? null : await T("coupons").FindByAsync("code", Coupon.NormaliseCode(code), ct) is { } r ? CouponOf(r) : null;

    /// <summary>Saves a coupon (its use count is never written here — see <see cref="UseCouponAsync"/>). The code must be free; false when another coupon has it.</summary>
    public async Task<(bool Ok, long Id)> SaveCouponAsync(Coupon c, CancellationToken ct = default)
    {
        var existing = await T("coupons").FindByAsync("code", c.Code, ct);
        if (existing is not null && existing.Id != c.Id) return (false, 0);
        var values = new Dictionary<string, object?>
        {
            ["code"] = c.Code, ["kind"] = c.Kind, ["value"] = c.Value, ["min_order"] = c.MinOrderCents, ["max_uses"] = c.MaxUses, ["per_customer"] = c.OncePerCustomer,
            ["starts"] = c.StartsUtc, ["expires"] = c.ExpiresUtc, ["category"] = c.Category, ["active"] = c.Active, ["note"] = c.Note
        };
        if (c.Id == 0) { values["created"] = DateTime.UtcNow; return (true, await T("coupons").InsertAsync(values, ct)); }
        await T("coupons").UpdateAsync(c.Id, values, ct);
        return (true, c.Id);
    }

    public Task<int> DeleteCouponAsync(long id, CancellationToken ct = default) => T("coupons").DeleteAsync(id, ct);

    /// <summary>Counts one use. Call inside the order's transaction, after the limit was checked there (the transaction holds the write lock, so nobody else can slip in between).</summary>
    public async Task UseCouponAsync(string code, CancellationToken ct = default)
    {
        if (await T("coupons").FindByAsync("code", code, ct) is { } r) await T("coupons").IncrementAsync(r.Id, "used", 1, null, ct);
    }

    /// <summary>Gives a use back (the order was cancelled before it was paid).</summary>
    public async Task ReleaseCouponAsync(string code, CancellationToken ct = default)
    {
        if (await T("coupons").FindByAsync("code", code, ct) is { } r) await T("coupons").IncrementAsync(r.Id, "used", -1, 0, ct);
    }

    /// <summary>How many orders this address has placed with the coupon and not cancelled.</summary>
    public async Task<long> CouponOrdersOfAsync(string code, string email, CancellationToken ct = default) =>
        await T("orders").CountAsync(DataQuery.Everything.Eq("coupon_code", code).Eq("email", email.Trim().ToLowerInvariant()).Where("status", DataOp.Ne, OrderStatus.Cancelled), ct);

    public async Task<long> OrdersWithCouponAsync(string code, CancellationToken ct = default) =>
        await T("orders").CountAsync(DataQuery.Everything.Eq("coupon_code", code).Where("status", DataOp.Ne, OrderStatus.Cancelled), ct);

    public sealed record DownloadRow(long Id, long OrderId, long FileId, string TokenHash, DateTime ExpiresUtc, int MaxDownloads, int Count, bool Revoked);

    private static DownloadRow DownloadOf(DataRow r) => new(r.Id, r.Int("order_id"), r.Int("file_id"), r.Text("token_hash"), r.Timestamp("expires") ?? DateTime.UtcNow,
        (int)r.Int("max_downloads"), (int)r.Int("count"), r.Bool("revoked"));

    public Task<long> AddDownloadAsync(long orderId, long fileId, string tokenHash, DateTime expiresUtc, int maxDownloads, CancellationToken ct = default) =>
        T("downloads").InsertAsync(new Dictionary<string, object?>
        {
            ["order_id"] = orderId, ["file_id"] = fileId, ["token_hash"] = tokenHash, ["expires"] = expiresUtc, ["max_downloads"] = maxDownloads, ["count"] = 0L, ["revoked"] = false,
            ["created"] = DateTime.UtcNow
        }, ct);

    public async Task<DownloadRow?> DownloadByHashAsync(string tokenHash, CancellationToken ct = default) =>
        await T("downloads").FindByAsync("token_hash", tokenHash, ct) is { } r ? DownloadOf(r) : null;

    public async Task<IReadOnlyList<DownloadRow>> DownloadsOfOrderAsync(long orderId, CancellationToken ct = default) =>
        (await T("downloads").QueryAsync(DataQuery.Everything.Eq("order_id", orderId).OrderBy("id"), ct)).Select(DownloadOf).ToList();

    /// <summary>
    /// Counts one download, but only while the limit allows. The count goes up in ONE database step and the figure that comes back is checked against the
    /// limit: two simultaneous requests get different figures, so only as many as the limit allows come out ahead; the one that went over takes its count back.
    /// </summary>
    public async Task<bool> TryCountDownloadAsync(DownloadRow d, CancellationToken ct = default)
    {
        var table = T("downloads");
        var now = await table.IncrementAsync(d.Id, "count", 1, null, ct);
        if (now is null) return false;
        if (d.MaxDownloads > 0 && now.Value > d.MaxDownloads)
        {
            await table.IncrementAsync(d.Id, "count", -1, null, ct);
            return false;
        }
        return true;
    }

    public Task<int> RevokeDownloadsAsync(long orderId, CancellationToken ct = default) =>
        T("downloads").UpdateWhereAsync(DataQuery.Everything.Eq("order_id", orderId), new Dictionary<string, object?> { ["revoked"] = true }, ct);

    /// <summary>
    /// The FIRST statement of every transaction that reads and then writes: it takes SQLite's write lock before anything is read. Without it two such transactions both read
    /// first (each holding a read lock) and then both want to write — SQLite cannot let either through without a deadlock and fails one at once with "database is locked", which
    /// for a shop means an order lost to an error page. With it they simply queue (the busy timeout waits), and the second one re-reads what the first one wrote.
    /// </summary>
    public Task LockForWriteAsync(CancellationToken ct = default) =>
        T("sequences").UpdateWhereAsync(DataQuery.Everything.Eq("name", "__lock"), new Dictionary<string, object?> { ["value"] = 0L }, ct);

    // ---- sequences and stock ------------------------------------------------------------------------------

    /// <summary>The next number of a named sequence. Call inside the transaction that uses it (see <see cref="PluginDataExtensions.NextNumberAsync"/>).</summary>
    public Task<long> NextAsync(string name, long start, CancellationToken ct = default) => NextCoreAsync(name, start, ct);

    private async Task<long> NextCoreAsync(string name, long start, CancellationToken ct)
    {
        var table = T("sequences");
        var row = await table.FindByAsync("name", name, ct);
        if (row is null)
        {
            try { await table.InsertAsync(new Dictionary<string, object?> { ["name"] = name, ["value"] = Math.Max(0, start - 1) }, ct); } catch { /* created concurrently */ }
        }
        return await table.NextNumberAsync(name, "name", "value", ct);
    }

    /// <summary>Takes <paramref name="qty"/> out of a product's stock, atomically; false when there is not enough. Products without stock tracking always succeed.</summary>
    public async Task<bool> TakeStockAsync(Product p, Variant? v, int qty, CancellationToken ct = default)
    {
        if (v is not null && v.TrackStock) return await T("variants").IncrementAsync(v.Id, "stock", -qty, 0, ct) is not null;
        if (p.TrackStock) return await T("products").IncrementAsync(p.Id, "stock", -qty, 0, ct) is not null;
        return true;
    }

    /// <summary>Puts stock back (a cancelled order). Reads what was tracked at that moment, so a product that stopped tracking is left alone.</summary>
    public async Task ReturnStockAsync(long productId, long variantId, int qty, CancellationToken ct = default)
    {
        if (variantId > 0 && await VariantAsync(variantId, ct) is { TrackStock: true }) { await T("variants").IncrementAsync(variantId, "stock", qty, null, ct); return; }
        if (await ProductAsync(productId, ct) is { TrackStock: true }) await T("products").IncrementAsync(productId, "stock", qty, null, ct);
    }

    public async Task<IReadOnlyList<Product>> LowStockAsync(int threshold, CancellationToken ct = default) =>
        (await T("products").QueryAsync(DataQuery.Everything.Eq("active", true).Eq("track_stock", true).Where("stock", DataOp.Le, (long)threshold).OrderBy("stock"), ct)).Select(ProductOf).ToList();

    // ---- customers (derived from orders) -------------------------------------------------------------------

    public sealed record CustomerRow(string Email, string Name, long MemberId, int Orders, long SpentCents, DateTime LastOrderUtc);

    /// <summary>The people who ordered, one row per address, newest first. Derived from the orders — there is no separate customer table to keep in step.</summary>
    public async Task<IReadOnlyList<CustomerRow>> CustomersAsync(CancellationToken ct = default)
    {
        var all = await T("orders").QueryAsync(DataQuery.Everything.OrderBy("created", descending: true), ct);
        return all.GroupBy(r => r.Text("email").ToLowerInvariant()).Where(g => g.Key.Length > 0).Select(g =>
        {
            var latest = g.First();
            var paid = g.Where(r => OrderStatus.IsPaid(r.Text("status")));
            return new CustomerRow(g.Key, latest.Text("name"), g.Select(r => r.Int("member_id")).FirstOrDefault(m => m > 0), g.Count(),
                paid.Sum(r => r.Int("total") - r.Int("refunded")), latest.Timestamp("created") ?? DateTime.UtcNow);
        }).OrderByDescending(c => c.LastOrderUtc).ToList();
    }

    public async Task<IReadOnlyList<Order>> OrdersOfMemberAsync(long memberId, string email, CancellationToken ct = default) =>
        (await OrdersAsync(memberId: memberId, email: email, ct: ct)).Items;
}
