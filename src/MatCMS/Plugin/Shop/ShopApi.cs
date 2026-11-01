using System.Globalization;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using MatCMS.Services;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The shop's API for an ERP / merchandise system (<c>/api/v1/shop/…</c>, see <see cref="IPluginBuilder.Api"/>): products by ARTICLE NUMBER (SKU) —
/// read, create or change in one call (upsert), switch off —, stock and prices in batches, categories, and orders to fetch and report as shipped.
/// <para>The SKU is the ERP's key: a product's own <c>sku</c> or one of its variants'. Money is whole cents and GROSS like everywhere in the shop
/// (<c>price_cents</c>; <c>"price": "19,90"</c> is read too); <c>stock: null</c> means "not counted" (unlimited). A product is never deleted through the
/// API, only switched off (<c>active: false</c>) — orders refer to it. Changes go through the same <see cref="ShopStore"/> the back office uses.</para>
/// </summary>
internal static class ShopApi
{
    private const int MaxBatch = 5000;
    private const int MaxPage = 500;

    public static void Register(IPluginBuilder b)
    {
        b.Api("GET", "products", ListAsync, new ApiRouteOptions { Summary = "Produkte (Seiten): ?updated_since=2026-11-01T00:00:00Z, ?active=true|false, ?page=1, ?limit=100 (höchstens 500)." });
        b.Api("GET", "products/{sku}", GetAsync, new ApiRouteOptions { Summary = "Ein Produkt mit seinen Varianten – per Artikelnummer des Produkts oder einer Variante." });
        b.Api("PUT", "products/{sku}", UpsertAsync, new ApiRouteOptions
        {
            Access = ApiAccess.Write,
            Summary = "Legt ein Produkt mit dieser Artikelnummer an oder ändert es (nur die gesendeten Felder). Varianten per eigener Artikelnummer.",
            Example = """{ "title": "Espresso Bohne 1 kg", "price_cents": 2490, "tax_percent": 7, "stock": 120, "gtin": "4012345678901", "category": "kaffee", "active": true, "variants": [ { "sku": "ESP-1000-M", "name": "Gemahlen", "price_delta_cents": 0, "stock": 40 } ] }"""
        });
        b.Api("DELETE", "products/{sku}", DeactivateAsync, new ApiRouteOptions { Access = ApiAccess.Write, Summary = "Schaltet ein Produkt ab (active = false). Gelöscht wird nichts – Bestellungen verweisen darauf." });
        b.Api("POST", "stock", StockAsync, new ApiRouteOptions
        {
            Access = ApiAccess.Write,
            Summary = "Bestand vieler Artikel in einem Aufruf (bis 5000 Zeilen): absolut (stock), relativ (add) oder ungezählt (stock: null).",
            Example = """{ "items": [ { "sku": "ESP-1000", "stock": 120 }, { "sku": "ESP-1000-M", "add": -3 }, { "sku": "GUTSCHEIN", "stock": null } ] }"""
        });
        b.Api("POST", "prices", PricesAsync, new ApiRouteOptions
        {
            Access = ApiAccess.Write,
            Summary = "Preise vieler Artikel in einem Aufruf (brutto, in Cent). Für eine Variante ist es ihr Endpreis.",
            Example = """{ "items": [ { "sku": "ESP-1000", "price_cents": 2490, "compare_cents": 2990 }, { "sku": "ESP-1000-M", "price_cents": 2590 } ] }"""
        });
        b.Api("GET", "categories", CategoriesAsync, new ApiRouteOptions { Summary = "Alle Kategorien (slug, name, parent)." });
        b.Api("PUT", "categories/{slug}", UpsertCategoryAsync, new ApiRouteOptions
        {
            Access = ApiAccess.Write, Summary = "Legt eine Kategorie an oder ändert sie (name, parent, description, sort).",
            Example = """{ "name": "Kaffee", "parent": "", "description": "Bohnen und gemahlen" }"""
        });
        b.Api("GET", "orders", OrdersAsync, new ApiRouteOptions { Access = ApiAccess.PersonalData, Summary = "Bestellungen, neueste zuerst: ?status=paid, ?since=2026-11-01, ?limit=25 (höchstens 100). Mit Kundendaten." });
        b.Api("GET", "orders/{number}", OrderAsync, new ApiRouteOptions { Access = ApiAccess.PersonalData, Summary = "Eine Bestellung mit Positionen und Adressen." });
        b.Api("POST", "orders/{number}/ship", ShipAsync, new ApiRouteOptions
        {
            Access = ApiAccess.PersonalData, Summary = "Meldet eine Bestellung als versendet (Sendungsnummer, Versanddienst, Kunde benachrichtigen).",
            Example = """{ "tracking": "00340434161234567890", "carrier": "dhl", "notify": true }"""
        });
        b.Api("POST", "orders/{number}/status", OrderStatusAsync, new ApiRouteOptions
        {
            Access = ApiAccess.PersonalData, Summary = "Bezahlt, abgeschlossen oder storniert melden, oder eine Notiz: action = paid | complete | cancel | note.",
            Example = """{ "action": "paid" }"""
        });
    }

    // ---- answers --------------------------------------------------------------------------------------------------

    private static PluginResult Fail(int status, string message) => PluginResult.Json(new JsonObject { ["error"] = message }, status);
    private static PluginResult BadBody() => Fail(400, "Der Body ist kein JSON-Objekt.");

    private static string Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : o[k] is JsonValue n ? n.ToString().Trim() : "";
    private static bool Has(JsonObject o, string k) => o.ContainsKey(k);
    private static bool? Flag(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static bool Whole(JsonNode? n, out long value)
    {
        value = 0;
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<long>(out value)) return true;
        if (v.TryGetValue<double>(out var d) && Math.Abs(d - Math.Round(d)) < 1e-9) { value = (long)Math.Round(d); return true; }
        return v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A price from <c>{key}_cents</c> (whole cents) or <c>{key}</c> ("19,90"); null when neither is sent, false when one is but is not a price.</summary>
    private static bool? Cents(JsonObject o, string key, out long cents)
    {
        cents = 0;
        if (o.ContainsKey(key + "_cents")) return Whole(o[key + "_cents"], out cents) && cents >= 0;
        if (o.ContainsKey(key)) return Money.TryParse(o[key]?.ToString(), out cents) && cents >= 0;
        return null;
    }

    private static string CleanSku(string? s) => (s ?? "").Trim() is var t && t.Length is > 0 and <= 80 ? t : "";

    // ---- the shape of a product -------------------------------------------------------------------------------------

    private static JsonObject ProductJson(Product p, IReadOnlyList<Variant> variants, ShopConfig cfg) => new()
    {
        ["sku"] = p.Sku, ["gtin"] = p.Gtin, ["slug"] = p.Slug, ["url"] = ShopUrls.Product(p.Slug), ["title"] = p.Title, ["summary"] = p.Summary,
        ["description_html"] = p.DescriptionHtml, ["kind"] = p.Kind, ["price_cents"] = p.PriceCents, ["compare_cents"] = p.CompareCents,
        ["tax_percent"] = p.TaxBp / 100m, ["currency"] = cfg.Currency, ["active"] = p.Active, ["stock"] = p.TrackStock ? p.Stock : null,
        ["weight_g"] = p.WeightGrams, ["category"] = p.Category,
        ["categories"] = new JsonArray(p.ExtraCategories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => (JsonNode)x).ToArray()),
        ["tags"] = new JsonArray(ShopTags.Split(p.Tags).Select(x => (JsonNode)x).ToArray()), ["featured"] = p.Featured, ["image"] = p.Image,
        ["gallery"] = JsonNode.Parse(string.IsNullOrWhiteSpace(p.GalleryJson) ? "[]" : p.GalleryJson),
        ["updated"] = DateTime.SpecifyKind(p.UpdatedUtc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ["variants"] = new JsonArray(variants.Select(v => (JsonNode)new JsonObject
        {
            ["sku"] = v.Sku, ["gtin"] = v.Gtin, ["name"] = v.Name, ["price_cents"] = p.PriceCents + v.PriceDeltaCents, ["price_delta_cents"] = v.PriceDeltaCents,
            ["stock"] = v.TrackStock ? v.Stock : null, ["active"] = v.Active, ["image"] = v.Image
        }).ToArray())
    };

    // ---- products -------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> ListAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        DateTime? since = null;
        if (ctx.Request.Q("updated_since") is { Length: > 0 } us)
        {
            if (!DateTime.TryParse(us, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                return Fail(400, "updated_since muss ein Zeitpunkt sein (2026-11-01T00:00:00Z).");
            since = d;
        }
        bool? active = ctx.Request.Q("active").ToLowerInvariant() switch { "true" or "1" => true, "false" or "0" => false, _ => null };
        var page = int.TryParse(ctx.Request.Q("page"), out var pg) && pg > 0 ? pg : 1;
        var limit = int.TryParse(ctx.Request.Q("limit"), out var l) ? Math.Clamp(l, 1, MaxPage) : 100;
        var (items, total) = await store.ProductsForApiAsync(since, active, (page - 1) * limit, limit, ct);
        var variants = (await store.VariantsOfAsync(items.Select(p => p.Id), ct)).ToLookup(v => v.ProductId);
        var cfg = ShopConfig.From(ctx.Settings);
        return PluginResult.Json(new JsonObject
        {
            ["items"] = new JsonArray(items.Select(p => (JsonNode)ProductJson(p, variants[p.Id].OrderBy(v => v.Sort).ToList(), cfg)).ToArray()),
            ["page"] = page, ["limit"] = limit, ["total"] = total
        });
    }

    private static async Task<PluginResult> GetAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var sku = CleanSku(ctx.Request.R("sku"));
        var (p, _) = await store.BySkuAsync(sku, ct);
        if (p is null) return Fail(404, $"Keine Artikelnummer „{sku}“.");
        return PluginResult.Json(ProductJson(p, await store.VariantsAsync(p.Id, false, ct), ShopConfig.From(ctx.Settings)));
    }

    private static async Task<PluginResult> DeactivateAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var sku = CleanSku(ctx.Request.R("sku"));
        if (await store.ProductBySkuAsync(sku, ct) is not { } p) return Fail(404, $"Kein Produkt mit der Artikelnummer „{sku}“.");
        await store.SaveProductAsync(p with { Active = false, UpdatedUtc = DateTime.UtcNow }, ct);
        return PluginResult.Json(new JsonObject { ["sku"] = sku, ["active"] = false });
    }

    /// <summary>
    /// PUT products/{sku}: one call that creates the product when the article number is new and changes ONLY the fields sent when it exists — an ERP
    /// sends what it owns (price, stock, title) and leaves what the shop's editors wrote (texts, pictures) alone by not sending it.
    /// </summary>
    private static async Task<PluginResult> UpsertAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (ctx.Request.Json() is not JsonObject o) return BadBody();
        var sku = CleanSku(ctx.Request.R("sku"));
        if (sku.Length == 0) return Fail(400, "Die Artikelnummer fehlt oder ist länger als 80 Zeichen.");
        var store = new ShopStore(ctx.Data);
        var cfg = ShopConfig.From(ctx.Settings);
        var existing = await store.ProductBySkuAsync(sku, ct);
        if (existing is null && await store.VariantBySkuAsync(sku, ct) is not null)
            return Fail(409, $"„{sku}“ ist die Artikelnummer einer Variante. Varianten werden über ihr Produkt geändert (variants) oder per /stock und /prices.");

        var p = existing ?? new Product { Sku = sku, TaxBp = cfg.DefaultTaxBp, Active = true };
        if (existing is null)
        {
            var title = Str(o, "title");
            if (title.Length == 0) return Fail(400, "Ein neues Produkt braucht einen Titel (title).");
            if (Cents(o, "price", out _) is not true) return Fail(400, "Ein neues Produkt braucht einen Preis (price_cents oder price).");
            var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(Str(o, "slug") is { Length: > 0 } s ? s : title);
            if (slug.Length == 0 || ShopPublic.ReservedSlugs.Contains(slug)) slug = "produkt";
            var baseSlug = slug;
            for (var i = 2; await store.SlugTakenAsync(slug, 0, ct); i++) slug = baseSlug + "-" + i;
            p = p with { Slug = slug };
        }

        if (Has(o, "title")) { if (Str(o, "title").Length == 0) return Fail(400, "title darf nicht leer sein."); p = p with { Title = Str(o, "title") }; }
        if (Has(o, "summary")) p = p with { Summary = Str(o, "summary") };
        if (Has(o, "description_html")) p = p with { DescriptionHtml = SafeHtml.Sanitize(o["description_html"]?.ToString()) };
        switch (Cents(o, "price", out var price)) { case false: return Fail(400, "price_cents / price ist kein gültiger Preis."); case true: p = p with { PriceCents = price }; break; }
        switch (Cents(o, "compare", out var cmp)) { case false: return Fail(400, "compare_cents / compare ist kein gültiger Preis."); case true: p = p with { CompareCents = cmp }; break; }
        if (Has(o, "tax_percent"))
        {
            if (!Money.TryParsePercent(o["tax_percent"]?.ToString(), out var bp)) return Fail(400, "tax_percent ist kein Steuersatz (z. B. 19 oder 7).");
            p = p with { TaxBp = bp };
        }
        if (Flag(o, "active") is { } act) p = p with { Active = act };
        if (Flag(o, "featured") is { } feat) p = p with { Featured = feat };
        if (Has(o, "stock"))
        {
            if (o["stock"] is null) p = p with { TrackStock = false };
            else if (Whole(o["stock"], out var st) && st >= 0) p = p with { TrackStock = true, Stock = st };
            else return Fail(400, "stock muss eine ganze Zahl ≥ 0 oder null (ungezählt) sein.");
        }
        if (Has(o, "gtin")) p = p with { Gtin = Str(o, "gtin") };
        if (Has(o, "weight_g")) { if (!Whole(o["weight_g"], out var w) || w < 0) return Fail(400, "weight_g muss eine ganze Zahl ≥ 0 sein."); p = p with { WeightGrams = (int)w }; }
        if (Has(o, "kind")) p = p with { Kind = ProductKind.Normalise(Str(o, "kind")) };
        if (Has(o, "image")) p = p with { Image = ShopPublic.SafeUrl(Str(o, "image")) };
        if (o["gallery"] is JsonArray g)
            p = p with { GalleryJson = new JsonArray(g.Select(x => ShopPublic.SafeUrl(x?.ToString() ?? "")).Where(x => x.Length > 0).Take(30).Select(x => (JsonNode)x).ToArray()).ToJsonString() };
        if (o["tags"] is JsonArray ta) p = p with { Tags = ShopTags.Normalise(string.Join(", ", ta.Select(x => x?.ToString() ?? ""))) };
        else if (Has(o, "tags")) p = p with { Tags = ShopTags.Normalise(Str(o, "tags")) };
        if (Has(o, "category"))
        {
            var cat = Str(o, "category");
            if (cat.Length == 0) p = p with { Category = "" };
            else if (await ResolveCategoryAsync(store, cat, ct) is { } slug) p = p with { Category = slug };
            else return Fail(400, $"Die Kategorie „{cat}“ gibt es nicht (erst PUT categories/{{slug}}).");
        }
        if (o["categories"] is JsonArray extra)
        {
            var slugs = new List<string>();
            foreach (var c in extra.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0))
            {
                if (await ResolveCategoryAsync(store, c, ct) is not { } found) return Fail(400, $"Die Kategorie „{c}“ gibt es nicht.");
                slugs.Add(found);
            }
            p = p with { ExtraCategories = string.Join(",", slugs.Distinct().Where(s => s != p.Category)) };
        }

        var id = await store.SaveProductAsync(p with { UpdatedUtc = DateTime.UtcNow }, ct);
        p = p with { Id = id };

        if (o["variants"] is JsonArray vs)
        {
            var current = await store.VariantsAsync(id, false, ct);
            var seen = new HashSet<long>();
            var sort = current.Count == 0 ? 0 : current.Max(v => v.Sort) + 1;
            foreach (var node in vs)
            {
                if (node is not JsonObject vo) return Fail(400, "variants enthält einen Eintrag, der kein Objekt ist.");
                var vsku = CleanSku(Str(vo, "sku"));
                if (vsku.Length == 0) return Fail(400, "Jede Variante braucht eine Artikelnummer (sku).");
                var other = await store.VariantBySkuAsync(vsku, ct);
                if (other is not null && other.ProductId != id) return Fail(409, $"Die Variante „{vsku}“ gehört zu einem anderen Produkt.");
                if (await store.ProductBySkuAsync(vsku, ct) is not null) return Fail(409, $"„{vsku}“ ist schon die Artikelnummer eines Produkts.");
                var v = other ?? new Variant { ProductId = id, Sku = vsku, Active = true, Sort = sort++ };
                if (other is null && Str(vo, "name").Length == 0) return Fail(400, $"Die neue Variante „{vsku}“ braucht einen Namen (name).");
                if (Has(vo, "name") && Str(vo, "name").Length > 0) v = v with { Name = Str(vo, "name") };
                if (Has(vo, "price_delta_cents")) { if (!Whole(vo["price_delta_cents"], out var dl)) return Fail(400, $"price_delta_cents von „{vsku}“ ist keine ganze Zahl."); v = v with { PriceDeltaCents = dl }; }
                else switch (Cents(vo, "price", out var vp)) { case false: return Fail(400, $"Der Preis von „{vsku}“ ist ungültig."); case true: v = v with { PriceDeltaCents = vp - p.PriceCents }; break; }
                if (Has(vo, "stock"))
                {
                    if (vo["stock"] is null) v = v with { TrackStock = false };
                    else if (Whole(vo["stock"], out var vst) && vst >= 0) v = v with { TrackStock = true, Stock = vst };
                    else return Fail(400, $"stock von „{vsku}“ muss eine ganze Zahl ≥ 0 oder null sein.");
                }
                if (Flag(vo, "active") is { } va) v = v with { Active = va };
                if (Has(vo, "gtin")) v = v with { Gtin = Str(vo, "gtin") };
                if (Has(vo, "image")) v = v with { Image = ShopPublic.SafeUrl(Str(vo, "image")) };
                seen.Add(await store.SaveVariantAsync(v, ct));
            }
            // "replace": what the ERP did not send any more is switched off — never deleted (orders refer to it).
            if (Flag(o, "variants_replace") == true)
                foreach (var gone in current.Where(v => !seen.Contains(v.Id) && v.Active))
                    await store.SaveVariantAsync(gone with { Active = false }, ct);
        }

        var saved = await store.ProductAsync(id, ct) ?? p;
        var json = ProductJson(saved, await store.VariantsAsync(id, false, ct), cfg);
        return PluginResult.Json(json, existing is null ? 201 : 200);
    }

    /// <summary>A category named by the ERP: its slug, or a name whose slug exists.</summary>
    private static async Task<string?> ResolveCategoryAsync(ShopStore store, string value, CancellationToken ct)
    {
        if (await store.CategoryBySlugAsync(value.ToLowerInvariant(), ct) is { } byslug) return byslug.Slug;
        var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(value);
        return slug.Length > 0 && await store.CategoryBySlugAsync(slug, ct) is { } c ? c.Slug : null;
    }

    // ---- stock and prices in batches ----------------------------------------------------------------------------------

    private static IEnumerable<JsonObject>? Items(IPluginContext ctx, out PluginResult? error)
    {
        error = null;
        if (ctx.Request.Json() is not JsonObject o || o["items"] is not JsonArray items) { error = Fail(400, "Erwartet wird { \"items\": [ … ] }."); return null; }
        if (items.Count > MaxBatch) { error = Fail(413, $"Höchstens {MaxBatch} Zeilen pro Aufruf."); return null; }
        return items.OfType<JsonObject>().ToList();
    }

    /// <summary>
    /// POST stock — every line on its own: <c>stock</c> sets the count (and counts from now on), <c>add</c> changes it by a number (atomically, never
    /// below 0), <c>stock: null</c> stops counting. The answer says per line what the count is now, or why the line was refused; the others still apply.
    /// </summary>
    private static async Task<PluginResult> StockAsync(IPluginContext ctx, CancellationToken ct)
    {
        var lines = Items(ctx, out var error);
        if (lines is null) return error!;
        var store = new ShopStore(ctx.Data);
        var results = new JsonArray();
        int ok = 0, failed = 0;
        foreach (var line in lines)
        {
            var sku = CleanSku(Str(line, "sku"));
            var r = new JsonObject { ["sku"] = sku };
            var (p, v) = await store.BySkuAsync(sku, ct);
            string? why = null;
            if (p is null) why = "unbekannte Artikelnummer";
            else if (line.ContainsKey("add"))
            {
                if (!Whole(line["add"], out var add)) why = "add muss eine ganze Zahl sein";
                else
                {
                    var now = await store.AddStockAsync(p, v, add, ct);
                    if (now is null) why = v is { TrackStock: false } || (v is null && !p.TrackStock) ? "der Bestand wird nicht gezählt (erst stock setzen)" : "der Bestand würde unter 0 fallen";
                    else r["stock"] = now;
                }
            }
            else if (line.ContainsKey("stock"))
            {
                if (line["stock"] is null) { await store.SetStockAsync(p, v, null, ct); r["stock"] = null; }
                else if (Whole(line["stock"], out var st) && st >= 0) { await store.SetStockAsync(p, v, st, ct); r["stock"] = st; }
                else why = "stock muss eine ganze Zahl ≥ 0 oder null sein";
            }
            else why = "weder stock noch add";
            r["ok"] = why is null;
            if (why is not null) { r["error"] = why; failed++; } else ok++;
            results.Add(r);
        }
        return PluginResult.Json(new JsonObject { ["ok"] = ok, ["failed"] = failed, ["items"] = results });
    }

    /// <summary>POST prices — per line the GROSS price in cents (and the struck-through earlier price); a variant's price is its final price.</summary>
    private static async Task<PluginResult> PricesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var lines = Items(ctx, out var error);
        if (lines is null) return error!;
        var store = new ShopStore(ctx.Data);
        var results = new JsonArray();
        int ok = 0, failed = 0;
        foreach (var line in lines)
        {
            var sku = CleanSku(Str(line, "sku"));
            var r = new JsonObject { ["sku"] = sku };
            var (p, v) = await store.BySkuAsync(sku, ct);
            string? why = null;
            var priceState = Cents(line, "price", out var price);
            var compareState = Cents(line, "compare", out var compare);
            if (p is null) why = "unbekannte Artikelnummer";
            else if (priceState is false || compareState is false) why = "kein gültiger Preis";
            else if (priceState is null && compareState is null) why = "weder price_cents noch compare_cents";
            else if (v is not null)
            {
                if (compareState is not null) why = "ein Streichpreis gilt für das Produkt, nicht für eine Variante";
                else { await store.SaveVariantAsync(v with { PriceDeltaCents = price - p.PriceCents }, ct); r["price_cents"] = price; }
            }
            else
            {
                var n = p;
                if (priceState is true) n = n with { PriceCents = price };
                if (compareState is true) n = n with { CompareCents = compare };
                await store.SaveProductAsync(n with { UpdatedUtc = DateTime.UtcNow }, ct);
                r["price_cents"] = n.PriceCents; r["compare_cents"] = n.CompareCents;
            }
            r["ok"] = why is null;
            if (why is not null) { r["error"] = why; failed++; } else ok++;
            results.Add(r);
        }
        return PluginResult.Json(new JsonObject { ["ok"] = ok, ["failed"] = failed, ["items"] = results });
    }

    // ---- categories ---------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> CategoriesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var all = await new ShopStore(ctx.Data).CategoriesAsync(ct);
        return PluginResult.Json(new JsonObject
        {
            ["items"] = new JsonArray(all.Select(c => (JsonNode)new JsonObject
            {
                ["slug"] = c.Slug, ["name"] = c.Name, ["parent"] = all.FirstOrDefault(x => x.Id == c.ParentId)?.Slug ?? "", ["description"] = c.Description, ["sort"] = c.Sort
            }).ToArray())
        });
    }

    private static async Task<PluginResult> UpsertCategoryAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (ctx.Request.Json() is not JsonObject o) return BadBody();
        var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(ctx.Request.R("slug"));
        if (slug.Length == 0) return Fail(400, "Die Adresse (slug) der Kategorie fehlt.");
        var store = new ShopStore(ctx.Data);
        var existing = await store.CategoryBySlugAsync(slug, ct);
        var c = existing ?? new Category { Slug = slug };
        if (existing is null && Str(o, "name").Length == 0) return Fail(400, "Eine neue Kategorie braucht einen Namen (name).");
        if (Str(o, "name") is { Length: > 0 } name) c = c with { Name = name };
        if (Has(o, "description")) c = c with { Description = Str(o, "description") };
        if (Has(o, "sort") && Whole(o["sort"], out var sort)) c = c with { Sort = (int)sort };
        if (Has(o, "parent"))
        {
            var parent = Str(o, "parent");
            if (parent.Length == 0) c = c with { ParentId = 0 };
            else if (await store.CategoryBySlugAsync(parent, ct) is { } pc) c = c with { ParentId = pc.Id };
            else return Fail(400, $"Die übergeordnete Kategorie „{parent}“ gibt es nicht.");
        }
        await store.SaveCategoryAsync(c, ct);
        return PluginResult.Json(new JsonObject { ["slug"] = slug, ["name"] = c.Name }, existing is null ? 201 : 200);
    }

    // ---- orders (personal data) — the same operations the back office's agent tools run -----------------------------

    /// <summary>Turns a shop operation's answer ({status, message, data}) into an API answer.</summary>
    private static PluginResult FromOp(JsonNode? answer, int failStatus = 400)
    {
        var status = answer?["status"]?.ToString() ?? "failed";
        var message = answer?["message"]?.ToString() ?? "";
        if (status == "failed") return Fail(message.StartsWith("Keine Bestellung", StringComparison.Ordinal) ? 404 : failStatus, message);
        return PluginResult.Json(answer?["data"]?.DeepClone() as JsonObject ?? new JsonObject { ["ok"] = true, ["message"] = message });
    }

    private static async Task<PluginResult> OrdersAsync(IPluginContext ctx, CancellationToken ct)
    {
        var args = new JsonObject();
        foreach (var k in new[] { "status", "since", "limit" })
            if (ctx.Request.Q(k) is { Length: > 0 } v) args[k] = k == "limit" && int.TryParse(v, out var n) ? n : v;
        return FromOp(await ShopOps.ListOrdersAsync(ctx, args, ct));
    }

    private static async Task<PluginResult> OrderAsync(IPluginContext ctx, CancellationToken ct)
    {
        var answer = await ShopOps.ListOrdersAsync(ctx, new JsonObject { ["number"] = ctx.Request.R("number") }, ct);
        if (answer?["status"]?.ToString() == "failed") return FromOp(answer);
        return answer?["data"]?["orders"] is JsonArray { Count: > 0 } list ? PluginResult.Json(list[0]!.DeepClone()) : Fail(404, "Keine Bestellung.");
    }

    private static async Task<PluginResult> ShipAsync(IPluginContext ctx, CancellationToken ct)
    {
        var o = ctx.Request.Json() as JsonObject ?? new JsonObject();
        var args = new JsonObject
        {
            ["number"] = ctx.Request.R("number"), ["action"] = "ship", ["tracking"] = Str(o, "tracking"), ["carrier"] = Str(o, "carrier"),
            ["notify"] = Flag(o, "notify") ?? true, ["by"] = "api:" + ctx.Request.ApiCaller
        };
        return FromOp(await ShopOps.UpdateOrderAsync(ctx, args, ct), 409);
    }

    private static async Task<PluginResult> OrderStatusAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (ctx.Request.Json() is not JsonObject o) return BadBody();
        var action = Str(o, "action").ToLowerInvariant();
        if (action is not ("paid" or "complete" or "cancel" or "note")) return Fail(400, "action muss paid, complete, cancel oder note sein (Versand: …/ship).");
        var args = new JsonObject
        {
            ["number"] = ctx.Request.R("number"), ["action"] = action, ["text"] = Str(o, "text"), ["notify"] = Flag(o, "notify") ?? false, ["by"] = "api:" + ctx.Request.ApiCaller
        };
        return FromOp(await ShopOps.UpdateOrderAsync(ctx, args, ct), 409);
    }
}
