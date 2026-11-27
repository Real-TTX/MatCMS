using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The rest of what an operator manages in the shop, as described operations for the site's AI assistant, the cloud's MCP and the site's own MCP server:
/// coupons, attributes and shipping rates (list, change, switch off) — and the undo of the writes that had none (<c>coupon.create</c>, <c>variant.set</c>,
/// <c>attribute.create</c>, <c>product.attributes</c>). The rules are the forms' (<see cref="ShopAdminCoupons"/>, <see cref="ShopAdminAttributes"/>, the
/// shipping tab); every write answers with an <c>undo</c> naming one of the internal operations below, which write the stored row back.
/// Nothing here carries customers' data: a coupon, an attribute and a shipping rate are the operator's own.
/// </summary>
internal static class ShopOpsMore
{
    // ---- descriptions -------------------------------------------------------------------------------------------

    public static readonly PluginOperationSpec CouponList = new("coupon.list",
        "Gutscheine lesen: Code, Art (percent|fixed|shipping), Wert (percent bzw. amountCents), Mindestwert, Kategorie, gültig von/bis, Nutzungen, aktiv, Notiz.",
        """{"search"?: "SOMMER", "activeOnly"?: false}""", Writes: false);
    public static readonly PluginOperationSpec CouponUpdate = new("coupon.update",
        "Einen Gutschein ändern (gefunden über code); nicht genannte Felder bleiben. active: false schaltet ihn ab (gelöscht wird nie). expires \"\" = ohne Ablauf, category \"\" = alle Waren.",
        """{"code": "SOMMER10", "kind"?: "percent|fixed|shipping", "percent"?: 15, "amount_cents"?: 500, "min_order_cents"?: 2000, "category"?: "slug", "expires"?: "2026-12-31", "max_uses"?: 100, "once_per_customer"?: false, "note"?, "active"?: true}""");
    public static readonly PluginOperationSpec AttributeList = new("attribute.list",
        "Alle Produkt-Eigenschaften (für Filter und Tabelle): slug, Name, Art (choice|text|number|bool), Auswahlwerte, Einheit, filterbar, Kategorie, Reihenfolge, Anzahl Produkte.", "{}", Writes: false);
    public static readonly PluginOperationSpec AttributeUpdate = new("attribute.update",
        "Eine Eigenschaft ändern (gefunden über attribute = slug; die Adresse bleibt). Nicht genannte Felder bleiben; eine Auswahl braucht options.",
        """{"attribute": "farbe", "name"?, "kind"?: "choice", "options"?: ["Rot","Blau"], "unit"?: "kg", "filterable"?: true, "category"?: "slug"|"", "sort"?: 10}""");
    public static readonly PluginOperationSpec ShippingList = new("shipping.list",
        "Alle Versandarten: id, Name, Länder (Kürzel, * = alle), Preis (Cent), versandkostenfrei ab (Cent, 0 = nie), Höchstgewicht (Gramm, 0 = ohne), Abholung, aktiv.", "{}", Writes: false);
    public static readonly PluginOperationSpec ShippingSave = new("shipping.save",
        "Versandart anlegen (ohne id) oder ändern (mit id); nicht genannte Felder bleiben. Länder als Kürzel mit zwei Buchstaben (\"DE,AT\") oder \"*\". active: false schaltet sie ab.",
        """{"id"?: 3, "name": "DHL Paket", "countries"?: "DE,AT", "price_cents"?: 490, "free_from_cents"?: 5000, "max_weight_grams"?: 31500, "pickup"?: false, "active"?: true}""");

    public static readonly PluginOperationSpec CouponRestore = new("coupon.restore", "Stellt einen Gutschein wieder her (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec CouponRemove = new("coupon.remove", "Entfernt einen eben angelegten Gutschein (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec AttributeRestore = new("attribute.restore", "Stellt eine Eigenschaft wieder her (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec AttributeRemove = new("attribute.remove", "Entfernt eine eben angelegte Eigenschaft (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec ProductAttributesRestore = new("product.attributes.restore", "Gibt einem Produkt seine früheren Eigenschaftswerte zurück (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec VariantRestore = new("variant.restore", "Gibt einem Produkt seine früheren Varianten zurück (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec ShippingRestore = new("shipping.restore", "Stellt eine Versandart wieder her (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec ShippingRemove = new("shipping.remove", "Entfernt eine eben angelegte Versandart (Rückgängig).", Internal: true);

    public static void Register(IPluginBuilder b)
    {
        b.Operation(CouponList, ListCouponsAsync);
        b.Operation(CouponUpdate, UpdateCouponAsync);
        b.Operation(AttributeList, ListAttributesAsync);
        b.Operation(AttributeUpdate, UpdateAttributeAsync);
        b.Operation(ShippingList, ListRatesAsync);
        b.Operation(ShippingSave, SaveRateAsync);
        b.Operation(CouponRestore, RestoreCouponAsync);
        b.Operation(CouponRemove, RemoveCouponAsync);
        b.Operation(AttributeRestore, RestoreAttributeAsync);
        b.Operation(AttributeRemove, RemoveAttributeAsync);
        b.Operation(ProductAttributesRestore, RestoreProductAttributesAsync);
        b.Operation(VariantRestore, RestoreVariantsAsync);
        b.Operation(ShippingRestore, RestoreRateAsync);
        b.Operation(ShippingRemove, RemoveRateAsync);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    internal static JsonObject Answer(string status, string message, JsonNode? data = null, JsonNode? undo = null)
    {
        var o = new JsonObject { ["status"] = status, ["message"] = message };
        if (data is not null) o["data"] = data;
        if (undo is not null) o["undo"] = undo;
        return o;
    }

    internal static JsonObject Undo(string name, JsonNode args) => new() { ["name"] = name, ["args"] = args };
    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : null;
    private static bool? Flag(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static bool Whole(JsonNode? n, out long value)
    {
        value = 0;
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<long>(out value)) return true;
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d)) { value = (long)d; return true; }
        return v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static long? Cents(JsonObject o, string centsKey, string moneyKey)
    {
        if (Whole(o[centsKey], out var c)) return c;
        return o[moneyKey] is { } m && Money.TryParse(m.ToString(), out var parsed) ? parsed : null;
    }

    private static string Day(DateTime? utc) => utc is null ? "" : utc.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---- coupons ------------------------------------------------------------------------------------------------

    private static JsonObject CouponJson(Coupon c) => new()
    {
        ["id"] = c.Id, ["code"] = c.Code, ["kind"] = c.Kind,
        ["percent"] = c.Kind == CouponKind.Percent ? c.Value / 100.0 : null, ["amountCents"] = c.Kind == CouponKind.Fixed ? c.Value : null,
        ["minOrderCents"] = c.MinOrderCents, ["category"] = c.Category, ["starts"] = Day(c.StartsUtc), ["expires"] = Day(c.ExpiresUtc),
        ["maxUses"] = c.MaxUses, ["used"] = c.Used, ["oncePerCustomer"] = c.OncePerCustomer, ["active"] = c.Active, ["note"] = c.Note,
    };

    public static async Task<JsonNode?> ListCouponsAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        var search = (Str(o, "search") ?? "").Trim();
        var activeOnly = Flag(o, "activeOnly") ?? false;
        var all = (await new ShopStore(ctx.Data).CouponsAsync(ct))
            .Where(c => !activeOnly || c.Active)
            .Where(c => search.Length == 0 || c.Code.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Note.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Answer("applied", $"{all.Count} Gutschein(e)", new JsonArray(all.Select(c => (JsonNode)CouponJson(c)).ToArray()));
    }

    public static async Task<JsonNode?> UpdateCouponAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var store = new ShopStore(ctx.Data);
        var code = Coupon.NormaliseCode(Str(o, "code"));
        if (code.Length == 0 || await store.CouponByCodeAsync(code, ct) is not { } c) return Answer("failed", $"Keinen Gutschein „{Str(o, "code")}“ gefunden.");
        var before = JsonSerializer.SerializeToNode(c, ShopJson.Default.Coupon)!;
        var n = c;
        if (Str(o, "kind") is { Length: > 0 } k)
        {
            if (!CouponKind.All.Contains(k)) return Answer("failed", "kind muss percent, fixed oder shipping sein.");
            n = n with { Kind = k, Value = k == c.Kind ? c.Value : 0 };
        }
        if (o["percent"] is { } pct)
        {
            if (!Money.TryParsePercent(pct.ToString(), out var bp) || bp <= 0) return Answer("failed", "\"percent\" muss zwischen 0 und 100 liegen.");
            n = n with { Value = bp };
        }
        if (Cents(o, "amount_cents", "amount") is { } amount)
        {
            if (amount <= 0) return Answer("failed", "Der Betrag muss größer als 0 sein.");
            n = n with { Value = amount };
        }
        if (n.Kind != CouponKind.Shipping && n.Value <= 0)
            return Answer("failed", n.Kind == CouponKind.Percent ? "Ein Prozent-Gutschein braucht \"percent\"." : "Ein Festbetrag-Gutschein braucht \"amount_cents\".");
        if (n.Kind == CouponKind.Shipping) n = n with { Value = 0 };
        if (Cents(o, "min_order_cents", "min_order") is { } min) n = n with { MinOrderCents = Math.Max(0, min) };
        if (Str(o, "category") is { } cat)
        {
            var slug = cat.Trim().Length == 0 ? "" : PluginText.Slugify(cat);
            if (slug.Length > 0 && await store.CategoryBySlugAsync(slug, ct) is null) return Answer("failed", $"Die Kategorie „{cat.Trim()}“ gibt es nicht.");
            n = n with { Category = slug };
        }
        if (Str(o, "expires") is { } ex)
        {
            if (ex.Trim().Length == 0) n = n with { ExpiresUtc = null };
            else if (!DateTime.TryParseExact(ex.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return Answer("failed", "\"expires\" muss ein Datum der Form JJJJ-MM-TT sein.");
            else n = n with { ExpiresUtc = DateTime.SpecifyKind(d.AddDays(1).AddSeconds(-1), DateTimeKind.Local).ToUniversalTime() };
        }
        if (o.ContainsKey("max_uses")) n = n with { MaxUses = Whole(o["max_uses"], out var mu) && mu > 0 ? (int)Math.Min(mu, int.MaxValue) : 0 };
        if (Flag(o, "once_per_customer") is { } once) n = n with { OncePerCustomer = once };
        if (Str(o, "note") is { } note) n = n with { Note = note.Trim() };
        if (Flag(o, "active") is { } a) n = n with { Active = a };
        var (ok, _) = await store.SaveCouponAsync(n, ct);
        if (!ok) return Answer("failed", $"Der Gutschein {n.Code} ließ sich nicht speichern.");
        return Answer("applied", $"Gutschein {n.Code} geändert.", CouponJson(n), Undo("coupon.restore", before));
    }

    public static async Task<JsonNode?> RestoreCouponAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var c = args.Deserialize(ShopJson.Default.Coupon) ?? throw new InvalidOperationException("Kein Gutschein.");
        var store = new ShopStore(ctx.Data);
        if (await store.CouponAsync(c.Id, ct) is null) return Answer("failed", "Den Gutschein gibt es nicht mehr.");
        var (ok, _) = await store.SaveCouponAsync(c, ct);
        return ok ? Answer("applied", $"Gutschein {c.Code} wiederhergestellt.") : Answer("failed", $"Der Code {c.Code} ist inzwischen vergeben.");
    }

    /// <summary>The undo of a coupon made a moment ago: deleted while nobody used it; a coupon an order already carries is only switched off.</summary>
    public static async Task<JsonNode?> RemoveCouponAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        if (!Whole(o["id"], out var id)) return Answer("failed", "id fehlt.");
        var store = new ShopStore(ctx.Data);
        if (await store.CouponAsync(id, ct) is not { } c) return Answer("applied", "Den Gutschein gibt es schon nicht mehr.");
        if (c.Used > 0 || await store.OrdersWithCouponAsync(c.Code, ct) > 0)
        {
            await store.SaveCouponAsync(c with { Active = false }, ct);
            return Answer("applied", $"Gutschein {c.Code} abgeschaltet – er wurde schon benutzt.");
        }
        await store.DeleteCouponAsync(id, ct);
        return Answer("applied", $"Gutschein {c.Code} entfernt.");
    }

    // ---- attributes ---------------------------------------------------------------------------------------------

    private static JsonObject AttributeJson(ShopAttribute a, int products) => new()
    {
        ["id"] = a.Id, ["slug"] = a.Slug, ["name"] = a.Name, ["kind"] = a.Kind, ["options"] = new JsonArray(a.OptionList.Select(x => (JsonNode)x).ToArray()),
        ["unit"] = a.Unit, ["filterable"] = a.Filterable, ["category"] = a.Category, ["sort"] = a.Sort, ["products"] = products,
    };

    public static async Task<JsonNode?> ListAttributesAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var all = await store.AttributesAsync(ct);
        var use = await store.AttrUseAsync(ct);
        return Answer("applied", $"{all.Count} Eigenschaft(en)", new JsonArray(all.Select(a => (JsonNode)AttributeJson(a, use.GetValueOrDefault(a.Id))).ToArray()));
    }

    public static async Task<JsonNode?> UpdateAttributeAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var store = new ShopStore(ctx.Data);
        var key = PluginText.Slugify(Str(o, "attribute") ?? Str(o, "slug") ?? "");
        if (key.Length == 0 || await store.AttributeBySlugAsync(key, ct) is not { } a) return Answer("failed", $"Die Eigenschaft „{Str(o, "attribute")}“ gibt es nicht.");
        var before = JsonSerializer.SerializeToNode(a, ShopJson.Default.ShopAttribute)!;
        var n = a;
        if (Str(o, "name") is { } name)
        {
            var clean = ShopAttributes.CleanValue(name);
            if (clean.Length == 0) return Answer("failed", "Eine Eigenschaft braucht einen Namen.");
            n = n with { Name = clean.Length > 80 ? clean[..80] : clean };
        }
        if (Str(o, "kind") is { Length: > 0 } kind) n = n with { Kind = AttributeKind.Normalise(kind) };
        if (o["options"] is JsonArray oa) n = n with { Options = ShopAttributes.NormaliseOptions(string.Join("\n", oa.Select(x => x?.ToString() ?? ""))) };
        else if (Str(o, "options") is { } os) n = n with { Options = ShopAttributes.NormaliseOptions(os) };
        if (n.Kind == AttributeKind.Choice && ShopAttributes.SplitOptions(n.Options).Count == 0) return Answer("failed", "Eine Auswahl-Eigenschaft braucht options.");
        if (Str(o, "unit") is { } unit) n = n with { Unit = ShopAttributes.CleanValue(unit) };
        if (Flag(o, "filterable") is { } f) n = n with { Filterable = f };
        if (Str(o, "category") is { } cat)
        {
            var slug = cat.Trim().Length == 0 ? "" : PluginText.Slugify(cat);
            if (slug.Length > 0 && await store.CategoryBySlugAsync(slug, ct) is null) return Answer("failed", $"Die Kategorie „{cat.Trim()}“ gibt es nicht.");
            n = n with { Category = slug };
        }
        if (Whole(o["sort"], out var sort)) n = n with { Sort = (int)Math.Clamp(sort, 0, 100000) };
        await store.SaveAttributeAsync(n, ct);
        return Answer("applied", $"Eigenschaft „{n.Name}“ geändert.", AttributeJson(n, (await store.AttrUseAsync(ct)).GetValueOrDefault(n.Id)), Undo("attribute.restore", before));
    }

    public static async Task<JsonNode?> RestoreAttributeAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var a = args.Deserialize(ShopJson.Default.ShopAttribute) ?? throw new InvalidOperationException("Keine Eigenschaft.");
        var store = new ShopStore(ctx.Data);
        if (await store.AttributeAsync(a.Id, ct) is null) return Answer("failed", "Die Eigenschaft gibt es nicht mehr.");
        await store.SaveAttributeAsync(a, ct);
        return Answer("applied", $"Eigenschaft „{a.Name}“ wiederhergestellt.");
    }

    /// <summary>The undo of an attribute made a moment ago (with whatever values products got of it since).</summary>
    public static async Task<JsonNode?> RemoveAttributeAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        if (!Whole(o["id"], out var id)) return Answer("failed", "id fehlt.");
        await new ShopStore(ctx.Data).DeleteAttributeAsync(id, ct);
        return Answer("applied", "Eigenschaft entfernt.");
    }

    /// <summary>What <c>product.attributes</c> needs to be undone: the product's values of the attributes it is about to touch, by attribute id.</summary>
    internal static async Task<JsonObject> ProductValuesUndoAsync(ShopStore store, long productId, IEnumerable<long> attrIds, CancellationToken ct)
    {
        var now = await store.ProductValuesAsync(productId, ct);
        var values = new JsonObject();
        foreach (var id in attrIds.Distinct())
            values[id.ToString(CultureInfo.InvariantCulture)] = new JsonArray((now.TryGetValue(id, out var list) ? list : new List<string>()).Select(v => (JsonNode)v).ToArray());
        return Undo("product.attributes.restore", new JsonObject { ["productId"] = productId, ["values"] = values });
    }

    public static async Task<JsonNode?> RestoreProductAttributesAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        if (!Whole(o["productId"], out var productId) || o["values"] is not JsonObject values) return Answer("failed", "productId oder values fehlt.");
        var store = new ShopStore(ctx.Data);
        if (await store.ProductAsync(productId, ct) is null) return Answer("failed", "Das Produkt gibt es nicht mehr.");
        foreach (var (key, node) in values)
        {
            if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var attrId) || await store.AttributeAsync(attrId, ct) is null) continue;
            var list = node is JsonArray arr ? arr.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
            await store.SetProductAttrAsync(productId, attrId, list, ct);
        }
        return Answer("applied", "Eigenschaftswerte wiederhergestellt.");
    }

    // ---- variants -----------------------------------------------------------------------------------------------

    /// <summary>What <c>variant.set</c> needs to be undone: the product's variants and option list as they are before it writes.</summary>
    internal static async Task<JsonObject> VariantsBeforeAsync(ShopStore store, Product product, CancellationToken ct) => new()
    {
        ["productId"] = product.Id, ["options"] = product.VariantOptions,
        ["variants"] = new JsonArray((await store.VariantsAsync(product.Id, false, ct)).Select(v => (JsonNode)JsonSerializer.SerializeToNode(v, ShopJson.Default.Variant)!).ToArray()),
    };

    /// <summary>
    /// Gives a product its variants back: every variant it had is written as it was; a variant made since is deleted — unless an order already names it,
    /// then it is only switched off (an order's history keeps its source, as everywhere in the shop).
    /// </summary>
    public static async Task<JsonNode?> RestoreVariantsAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        if (!Whole(o["productId"], out var productId) || o["variants"] is not JsonArray list) return Answer("failed", "productId oder variants fehlt.");
        var store = new ShopStore(ctx.Data);
        if (await store.ProductAsync(productId, ct) is not { } product) return Answer("failed", "Das Produkt gibt es nicht mehr.");
        var old = list.Select(n => n.Deserialize(ShopJson.Default.Variant)).Where(v => v is not null && v.ProductId == productId).Select(v => v!).ToList();
        foreach (var v in old) await store.SaveVariantAsync(v, ct);
        foreach (var v in (await store.VariantsAsync(productId, false, ct)).Where(v => old.All(x => x.Id != v.Id)))
        {
            if (await ctx.Data.Table("order_items").CountAsync(DataQuery.Everything.Eq("variant_id", v.Id), ct) > 0) await store.SaveVariantAsync(v with { Active = false }, ct);
            else await store.DeleteVariantAsync(v.Id, ct);
        }
        if (Str(o, "options") is { } options) await store.SaveProductAsync(product with { VariantOptions = options }, ct);
        return Answer("applied", $"Varianten von „{product.Title}“ wiederhergestellt.");
    }

    // ---- shipping rates -----------------------------------------------------------------------------------------

    private static JsonObject RateJson(ShippingRate r) => new()
    {
        ["id"] = r.Id, ["name"] = r.Name, ["countries"] = r.Countries, ["priceCents"] = r.PriceCents, ["freeFromCents"] = r.FreeFromCents,
        ["maxWeightGrams"] = r.MaxWeightGrams, ["pickup"] = r.Pickup, ["active"] = r.Active, ["sort"] = r.Sort,
    };

    public static async Task<JsonNode?> ListRatesAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var all = await new ShopStore(ctx.Data).RatesAsync(false, ct);
        return Answer("applied", $"{all.Count} Versandart(en)", new JsonArray(all.Select(r => (JsonNode)RateJson(r)).ToArray()));
    }

    /// <summary>The countries as the shipping tab stores them: "*" or two-letter codes, upper case, comma-separated. Null when one is no code.</summary>
    private static string? Countries(string text)
    {
        var s = text.Trim();
        if (s.Length == 0 || s == "*") return "*";
        var codes = s.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.ToUpperInvariant()).ToList();
        if (codes.Any(c => c.Length != 2 || !char.IsAsciiLetter(c[0]) || !char.IsAsciiLetter(c[1]))) return null;
        return string.Join(",", codes.Distinct());
    }

    public static async Task<JsonNode?> SaveRateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var store = new ShopStore(ctx.Data);
        ShippingRate? existing = null;
        if (Whole(o["id"], out var id) && id > 0 && (existing = await store.RateAsync(id, ct)) is null) return Answer("failed", $"Keine Versandart mit der id {id}.");
        var r = existing ?? new ShippingRate { Sort = (await store.RatesAsync(false, ct)).Count, Active = true };
        if (Str(o, "name") is { } name) r = r with { Name = name.Trim() };
        if (r.Name.Length == 0) return Answer("failed", "Eine Versandart braucht einen Namen.");
        if (Str(o, "countries") is { } cs)
        {
            if (Countries(cs) is not { } clean) return Answer("failed", "Länderkürzel müssen zwei Buchstaben haben (\"DE,AT\") oder \"*\" sein.");
            r = r with { Countries = clean };
        }
        if (o.ContainsKey("price_cents") || o.ContainsKey("price"))
        {
            if (Cents(o, "price_cents", "price") is not { } price || price < 0) return Answer("failed", "Kein gültiger Preis.");
            r = r with { PriceCents = price };
        }
        else if (existing is null) return Answer("failed", "Eine neue Versandart braucht price_cents.");
        if (o.ContainsKey("free_from_cents") || o.ContainsKey("free_from"))
        {
            var free = Cents(o, "free_from_cents", "free_from");
            if (free is < 0) return Answer("failed", "Kein gültiger Betrag für „versandkostenfrei ab“.");
            r = r with { FreeFromCents = free ?? 0 };
        }
        if (o.ContainsKey("max_weight_grams")) r = r with { MaxWeightGrams = Whole(o["max_weight_grams"], out var w) && w > 0 ? (int)Math.Min(w, int.MaxValue) : 0 };
        if (Flag(o, "pickup") is { } p) r = r with { Pickup = p };
        if (Flag(o, "active") is { } a) r = r with { Active = a };
        if (existing is null)
        {
            var newId = await store.SaveRateAsync(r, ct);
            return Answer("applied", $"Versandart „{r.Name}“ angelegt.", RateJson(r with { Id = newId }), Undo("shipping.remove", new JsonObject { ["id"] = newId }));
        }
        var before = JsonSerializer.SerializeToNode(existing, ShopJson.Default.ShippingRate)!;
        await store.SaveRateAsync(r, ct);
        return Answer("applied", $"Versandart „{r.Name}“ geändert.", RateJson(r), Undo("shipping.restore", before));
    }

    public static async Task<JsonNode?> RestoreRateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var r = args.Deserialize(ShopJson.Default.ShippingRate) ?? throw new InvalidOperationException("Keine Versandart.");
        var store = new ShopStore(ctx.Data);
        if (r.Id <= 0 || await store.RateAsync(r.Id, ct) is null) return Answer("failed", "Die Versandart gibt es nicht mehr.");
        await store.SaveRateAsync(r, ct);
        return Answer("applied", $"Versandart „{r.Name}“ wiederhergestellt.");
    }

    public static async Task<JsonNode?> RemoveRateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        if (!Whole(o["id"], out var id)) return Answer("failed", "id fehlt.");
        await new ShopStore(ctx.Data).DeleteRateAsync(id, ct);
        return Answer("applied", "Versandart entfernt.");
    }
}
