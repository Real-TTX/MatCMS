using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using static MatCMS.Plugin.Shop.ShopHtml;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The brands in public: the page of all brands (<c>/shop/brands</c>), a brand's own page (<c>/shop/brands/{slug}</c> — logo, picture, short text, the details that are shown nowhere else, the
/// manufacturer's website and the brand's products), the block <c>shop_brands</c> that shows them as a logo wall, cards or a carousel on any page, a menu element and the link targets.
/// <para>For search engines a brand's page is a page of its own with its own title and description, schema.org <c>Brand</c> and breadcrumbs, and every product names its brand in its own
/// structured data — so "Transrotor Plattenspieler" finds the brand's page, not only a filtered list.</para>
/// </summary>
internal static class ShopBrands
{
    private const string SvgBrands = "<rect x=\"3\" y=\"5\" width=\"7\" height=\"6\" rx=\"1\"/><rect x=\"14\" y=\"5\" width=\"7\" height=\"6\" rx=\"1\"/><rect x=\"3\" y=\"14\" width=\"7\" height=\"6\" rx=\"1\"/><rect x=\"14\" y=\"14\" width=\"7\" height=\"6\" rx=\"1\"/>";

    /// <summary>What the brand gallery, the brand's page and the page of all brands need; scoped under <c>.shop</c> and built on the site's own variables like the rest of the shop.</summary>
    internal const string BrandCss = """
        .shop-brands{display:grid;grid-template-columns:repeat(auto-fill,minmax(170px,1fr));gap:16px}
        .shop-brands.cols-3{grid-template-columns:repeat(3,minmax(0,1fr))}.shop-brands.cols-4{grid-template-columns:repeat(4,minmax(0,1fr))}.shop-brands.cols-5{grid-template-columns:repeat(5,minmax(0,1fr))}.shop-brands.cols-6{grid-template-columns:repeat(6,minmax(0,1fr))}
        @media(max-width:900px){.shop-brands.cols-5,.shop-brands.cols-6{grid-template-columns:repeat(3,minmax(0,1fr))}}
        @media(max-width:600px){.shop-brands,.shop-brands.cols-3,.shop-brands.cols-4,.shop-brands.cols-5,.shop-brands.cols-6{grid-template-columns:repeat(2,minmax(0,1fr));gap:10px}}
        .shop-brand{display:flex;flex-direction:column;align-items:center;gap:8px;padding:16px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.12)));border-radius:var(--shop-radius,12px);text-decoration:none;color:inherit;background:var(--shop-card,transparent);transition:transform .2s ease,box-shadow .2s ease}
        .shop-brand:hover{transform:translateY(-2px);box-shadow:0 12px 28px rgba(0,0,0,.08)}@media(prefers-reduced-motion:reduce){.shop-brand{transition:none}.shop-brand:hover{transform:none}}
        .shop-brand .logo{display:flex;align-items:center;justify-content:center;width:100%;aspect-ratio:3/2}.shop-brand .logo img{max-width:100%;max-height:100%;object-fit:contain}
        .shop-brand .logo .ph{font-weight:700;font-size:1.15em;text-align:center;font-family:var(--shop-font-head,var(--font-head,inherit))}
        .shop-brand .name{font-weight:600;text-align:center;font-family:var(--shop-font-head,var(--font-head,inherit))}.shop-brand .count{font-size:.8em;opacity:.65}
        .shop-brand.card{align-items:stretch;text-align:left}.shop-brand.card .logo{aspect-ratio:auto;height:120px;padding:6px 0}.shop-brand.card .name{text-align:left}.shop-brand.card .summary{font-size:.9em;opacity:.8;margin:0}
        .shop-brands.bare .shop-brand,.shop-carousel.bare .shop-brand{border-color:transparent;background:transparent}.shop-brands.bare .shop-brand:hover,.shop-carousel.bare .shop-brand:hover{box-shadow:none}
        .shop-brands.grey .shop-brand img,.shop-carousel.grey .shop-brand img{filter:grayscale(1);opacity:.7;transition:filter .2s ease,opacity .2s ease}.shop-brands.grey .shop-brand:hover img,.shop-carousel.grey .shop-brand:hover img{filter:none;opacity:1}
        .shop-carousel .track .shop-brand{flex:0 0 min(190px,44%);scroll-snap-align:start}
        .shop-brandhead{display:flex;gap:28px;align-items:center;margin:0 0 22px}.shop-brandhead .logo{flex:none;width:200px;max-height:130px;display:flex;align-items:center;justify-content:center}
        .shop-brandhead .logo img{max-width:100%;max-height:130px;object-fit:contain}.shop-brandhead h1{margin:0 0 8px}.shop-brandhead p{margin:0 0 8px}
        @media(max-width:640px){.shop-brandhead{flex-direction:column;align-items:flex-start;gap:14px}.shop-brandhead .logo{width:150px}}
        .shop-brandhero{aspect-ratio:21/8;border-radius:var(--shop-radius,12px);background:rgba(0,0,0,.06) center/cover no-repeat;margin:0 0 26px}
        .shop-branddetails{margin:0 0 30px;max-width:860px}.shop-brandsite{font-size:.92em}.shop-brandlink{display:inline-flex;align-items:center;gap:10px;margin:0 0 8px;text-decoration:none;font-size:.9em;opacity:.85}
        .shop-brandlink img{height:34px;max-width:120px;object-fit:contain}.shop-brandlink:hover{opacity:1}
        """;

    // ---- registering ------------------------------------------------------------------------------------------

    public static void Register(IPluginBuilder b)
    {
        var get = new PublicRouteOptions { Methods = PluginMethod.Get };
        b.Route(ShopUrls.BrandsPage, BrandsPageAsync, get);
        b.Route(ShopUrls.BrandsPage + "/{slug}", BrandPageAsync, get);

        b.OptionsSource("brands", async (ctx, ct) => (await new ShopStore(ctx.Data).BrandsAsync(true, ct)).Select(x => new FieldOption(x.Slug, x.Name)).ToList());
        var yesNo = new[] { new FieldOption("yes", "shop.block.yes"), new FieldOption("no", "shop.block.no") };
        b.Block(new BlockSpec("shop_brands", "shop.blockb.name", "shop.blockb.desc")
        {
            Category = "design", IconSvg = SvgBrands,
            Fields = new[]
            {
                new FieldSpec("heading", "shop.block.heading", FieldKind.Text),
                new FieldSpec("source", "shop.blockb.source", FieldKind.Select)
                {
                    Default = "all", Options = new[] { new FieldOption("all", "shop.blockb.src.all"), new FieldOption("featured", "shop.blockb.src.featured"), new FieldOption("pick", "shop.blockb.src.pick") }
                },
                new FieldSpec("brands", "shop.blockb.brands", FieldKind.MultiSelect) { OptionsSource = "brands", Help = "shop.blockb.brandsHelp", ShowWhenField = "source", ShowWhenValue = "pick" },
                new FieldSpec("style", "shop.blockb.style", FieldKind.Select)
                {
                    Default = "logos", Options = new[] { new FieldOption("logos", "shop.blockb.style.logos"), new FieldOption("cards", "shop.blockb.style.cards"), new FieldOption("carousel", "shop.blockb.style.carousel") }
                },
                new FieldSpec("columns", "shop.block.columns", FieldKind.Select)
                {
                    Default = "auto",
                    Options = new[]
                    {
                        new FieldOption("auto", "shop.block.columns.auto"), new FieldOption("3", "shop.block.columns.3"), new FieldOption("4", "shop.block.columns.4"),
                        new FieldOption("5", "shop.blockb.columns.5"), new FieldOption("6", "shop.blockb.columns.6")
                    }
                },
                new FieldSpec("showName", "shop.blockb.showName", FieldKind.Select) { Default = "yes", Options = yesNo },
                new FieldSpec("showCount", "shop.blockb.showCount", FieldKind.Select) { Default = "no", Options = yesNo },
                new FieldSpec("frame", "shop.blockb.frame", FieldKind.Select) { Default = "yes", Options = yesNo },
                new FieldSpec("grey", "shop.blockb.grey", FieldKind.Select) { Default = "no", Options = yesNo, Help = "shop.blockb.greyHelp" },
                new FieldSpec("limit", "shop.blockb.limit", FieldKind.Text) { Default = "", Help = "shop.blockb.limitHelp" },
                new FieldSpec("allLink", "shop.block.allLink", FieldKind.Select) { Default = "no", Options = yesNo }
            }
        }, BlockAsync);

        b.MenuElement(new MenuElementSpec("brands", "shop.el.brands") { Help = "shop.el.brandsHelp", Icon = "award" }, MenuElementAsync);
        b.LinkTargets("shop.lt.brands", LinkTargetsAsync);
    }

    // ---- shared pieces ----------------------------------------------------------------------------------------

    /// <summary>The logo as an image, or the name set as a word when there is none — a brand is always recognisable.</summary>
    internal static string LogoHtml(Brand x)
    {
        var logo = ShopPublic.SafeUrl(x.Logo);
        return logo.Length > 0 ? $"<img src=\"{E(logo)}\" alt=\"{E(x.Name)}\" loading=\"lazy\">" : $"<span class=\"ph\">{E(x.Name)}</span>";
    }

    /// <summary>One brand in a gallery: logo, optionally the name, the short text (cards) and how many products it has; a link to its page.</summary>
    internal static string Tile(IPluginContext ctx, Brand x, bool card, bool name, int? count)
    {
        var sb = new StringBuilder($"<a class=\"shop-brand{(card ? " card" : "")}\" href=\"{E(ShopUrls.Brand(x.Slug))}\"{(name || card ? "" : $" aria-label=\"{E(x.Name)}\" title=\"{E(x.Name)}\"")}>");
        sb.Append("<span class=\"logo\">").Append(LogoHtml(x)).Append("</span>");
        // Without a logo the name already stands in its place.
        if ((name || card) && ShopPublic.SafeUrl(x.Logo).Length > 0) sb.Append($"<span class=\"name\">{E(x.Name)}</span>");
        if (card && x.Summary.Length > 0) sb.Append($"<p class=\"summary\">{E(x.Summary.Length > 160 ? x.Summary[..160].TrimEnd() + "…" : x.Summary)}</p>");
        if (count is { } n) sb.Append($"<span class=\"count\">{E(ShopPublic.ProductCount(ctx, n))}</span>");
        return sb.Append("</a>").ToString();
    }

    private static string Abs(IPluginContext ctx, string url) => url.StartsWith('/') ? ctx.Site.Origin + url : url;

    /// <summary>The manufacturer's website when it is a plain http(s) address — never anything a browser would run.</summary>
    internal static string SafeWebsite(string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0 || u.Length > 300) return "";
        if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) u = "https://" + u;
        return Uri.TryCreate(u, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" && parsed.Host.Contains('.') && u.IndexOfAny(new[] { '"', '\'', '<', '>', ' ', '\\' }) < 0 ? u : "";
    }

    /// <summary>What a product's page says about its brand: the name, the address of the brand's page, the logo and a small link ("logo · Mehr von Transrotor").</summary>
    internal static async Task<(Brand? Brand, Dictionary<string, string> Values)> ForProductAsync(IPluginContext ctx, ShopStore store, Product p, CancellationToken ct)
    {
        var x = p.Brand.Length > 0 ? await store.BrandBySlugAsync(p.Brand, ct) : null;
        if (x is null || !x.Active) return (null, new Dictionary<string, string> { ["brand"] = "", ["brand_url"] = "", ["brand_logo_url"] = "", ["brand_html"] = "" });
        var url = ShopUrls.Brand(x.Slug);
        var logo = ShopPublic.SafeUrl(x.Logo);
        var html = $"<a class=\"shop-brandlink\" href=\"{E(url)}\">{(logo.Length > 0 ? $"<img src=\"{E(logo)}\" alt=\"{E(x.Name)}\">" : $"<strong>{E(x.Name)}</strong>")}<span>{E(ctx.T("shop.brand.more", x.Name))}</span></a>";
        return (x, new Dictionary<string, string> { ["brand"] = x.Name, ["brand_url"] = url, ["brand_logo_url"] = logo, ["brand_html"] = html });
    }

    // ---- the page of all brands -------------------------------------------------------------------------------

    public static async Task<PluginResult> BrandsPageAsync(IPluginContext ctx, CancellationToken ct) => await ShopPageHost.RunAsync(ctx, "brands", await BrandsViewAsync(ctx, ct), ct);

    internal static async Task<PageView> BrandsViewAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = ShopStore.Public(ctx);
        var brands = await store.BrandsAsync(true, ct);
        var counts = await store.BrandCountsAsync(true, ct);
        var grid = new StringBuilder();
        if (brands.Count == 0) grid.Append($"<p class=\"shop-muted\">{E(ctx.T("shop.brands.empty"))}</p>");
        else
        {
            grid.Append("<div class=\"shop-brands\">");
            foreach (var x in brands) grid.Append(Tile(ctx, x, true, true, counts.GetValueOrDefault(x.Slug)));
            grid.Append("</div>");
        }
        var title = ctx.T("shop.brands");
        var values = new Dictionary<string, string>
        {
            ["title"] = title, ["total"] = Intl(brands.Count), ["grid_html"] = grid.ToString(),
            ["crumbs_html"] = $"<p class=\"shop-crumbs\"><a href=\"/shop\">{E(ctx.T("shop.title"))}</a> › {E(title)}</p>"
        };
        var tail = "<script type=\"application/ld+json\">" + new JsonObject
        {
            ["@context"] = "https://schema.org", ["@type"] = "ItemList", ["name"] = title,
            ["itemListElement"] = new JsonArray(brands.Select((x, i) => (JsonNode)new JsonObject { ["@type"] = "ListItem", ["position"] = i + 1, ["url"] = ctx.Site.Origin + ShopUrls.Brand(x.Slug), ["name"] = x.Name }).ToArray())
        }.ToJsonString().Replace("<", "\\u003c") + "</script>";
        return new PageView(null, title, ctx.T("shop.brands.description", ctx.Site.Name), values, ctx.RenderPart("brands", values)) { Tail = tail };
    }

    // ---- a brand's page ---------------------------------------------------------------------------------------

    public static async Task<PluginResult> BrandPageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = ShopStore.Public(ctx);
        var x = await store.BrandBySlugAsync(ctx.Request.R("slug").Trim(), ct);
        if (x is null || !x.Active && !ctx.User.IsAdmin)
            return await PageAsync(ctx, ShopConfig.From(ctx.Settings), ctx.T("shop.notFound"), $"<h1>{E(ctx.T("shop.notFound"))}</h1><p><a href=\"{ShopUrls.BrandsPage}\">{E(ctx.T("shop.brands.back"))}</a></p>", status: 404, noIndex: true);
        return await ShopPageHost.RunAsync(ctx, "brand", await BrandViewAsync(ctx, store, x, ct), ct);
    }

    internal static async Task<PageView> BrandViewAsync(IPluginContext ctx, ShopStore store, Brand x, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var page = int.TryParse(ctx.Request.Q("page"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pg) && pg > 0 ? pg : 1;
        var sort = ProductQuery.NormaliseSort(ctx.Request.Q("sort"));
        var (items, total) = await store.FindProductsAsync(new ProductQuery { Brands = new[] { x.Slug }, Sort = sort, Skip = (page - 1) * cfg.PerPage, Take = cfg.PerPage }, ct);
        var variants = (await store.VariantsOfAsync(items.Select(p => p.Id), ct)).Where(v => v.Active).GroupBy(v => v.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var url = ShopUrls.Brand(x.Slug);

        var grid = new StringBuilder();
        if (items.Count > 0)
        {
            grid.Append("<div class=\"shop-grid\">");
            foreach (var p in items) grid.Append(ShopPublic.CardHtml(ctx, cfg, p, variants.TryGetValue(p.Id, out var vs) ? vs : new List<Variant>(), new ShopPublic.CardView(true, true, false)));
            grid.Append("</div>");
        }
        var pager = new StringBuilder();
        var pages = (int)Math.Ceiling(total / (double)cfg.PerPage);
        if (pages > 1)
        {
            pager.Append("<div class=\"shop-pager\">");
            for (var i = 1; i <= pages; i++)
            {
                var link = url + (i > 1 || sort.Length > 0 ? "?" + string.Join("&", new[] { sort.Length > 0 ? "sort=" + Uri.EscapeDataString(sort) : "", i > 1 ? "page=" + Intl(i) : "" }.Where(s => s.Length > 0)) : "");
                pager.Append(i == page ? $"<span class=\"on\">{i}</span>" : $"<a href=\"{E(link)}\">{i}</a>");
            }
            pager.Append("</div>");
        }
        var logo = ShopPublic.SafeUrl(x.Logo);
        var image = ShopPublic.SafeUrl(x.Image);
        var website = SafeWebsite(x.Website);
        var websiteHtml = website.Length > 0 ? $"<p class=\"shop-brandsite\"><a href=\"{E(website)}\" target=\"_blank\" rel=\"noopener\">{E(ctx.T("shop.brand.website"))} ↗</a></p>" : "";
        var logoHtml = logo.Length > 0 ? $"<div class=\"logo\"><img src=\"{E(logo)}\" alt=\"{E(x.Name)}\"></div>" : "";
        var head = $"<div class=\"shop-brandhead\">{logoHtml}<div><h1>{E(x.Name)}</h1>{(x.Summary.Length > 0 ? $"<p class=\"shop-muted\">{E(x.Summary)}</p>" : "")}{websiteHtml}</div></div>";
        var shopLink = ShopUrls.Catalog("", extra: new[] { new KeyValuePair<string, string>("brand", x.Slug) });
        var notice = x.Active ? "" : Alert(ctx.T("shop.brand.inactiveNote"), "info");
        var values = new Dictionary<string, string>
        {
            ["title"] = x.Name, ["name"] = x.Name, ["summary"] = x.Summary, ["url"] = url, ["logo_url"] = logo, ["image_url"] = image, ["website"] = website, ["total"] = Intl(total),
            ["crumbs_html"] = $"<p class=\"shop-crumbs\"><a href=\"/shop\">{E(ctx.T("shop.title"))}</a> › <a href=\"{ShopUrls.BrandsPage}\">{E(ctx.T("shop.brands"))}</a> › {E(x.Name)}</p>" + notice,
            ["head_html"] = head, ["logo_html"] = logoHtml, ["website_html"] = websiteHtml,
            ["image_html"] = image.Length > 0 ? $"<div class=\"shop-brandhero\" style=\"background-image:url('{E(image)}')\" role=\"img\" aria-label=\"{E(x.Name)}\"></div>" : "",
            ["details_html"] = x.DetailsHtml.Length > 0 ? $"<div class=\"shop-desc shop-branddetails\">{x.DetailsHtml}</div>" : "",       // sanitised when it was saved
            ["products_heading"] = total > 0 ? ctx.T("shop.brand.products", x.Name) : "", ["products_html"] = grid.ToString(), ["pager_html"] = pager.ToString(),
            ["more_html"] = total > 0 ? $"<p class=\"shop-more\"><a class=\"shop-btn ghost\" href=\"{E(shopLink)}\">{E(ctx.T("shop.brand.filter", x.Name))}</a></p>" : ""
        };

        // What a search engine and a shared link show.
        var seoTitle = x.SeoTitle.Length > 0 ? x.SeoTitle : ctx.T("shop.brand.seoTitle", x.Name, ctx.Site.Name);
        var seoDescription = x.SeoDescription.Length > 0 ? x.SeoDescription : x.Summary.Length > 0 ? (x.Summary.Length > 160 ? x.Summary[..157].TrimEnd() + "…" : x.Summary) : null;
        var share = image.Length > 0 ? image : logo;
        var meta = new List<KeyValuePair<string, string>> { new("og:type", "website"), new("og:title", seoTitle), new("og:url", ctx.Site.Origin + url) };
        if (seoDescription is not null) meta.Add(new("og:description", seoDescription));
        if (share.Length > 0) meta.Add(new("og:image", Abs(ctx, share)));
        var brandLd = new JsonObject { ["@type"] = "Brand", ["@id"] = ctx.Site.Origin + url + "#brand", ["name"] = x.Name, ["url"] = ctx.Site.Origin + url };
        if (logo.Length > 0) brandLd["logo"] = Abs(ctx, logo);
        if (image.Length > 0) brandLd["image"] = Abs(ctx, image);
        if (x.Summary.Length > 0) brandLd["description"] = x.Summary;
        if (website.Length > 0) brandLd["sameAs"] = new JsonArray(website);
        var crumbsLd = new JsonObject
        {
            ["@type"] = "BreadcrumbList",
            ["itemListElement"] = new JsonArray(
                new JsonObject { ["@type"] = "ListItem", ["position"] = 1, ["name"] = ctx.T("shop.title"), ["item"] = ctx.Site.Origin + ShopUrls.Root },
                new JsonObject { ["@type"] = "ListItem", ["position"] = 2, ["name"] = ctx.T("shop.brands"), ["item"] = ctx.Site.Origin + ShopUrls.BrandsPage },
                new JsonObject { ["@type"] = "ListItem", ["position"] = 3, ["name"] = x.Name, ["item"] = ctx.Site.Origin + url })
        };
        var tail = "<script type=\"application/ld+json\">" + new JsonObject { ["@context"] = "https://schema.org", ["@graph"] = new JsonArray(brandLd, crumbsLd) }.ToJsonString().Replace("<", "\\u003c") + "</script>";
        return new PageView(null, seoTitle, seoDescription, values, ctx.RenderPart("brand", values)) { Tail = tail, Meta = meta, NoIndex = !x.Active };
    }

    // ---- the gallery block ------------------------------------------------------------------------------------

    private static string Field(IPluginContext ctx, string id) => (ctx.Block?.Get(id) ?? "").Trim();

    private static async Task<PluginResult> BlockAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = ShopStore.Public(ctx);
        var all = await store.BrandsAsync(true, ct);
        IEnumerable<Brand> shown = all;
        switch (Field(ctx, "source"))
        {
            case "featured": shown = all.Where(x => x.Featured); break;
            case "pick":
                // The order the editor picked them in.
                var picked = Field(ctx, "brands").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                shown = picked.Select(s => all.FirstOrDefault(x => x.Slug == s)).OfType<Brand>();
                break;
        }
        if (int.TryParse(Field(ctx, "limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit > 0) shown = shown.Take(Math.Min(limit, 200));
        var list = shown.ToList();
        if (list.Count == 0) return PluginResult.Fragment("");

        var style = Field(ctx, "style");
        var card = style == "cards";
        var name = Field(ctx, "showName") != "no";
        var counts = Field(ctx, "showCount") == "yes" ? await store.BrandCountsAsync(true, ct) : null;
        var cls = "shop-brands" + (Field(ctx, "columns") is "3" or "4" or "5" or "6" ? " cols-" + Field(ctx, "columns") : "") + (Field(ctx, "frame") == "no" ? " bare" : "") + (Field(ctx, "grey") == "yes" ? " grey" : "");
        var tiles = new StringBuilder();
        foreach (var x in list) tiles.Append(Tile(ctx, x, card, name, counts?.GetValueOrDefault(x.Slug)));

        var sb = new StringBuilder("<style>").Append(CardCss).Append(BrandCss).Append("</style><section class=\"section\"><div class=\"container\"><div class=\"shop shop-block\">");
        if (Field(ctx, "heading") is { Length: > 0 } heading) sb.Append("<h2>").Append(E(heading)).Append("</h2>");
        if (style == "carousel")
        {
            var id = "shop-bcar-" + Guid.NewGuid().ToString("N")[..8];
            sb.Append($"<div class=\"shop-carousel {cls.Replace("shop-brands", "").Trim()}\" id=\"{id}\"><button type=\"button\" class=\"prev\" aria-label=\"{E(ctx.T("shop.block.prev"))}\">‹</button><div class=\"track shop-brands-track\" tabindex=\"0\">{tiles}</div><button type=\"button\" class=\"next\" aria-label=\"{E(ctx.T("shop.block.next"))}\">›</button></div>");
            sb.Append($"<script>(function(){{var r=document.getElementById('{id}');if(!r)return;var t=r.querySelector('.track'),p=r.querySelector('.prev'),n=r.querySelector('.next');function u(){{p.hidden=t.scrollLeft<4;n.hidden=t.scrollLeft+t.clientWidth>=t.scrollWidth-4}}function g(d){{t.scrollBy({{left:d*t.clientWidth*.85,behavior:'smooth'}})}}p.onclick=function(){{g(-1)}};n.onclick=function(){{g(1)}};t.addEventListener('scroll',u,{{passive:true}});window.addEventListener('resize',u);u()}})()</script>");
        }
        else sb.Append($"<div class=\"{cls}\">").Append(tiles).Append("</div>");
        if (Field(ctx, "allLink") == "yes") sb.Append($"<p class=\"shop-more\"><a class=\"shop-btn ghost\" href=\"{ShopUrls.BrandsPage}\">{E(ctx.T("shop.blockb.allLinkText"))}</a></p>");
        return PluginResult.Fragment(sb.Append("</div></div></section>").ToString());
    }

    // ---- menus and links --------------------------------------------------------------------------------------

    /// <summary>"Marken": the brands as a dropdown (highlighted first, at most 40), the heading opens the page of all brands.</summary>
    private static async Task<MenuEntry?> MenuElementAsync(IPluginContext ctx, CancellationToken ct)
    {
        var brands = await ShopStore.Public(ctx).BrandsAsync(true, ct);
        return new MenuEntry(ctx.T("shop.nav.brands"), ShopUrls.BrandsPage) { Children = brands.Take(40).Select(x => new MenuEntry(x.Name, ShopUrls.Brand(x.Slug))).ToList() };
    }

    private static async Task<IEnumerable<LinkTarget>> LinkTargetsAsync(IPluginContext ctx, CancellationToken ct) =>
        new[] { new LinkTarget(ctx.T("shop.lt.allBrands"), ShopUrls.BrandsPage) }
            .Concat((await new ShopStore(ctx.Data).BrandsAsync(true, ct)).Select(x => new LinkTarget(x.Name, ShopUrls.Brand(x.Slug)))).ToList();
}
