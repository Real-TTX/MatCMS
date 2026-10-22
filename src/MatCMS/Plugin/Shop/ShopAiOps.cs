using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Services;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The catalogue operations the site's AI assistant (and the cloud, MCP) needs besides "write a product": find, read and change products, list
/// and write categories — plus the descriptions of the operations the shop had before. Writes go through the same <see cref="ShopStore"/> calls as the
/// admin's forms, prices are whole cents, an AI's description HTML is sanitised, and every write answers with its <c>undo</c>. A product is never
/// deleted from here (orders and variants hang off it): it is switched off with <c>active: false</c>.
/// </summary>
internal static class ShopAiOps
{
    // ---- descriptions -------------------------------------------------------------------------------------------

    public static readonly PluginOperationSpec ProductList = new("product.list",
        "Produkte finden: id, Adresse, Titel, Preis (Cent, brutto), Kategorie, Schlagwörter, aktiv, Lager.",
        """{"search"?: "text", "category"?: "kategorie-slug", "tag"?: "…", "includeInactive"?: false, "limit"?: 40}""", Writes: false);
    public static readonly PluginOperationSpec ProductRead = new("product.read",
        "Ein Produkt vollständig lesen (Texte, Preise, Bilder, Varianten).", """{"product": "slug"} oder {"id": 12}""", Writes: false);
    public static readonly PluginOperationSpec ProductCreate = new("product.create",
        "Neues Produkt anlegen. Preis brutto: price_cents (Ganzzahl) oder price (\"19,90\"). Eine neue Kategorie wird mit angelegt. image: url aus der Mediathek. description_html: Absätze, Listen, Überschriften.",
        """{"title": "…", "price_cents": 1990, "slug"?, "summary"?, "description_html"?, "image"?: "/uploads/…", "category"?: "Kaffee", "tags"?: "A, B", "stock"?: 10, "tax_percent"?: 19, "compare_price"?: "24,90", "featured"?: false, "kind"?: "physical|digital", "sku"?, "publish"?: true}""");
    public static readonly PluginOperationSpec ProductUpdate = new("product.update",
        "Ein Produkt ändern; nicht genannte Felder bleiben. active: false nimmt es aus dem Shop (gelöscht wird nie).",
        """{"product": "slug" oder "id": 12, "title"?, "summary"?, "description_html"?, "price_cents"?|"price"?, "compare_cents"?|"compare_price"?, "image"?, "gallery"?: ["/uploads/…"], "category"?: "slug", "tags"?, "featured"?, "stock"?: 5|null, "sku"?, "tax_percent"?, "seo_title"?, "seo_description"?, "active"?}""");
    public static readonly PluginOperationSpec CategoryList = new("category.list",
        "Alle Shop-Kategorien als Baum (id, slug, Name, Eltern-id, Bild, Text).", "{}", Writes: false);
    public static readonly PluginOperationSpec CategoryCreate = new("category.create",
        "Neue Kategorie anlegen (parent = slug der übergeordneten, leer = oberste Ebene).",
        """{"name": "…", "slug"?, "parent"?: "slug", "image"?: "/uploads/…", "description"?: "kurzer Text", "tags"?: "…"}""");
    public static readonly PluginOperationSpec CategoryUpdate = new("category.update",
        "Kategorie ändern (Name, Bild, Text, Eltern, Schlagwörter; die Adresse bleibt).",
        """{"category": "slug", "name"?, "parent"?: "slug"|"", "image"?, "description"?, "tags"?}""");
    public static readonly PluginOperationSpec CouponCreate = new("coupon.create",
        "Gutschein anlegen: kind percent (percent 0–100) oder fixed (amount_cents), optional Mindestwert, Kategorie, Ablaufdatum.",
        """{"code"?: "SOMMER10", "kind": "percent|fixed|shipping", "percent"?: 10, "amount_cents"?: 500, "min_order_cents"?: 2000, "category"?: "slug", "expires"?: "2026-12-31", "max_uses"?: 100, "note"?}""");
    public static readonly PluginOperationSpec VariantSet = new("variant.set",
        "Varianten eines Produkts setzen (Größe, Farbe …): gleicher Name = aktualisiert, neuer = angelegt; replace schaltet fehlende ab.",
        """{"product": "slug", "variants": [{"name": "S / Rot", "delta_cents"?: 0, "stock"?: 5, "sku"?, "image"?}], "options"?: {"Größe": ["S","M"], "Farbe": ["Rot"]}, "replace"?: false}""");
    public static readonly PluginOperationSpec AttributeCreate = new("attribute.create",
        "Neue Produkt-Eigenschaft (für Filter und Tabelle): kind choice|text|number|bool.",
        """{"name": "Farbe", "slug"?, "kind": "choice", "options"?: ["Rot","Blau"], "unit"?, "filterable"?: true, "category"?: "slug"}""");
    public static readonly PluginOperationSpec ProductAttributes = new("product.attributes",
        "Einem Produkt Eigenschaftswerte geben (Eigenschafts-slug → Wert; Auswahl darf eine Liste sein).",
        """{"product": "slug", "values": {"farbe": ["Rot"], "gewicht": 1.5}}""");
    public static readonly PluginOperationSpec OrderList = new("order.list", "Bestellungen lesen.", """{"status"?, "number"?, "since"?, "limit"?}""", Writes: false, PersonalData: true);
    public static readonly PluginOperationSpec OrderUpdate = new("order.update", "Bestellung weiterschalten.", """{"number", "action"}""", PersonalData: true);

    public static readonly PluginOperationSpec ProductRestore = new("product.restore", "Stellt ein Produkt aus einer Rückgängig-Aufzeichnung wieder her.", Internal: true);
    public static readonly PluginOperationSpec ProductRemove = new("product.remove", "Entfernt ein eben angelegtes Produkt (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec CategoryRestore = new("category.restore", "Stellt eine Kategorie wieder her (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec CategoryRemove = new("category.remove", "Entfernt eine eben angelegte Kategorie (Rückgängig).", Internal: true);

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static JsonObject Answer(string status, string message, JsonNode? data = null, JsonNode? undo = null)
    {
        var o = new JsonObject { ["status"] = status, ["message"] = message };
        if (data is not null) o["data"] = data;
        if (undo is not null) o["undo"] = undo;
        return o;
    }

    private static JsonObject Undo(string name, JsonNode args) => new() { ["name"] = name, ["args"] = args };
    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : null;
    private static bool? Flag(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static bool Whole(JsonNode? n, out long value)
    {
        value = 0;
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<long>(out value)) return true;
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        return v.TryGetValue<double>(out var d) && d == Math.Floor(d) && (value = (long)d) == d;
    }

    private static long? Cents(JsonObject o, string centsKey, string moneyKey)
    {
        if (Whole(o[centsKey], out var c)) return c;
        return o[moneyKey] is { } m && Money.TryParse(m.ToString(), out var parsed) ? parsed : null;
    }

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    private static async Task<Product?> FindProductAsync(ShopStore store, JsonObject o, CancellationToken ct)
    {
        if (Whole(o["id"], out var id)) return await store.ProductAsync(id, ct);
        var key = (Str(o, "product") ?? Str(o, "slug") ?? "").Trim();
        if (long.TryParse(key, out id)) return await store.ProductAsync(id, ct);
        return key.Length > 0 ? await store.ProductBySlugAsync(key, ct) : null;
    }

    private static JsonObject Short(Product p) => new()
    {
        ["id"] = p.Id, ["slug"] = p.Slug, ["url"] = ShopUrls.Product(p.Slug), ["title"] = p.Title, ["priceCents"] = p.PriceCents, ["category"] = p.Category,
        ["tags"] = p.Tags, ["active"] = p.Active, ["stock"] = p.TrackStock ? p.Stock : null, ["image"] = p.Image, ["featured"] = p.Featured,
    };

    // ---- products -----------------------------------------------------------------------------------------------

    public static async Task<JsonNode?> ListProductsAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        var limit = Whole(o["limit"], out var l) ? (int)Math.Clamp(l, 1, 200) : 40;
        var (items, total) = await new ShopStore(ctx.Data).FindProductsAsync(new ProductQuery
        {
            ActiveOnly = !(Flag(o, "includeInactive") ?? false), Search = Str(o, "search"), Category = Str(o, "category"), Tag = Str(o, "tag"), SearchIn = "all", Take = limit,
        }, ct);
        return Answer("applied", $"{total} Produkte", new JsonArray(items.Select(p => (JsonNode)Short(p)).ToArray()));
    }

    public static async Task<JsonNode?> ReadProductAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        if (await FindProductAsync(store, args as JsonObject ?? new JsonObject(), ct) is not { } p) return Answer("failed", "Produkt nicht gefunden.");
        var data = Short(p);
        data["summary"] = p.Summary; data["descriptionHtml"] = p.DescriptionHtml; data["compareCents"] = p.CompareCents; data["taxPercent"] = p.TaxBp / 100.0;
        data["sku"] = p.Sku; data["gallery"] = JsonNode.Parse(string.IsNullOrWhiteSpace(p.GalleryJson) ? "[]" : p.GalleryJson); data["kind"] = p.Kind;
        data["seoTitle"] = p.SeoTitle; data["seoDescription"] = p.SeoDescription;
        data["variants"] = new JsonArray((await store.VariantsAsync(p.Id, false, ct)).Select(v => (JsonNode)new JsonObject
        {
            ["name"] = v.Name, ["deltaCents"] = v.PriceDeltaCents, ["stock"] = v.TrackStock ? v.Stock : null, ["sku"] = v.Sku, ["active"] = v.Active,
        }).ToArray());
        return Answer("applied", p.Title, data);
    }

    public static async Task<JsonNode?> UpdateProductAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var store = new ShopStore(ctx.Data);
        if (await FindProductAsync(store, o, ct) is not { } p) return Answer("failed", "Produkt nicht gefunden.");
        // The undo writes the stored row back, not the translated view a read may give.
        var before = JsonSerializer.SerializeToNode(p, Json)!;
        var n = p;
        if (Str(o, "title") is { } t) { if (t.Trim().Length == 0) return Answer("failed", "Ein Produkt braucht einen Titel."); n = n with { Title = t.Trim() }; }
        if (Str(o, "summary") is { } su) n = n with { Summary = su.Trim() };
        if ((Str(o, "description_html") ?? Str(o, "descriptionHtml")) is { } d) n = n with { DescriptionHtml = SafeHtml.Sanitize(d) };
        if (Cents(o, "price_cents", "price") is { } price) { if (price < 0) return Answer("failed", "Der Preis darf nicht negativ sein."); n = n with { PriceCents = price }; }
        if (Cents(o, "compare_cents", "compare_price") is { } cmp) n = n with { CompareCents = Math.Max(0, cmp) };
        if (o.ContainsKey("image")) n = n with { Image = ShopPublic.SafeUrl(Str(o, "image")) };
        if (o["gallery"] is JsonArray g) n = n with { GalleryJson = new JsonArray(g.Select(x => ShopPublic.SafeUrl(x?.ToString())).Where(x => x.Length > 0).Take(30).Select(x => (JsonNode)x).ToArray()).ToJsonString() };
        if (Str(o, "category") is { } cat)
        {
            var slug = cat.Trim();
            if (slug.Length > 0 && await store.CategoryBySlugAsync(slug, ct) is null) return Answer("failed", $"Die Kategorie „{slug}“ gibt es nicht – erst category.create.");
            n = n with { Category = slug };
        }
        if (o["tags"] is JsonArray ta) n = n with { Tags = ShopTags.Normalise(string.Join(", ", ta.Select(x => x?.ToString() ?? ""))) };
        else if (Str(o, "tags") is { } tags) n = n with { Tags = ShopTags.Normalise(tags) };
        if (Flag(o, "featured") is { } f) n = n with { Featured = f };
        if ((Flag(o, "active") ?? Flag(o, "publish")) is { } a) n = n with { Active = a };
        if (o.ContainsKey("stock")) n = Whole(o["stock"], out var st) && st >= 0 ? n with { TrackStock = true, Stock = st } : n with { TrackStock = false };
        if (Str(o, "sku") is { } sku) n = n with { Sku = sku.Trim() };
        if (o["tax_percent"] is { } tp && Money.TryParsePercent(tp.ToString(), out var bp)) n = n with { TaxBp = bp };
        if (Str(o, "seo_title") is { } stt) n = n with { SeoTitle = stt.Trim() };
        if (Str(o, "seo_description") is { } sd) n = n with { SeoDescription = sd.Trim() };
        await store.SaveProductAsync(n with { UpdatedUtc = DateTime.UtcNow }, ct);
        return Answer("applied", $"Produkt „{n.Title}“ geändert.", Short(n), Undo("product.restore", before));
    }

    public static async Task<JsonNode?> RestoreProductAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var p = args.Deserialize<Product>(Json) ?? throw new InvalidOperationException("Kein Produkt.");
        var store = new ShopStore(ctx.Data);
        if (await store.ProductAsync(p.Id, ct) is null) return Answer("failed", "Das Produkt gibt es nicht mehr.");
        await store.SaveProductAsync(p, ct);
        return Answer("applied", $"Produkt „{p.Title}“ wiederhergestellt.");
    }

    public static async Task<JsonNode?> RemoveProductAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        if (!Whole(o["id"], out var id)) return Answer("failed", "id fehlt.");
        await new ShopStore(ctx.Data).DeleteProductAsync(id, ct);
        return Answer("applied", "Produkt entfernt.");
    }

    // ---- categories ---------------------------------------------------------------------------------------------

    private static JsonObject CategoryJson(Category c) => new()
    {
        ["id"] = c.Id, ["slug"] = c.Slug, ["name"] = c.Name, ["parentId"] = c.ParentId, ["url"] = ShopUrls.Category(c.Slug), ["image"] = c.Image,
        ["description"] = c.Description, ["tags"] = c.Tags,
    };

    public static async Task<JsonNode?> ListCategoriesAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var all = await new ShopStore(ctx.Data).CategoriesAsync(ct);
        return Answer("applied", $"{all.Count} Kategorien", new JsonArray(all.Select(c => (JsonNode)CategoryJson(c)).ToArray()));
    }

    public static async Task<JsonNode?> CreateCategoryAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var name = (Str(o, "name") ?? "").Trim();
        if (name.Length == 0) return Answer("failed", "Eine Kategorie braucht einen Namen.");
        var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(Str(o, "slug") ?? name);
        if (slug.Length == 0) return Answer("failed", "Keine gültige Adresse.");
        var store = new ShopStore(ctx.Data);
        if (await store.CategoryBySlugAsync(slug, ct) is not null) return Answer("skipped-exists", $"Die Kategorie „{slug}“ gibt es schon.");
        long parent = 0;
        if ((Str(o, "parent") ?? "").Trim() is { Length: > 0 } ps)
            parent = (await store.CategoryBySlugAsync(ps, ct))?.Id ?? throw new InvalidOperationException($"Die Kategorie „{ps}“ gibt es nicht.");
        var id = await store.SaveCategoryAsync(new Category
        {
            Slug = slug, Name = name, ParentId = parent, Image = ShopPublic.SafeUrl(Str(o, "image")), Description = (Str(o, "description") ?? "").Trim(),
            Tags = Str(o, "tags") ?? "", Sort = (await store.CategoriesAsync(ct)).Count,
        }, ct);
        return Answer("applied", $"Kategorie „{name}“ angelegt ({ShopUrls.Category(slug)}).", new JsonObject { ["id"] = id, ["slug"] = slug },
            Undo("category.remove", new JsonObject { ["id"] = id }));
    }

    public static async Task<JsonNode?> UpdateCategoryAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var store = new ShopStore(ctx.Data);
        var key = (Str(o, "category") ?? Str(o, "slug") ?? "").Trim();
        if (await store.CategoryBySlugAsync(key, ct) is not { } c) return Answer("failed", $"Die Kategorie „{key}“ gibt es nicht.");
        var before = JsonSerializer.SerializeToNode(c, Json)!;
        var n = c;
        if (Str(o, "name") is { } name && name.Trim().Length > 0) n = n with { Name = name.Trim() };
        if (o.ContainsKey("image")) n = n with { Image = ShopPublic.SafeUrl(Str(o, "image")) };
        if (Str(o, "description") is { } d) n = n with { Description = d.Trim() };
        if (Str(o, "tags") is { } t) n = n with { Tags = t };
        if (Str(o, "parent") is { } ps)
            n = n with { ParentId = ps.Trim().Length == 0 ? 0 : (await store.CategoryBySlugAsync(ps.Trim(), ct))?.Id ?? throw new InvalidOperationException($"Die Kategorie „{ps}“ gibt es nicht.") };
        await store.SaveCategoryAsync(n, ct);
        return Answer("applied", $"Kategorie „{n.Name}“ geändert.", CategoryJson(n), Undo("category.restore", before));
    }

    public static async Task<JsonNode?> RestoreCategoryAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var c = args.Deserialize<Category>(Json) ?? throw new InvalidOperationException("Keine Kategorie.");
        await new ShopStore(ctx.Data).SaveCategoryAsync(c, ct);
        return Answer("applied", $"Kategorie „{c.Name}“ wiederhergestellt.");
    }

    public static async Task<JsonNode?> RemoveCategoryAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        if (!Whole(o["id"], out var id)) return Answer("failed", "id fehlt.");
        await new ShopStore(ctx.Data).DeleteCategoryAsync(id, ct);
        return Answer("applied", "Kategorie entfernt.");
    }
}
