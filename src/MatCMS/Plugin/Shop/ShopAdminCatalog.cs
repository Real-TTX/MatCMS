using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using MatCMS.Services;

namespace MatCMS.Plugin.Shop;

/// <summary>The shop's back office, part two: products (with variants and the files of digital ones), categories, shipping rates and the list of customers.</summary>
internal static class ShopAdminCatalog
{
    private const string Section = ShopAdmin.Section;
    private static string Intl(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static string ProductUrl(long id) => $"{Section}/products/{id}";

    /// <summary>A file name that is safe on disk and in a header: letters, digits, dot, dash, underscore; never empty, never starting with a dot.</summary>
    internal static string SafeFileName(string? name)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/'));
        var s = new string(n.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray()).TrimStart('.');
        if (s.Length > 90) s = s[^90..].TrimStart('.');
        return s.Length == 0 ? "file" : s;
    }

    // ---- products: list ----------------------------------------------------------------------------------

    public static async Task<PluginResult> ProductsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        if (ctx.Request.IsPost && ctx.Request.Action == "delete" && long.TryParse(ctx.Request.F("id"), out var del))
        {
            foreach (var f in await store.FilesOfProductAsync(del, ct)) await ctx.Files.DeleteAsync(f.StorageName, ct);
            await store.DeleteProductAsync(del, ct);
            return PluginResult.Redirect($"{Section}/products", ctx.T("shop.adm.productDeleted"));
        }

        var cfg = ShopConfig.From(ctx.Settings);
        var (products, _) = await store.ProductsAsync(false, ct: ct);
        var variants = (await store.VariantsOfAsync(products.Select(p => p.Id), ct)).Where(v => v.Active).GroupBy(v => v.ProductId).ToDictionary(g => g.Key, g => g.Count());
        var table = Ui.Table(
            new[] { Ui.Column(""), Ui.Column(ctx.T("shop.adm.product")), Ui.Column(ctx.T("shop.adm.kind")), Ui.Column(ctx.T("shop.price"), right: true), Ui.Column(ctx.T("shop.adm.stock"), right: true), Ui.Column(ctx.T("shop.adm.status")), Ui.Column("", actions: true) },
            products.Select(p => Ui.RowFor($"{p.Title} {p.Slug} {p.Sku} {p.Category} {p.Tags}",
                p.Image.Length > 0 && ShopPublic.SafeUrl(p.Image).Length > 0 ? Ui.Img(p.Image, "", 40) : Ui.Text("") with { Inline = true },
                Ui.Inline(Ui.Text(p.Title) with { Inline = true, Strong = true }, Ui.Text(p.Slug) with { Inline = true, Muted = true }, p.Featured ? Ui.Badge(ctx.T("shop.adm.featured"), UiTone.Info) : null, p.Tags.Length > 0 ? Ui.Text(p.Tags) with { Inline = true, Muted = true } : null),
                p.IsDigital ? Ui.Badge(ctx.T("shop.digital"), UiTone.Info) : Ui.Badge(ctx.T("shop.physical"), UiTone.Default),
                Money.Format(p.PriceCents, cfg.Currency, "de") + (p.OnSale ? " · " + ctx.T("shop.adm.onSale", p.SavePercent) : "") + (variants.TryGetValue(p.Id, out var n) ? " · " + ctx.T("shop.adm.variantCount", n) : ""),
                p.TrackStock ? Intl(p.Stock) : "∞",
                p.Active ? Ui.Badge(ctx.T("shop.adm.active"), UiTone.Success) : Ui.Badge(ctx.T("shop.adm.inactive"), UiTone.Muted),
                Ui.Inline(
                    Ui.Button(ctx.T("action.edit"), ProductUrl(p.Id), UiButtonKind.Ghost, small: true),
                    Ui.Button(ctx.T("shop.adm.view"), "/shop/" + Uri.EscapeDataString(p.Slug), UiButtonKind.Ghost, icon: "ti-external-link", small: true) with { IconOnly = true, NewTab = true },
                    Ui.Action(ctx.T("action.delete"), "delete", Intl(p.Id), UiButtonKind.Danger, confirm: ctx.T("shop.adm.deleteProductConfirm", p.Title), icon: "ti-trash")))),
            searchPlaceholder: ctx.T("action.search"), pageSize: 25, emptyText: ctx.T("shop.adm.noProducts"));
        return PluginResult.Page(Ui.Stack(Ui.PageHead(null, ctx.T("shop.adm.products"), Ui.Button(ctx.T("shop.adm.newProduct"), ProductUrl(0), small: true)), table), ctx.T("shop.adm.products"));
    }

    // ---- products: editor --------------------------------------------------------------------------------

    /// <summary>The variants textarea: one variant per line, <c>Name | Aufpreis | Bestand | Art.-Nr.</c> — price difference, stock (empty = not counted) and article number are optional.</summary>
    internal static List<(string Name, long DeltaCents, long? Stock, string Sku)> ParseVariants(string text, out string? error)
    {
        error = null;
        var list = new List<(string, long, long?, string)>();
        var lineNo = 0;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            lineNo++;
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;
            var parts = line.Split(new[] { '|', ';' }).Select(x => x.Trim()).ToArray();
            var name = parts[0];
            if (name.Length == 0 || name.Length > 80) { error = $"Zeile {lineNo}: Name fehlt oder ist zu lang."; return list; }
            long delta = 0;
            if (parts.Length > 1 && parts[1].Length > 0 && !Money.TryParse(parts[1].TrimStart('+'), out delta)) { error = $"Zeile {lineNo}: „{parts[1]}“ ist kein Betrag."; return list; }
            long? stock = null;
            if (parts.Length > 2 && parts[2].Length > 0)
            {
                if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var st) || st < 0) { error = $"Zeile {lineNo}: „{parts[2]}“ ist keine Stückzahl."; return list; }
                stock = st;
            }
            if (list.Any(v => string.Equals(v.Item1, name, StringComparison.OrdinalIgnoreCase))) { error = $"Zeile {lineNo}: „{name}“ kommt doppelt vor."; return list; }
            list.Add((name, delta, stock, parts.Length > 3 ? parts[3] : ""));
        }
        if (list.Count > 60) error = "Mehr als 60 Varianten sind nicht möglich.";
        return list;
    }

    private static string VariantsText(IReadOnlyList<Variant> variants) =>
        string.Join("\n", variants.Select(v => $"{v.Name} | {(v.PriceDeltaCents == 0 ? "" : (v.PriceDeltaCents > 0 ? "+" : "") + Money.ForInput(v.PriceDeltaCents))} | {(v.TrackStock ? Intl(v.Stock) : "")} | {v.Sku}".TrimEnd(' ', '|', ' ')));

    public static async Task<PluginResult> ProductAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var cfg = ShopConfig.From(ctx.Settings);
        var id = long.TryParse(ctx.Request.R("id"), out var parsed) ? parsed : 0;
        var product = id == 0 ? new Product { TaxBp = cfg.DefaultTaxBp } : await store.ProductAsync(id, ct);
        if (product is null) return PluginResult.Redirect($"{Section}/products");
        var variants = id == 0 ? new List<Variant>() : (await store.VariantsAsync(id, true, ct)).ToList();
        var variantsText = VariantsText(variants);
        string? error = null;

        if (ctx.Request.IsPost)
        {
            var f = ctx.Request;
            switch (f.Action)
            {
                case "save":
                {
                    variantsText = f.F("variants");
                    var read = ReadProduct(ctx, f, product, cfg, out error);
                    var parsedVariants = ParseVariants(variantsText, out var vErr);
                    error ??= vErr;
                    product = read;
                    if (error is null)
                    {
                        var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(string.IsNullOrWhiteSpace(f.F("slug")) ? f.F("title") : f.F("slug"));
                        if (string.IsNullOrWhiteSpace(slug)) slug = "produkt";
                        if (ShopPublic.ReservedSlugs.Contains(slug)) slug += "-1";
                        var baseSlug = slug; var n = 2;
                        while (await store.SlugTakenAsync(slug, product.Id, ct)) slug = baseSlug + "-" + n++;
                        var saved = await store.SaveProductAsync(product with { Slug = slug }, ct);
                        await SaveVariantsAsync(store, saved, parsedVariants, ct);
                        return PluginResult.Redirect(ProductUrl(saved), ctx.T("shop.adm.productSaved"));
                    }
                    break;
                }
                case "upload-file" when id > 0:
                {
                    var file = f.File("file");
                    if (file is null || file.Length == 0) { error = ctx.T("shop.adm.noFile"); break; }
                    var safe = SafeFileName(file.FileName);
                    var storage = $"products/{id}/{Guid.NewGuid().ToString("N")[..8]}-{safe}";
                    await using (var s = file.OpenRead()) await ctx.Files.SaveAsync(storage, s, ct);
                    await store.AddFileAsync(new ProductFile { ProductId = id, FileName = safe, StorageName = storage, Size = file.Length, Sort = (await store.FilesOfProductAsync(id, ct)).Count }, ct);
                    return PluginResult.Redirect(ProductUrl(id), ctx.T("shop.adm.fileAdded"));
                }
                case "delete-file" when id > 0 && long.TryParse(f.F("id"), out var fid):
                    if (await store.FileAsync(fid, ct) is { } pf && pf.ProductId == id)
                    {
                        await ctx.Files.DeleteAsync(pf.StorageName, ct);
                        await store.DeleteFileAsync(fid, ct);
                    }
                    return PluginResult.Redirect(ProductUrl(id), ctx.T("shop.adm.fileDeleted"));
            }
        }

        var cats = await store.CategoriesAsync(ct);
        var knownTags = (await store.FacetsAsync(ct)).Tags.Select(t => t.Tag).ToList();
        var kids = new List<UiNode?>();
        if (error is not null) kids.Add(Ui.Alert(error, UiTone.Error));
        kids.Add(Ui.Hidden("action", "save"));
        kids.Add(Ui.Input("title", ctx.T("shop.adm.title"), product.Title) with { Required = true });
        kids.Add(Ui.Input("slug", ctx.T("shop.adm.slug"), product.Slug) with { Help = ctx.T("shop.adm.slugHelp") });
        kids.Add(Ui.Select("kind", ctx.T("shop.adm.kind"), product.Kind, Ui.Option(ProductKind.Physical, ctx.T("shop.physical")), Ui.Option(ProductKind.Digital, ctx.T("shop.digital"))));
        kids.Add(Ui.Input("price", ctx.T("shop.adm.priceGross", cfg.Currency), Money.ForInput(product.PriceCents)) with { Required = true, Help = ctx.T("shop.adm.priceHelp") });
        kids.Add(Ui.Input("compare", ctx.T("shop.adm.compare", cfg.Currency), product.CompareCents > 0 ? Money.ForInput(product.CompareCents) : "") with { Help = ctx.T("shop.adm.compareHelp") });
        kids.Add(Ui.Input("tax", ctx.T("shop.adm.taxPercent"), Money.Percent(product.TaxBp)) with { Help = ctx.T(cfg.SmallBusiness ? "shop.adm.taxIgnored" : "shop.adm.taxHelp") });
        kids.Add(Ui.Input("sku", ctx.T("shop.adm.sku"), product.Sku));
        kids.Add(Ui.Select("category", ctx.T("shop.adm.category"), product.Category, new[] { Ui.Option("", "—") }.Concat(cats.Select(c => Ui.Option(c.Slug, c.Name))).ToArray()));
        kids.Add(Ui.Tags("tags", ctx.T("shop.adm.tags"), ShopTags.Normalise(product.Tags), knownTags) with { Help = ctx.T("shop.adm.tagsHelp") });
        kids.Add(Ui.Checkbox("featured", ctx.T("shop.adm.featured"), product.Featured) with { Help = ctx.T("shop.adm.featuredHelp") });
        kids.Add(Ui.TextArea("summary", ctx.T("shop.adm.summary"), product.Summary, 2) with { Help = ctx.T("shop.adm.summaryHelp") });
        kids.Add(Ui.RichText("description", ctx.T("shop.description"), product.DescriptionHtml));
        kids.Add(Ui.Image("image", ctx.T("shop.adm.image"), product.Image));
        kids.Add(Ui.MediaList("gallery", ctx.T("shop.adm.gallery"), product.GalleryJson));
        kids.Add(Ui.TextArea("variants", ctx.T("shop.adm.variants"), variantsText, 5) with { Help = ctx.T("shop.adm.variantsHelp"), Placeholder = "Größe M | | 12 | TS-M\nGröße L | +2,00 | 8 | TS-L" });
        kids.Add(Ui.Checkbox("track_stock", ctx.T("shop.adm.trackStock"), product.TrackStock));
        kids.Add(Ui.Input("stock", ctx.T("shop.adm.stockCount"), Intl(product.Stock), UiInputType.Number) with { Help = ctx.T("shop.adm.stockHelp"), Min = "0" });
        kids.Add(Ui.Input("weight", ctx.T("shop.adm.weight"), Intl(product.WeightGrams), UiInputType.Number) with { Min = "0", Help = ctx.T("shop.adm.weightHelp") });
        kids.Add(Ui.Input("max", ctx.T("shop.adm.maxPerOrder"), Intl(product.MaxPerOrder), UiInputType.Number) with { Min = "0", Help = ctx.T("shop.adm.maxHelp") });
        kids.Add(Ui.Input("sort", ctx.T("shop.adm.sort"), Intl(product.Sort), UiInputType.Number));
        kids.Add(Ui.Checkbox("active", ctx.T("shop.adm.activeLabel"), product.Active || id == 0));
        kids.Add(Ui.FormActions(Ui.Submit(ctx.T("action.save")), Ui.Button(ctx.T("action.cancel"), $"{Section}/products", UiButtonKind.Ghost)));

        var nodes = new List<UiNode?> { Ui.PageHead(null, ctx.T(id == 0 ? "shop.adm.newProduct" : "shop.adm.editProduct"), Ui.Button(ctx.T("shop.adm.back"), $"{Section}/products", UiButtonKind.Ghost, icon: "ti-arrow-left", small: true)), Ui.Card(Ui.Form(kids.ToArray())) };

        if (id > 0 && product.IsDigital)
        {
            var files = await store.FilesOfProductAsync(id, ct);
            nodes.Add(Ui.Card(ctx.T("shop.adm.files"),
                Ui.Muted(ctx.T("shop.adm.filesHelp")),
                files.Count == 0 ? Ui.Empty(ctx.T("shop.adm.noFiles")) : Ui.Table(
                    new[] { Ui.Column(ctx.T("shop.adm.fileName")), Ui.Column(ctx.T("shop.adm.size"), right: true), Ui.Column("", actions: true) },
                    files.Select(x => Ui.Row(x.FileName, ShopPublic.FileSize(x.Size), Ui.Action(ctx.T("action.delete"), "delete-file", Intl(x.Id), UiButtonKind.Danger, confirm: ctx.T("shop.adm.deleteFileConfirm", x.FileName), icon: "ti-trash")))),
                Ui.Form(Ui.Hidden("action", "upload-file"), Ui.File("file", ctx.T("shop.adm.addFile")), Ui.FormActions(Ui.Submit(ctx.T("shop.adm.upload"), UiButtonKind.Ghost))) with { Upload = true }));
        }
        else if (id > 0) nodes.Add(Ui.Muted(ctx.T("shop.adm.filesOnlyDigital")));
        else nodes.Add(Ui.Muted(ctx.T("shop.adm.saveFirst")));
        return PluginResult.Page(Ui.Stack(nodes.ToArray()), ctx.T(id == 0 ? "shop.adm.newProduct" : "shop.adm.editProduct"));
    }

    /// <summary>The product as the form describes it. Sets <paramref name="error"/> on the first thing wrong — the form comes back with what was typed.</summary>
    private static Product ReadProduct(IPluginContext ctx, RequestData f, Product current, ShopConfig cfg, out string? error)
    {
        error = null;
        var price = current.PriceCents;
        if (!Money.TryParse(f.F("price"), out price) || price < 0) error = ctx.T("shop.adm.errPrice");
        long compare = 0;
        if (f.F("compare").Trim().Length > 0 && (!Money.TryParse(f.F("compare"), out compare) || compare < 0)) { error ??= ctx.T("shop.adm.errCompare"); compare = 0; }
        var bp = current.TaxBp;
        if (!Money.TryParsePercent(f.F("tax"), out bp)) { error ??= ctx.T("shop.adm.errTax"); bp = current.TaxBp; }
        long Num(string key, long def = 0) => long.TryParse(f.F(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Max(0, v) : def;
        if (string.IsNullOrWhiteSpace(f.F("title"))) error ??= ctx.T("shop.adm.errTitle");
        var kind = ProductKind.Normalise(f.F("kind"));
        return current with
        {
            Title = f.F("title").Trim(), Summary = f.F("summary").Trim(), DescriptionHtml = SafeHtml.Sanitize(f.F("description")), Image = ShopPublic.SafeUrl(f.F("image")),
            GalleryJson = CleanGallery(f.F("gallery")), Kind = kind, PriceCents = Math.Max(0, price), CompareCents = Math.Max(0, compare), TaxBp = bp, Sku = f.F("sku").Trim(), Category = f.F("category").Trim(),
            Tags = ShopTags.Normalise(f.F("tags")), Featured = f.Form.ContainsKey("featured"),
            TrackStock = f.Form.ContainsKey("track_stock"),
            Stock = Num("stock"), WeightGrams = kind == ProductKind.Digital ? 0 : (int)Math.Min(Num("weight"), int.MaxValue), MaxPerOrder = (int)Math.Min(Num("max"), 100000), Sort = (int)Math.Min(Num("sort"), 100000),
            Active = f.Form.ContainsKey("active")
        };
    }

    /// <summary>The gallery as a JSON array of safe picture addresses; anything else the form sent is dropped.</summary>
    private static string CleanGallery(string json)
    {
        var keep = new JsonArray();
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) is JsonArray arr)
                foreach (var n in arr)
                    if (n is JsonObject o && o["url"]?.GetValue<string>() is { } url && ShopPublic.SafeUrl(url).Length > 0)
                        keep.Add(new JsonObject { ["url"] = url, ["alt"] = o["alt"]?.GetValue<string>() ?? "" });
        }
        catch { /* a damaged field saves as an empty gallery */ }
        return keep.ToJsonString();
    }

    /// <summary>
    /// Brings the stored variants in line with the textarea. A variant is the same one when its NAME is: its row (and so every order that points at it) is kept and
    /// updated. A name that is gone is switched off, never deleted — an old order's line and the stock it must give back still refer to it.
    /// </summary>
    private static async Task SaveVariantsAsync(ShopStore store, long productId, List<(string Name, long DeltaCents, long? Stock, string Sku)> wanted, CancellationToken ct)
    {
        var existing = await store.VariantsAsync(productId, false, ct);
        var sort = 0;
        foreach (var w in wanted)
        {
            var current = existing.FirstOrDefault(v => string.Equals(v.Name, w.Name, StringComparison.OrdinalIgnoreCase));
            await store.SaveVariantAsync((current ?? new Variant { ProductId = productId }) with
            {
                Name = w.Name, Sku = w.Sku, PriceDeltaCents = w.DeltaCents, TrackStock = w.Stock is not null, Stock = w.Stock ?? 0, Active = true, Sort = sort++
            }, ct);
        }
        foreach (var gone in existing.Where(v => v.Active && !wanted.Any(w => string.Equals(w.Name, v.Name, StringComparison.OrdinalIgnoreCase))))
            await store.SaveVariantAsync(gone with { Active = false }, ct);
    }

    // ---- categories --------------------------------------------------------------------------------------

    public static async Task<PluginResult> CategoriesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        if (ctx.Request.IsPost && ctx.Request.Action == "save")
        {
            var f = ctx.Request;
            foreach (var c in await store.CategoriesAsync(ct))
            {
                if (f.Form.ContainsKey($"del_{c.Id}")) { await store.DeleteCategoryAsync(c.Id, ct); continue; }
                var name = f.F($"name_{c.Id}").Trim();
                if (name.Length > 0) await store.UpdateCategoryAsync(c.Id, name, int.TryParse(f.F($"sort_{c.Id}"), out var s) ? s : c.Sort, ct);
            }
            var fresh = f.F("new_name").Trim();
            if (fresh.Length > 0)
            {
                var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(fresh);
                if (slug.Length > 0 && await store.CategoryBySlugAsync(slug, ct) is null) await store.AddCategoryAsync(slug, fresh, (await store.CategoriesAsync(ct)).Count, ct);
            }
            return PluginResult.Redirect($"{Section}/categories", ctx.T("shop.adm.saved"));
        }
        var cats = await store.CategoriesAsync(ct);
        var counts = (await store.ProductsAsync(false, ct: ct)).Items.Where(p => p.Category.Length > 0).GroupBy(p => p.Category).ToDictionary(g => g.Key, g => g.Count());
        var rows = new List<UiNode?>();
        foreach (var c in cats)
            rows.Add(Ui.Grid(200, Ui.Input($"name_{c.Id}", c.Slug, c.Name) with { Help = ctx.T(counts.TryGetValue(c.Slug, out var n) ? "shop.adm.productCount" : "shop.adm.products0", n) }, Ui.Input($"sort_{c.Id}", ctx.T("shop.adm.sort"), Intl(c.Sort), UiInputType.Number), Ui.Checkbox($"del_{c.Id}", ctx.T("shop.adm.removeCategory"))));
        rows.Add(Ui.Heading(ctx.T("shop.adm.newCategory"), 3));
        rows.Add(Ui.Input("new_name", ctx.T("shop.adm.categoryName"), ""));
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(ctx.T("shop.adm.categoriesHelp"), ctx.T("shop.adm.categories")),
            Ui.Card(Ui.Form(new UiNode?[] { Ui.Hidden("action", "save") }.Concat(rows).Append(Ui.FormActions(Ui.Submit(ctx.T("action.save")))).ToArray()))), ctx.T("shop.adm.categories"));
    }

    // ---- shipping ----------------------------------------------------------------------------------------

    public static async Task<PluginResult> ShippingAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        string? error = null;
        if (ctx.Request.IsPost && ctx.Request.Action == "save")
        {
            var f = ctx.Request;
            var rates = (await store.RatesAsync(false, ct)).ToList();
            var updates = new List<ShippingRate>(); var deletes = new List<long>();
            foreach (var r in rates)
            {
                if (f.Form.ContainsKey($"del_{r.Id}")) { deletes.Add(r.Id); continue; }
                var next = ReadRate(f, $"_{r.Id}", r, out var e);
                if (e is not null) { error = e; break; }
                updates.Add(next);
            }
            ShippingRate? created = null;
            if (error is null && f.F("name_new").Trim().Length > 0)
            {
                created = ReadRate(f, "_new", new ShippingRate { Sort = rates.Count }, out var e);
                error = e;
            }
            if (error is null)
            {
                foreach (var d in deletes) await store.DeleteRateAsync(d, ct);
                foreach (var u in updates) await store.SaveRateAsync(u, ct);
                if (created is not null) await store.SaveRateAsync(created, ct);
                return PluginResult.Redirect($"{Section}/shipping", ctx.T("shop.adm.saved"));
            }
        }

        var cfg = ShopConfig.From(ctx.Settings);
        var list = await store.RatesAsync(false, ct);
        var kids = new List<UiNode?> { Ui.Hidden("action", "save") };
        if (error is not null) kids.Add(Ui.Alert(error, UiTone.Error));
        foreach (var r in list) { kids.Add(RateFields(ctx, $"_{r.Id}", r, true)); }
        kids.Add(Ui.Heading(ctx.T("shop.adm.newRate"), 3));
        kids.Add(RateFields(ctx, "_new", new ShippingRate { Name = "" }, false));
        kids.Add(Ui.FormActions(Ui.Submit(ctx.T("action.save"))));
        return PluginResult.Page(Ui.Stack(Ui.PageHead(ctx.T("shop.adm.shippingHelp"), ctx.T("shop.adm.shipping")), Ui.Card(Ui.Form(kids.ToArray()))), ctx.T("shop.adm.shipping"));
    }

    private static UiNode RateFields(IPluginContext ctx, string suffix, ShippingRate r, bool deletable) => Ui.Card(
        Ui.Grid(200,
            Ui.Input("name" + suffix, ctx.T("shop.adm.rateName"), r.Name),
            Ui.Input("countries" + suffix, ctx.T("shop.adm.rateCountries"), r.Countries) with { Help = ctx.T("shop.adm.rateCountriesHelp") },
            Ui.Input("price" + suffix, ctx.T("shop.adm.ratePrice"), Money.ForInput(r.PriceCents)),
            Ui.Input("free" + suffix, ctx.T("shop.adm.rateFree"), r.FreeFromCents > 0 ? Money.ForInput(r.FreeFromCents) : "") with { Help = ctx.T("shop.adm.rateFreeHelp") },
            Ui.Input("weight" + suffix, ctx.T("shop.adm.rateWeight"), r.MaxWeightGrams > 0 ? Intl(r.MaxWeightGrams) : "", UiInputType.Number) with { Help = ctx.T("shop.adm.rateWeightHelp") }),
        Ui.Inline(Ui.Checkbox("active" + suffix, ctx.T("shop.adm.activeLabel"), r.Active || r.Id == 0), deletable ? Ui.Checkbox("del" + suffix, ctx.T("shop.adm.removeRate")) : null));

    private static ShippingRate ReadRate(RequestData f, string suffix, ShippingRate current, out string? error)
    {
        error = null;
        var name = f.F("name" + suffix).Trim();
        if (name.Length == 0) error = "Eine Versandart braucht einen Namen.";
        if (!Money.TryParse(f.F("price" + suffix), out var price) || price < 0) error ??= $"„{f.F("price" + suffix)}“ ist kein gültiger Preis ({name}).";
        long free = 0;
        if (f.F("free" + suffix).Trim().Length > 0 && (!Money.TryParse(f.F("free" + suffix), out free) || free < 0)) error ??= $"„{f.F("free" + suffix)}“ ist kein gültiger Betrag ({name}).";
        var weight = int.TryParse(f.F("weight" + suffix), out var w) && w > 0 ? w : 0;
        var countries = f.F("countries" + suffix).Trim();
        if (countries.Length == 0) countries = "*";
        if (countries != "*")
        {
            var codes = countries.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.ToUpperInvariant()).ToList();
            if (codes.Any(c => c.Length != 2 || !char.IsAsciiLetter(c[0]) || !char.IsAsciiLetter(c[1]))) error ??= $"Länderkürzel müssen zwei Buchstaben haben ({name}).";
            countries = string.Join(",", codes.Distinct());
        }
        return current with { Name = name, Countries = countries, PriceCents = price, FreeFromCents = free, MaxWeightGrams = weight, Active = f.Form.ContainsKey("active" + suffix) };
    }

    // ---- customers ---------------------------------------------------------------------------------------

    public static async Task<PluginResult> CustomersAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var cfg = ShopConfig.From(ctx.Settings);
        var customers = await store.CustomersAsync(ct);
        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("shop.adm.customer")), Ui.Column(ctx.T("shop.adm.account")), Ui.Column(ctx.T("shop.adm.orderCount"), right: true), Ui.Column(ctx.T("shop.adm.spent"), right: true), Ui.Column(ctx.T("shop.adm.lastOrder")), Ui.Column("", actions: true) },
            customers.Select(c => Ui.RowFor($"{c.Email} {c.Name}",
                Ui.Inline(Ui.Text(string.IsNullOrWhiteSpace(c.Name) ? c.Email : c.Name) with { Inline = true, Strong = true }, Ui.Text(c.Email) with { Inline = true, Muted = true }),
                c.MemberId > 0 ? Ui.Badge(ctx.T("shop.adm.hasAccount"), UiTone.Success) : Ui.Badge(ctx.T("shop.adm.guest"), UiTone.Muted),
                c.Orders, Money.Format(c.SpentCents, cfg.Currency, "de"), c.LastOrderUtc.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                Ui.Button(ctx.T("shop.adm.orders"), $"{Section}/orders?q={Uri.EscapeDataString(c.Email)}", UiButtonKind.Ghost, small: true))),
            searchPlaceholder: ctx.T("action.search"), pageSize: 25, emptyText: ctx.T("shop.adm.noCustomers"));
        return PluginResult.Page(Ui.Stack(Ui.PageHead(ctx.T("shop.adm.customersHelp"), ctx.T("shop.adm.customers")), table), ctx.T("shop.adm.customers"));
    }
}
