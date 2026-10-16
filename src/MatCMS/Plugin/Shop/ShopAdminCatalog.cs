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

        if (ctx.Request.IsPost && ctx.Request.Action == "duplicate" && long.TryParse(ctx.Request.F("id"), out var dup))
        {
            var copy = await store.DuplicateProductAsync(dup, ctx.T("shop.adm.copyWord"), ct);
            return copy > 0 ? PluginResult.Redirect(ProductUrl(copy), ctx.T("shop.adm.duplicated")) : PluginResult.Redirect($"{Section}/products");
        }
        var cfg = ShopConfig.From(ctx.Settings);
        var (products, _) = await store.ProductsAsync(false, ct: ct);
        var variants = (await store.VariantsOfAsync(products.Select(p => p.Id), ct)).Where(v => v.Active).GroupBy(v => v.ProductId).ToDictionary(g => g.Key, g => g.Count());
        var allCats = await store.CategoriesAsync(ct);
        var catNames = allCats.ToDictionary(c => c.Slug, c => ShopCategories.Label(allCats, c));
        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("shop.adm.product")), Ui.Column(ctx.T("shop.adm.kind")), Ui.Column(ctx.T("shop.price"), right: true), Ui.Column(ctx.T("shop.adm.stock"), right: true), Ui.Column(ctx.T("shop.adm.status")), Ui.Column("", actions: true) },
            products.Select(p => (Ui.RowFor($"{p.Title} {p.Slug} {p.Sku} {p.Category} {p.Tags}",
                Ui.Html(ProductCell(ctx, p, catNames)),
                p.IsDigital ? Ui.Badge(ctx.T("shop.digital"), UiTone.Info) : Ui.Badge(ctx.T("shop.physical"), UiTone.Default),
                Ui.Html($"<strong>{System.Net.WebUtility.HtmlEncode(Money.Format(p.PriceCents, cfg.Currency, "de"))}</strong>" + (p.OnSale ? $"<div class=\"muted\"><s>{System.Net.WebUtility.HtmlEncode(Money.Format(p.CompareCents, cfg.Currency, "de"))}</s> −{p.SavePercent} %</div>" : "") + (variants.TryGetValue(p.Id, out var n) ? $"<div class=\"muted\">{System.Net.WebUtility.HtmlEncode(ctx.T("shop.adm.variantCount", n))}</div>" : "")),
                p.TrackStock ? (p.Stock <= cfg.LowStock ? Ui.Badge(Intl(p.Stock), p.Stock <= 0 ? UiTone.Error : UiTone.Warning) : Ui.Text(Intl(p.Stock)) with { Inline = true }) : Ui.Text("∞") with { Inline = true, Muted = true },
                p.Active ? Ui.Badge(ctx.T("shop.adm.active"), UiTone.Success) : Ui.Badge(ctx.T("shop.adm.inactive"), UiTone.Muted),
                Ui.Inline(
                    Ui.Button(ctx.T("action.edit"), ProductUrl(p.Id), UiButtonKind.Ghost, small: true),
                    Ui.Button(ctx.T("shop.adm.view"), "/shop/" + Uri.EscapeDataString(p.Slug), UiButtonKind.Ghost, icon: "ti-external-link", small: true) with { IconOnly = true, NewTab = true },
                    Ui.Action(ctx.T("shop.adm.duplicate"), "duplicate", Intl(p.Id), UiButtonKind.Ghost, icon: "ti-copy"),
                    Ui.Action(ctx.T("action.delete"), "delete", Intl(p.Id), UiButtonKind.Danger, confirm: ctx.T("shop.adm.deleteProductConfirm", p.Title), icon: "ti-trash"))) with
                { Tags = (p.Active ? "active" : "inactive") + (p.Featured ? " featured" : "") + (p.OnSale ? " sale" : "") + (p.IsDigital ? " digital" : " physical") })),
            searchPlaceholder: ctx.T("action.search"), pageSize: 25, emptyText: ctx.T("shop.adm.noProducts")) with
        {
            Filter = Ui.ListFilter(ctx.T("shop.adm.filter"),
                ("", $"{ctx.T("shop.adm.all")} ({products.Count})"), ("active", $"{ctx.T("shop.adm.active")} ({products.Count(p => p.Active)})"), ("inactive", $"{ctx.T("shop.adm.inactive")} ({products.Count(p => !p.Active)})"),
                ("featured", $"{ctx.T("shop.adm.featured")} ({products.Count(p => p.Featured)})"), ("sale", $"{ctx.T("shop.adm.filterSale")} ({products.Count(p => p.OnSale)})"),
                ("digital", $"{ctx.T("shop.digital")} ({products.Count(p => p.IsDigital)})")),
            Below = new UiNode[] { Ui.Button(ctx.T("shop.adm.newProduct"), ProductUrl(0)) }
        };
        return PluginResult.Page(Ui.Stack(table), ctx.T("shop.adm.products"));
    }

    /// <summary>The first cell of the product list: the picture (or a placeholder), the name, what it is filed under and its keywords.</summary>
    private static string ProductCell(IPluginContext ctx, Product p, IReadOnlyDictionary<string, string> catNames)
    {
        static string E(string? t) => System.Net.WebUtility.HtmlEncode(t ?? "");
        var img = ShopPublic.SafeUrl(p.Image);
        var thumb = img.Length > 0 ? $"<span class=\"ui-thumb\" style=\"background-image:url('{E(img)}')\"></span>" : "<span class=\"ui-thumb empty\"><i class=\"ti ti-photo\"></i></span>";
        var sb = new System.Text.StringBuilder("<div class=\"ui-media-cell\">").Append(thumb).Append("<div><a href=\"").Append(ProductUrl(p.Id)).Append("\"><strong>").Append(E(p.Title)).Append("</strong></a>");
        if (p.Featured) sb.Append(" <span class=\"badge badge-new\">").Append(E(ctx.T("shop.adm.featured"))).Append("</span>");
        sb.Append("<div class=\"muted\">").Append(E(p.Sku.Length > 0 ? p.Sku + " · " : "")).Append(E(p.Category.Length > 0 && catNames.TryGetValue(p.Category, out var cn) ? cn : "—")).Append("</div>");
        if (ShopTags.Split(p.Tags) is { Count: > 0 } tags) sb.Append("<div class=\"ui-chips\">").Append(string.Concat(tags.Select(t => $"<span class=\"chip\">{E(t)}</span>"))).Append("</div>");
        return sb.Append("</div></div>").ToString();
    }

    // ---- products: editor --------------------------------------------------------------------------------

    public static async Task<PluginResult> ProductAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var cfg = ShopConfig.From(ctx.Settings);
        var id = long.TryParse(ctx.Request.R("id"), out var parsed) ? parsed : 0;
        var product = id == 0 ? new Product { TaxBp = cfg.DefaultTaxBp } : await store.ProductAsync(id, ct);
        if (product is null) return PluginResult.Redirect($"{Section}/products");
        var variants = id == 0 ? new List<Variant>() : (await store.VariantsAsync(id, true, ct)).ToList();
        var variantRows = variants.Select(v => new VariantRow(v.Id, v.Name, v.PriceDeltaCents, v.TrackStock ? v.Stock : null, v.Sku, v.Image)).ToList();
        var options = ShopVariants.OptionsFromJson(product.VariantOptions);
        var created = 0; var generate = false;
        var oldSlug = product.Slug;
        var otherLocales = ShopAdminTranslations.Locales(ctx);
        var translations = id == 0 ? new Dictionary<string, Translation>() : await store.TranslationsOfAsync("product", id, ct);
        Dictionary<string, Translation>? postedTranslations = null;
        string? error = null;
        var allCats = await store.CategoriesAsync(ct);
        var allAttrs = await store.AttributesAsync(ct);
        var attrValues = id == 0 ? new Dictionary<long, List<string>>() : await store.ProductValuesAsync(id, ct);

        if (ctx.Request.IsPost)
        {
            var f = ctx.Request;
            switch (f.Action)
            {
                case "save":
                {
                    var read = ReadProduct(ctx, f, product, cfg, out error);
                    variantRows = ShopVariants.ReadRows(f.F, out var vErr);
                    if (vErr is { } ve) error ??= ctx.T(ve.Key, ve.Args);
                    options = ShopVariants.ReadOptions(f.F);
                    // "Kombinationen erzeugen" saves like any save and adds a variant for every combination of the options that is not there yet.
                    generate = f.Form.ContainsKey("generate");
                    if (generate && error is null)
                    {
                        if (options.Count == 0) error = ctx.T("shop.adm.var.errNoOptions");
                        else if (!ShopVariants.AddMissing(variantRows, ShopVariants.Combinations(options), out created)) error = ctx.T("shop.adm.var.errMany", ShopVariants.MaxVariants);
                    }
                    postedTranslations = ShopAdminTranslations.Read(f, otherLocales);
                    translations = postedTranslations;
                    // The further categories ticked in the form (the main one is never also a further one).
                    var extra = string.Join(",", allCats.Where(c => f.Form.ContainsKey("xcat_" + c.Slug) && c.Slug != read.Category).Select(c => c.Slug));
                    product = read with { VariantOptions = ShopVariants.OptionsToJson(options), ExtraCategories = extra };
                    if (error is null)
                    {
                        var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(string.IsNullOrWhiteSpace(f.F("slug")) ? f.F("title") : f.F("slug"));
                        if (string.IsNullOrWhiteSpace(slug)) slug = "produkt";
                        if (ShopPublic.ReservedSlugs.Contains(slug)) slug += "-1";
                        var baseSlug = slug; var n = 2;
                        while (await store.SlugTakenAsync(slug, product.Id, ct)) slug = baseSlug + "-" + n++;
                        var saved = await store.SaveProductAsync(product with { Slug = slug }, ct);
                        // The old address of a product whose address changed keeps working (a permanent redirect); a product that takes an old address over ends the redirect.
                        if (id > 0 && oldSlug.Length > 0 && oldSlug != slug) await store.RememberSlugAsync(oldSlug, saved, ct);
                        await store.ForgetRedirectAsync(slug, ct);
                        await ShopAdminTranslations.SaveAsync(store, "product", saved, otherLocales, postedTranslations, ct);
                        await SaveVariantsAsync(store, saved, variantRows, ct);
                        // The attributes the form showed (it names them in "attrs"): what was typed replaces what the product had.
                        var shown = f.F("attrs").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out var n) ? n : 0).ToHashSet();
                        foreach (var a in allAttrs.Where(a => shown.Contains(a.Id)))
                            await store.SetProductAttrAsync(saved, a.Id, ShopAttributes.ReadValues(a, f.F("attr_" + a.Id), f.Form.ContainsKey("attr_" + a.Id)), ct);
                        return PluginResult.Redirect(ProductUrl(saved), generate ? ctx.T("shop.adm.var.created", created) : ctx.T("shop.adm.productSaved"));
                    }
                    break;
                }
                case "duplicate" when id > 0:
                {
                    var copy = await store.DuplicateProductAsync(id, ctx.T("shop.adm.copyWord"), ct);
                    return copy > 0 ? PluginResult.Redirect(ProductUrl(copy), ctx.T("shop.adm.duplicated")) : PluginResult.Redirect($"{Section}/products");
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

        var cats = allCats;
        var knownTags = (await store.FacetsAsync(ct)).Tags.Select(t => t.Tag).ToList();
        if (ctx.Request.IsPost && ctx.Request.Action == "save")
            foreach (var a in allAttrs)
                if (ctx.Request.Form.ContainsKey("attr_" + a.Id) || a.Kind == AttributeKind.Bool) attrValues[a.Id] = ShopAttributes.ReadValues(a, ctx.Request.F("attr_" + a.Id), ctx.Request.Form.ContainsKey("attr_" + a.Id));
        var applicable = allAttrs.Where(a => ShopAttributes.AppliesTo(a, cats, product.Category)).ToList();
        var hidden = new List<UiNode?>();
        if (error is not null) hidden.Add(Ui.Alert(error, UiTone.Error));
        hidden.Add(Ui.Hidden("action", "save"));
        // Left: what the product IS — name, text, pictures, variants. Right: how it is sold — status, price, place in the shop, stock.
        var main = new List<UiNode?>
        {
            Ui.Card(ctx.T("shop.adm.secProduct"),
                Ui.Input("title", ctx.T("shop.adm.title"), product.Title) with { Required = true },
                Ui.TextArea("summary", ctx.T("shop.adm.summary"), product.Summary, 2) with { Help = ctx.T("shop.adm.summaryHelp") },
                Ui.RichText("description", ctx.T("shop.description"), product.DescriptionHtml)),
            Ui.Card(ctx.T("shop.adm.secPictures"),
                Ui.Image("image", ctx.T("shop.adm.image"), product.Image),
                Ui.MediaList("gallery", ctx.T("shop.adm.gallery"), product.GalleryJson)),
            AttributesCard(ctx, applicable, allAttrs.Count, attrValues),
            VariantsCard(ctx, cfg, product, options, variantRows),
            Ui.Card(ctx.T("shop.adm.seo"),
                Ui.Muted(ctx.T("shop.adm.seoHelp")),
                Ui.Input("seo_title", ctx.T("shop.adm.seoTitle"), product.SeoTitle) with { MaxLength = 120, Placeholder = product.Title },
                Ui.TextArea("seo_description", ctx.T("shop.adm.seoDescription"), product.SeoDescription, 3) with { Placeholder = product.Summary }),
            ShopAdminTranslations.ProductCard(ctx, translations)
        };
        var side = new List<UiNode?>
        {
            Ui.Card(ctx.T("shop.adm.secStatus"),
                Ui.Checkbox("active", ctx.T("shop.adm.activeLabel"), product.Active || id == 0),
                Ui.Checkbox("featured", ctx.T("shop.adm.featured"), product.Featured) with { Help = ctx.T("shop.adm.featuredHelp") },
                Ui.Select("kind", ctx.T("shop.adm.kind"), product.Kind, Ui.Option(ProductKind.Physical, ctx.T("shop.physical")), Ui.Option(ProductKind.Digital, ctx.T("shop.digital")))),
            Ui.Card(ctx.T("shop.adm.secPrice"),
                Ui.Input("price", ctx.T("shop.adm.priceGross", cfg.Currency), Money.ForInput(product.PriceCents)) with { Required = true, Help = ctx.T("shop.adm.priceHelp") },
                Ui.Input("compare", ctx.T("shop.adm.compare", cfg.Currency), product.CompareCents > 0 ? Money.ForInput(product.CompareCents) : "") with { Help = ctx.T("shop.adm.compareHelp") },
                Ui.Select("tax", ctx.T("shop.adm.taxPercent"), Money.Percent(product.TaxBp), ShopTaxChoices.ForProduct(ctx, cfg, product.TaxBp)) with { Help = ctx.T(cfg.SmallBusiness ? "shop.adm.taxIgnored" : "shop.adm.taxHelp") }),
            Ui.Card(ctx.T("shop.adm.secPlace"),
                Ui.Select("category", ctx.T("shop.adm.category"), product.Category, new[] { Ui.Option("", "—") }.Concat(ShopCategories.Flatten(cats).Select(t => Ui.Option(t.Cat.Slug, ShopCategories.Label(cats, t.Cat)))).ToArray()),
                allCats.Count > 1 ? Ui.Text(ctx.T("shop.adm.extraCategories")) with { Strong = true } : null,
                allCats.Count > 1 ? Ui.Muted(ctx.T("shop.adm.extraCategoriesHelp")) : null,
                allCats.Count > 1 ? Ui.Grid(190, ShopCategories.Flatten(cats).Select(t => (UiNode?)Ui.Checkbox("xcat_" + t.Cat.Slug, ShopCategories.Label(cats, t.Cat), ShopMembership.SplitSlugs(product.ExtraCategories).Contains(t.Cat.Slug))).ToArray()) : null,
                Ui.Tags("tags", ctx.T("shop.adm.tags"), ShopTags.Normalise(product.Tags), knownTags) with { Help = ctx.T("shop.adm.tagsHelp") },
                Ui.Input("slug", ctx.T("shop.adm.slug"), product.Slug) with { Help = ctx.T("shop.adm.slugHelp") },
                Ui.Input("sku", ctx.T("shop.adm.sku"), product.Sku)),
            Ui.Card(ctx.T("shop.adm.secStock"),
                Ui.Checkbox("track_stock", ctx.T("shop.adm.trackStock"), product.TrackStock),
                Ui.Input("stock", ctx.T("shop.adm.stockCount"), Intl(product.Stock), UiInputType.Number) with { Help = ctx.T("shop.adm.stockHelp"), Min = "0" },
                Ui.Input("weight", ctx.T("shop.adm.weight"), Intl(product.WeightGrams), UiInputType.Number) with { Min = "0", Help = ctx.T("shop.adm.weightHelp") },
                Ui.Input("max", ctx.T("shop.adm.maxPerOrder"), Intl(product.MaxPerOrder), UiInputType.Number) with { Min = "0", Help = ctx.T("shop.adm.maxHelp") },
                Ui.Input("sort", ctx.T("shop.adm.sort"), Intl(product.Sort), UiInputType.Number))
        };
        hidden.Add(Ui.Columns(main, side));
        hidden.Add(Ui.FormActions(Ui.Submit(ctx.T("action.save")), Ui.Button(ctx.T("action.cancel"), $"{Section}/products", UiButtonKind.Ghost)));

        var nodes = new List<UiNode?> { Ui.PageHead(null, ctx.T(id == 0 ? "shop.adm.newProduct" : "shop.adm.editProduct"), Ui.Button(ctx.T("shop.adm.back"), $"{Section}/products", UiButtonKind.Ghost, icon: "ti-arrow-left", small: true),
            id > 0 && product.Active ? Ui.Button(ctx.T("shop.adm.view"), "/shop/" + Uri.EscapeDataString(product.Slug), UiButtonKind.Ghost, icon: "ti-external-link", small: true) with { NewTab = true } : null,
            id > 0 ? Ui.Action(ctx.T("shop.adm.duplicate"), "duplicate", null, UiButtonKind.Ghost, icon: "ti-copy") with { WithLabel = true } : null), Ui.Form(hidden.ToArray()) };

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

    /// <summary>The card with a field for every attribute that applies to the product's category: a list to pick from, a line of text, a number, a tick.</summary>
    private static UiNode? AttributesCard(IPluginContext ctx, IReadOnlyList<ShopAttribute> attrs, int totalAttributes, IReadOnlyDictionary<long, List<string>> values)
    {
        if (totalAttributes == 0)
            return Ui.Card(ctx.T("shop.adm.secAttributes"), Ui.Muted(ctx.T("shop.adm.attrsNone")), Ui.Button(ctx.T("shop.adm.attrsManage"), $"{Section}/attributes/0", UiButtonKind.Ghost, small: true));
        var fields = new List<UiNode?> { Ui.Hidden("attrs", string.Join(",", attrs.Select(a => Intl(a.Id)))) };
        foreach (var a in attrs)
        {
            var name = "attr_" + a.Id;
            var have = values.TryGetValue(a.Id, out var v) ? v : new List<string>();
            fields.Add(a.Kind switch
            {
                AttributeKind.Bool => Ui.Checkbox(name, a.Name, have.Count > 0),
                AttributeKind.Number => Ui.Input(name, a.Unit.Length > 0 ? $"{a.Name} ({a.Unit})" : a.Name, have.FirstOrDefault() ?? "", UiInputType.Number) with { Step = "any" },
                AttributeKind.Text => Ui.Input(name, a.Name, have.FirstOrDefault() ?? ""),
                _ => Ui.Tags(name, a.Name, string.Join(", ", have), a.OptionList) with { Help = ctx.T("shop.adm.attrChoiceHelp") }
            });
        }
        fields.Add(Ui.Muted(ctx.T("shop.adm.attrsHelp")));
        fields.Add(Ui.Button(ctx.T("shop.adm.attrsManage"), $"{Section}/attributes", UiButtonKind.Ghost, small: true));
        return Ui.Card(ctx.T("shop.adm.secAttributes"), fields.ToArray());
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;

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
            SeoTitle = Clip(f.F("seo_title").Trim(), 120), SeoDescription = Clip(f.F("seo_description").Trim(), 320),
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
    /// The variants card: what variants are, the options that make them (name and values as chips, "Kombinationen erzeugen" adds every combination that is not there yet), and the variants themselves as rows
    /// — name, price difference, stock, article number — to change, add and delete in place.
    /// </summary>
    private static UiNode VariantsCard(IPluginContext ctx, ShopConfig cfg, Product product, IReadOnlyList<VariantOption> options, IReadOnlyList<VariantRow> rows)
    {
        UiNode Option(int i)
        {
            var o = i < options.Count ? options[i] : null;
            return Ui.Stack(
                Ui.Input($"opt_name_{i + 1}", ctx.T("shop.adm.var.optionName", i + 1), o?.Name ?? "") with { Placeholder = i == 0 ? ctx.T("shop.adm.var.optionEx1") : i == 1 ? ctx.T("shop.adm.var.optionEx2") : "", MaxLength = ShopVariants.MaxName },
                Ui.Tags($"opt_values_{i + 1}", ctx.T("shop.adm.var.optionValues"), string.Join(", ", o?.Values ?? Array.Empty<string>())));
        }
        string Delta(long c) => c == 0 ? "" : (c > 0 ? "+" : "") + Money.ForInput(c);
        var editor = Ui.RowEditor("variants", "",
            new[]
            {
                Ui.RowColumn("name", ctx.T("shop.adm.var.colName"), UiRowCell.Text, ctx.T("shop.adm.var.namePh")),
                Ui.RowColumn("image", ctx.T("shop.adm.var.colImage"), UiRowCell.Image, ctx.T("shop.adm.var.imagePh"), "14em"),
                Ui.RowColumn("delta", ctx.T("shop.adm.var.colDelta", cfg.Currency), UiRowCell.Number, "0,00", "9.5em"),
                Ui.RowColumn("stock", ctx.T("shop.adm.var.colStock"), UiRowCell.Number, ctx.T("shop.adm.var.stockPh"), "8em"),
                Ui.RowColumn("sku", ctx.T("shop.adm.var.colSku"), UiRowCell.Text, null, "10em")
            },
            rows.Select(r => Ui.RowValue(r.Id > 0 ? Intl(r.Id) : "", r.Name, r.Image, Delta(r.DeltaCents), r.Stock is { } st ? Intl(st) : "", r.Sku)),
            ctx.T("shop.adm.var.add")) with { EmptyText = ctx.T("shop.adm.var.empty"), MaxRows = ShopVariants.MaxVariants, Help = ctx.T("shop.adm.var.listHelp") };
        return Ui.Card(ctx.T("shop.adm.variants"),
            Ui.Muted(ctx.T("shop.adm.var.intro", Money.Format(product.PriceCents, cfg.Currency, "de"))),
            editor,
            Ui.Text(ctx.T("shop.adm.var.optionsTitle")) with { Strong = true },
            Ui.Muted(ctx.T("shop.adm.var.optionsHelp")),
            Ui.Grid(260, Option(0), Option(1), Option(2)),
            Ui.Inline(Ui.Submit(ctx.T("shop.adm.var.generate"), UiButtonKind.Ghost, "generate", "1", icon: "ti-wand", small: true)));
    }

    /// <summary>
    /// Brings the stored variants in line with the rows of the editor. A row that carries a variant's id IS that variant (renaming it keeps its stock and what old orders point at); a new row
    /// brings back a switched-off variant of the same name when there is one. What is no longer among the rows is switched off, never deleted — an old order's line and the stock it must give back
    /// still refer to it.
    /// </summary>
    private static async Task SaveVariantsAsync(ShopStore store, long productId, List<VariantRow> wanted, CancellationToken ct)
    {
        var existing = await store.VariantsAsync(productId, false, ct);
        var claimed = wanted.Where(w => w.Id > 0 && existing.Any(v => v.Id == w.Id)).Select(w => w.Id).ToHashSet();
        var sort = 0;
        foreach (var w in wanted)
        {
            var current = w.Id > 0 ? existing.FirstOrDefault(v => v.Id == w.Id) : null;
            if (current is null)
            {
                current = existing.FirstOrDefault(v => !claimed.Contains(v.Id) && string.Equals(v.Name, w.Name, StringComparison.OrdinalIgnoreCase));
                if (current is not null) claimed.Add(current.Id);
            }
            await store.SaveVariantAsync((current ?? new Variant { ProductId = productId }) with
            {
                Name = w.Name, Sku = w.Sku, PriceDeltaCents = w.DeltaCents, TrackStock = w.Stock is not null, Stock = w.Stock ?? 0, Active = true, Sort = sort++, Image = w.Image
            }, ct);
        }
        foreach (var gone in existing.Where(v => v.Active && !claimed.Contains(v.Id)))
            await store.SaveVariantAsync(gone with { Active = false }, ct);
    }

    // ---- categories --------------------------------------------------------------------------------------

    private static string CategoryUrl(long id, long parent = 0) => $"{Section}/categories/{id}" + (id == 0 && parent > 0 ? "?parent=" + Intl(parent) : "");

    /// <summary>The category tree as a list: a parent, its children indented below it, picture, description's first words, product counts and what can be done.</summary>
    public static async Task<PluginResult> CategoriesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        if (ctx.Request.IsPost && ctx.Request.Action == "delete" && long.TryParse(ctx.Request.F("id"), out var del))
        {
            await store.DeleteCategoryAsync(del, ct);
            return PluginResult.Redirect($"{Section}/categories", ctx.T("shop.adm.categoryDeleted"));
        }
        var cats = await store.CategoriesAsync(ct);
        var own = (await store.ProductsAsync(false, ct: ct)).Items.Where(p => p.Category.Length > 0).GroupBy(p => p.Category).ToDictionary(g => g.Key, g => g.Count());
        var total = ShopCategories.RollUp(cats, own);
        static string E(string? t) => System.Net.WebUtility.HtmlEncode(t ?? "");
        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("shop.adm.category")), Ui.Column(ctx.T("shop.adm.products"), right: true), Ui.Column(ctx.T("shop.adm.sort"), right: true), Ui.Column("", actions: true) },
            ShopCategories.Flatten(cats).Select(t =>
            {
                var c = t.Cat;
                var img = ShopPublic.SafeUrl(c.Image);
                var thumb = img.Length > 0 ? $"<span class=\"ui-thumb\" style=\"background-image:url('{E(img)}')\"></span>" : "<span class=\"ui-thumb empty\"><i class=\"ti ti-category\"></i></span>";
                var desc = c.Description.Length > 90 ? c.Description[..90] + "…" : c.Description;
                var cell = $"<div class=\"ui-media-cell\" style=\"padding-left:{t.Depth * 28}px\">{(t.Depth > 0 ? "<i class=\"ti ti-corner-down-right muted\" aria-hidden=\"true\"></i>" : "")}{thumb}<div><a href=\"{CategoryUrl(c.Id)}\"><strong>{E(c.Name)}</strong></a><div class=\"muted\">{E(c.Slug)}{(desc.Length > 0 ? " · " + E(desc) : "")}{(c.Tags.Length > 0 ? " · <i class=\"ti ti-tag\" aria-hidden=\"true\"></i> " + E(c.Tags) : "")}</div></div></div>";
                var n = own.GetValueOrDefault(c.Slug); var all = total.GetValueOrDefault(c.Slug);
                return Ui.RowFor($"{c.Name} {c.Slug} {c.Description} {c.Tags}",
                    Ui.Html(cell),
                    Ui.Text(all == n ? Intl(n) : $"{Intl(n)} ({Intl(all)})") with { Inline = true },
                    Ui.Text(Intl(c.Sort)) with { Inline = true, Muted = true },
                    Ui.Inline(
                        Ui.Button(ctx.T("action.edit"), CategoryUrl(c.Id), UiButtonKind.Ghost, small: true),
                        Ui.Button(ctx.T("shop.adm.addSub"), CategoryUrl(0, c.Id), UiButtonKind.Ghost, icon: "ti-plus", small: true) with { IconOnly = true },
                        Ui.Action(ctx.T("action.delete"), "delete", Intl(c.Id), UiButtonKind.Danger, confirm: ctx.T("shop.adm.deleteCategoryConfirm", c.Name), icon: "ti-trash")));
            }),
            searchPlaceholder: ctx.T("action.search"), emptyText: ctx.T("shop.adm.noCategories")) with { Below = new UiNode[] { Ui.Button(ctx.T("shop.adm.newCategory"), CategoryUrl(0)) } };
        return PluginResult.Page(Ui.Stack(table), ctx.T("shop.adm.categories"));
    }

    /// <summary>One category: name, picture, text, where it sits in the tree. The address (slug) is chosen once, when it is created — products refer to it.</summary>
    public static async Task<PluginResult> CategoryAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        long.TryParse(ctx.Request.R("id"), out var id);
        var current = await store.CategoryAsync(id, ct);
        if (id > 0 && current is null) return PluginResult.Redirect($"{Section}/categories");
        current ??= new Category { ParentId = long.TryParse(ctx.Request.Q("parent"), out var pre) ? pre : 0 };
        var all = await store.CategoriesAsync(ct);
        string? error = null;
        var otherLocales = ShopAdminTranslations.Locales(ctx);
        var translations = id == 0 ? new Dictionary<string, Translation>() : await store.TranslationsOfAsync("category", id, ct);

        if (ctx.Request.IsPost && ctx.Request.Action == "save")
        {
            var f = ctx.Request;
            translations = ShopAdminTranslations.ReadCategory(f, otherLocales);
            var name = f.F("name").Trim();
            var desc = f.F("description").Trim();
            current = current with
            {
                Name = name, Description = desc.Length > 2000 ? desc[..2000] : desc, Image = ShopPublic.SafeUrl(f.F("image")), Tags = ShopTags.Normalise(f.F("tags")), Rule = ShopCollectionRule.Clean(f.F("rule")),
                Sort = int.TryParse(f.F("sort"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var so) ? Math.Clamp(so, 0, 100000) : 0,
                ParentId = long.TryParse(f.F("parent"), out var pa) ? pa : 0
            };
            if (name.Length == 0) error = ctx.T("shop.adm.errCategoryName");
            else if (id == 0)
            {
                var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(string.IsNullOrWhiteSpace(f.F("slug")) ? name : f.F("slug"));
                if (string.IsNullOrWhiteSpace(slug)) slug = "kategorie";
                if (ShopPublic.ReservedSlugs.Contains(slug)) slug += "-1";
                var baseSlug = slug; var n = 2;
                while (await store.CategoryBySlugAsync(slug, ct) is not null) slug = baseSlug + "-" + n++;
                var created = await store.SaveCategoryAsync(current with { Slug = slug }, ct);
                await ShopAdminTranslations.SaveAsync(store, "category", created, otherLocales, translations, ct);
                return PluginResult.Redirect($"{Section}/categories", ctx.T("shop.adm.categorySaved"));
            }
            else
            {
                await store.SaveCategoryAsync(current, ct);
                await ShopAdminTranslations.SaveAsync(store, "category", id, otherLocales, translations, ct);
                return PluginResult.Redirect($"{Section}/categories", ctx.T("shop.adm.categorySaved"));
            }
        }

        // A parent can be any category except this one and what is below it (that would be a loop).
        var below = id > 0 ? ShopCategories.IdsUnder(all, id) : new HashSet<long>();
        var parents = new[] { Ui.Option("0", ctx.T("shop.adm.topLevel")) }
            .Concat(ShopCategories.Flatten(all).Where(t => !below.Contains(t.Cat.Id)).Select(t => Ui.Option(Intl(t.Cat.Id), string.Concat(Enumerable.Repeat("— ", t.Depth)) + t.Cat.Name))).ToArray();
        var main = new List<UiNode?>
        {
            Ui.Card(ctx.T("shop.adm.secCategory"),
                Ui.Input("name", ctx.T("shop.adm.categoryName"), current.Name) with { Required = true },
                Ui.TextArea("description", ctx.T("shop.adm.categoryDescription"), current.Description, 4) with { Help = ctx.T("shop.adm.categoryDescriptionHelp") },
                Ui.Input("tags", ctx.T("shop.adm.categoryTags"), current.Tags) with { Help = ctx.T("shop.adm.categoryTagsHelp") },
                Ui.Image("image", ctx.T("shop.adm.categoryImage"), current.Image)),
            Ui.Card(ctx.T("shop.adm.collection"),
                Ui.Muted(ctx.T("shop.adm.collectionHelp")),
                Ui.Input("rule", ctx.T("shop.adm.collectionRule"), current.Rule) with { Placeholder = "tag:Neuware; price<20", Help = ctx.T("shop.adm.collectionRuleHelp") }),
            ShopAdminTranslations.CategoryCard(ctx, translations)
        };
        var side = new List<UiNode?>
        {
            Ui.Card(ctx.T("shop.adm.secPlace"),
                Ui.Select("parent", ctx.T("shop.adm.parentCategory"), Intl(current.ParentId), parents),
                Ui.Input("sort", ctx.T("shop.adm.sort"), Intl(current.Sort), UiInputType.Number) with { Min = "0" },
                id == 0 ? Ui.Input("slug", ctx.T("shop.adm.slug"), "") with { Help = ctx.T("shop.adm.categorySlugHelp") } : Ui.KeyValue((ctx.T("shop.adm.slug"), current.Slug)))
        };
        var title = ctx.T(id == 0 ? "shop.adm.newCategory" : "shop.adm.editCategory");
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, title, Ui.Button(ctx.T("shop.adm.back"), $"{Section}/categories", UiButtonKind.Ghost, icon: "ti-arrow-left", small: true),
                id > 0 ? Ui.Button(ctx.T("shop.adm.view"), ShopUrls.Category(current.Slug), UiButtonKind.Ghost, icon: "ti-external-link", small: true) with { NewTab = true } : null),
            error is null ? null : Ui.Alert(error, UiTone.Error),
            Ui.Form(Ui.Hidden("action", "save"), Ui.Columns(main, side), Ui.FormActions(Ui.Submit(ctx.T("action.save")), Ui.Button(ctx.T("action.cancel"), $"{Section}/categories", UiButtonKind.Ghost)))), title);
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
        return PluginResult.Page(ShopConditions.Frame(ctx, "shipping", Ui.PageHead(ctx.T("shop.adm.shippingHelp"), ctx.T("shop.adm.shipping")), Ui.Card(Ui.Form(kids.ToArray()))), ctx.T("shop.adm.shipping"));
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
            customers.Select(c => (Ui.RowFor($"{c.Email} {c.Name}",
                Ui.Inline(Ui.Link(string.IsNullOrWhiteSpace(c.Name) ? c.Email : c.Name, ShopAdminCustomers.Url(c.Email)), Ui.Text(c.Email) with { Inline = true, Muted = true }),
                c.MemberId > 0 ? Ui.Badge(ctx.T("shop.adm.hasAccount"), UiTone.Success) : Ui.Badge(ctx.T("shop.adm.guest"), UiTone.Muted),
                c.Orders, Money.Format(c.SpentCents, cfg.Currency, "de"), c.LastOrderUtc.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                Ui.Button(ctx.T("shop.adm.open"), ShopAdminCustomers.Url(c.Email), UiButtonKind.Ghost, small: true)) with { Tags = c.MemberId > 0 ? "account" : "guest" })),
            searchPlaceholder: ctx.T("action.search"), pageSize: 25, emptyText: ctx.T("shop.adm.noCustomers")) with
        {
            Filter = Ui.ListFilter(ctx.T("shop.adm.filter"), ("", $"{ctx.T("shop.adm.all")} ({customers.Count})"), ("account", $"{ctx.T("shop.adm.hasAccount")} ({customers.Count(c => c.MemberId > 0)})"), ("guest", $"{ctx.T("shop.adm.guest")} ({customers.Count(c => c.MemberId <= 0)})"))
        };
        return PluginResult.Page(Ui.Stack(table), ctx.T("shop.adm.customers"));
    }
}
