using System.Globalization;
using System.Net;
using MatCMS.Plugin.Shop;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>"Nur Abholung": a product that is never sent restricts the cart to the shipping rates marked as pickup.</summary>
public class ShopPickupRuleTests
{
    private static readonly ShippingRate Parcel = new() { Id = 1, Name = "Paket", Countries = "DE", PriceCents = 490, MaxWeightGrams = 1000 };
    private static readonly ShippingRate Collect = new() { Id = 2, Name = "Abholung", Countries = "*", PriceCents = 0, Pickup = true };

    private static PricedCart Cart(bool pickupOnly, int weight)
    {
        var p = new Product { Id = 1, Title = "Box", PickupOnly = pickupOnly, WeightGrams = weight };
        var line = new PricedLine(p, null, 1, 10000, 10000, 1900, Money.TaxIncluded(10000, 1900));
        return new PricedCart(new[] { line }, 10000, line.TaxCents, true, weight, Array.Empty<string>(), false);
    }

    [Fact]
    public void A_normal_cart_gets_every_rate_and_delivery_is_preselected_even_when_collection_costs_nothing()
    {
        var options = ShopOrders.ShippingOptions(new[] { Parcel, Collect }, "DE", Cart(false, 500));
        Assert.Equal(2, options.Count);
        Assert.Equal("Paket", ShopOrders.Pick(options, 0)!.Rate.Name);
        Assert.Equal("Abholung", ShopOrders.Pick(options, 2)!.Rate.Name);
    }

    [Fact]
    public void A_cart_with_a_pickup_only_product_gets_the_pickup_rates_alone_and_no_weight_limit_applies_to_them()
    {
        var options = ShopOrders.ShippingOptions(new[] { Parcel, Collect with { MaxWeightGrams = 100 } }, "DE", Cart(true, 50000));
        Assert.Equal("Abholung", Assert.Single(options).Rate.Name);
        Assert.Equal("Abholung", ShopOrders.Pick(options, 1)!.Rate.Name);              // a posted delivery rate is not available to it
        Assert.Empty(ShopOrders.ShippingOptions(new[] { Parcel }, "DE", Cart(true, 500)));
    }

    [Fact]
    public void Goods_collected_at_the_shop_are_taxed_in_the_shops_country()
    {
        var cfg = new ShopConfig { HomeCountry = "DE" };
        Assert.Equal("DE", ShopOrders.TaxCountry(cfg, "FR", new ShippingOption(Collect, 0)));
        Assert.Equal("FR", ShopOrders.TaxCountry(cfg, "FR", new ShippingOption(Parcel, 490)));
        Assert.Equal("FR", ShopOrders.TaxCountry(cfg, "FR", null));
    }
}

/// <summary>The same through the real shop: product page, cart, checkout, the stored order.</summary>
public class ShopPickupTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopPickupTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }

    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private async Task AddPickupOnlyAsync(string title, long cents)
    {
        var id = await _shop.AddProductAsync(title, cents);
        await _shop.WithContextAsync(async (_, store) => await store.SaveProductAsync((await store.ProductAsync(id))! with { PickupOnly = true }));
    }

    [Fact]
    public async Task A_pickup_only_product_says_so_and_can_only_be_ordered_for_collection()
    {
        await AddPickupOnlyAsync("Grosse Box", 250000);
        await _shop.AddProductAsync("Kleine Box", 9900);

        var page = Decode(await _app.Browser().GetStringAsync("/shop/grosse-box"));
        Assert.Contains("Nur Abholung", page);
        Assert.Contains("Sie holen ihn bei uns ab", page);
        Assert.Contains("Nur Abholung", Decode(await _app.Browser().GetStringAsync("/shop")));    // the card's badge

        // No pickup rate yet: the order is refused with the reason, not with "we do not ship to Germany".
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "grosse-box", 1);
        var refused = await _shop.CheckoutAsync(c);
        Assert.Equal((HttpStatusCode)422, refused.StatusCode);
        Assert.Contains("keine Abholung", Decode(await refused.Content.ReadAsStringAsync()));
        var notes = await _shop.WithContextAsync(async (ctx, _) => (await ShopAdmin.AttentionAsync(ctx, default)).ToList());
        Assert.Contains(notes, n => n.Title == "Keine Abholung eingerichtet");

        var pickup = await _shop.WithContextAsync((_, store) => store.SaveRateAsync(new ShippingRate { Name = "Abholung in Pforzheim", Countries = "*", PriceCents = 0, Pickup = true }));
        var withRate = Decode(await _app.Browser().GetStringAsync("/shop/grosse-box"));
        Assert.Contains("Abholung in Pforzheim", withRate);                                       // the trust row names where it is collected …
        Assert.DoesNotContain("Kostenloser Versand", withRate);                                    // … and promises no delivery
        var form = Decode(await c.GetStringAsync("/shop/checkout"));
        Assert.Contains("Abholung in Pforzheim", form);
        Assert.DoesNotContain("Standardversand Deutschland", form);

        // Even a posted delivery rate cannot send it.
        var standard = await _shop.WithContextAsync(async (_, store) => (await store.RatesAsync()).First(r => !r.Pickup).Id);
        var placed = await _shop.CheckoutAsync(c, extra: new Dictionary<string, string> { ["rate"] = standard.ToString(CultureInfo.InvariantCulture) });
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(placed)))!;
        Assert.Equal("Abholung in Pforzheim", order.ShippingMethod);
        Assert.Equal(0, order.ShippingCents);
        Assert.Equal(250000, order.TotalCents);

        // A cart without such a product is offered both, with delivery preselected.
        var d = _app.Browser();
        await _shop.AddToCartAsync(d, "kleine-box", 1);
        var normal = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(d))))!;
        Assert.Equal("Standardversand Deutschland", normal.ShippingMethod);
        await _shop.WithContextAsync((_, store) => store.DeleteRateAsync(pickup));
    }
}

/// <summary>With VAT by country of delivery: collected goods are supplied at the shop, so they keep the home rate.</summary>
public class ShopPickupOssTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopPickupOssTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }

    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app, new Dictionary<string, string> { [Keys.TaxMode] = "oss", [Keys.TaxRates] = "FR 21 6" });
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_french_customer_collecting_pays_german_vat_and_one_who_has_it_delivered_pays_french_vat()
    {
        await _shop.AddProductAsync("Oss Abhol Tasse", 1000);
        var eu = await _shop.AddRateAsync("EU Paket", "FR,DE", 500);
        var pickup = await _shop.WithContextAsync((_, store) => store.SaveRateAsync(new ShippingRate { Name = "Abholung", Countries = "*", PriceCents = 0, Pickup = true }));

        async Task<(Order, IReadOnlyList<OrderItem>)> Buy(long rate)
        {
            var c = _app.Browser();
            await _shop.AddToCartAsync(c, "oss-abhol-tasse");
            var o = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c, country: "FR",
                extra: new Dictionary<string, string> { ["rate"] = rate.ToString(CultureInfo.InvariantCulture) }))))!;
            return (o, await _shop.ItemsAsync(o.Id));
        }

        var (collected, cItems) = await Buy(pickup);
        Assert.Equal("Abholung", collected.ShippingMethod);
        Assert.Equal(1900, cItems.Single().TaxBp);
        Assert.Equal("", collected.TaxNote);
        Assert.Equal(1000, collected.TotalCents);

        var (sent, sItems) = await Buy(eu);
        Assert.Equal(2100, sItems.Single().TaxBp);
        Assert.Equal("oss", sent.TaxNote);
    }
}
