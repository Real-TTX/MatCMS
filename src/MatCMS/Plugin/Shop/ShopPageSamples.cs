using System.Text;
using MatCMS.Plugin;
using static MatCMS.Plugin.Shop.ShopHtml;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// What the block editor shows next to a page's settings: the address of a real example (a product, the catalogue, the newest order) and — for the editor's live preview, which draws the unsaved draft
/// without going through the route — the variables of that example. Pages that need a signed-in customer or a full cart have no real example; they get sample pieces drawn with the shop's own
/// classes, so the layout around them can be judged with something in it.
/// </summary>
internal static class ShopPageSamples
{
    public static async Task<string?> PreviewUrlAsync(IPluginContext ctx, string role, CancellationToken ct)
    {
        var store = ShopStore.Public(ctx);
        switch (role)
        {
            case "catalog": return ShopUrls.Root;
            case "categories": return ShopUrls.CategoriesPage;
            case "tags": return ShopUrls.TagsPage;
            case "cart": return "/shop/cart";
            case "product":
                var p = (await store.FindProductsAsync(new ProductQuery { Take = 1, FeaturedOnly = false }, ct)).Items.FirstOrDefault();
                return p is null ? null : ShopUrls.Product(p.Slug);
            case "order":
                var o = (await store.OrdersAsync(take: 1, ct: ct)).Items.FirstOrDefault();
                return o is null ? null : $"/shop/order/{Uri.EscapeDataString(o.Number)}";
            default: return null;                                                // checkout and the customer area need a cart and a customer: the editor shows sample pieces
        }
    }

    public static async Task<PageScope?> ScopeAsync(IPluginContext ctx, string role, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = ShopStore.Public(ctx);
        PageView? view = role switch
        {
            "catalog" => await ShopPublic.CatalogViewAsync(ctx, ct),
            "categories" => await ShopPublic.CategoriesViewAsync(ctx, ct),
            "tags" => await ShopPublic.TagsViewAsync(ctx, ct),
            "product" => (await store.FindProductsAsync(new ProductQuery { Take = 1 }, ct)).Items.FirstOrDefault() is { } p ? await ShopPublic.ProductViewAsync(ctx, cfg, store, p, ct) : null,
            _ => null
        };
        var values = view is { Early: null } v ? new Dictionary<string, string>(v.Values) : Mock(ctx, cfg, role);
        if (!values.ContainsKey("nav_html")) values["nav_html"] = await NavAsync(ctx);
        return new PageScope(values, ShopPageRoles.HtmlNames(role));
    }

    // ---- sample pieces ----------------------------------------------------------------------------------

    private static string Box(string title, string inner) => $"<section class=\"shop-card-box\"><h2>{E(title)}</h2>{inner}</section>";

    private static string Line(string name, string price, int qty) =>
        $"<div class=\"shop-line\"><span class=\"img\"></span><div class=\"info\"><span class=\"title\">{E(name)}</span><div class=\"shop-muted shop-small\">{E(price)}</div><div class=\"controls\"><span class=\"shop-small shop-muted\">× {qty}</span></div></div><div class=\"lineprice\">{E(price)}</div></div>";

    private static Dictionary<string, string> Mock(IPluginContext ctx, ShopConfig cfg, string role)
    {
        string M(long c) => ShopPublic.M(ctx, cfg, c);
        var sum = $"<div class=\"shop-sum\"><div><span>{E(ctx.T("shop.subtotal"))}</span><span>{E(M(5980))}</span></div><div class=\"total\"><span>{E(ctx.T("shop.total"))}</span><span>{E(M(6470))}</span></div></div>";
        switch (role)
        {
            case "cart":
                return new Dictionary<string, string>
                {
                    ["title"] = ctx.T("shop.cart"), ["pieces"] = "3", ["pieces_text"] = ctx.T("shop.cartPieces", 3), ["has_items"] = "1", ["empty"] = "",
                    ["lines_html"] = "<div class=\"shop-cartlines\">" + Line("Hausmischung 250 g", M(1780), 2) + Line("Keramik-Tasse", M(1450), 1) + "</div>",
                    ["summary_html"] = $"<aside class=\"shop-cartsum\"><h2>{E(ctx.T("shop.cartSummary"))}</h2>{sum}<a class=\"shop-btn big\" href=\"#\">{E(ctx.T("shop.toCheckout"))}</a></aside>"
                };
            case "checkout":
                return new Dictionary<string, string>
                {
                    ["title"] = ctx.T("shop.checkout"), ["has_errors"] = "",
                    ["steps_html"] = $"<ol class=\"shop-steps shop-steps-mini\"><li class=\"done\">{E(ctx.T("shop.cart"))}</li><li class=\"done now\">{E(ctx.T("shop.checkout"))}</li><li>{E(ctx.T("shop.step.confirm"))}</li></ol>",
                    ["form_html"] = $"<div class=\"shop-checkout\"><div class=\"shop-checkout-main\">{Box(ctx.T("shop.contact"), "<p class=\"shop-muted\">…</p>")}{Box(ctx.T("shop.billingAddress"), "<p class=\"shop-muted\">…</p>")}{Box(ctx.T("shop.payment"), "<p class=\"shop-muted\">…</p>")}</div>"
                                    + $"<aside class=\"shop-checkout-side\"><div class=\"shop-card-box shop-summary\"><h2>{E(ctx.T("shop.yourOrder"))}</h2>{sum}<button class=\"shop-btn big\" type=\"button\">{E(ctx.T("shop.placeOrder"))}</button></div></aside></div>"
                };
            case "order":
                return new Dictionary<string, string>
                {
                    ["title"] = ctx.T("shop.orderNo", "R-1000"), ["number"] = "R-1000", ["status"] = "paid", ["status_label"] = ctx.T("shop.status.paid"), ["date"] = DateTime.Now.ToString("dd.MM.yyyy"), ["total"] = M(6470),
                    ["head_html"] = $"<div class=\"shop-ordhead\"><h1>{E(ctx.T("shop.orderNo", "R-1000"))}</h1><span class=\"shop-badge ok\">{E(ctx.T("shop.status.paid"))}</span></div>",
                    ["steps_html"] = $"<ol class=\"shop-steps\"><li class=\"done\">{E(ctx.T("shop.step.placed"))}</li><li class=\"done now\">{E(ctx.T("shop.step.paid"))}</li><li>{E(ctx.T("shop.step.shipped"))}</li><li>{E(ctx.T("shop.step.done"))}</li></ol>",
                    ["items_html"] = Box(ctx.T("shop.item"), Line("Hausmischung 250 g", M(1780), 2) + Line("Keramik-Tasse", M(1450), 1)),
                    ["side_html"] = $"<div class=\"shop-card-box\">{sum}</div>"
                };
            case "account":
                return new Dictionary<string, string>
                {
                    ["title"] = ctx.T("shop.acc.hello", "Erika Beispiel"), ["section"] = "home", ["name"] = "Erika Beispiel", ["email"] = "kundin@example.org", ["initials"] = "EB",
                    ["profile_html"] = "<div class=\"shop-accwho\"><span class=\"avatar\" aria-hidden=\"true\">EB</span><div><strong>Erika Beispiel</strong><span class=\"mail\">kundin@example.org</span></div></div>",
                    ["nav_html"] = ShopAccount.NavFor(ctx, "home"),
                    ["signout_html"] = $"<div class=\"shop-accout\"><button type=\"button\">{E(ctx.T("shop.acc.signOut"))}</button></div>",
                    ["content_html"] = $"<div class=\"shop-stats\"><div><span class=\"n\">3</span><span class=\"l\">{E(ctx.T("shop.acc.statOrders"))}</span></div><div><span class=\"n\">1</span><span class=\"l\">{E(ctx.T("shop.acc.statOpen"))}</span></div></div>"
                };
            default: return new Dictionary<string, string>();
        }
    }
}
