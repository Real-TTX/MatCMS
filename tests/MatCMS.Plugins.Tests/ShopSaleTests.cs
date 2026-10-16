using System.Net;
using MatCMS.Plugin.Shop;
using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The earlier ("statt") price: shown struck through where it helps, never charged, never shown where it would mislead.</summary>
public class ShopSaleTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopSaleTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private Task<long> Add(string title, long price, long compare, bool variants = false) =>
        _shop.WithContextAsync(async (_, store) =>
        {
            var id = await store.SaveProductAsync(new Product { Slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(title), Title = title, PriceCents = price, CompareCents = compare });
            if (variants) { await store.SaveVariantAsync(new Variant { ProductId = id, Name = "M" }); await store.SaveVariantAsync(new Variant { ProductId = id, Name = "L", PriceDeltaCents = 200 }); }
            return id;
        });

    private async Task<string> Block(string json)
    {
        using var scope = _app.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
        var block = registry.Blocks.First(b => b.Type == "shop_products");
        return await block.RenderAsync!(new PluginRequest { Services = scope.ServiceProvider, Registry = registry, Data = json, Path = "/seite", Query = new Dictionary<string, string>() }, default);
    }

    [Fact]
    public async Task A_product_on_sale_shows_the_struck_through_price_and_the_saving_on_its_page_the_catalogue_and_the_cart_charges_the_real_price()
    {
        await Add("Angebot Lampe", 800, 1000);
        var c = _app.Browser();
        var page = Decode(await c.GetStringAsync("/shop/angebot-lampe"));
        Assert.Contains("<s class=\"shop-was\"", page);
        Assert.Contains(">10,00 €</s>", page);
        Assert.Contains("8,00 €", page);
        Assert.Contains("−20 %", page);
        Assert.Contains("\"price\":\"8.00\"", page);                                           // search engines get the price that is charged

        var list = Decode(await c.GetStringAsync("/shop?q=angebot+lampe"));
        Assert.Contains(">10,00 €</s>", list);
        Assert.Contains("−20 %", list);

        await _shop.AddToCartAsync(c, "angebot-lampe");
        Assert.DoesNotContain("10,00 €", Decode(await c.GetStringAsync("/shop/cart")));         // the cart knows only the price
        Assert.Equal(800 + 490, (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c))))!.TotalCents);
    }

    [Fact]
    public async Task An_earlier_price_that_is_not_higher_is_ignored_and_variants_with_their_own_prices_show_none()
    {
        await Add("Kein Angebot", 1000, 1000);
        await Add("Billiger Vorher", 1000, 900);
        await Add("Angebot Varianten", 800, 1000, variants: true);
        var c = _app.Browser();
        foreach (var slug in new[] { "kein-angebot", "billiger-vorher", "angebot-varianten" })
        {
            var html = Decode(await c.GetStringAsync("/shop/" + slug));
            Assert.DoesNotContain("class=\"shop-was\"", html);
            Assert.DoesNotContain("shop-badge ok\">−", html);
        }
        Assert.False(new Product { PriceCents = 1000, CompareCents = 900 }.OnSale);
        Assert.False(new Product { PriceCents = 1000, CompareCents = 0 }.OnSale);
        Assert.Equal(20, new Product { PriceCents = 800, CompareCents = 1000 }.SavePercent);
        Assert.Equal(33, new Product { PriceCents = 667, CompareCents = 1000 }.SavePercent);   // rounded down: never promises more than it is
        Assert.Equal(0, new Product { PriceCents = 999, CompareCents = 1000 }.SavePercent);
    }

    [Fact]
    public async Task A_saving_below_one_percent_shows_the_old_price_but_no_badge()
    {
        await Add("Fast Gleich", 999, 1000);
        var html = Decode(await _app.Browser().GetStringAsync("/shop/fast-gleich"));
        Assert.Contains("class=\"shop-was\"", html);
        Assert.DoesNotContain("shop-badge ok\">−0", html);
    }

    [Fact]
    public async Task The_operator_sets_the_earlier_price_in_the_product_form_and_a_wrong_one_is_refused()
    {
        var admin = await _shop.AdminAsync();
        Dictionary<string, string> Form(string compare, string token) => new()
        {
            ["__RequestVerificationToken"] = token, ["action"] = "save", ["title"] = "Formular Angebot", ["slug"] = "", ["kind"] = "physical", ["price"] = "8,00", ["compare"] = compare, ["tax"] = "19",
            ["sku"] = "", ["category"] = "", ["tags"] = "", ["summary"] = "", ["description"] = "", ["image"] = "", ["gallery"] = "[]", ["variants"] = "", ["stock"] = "0", ["weight"] = "0", ["max"] = "0", ["sort"] = "0", ["active"] = "true"
        };
        var page = await admin.GetStringAsync("/admin/p/shop/products/0");
        var bad = await admin.PostAsync("/admin/p/shop/products/0", new FormUrlEncodedContent(Form("viel", AppFactory.Token(page)!)));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("Statt-Preis ist keine gültige Zahl", Decode(await bad.Content.ReadAsStringAsync()));

        var ok = await admin.PostAsync("/admin/p/shop/products/0", new FormUrlEncodedContent(Form("10,00", AppFactory.Token(page)!)));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var id = long.Parse(ok.Headers.Location!.OriginalString.Split('/').Last().Split('?')[0]);
        var saved = await _shop.ProductAsync(id);
        Assert.Equal(800, saved.PriceCents); Assert.Equal(1000, saved.CompareCents);
        Assert.Contains("−20 %", Decode(await admin.GetStringAsync("/admin/p/shop/products")));
        Assert.Contains("value=\"10,00\"", Decode(await admin.GetStringAsync($"/admin/p/shop/products/{id}")));
    }

    [Fact]
    public async Task The_carousel_layout_is_a_row_that_scrolls_and_snaps_with_two_buttons_and_the_cards_inside()
    {
        await Add("Karussell Eins", 500, 0); await Add("Karussell Zwei", 600, 0);
        var html = await Block("""{"source":"all","layout":"carousel","limit":"48"}""");
        Assert.Contains("class=\"shop-carousel\"", html);
        Assert.Contains("class=\"track\"", html);
        Assert.Contains("class=\"prev\"", html); Assert.Contains("class=\"next\"", html);
        Assert.Contains("Karussell Eins", html); Assert.Contains("Karussell Zwei", html);
        Assert.Contains("scroll-snap-type:x mandatory", html);                                 // the CSS comes with the block
        Assert.Contains("<script>(function(){var r=document.getElementById('shop-car-", html);
        // Two carousels on one page do not share a script target.
        var other = await Block("""{"source":"all","layout":"carousel"}""");
        var idOf = (string s) => System.Text.RegularExpressions.Regex.Match(s, "id=\"(shop-car-[a-f0-9]+)\"").Groups[1].Value;
        Assert.NotEqual(idOf(html), idOf(other));
        Assert.DoesNotContain("shop-carousel\"", await Block("""{"source":"all","layout":"grid"}"""));
    }

    [Fact]
    public async Task The_best_sellers_are_the_products_sold_most_in_paid_orders_and_an_empty_history_shows_the_highlights()
    {
        var featured = await _shop.WithContextAsync((_, s) => s.SaveProductAsync(new Product { Slug = "top-noch-nichts", Title = "Top Noch Nichts", PriceCents = 700, Featured = true }));
        Assert.True(featured > 0);
        // Before any sale the block does not stay empty.
        Assert.Contains("Top Noch Nichts", await Block("""{"source":"top","limit":"1"}"""));

        await _shop.AddProductAsync("Top Selten", 1000);
        await _shop.AddProductAsync("Top Oft", 1000);
        await _shop.AddProductAsync("Top Nie Bezahlt", 1000);
        var admin = await _shop.AdminAsync();
        async Task Sell(string slug, int qty, bool pay = true)
        {
            var c = _app.Browser();
            await _shop.AddToCartAsync(c, slug, qty);
            var order = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c))))!;
            if (pay) await _shop.AdminActionAsync(admin, order.Id, "paid");
        }
        await Sell("top-selten", 1); await Sell("top-oft", 2); await Sell("top-oft", 3); await Sell("top-nie-bezahlt", 9, pay: false);
        var html = await Block("""{"source":"top","limit":"2"}""");
        Assert.True(html.IndexOf("Top Oft", StringComparison.Ordinal) is var a and >= 0 && html.IndexOf("Top Selten", StringComparison.Ordinal) is var b && b > a);   // most pieces first
        Assert.DoesNotContain("Top Nie Bezahlt", html);                                         // an order nobody paid is no sale
        Assert.DoesNotContain("Top Noch Nichts", html);                                         // with sales, the highlights step aside
        Assert.DoesNotContain("Top Selten", await Block("""{"source":"top","limit":"1"}"""));    // the limit counts
    }

    [Fact]
    public async Task The_agent_may_set_an_earlier_price_and_a_page_block_can_show_only_the_products_on_sale()
    {
        var made = (System.Text.Json.Nodes.JsonObject)(await _shop.WithContextAsync((ctx, _) => ShopEvents.CreateProductAsync(ctx,
            System.Text.Json.Nodes.JsonNode.Parse("""{"title":"Agent Angebot","price_cents":1500,"compare_price":"20,00"}"""), default)))!;
        Assert.Equal("applied", made["status"]!.GetValue<string>());
        var product = await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("agent-angebot"));
        Assert.Equal(2000, product!.CompareCents);

        await Add("Block Sale Ein", 500, 800);
        await Add("Block Regulaer", 500, 0);
        var html = await Block("""{"source":"sale","limit":"48"}""");
        Assert.Contains("Block Sale Ein", html);
        Assert.Contains("Agent Angebot", html);
        Assert.DoesNotContain("Block Regulaer", html);
        Assert.DoesNotContain("Kein Angebot", html);                                           // an earlier price that is not higher is no sale
        Assert.DoesNotContain("Billiger Vorher", html);
    }
}
