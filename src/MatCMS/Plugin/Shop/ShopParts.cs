using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The shop's template parts: HTML files a site's template can replace to change how the shop looks — the product card (lists, blocks, related products), the product page and the
/// catalogue page and the page of all categories. Each ships its default here; the template editor shows them next to <c>body.html</c> (as <c>shop-card.html</c>, <c>shop-product.html</c>, <c>shop-catalog.html</c>).
/// The cart, the checkout and the customer's pages are forms the shop must keep working, so they stay in the plugin — their look is the shop's CSS (and the "own CSS" setting).
/// </summary>
internal static class ShopParts
{
    private static TemplatePlaceholder P(string name, bool html = false) => new(name, "shop.ph." + name, html);

    public const string CardDefault = """
        <div class="shop-card"><a href="{{url}}" class="img"{{image_style}} aria-label="{{title}}"></a><div class="body"><a class="title" href="{{url}}">{{title}}</a>{{summary_html}}{{price_html}}{{tags_html}}{{badges_html}}</div></div>
        """;

    public const string ProductDefault = """
        <div class="shop-product{{#unless image_html}} single{{/unless}}"><div{{#unless image_html}} style="display:none"{{/unless}}>{{image_html}}</div><div>
        {{breadcrumbs_html}}
        <h1>{{title}}</h1>
        {{#if summary}}<p class="shop-muted">{{summary}}</p>{{/if}}
        <p class="shop-price" style="font-size:1.5em">{{price_html}}</p>
        <p class="shop-muted shop-small">{{tax_note}}</p>
        {{notice_html}}{{form_html}}
        {{#if digital_note}}<p class="shop-small shop-muted">{{digital_note}}</p>{{/if}}
        {{tags_html}}
        {{attributes_html}}
        </div></div>
        {{#if description_html}}<h2>{{description_label}}</h2><div class="shop-desc">{{description_html}}</div>{{/if}}
        {{related_html}}
        """;

    public const string CatalogDefault = """
        {{crumbs_html}}
        {{category_image_html}}
        <h1>{{title}}</h1>
        {{#if category_description}}<p class="shop-muted shop-catdesc">{{category_description}}</p>{{/if}}
        {{subcategories_html}}
        {{search_html}}
        <div class="shop-layout {{layout_class}}">
        <input type="checkbox" id="shop-ft" class="shop-ftoggle" aria-hidden="true" tabindex="-1">
        <aside class="shop-side" aria-label="{{filter_label}}">{{categories_html}}{{filters_html}}{{tags_html}}</aside>
        <div class="shop-main">
        <div class="shop-toolbar"><label for="shop-ft" class="shop-ftbtn">{{filter_label}}</label>{{count_html}}{{sort_html}}</div>
        {{active_html}}{{grid_html}}{{pager_html}}
        </div>
        </div>
        """;

    /// <summary>The page of all categories (/shop/categories): the top level as tiles, or the whole tree as a nested list.</summary>
    public const string CategoriesDefault = """
        {{crumbs_html}}
        <h1>{{title}}</h1>
        {{tags_html}}
        {{tiles_html}}
        """;

    /// <summary>The customer's area (/konto and its pages): who is signed in, the navigation, and the page itself.</summary>
    public const string AccountDefault = """
        <div class="shop-acc">
        <aside class="shop-accside">
        <div class="shop-accwho"><span class="avatar" aria-hidden="true">{{initials}}</span><div><strong>{{name}}</strong><span class="mail">{{email}}</span></div></div>
        {{nav_html}}
        {{signout_html}}
        </aside>
        <section class="shop-accmain">
        <h1>{{title}}</h1>
        {{content_html}}
        </section>
        </div>
        """;

    /// <summary>The page of all keywords (/shop/tags).</summary>
    public const string TagsDefault = """
        {{crumbs_html}}
        <h1>{{title}}</h1>
        {{tags_html}}
        """;

    /// <summary>The search box in a menu: a form the site's template may rewrite and style (class <c>mat-search</c> is the default look).</summary>
    public const string SearchDefault = """
        <form class="mat-search" role="search" method="get" action="{{action}}"><input type="search" name="q" value="{{query}}" placeholder="{{placeholder}}" aria-label="{{placeholder}}"><button type="submit" aria-label="{{button}}">{{icon_html}}</button></form>
        """;

    public static void Register(IPluginBuilder b)
    {
        b.TemplatePart(new TemplatePartSpec("categories", "shop.part.categories", CategoriesDefault)
        {
            Help = "shop.part.categoriesHelp",
            Placeholders = new[] { P("title"), P("total"), P("tag"), P("crumbs_html", true), P("tags_html", true), P("tiles_html", true), P("list_html", true) }
        });
        b.TemplatePart(new TemplatePartSpec("tags", "shop.part.tags", TagsDefault)
        {
            Help = "shop.part.tagsHelp",
            Placeholders = new[] { P("title"), P("total"), P("crumbs_html", true), P("tags_html", true) }
        });
        b.TemplatePart(new TemplatePartSpec("account", "shop.part.account", AccountDefault)
        {
            Help = "shop.part.accountHelp",
            Placeholders = new[] { P("title"), P("section"), P("name"), P("email"), P("initials"), P("nav_html", true), P("signout_html", true), P("content_html", true) }
        });
        b.TemplatePart(new TemplatePartSpec("search", "shop.part.search", SearchDefault)
        {
            Help = "shop.part.searchHelp",
            Placeholders = new[] { P("action"), P("placeholder"), P("button"), P("query"), P("icon_html", true) }
        });
        b.TemplatePart(new TemplatePartSpec("card", "shop.part.card", CardDefault)
        {
            Help = "shop.part.cardHelp",
            Placeholders = new[]
            {
                P("url"), P("title"), P("image_url"), P("image_style", true), P("summary"), P("summary_html", true), P("price"), P("price_old"), P("save_percent"), P("price_html", true),
                P("tags_html", true), P("badges_html", true), P("sold_out"), P("digital"), P("on_sale"), P("category")
            }
        });
        b.TemplatePart(new TemplatePartSpec("product", "shop.part.product", ProductDefault)
        {
            Help = "shop.part.productHelp",
            Placeholders = new[]
            {
                P("title"), P("summary"), P("sku"), P("category"), P("category_url"), P("image_url"), P("image_html", true), P("price"), P("price_old"), P("save_percent"), P("price_html", true), P("tax_note"),
                P("breadcrumbs_html", true), P("notice_html", true), P("form_html", true), P("sold_out"), P("digital"), P("digital_note"), P("tags_html", true), P("description_html", true),
                P("description_label"), P("related_html", true), P("attributes_html", true)
            }
        });
        b.TemplatePart(new TemplatePartSpec("catalog", "shop.part.catalog", CatalogDefault)
        {
            Help = "shop.part.catalogHelp",
            Placeholders = new[]
            {
                P("title"), P("category"), P("category_description"), P("category_image_url"), P("category_image_html", true), P("crumbs_html", true), P("subcategories_html", true), P("query"), P("total"),
                P("search_html", true), P("categories_html", true), P("tags_html", true), P("filters_html", true), P("count_html", true), P("grid_html", true), P("pager_html", true),
                P("sort_html", true), P("active_html", true), P("filter_label"), P("layout_class")
            }
        });
    }
}
