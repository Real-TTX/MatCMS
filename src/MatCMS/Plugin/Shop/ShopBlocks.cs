using System.Globalization;
using System.Text;
using MatCMS.Plugin;
using static MatCMS.Plugin.Shop.ShopHtml;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The shop's blocks for pages: a list of products (all, one category, one keyword, the highlighted ones or a hand-picked selection, as cards or as a list)
/// and a single product shown large. The editor's choices for category, keyword and product come from the shop itself (<c>OptionsSource</c>), so nobody types a slug.
/// </summary>
internal static class ShopBlocks
{
    private const string SvgBag = @"<path d=""M6 7h12l1 13H5L6 7z""/><path d=""M9 7a3 3 0 0 1 6 0""/>";
    private const string SvgOne = "<rect x=\"3\" y=\"4\" width=\"10\" height=\"10\" rx=\"1\"/><path d=\"M16 6h5M16 10h5M3 18h18\"/>";

    public static void Register(IPluginBuilder b)
    {
        // The lists the block editor offers, read from the shop each time the editor opens.
        b.OptionsSource("categories", async (ctx, ct) =>
            (await new ShopStore(ctx.Data).CategoriesAsync(ct)).Select(c => new FieldOption(c.Slug, c.Name)).ToList());
        b.OptionsSource("tags", async (ctx, ct) =>
            (await new ShopStore(ctx.Data).FacetsAsync(ct)).Tags.Select(t => new FieldOption(t.Tag, $"{t.Tag} ({t.Count})")).ToList());
        b.OptionsSource("products", async (ctx, ct) =>
            (await new ShopStore(ctx.Data).FindProductsAsync(new ProductQuery { Sort = "name" }, ct)).Items.Select(p => new FieldOption(Intl(p.Id), p.Title)).ToList());

        var yesNo = new[] { new FieldOption("yes", "shop.block.yes"), new FieldOption("no", "shop.block.no") };
        b.Block(new BlockSpec("shop_products", "shop.block.name", "shop.block.desc")
        {
            Category = "design", IconSvg = SvgBag,
            Fields = new[]
            {
                new FieldSpec("heading", "shop.block.heading", FieldKind.Text),
                new FieldSpec("source", "shop.block.source", FieldKind.Select)
                {
                    Default = "all",
                    Options = new[]
                    {
                        new FieldOption("all", "shop.block.src.all"), new FieldOption("category", "shop.block.src.category"), new FieldOption("tag", "shop.block.src.tag"),
                        new FieldOption("featured", "shop.block.src.featured"), new FieldOption("sale", "shop.block.src.sale"), new FieldOption("pick", "shop.block.src.pick")
                    }
                },
                new FieldSpec("category", "shop.block.category", FieldKind.Select) { OptionsSource = "categories", ShowWhenField = "source", ShowWhenValue = "category" },
                new FieldSpec("tag", "shop.block.tag", FieldKind.Select) { OptionsSource = "tags", ShowWhenField = "source", ShowWhenValue = "tag" },
                new FieldSpec("products", "shop.block.products", FieldKind.MultiSelect) { OptionsSource = "products", Help = "shop.block.productsHelp", ShowWhenField = "source", ShowWhenValue = "pick" },
                new FieldSpec("sort", "shop.block.sort", FieldKind.Select)
                {
                    Default = "",
                    Options = new[]
                    {
                        new FieldOption("", "shop.block.sort.manual"), new FieldOption("new", "shop.block.sort.new"), new FieldOption("price_asc", "shop.block.sort.priceAsc"),
                        new FieldOption("price_desc", "shop.block.sort.priceDesc"), new FieldOption("name", "shop.block.sort.name")
                    }
                },
                new FieldSpec("limit", "shop.block.limit", FieldKind.Text) { Default = "4", Help = "shop.block.limitHelp" },
                new FieldSpec("layout", "shop.block.layout", FieldKind.Select)
                {
                    Default = "grid", Options = new[] { new FieldOption("grid", "shop.block.layout.grid"), new FieldOption("list", "shop.block.layout.list") }
                },
                new FieldSpec("columns", "shop.block.columns", FieldKind.Select)
                {
                    Default = "auto", ShowWhenField = "layout", ShowWhenValue = "grid",
                    Options = new[]
                    {
                        new FieldOption("auto", "shop.block.columns.auto"), new FieldOption("2", "shop.block.columns.2"), new FieldOption("3", "shop.block.columns.3"), new FieldOption("4", "shop.block.columns.4")
                    }
                },
                new FieldSpec("showSummary", "shop.block.showSummary", FieldKind.Select) { Default = "yes", Options = yesNo },
                new FieldSpec("showPrice", "shop.block.showPrice", FieldKind.Select) { Default = "yes", Options = yesNo },
                new FieldSpec("allLink", "shop.block.allLink", FieldKind.Select) { Default = "no", Options = yesNo }
            }
        }, ProductsAsync);

        b.Block(new BlockSpec("shop_product", "shop.block1.name", "shop.block1.desc")
        {
            Category = "design", IconSvg = SvgOne,
            Fields = new[]
            {
                new FieldSpec("product", "shop.block1.product", FieldKind.Select) { OptionsSource = "products" },
                new FieldSpec("button", "shop.block1.button", FieldKind.Text) { Help = "shop.block1.buttonHelp" }
            }
        }, ProductAsync);
    }

    private static string Field(IPluginContext ctx, string id) => (ctx.Block?.Get(id) ?? "").Trim();

    /// <summary>The ids a multi-select stored ("3, 7, 12"), in that order, without anything that is not a number.</summary>
    internal static List<long> ParseIds(string text) =>
        text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0).Where(n => n > 0).Distinct().ToList();

    private static async Task<PluginResult> ProductsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        var limit = int.TryParse(Field(ctx, "limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? Math.Clamp(l, 1, 48) : 4;
        var source = Field(ctx, "source");
        // A block saved before the choices existed has only a category (or nothing): it keeps showing what it showed.
        if (source.Length == 0) source = Field(ctx, "category").Length > 0 ? "category" : "all";

        var sort = ProductQuery.NormaliseSort(Field(ctx, "sort"));
        var pq = new ProductQuery { Sort = sort, Take = limit };
        string more = "/shop";
        List<long>? picked = null;
        switch (source)
        {
            case "category" when Field(ctx, "category") is { Length: > 0 } cat:
                pq = pq with { Category = cat }; more = "/shop?cat=" + Uri.EscapeDataString(cat); break;
            case "tag" when Field(ctx, "tag") is { Length: > 0 } tag:
                pq = pq with { Tag = tag }; more = "/shop?tag=" + Uri.EscapeDataString(tag); break;
            case "featured":
                pq = pq with { FeaturedOnly = true }; break;
            case "sale":
                pq = pq with { OnSaleOnly = true }; break;
            case "pick":
                picked = ParseIds(Field(ctx, "products"));
                pq = pq with { Ids = picked }; break;
        }
        var (items, _) = await store.FindProductsAsync(pq, ct);
        // A hand-picked list keeps the order the choice was stored in, unless another order was asked for.
        if (picked is { Count: > 0 } && sort.Length == 0) items = items.OrderBy(p => picked.IndexOf(p.Id)).ToList();
        if (items.Count == 0) return PluginResult.Fragment("");

        var variants = (await store.VariantsOfAsync(items.Select(p => p.Id), ct)).Where(v => v.Active).GroupBy(v => v.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var view = new ShopPublic.CardView(Field(ctx, "showSummary") != "no", Field(ctx, "showPrice") != "no", false);
        var list = Field(ctx, "layout") == "list";
        var cols = Field(ctx, "columns") is "2" or "3" or "4" ? " cols-" + Field(ctx, "columns") : "";

        var sb = new StringBuilder("<style>").Append(CardCss).Append("</style><section class=\"section\"><div class=\"container\"><div class=\"shop shop-block\">");
        if (Field(ctx, "heading") is { Length: > 0 } heading) sb.Append("<h2>").Append(E(heading)).Append("</h2>");
        sb.Append(list ? "<div class=\"shop-list\">" : $"<div class=\"shop-grid{cols}\">");
        foreach (var p in items) sb.Append(ShopPublic.CardHtml(ctx, cfg, p, variants.TryGetValue(p.Id, out var vs) ? vs : new List<Variant>(), view));
        sb.Append("</div>");
        if (Field(ctx, "allLink") == "yes") sb.Append($"<p class=\"shop-more\"><a class=\"shop-btn ghost\" href=\"{E(more)}\">{E(ctx.T("shop.block.allLinkText"))}</a></p>");
        return PluginResult.Fragment(sb.Append("</div></div></section>").ToString());
    }

    private static async Task<PluginResult> ProductAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!long.TryParse(Field(ctx, "product"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return PluginResult.Fragment("");
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        if (await store.ProductAsync(id, ct) is not { Active: true } p) return PluginResult.Fragment("");
        var variants = (await store.VariantsAsync(p.Id, true, ct)).ToList();
        var url = "/shop/" + Uri.EscapeDataString(p.Slug);
        var img = ShopPublic.SafeUrl(p.Image);
        var label = Field(ctx, "button") is { Length: > 0 } b ? b : ctx.T("shop.view");
        var sb = new StringBuilder("<style>").Append(CardCss).Append("</style><section class=\"section\"><div class=\"container\"><div class=\"shop shop-block\"><div class=\"shop-feature\">");
        sb.Append(img.Length > 0 ? $"<a class=\"img\" href=\"{E(url)}\" style=\"background-image:url('{E(img)}')\" aria-label=\"{E(p.Title)}\"></a>" : "<span></span>");
        sb.Append($"<div><h3>{E(p.Title)}</h3>");
        if (p.Summary.Length > 0) sb.Append($"<p class=\"shop-muted\">{E(p.Summary)}</p>");
        sb.Append($"<p class=\"shop-price\" style=\"font-size:1.4em\">{E(ShopPublic.PriceText(ctx, cfg, p, variants))}{ShopPublic.WasHtml(ctx, cfg, p, variants)}</p>");
        if (ShopPublic.SoldOut(p, variants)) sb.Append($"<p><span class=\"shop-badge bad\">{E(ctx.T("shop.soldOut"))}</span></p>");
        sb.Append($"<p><a class=\"shop-btn\" href=\"{E(url)}\">{E(label)}</a></p></div></div></div></div></section>");
        return PluginResult.Fragment(sb.ToString());
    }
}
