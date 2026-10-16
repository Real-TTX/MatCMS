using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The starting layout of each of the shop's pages: sections, columns, headings and texts with variables, and the shop's own pieces placed with the <c>shop_slot</c> block. It draws what the page
/// showed before the editors could change it — a catalogue with a filter column and a grid, a product with its pictures beside its name, price and buy button — and is what "back to default" restores.
/// </summary>
internal static class ShopPageLayouts
{
    private static IReadOnlyDictionary<string, string> D(params (string, string)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    private static PageBlockSeed Section(params PageBlockSeed[] children) => new("el-section") { Data = D(("pad", "s")), Children = children };
    private static PageBlockSeed Columns(string layout, params PageBlockSeed[] columns) => new("el-columns") { Data = D(("layout", layout), ("valign", "top")), Children = columns };
    private static PageBlockSeed Columns(string layout, string showIf, params PageBlockSeed[] columns) => new("el-columns") { Data = D(("layout", layout), ("valign", "top"), ("_showIf", showIf)), Children = columns };
    private static PageBlockSeed Column(params PageBlockSeed[] children) => new("el-column") { Children = children };
    private static PageBlockSeed Slot(string name, string? showIf = null) =>
        new("shop_slot") { Data = showIf is null ? D(("slot", name)) : D(("slot", name), ("_showIf", showIf)) };
    private static PageBlockSeed Heading(string text, string level = "h1", string? showIf = null) =>
        new("el-heading") { Data = showIf is null ? D(("text", text), ("level", level)) : D(("text", text), ("level", level), ("_showIf", showIf)) };
    private static PageBlockSeed Text(string html, string? showIf = null) =>
        new("el-text") { Data = showIf is null ? D(("body", html)) : D(("body", html), ("_showIf", showIf)) };

    public static Func<PageSeedContext, IReadOnlyList<PageBlockSeed>> For(string role) => _ => role switch
    {
        "catalog" => new[]
        {
            Section(Slot("catalog.crumbs_html"), Slot("catalog.category_image_html"), Heading("{{catalog.title}}"),
                Text("<p>{{catalog.category_description}}</p>", "catalog.category_description"),
                Slot("catalog.subcategories_html"), Slot("catalog.search_html"),
                Columns("25-75",
                    Column(Slot("catalog.panel_html")),
                    Column(Slot("catalog.toolbar_html"), Slot("catalog.active_html"), Slot("catalog.grid_html"), Slot("catalog.pager_html"))))
        },
        "categories" => new[] { Section(Slot("categories.crumbs_html"), Heading("{{categories.title}}"), Slot("categories.tags_html"), Slot("categories.tiles_html")) },
        "tags" => new[] { Section(Slot("tags.crumbs_html"), Heading("{{tags.title}}"), Slot("tags.tags_html")) },
        "product" => new[]
        {
            Section(Slot("product.breadcrumbs_html"),
                Columns("50-50",
                    Column(Slot("product.image_html")),
                    Column(Heading("{{product.title}}"), Text("<p>{{product.summary}}</p>", "product.summary"), Slot("product.price_html"), Text("<p><small>{{product.tax_note}}</small></p>"),
                        Slot("product.notice_html"), Slot("product.form_html"), Text("<p><small>{{product.digital_note}}</small></p>", "product.digital_note"),
                        Slot("product.tags_html"), Slot("product.attributes_html"))),
                Heading("{{product.description_label}}", "h2", "product.description_html"), Slot("product.description_html"), Slot("product.related_html"))
        },
        "cart" => new[]
        {
            Section(Heading("{{cart.title}}"), Text("<p class=\"shop-muted\">{{cart.pieces_text}}</p>", "cart.has_items"), Slot("cart.notices_html"),
                Columns("67-33", "cart.has_items", Column(Slot("cart.lines_html")), Column(Slot("cart.summary_html"))),
                Slot("cart.empty_html"))
        },
        "checkout" => new[] { Section(Heading("{{checkout.title}}"), Slot("checkout.steps_html"), Slot("checkout.errors_html"), Slot("checkout.form_html")) },
        "order" => new[]
        {
            Section(Slot("order.head_html"), Slot("order.message_html"), Slot("order.steps_html"), Slot("order.pay_html"),
                Columns("67-33", Column(Slot("order.items_html"), Slot("order.history_html")), Column(Slot("order.side_html"))),
                Slot("order.back_html"))
        },
        "account" => new[]
        {
            Section(Columns("25-75",
                Column(Slot("account.profile_html"), Slot("account.nav_html"), Slot("account.signout_html")),
                Column(Heading("{{account.title}}"), Slot("account.content_html"))))
        },
        _ => Array.Empty<PageBlockSeed>()
    };
}
