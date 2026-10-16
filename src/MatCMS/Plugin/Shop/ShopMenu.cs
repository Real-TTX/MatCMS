using System.Globalization;
using System.Text;
using MatCMS.Plugin;
using static MatCMS.Plugin.Shop.ShopHtml;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The shop inside the site's own navigation and pages: entries it adds to the menus (the cart with its item count and the way in to the account in the icon strip, the shop with its
/// categories as a dropdown in the main menu) and blocks an editor puts on any page (categories, search, a small cart, the member's account). The account is the CMS's own members
/// area — the shop has no accounts of its own: the sign-in page, the roles and "members only" pages are the CMS's, the shop adds what a customer does with them.
/// </summary>
internal static class ShopMenu
{
    // ---- menu elements --------------------------------------------------------------------------------------
    // Pieces the editor puts into the site's menus (Admin → Menü → "Plugin-Element"); the first time the shop is loaded they are added to the icon strip and the main menu, as ordinary items that
    // can be moved, renamed and deleted. Everything depends on the visitor: the cart is his, and "Anmelden" becomes "Mein Konto" once he is a member. The look comes from the site's template:
    // the cart and the account go through its menu markup (icon, badge), the search box is a template part (shop-search.html).

    /// <summary>"Shop", with the top-level categories as a dropdown.</summary>
    private static async Task<MenuEntry?> ShopElementAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = ShopStore.Public(ctx);
        var (counts, _) = await store.FacetsAsync(ct);
        var cats = ShopCategories.Roots(await store.CategoriesAsync(ct)).Where(c => counts.ContainsKey(c.Slug))
            .Select(c => new MenuEntry(c.Name, ShopUrls.Category(c.Slug))).ToList();
        return new MenuEntry(ctx.T("shop.nav.shop"), "/shop") { Children = cats };
    }

    /// <summary>"Kategorien": the whole category tree as a multi-level dropdown (category, subcategory, …) and a click on the heading opens the page of all categories.</summary>
    private static async Task<MenuEntry?> CategoriesElementAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = ShopStore.Public(ctx);
        var (counts, _) = await store.FacetsAsync(ct);
        var cats = await store.CategoriesAsync(ct);
        MenuEntry Node(Category c, int depth) => new(c.Name, ShopUrls.Category(c.Slug))
        {
            Children = depth >= 3 ? Array.Empty<MenuEntry>() : ShopCategories.Children(cats, c.Id).Where(k => counts.ContainsKey(k.Slug)).Select(k => Node(k, depth + 1)).ToList()
        };
        return new MenuEntry(ctx.T("shop.nav.categories"), ShopUrls.CategoriesPage) { Children = ShopCategories.Roots(cats).Where(c => counts.ContainsKey(c.Slug)).Select(c => Node(c, 1)).ToList() };
    }

    /// <summary>"Schlagwörter": the busiest keywords as a dropdown, the heading opens the page of all of them.</summary>
    private static async Task<MenuEntry?> TagsElementAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (_, tags) = await ShopStore.Public(ctx).FacetsAsync(ct);
        return new MenuEntry(ctx.T("shop.nav.tags"), ShopUrls.TagsPage) { Children = tags.Take(20).Select(t => new MenuEntry(t.Tag, ShopUrls.Catalog("", t.Tag))).ToList() };
    }

    /// <summary>The cart with the number of pieces in the visitor's own cart.</summary>
    private static async Task<MenuEntry?> CartElementAsync(IPluginContext ctx, CancellationToken ct)
    {
        var pieces = (await ShopCart.LinesAsync(ctx, ShopStore.Public(ctx), ct)).Sum(l => l.Qty);
        return new MenuEntry(ctx.T("shop.nav.cart"), "/shop/cart") { Icon = "shopping-cart", Badge = pieces > 0 ? pieces.ToString(CultureInfo.InvariantCulture) : null };
    }

    /// <summary>"Mein Konto" for a signed-in member, "Anmelden" for everybody else.</summary>
    private static Task<MenuEntry?> AccountElementAsync(IPluginContext ctx, CancellationToken ct) => Task.FromResult<MenuEntry?>(ctx.Member is not null
        ? new MenuEntry(ctx.T("shop.nav.account"), "/konto") { Icon = "user-check" }
        : new MenuEntry(ctx.T("shop.nav.signin"), "/anmelden?returnUrl=%2Fkonto") { Icon = "user" });

    /// <summary>A search box that starts a product search. The markup is the template part "search", so the site's template can replace it and style it.</summary>
    private static Task<MenuEntry?> SearchElementAsync(IPluginContext ctx, CancellationToken ct)
    {
        var html = ctx.RenderPart("search", new Dictionary<string, string>
        {
            ["action"] = "/shop", ["placeholder"] = ctx.T("shop.search"), ["button"] = ctx.T("shop.searchButton"), ["query"] = ctx.Request.Q("q"), ["icon_html"] = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\">" + SvgSearch + "</svg>"
        });
        // The suggestions' script rides OUTSIDE the template part, so a template that rewrites the box cannot lose it.
        return Task.FromResult<MenuEntry?>(new MenuEntry(ctx.T("shop.nav.search"), "/shop") { Html = html + ShopSuggest.ScriptFor(ctx) });
    }

    // ---- link targets ----------------------------------------------------------------------------------------

    private static Task<IEnumerable<LinkTarget>> PageTargetsAsync(IPluginContext ctx, CancellationToken ct) => Task.FromResult<IEnumerable<LinkTarget>>(new[]
    {
        new LinkTarget(ctx.T("shop.lt.all"), "/shop"), new LinkTarget(ctx.T("shop.lt.allCategories"), "/shop/categories"), new LinkTarget(ctx.T("shop.nav.cart"), "/shop/cart"), new LinkTarget(ctx.T("shop.checkout"), "/shop/checkout"),
        new LinkTarget(ctx.T("shop.nav.account"), "/konto")
    });

    private static async Task<IEnumerable<LinkTarget>> CategoryTargetsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cats = await new ShopStore(ctx.Data).CategoriesAsync(ct);
        return ShopCategories.Flatten(cats).Select(t => new LinkTarget(ShopCategories.Label(cats, t.Cat), ShopUrls.Category(t.Cat.Slug))).ToList();
    }

    /// <summary>One entry per keyword that sits on a category ("Neuware"): a page with the categories that carry it.</summary>
    private static async Task<IEnumerable<LinkTarget>> CategoryTagTargetsAsync(IPluginContext ctx, CancellationToken ct) =>
        (await new ShopStore(ctx.Data).CategoriesAsync(ct)).SelectMany(c => ShopTags.Split(c.Tags)).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Select(t => new LinkTarget(t, ShopUrls.CategoriesPage + "?tag=" + Uri.EscapeDataString(t))).ToList();

    /// <summary>The page of all keywords, and one entry per keyword on the products (the catalogue filtered by it).</summary>
    private static async Task<IEnumerable<LinkTarget>> TagTargetsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (_, tags) = await new ShopStore(ctx.Data).FacetsAsync(ct);
        return new[] { new LinkTarget(ctx.T("shop.lt.allTags"), ShopUrls.TagsPage) }.Concat(tags.Select(t => new LinkTarget(t.Tag, ShopUrls.Catalog("", t.Tag)))).ToList();
    }

    private static async Task<IEnumerable<LinkTarget>> ProductTargetsAsync(IPluginContext ctx, CancellationToken ct) =>
        (await new ShopStore(ctx.Data).FindProductsAsync(new ProductQuery { Sort = "name", Take = 300 }, ct)).Items.Select(p => new LinkTarget(p.Title, "/shop/" + Uri.EscapeDataString(p.Slug))).ToList();

    // ---- blocks ----------------------------------------------------------------------------------------

    private const string SvgCats = "<path d=\"M4 6h16M4 12h16M4 18h10\"/>";
    private const string SvgSearch = "<circle cx=\"11\" cy=\"11\" r=\"6\"/><path d=\"M20 20l-4.5-4.5\"/>";
    private const string SvgCart = "<circle cx=\"9\" cy=\"20\" r=\"1.5\"/><circle cx=\"18\" cy=\"20\" r=\"1.5\"/><path d=\"M3 4h2l2.5 11h11L21 7H6\"/>";
    private const string SvgUser = "<circle cx=\"12\" cy=\"8\" r=\"4\"/><path d=\"M4 21c1-4 4-6 8-6s7 2 8 6\"/>";

    private static string Field(IPluginContext ctx, string id) => (ctx.Block?.Get(id) ?? "").Trim();

    /// <summary>The wrapper every one of these blocks sits in — the site's container, the shop's scoped style.</summary>
    private static string Wrap(IPluginContext ctx, string inner, string extraCss = "")
    {
        var heading = Field(ctx, "heading") is { Length: > 0 } h ? $"<h2>{E(h)}</h2>" : "";
        return $"<style>{CardCss}{BlockCss}{extraCss}</style><section class=\"section\"><div class=\"container\"><div class=\"shop shop-block\">{heading}{inner}</div></div></section>";
    }

    private const string BlockCss = """
        .shop-links{display:flex;gap:8px;flex-wrap:wrap}.shop-links a{padding:7px 14px;border:1px solid var(--shop-line,rgba(0,0,0,.2));border-radius:99px;text-decoration:none}
        .shop-links a span{opacity:.6;margin-left:4px;font-size:.85em}.shop-links a.big{border-radius:8px}
        .shop-linklist{list-style:none;padding:0;margin:0;display:grid;gap:6px}.shop-linklist a{text-decoration:none}
        .shop-panel{border:1px solid var(--shop-line,rgba(0,0,0,.12));border-radius:10px;padding:16px 18px;max-width:520px}
        .shop-panel table{width:100%;border-collapse:collapse;margin:8px 0}.shop-panel td{padding:5px 0;border-bottom:1px solid var(--shop-line,rgba(0,0,0,.08))}.shop-panel td.r{text-align:right}
        .shop-searchbar{display:flex;gap:8px;max-width:520px}.shop-searchbar input{flex:1;padding:10px 12px;border:1px solid rgba(0,0,0,.25);border-radius:8px;font:inherit}
        """;

    public static void Register(IPluginBuilder b)
    {
        // The icon strip gets the search box, the cart and the way to sign in; the main menu gets "Shop" with its categories — once, as ordinary menu items.
        b.MenuElement(new MenuElementSpec("search", "shop.el.search") { Help = "shop.el.searchHelp", Icon = "search", DefaultMenu = "toolbar" }, SearchElementAsync);
        b.MenuElement(new MenuElementSpec("cart", "shop.el.cart") { Help = "shop.el.cartHelp", Icon = "shopping-cart", DefaultMenu = "toolbar" }, CartElementAsync);
        b.MenuElement(new MenuElementSpec("account", "shop.el.account") { Help = "shop.el.accountHelp", Icon = "user", DefaultMenu = "toolbar" }, AccountElementAsync);
        b.MenuElement(new MenuElementSpec("shop", "shop.el.shop") { Help = "shop.el.shopHelp", Icon = "building-store", DefaultMenu = "header" }, ShopElementAsync);
        b.MenuElement(new MenuElementSpec("categories", "shop.el.categories") { Help = "shop.el.categoriesHelp", Icon = "category" }, CategoriesElementAsync);
        b.MenuElement(new MenuElementSpec("tags", "shop.el.tags") { Help = "shop.el.tagsHelp", Icon = "tag" }, TagsElementAsync);
        b.LinkTargets("shop.lt.pages", PageTargetsAsync);
        b.LinkTargets("shop.lt.categoryTags", CategoryTagTargetsAsync);
        b.LinkTargets("shop.lt.tags", TagTargetsAsync);
        b.LinkTargets("shop.lt.categories", CategoryTargetsAsync);
        b.LinkTargets("shop.lt.products", ProductTargetsAsync);

        var yesNo = new[] { new FieldOption("yes", "shop.block.yes"), new FieldOption("no", "shop.block.no") };

        b.Block(new BlockSpec("shop_categories", "shop.blockc.name", "shop.blockc.desc")
        {
            Category = "design", IconSvg = SvgCats,
            Fields = new[]
            {
                new FieldSpec("heading", "shop.block.heading", FieldKind.Text),
                new FieldSpec("by", "shop.blockc.by", FieldKind.Select) { Default = "category", Options = new[] { new FieldOption("category", "shop.blockc.byCategory"), new FieldOption("tag", "shop.blockc.byTag") } },
                new FieldSpec("parent", "shop.blockc.parent", FieldKind.Select) { OptionsSource = "categories", Help = "shop.blockc.parentHelp", ShowWhenField = "by", ShowWhenValue = "category" },
                new FieldSpec("style", "shop.blockc.style", FieldKind.Select) { Default = "chips", Options = new[] { new FieldOption("chips", "shop.blockc.chips"), new FieldOption("list", "shop.blockc.list"), new FieldOption("tiles", "shop.blockc.tiles") } },
                new FieldSpec("counts", "shop.blockc.counts", FieldKind.Select) { Default = "yes", Options = yesNo },
                new FieldSpec("allLink", "shop.blockc.all", FieldKind.Select) { Default = "yes", Options = yesNo }
            }
        }, CategoriesAsync);

        b.Block(new BlockSpec("shop_search", "shop.blocks.name", "shop.blocks.desc")
        {
            Category = "design", IconSvg = SvgSearch,
            Fields = new[] { new FieldSpec("heading", "shop.block.heading", FieldKind.Text), new FieldSpec("placeholder", "shop.blocks.placeholder", FieldKind.Text) }
        }, SearchAsync);

        b.Block(new BlockSpec("shop_cart", "shop.blockk.name", "shop.blockk.desc")
        {
            Category = "design", IconSvg = SvgCart,
            Fields = new[] { new FieldSpec("heading", "shop.block.heading", FieldKind.Text) }
        }, CartAsync);

        b.Block(new BlockSpec("shop_account", "shop.blocka.name", "shop.blocka.desc")
        {
            Category = "design", IconSvg = SvgUser,
            Fields = new[]
            {
                new FieldSpec("heading", "shop.block.heading", FieldKind.Text),
                new FieldSpec("orders", "shop.blocka.orders", FieldKind.Text) { Default = "5", Help = "shop.blocka.ordersHelp" }
            }
        }, AccountAsync);
    }

    private static async Task<PluginResult> CategoriesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = ShopStore.Public(ctx);
        var (catCounts, tags) = await store.FacetsAsync(ct);
        var counts = Field(ctx, "counts") != "no";
        string Count(int n) => counts ? $"<span>{n.ToString(CultureInfo.InvariantCulture)}</span>" : "";
        var style = Field(ctx, "style");
        var sb = new StringBuilder();
        if (Field(ctx, "by") == "tag")
        {
            var tagItems = tags.Select(t => (t.Tag, "/shop?tag=" + Uri.EscapeDataString(t.Tag), t.Count)).ToList();
            if (tagItems.Count == 0) return PluginResult.Fragment("");
            var asList = style == "list";
            sb.Append(asList ? "<ul class=\"shop-linklist\">" : "<div class=\"shop-links\">");
            if (Field(ctx, "allLink") != "no") sb.Append(asList ? $"<li><a href=\"/shop\">{E(ctx.T("shop.allCategories"))}</a></li>" : $"<a href=\"/shop\">{E(ctx.T("shop.allCategories"))}</a>");
            foreach (var (label, url, n) in tagItems)
                sb.Append(asList ? $"<li><a href=\"{E(url)}\">{E(label)} {Count(n)}</a></li>" : $"<a href=\"{E(url)}\">{E(label)}{Count(n)}</a>");
            sb.Append(asList ? "</ul>" : "</div>");
            return PluginResult.Fragment(Wrap(ctx, sb.ToString()));
        }

        // Categories: the top level, or the subcategories of the one the editor named. Chips and tiles show that one level, a list shows the branches below it as well.
        var cats = await store.CategoriesAsync(ct);
        var start = Field(ctx, "parent") is { Length: > 0 } ps && cats.FirstOrDefault(c => c.Slug == ps) is { } pc ? ShopCategories.Children(cats, pc.Id) : ShopCategories.Roots(cats);
        var level = start.Where(c => catCounts.ContainsKey(c.Slug)).ToList();
        if (level.Count == 0) return PluginResult.Fragment("");
        string Url(Category c) => ShopUrls.Category(c.Slug);
        if (style == "tiles")
        {
            sb.Append("<div class=\"shop-subcats\">");
            foreach (var c in level) sb.Append(ShopPublic.CategoryTile(c, catCounts[c.Slug], Url(c)));
            sb.Append("</div>");
            return PluginResult.Fragment(Wrap(ctx, sb.ToString(), ShopHtml.TileCss));
        }
        else if (style == "list")
        {
            void Branch(IEnumerable<Category> items)
            {
                sb.Append("<ul class=\"shop-linklist\">");
                foreach (var c in items.Where(c => catCounts.ContainsKey(c.Slug)))
                {
                    sb.Append($"<li><a href=\"{E(Url(c))}\">{E(c.Name)} {Count(catCounts[c.Slug])}</a>");
                    if (ShopCategories.Children(cats, c.Id) is { Count: > 0 } below) Branch(below);
                    sb.Append("</li>");
                }
                sb.Append("</ul>");
            }
            if (Field(ctx, "allLink") != "no") sb.Append($"<p><a href=\"/shop\">{E(ctx.T("shop.allCategories"))}</a></p>");
            Branch(level);
        }
        else
        {
            sb.Append("<div class=\"shop-links\">");
            if (Field(ctx, "allLink") != "no") sb.Append($"<a href=\"/shop\">{E(ctx.T("shop.allCategories"))}</a>");
            foreach (var c in level) sb.Append($"<a href=\"{E(Url(c))}\">{E(c.Name)}{Count(catCounts[c.Slug])}</a>");
            sb.Append("</div>");
        }
        return PluginResult.Fragment(Wrap(ctx, sb.ToString()));
    }

    private static Task<PluginResult> SearchAsync(IPluginContext ctx, CancellationToken ct)
    {
        var ph = Field(ctx, "placeholder") is { Length: > 0 } p ? p : ctx.T("shop.search");
        return Task.FromResult(PluginResult.Fragment(Wrap(ctx, ShopSuggest.ScriptFor(ctx) + $"<form method=\"get\" action=\"/shop\" class=\"shop-searchbar\"><input type=\"search\" name=\"q\" placeholder=\"{E(ph)}\" aria-label=\"{E(ph)}\"><button class=\"shop-btn\" type=\"submit\">{E(ctx.T("shop.searchButton"))}</button></form>")));
    }

    private static async Task<PluginResult> CartAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = ShopStore.Public(ctx);
        var priced = await ShopOrders.PriceAsync(store, await ShopCart.LinesAsync(ctx, store, ct), cfg, ctx, ct);
        var sb = new StringBuilder("<div class=\"shop-panel\">");
        if (priced.Lines.Count == 0) sb.Append($"<p class=\"shop-muted\">{E(ctx.T("shop.cartEmpty"))}</p><a class=\"shop-btn ghost\" href=\"/shop\">{E(ctx.T("shop.continue"))}</a>");
        else
        {
            sb.Append("<table>");
            foreach (var l in priced.Lines.Take(6)) sb.Append($"<tr><td>{l.Qty} × {E(l.DisplayTitle)}</td><td class=\"r\">{E(Money.Format(l.LineCents, cfg.Currency, ctx.Locale))}</td></tr>");
            if (priced.Lines.Count > 6) sb.Append($"<tr><td class=\"shop-muted\" colspan=\"2\">… {priced.Lines.Count - 6}</td></tr>");
            sb.Append($"<tr><td><strong>{E(ctx.T("shop.subtotal"))}</strong></td><td class=\"r\"><strong>{E(Money.Format(priced.SubtotalCents, cfg.Currency, ctx.Locale))}</strong></td></tr></table>");
            sb.Append($"<a class=\"shop-btn ghost\" href=\"/shop/cart\">{E(ctx.T("shop.cart"))}</a> <a class=\"shop-btn\" href=\"/shop/checkout\">{E(ctx.T("shop.toCheckout"))}</a>");
        }
        return PluginResult.Fragment(Wrap(ctx, sb.Append("</div>").ToString()));
    }

    private static async Task<PluginResult> AccountAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var sb = new StringBuilder("<div class=\"shop-panel\">");
        if (ctx.Member is not { } member)
            sb.Append($"<p>{E(ctx.T("shop.blocka.guest"))}</p><a class=\"shop-btn\" href=\"/anmelden?returnUrl=%2Fkonto\">{E(ctx.T("shop.nav.signin"))}</a>");
        else
        {
            var take = int.TryParse(Field(ctx, "orders"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 0, 20) : 5;
            sb.Append($"<p>{E(ctx.T("shop.blocka.hello", member.DisplayName))}</p>");
            if (take > 0)
            {
                var orders = (await new ShopStore(ctx.Data).OrdersOfMemberAsync(member.Id, member.Email, ct)).Take(take).ToList();
                if (orders.Count == 0) sb.Append($"<p class=\"shop-muted\">{E(ctx.T("shop.blocka.noOrders"))}</p>");
                else
                {
                    sb.Append("<table>");
                    foreach (var o in orders)
                        sb.Append($"<tr><td><a href=\"/shop/order/{E(Uri.EscapeDataString(o.Number))}\">{E(o.Number)}</a> <span class=\"shop-muted shop-small\">{o.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}</span></td><td>{E(ctx.T("shop.status." + o.Status))}</td><td class=\"r\">{E(Money.Format(o.TotalCents, o.Currency, ctx.Locale))}</td></tr>");
                    sb.Append("</table>");
                }
            }
            sb.Append($"<a class=\"shop-btn ghost\" href=\"/konto/bestellungen\">{E(ctx.T("shop.myOrders"))}</a> <a class=\"shop-btn ghost\" href=\"/konto\">{E(ctx.T("shop.nav.account"))}</a>");
        }
        return PluginResult.Fragment(Wrap(ctx, sb.Append("</div>").ToString()));
    }
}
