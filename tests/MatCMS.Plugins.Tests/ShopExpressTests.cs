using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The cart's "pay directly with" buttons: the same checkout, the way to pay already chosen — and only for methods that are set up.</summary>
public class ShopExpressTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopExpressTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app, stripe: true, paypal: true);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Checked(string html) => Regex.Match(html, "<input type=\"radio\" name=\"pay\" value=\"([a-z]+)\" checked").Groups[1].Value;

    [Fact]
    public async Task The_cart_offers_paypal_and_card_and_the_checkout_opens_with_that_choice_made()
    {
        await _shop.AddProductAsync("Ex Ding", 4900);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "ex-ding");
        var cart = WebUtility.HtmlDecode(await c.GetStringAsync("/shop/cart"));
        Assert.Contains("class=\"shop-express\"", cart); Assert.Contains("/shop/checkout?pay=paypal", cart); Assert.Contains("/shop/checkout?pay=stripe", cart);

        Assert.Equal("paypal", Checked(await c.GetStringAsync("/shop/checkout?pay=paypal")));
        Assert.Equal("stripe", Checked(await c.GetStringAsync("/shop/checkout?pay=stripe")));
        Assert.Equal("bank", Checked(await c.GetStringAsync("/shop/checkout?pay=bank")));
        // A method that does not exist, or none, falls back to the first on offer — a radio is always chosen.
        var fallback = Checked(await c.GetStringAsync("/shop/checkout?pay=bitcoin"));
        Assert.NotEqual("", fallback); Assert.Equal(fallback, Checked(await c.GetStringAsync("/shop/checkout")));
        Assert.DoesNotContain("bitcoin", await c.GetStringAsync("/shop/checkout?pay=bitcoin"));
    }
}

public class ShopExpressOffTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopExpressOffTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Without_paypal_and_card_set_up_there_are_no_direct_buttons_and_no_reminder_box()
    {
        await _shop.AddProductAsync("Ex Aus", 1000);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "ex-aus");
        var cart = await c.GetStringAsync("/shop/cart");
        Assert.DoesNotContain("class=\"shop-express\"", cart);
        Assert.DoesNotContain("class=\"shop-remind\"", cart);                                            // reminders are off until the operator turns them on
        Assert.Contains("class=\"shop-est\"", cart);                                                       // the shipping estimate is always there when something ships
    }
}
