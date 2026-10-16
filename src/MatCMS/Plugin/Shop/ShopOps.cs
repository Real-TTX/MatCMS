using System.Globalization;
using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// What the cloud's agent interface (MCP) can do in a shop beyond writing a product or a coupon: read orders, move an order along, set a product's variants, define attributes and give a product its
/// values. The same rules as the forms — an operation calls the code the admin calls (<see cref="ShopOrders"/>, <see cref="ShopVariants"/>, <see cref="ShopAttributes"/>), never a second implementation.
/// An answer is <c>{ status, message }</c>, and a READ also carries <c>data</c> (the content-op report's result).
/// </summary>
internal static class ShopOps
{
    public const int MaxOrders = 100;

    private static JsonObject Answer(string status, string message, JsonNode? data = null)
    {
        var o = new JsonObject { ["status"] = status, ["message"] = message };
        if (data is not null) o["data"] = data;
        return o;
    }

    private static string Str(JsonObject o, string key) => (o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[key]?.ToString() ?? "").Trim();
    private static bool Flag(JsonObject o, string key, bool def) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;
    /// <summary>A whole number from JSON — parsed text gives a long, a value built in code may be an int.</summary>
    private static bool Whole(JsonNode? n, out long value)
    {
        value = 0;
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<long>(out value)) return true;
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        return false;
    }

    private static long? Cents(JsonObject o, string centsKey, string moneyKey)
    {
        if (Whole(o[centsKey], out var c)) return c;
        return o[moneyKey] is { } m && Money.TryParse(m.ToString(), out var parsed) ? parsed : null;
    }

    // ---- orders ----------------------------------------------------------------------------------------------

    private static JsonObject OrderJson(Order o, IReadOnlyList<OrderItem> items) => new()
    {
        ["number"] = o.Number, ["status"] = o.Status, ["createdUtc"] = o.CreatedUtc.ToString("o", CultureInfo.InvariantCulture), ["paidUtc"] = o.PaidUtc?.ToString("o", CultureInfo.InvariantCulture),
        ["email"] = o.Email, ["name"] = o.Name, ["currency"] = o.Currency, ["subtotalCents"] = o.SubtotalCents, ["discountCents"] = o.DiscountCents, ["shippingCents"] = o.ShippingCents,
        ["taxCents"] = o.TaxCents, ["totalCents"] = o.TotalCents, ["refundedCents"] = o.RefundedCents, ["payment"] = o.PaymentProvider, ["coupon"] = o.CouponCode, ["invoiceNumber"] = o.InvoiceNumber,
        ["carrier"] = o.Carrier, ["tracking"] = o.Tracking, ["note"] = o.Note,
        ["shippingAddress"] = new JsonObject { ["name"] = o.Shipping.Name, ["line1"] = o.Shipping.Line1, ["line2"] = o.Shipping.Line2, ["zip"] = o.Shipping.Zip, ["city"] = o.Shipping.City, ["country"] = o.Shipping.Country },
        ["items"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
        {
            ["title"] = i.Title, ["variant"] = i.VariantTitle, ["sku"] = i.Sku, ["qty"] = i.Qty, ["unitCents"] = i.UnitCents, ["taxBp"] = i.TaxBp, ["lineCents"] = i.LineCents
        }).ToArray())
    };

    /// <summary>Orders, newest first: optionally of one status, since a day, or ONE order by its number. Customers' names and addresses are in it — the caller's key must be allowed to write to the site.</summary>
    public static async Task<JsonNode?> ListOrdersAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        var store = new ShopStore(ctx.Data);
        if (Str(o, "number") is { Length: > 0 } number)
        {
            var one = await store.OrderByNumberAsync(number, ct);
            return one is null ? Answer("failed", $"Keine Bestellung „{number}“.") : Answer("applied", $"Bestellung {one.Number}.", new JsonObject { ["orders"] = new JsonArray(OrderJson(one, await store.ItemsAsync(one.Id, ct))) });
        }
        var status = Str(o, "status");
        if (status.Length > 0 && !OrderStatus.All.Contains(status)) return Answer("failed", $"Unbekannter Status „{status}“. Erlaubt: {string.Join(", ", OrderStatus.All)}.");
        var since = DateTime.MinValue;
        if (Str(o, "since") is { Length: > 0 } s && !DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out since))
            return Answer("failed", "since muss ein Datum yyyy-MM-dd sein.");
        var limit = Whole(o["limit"], out var l) ? (int)Math.Clamp(l, 1, MaxOrders) : 25;
        var (found, total) = await store.OrdersAsync(status.Length > 0 ? status : null, null, take: since == DateTime.MinValue ? limit : 0, ct: ct);
        var picked = found.Where(x => x.CreatedUtc >= since).Take(limit).ToList();
        var list = new JsonArray();
        foreach (var order in picked) list.Add(OrderJson(order, await store.ItemsAsync(order.Id, ct)));
        return Answer("applied", $"{picked.Count} von {total} Bestellung(en).", new JsonObject { ["orders"] = list, ["total"] = total });
    }

    /// <summary>Moves an order along the way the admin does: paid, ship (with carrier and tracking), complete, cancel — or adds a note. A move the order cannot make is refused with the reason, never forced.</summary>
    public static async Task<JsonNode?> UpdateOrderAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var store = new ShopStore(ctx.Data);
        var cfg = ShopConfig.From(ctx.Settings);
        var number = Str(o, "number");
        if (await store.OrderByNumberAsync(number, ct) is not { } order) return Answer("failed", $"Keine Bestellung „{number}“.");
        const string who = "agent";
        var action = Str(o, "action").ToLowerInvariant();
        switch (action)
        {
            case "paid":
                return await ShopOrders.MarkPaidAsync(ctx, cfg, order.Id, who, "Per Agent als bezahlt markiert", ct: ct) ? Answer("applied", $"{order.Number} ist bezahlt.") : Answer("failed", $"{order.Number} kann nicht als bezahlt markiert werden (Status {order.Status}).");
            case "ship":
                return await ShopOrders.ShipAsync(ctx, order.Id, Str(o, "tracking"), who, Flag(o, "notify", true), ct, ShopTracking.Clean(Str(o, "carrier")))
                    ? Answer("applied", $"{order.Number} ist versendet.") : Answer("failed", $"{order.Number} kann nicht versendet werden (Status {order.Status}).");
            case "complete":
                return await ShopOrders.CompleteAsync(ctx, order.Id, who, ct) ? Answer("applied", $"{order.Number} ist abgeschlossen.") : Answer("failed", $"{order.Number} kann nicht abgeschlossen werden (Status {order.Status}).");
            case "cancel":
                return await ShopOrders.CancelAsync(ctx, cfg, order.Id, "admin", who, "Per Agent storniert", Flag(o, "notify", false), ct) ? Answer("applied", $"{order.Number} ist storniert.") : Answer("failed", $"{order.Number} kann nicht storniert werden (Status {order.Status}).");
            case "note":
                if (Str(o, "text").Length == 0) return Answer("failed", "Die Notiz ist leer.");
                await store.AddEventAsync(order.Id, "note", Str(o, "text"), who, null, ct);
                return Answer("applied", "Notiz gespeichert.");
            case "tracking":
                await store.UpdateOrderAsync(order.Id, new Dictionary<string, object?> { ["tracking"] = Str(o, "tracking"), ["carrier"] = ShopTracking.Clean(Str(o, "carrier")) }, ct);
                return Answer("applied", "Sendungsnummer gespeichert.");
            default: return Answer("failed", "action muss paid, ship, complete, cancel, note oder tracking sein.");
        }
    }

    // ---- variants ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Sets the variants of a product by its address. <c>variants</c> is a list of <c>{ name, delta_cents|delta, stock, sku, image }</c>; a variant with a known name is updated (it keeps its id, so old orders and carts keep
    /// pointing at it), a new name is added. With <c>replace</c> the variants not in the list are switched off — never deleted, so an order's snapshot still has its source. <c>options</c> (name → values) is remembered
    /// on the product for the storefront's separate pickers.
    /// </summary>
    public static async Task<JsonNode?> SetVariantsAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var store = new ShopStore(ctx.Data);
        var slug = Str(o, "product");
        if (await store.ProductBySlugAsync(slug, ct) is not { } product) return Answer("failed", $"Kein Produkt „{slug}“.");
        if (o["variants"] is not JsonArray list || list.Count == 0) return Answer("failed", "variants fehlt oder ist leer.");
        if (list.Count > ShopVariants.MaxVariants) return Answer("failed", $"Höchstens {ShopVariants.MaxVariants} Varianten.");

        var wanted = new List<(string Name, long Delta, long? Stock, string Sku, string Image)>();
        foreach (var node in list.OfType<JsonObject>())
        {
            var name = Str(node, "name");
            if (name.Length == 0 || name.Length > ShopVariants.MaxName) return Answer("failed", $"Ein Variantenname fehlt oder ist länger als {ShopVariants.MaxName} Zeichen.");
            if (wanted.Any(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase))) return Answer("failed", $"Die Variante „{name}“ steht doppelt in der Liste.");
            var stock = Whole(node["stock"], out var s) && s >= 0 ? s : (long?)null;
            wanted.Add((name, Cents(node, "delta_cents", "delta") ?? 0, stock, Str(node, "sku"), ShopPublic.SafeUrl(Str(node, "image"))));
        }

        var existing = (await store.VariantsAsync(product.Id, false, ct)).ToList();
        int added = 0, updated = 0, off = 0;
        foreach (var w in wanted)
        {
            var have = existing.FirstOrDefault(v => string.Equals(v.Name, w.Name, StringComparison.OrdinalIgnoreCase));
            await store.SaveVariantAsync((have ?? new Variant { ProductId = product.Id, Sort = existing.Count + added }) with
            {
                Name = w.Name, PriceDeltaCents = w.Delta, TrackStock = w.Stock is not null, Stock = w.Stock ?? 0, Sku = w.Sku, Image = w.Image, Active = true
            }, ct);
            if (have is null) added++; else updated++;
        }
        if (Flag(o, "replace", false))
            foreach (var v in existing.Where(v => v.Active && !wanted.Any(w => string.Equals(w.Name, v.Name, StringComparison.OrdinalIgnoreCase))))
            { await store.SaveVariantAsync(v with { Active = false }, ct); off++; }

        if (o["options"] is JsonObject opts)
        {
            var parsed = opts.Take(ShopVariants.MaxOptions).Select(kv => new VariantOption(kv.Key.Trim(), ShopVariants.Split(kv.Value is JsonArray a ? string.Join(",", a.Select(x => x?.ToString() ?? "")) : kv.Value?.ToString()))).Where(x => x.Values.Count > 0 && x.Name.Length > 0).ToList();
            await store.SaveProductAsync(product with { VariantOptions = ShopVariants.OptionsToJson(parsed) }, ct);
        }
        return Answer("applied", $"„{product.Title}“: {added} Variante(n) neu, {updated} aktualisiert" + (off > 0 ? $", {off} abgeschaltet." : "."));
    }

    // ---- attributes ---------------------------------------------------------------------------------------------

    /// <summary>Defines a NEW attribute (add-only: an existing address is skipped).</summary>
    public static async Task<JsonNode?> CreateAttributeAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var store = new ShopStore(ctx.Data);
        var name = Str(o, "name");
        if (name.Length == 0) return Answer("failed", "Eine Eigenschaft braucht einen Namen.");
        var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(Str(o, "slug").Length > 0 ? Str(o, "slug") : name);
        if (slug.Length == 0) return Answer("failed", "Aus dem Namen lässt sich keine Adresse ableiten.");
        if (await store.AttributeBySlugAsync(slug, ct) is not null) return Answer("skipped-exists", $"Es gibt schon eine Eigenschaft „{slug}“.");
        var kind = AttributeKind.Normalise(Str(o, "kind"));
        var options = o["options"] is JsonArray oa ? string.Join("\n", oa.Select(x => x?.ToString() ?? "")) : Str(o, "options");
        var category = Str(o, "category");
        if (category.Length > 0)
        {
            var named = category;
            category = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(category);
            if (await store.CategoryBySlugAsync(category, ct) is null) return Answer("failed", $"Die Kategorie „{named}“ gibt es nicht.");
        }
        if (kind == AttributeKind.Choice && ShopAttributes.SplitOptions(options).Count == 0) return Answer("failed", "Eine Auswahl-Eigenschaft braucht options.");
        var sort = (await store.AttributesAsync(ct)).Count;
        await store.SaveAttributeAsync(new ShopAttribute
        {
            Slug = slug, Name = name.Length > 80 ? name[..80] : name, Kind = kind, Options = kind == AttributeKind.Choice ? options : "", Unit = kind == AttributeKind.Number ? Str(o, "unit") : "",
            Filterable = Flag(o, "filterable", true), Category = category, Sort = sort
        }, ct);
        return Answer("applied", $"Eigenschaft „{name}“ ({kind}) angelegt.");
    }

    /// <summary>Gives a product its values: <c>values</c> is an object attribute address → value (a choice may be a list; true/false for yes/no). An unknown attribute or a value that is no option of a choice is reported, not stored.</summary>
    public static async Task<JsonNode?> SetProductAttributesAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var store = new ShopStore(ctx.Data);
        var slug = Str(o, "product");
        if (await store.ProductBySlugAsync(slug, ct) is not { } product) return Answer("failed", $"Kein Produkt „{slug}“.");
        if (o["values"] is not JsonObject values || values.Count == 0) return Answer("failed", "values fehlt oder ist leer.");
        var cats = await store.CategoriesAsync(ct);
        var notes = new List<string>(); var set = 0;
        foreach (var (key, node) in values)
        {
            if (await store.AttributeBySlugAsync(MatCMS.Pages.Admin.Pages.IndexModel.Slugify(key), ct) is not { } attr) { notes.Add($"„{key}“ ist keine Eigenschaft"); continue; }
            if (!ShopAttributes.AppliesTo(attr, cats, product.Category)) { notes.Add($"„{attr.Slug}“ gilt nicht für die Kategorie des Produkts"); continue; }
            string? text = node switch
            {
                JsonArray arr => string.Join(",", arr.Select(x => x?.ToString() ?? "")),
                JsonValue v when v.TryGetValue<bool>(out var b) => b ? "1" : "",
                null => "",
                _ => node.ToString()
            };
            var stored = ShopAttributes.ReadValues(attr, text, present: text is { Length: > 0 });
            if (stored.Count == 0 && text is { Length: > 0 }) { notes.Add($"„{attr.Slug}“: „{text}“ ist kein gültiger Wert"); continue; }
            await store.SetProductAttrAsync(product.Id, attr.Id, stored, ct);
            set++;
        }
        return Answer(set > 0 ? "applied" : "failed", $"„{product.Title}“: {set} Eigenschaft(en) gesetzt" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "") + ".");
    }
}
