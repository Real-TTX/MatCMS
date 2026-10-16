using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using static MatCMS.Plugin.Shop.ShopHtml;

namespace MatCMS.Plugin.Shop;

/// <summary>The pages a visitor sees: catalogue, product, cart, checkout, the order's own page and the invoice.</summary>
internal static partial class ShopPublic
{
    /// <summary>Words a product's address cannot be: they are the shop's own pages.</summary>
    public static readonly string[] ReservedSlugs = { "categories", "tags", "cart", "checkout", "order", "download", "invoice", "webhook", "pay", "account", "suggest" };

    internal static string M(IPluginContext ctx, ShopConfig cfg, long cents) => Money.Format(cents, cfg.Currency, ctx.Locale);
    private static string Csrf(IPluginContext ctx) => ctx.Antiforgery;
    private static string Intl(long n) => n.ToString(CultureInfo.InvariantCulture);

    // ---- catalogue ---------------------------------------------------------------------------------------

    private static string CatalogUrl(string cat, string tag, string q, string sort, int page = 1, IEnumerable<KeyValuePair<string, string>>? extra = null) => ShopUrls.Catalog(cat, tag, q, sort, page, extra);

    internal static string SortLabel(IPluginContext ctx, string sort) => ctx.T(sort switch { "new" => "shop.sort.new", "price_asc" => "shop.sort.priceAsc", "price_desc" => "shop.sort.priceDesc", "name" => "shop.sort.name", _ => "shop.sort.manual" });

    public static async Task<PluginResult> CatalogAsync(IPluginContext ctx, CancellationToken ct) => await ShopPageHost.RunAsync(ctx, "catalog", await CatalogViewAsync(ctx, ct), ct);

    /// <summary>What the catalogue says for the request: the values of its page and what it draws without a page of the editors'.</summary>
    internal static async Task<PageView> CatalogViewAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = ShopStore.Public(ctx);
        var tag = ctx.Request.Q("tag").Trim(); var q = ctx.Request.Q("q").Trim();
        var sort = ProductQuery.NormaliseSort(ctx.Request.Q("sort"));
        var page = int.TryParse(ctx.Request.Q("page"), out var pg) && pg > 0 ? pg : 1;
        // The category is part of the address (/shop/categories/{slug}); the old /shop?cat=… moved there for good.
        var cat = ctx.Request.R("slug").Trim();
        if (cat.Length == 0 && ctx.Request.Q("cat").Trim() is { Length: > 0 } old)
            return PageView.Done(PluginResult.RedirectPermanent(ShopUrls.Catalog(old, tag, q, sort, page)));
        var cats = await store.CategoriesAsync(ct);
        var pq = new ProductQuery { Category = cat.Length > 0 ? cat : null, Tag = tag.Length > 0 ? tag : null, Search = q.Length > 0 ? q : null, SearchIn = cfg.SearchIn, Sort = sort };
        var (pMin, pMax) = cfg.ShowFilters ? ShopCatalogUi.ReadPrice(ctx.Request.Q) : ((long?)null, (long?)null);

        // Attribute filters (colour, size …) and the price range: the attributes this page may offer, what the address asks for, and — counted over what the page would show WITHOUT the filters — what is left to pick.
        var filterAttrs = cfg.ShowFilters ? (await store.AttributesAsync(ct)).Where(a => a.Filterable && ShopAttributes.AppliesTo(a, cats, cat)).ToList() : new List<ShopAttribute>();
        var filters = ShopAttributes.ReadFilters(filterAttrs, ctx.Request.Q);
        var state = new CatalogState(cat, tag, q, sort, filters, pMin, pMax);
        var fx = state.Extra();
        var facets = new Dictionary<long, AttributeFacet>();
        IReadOnlyCollection<long>? only = null;
        (long Lo, long Hi)? priceExtent = null;
        if (cfg.ShowFilters)
        {
            var baseItems = (await store.FindProductsAsync(pq, ct)).Items;
            if (baseItems.Count > 0) priceExtent = (baseItems.Min(p => p.PriceCents), baseItems.Max(p => p.PriceCents));
            if (filterAttrs.Count > 0)
            {
                var baseIds = baseItems.Select(p => p.Id).ToList();
                var rows = await store.AttrRowsAsync(baseIds, filterAttrs.Select(a => a.Id).ToList(), ct);
                facets = ShopAttributes.Facets(filterAttrs, rows);
                if (filters.Count > 0) only = ShopAttributes.Match(baseIds, filters, rows).ToList();
            }
        }
        var (items, total) = await store.FindProductsAsync(pq with { Ids = only, MinCents = pMin, MaxCents = pMax, Skip = (page - 1) * cfg.PerPage, Take = cfg.PerPage }, ct);
        var (catCounts, tags) = await store.FacetsAsync(ct);
        var variants = (await store.VariantsOfAsync(items.Select(p => p.Id), ct)).Where(v => v.Active).GroupBy(v => v.ProductId).ToDictionary(g => g.Key, g => g.ToList());

        // The page speaks of the category it shows, when there is one (also the title a search engine sees).
        var current = cats.FirstOrDefault(c => c.Slug == cat);
        if (cat.Length > 0 && current is null)
            return PageView.Done(await PageAsync(ctx, cfg, ctx.T("shop.notFound"), $"<h1>{E(ctx.T("shop.notFound"))}</h1><p><a href=\"/shop\">{E(ctx.T("shop.backToShop"))}</a></p>", status: 404, noIndex: true));
        var path = current is null ? new List<Category>() : ShopCategories.Path(cats, cat);
        var title = current?.Name ?? (q.Length > 0 ? ctx.T("shop.resultsFor", q) : ctx.T("shop.title"));
        var search = ShopCatalogUi.Find(ctx, cfg, state);
        var sortHtml = ShopCatalogUi.Sort(ctx, cfg, state);
        var activeHtml = ShopCatalogUi.Active(ctx, cfg, state);
        var cats2 = ShopCatalogUi.CategoryNav(ctx, cfg, state, cats, catCounts, path);
        var filtersHtml = cfg.ShowFilters ? ShopCatalogUi.Filters(ctx, cfg, state, filterAttrs, facets, priceExtent) : "";
        // On a category's page: where it sits, its picture and text, and its subcategories as tiles.
        var crumbsHtml = new StringBuilder();
        var subHtml = new StringBuilder();
        var imageHtml = "";
        if (current is not null)
        {
            crumbsHtml.Append($"<p class=\"shop-crumbs\"><a href=\"/shop\">{E(ctx.T("shop.title"))}</a>");
            foreach (var c in path) crumbsHtml.Append(c.Id == current.Id ? $" › {E(c.Name)}" : $" › <a href=\"{E(CatalogUrl(c.Slug, "", "", ""))}\">{E(c.Name)}</a>");
            crumbsHtml.Append("</p>");
            if (ShopPublic.SafeUrl(current.Image) is { Length: > 0 } hero) imageHtml = $"<div class=\"shop-cathero\" style=\"background-image:url('{E(hero)}')\" role=\"img\" aria-label=\"{E(current.Name)}\"></div>";
            var kids = ShopCategories.Children(cats, current.Id).Where(c => catCounts.ContainsKey(c.Slug)).ToList();
            if (kids.Count > 0)
            {
                subHtml.Append("<div class=\"shop-subcats\">");
                foreach (var c in kids) subHtml.Append(CategoryTile(c, catCounts.GetValueOrDefault(c.Slug), CatalogUrl(c.Slug, "", "", "")));
                subHtml.Append("</div>");
            }
        }
        var tags2 = new StringBuilder();
        if (tags.Count > 0 && cfg.ShowTags)
        {
            tags2.Append($"<div class=\"shop-fgroup shop-tagsgroup\"><h3>{E(ctx.T("shop.tags"))}</h3><div class=\"shop-tags\" aria-label=\"{E(ctx.T("shop.tags"))}\">");
            // The busiest keywords, and always the one being filtered by.
            foreach (var t in tags.Where((x, i) => i < 14 || string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase)))
            {
                var on = string.Equals(t.Tag, tag, StringComparison.OrdinalIgnoreCase);
                tags2.Append($"<a href=\"{E(CatalogUrl(cat, on ? "" : t.Tag, q, sort, 1, fx))}\" class=\"{(on ? "on" : "")}\">{E(t.Tag)}</a>");
            }
            tags2.Append("</div></div>");
        }
        var filtered = state.Filtered;
        var count = $"<p class=\"shop-count\">{E(total == 1 ? ctx.T("shop.resultOne") : ctx.T("shop.resultMany", total))}</p>";
        var grid = new StringBuilder();
        var pager = new StringBuilder();
        if (items.Count == 0)
        {
            // "Meinten Sie …?": a word of the search that is nearly one of the shop's own (titles, keywords, categories).
            var dym = q.Length > 0 ? ShopSuggest.DidYouMean(q, await ShopSuggest.VocabularyAsync(store, ct)) : null;
            var dymHtml = dym is null ? "" : $"<p class=\"shop-dym\">{E(ctx.T("shop.sg.didYouMean"))} <a href=\"{E(ShopUrls.Catalog(cat, tag, dym, sort))}\"><strong>{E(dym)}</strong></a>?</p>";
            grid.Append(filtered && (q.Length > 0 || tag.Length > 0 || filters.Count > 0 || state.HasPrice)
                ? $"<div class=\"shop-nomatch\"><p>{E(ctx.T("shop.noMatch"))}</p>{dymHtml}<a class=\"shop-btn\" href=\"{E(ShopUrls.Catalog(cat, "", "", ""))}\" rel=\"nofollow\">{E(ctx.T("shop.resetFilter"))}</a></div>"
                : $"<p class=\"shop-muted\">{E(filtered ? ctx.T("shop.noMatch") : ctx.T("shop.empty"))}</p>");
        }
        else
        {
            grid.Append("<div class=\"shop-grid\">");
            foreach (var p in items) grid.Append(CardHtml(ctx, cfg, p, variants.TryGetValue(p.Id, out var vs) ? vs : new List<Variant>(), new CardView(true, true, true)));
            grid.Append("</div>");
            var pages = (int)Math.Ceiling(total / (double)cfg.PerPage);
            if (pages > 1)
            {
                pager.Append("<div class=\"shop-pager\">");
                for (var i = 1; i <= pages; i++)
                    pager.Append(i == page ? $"<span class=\"on\">{i}</span>" : $"<a href=\"{E(CatalogUrl(cat, tag, q, sort, i, fx))}\">{i}</a>");
                pager.Append("</div>");
            }
        }
        // The filter area as ONE piece for a page laid out in blocks: categories, filters and keywords in a panel that a phone folds away (the button is its summary).
        var panel = cats2.Length + filtersHtml.Length + tags2.Length == 0 ? ""
            : $"<details class=\"shop-fpanel\" open><summary class=\"shop-ftbtn\">{E(ctx.T("shop.filters"))}</summary>{cats2}{filtersHtml}{tags2}</details>"
              + "<script>if(window.matchMedia('(max-width:900px)').matches){var fp=document.querySelectorAll('.shop-fpanel');for(var i=0;i<fp.length;i++)fp[i].removeAttribute('open')}</script>";
        var values = new Dictionary<string, string>
        {
            ["toolbar_html"] = "<div class=\"shop-toolbar\">" + count + sortHtml + "</div>", ["panel_html"] = panel, ["sort_html"] = sortHtml, ["active_html"] = activeHtml,
            ["title"] = title, ["search_html"] = search, ["categories_html"] = cats2, ["tags_html"] = tags2.ToString(), ["count_html"] = count,
            ["filter_label"] = ctx.T("shop.filters"), ["layout_class"] = cats2.Length + filtersHtml.Length + tags2.Length == 0 ? "no-side" : "",
            ["grid_html"] = grid.ToString(), ["pager_html"] = pager.ToString(), ["filters_html"] = filtersHtml, ["category"] = current?.Name ?? "", ["query"] = q, ["total"] = Intl(total),
            ["crumbs_html"] = crumbsHtml.ToString(), ["category_description"] = current?.Description ?? "", ["category_image_url"] = current is null ? "" : SafeUrl(current.Image),
            ["category_image_html"] = imageHtml, ["subcategories_html"] = subHtml.ToString()
        };
        return new PageView(null, title, null, values, ctx.RenderPart("catalog", values));
    }

    /// <summary>
    /// /shop/categories — every category that holds something to buy: the top level as tiles and the whole tree as a list, handed to the template's `categories` part. With `?tag=Neuware` it shows the
    /// categories (at any level) that carry that keyword instead; the keywords in use on categories are offered as links above the tiles.
    /// </summary>
    public static async Task<PluginResult> CategoriesPageAsync(IPluginContext ctx, CancellationToken ct) => await ShopPageHost.RunAsync(ctx, "categories", await CategoriesViewAsync(ctx, ct), ct);

    internal static async Task<PageView> CategoriesViewAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = ShopStore.Public(ctx);
        var cats = await store.CategoriesAsync(ct);
        var (counts, _) = await store.FacetsAsync(ct);
        var tag = ctx.Request.Q("tag").Trim();
        var offered = ShopCategories.Flatten(cats).Select(t => t.Cat).Where(c => counts.ContainsKey(c.Slug)).ToList();
        var shown = tag.Length > 0
            ? offered.Where(c => ShopTags.Split(c.Tags).Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))).ToList()
            : ShopCategories.Roots(cats).Where(c => counts.ContainsKey(c.Slug)).ToList();
        var tiles = new StringBuilder();
        var list = new StringBuilder();
        if (shown.Count == 0) tiles.Append($"<p class=\"shop-muted\">{E(ctx.T("shop.categoriesEmpty"))}</p>");
        else
        {
            tiles.Append("<div class=\"shop-catgrid\">");
            foreach (var c in shown) tiles.Append(CategoryCard(ctx, cfg, c, counts[c.Slug], ShopCategories.Children(cats, c.Id).Where(k => counts.ContainsKey(k.Slug)).ToList(), counts));
            tiles.Append("</div>");
            void Branch(IEnumerable<Category> items)
            {
                list.Append("<ul class=\"shop-linklist\">");
                foreach (var c in items.Where(c => counts.ContainsKey(c.Slug)))
                {
                    list.Append($"<li><a href=\"{E(ShopUrls.Category(c.Slug))}\">{E(c.Name)} <span>{Intl(counts[c.Slug])}</span></a>");
                    if (tag.Length == 0 && ShopCategories.Children(cats, c.Id) is { Count: > 0 } below) Branch(below);
                    list.Append("</li>");
                }
                list.Append("</ul>");
            }
            Branch(shown);
        }
        var inUse = offered.SelectMany(c => ShopTags.Split(c.Tags)).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        var chips = new StringBuilder();
        if (inUse.Count > 0 && cfg.CatPageTags)
        {
            chips.Append("<div class=\"shop-cats\">");
            chips.Append($"<a href=\"{ShopUrls.CategoriesPage}\" class=\"{(tag.Length == 0 ? "on" : "")}\">{E(ctx.T("shop.allCategories"))}</a>");
            foreach (var t in inUse) chips.Append($"<a href=\"{E(ShopUrls.CategoriesPage + "?tag=" + Uri.EscapeDataString(t))}\" class=\"{(string.Equals(t, tag, StringComparison.OrdinalIgnoreCase) ? "on" : "")}\">{E(t)}</a>");
            chips.Append("</div>");
        }
        var heading = ctx.T("shop.categories");
        var title = tag.Length > 0 ? heading + " · " + tag : heading;
        tiles.Insert(0, $"<p class=\"shop-muted shop-catintro\">{E(ctx.T("shop.categoriesCount", shown.Count))}</p>");
        var crumbs = $"<p class=\"shop-crumbs\"><a href=\"/shop\">{E(ctx.T("shop.title"))}</a> › " + (tag.Length > 0 ? $"<a href=\"{ShopUrls.CategoriesPage}\">{E(heading)}</a> › {E(tag)}" : E(heading)) + "</p>";
        var values = new Dictionary<string, string>
        {
            ["title"] = title, ["total"] = Intl(tag.Length > 0 ? shown.Count : offered.Count), ["tag"] = tag, ["crumbs_html"] = crumbs, ["tags_html"] = chips.ToString(), ["tiles_html"] = tiles.ToString(), ["list_html"] = list.ToString()
        };
        return new PageView(null, title, null, values, ctx.RenderPart("categories", values)) { NoIndex = tag.Length > 0 };
    }

    /// <summary>/shop/tags — every keyword on the products, with how many each has, as links into the catalogue.</summary>
    public static async Task<PluginResult> TagsPageAsync(IPluginContext ctx, CancellationToken ct) => await ShopPageHost.RunAsync(ctx, "tags", await TagsViewAsync(ctx, ct), ct);

    internal static async Task<PageView> TagsViewAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var (_, tags) = await ShopStore.Public(ctx).FacetsAsync(ct);
        var html = new StringBuilder();
        if (tags.Count == 0) html.Append($"<p class=\"shop-muted\">{E(ctx.T("shop.tagsEmpty"))}</p>");
        else
        {
            html.Append("<div class=\"shop-tags shop-tagcloud\">");
            foreach (var (t, n) in tags) html.Append($"<a href=\"{E(ShopUrls.Catalog("", t))}\">{E(t)} <span class=\"shop-muted\">{Intl(n)}</span></a>");
            html.Append("</div>");
        }
        var title = ctx.T("shop.tags");
        var values = new Dictionary<string, string>
        {
            ["title"] = title, ["total"] = Intl(tags.Count), ["tags_html"] = html.ToString(),
            ["crumbs_html"] = $"<p class=\"shop-crumbs\"><a href=\"/shop\">{E(ctx.T("shop.title"))}</a> › {E(title)}</p>"
        };
        return new PageView(null, title, null, values, ctx.RenderPart("tags", values));
    }

    /// <summary>The words "1 product" / "12 products".</summary>
    internal static string ProductCount(IPluginContext ctx, int n) => n == 1 ? ctx.T("shop.productOne") : ctx.T("shop.productMany", n);

    /// <summary>A category as a card for the page of all categories: its picture (or a tinted block with the first letter), name, how many products, the text and its subcategories as links.</summary>
    private static string CategoryCard(IPluginContext ctx, ShopConfig cfg, Category c, int count, IReadOnlyList<Category> subs, IReadOnlyDictionary<string, int> counts)
    {
        var url = E(ShopUrls.Category(c.Slug));
        var img = SafeUrl(c.Image);
        var sb = new StringBuilder("<article class=\"shop-catcard\">");
        sb.Append(img.Length > 0
            ? $"<a class=\"img\" href=\"{url}\" style=\"background-image:url('{E(img)}')\" aria-label=\"{E(c.Name)}\"></a>"
            : $"<a class=\"img empty\" href=\"{url}\" aria-label=\"{E(c.Name)}\"><span>{E(c.Name.Length > 0 ? c.Name[..1].ToUpperInvariant() : "")}</span></a>");
        sb.Append($"<div class=\"body\"><h3><a href=\"{url}\">{E(c.Name)}</a></h3>");
        if (cfg.CatPageCounts) sb.Append($"<div class=\"count\">{E(ProductCount(ctx, count))}</div>");
        if (cfg.CatPageDesc && c.Description.Length > 0) sb.Append($"<p class=\"desc\">{E(c.Description)}</p>");
        if (cfg.CatPageSubs && subs.Count > 0)
        {
            sb.Append("<div class=\"subs\">");
            foreach (var k in subs.Take(6)) sb.Append($"<a href=\"{E(ShopUrls.Category(k.Slug))}\">{E(k.Name)}</a>");
            if (subs.Count > 6) sb.Append($"<a class=\"more\" href=\"{url}\">+{subs.Count - 6}</a>");
            sb.Append("</div>");
        }
        return sb.Append("</div></article>").ToString();
    }

    /// <summary>The attributes a product has, as a table for its page.</summary>
    private static async Task<string> AttributesTableAsync(IPluginContext ctx, ShopStore store, Product p, CancellationToken ct)
    {
        var attrs = await store.AttributesAsync(ct);
        var values = await store.ProductValuesAsync(p.Id, ct);
        var rows = attrs.Where(a => values.TryGetValue(a.Id, out var v) && v.Count > 0)
            .Select(a => $"<tr><th>{E(a.Name)}</th><td>{E(ShopAttributes.Show(a, values[a.Id], ctx.T("shop.attrYes"), ctx.Locale))}</td></tr>").ToList();
        return rows.Count == 0 ? "" : $"<h2 class=\"shop-attrh\">{E(ctx.T("shop.attributes"))}</h2><table class=\"shop-attrs\">{string.Concat(rows)}</table>";
    }

    /// <summary>A category as a tile: its picture (or a plain block), name and how many products it holds — the markup the catalogue and the category block share.</summary>
    internal static string CategoryTile(Category c, int count, string url)
    {
        var img = SafeUrl(c.Image);
        return $"<a class=\"shop-subcat\" href=\"{E(url)}\"><span class=\"img\"{(img.Length > 0 ? $" style=\"background-image:url('{E(img)}')\"" : "")}></span><span class=\"name\">{E(c.Name)}</span><span class=\"shop-muted\">{Intl(count)}</span></a>";
    }

    internal static bool SoldOut(Product p, IReadOnlyList<Variant> variants) =>
        variants.Count > 0 ? variants.All(v => v.TrackStock ? v.Stock <= 0 : p.TrackStock && p.Stock <= 0) : p.TrackStock && p.Stock <= 0;

    /// <summary>The price as shown: "ab …" when variants differ.</summary>
    internal static string PriceText(IPluginContext ctx, ShopConfig cfg, Product p, IReadOnlyList<Variant> variants)
    {
        if (variants.Count == 0) return M(ctx, cfg, p.PriceCents);
        var prices = variants.Select(v => Math.Max(0, p.PriceCents + v.PriceDeltaCents)).ToList();
        return prices.Min() == prices.Max() ? M(ctx, cfg, prices[0]) : ctx.T("shop.from", M(ctx, cfg, prices.Min()));
    }

    /// <summary>The struck-through earlier price and the saving, after the price — when the product is on sale and has no variants (a variant's own price would make the comparison untrue).</summary>
    internal static string WasHtml(IPluginContext ctx, ShopConfig cfg, Product p, IReadOnlyList<Variant> variants) =>
        p.OnSale && variants.Count == 0
            ? $" <s class=\"shop-was\" aria-label=\"{E(ctx.T("shop.was", M(ctx, cfg, p.CompareCents)))}\">{E(M(ctx, cfg, p.CompareCents))}</s>{(p.SavePercent >= 1 ? $" <span class=\"shop-badge ok\">{E(ctx.T("shop.save", p.SavePercent))}</span>" : "")}"
            : "";

    /// <summary>What a card shows beyond the picture and the title.</summary>
    internal readonly record struct CardView(bool Summary, bool Price, bool Tags)
    {
        public static readonly CardView Default = new(true, true, false);
    }

    internal static string CardHtml(IPluginContext ctx, ShopConfig cfg, Product p, IReadOnlyList<Variant> variants, CardView? view = null)
    {
        var v = view ?? CardView.Default;
        var img = SafeUrl(p.Image);
        var sold = SoldOut(p, variants);
        var url = "/shop/" + Uri.EscapeDataString(p.Slug);
        var price = PriceText(ctx, cfg, p, variants);
        var tags = ShopTags.Split(p.Tags);
        return ctx.RenderPart("card", new Dictionary<string, string>
        {
            ["url"] = url, ["title"] = p.Title, ["image_url"] = img, ["image_style"] = img.Length > 0 ? $" style=\"background-image:url('{E(img)}')\"" : "",
            ["summary"] = v.Summary ? Trunc(p.Summary, 110) : "",
            ["summary_html"] = v.Summary && p.Summary.Length > 0 ? $"<div class=\"shop-muted shop-small\">{E(Trunc(p.Summary, 110))}</div>" : "",
            ["price"] = v.Price ? price : "", ["price_old"] = v.Price && p.OnSale && variants.Count == 0 ? M(ctx, cfg, p.CompareCents) : "", ["save_percent"] = p.OnSale && variants.Count == 0 ? Intl(p.SavePercent) : "",
            ["price_html"] = v.Price ? $"<div class=\"shop-price\">{E(price)}{WasHtml(ctx, cfg, p, variants)}</div>" : "",
            ["tags_html"] = v.Tags && tags.Count > 0 ? "<div class=\"shop-tags\">" + string.Concat(tags.Select(t => $"<a href=\"/shop?tag={E(Uri.EscapeDataString(t))}\">{E(t)}</a>")) + "</div>" : "",
            ["badges_html"] = sold ? $"<div><span class=\"shop-badge bad\">{E(ctx.T("shop.soldOut"))}</span></div>" : p.IsDigital ? $"<div><span class=\"shop-badge\">{E(ctx.T("shop.digital"))}</span></div>" : "",
            ["sold_out"] = sold ? "1" : "", ["digital"] = p.IsDigital ? "1" : "", ["on_sale"] = p.OnSale && variants.Count == 0 ? "1" : "", ["category"] = p.Category
        });
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";

    /// <summary>A picture address that is safe in a CSS url('…') and an img src: an http(s) or local path, no quotes or brackets.</summary>
    internal static string SafeUrl(string url)
    {
        var u = (url ?? "").Trim();
        if (u.IndexOfAny(new[] { '\'', '"', '(', ')', '\\', '<', '>', ' ' }) >= 0) return "";
        if (u.StartsWith('/') && !u.StartsWith("//")) return u;
        return u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? u : "";
    }

    // ---- product -----------------------------------------------------------------------------------------

    public static async Task<PluginResult> ProductAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = ShopStore.Public(ctx);
        var slug = ctx.Request.R("slug");
        var p = ReservedSlugs.Contains(slug) ? null : await store.ProductBySlugAsync(slug, ct);
        // An address the product used to have: a permanent redirect to where it is now (links, bookmarks and search results keep working).
        if (p is null && !ReservedSlugs.Contains(slug) && await store.ProductByOldSlugAsync(slug, ct) is { } moved && (moved.Active || ctx.User.IsAdmin))
            return PluginResult.RedirectPermanent(ShopUrls.Product(moved.Slug));
        if (p is null || !p.Active && !ctx.User.IsAdmin)
            return await PageAsync(ctx, cfg, ctx.T("shop.notFound"), $"<h1>{E(ctx.T("shop.notFound"))}</h1><p><a href=\"/shop\">{E(ctx.T("shop.backToShop"))}</a></p>", status: 404, noIndex: true);
        return await ShopPageHost.RunAsync(ctx, "product", await ProductViewAsync(ctx, cfg, store, p, ct), ct);
    }

    /// <summary>What a product's page says: its values (also the variables of the page the editors laid out) and what it draws without one.</summary>
    internal static async Task<PageView> ProductViewAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, Product p, CancellationToken ct)
    {
        var variants = (await store.VariantsAsync(p.Id, true, ct)).ToList();
        var sold = SoldOut(p, variants);
        var gallery = ParseGallery(p.GalleryJson);
        var images = new StringBuilder();
        if (SafeUrl(p.Image).Length > 0) images.Append($"<img class=\"main-img\" src=\"{E(SafeUrl(p.Image))}\" alt=\"{E(p.Title)}\">");
        if (gallery is { Count: > 0 } gal)
        {
            // Several pictures: thumbnails under the main one (the main picture is the first of them). A click shows that picture in the main place, a click on the main picture opens it large
            // (arrow keys and the ‹ › buttons walk through all of them). Without script the thumbnails are plain links to the pictures.
            var all = new List<string>();
            if (SafeUrl(p.Image).Length > 0) all.Add(SafeUrl(p.Image));
            all.AddRange(gal);
            var gid = "gal-" + Intl(p.Id);
            images.Insert(0, $"<div class=\"shop-gal\" id=\"{gid}\">");
            images.Append("<div class=\"shop-thumbs\">");
            for (var i = 0; i < all.Count; i++)
                images.Append($"<a class=\"th{(i == 0 ? " on" : "")}\" href=\"{E(all[i])}\" data-src=\"{E(all[i])}\" target=\"_blank\" rel=\"noopener\"><img src=\"{E(all[i])}\" alt=\"\" loading=\"lazy\"></a>");
            images.Append("</div></div>");
            images.Append("<script>(function(){var r=document.getElementById('" + gid + "');if(!r)return;var m=r.querySelector('.main-img'),th=[].slice.call(r.querySelectorAll('.th'));if(!m){m=document.createElement('img');m.className='main-img';m.alt='';r.insertBefore(m,r.firstChild);m.src=th[0].dataset.src}"
                + "function show(i){i=(i+th.length)%th.length;m.src=th[i].dataset.src;th.forEach(function(x){x.classList.remove('on')});th[i].classList.add('on');return i}"
                + "th.forEach(function(b,i){b.addEventListener('click',function(e){e.preventDefault();show(i)})});"
                + "m.style.cursor='zoom-in';m.addEventListener('click',function(){var i=th.findIndex(function(x){return x.classList.contains('on')}),o=document.createElement('div');o.className='shop-lightbox';"
                + "o.innerHTML='<img alt=\"\"><button type=\"button\" class=\"x\" aria-label=\"×\">×</button>'+(th.length>1?'<button type=\"button\" class=\"p\" aria-label=\"‹\">‹</button><button type=\"button\" class=\"n\" aria-label=\"›\">›</button>':'');"
                + "var im=o.querySelector('img');im.src=th[i].dataset.src;function go(d){i=show(i+d);im.src=th[i].dataset.src}function close(){document.removeEventListener('keydown',key);o.remove()}"
                + "function key(e){if(e.key==='Escape')close();else if(e.key==='ArrowRight')go(1);else if(e.key==='ArrowLeft')go(-1)}"
                + "o.addEventListener('click',function(e){var c=e.target.className;if(c==='x'||e.target===o)close();else if(c==='p')go(-1);else if(c==='n')go(1)});document.addEventListener('keydown',key);document.body.appendChild(o)});})();</script>");
        }
        var catPath = p.Category.Length > 0 ? ShopCategories.Path(await store.CategoriesAsync(ct), p.Category) : new List<Category>();
        var category = catPath.LastOrDefault();
        var crumbs = $"<p class=\"shop-crumbs\"><a href=\"/shop\">{E(ctx.T("shop.title"))}</a>" + string.Concat(catPath.Select(c => $" › <a href=\"{E(ShopUrls.Category(c.Slug))}\">{E(c.Name)}</a>")) + "</p>";
        var notice = new StringBuilder();
        if (!p.Active) notice.Append(Alert(ctx.T("shop.inactiveNote"), "info"));
        if (sold) notice.Append($"<p><span class=\"shop-badge bad\">{E(ctx.T("shop.soldOut"))}</span></p>");
        if (!sold && p.TrackStock && p.Stock > 0 && p.Stock <= cfg.LowStock && variants.Count == 0) notice.Append($"<p class=\"shop-small\">{E(ctx.T("shop.onlyLeft", p.Stock))}</p>");
        var form = new StringBuilder();
        if (!sold)
        {
            form.Append($"<form method=\"post\" action=\"/shop/cart\" class=\"shop-form\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"add\"><input type=\"hidden\" name=\"product\" value=\"{Intl(p.Id)}\">");
            if (variants.Count > 0)
            {
                // One picker per option when the variants are made of the product's options (Größe, Farbe …); the plain list otherwise — and, without script, inside a noscript.
                var plan = ShopVariantPicker.Plan(ShopVariants.OptionsFromJson(p.VariantOptions), variants, p.PriceCents);
                var list = new StringBuilder($"<div><label>{E(ctx.T("shop.variant"))}</label><select name=\"variant\">");
                foreach (var v in variants)
                {
                    var out_ = v.TrackStock && v.Stock <= 0;
                    var delta = v.PriceDeltaCents == 0 ? "" : " (" + (v.PriceDeltaCents > 0 ? "+" : "") + M(ctx, cfg, v.PriceDeltaCents) + ")";
                    list.Append($"<option value=\"{Intl(v.Id)}\"{(out_ ? " disabled" : "")}>{E(v.Name + delta + (out_ ? " – " + ctx.T("shop.soldOut") : ""))}</option>");
                }
                list.Append("</select></div>");
                if (plan is null) form.Append(list);
                else form.Append(ShopVariantPicker.Html(ShopVariants.OptionsFromJson(p.VariantOptions), plan, c => M(ctx, cfg, c), ctx.T("shop.variantNA"))).Append("<noscript>").Append(list).Append("</noscript>");
            }
            form.Append($"<div class=\"shop-actions\"><input class=\"shop-qty\" type=\"number\" name=\"qty\" value=\"1\" min=\"1\" max=\"{(p.MaxPerOrder > 0 ? Intl(p.MaxPerOrder) : "999")}\"><button class=\"shop-btn\" type=\"submit\">{E(ctx.T("shop.addToCart"))}</button></div></form>");
        }
        var ptags = ShopTags.Split(p.Tags);
        var taxNote = (cfg.SmallBusiness ? ctx.T("shop.smallBusinessNote") : ctx.T("shop.taxIncluded", Money.Percent(p.TaxBp))) + (p.IsDigital ? "" : " · " + ctx.T("shop.plusShipping"));
        var values = new Dictionary<string, string>
        {
            ["url"] = ShopUrls.Product(p.Slug), ["on_sale"] = p.OnSale && variants.Count == 0 ? "1" : "",
            ["title"] = p.Title, ["summary"] = p.Summary, ["sku"] = p.Sku, ["category"] = category?.Name ?? "", ["category_url"] = category is null ? "" : ShopUrls.Category(category.Slug),
            ["image_url"] = SafeUrl(p.Image), ["image_html"] = SafeUrl(p.Image).Length > 0 || gallery.Count > 0 ? images.ToString() : "",
            ["price"] = PriceText(ctx, cfg, p, variants), ["price_old"] = p.OnSale && variants.Count == 0 ? M(ctx, cfg, p.CompareCents) : "", ["save_percent"] = p.OnSale && variants.Count == 0 ? Intl(p.SavePercent) : "",
            ["price_html"] = "<span data-shop-price>" + E(PriceText(ctx, cfg, p, variants)) + "</span>" + WasHtml(ctx, cfg, p, variants), ["tax_note"] = taxNote,
            ["breadcrumbs_html"] = crumbs, ["notice_html"] = notice.ToString(), ["form_html"] = form.ToString(), ["sold_out"] = sold ? "1" : "", ["digital"] = p.IsDigital ? "1" : "",
            ["digital_note"] = p.IsDigital ? ctx.T("shop.digitalNote") : "",
            ["attributes_html"] = await AttributesTableAsync(ctx, store, p, ct),
            ["tags_html"] = ptags.Count > 0 ? "<div class=\"shop-tags\" style=\"margin-top:14px\">" + string.Concat(ptags.Select(t => $"<a href=\"/shop?tag={E(Uri.EscapeDataString(t))}\">{E(t)}</a>")) + "</div>" : "",
            ["description_html"] = p.DescriptionHtml, ["description_label"] = ctx.T("shop.description"),      // sanitised when it was saved
            ["related_html"] = await RelatedAsync(ctx, cfg, store, p, ct)
        };
        // What a search engine and a shared link show: the title and description the editors wrote for them, else the product's own.
        var seoTitle = p.SeoTitle.Length > 0 ? p.SeoTitle : p.Title;
        var seoDescription = p.SeoDescription.Length > 0 ? p.SeoDescription : p.Summary.Length > 0 ? Trunc(p.Summary, 160) : null;
        var meta = new List<KeyValuePair<string, string>> { new("og:type", "product"), new("og:title", seoTitle), new("og:url", ctx.Site.Origin + ShopUrls.Product(p.Slug)) };
        if (seoDescription is not null) meta.Add(new("og:description", seoDescription));
        if (SafeUrl(p.Image).Length > 0) meta.Add(new("og:image", p.Image.StartsWith('/') ? ctx.Site.Origin + p.Image : p.Image));
        return new PageView(null, seoTitle, seoDescription, values, ctx.RenderPart("product", values)) { Tail = JsonLd(ctx, cfg, p, variants, sold), Meta = meta };
    }

    /// <summary>Up to four other products: first the ones of the same category, then those that share a keyword.</summary>
    private static async Task<string> RelatedAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, Product p, CancellationToken ct)
    {
        const int Want = 4;
        var found = new List<Product>();
        if (p.Category.Length > 0)
            found.AddRange((await store.FindProductsAsync(new ProductQuery { Category = p.Category, ExceptId = p.Id, Take = Want }, ct)).Items);
        foreach (var tag in ShopTags.Split(p.Tags))
        {
            if (found.Count >= Want) break;
            foreach (var other in (await store.FindProductsAsync(new ProductQuery { Tag = tag, ExceptId = p.Id, Take = Want }, ct)).Items)
                if (found.Count < Want && found.All(x => x.Id != other.Id)) found.Add(other);
        }
        if (found.Count == 0) return "";
        var variants = (await store.VariantsOfAsync(found.Select(x => x.Id), ct)).Where(v => v.Active).GroupBy(v => v.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var sb = new StringBuilder($"<h2>{E(ctx.T("shop.related"))}</h2><div class=\"shop-grid cols-4\">");
        foreach (var o in found) sb.Append(CardHtml(ctx, cfg, o, variants.TryGetValue(o.Id, out var vs) ? vs : new List<Variant>(), new CardView(false, true, false)));
        return sb.Append("</div>").ToString();
    }

    internal static List<string> ParseGalleryUrls(string json) => ParseGallery(json);

    private static List<string> ParseGallery(string json)
    {
        var list = new List<string>();
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) is JsonArray arr)
                foreach (var n in arr)
                    if ((n is JsonObject o ? o["url"]?.GetValue<string>() : n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null) is { } url && SafeUrl(url).Length > 0)
                        list.Add(SafeUrl(url));
        }
        catch { /* a damaged gallery shows nothing */ }
        return list;
    }

    /// <summary>schema.org Product markup for search engines (name, picture, price, availability).</summary>
    private static string JsonLd(IPluginContext ctx, ShopConfig cfg, Product p, IReadOnlyList<Variant> variants, bool sold)
    {
        var o = new JsonObject
        {
            ["@context"] = "https://schema.org", ["@type"] = "Product", ["name"] = p.Title, ["description"] = p.Summary.Length > 0 ? p.Summary : p.Title,
            ["offers"] = new JsonObject
            {
                ["@type"] = "Offer", ["priceCurrency"] = cfg.Currency, ["price"] = (Math.Max(0, p.PriceCents) / 100m).ToString("0.00", CultureInfo.InvariantCulture),
                ["availability"] = sold ? "https://schema.org/OutOfStock" : "https://schema.org/InStock", ["url"] = $"{ctx.Site.Origin}/shop/{Uri.EscapeDataString(p.Slug)}"
            }
        };
        if (SafeUrl(p.Image).Length > 0) o["image"] = p.Image.StartsWith('/') ? ctx.Site.Origin + p.Image : p.Image;
        if (p.Sku.Length > 0) o["sku"] = p.Sku;
        return "<script type=\"application/ld+json\">" + o.ToJsonString().Replace("<", "\\u003c") + "</script>";
    }

    // ---- cart --------------------------------------------------------------------------------------------

    public static async Task<PluginResult> CartAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = ShopStore.Public(ctx);
        var token = ShopCart.Token(ctx);

        // The link in a reminder mail: the cart it was written about comes back in THIS browser (the key is long and random; nothing else shows a cart).
        if (!ctx.Request.IsPost && ctx.Request.Q("restore").Length > 0 && await ShopReminders.FindForRestoreAsync(store, ctx.Request.Q("restore"), ct) is { } restored)
            return ShopCart.WithCartCookie(PluginResult.Redirect("/shop/cart"), restored.Token);

        // A coupon code is typed on the cart page. The page is drawn again with the answer (no redirect: the reason a code was refused is a sentence, not state).
        string? couponMessage = null; var couponTone = "info";
        var couponAction = ctx.Request.IsPost && ctx.Request.Action is "coupon" or "coupon-remove";
        if (couponAction && token.Length > 0 && await store.CartAsync(token, ct) is not null)
        {
            if (ctx.Request.Action == "coupon-remove") await store.SetCartCouponAsync(token, "", ct);
            else
            {
                var typed = Coupon.NormaliseCode(ctx.Request.F("code"));
                var pricedNow = await ShopOrders.PriceAsync(store, await ShopCart.LinesAsync(ctx, store, ct), cfg, ctx, ct);
                if (typed.Length == 0) { couponMessage = ctx.T("shop.coupon.empty"); couponTone = "error"; }
                else if (!ShopCoupons.AttemptAllowed(ctx.Request.ClientIp)) { couponMessage = ctx.T("shop.coupon.tooMany"); couponTone = "error"; }
                else
                {
                    var res = await ShopCoupons.ResolveAsync(ctx, store, pricedNow, typed, ctx.Member?.Email ?? "", ct);
                    if (res.Problem is not null) { ShopCoupons.RecordFailure(ctx.Request.ClientIp); couponMessage = res.Problem; couponTone = "error"; }
                    else { await store.SetCartCouponAsync(token, res.Coupon!.Code, ct); couponMessage = ctx.T("shop.coupon.applied", res.Coupon.Code); couponTone = "ok"; }
                }
            }
        }

        // "Remind me": the customer gives an address for ONE reminder mail about this cart, or takes it back.
        var remindAction = ctx.Request.IsPost && ctx.Request.Action is "remind" or "remind-off";
        if (remindAction && cfg.RemindEnabled && token.Length > 0)
        {
            if (ctx.Request.Action == "remind-off") { await store.SetCartReminderAsync(token, "", false, ct); couponMessage = ctx.T("shop.rem.offDone"); }
            else if (!ShopReminders.ValidEmail(ctx.Request.F("email").Trim())) { couponMessage = ctx.T("shop.rem.badMail"); couponTone = "error"; }
            else if (await store.SetCartReminderAsync(token, ctx.Request.F("email").Trim().ToLowerInvariant(), true, ct)) { couponMessage = ctx.T("shop.rem.onDone", ctx.Request.F("email").Trim()); couponTone = "ok"; }
        }

        if (ctx.Request.IsPost && !couponAction && !remindAction)
        {
            var lines = (await ShopCart.LinesAsync(ctx, store, ct)).ToList();
            switch (ctx.Request.Action)
            {
                case "add":
                {
                    long.TryParse(ctx.Request.F("product"), out var pid); long.TryParse(ctx.Request.F("variant"), out var vid);
                    var qty = int.TryParse(ctx.Request.F("qty"), out var q) ? Math.Clamp(q, 1, 999) : 1;
                    var p = await store.ProductAsync(pid, ct);
                    if (p is null || !p.Active) return PluginResult.Redirect("/shop/cart");
                    var hasVariants = (await store.VariantsAsync(pid, true, ct)).Count > 0;
                    if (!hasVariants) vid = 0;
                    var i = lines.FindIndex(l => l.ProductId == pid && l.VariantId == vid);
                    if (i >= 0) lines[i] = lines[i] with { Qty = Math.Min(999, lines[i].Qty + qty) }; else lines.Add(new CartLine(pid, vid, qty));
                    break;
                }
                case "update":
                    foreach (var kv in ctx.Request.Form.Where(k => k.Key.StartsWith("q:", StringComparison.Ordinal)))
                    {
                        var parts = kv.Key.Split(':');
                        if (parts.Length != 3 || !long.TryParse(parts[1], out var pid) || !long.TryParse(parts[2], out var vid)) continue;
                        var i = lines.FindIndex(l => l.ProductId == pid && l.VariantId == vid);
                        if (i < 0) continue;
                        if (!int.TryParse(kv.Value, out var qty) || qty <= 0) lines.RemoveAt(i); else lines[i] = lines[i] with { Qty = Math.Min(qty, 999) };
                    }
                    break;
                case "remove":
                {
                    var parts = ctx.Request.F("line").Split(':');
                    if (parts.Length == 2 && long.TryParse(parts[0], out var pid) && long.TryParse(parts[1], out var vid)) lines.RemoveAll(l => l.ProductId == pid && l.VariantId == vid);
                    break;
                }
                case "clear": lines.Clear(); break;
            }
            if (token.Length == 0) token = ShopOrders.NewToken();
            await store.SaveCartAsync(token, ctx.Member?.Id ?? 0, lines, ct);
            return ShopCart.WithCartCookie(PluginResult.Redirect("/shop/cart"), token);
        }

        var current = await ShopCart.LinesAsync(ctx, store, ct);
        var priced = await ShopOrders.PriceAsync(store, current, cfg, ctx, ct);
        if (priced.Changed && token.Length > 0) await store.SaveCartAsync(token, ctx.Member?.Id ?? 0, priced.Lines.Select(l => new CartLine(l.Product.Id, l.Variant?.Id ?? 0, l.Qty)).ToList(), ct);

        // The cart's coupon, checked again every time it is looked at: it may have run out, or the cart may have shrunk below its minimum.
        var cartRow = token.Length > 0 ? await store.CartAsync(token, ct) : null;
        if (cartRow is { Coupon.Length: > 0 } && priced.Lines.Count > 0)
        {
            var res = await ShopCoupons.ResolveAsync(ctx, store, priced, cartRow.Coupon, ctx.Member?.Email ?? "", ct);
            if (res.Problem is not null) { await store.SetCartCouponAsync(token, "", ct); couponMessage ??= ctx.T("shop.coupon.removed", cartRow.Coupon, res.Problem); }
            else priced = res.Cart;
        }

        var pieces = priced.Lines.Sum(l => l.Qty);
        var head = $"<h1>{E(ctx.T("shop.cart"))}{(pieces > 0 ? $" <span class=\"shop-muted shop-cartcount\">{E(ctx.T("shop.cartPieces", pieces))}</span>" : "")}</h1>";
        var notices = new StringBuilder();
        if (couponMessage is not null) notices.Append(Alert(couponMessage, couponTone));
        foreach (var n in priced.Notes.Distinct()) notices.Append(Alert(n, "info"));
        var linesForm = new StringBuilder(); var summary = new StringBuilder(); var removeForms = new StringBuilder(); var emptyHtml = "";
        if (priced.Lines.Count == 0) emptyHtml = await EmptyCartAsync(ctx, cfg, store, ct);
        else
        {
            // The lines (one form: quantities), and beside them what it all adds up to — as Amazon, Zalando and Shopify's own themes lay it out: picture, name, price, quantity, remove on the left;
            // the summary with the one big button on the right (below the lines on a phone).
            linesForm.Append($"<form method=\"post\" action=\"/shop/cart\" class=\"shop-cartlines\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"update\">");
            foreach (var l in priced.Lines)
            {
                var key = $"{Intl(l.Product.Id)}-{Intl(l.Variant?.Id ?? 0)}";
                var img = SafeUrl(l.Variant?.Image is { Length: > 0 } vi ? vi : l.Product.Image);          // a variant with its own picture shows it
                var url = ShopUrls.Product(l.Product.Slug);
                linesForm.Append("<div class=\"shop-line\">");
                linesForm.Append($"<a class=\"img\" href=\"{E(url)}\"{(img.Length > 0 ? $" style=\"background-image:url('{E(img)}')\"" : "")} aria-label=\"{E(l.Title)}\"></a>");
                linesForm.Append($"<div class=\"info\"><a class=\"title\" href=\"{E(url)}\">{E(l.Title)}</a>{(l.VariantTitle.Length > 0 ? "<div class=\"shop-muted shop-small\">" + E(l.VariantTitle) + "</div>" : "")}{(l.Product.IsDigital ? "<div class=\"shop-small\"><span class=\"shop-badge\">" + E(ctx.T("shop.digital")) + "</span></div>" : "")}");
                linesForm.Append($"<div class=\"shop-muted shop-small\">{E(M(ctx, cfg, l.UnitCents))}</div>");
                linesForm.Append($"<div class=\"controls\"><label class=\"shop-qtylabel\"><span class=\"shop-small shop-muted\">{E(ctx.T("shop.qty"))}</span> <input class=\"shop-qty\" type=\"number\" min=\"0\" max=\"999\" name=\"q:{Intl(l.Product.Id)}:{Intl(l.Variant?.Id ?? 0)}\" value=\"{l.Qty}\" onchange=\"this.form.submit()\"></label>");
                linesForm.Append($"<button class=\"shop-linkbtn\" type=\"submit\" form=\"rm-{key}\">{E(ctx.T("shop.remove"))}</button></div></div>");
                linesForm.Append($"<div class=\"lineprice\">{E(M(ctx, cfg, l.LineCents))}</div></div>");
            }
            linesForm.Append($"<p class=\"shop-cartupdate\"><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.update"))}</button> <a class=\"shop-small\" href=\"/shop\">{E(ctx.T("shop.continue"))}</a></p></form>");
            summary.Append($"<aside class=\"shop-cartsum\"><h2>{E(ctx.T("shop.cartSummary"))}</h2><div class=\"shop-sum\"><div><span>{E(ctx.T("shop.subtotal"))}</span><span>{E(M(ctx, cfg, priced.SubtotalCents))}</span></div>");
            if (priced.DiscountCents > 0)
                summary.Append($"<div><span>{E(ctx.T("shop.coupon.line", priced.CouponCode))}</span><span>−{E(M(ctx, cfg, priced.DiscountCents))}</span></div><div class=\"total\"><span>{E(ctx.T("shop.goodsTotal"))}</span><span>{E(M(ctx, cfg, priced.GoodsCents))}</span></div>");
            else if (priced.FreeShipping) summary.Append($"<div><span>{E(ctx.T("shop.coupon.line", priced.CouponCode))}</span><span>{E(ctx.T("shop.coupon.freeShipping"))}</span></div>");
            summary.Append($"<div class=\"shop-muted shop-small\"><span>{E(priced.NeedsShipping ? ctx.T("shop.shippingLater") : ctx.T("shop.noShipping"))}</span><span></span></div></div>");
            summary.Append(await ShippingEstimateAsync(ctx, cfg, store, priced, ct));
            summary.Append($"<a class=\"shop-btn big\" href=\"/shop/checkout\">{E(ctx.T("shop.toCheckout"))}</a>");
            summary.Append(CouponBox(ctx, priced));
            summary.Append(ExpressHtml(ctx, cfg));
            summary.Append(ShopReminders.BoxHtml(ctx, cfg, cartRow));
            summary.Append("</aside>");
            // Remove buttons are their own forms (a form inside the lines' form would not nest).
            foreach (var l in priced.Lines)
                removeForms.Append($"<form id=\"rm-{Intl(l.Product.Id)}-{Intl(l.Variant?.Id ?? 0)}\" method=\"post\" action=\"/shop/cart\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"remove\"><input type=\"hidden\" name=\"line\" value=\"{Intl(l.Product.Id)}:{Intl(l.Variant?.Id ?? 0)}\"></form>");
        }
        var fallback = head + notices + (priced.Lines.Count == 0 ? emptyHtml : $"<div class=\"shop-cartpage\">{linesForm}{summary}</div>{removeForms}");
        var values = new Dictionary<string, string>
        {
            ["title"] = ctx.T("shop.cart"), ["pieces"] = Intl(pieces), ["pieces_text"] = pieces > 0 ? ctx.T("shop.cartPieces", pieces) : "", ["has_items"] = pieces > 0 ? "1" : "", ["empty"] = priced.Lines.Count == 0 ? "1" : "",
            ["notices_html"] = notices.ToString(), ["lines_html"] = linesForm.ToString() + removeForms, ["summary_html"] = summary.ToString(), ["empty_html"] = emptyHtml
        };
        return await ShopPageHost.RunAsync(ctx, "cart", new PageView(null, ctx.T("shop.cart"), null, values, fallback) { NoIndex = true }, ct);
    }

    /// <summary>
    /// What an empty cart shows: not a bare sentence but a way on — a clear message, one button back to the shop, the top categories and a few products to start with (the highlighted ones,
    /// then the newest), the way Amazon, Zalando or Shopify's own themes do it.
    /// </summary>
    private static async Task<string> EmptyCartAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, CancellationToken ct)
    {
        var sb = new StringBuilder("<div class=\"shop-cart-empty\"><div class=\"icon\" aria-hidden=\"true\"><svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.4\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><circle cx=\"9\" cy=\"20\" r=\"1.5\"/><circle cx=\"18\" cy=\"20\" r=\"1.5\"/><path d=\"M3 4h2l2.5 11h11L21 7H6\"/></svg></div>");
        sb.Append($"<h2>{E(ctx.T("shop.cartEmpty"))}</h2><p class=\"shop-muted\">{E(ctx.T("shop.cartEmptyHelp"))}</p><p><a class=\"shop-btn big\" href=\"/shop\">{E(ctx.T("shop.continue"))}</a></p>");
        var cats = await store.CategoriesAsync(ct);
        var (counts, _) = await store.FacetsAsync(ct);
        var roots = ShopCategories.Roots(cats).Where(c => counts.ContainsKey(c.Slug)).Take(8).ToList();
        if (roots.Count > 0) sb.Append("<div class=\"shop-cats\">" + string.Concat(roots.Select(c => $"<a href=\"{E(ShopUrls.Category(c.Slug))}\">{E(c.Name)}</a>")) + "</div>");
        sb.Append("</div>");

        var picks = (await store.FindProductsAsync(new ProductQuery { FeaturedOnly = true, Take = 4 }, ct)).Items.ToList();
        if (picks.Count < 4)
            picks.AddRange((await store.FindProductsAsync(new ProductQuery { Sort = "new", Take = 8 }, ct)).Items.Where(p => picks.All(x => x.Id != p.Id)).Take(4 - picks.Count));
        if (picks.Count > 0)
        {
            var variants = (await store.VariantsOfAsync(picks.Select(p => p.Id), ct)).Where(v => v.Active).GroupBy(v => v.ProductId).ToDictionary(g => g.Key, g => g.ToList());
            sb.Append($"<h2 class=\"shop-cartpicks\">{E(ctx.T("shop.cartPicks"))}</h2><div class=\"shop-grid\">");
            foreach (var p in picks) sb.Append(CardHtml(ctx, cfg, p, variants.TryGetValue(p.Id, out var vs) ? vs : new List<Variant>(), new CardView(true, true, true)));
            sb.Append("</div>");
        }
        return sb.ToString();
    }

    /// <summary>The field for a coupon code — or, once one is applied, what it does and a way to take it off.</summary>
    private static string CouponBox(IPluginContext ctx, PricedCart priced) =>
        priced.CouponCode.Length > 0
            ? $"<form method=\"post\" action=\"/shop/cart\" class=\"shop-coupon\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"coupon-remove\"><span class=\"shop-badge ok\">{E(ctx.T("shop.coupon.badge", priced.CouponCode))}</span> <button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.coupon.remove"))}</button></form>"
            : $"<form method=\"post\" action=\"/shop/cart\" class=\"shop-coupon\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"coupon\"><input type=\"text\" name=\"code\" maxlength=\"32\" autocomplete=\"off\" placeholder=\"{E(ctx.T("shop.coupon.placeholder"))}\" aria-label=\"{E(ctx.T("shop.coupon.label"))}\"> <button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.coupon.apply"))}</button></form>";

    // ---- checkout ----------------------------------------------------------------------------------------

    /// <summary>Text from a form, trimmed and cut to a sensible length: nobody has a 5000-letter street, and an order must not carry whatever a script posts.</summary>
    internal static string Clip(string? text, int max) { var t = (text ?? "").Trim(); return t.Length <= max ? t : t[..max]; }

    private static Address ReadAddress(RequestData f, string prefix, string fallbackName) => new()
    {
        Name = Clip(f.F(prefix + "name").Trim() is { Length: > 0 } n ? n : fallbackName, 120), Line1 = Clip(f.F(prefix + "line1"), 120), Line2 = Clip(f.F(prefix + "line2"), 120),
        Zip = Clip(f.F(prefix + "zip"), 20), City = Clip(f.F(prefix + "city"), 80), Country = Clip(f.F(prefix + "country"), 2).ToUpperInvariant()
    };

    public static async Task<PluginResult> CheckoutAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = ShopStore.Public(ctx);
        var lines = await ShopCart.LinesAsync(ctx, store, ct);
        if (lines.Count == 0) return PluginResult.Redirect("/shop/cart");
        // No guest orders: the customer signs in — or, where the shop offers an account at checkout, creates one right here (then it is not optional).
        var accountRequired = !cfg.GuestCheckout && ctx.Member is null;
        if (accountRequired && !cfg.AccountsAtCheckout) return PluginResult.Redirect("/anmelden?returnUrl=%2Fshop%2Fcheckout");

        var priced = await ShopOrders.PriceAsync(store, lines, cfg, ctx, ct);
        var token = ShopCart.Token(ctx);
        // Something in the cart is no longer as it was (sold out, cheaper, gone): the cart page corrects it AND says so — this page only sends the buyer there. (Saving the
        // correction here would make the cart page see nothing to report.)
        if (priced.Changed) return PluginResult.Redirect("/shop/cart");

        // What the form starts with: the signed-in customer's own details, or what was typed before an error.
        var def = ctx.Member is { } m ? (await store.AddressesAsync(m.Id, ct)).FirstOrDefault(a => a.IsDefault) ?? (await store.AddressesAsync(m.Id, ct)).FirstOrDefault() : null;
        var f = ctx.Request;
        var posted = ctx.Request.IsPost && (f.Action is "place" or "recalc");
        var email = posted ? Clip(f.F("email"), 254) : ctx.Member?.Email ?? "";
        var billing = posted ? ReadAddress(f, "b_", f.F("b_name")) : def?.Address ?? new Address { Name = ctx.Member?.DisplayName ?? "", Country = "DE" };
        var shipOther = posted && priced.NeedsShipping && f.Form.ContainsKey("ship_other");
        var shipping = shipOther ? ReadAddress(f, "s_", billing.Name) : billing;
        var phone = posted ? Clip(f.F("phone"), 40) : def?.Address.Phone ?? "";
        // A first visit may name the way to pay (the cart's "direct" buttons: ?pay=paypal); a name that is not offered is ignored below.
        var payMethod = posted ? f.F("pay") : f.Q("pay");
        long rateId = long.TryParse(f.F("rate"), out var rid) ? rid : 0;
        var country = (shipOther ? shipping.Country : billing.Country).ToUpperInvariant();
        // VAT by the country of delivery, then the cart's coupon (looked at again: it may no longer apply), then what shipping costs there.
        priced = ShopTax.Apply(priced, cfg, country, billing.Country.ToUpperInvariant());
        Coupon? coupon = null; string? couponProblem = null;
        var couponCode = (await store.CartAsync(token, ct))?.Coupon ?? "";
        if (couponCode.Length > 0)
        {
            var res = await ShopCoupons.ResolveAsync(ctx, store, priced, couponCode, email, ct);
            if (res.Problem is null) { priced = res.Cart; coupon = res.Coupon; }
            else { couponProblem = ctx.T("shop.coupon.removed", couponCode, res.Problem); await store.SetCartCouponAsync(token, "", ct); }
        }
        var options = ShopOrders.ShippingOptions(await store.RatesAsync(true, ct), country, priced);
        var chosen = ShopOrders.Pick(options, rateId);
        var totals = ShopOrders.Compute(priced, chosen, cfg);
        var methods = await Payments.MethodsAsync(ctx, cfg, totals.Total, ct);
        if (!posted && !methods.Any(m => m.Id == payMethod)) payMethod = "";

        var errors = new List<string>();
        // A coupon that fell away is told to the buyer, who then sees the new total and decides again — an order is never placed at a price they did not see.
        if (couponProblem is not null) errors.Add(couponProblem);
        if (ctx.Request.IsPost && f.Action == "place" && couponProblem is null)
        {
            if (!ShopOrdersValidation.ValidEmail(email)) errors.Add(ctx.T("shop.err.email"));
            if (billing.Name.Trim().Length == 0) errors.Add(ctx.T("shop.err.name"));
            if (!billing.IsComplete) errors.Add(ctx.T("shop.err.address"));
            else if (!Countries.Known(billing.Country)) errors.Add(ctx.T("shop.err.country"));
            if (shipOther && (!shipping.IsComplete || !Countries.Known(shipping.Country))) errors.Add(ctx.T("shop.err.shipAddress"));
            if (priced.NeedsShipping && chosen is null) errors.Add(ctx.T("shop.err.noShipping", Countries.Name(country, ctx.Locale)));
            if (totals.Total > 0 && methods.All(x => x.Id != payMethod)) errors.Add(ctx.T("shop.err.payMethod"));
            if (!f.Form.ContainsKey("terms")) errors.Add(ctx.T("shop.err.terms"));
            if (priced.HasDigital && !f.Form.ContainsKey("waiver")) errors.Add(ctx.T("shop.err.waiver"));
            var createAccount = ctx.Member is null && cfg.AccountsAtCheckout && (accountRequired || f.Form.ContainsKey("create_account"));
            if (createAccount && f.F("password").Length < 8) errors.Add(ctx.T("shop.err.password", 8));
            if (ctx.Member is null && !cfg.GuestCheckout && !createAccount) errors.Add(ctx.T("shop.err.accountNeeded"));

            if (errors.Count == 0)
            {
                long memberId = ctx.Member?.Id ?? 0;
                if (createAccount)
                {
                    var created = await ctx.Members.CreateAsync(email, f.F("password"), billing.Name, ct);
                    if (created.Status == MemberCreateStatus.EmailTaken) errors.Add(ctx.T("shop.err.emailTaken"));
                    else if (created.Status != MemberCreateStatus.Created) errors.Add(ctx.T("shop.err.email"));
                    else { memberId = created.Account!.Id; await ctx.Members.SignInAsync(created.Account, false, ct); }
                }
                if (errors.Count == 0)
                {
                    var form = new CheckoutForm
                    {
                        Email = email, Name = billing.Name, Phone = phone, Billing = billing, Shipping = shipping, ShipToBilling = !shipOther, RateId = chosen?.Rate.Id ?? 0,
                        PayMethod = payMethod, Terms = true, Waiver = f.Form.ContainsKey("waiver"), Note = Clip(f.F("note"), 1000), Coupon = coupon?.Code ?? ""
                    };
                    var provider = totals.Total == 0 ? "free" : payMethod == Payments.Stripe || payMethod == Payments.PayPal || payMethod == Payments.Bank ? payMethod : "external";
                    var placed = await ShopOrders.PlaceAsync(ctx, cfg, form, lines, memberId, provider, ct);
                    if (placed.Order is null) errors.Add(placed.Error ?? ctx.T("shop.err.generic"));
                    else
                    {
                        var order = placed.Order;
                        var items = await store.ItemsAsync(order.Id, ct);
                        if (memberId > 0 && f.Form.ContainsKey("save_address"))
                            await store.SaveAddressAsync(0, memberId, billing.Line1, billing with { Phone = phone }, def is null, ct);
                        await ShopOrders.EmitPlacedAsync(ctx, order, ct);

                        if (totals.Total == 0)
                        {
                            await ShopOrders.MarkPaidAsync(ctx, cfg, order.Id, "system", "Bestellung ohne Zahlbetrag", ct: ct);
                            await store.DeleteCartAsync(token, ct);
                            return OrderRedirect(order).WithoutCookie(ShopCart.CookieName);
                        }
                        var start = await Payments.StartAsync(ctx, cfg, order, items, payMethod, ct);
                        if (start.Error is not null)
                        {
                            await ShopOrders.CancelAsync(ctx, cfg, order.Id, "payment_failed", "system", "Zahlung konnte nicht gestartet werden", notifyCustomer: false, ct: ct);
                            errors.Add(start.Error == "unknown" ? ctx.T("shop.err.payMethod") : start.Error);
                        }
                        else
                        {
                            await store.DeleteCartAsync(token, ct);
                            var go = start.RedirectUrl is { Length: > 0 } u ? (u.StartsWith('/') ? PluginResult.Redirect(u) : PluginResult.RedirectExternal(u)) : OrderRedirect(order);
                            return go.WithoutCookie(ShopCart.CookieName);
                        }
                    }
                }
            }
        }

        return await CheckoutPageAsync(ctx, cfg, store, priced, options, chosen, totals, methods, errors, email, billing, shipping, shipOther, phone, payMethod, accountRequired, ct);
    }

    private static PluginResult OrderRedirect(Order o) => PluginResult.Redirect($"/shop/order/{Uri.EscapeDataString(o.Number)}?t={Uri.EscapeDataString(o.AccessToken)}&new=1");

    private static string AddressFields(IPluginContext ctx, string prefix, Address a, bool name)
    {
        var sb = new StringBuilder();
        if (name) sb.Append($"<div><label>{E(ctx.T("shop.fullName"))}</label><input type=\"text\" name=\"{prefix}name\" value=\"{E(a.Name)}\" autocomplete=\"name\"></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.street"))}</label><input type=\"text\" name=\"{prefix}line1\" value=\"{E(a.Line1)}\" autocomplete=\"address-line1\"></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.street2"))}</label><input type=\"text\" name=\"{prefix}line2\" value=\"{E(a.Line2)}\" autocomplete=\"address-line2\"></div>");
        sb.Append($"<div class=\"shop-row\"><div><label>{E(ctx.T("shop.zip"))}</label><input type=\"text\" name=\"{prefix}zip\" value=\"{E(a.Zip)}\" autocomplete=\"postal-code\"></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.city"))}</label><input type=\"text\" name=\"{prefix}city\" value=\"{E(a.City)}\" autocomplete=\"address-level2\"></div></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.country"))}</label>{Select(prefix + "country", string.IsNullOrEmpty(a.Country) ? "DE" : a.Country, CountryOptions(ctx.Locale))}</div>");
        return sb.ToString();
    }

    private static async Task<PluginResult> CheckoutPageAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, PricedCart priced, IReadOnlyList<ShippingOption> options, ShippingOption? chosen,
        ShopOrders.Totals totals, IReadOnlyList<PayMethod> methods, List<string> errors, string email, Address billing, Address shipping, bool shipOther, string phone, string payMethod, bool accountRequired, CancellationToken ct)
    {
        var head = $"<div class=\"shop-ordhead\"><h1>{E(ctx.T("shop.checkout"))}</h1></div>";
        var stepsHtml = $"<ol class=\"shop-steps shop-steps-mini\"><li class=\"done\"><a href=\"/shop/cart\">{E(ctx.T("shop.cart"))}</a></li><li class=\"done now\">{E(ctx.T("shop.checkout"))}</li><li>{E(ctx.T("shop.step.confirm"))}</li></ol>";
        var errorsHtml = string.Concat(errors.Select(e => Alert(e, "error")));
        var sb = new StringBuilder($"<form method=\"post\" action=\"/shop/checkout\" class=\"shop-checkoutform\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"place\">");
        sb.Append("<div class=\"shop-checkout\"><div class=\"shop-checkout-main\">");

        // Each step is a card of its own, numbered; the choices (shipping, payment) are cards one picks, not a row of radio buttons.
        var step = 0;
        string Sec(string title) => $"<section class=\"shop-card-box shop-sec\"><h2><span class=\"n\">{++step}</span>{E(title)}</h2><div class=\"shop-form\">";
        string Opt(string input, string title, string price = "", string hint = "") =>
            $"<label class=\"shop-opt\">{input}<span class=\"t\"><strong>{E(title)}</strong>{(hint.Length > 0 ? "<span class=\"shop-muted shop-small\">" + E(hint) + "</span>" : "")}</span>{(price.Length > 0 ? "<span class=\"p\">" + E(price) + "</span>" : "")}</label>";

        sb.Append(Sec(ctx.T("shop.contact")));
        sb.Append($"<div><label>{E(ctx.T("shop.email"))}</label><input type=\"email\" name=\"email\" value=\"{E(email)}\" autocomplete=\"email\"{(ctx.Member is null ? "" : " readonly")}></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.phone"))}</label><input type=\"tel\" name=\"phone\" value=\"{E(phone)}\" autocomplete=\"tel\"></div></div></section>");

        sb.Append(Sec(ctx.T("shop.billingAddress"))).Append(AddressFields(ctx, "b_", billing, true));
        if (priced.NeedsShipping)
        {
            sb.Append($"<label class=\"shop-check\"><input type=\"checkbox\" name=\"ship_other\" value=\"1\"{(shipOther ? " checked" : "")}> <span>{E(ctx.T("shop.shipOther"))}</span></label>");
            // The second address is only there while the box is ticked (script); without script it is simply always shown.
            sb.Append($"<div id=\"ship-box\" class=\"shop-form\" style=\"padding-left:12px;border-left:3px solid rgba(0,0,0,.1){(shipOther ? "" : ";display:none")}\"><h3 style=\"margin:0\">{E(ctx.T("shop.shippingAddress"))}</h3>").Append(AddressFields(ctx, "s_", shipping, true)).Append("</div>")
              .Append("<noscript><style>#ship-box{display:grid!important}</style></noscript>")
              .Append("<script>(function(){var c=document.querySelector('input[name=ship_other]'),b=document.getElementById('ship-box');if(c&&b)c.addEventListener('change',function(){b.style.display=c.checked?'':'none'})})()</script>");
            sb.Append("</div></section>");

            sb.Append(Sec(ctx.T("shop.shippingMethod")));
            if (options.Count == 0) sb.Append($"<p class=\"shop-muted\">{E(ctx.T("shop.noShippingFor", Countries.Name((shipOther ? shipping : billing).Country, ctx.Locale)))}</p>");
            foreach (var o in options)
                sb.Append(Opt($"<input type=\"radio\" name=\"rate\" value=\"{Intl(o.Rate.Id)}\"{(chosen?.Rate.Id == o.Rate.Id ? " checked" : "")}>", o.Rate.Name, o.PriceCents == 0 ? ctx.T("shop.free") : M(ctx, cfg, o.PriceCents)));
            sb.Append($"<div><button class=\"shop-btn ghost\" type=\"submit\" name=\"action\" value=\"recalc\" formnovalidate>{E(ctx.T("shop.refreshShipping"))}</button></div></div></section>");
        }
        else sb.Append("</div></section>");

        if (totals.Total > 0)
        {
            sb.Append(Sec(ctx.T("shop.payment")));
            if (methods.Count == 0) sb.Append(Alert(ctx.T("shop.noPayment"), "error"));
            foreach (var m in methods)
                sb.Append(Opt($"<input type=\"radio\" name=\"pay\" value=\"{E(m.Id)}\"{(payMethod == m.Id || payMethod.Length == 0 && m == methods[0] ? " checked" : "")}>", m.Label, "", m.Hint));
            sb.Append("</div></section>");
        }

        sb.Append(Sec(ctx.T("shop.checkoutMore")));
        if (ctx.Member is null && cfg.AccountsAtCheckout && accountRequired)
            sb.Append($"<input type=\"hidden\" name=\"create_account\" value=\"1\"><p class=\"shop-muted\">{E(ctx.T("shop.accountRequired"))}</p>")
              .Append($"<div><label>{E(ctx.T("shop.password"))}</label><input type=\"password\" name=\"password\" autocomplete=\"new-password\"><div class=\"shop-small shop-muted\">{E(ctx.T("shop.passwordHint"))}</div></div>");
        else if (ctx.Member is null && cfg.AccountsAtCheckout)
            sb.Append($"<label class=\"shop-check\"><input type=\"checkbox\" name=\"create_account\" value=\"1\"{(ctx.Request.Form.ContainsKey("create_account") ? " checked" : "")}> <span>{E(ctx.T("shop.createAccount"))}</span></label>")
              .Append($"<div><label>{E(ctx.T("shop.password"))}</label><input type=\"password\" name=\"password\" autocomplete=\"new-password\"><div class=\"shop-small shop-muted\">{E(ctx.T("shop.passwordHint"))}</div></div>");
        else if (ctx.Member is not null)
            sb.Append($"<label class=\"shop-check\"><input type=\"checkbox\" name=\"save_address\" value=\"1\" checked> <span>{E(ctx.T("shop.saveAddress"))}</span></label>");
        sb.Append($"<div><label>{E(ctx.T("shop.note"))}</label><textarea name=\"note\" rows=\"3\">{E(ctx.Request.F("note"))}</textarea></div></div></section>");
        sb.Append("</div>");

        // ---- the summary: what is ordered (with pictures), the sum, the two confirmations and the one button — it stays in view while the form is long ----
        sb.Append($"<aside class=\"shop-checkout-side\"><div class=\"shop-card-box shop-summary\"><h2>{E(ctx.T("shop.yourOrder"))}</h2>");
        foreach (var l in priced.Lines)
        {
            var img = SafeUrl(l.Variant?.Image is { Length: > 0 } vi ? vi : l.Product.Image);          // a variant with its own picture shows it
            sb.Append($"<div class=\"shop-sline\"><span class=\"img\"{(img.Length > 0 ? $" style=\"background-image:url('{E(img)}')\"" : "")}><b>{l.Qty}</b></span><span class=\"t\">{E(l.DisplayTitle)}</span><span class=\"p\">{E(M(ctx, cfg, l.LineCents))}</span></div>");
        }
        sb.Append("<div class=\"shop-sum\" style=\"max-width:none\">");
        sb.Append($"<div><span>{E(ctx.T("shop.subtotal"))}</span><span>{E(M(ctx, cfg, totals.Subtotal))}</span></div>");
        if (totals.Discount > 0) sb.Append($"<div><span>{E(ctx.T("shop.coupon.line", priced.CouponCode))}</span><span>−{E(M(ctx, cfg, totals.Discount))}</span></div>");
        if (priced.NeedsShipping) sb.Append($"<div><span>{E(ctx.T("shop.shipping"))}{(priced.FreeShipping ? " · " + E(ctx.T("shop.coupon.line", priced.CouponCode)) : "")}</span><span>{E(chosen is null ? "—" : M(ctx, cfg, totals.Shipping))}</span></div>");
        sb.Append($"<div class=\"total\"><span>{E(ctx.T("shop.total"))}</span><span>{E(M(ctx, cfg, totals.Total))}</span></div>");
        sb.Append($"<div class=\"shop-muted shop-small\"><span>{E(cfg.SmallBusiness ? ctx.T("shop.smallBusinessNote") : priced.TaxNote == "export" ? ctx.T("shop.taxExport") : ctx.T("shop.vatIncluded", M(ctx, cfg, totals.Tax)))}</span><span></span></div></div>");
        sb.Append($"<div class=\"shop-small\" style=\"margin-top:8px\"><a href=\"/shop/cart\">{E(priced.CouponCode.Length > 0 ? ctx.T("shop.coupon.change") : ctx.T("shop.coupon.have"))}</a></div>");

        var termsLink = cfg.TermsUrl.Length > 0 ? $"<a href=\"{E(cfg.TermsUrl)}\" target=\"_blank\" rel=\"noopener\">{E(ctx.T("shop.terms"))}</a>" : E(ctx.T("shop.terms"));
        var withdrawalLink = cfg.WithdrawalUrl.Length > 0 ? $"<a href=\"{E(cfg.WithdrawalUrl)}\" target=\"_blank\" rel=\"noopener\">{E(ctx.T("shop.withdrawal"))}</a>" : E(ctx.T("shop.withdrawal"));
        var privacyLink = cfg.PrivacyUrl.Length > 0 ? $"<a href=\"{E(cfg.PrivacyUrl)}\" target=\"_blank\" rel=\"noopener\">{E(ctx.T("shop.privacy"))}</a>" : E(ctx.T("shop.privacy"));
        sb.Append($"<label class=\"shop-check shop-confirm\"><input type=\"checkbox\" name=\"terms\" value=\"1\"{(ctx.Request.Form.ContainsKey("terms") ? " checked" : "")}> <span>{string.Format(CultureInfo.InvariantCulture, E(ctx.T("shop.termsAccept")).Replace("{0}", "{0}").Replace("{1}", "{1}").Replace("{2}", "{2}"), termsLink, withdrawalLink, privacyLink)}</span></label>");
        if (priced.HasDigital)
            sb.Append($"<label class=\"shop-check shop-confirm\"><input type=\"checkbox\" name=\"waiver\" value=\"1\"{(ctx.Request.Form.ContainsKey("waiver") ? " checked" : "")}> <span>{E(ctx.T("shop.waiver"))}</span></label>");
        sb.Append($"<button class=\"shop-btn big\" type=\"submit\">{E(totals.Total > 0 ? ctx.T("shop.placeOrder") : ctx.T("shop.placeFree"))}</button>");
        sb.Append("</div></aside></div></form>");
        var values = new Dictionary<string, string>
        {
            ["title"] = ctx.T("shop.checkout"), ["has_errors"] = errors.Count > 0 ? "1" : "", ["steps_html"] = stepsHtml, ["errors_html"] = errorsHtml, ["form_html"] = sb.ToString()
        };
        return await ShopPageHost.RunAsync(ctx, "checkout", new PageView(null, ctx.T("shop.checkout"), null, values, head + stepsHtml + errorsHtml + sb) { NoIndex = true, Status = errors.Count > 0 ? 422 : 200 }, ct);
    }

    // ---- the order's own page ----------------------------------------------------------------------------

    public static async Task<PluginResult> OrderAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = ShopStore.Public(ctx);
        var order = await store.OrderByNumberAsync(ctx.Request.R("number"), ct);
        if (order is null || !ShopAccess.May(ctx, order, ctx.Request.Q("t")))
            return await PageAsync(ctx, cfg, ctx.T("shop.notFound"), $"<h1>{E(ctx.T("shop.notFound"))}</h1>", status: 404, noIndex: true);
        var token = ctx.Request.Q("t");
        var self = $"/shop/order/{Uri.EscapeDataString(order.Number)}" + (token.Length > 0 ? "?t=" + Uri.EscapeDataString(token) : "");

        string? message = null; var tone = "info";
        if (ctx.Request.IsPost)
        {
            if (ctx.Request.Action == "cancel" && order.Status == OrderStatus.AwaitingPayment)
            {
                await ShopOrders.CancelAsync(ctx, cfg, order.Id, "customer", "customer", "Vom Kunden storniert", ct: ct);
                return PluginResult.Redirect(self);
            }
            if (ctx.Request.Action == "retry" && order.Status == OrderStatus.AwaitingPayment && (order.PaymentProvider == Payments.Stripe && cfg.StripeReady || order.PaymentProvider == Payments.PayPal && cfg.PayPalReady))
            {
                var start = order.PaymentProvider == Payments.PayPal
                    ? await PayPalPayments.RestartAsync(ctx, cfg, order, await store.ItemsAsync(order.Id, ct), ct)
                    : await StripePayments.RestartAsync(ctx, cfg, order, await store.ItemsAsync(order.Id, ct), ct);
                if (start.RedirectUrl is { Length: > 0 } u) return PluginResult.RedirectExternal(u);
                message = start.Error ?? ctx.T("shop.err.payment"); tone = "error";
            }
        }
        // The customer is back from Stripe before the notification was: ask Stripe, so the page tells the truth.
        if (order.Status == OrderStatus.AwaitingPayment && ctx.Request.Q("paid") == "1")
        {
            await StripePayments.ReconcileAsync(ctx, cfg, order, ct);
            order = (await store.OrderAsync(order.Id, ct))!;
        }
        // Back from PayPal's page: take the payment now (asked of the order's OWN PayPal order — the address only says "the customer returned").
        if (order.Status == OrderStatus.AwaitingPayment && ctx.Request.Q("paypal") == "1")
        {
            await PayPalPayments.CaptureAsync(ctx, cfg, order, ct);
            order = (await store.OrderAsync(order.Id, ct))!;
        }

        var items = await store.ItemsAsync(order.Id, ct);
        string Mo(long c) => Money.Format(c, order.Currency, ctx.Locale);
        var events = await store.EventsAsync(order.Id, ct);
        string Day(DateTime d) => d.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

        var headHtml = $"<div class=\"shop-ordhead\"><h1>{E(ctx.T("shop.orderNo", order.Number))}</h1>{StatusBadge(ctx, order.Status)}<span class=\"shop-muted\">{E(Day(order.CreatedUtc))}</span></div>";
        var sb = new StringBuilder();           // the notices
        if (message is not null) sb.Append(Alert(message, tone));
        if (ctx.Request.Q("new") == "1" || ctx.Request.Q("paid") == "1" || ctx.Request.Q("paypal") == "1")
            sb.Append(Alert(order.Status switch
            {
                OrderStatus.AwaitingPayment => order.PaymentProvider == Payments.Bank ? ctx.T("shop.thanksBank") : ctx.T("shop.thanksPending"),
                _ => ctx.T("shop.thanksPaid")
            }, order.Status == OrderStatus.AwaitingPayment ? "info" : "ok"));
        if (ctx.Request.Q("cancelled") == "1" && order.Status == OrderStatus.AwaitingPayment) sb.Append(Alert(ctx.T("shop.paymentCancelled"), "info"));

        // Where the order stands: placed, paid, shipped, completed (an order that was cancelled or refunded has no road to show).
        var stepsOrd = new StringBuilder();
        if (order.Status is not (OrderStatus.Cancelled or OrderStatus.Refunded))
        {
            var reached = order.Status switch { OrderStatus.AwaitingPayment => 0, OrderStatus.Paid => 1, OrderStatus.Shipped => 2, _ => 3 };
            var steps = new (string Key, string Event)[] { ("placed", "placed"), ("paid", "paid"), ("shipped", "shipped"), ("done", "delivered") };
            stepsOrd.Append("<ol class=\"shop-steps\">");
            for (var i = 0; i < steps.Length; i++)
            {
                var when = events.Where(e => e.Kind == steps[i].Event).Select(e => (DateTime?)e.CreatedUtc).LastOrDefault();
                stepsOrd.Append($"<li class=\"{(i < reached ? "done" : i == reached ? "done now" : "")}\">{E(ctx.T("shop.step." + steps[i].Key))}{(i <= reached && when is { } w ? $"<small>{E(Day(w))}</small>" : "")}</li>");
            }
            stepsOrd.Append("</ol>");
        }

        var pay = new StringBuilder();
        if (order.Status == OrderStatus.AwaitingPayment)
        {
            pay.Append("<div class=\"shop-box shop-paybox\">");
            if (order.PaymentProvider == Payments.Bank) pay.Append($"<div class=\"shop-pre\">{E(Payments.BankInstructions(ctx, cfg, order))}</div>");
            else if (order.PaymentProvider == Payments.Stripe && cfg.StripeReady || order.PaymentProvider == Payments.PayPal && cfg.PayPalReady)
                pay.Append($"<p>{E(ctx.T("shop.payNow"))}</p><form method=\"post\" action=\"{E(self)}\" class=\"shop-actions\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"retry\"><button class=\"shop-btn\" type=\"submit\">{E(ctx.T("shop.payAgain"))}</button></form>");
            pay.Append($"<form method=\"post\" action=\"{E(self)}\" style=\"margin-top:12px\" data-confirm=\"{E(ctx.T("shop.cancelConfirm"))}\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"cancel\"><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.cancelOrder"))}</button></form></div>");
        }

        // ---- left: what was ordered, with its picture, and what happened to it ----
        var products = (await store.ProductsByIdsAsync(items.Select(i => i.ProductId).Where(id => id > 0).Distinct(), ct)).ToDictionary(p => p.Id);
        var itemsHtml = new StringBuilder($"<section class=\"shop-card-box\"><h2>{E(ctx.T("shop.item"))}</h2>");
        foreach (var i in items)
        {
            var img = products.TryGetValue(i.ProductId, out var prod) ? SafeUrl(prod.Image) : "";
            var thumb = img.Length > 0 ? $"<span class=\"img\" style=\"background-image:url('{E(img)}')\"></span>" : "<span class=\"img\"></span>";
            itemsHtml.Append($"<div class=\"shop-oline\">{thumb}<div><div class=\"t\">{E(i.DisplayTitle)}</div><div class=\"shop-muted shop-small\">{i.Qty} × {E(Mo(i.UnitCents))}</div></div><div class=\"p\">{E(Mo(i.LineCents))}</div></div>");
        }
        itemsHtml.Append("</section>");
        var visible = new[] { "placed", "paid", "shipped", "delivered", "cancelled", "refund" };
        var history = events.Where(e => visible.Contains(e.Kind)).ToList();
        var historyHtml = new StringBuilder();
        if (history.Count > 0)
        {
            historyHtml.Append($"<section class=\"shop-card-box\"><h2>{E(ctx.T("shop.history"))}</h2><div class=\"shop-small\">");
            foreach (var e in history) historyHtml.Append($"<div><span class=\"shop-muted\">{e.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}</span> — {E(ctx.T("shop.event." + e.Kind))}</div>");
            historyHtml.Append("</div></section>");
        }
        var main = itemsHtml.ToString() + historyHtml;

        // ---- right: the sum, delivery, documents, downloads ----
        var side = new StringBuilder("<div class=\"shop-card-box\"><div class=\"shop-sum\">");
        side.Append($"<div><span>{E(ctx.T("shop.subtotal"))}</span><span>{E(Mo(order.SubtotalCents))}</span></div>");
        if (order.DiscountCents > 0) side.Append($"<div><span>{E(ctx.T("shop.coupon.line", order.CouponCode))}</span><span>−{E(Mo(order.DiscountCents))}</span></div>");
        if (order.ShippingCents > 0 || order.ShippingMethod.Length > 0) side.Append($"<div><span>{E(ctx.T("shop.shipping"))}{(order.ShippingMethod.Length > 0 ? " (" + E(order.ShippingMethod) + ")" : "")}</span><span>{E(Mo(order.ShippingCents))}</span></div>");
        side.Append($"<div class=\"total\"><span>{E(ctx.T("shop.total"))}</span><span>{E(Mo(order.TotalCents))}</span></div>");
        if (order.RefundedCents > 0) side.Append($"<div><span>{E(ctx.T("shop.refunded"))}</span><span>−{E(Mo(order.RefundedCents))}</span></div>");
        side.Append("</div></div>");

        if (order.Tracking.Length > 0)
        {
            var trackUrl = ShopTracking.UrlOf(cfg, order);
            var carrierName = ShopTracking.NameOf(cfg, order);
            side.Append($"<div class=\"shop-card-box shop-track\"><h2>{E(ctx.T("shop.tracking"))}</h2>{(carrierName.Length > 0 ? $"<div class=\"shop-muted shop-small\">{E(carrierName)}</div>" : "")}<p class=\"num\"><strong>{E(order.Tracking)}</strong></p>");
            if (trackUrl.Length > 0) side.Append($"<a class=\"shop-btn big\" href=\"{E(trackUrl)}\" target=\"_blank\" rel=\"noopener noreferrer\">{E(ctx.T("shop.trackShipment"))}</a>");
            side.Append("</div>");
        }

        side.Append($"<div class=\"shop-card-box\"><h2>{E(ctx.T("shop.billingAddress"))}</h2><div class=\"shop-pre\">{E(order.Billing.Lines())}</div>");
        if (order.Shipping.IsComplete) side.Append($"<h2 style=\"margin-top:16px\">{E(ctx.T("shop.shippingAddress"))}</h2><div class=\"shop-pre\">{E(order.Shipping.Lines())}</div>");
        side.Append("</div>");

        if (OrderStatus.IsPaid(order.Status))
        {
            var downloads = (await store.DownloadsOfOrderAsync(order.Id, ct)).Where(d => !d.Revoked).ToList();
            if (downloads.Count > 0)
            {
                side.Append($"<div class=\"shop-card-box\"><h2>{E(ctx.T("shop.downloads"))}</h2>");
                foreach (var d in downloads)
                    if (await store.FileAsync(d.FileId, ct) is { } file)
                        side.Append($"<div style=\"margin-bottom:8px\"><a href=\"/shop/download/{E(Uri.EscapeDataString(order.Number))}/{Intl(file.Id)}{(token.Length > 0 ? "?t=" + E(Uri.EscapeDataString(token)) : "")}\">{E(file.FileName)}</a> <span class=\"shop-muted shop-small\">({E(FileSize(file.Size))}{(d.MaxDownloads > 0 ? ", " + E(ctx.T("shop.dlLeft", Math.Max(0, d.MaxDownloads - d.Count))) : "")}, {E(ctx.T("shop.dlUntil", d.ExpiresUtc.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)))})</span></div>");
                side.Append("</div>");
            }
        }
        // The invoice and its credit notes stay reachable after a refund: a refunded order is no longer "paid", but its documents are still what the customer was billed.
        if (order.InvoiceNumber.Length > 0)
        {
            var credits = await store.CreditNotesOfOrderAsync(order.Id, ct);
            string DocUrl(string? doc, bool pdf) => $"/shop/invoice/{E(Uri.EscapeDataString(order.Number))}?" + string.Join("&amp;", new[]
                { token.Length > 0 ? "t=" + E(Uri.EscapeDataString(token)) : "", doc is null ? "" : "doc=" + E(Uri.EscapeDataString(doc)), pdf ? "pdf=1" : "" }.Where(x => x.Length > 0));
            side.Append($"<div class=\"shop-card-box\"><p style=\"margin:0\"><a class=\"shop-btn ghost\" href=\"{DocUrl(null, false)}\" target=\"_blank\" rel=\"noopener\">{E(ctx.T("shop.invoice"))} {E(order.InvoiceNumber)}</a> <a href=\"{DocUrl(null, true)}\" style=\"margin-left:10px\">{E(ctx.T("shop.pdf"))}</a></p>");
            foreach (var c in credits)
                side.Append($"<p class=\"shop-small\" style=\"margin:10px 0 0\"><a href=\"{DocUrl(c.Number, false)}\" target=\"_blank\" rel=\"noopener\">{E(ctx.T("shop.creditNote"))} {E(c.Number)}</a> ({E(Mo(c.AmountCents))}) · <a href=\"{DocUrl(c.Number, true)}\">{E(ctx.T("shop.pdf"))}</a></p>");
            side.Append("</div>");
        }

        var back = ctx.Member is not null ? $"<p style=\"margin-top:24px\"><a href=\"/konto/bestellungen\">← {E(ctx.T("shop.myOrders"))}</a></p>" : "";
        var fallback = headHtml + sb + stepsOrd + pay + $"<div class=\"shop-orderpage\"><div class=\"shop-order-main\">{main}</div><aside class=\"shop-order-side\">{side}</aside></div>" + back;
        var values = new Dictionary<string, string>
        {
            ["title"] = ctx.T("shop.orderNo", order.Number), ["number"] = order.Number, ["status"] = order.Status, ["status_label"] = ctx.T("shop.status." + order.Status), ["date"] = Day(order.CreatedUtc), ["total"] = Mo(order.TotalCents),
            ["head_html"] = headHtml, ["message_html"] = sb.ToString(), ["steps_html"] = stepsOrd.ToString(), ["pay_html"] = pay.ToString(), ["items_html"] = itemsHtml.ToString(), ["history_html"] = historyHtml.ToString(),
            ["side_html"] = side.ToString(), ["back_html"] = back
        };
        return await ShopPageHost.RunAsync(ctx, "order", new PageView(null, ctx.T("shop.orderNo", order.Number), null, values, fallback) { NoIndex = true }, ct);
    }

    public static string FileSize(long bytes) => bytes switch
    {
        < 1024 => bytes + " B", < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB", _ => (bytes / 1073741824.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB"
    };

    // ---- invoice -----------------------------------------------------------------------------------------

    public static async Task<PluginResult> InvoiceAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = ShopStore.Public(ctx);
        var order = await store.OrderByNumberAsync(ctx.Request.R("number"), ct);
        if (order is null || !ShopAccess.May(ctx, order, ctx.Request.Q("t"))) return PluginResult.NotFound();
        // The invoice, or — with ?doc= — one of the order's credit notes; as a page, or as the PDF with ?pdf=1.
        var wanted = ctx.Request.Q("doc");
        var inv = wanted.Length == 0 ? await store.InvoiceOfOrderAsync(order.Id, ct) : await store.DocumentOfOrderAsync(order.Id, wanted, ct);
        if (inv is null) return PluginResult.NotFound();
        if (ctx.Request.Q("pdf") == "1")
        {
            var bytes = inv.Pdf;
            if (bytes.Length == 0) return PluginResult.NotFound();
            return PluginResult.File(bytes, "application/pdf", $"{(inv.IsCredit ? "Gutschrift" : "Rechnung")}-{inv.Number}.pdf").WithHeader("Cache-Control", "private, no-store").WithHeader("X-Robots-Tag", "noindex");
        }
        var self = $"/shop/invoice/{Uri.EscapeDataString(order.Number)}?pdf=1" + (ctx.Request.Q("t").Length > 0 ? "&t=" + Uri.EscapeDataString(ctx.Request.Q("t")) : "") + (wanted.Length > 0 ? "&doc=" + Uri.EscapeDataString(wanted) : "");
        return PluginResult.Fragment(inv.Html.Replace(ShopInvoice.PdfMarker, E(self))).WithHeader("Cache-Control", "private, no-store").WithHeader("X-Robots-Tag", "noindex");
    }
}

/// <summary>Checks on what a visitor typed.</summary>
internal static class ShopOrdersValidation
{
    public static bool ValidEmail(string? email)
    {
        var e = (email ?? "").Trim();
        if (e.Length is < 5 or > 254 || e.Any(char.IsControl) || e.Contains(' ')) return false;                      // no line break can slip into a mail header
        var at = e.IndexOf('@');
        return at > 0 && at == e.LastIndexOf('@') && e.IndexOf('.', at) > at + 1 && !e.EndsWith('.');
    }
}
