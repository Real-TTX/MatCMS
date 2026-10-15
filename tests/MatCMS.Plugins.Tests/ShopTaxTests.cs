using System.Net;
using MatCMS.Plugin.Shop;
using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>VAT by the country the goods go to: the rules as plain functions, so a wrong rate is found without placing an order.</summary>
public class ShopTaxUnitTests
{
    private static ShopConfig Oss(bool keepGross = true, string overrides = "") => new()
    {
        TaxMode = "oss", HomeCountry = "DE", DefaultTaxBp = 1900, ShippingTaxBp = 1900, OssKeepGross = keepGross, TaxRateOverrides = overrides, TaxRates = ShopTax.ParseOverrides(overrides)
    };

    [Theory]
    [InlineData(1900, "FR", 2000, "oss")]        // the standard rate of the country
    [InlineData(1900, "AT", 2000, "oss")]
    [InlineData(1900, "HU", 2700, "oss")]
    [InlineData(1900, "FI", 2550, "oss")]
    [InlineData(700, "FR", 550, "oss")]          // a product taxed below the standard rate at home gets the country's reduced rate
    [InlineData(700, "AT", 1000, "oss")]
    [InlineData(700, "DK", 2500, "oss")]         // a country without a reduced rate: the standard one
    [InlineData(0, "FR", 0, "oss")]              // not taxed at home, not taxed there
    [InlineData(1900, "DE", 1900, "")]           // at home nothing changes
    [InlineData(1900, "de", 1900, "")]
    [InlineData(1900, "", 1900, "")]             // no country known yet
    [InlineData(1900, "CH", 0, "export")]        // outside the EU: no VAT
    [InlineData(1900, "US", 0, "export")]
    [InlineData(1900, "GB", 0, "export")]
    public void The_rate_follows_the_country_of_delivery(int homeBp, string country, int expectedBp, string expectedNote)
    {
        var (bp, note, _) = ShopTax.For(Oss(), homeBp, country);
        Assert.Equal(expectedBp, bp);
        Assert.Equal(expectedNote, note);
    }

    [Fact]
    public void Without_oss_and_for_a_small_business_nothing_changes_anywhere()
    {
        var domestic = new ShopConfig { TaxMode = "domestic", DefaultTaxBp = 1900 };
        Assert.Equal((1900, "", true), ShopTax.For(domestic, 1900, "FR"));
        Assert.Equal((1900, "", true), ShopTax.For(domestic, 1900, "CH"));
        var small = Oss() with { SmallBusiness = true };
        Assert.False(small.Oss);
        Assert.Equal((1900, "", true), ShopTax.For(small, 1900, "FR"));
    }

    [Fact]
    public void Every_eu_member_state_has_a_rate_and_an_operator_override_wins()
    {
        Assert.Equal(27, ShopTax.Eu.Count);
        foreach (var c in new[] { "DE", "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE" })
            Assert.True(ShopTax.Eu.ContainsKey(c), c);
        Assert.All(ShopTax.Eu.Values, r => { Assert.InRange(r.StandardBp, 1500, 2800); Assert.InRange(r.ReducedBp, 0, 2000); });
        Assert.All(new[] { "CH", "GB", "NO", "US", "LI" }, c => Assert.False(ShopTax.IsEu(c)));

        var cfg = Oss(overrides: "FR 21 6");
        Assert.Equal(2100, ShopTax.For(cfg, 1900, "FR").Bp);
        Assert.Equal(600, ShopTax.For(cfg, 700, "FR").Bp);
        Assert.Equal(2000, ShopTax.For(cfg, 1900, "AT").Bp);                                 // the others keep the built-in rates
    }

    [Fact]
    public void The_overrides_are_read_forgivingly_and_what_cannot_be_read_is_reported()
    {
        var problems = new List<string>();
        var rates = ShopTax.ParseOverrides("FR 20 5,5\nat=21/11\n# a comment\n\n lu\t17  8 \nXX\nFR abc\nF 20\nDE 120\nIT 22 x", problems);
        Assert.Equal(new VatRate(2000, 550), rates["FR"]);
        Assert.Equal(new VatRate(2100, 1100), rates["AT"]);
        Assert.Equal(new VatRate(1700, 800), rates["LU"]);
        Assert.Equal(new[] { "XX", "FR abc", "F 20", "DE 120", "IT 22 x" }, problems);
        Assert.DoesNotContain("DE", rates.Keys);
        Assert.Equal(new VatRate(1900, 700), ShopTax.ParseOverrides("DE 19")["DE"]);          // the reduced rate defaults to the built-in one
        Assert.Empty(ShopTax.ParseOverrides(null));
    }

    [Theory]
    [InlineData(1190, 1900, 2000, true, 1190)]       // the shown price is kept
    [InlineData(1190, 1900, 2000, false, 1200)]      // the net price (10,00) is kept, the gross follows the rate
    [InlineData(1190, 1900, 0, false, 1000)]         // export: the net price
    [InlineData(1190, 1900, 1900, false, 1190)]
    [InlineData(0, 1900, 2000, false, 0)]
    public void A_price_is_moved_to_another_rate_keeping_either_the_gross_or_the_net(long gross, int from, int to, bool keepGross, long expected) =>
        Assert.Equal(expected, ShopTax.Convert(gross, from, to, keepGross));

    private static PricedCart Cart(params (long Cents, int Bp, bool Digital, int Qty)[] lines)
    {
        var list = lines.Select((l, i) => new PricedLine(new Product { Id = i + 1, Title = "P" + i, Kind = l.Digital ? ProductKind.Digital : ProductKind.Physical, TaxBp = l.Bp }, null, l.Qty, l.Cents, l.Cents * l.Qty, l.Bp, Money.TaxIncluded(l.Cents * l.Qty, l.Bp))).ToList();
        return new PricedCart(list, list.Sum(l => l.LineCents), list.Sum(l => l.TaxCents), list.Any(l => !l.Product.IsDigital), 0, Array.Empty<string>(), false);
    }

    [Fact]
    public void A_cart_is_taxed_for_its_delivery_country_with_digital_goods_following_the_billing_country()
    {
        var cart = Cart((1190, 1900, false, 2), (1190, 1900, true, 1));
        var fr = ShopTax.Apply(cart, Oss(), "FR", "DE");                                      // goods to France, billed in Germany
        Assert.Equal(2000, fr.Lines[0].TaxBp);                                                  // the physical line follows the delivery
        Assert.Equal(1900, fr.Lines[1].TaxBp);                                                  // the download follows the billing address: Germany
        Assert.Equal("oss", fr.TaxNote);
        Assert.Equal(1190, fr.Lines[0].UnitCents);                                              // gross kept
        Assert.Equal(Money.TaxIncluded(2380, 2000), fr.Lines[0].TaxCents);
        Assert.Equal(fr.Lines.Sum(l => l.TaxCents), fr.LineTaxCents);
        Assert.Equal(cart.SubtotalCents, fr.SubtotalCents);

        var net = ShopTax.Apply(cart, Oss(keepGross: false), "FR", "FR");
        Assert.Equal(1200, net.Lines[0].UnitCents);                                             // net 10,00 + 20 %
        Assert.Equal(2400, net.Lines[0].LineCents);
        Assert.Equal(1200, net.Lines[1].UnitCents);                                             // the download too: billed in France
        Assert.Equal(net.Lines.Sum(l => l.LineCents), net.SubtotalCents);

        var ch = ShopTax.Apply(cart, Oss(), "CH", "CH");
        Assert.Equal("export", ch.TaxNote);
        Assert.All(ch.Lines, l => { Assert.Equal(0, l.TaxBp); Assert.Equal(0, l.TaxCents); Assert.Equal(1000, l.UnitCents); });
        Assert.Equal(0, ch.LineTaxCents);
        Assert.Equal(0, ch.ShipTax!.Bp);
        Assert.Equal(840, ch.ShipTax.Price(1000));                                              // shipping goes net too: 10,00 → 8,40

        var home = ShopTax.Apply(cart, Oss(), "DE", "DE");
        Assert.Equal("", home.TaxNote);
        Assert.Equal(cart.Lines, home.Lines);
    }

    [Fact]
    public void Shipping_is_taxed_like_the_goods_of_the_country_and_a_digital_only_cart_has_no_shipping_to_tax()
    {
        var shipped = ShopTax.Apply(Cart((1000, 1900, false, 1)), Oss(), "AT", "AT");
        Assert.Equal(2000, shipped.ShipTax!.Bp);
        Assert.Equal(1900, shipped.ShipTax.FromBp);
        Assert.Equal(500, shipped.ShipTax.Price(500));                                          // gross kept
        var totals = ShopOrders.Compute(shipped, new ShippingOption(new ShippingRate(), 500), Oss());
        Assert.Equal(shipped.LineTaxCents + Money.TaxIncluded(500, 2000), totals.Tax);
        Assert.Equal(2000, totals.ShipTaxBp);

        var digital = ShopTax.Apply(Cart((1000, 1900, true, 1)), Oss(), "", "FR");
        Assert.False(digital.NeedsShipping);
        Assert.Equal(2000, digital.Lines[0].TaxBp);
    }

    [Fact]
    public void The_notes_on_an_invoice_say_what_happened()
    {
        Assert.Contains("OSS", ShopTax.NoteText("oss", false));
        Assert.Contains("Ausfuhr", ShopTax.NoteText("export", false));
        Assert.Contains("One-Stop-Shop", ShopTax.NoteText("oss", true));
        Assert.Contains("outside the EU", ShopTax.NoteText("export", true));
        Assert.Equal("", ShopTax.NoteText("", false));
    }
}

/// <summary>The shop with "VAT by country of delivery" switched on and the rates of France overridden.</summary>
public class ShopOssTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopOssTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }

    public async Task InitializeAsync()
    {
        _shop = await ShopApp.StartAsync(_app, new Dictionary<string, string> { [Keys.TaxMode] = "oss", [Keys.TaxRates] = "FR 21 6" });
        await _shop.WithContextAsync(async (_, store) =>
        {
            if (!(await store.RatesAsync()).Any(r => r.Name == "EU Test")) await store.SaveRateAsync(new ShippingRate { Name = "EU Test", Countries = "FR,AT,CH,DE", PriceCents = 500 });
        });
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private async Task<(Order Order, string Token, IReadOnlyList<OrderItem> Items)> OrderAsync(string title, long cents, string country, int taxBp = 1900, string kind = ProductKind.Physical,
        IDictionary<string, string>? extra = null)
    {
        await _shop.AddProductAsync(title, cents, kind, taxBp: taxBp);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, MatCMS.Pages.Admin.Pages.IndexModel.Slugify(title));
        var res = await _shop.CheckoutAsync(c, country: country, waiver: kind == ProductKind.Digital, extra: extra);
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(res)))!;
        return (order, ShopApp.TokenOf(res), await _shop.ItemsAsync(order.Id));
    }

    [Fact]
    public async Task A_delivery_to_another_eu_country_is_taxed_at_that_countries_rate_and_the_invoice_says_so()
    {
        var (order, token, items) = await OrderAsync("Oss Tasse", 1000, "FR");
        Assert.Equal("oss", order.TaxNote);
        Assert.Equal(2100, items.Single().TaxBp);                                              // France's rate from the override
        Assert.Equal(1000, items.Single().UnitCents);                                          // the price shown is what is paid
        Assert.Equal(2100, order.ShippingTaxBp);
        Assert.Equal(500, order.ShippingCents);
        Assert.Equal(1500, order.TotalCents);
        Assert.Equal(Money.TaxIncluded(1000, 2100) + Money.TaxIncluded(500, 2100), order.TaxCents);

        await _shop.AdminActionAsync(await _shop.AdminAsync(), order.Id, "paid");
        var invoice = Decode(await _app.Browser().GetStringAsync($"/shop/invoice/{order.Number}?t={token}"));
        Assert.Contains("21 %", invoice);
        Assert.DoesNotContain("19 %", invoice);
        Assert.Contains("(OSS)", invoice);
        Assert.Contains("Frankreich", invoice);
    }

    [Fact]
    public async Task A_product_taxed_below_the_standard_rate_gets_the_reduced_rate_of_the_country()
    {
        var (order, _, items) = await OrderAsync("Oss Buch", 1000, "FR", taxBp: 700);
        Assert.Equal(600, items.Single().TaxBp);                                               // the override's reduced rate
        Assert.Equal(Money.TaxIncluded(1000, 600), items.Single().TaxCents);
        Assert.Equal(2100, order.ShippingTaxBp);                                               // shipping is a standard-rate item
    }

    [Fact]
    public async Task A_delivery_outside_the_eu_carries_no_vat_and_is_charged_the_net_price()
    {
        var (order, token, items) = await OrderAsync("Oss Export", 1000, "CH");
        Assert.Equal("export", order.TaxNote);
        Assert.Equal(840, items.Single().UnitCents);                                           // 10,00 gross at 19 % is 8,40 net
        Assert.Equal(0, items.Single().TaxBp);
        Assert.Equal(0, order.TaxCents);
        Assert.Equal(420, order.ShippingCents);                                                // shipping too: 5,00 → 4,20
        Assert.Equal(1260, order.TotalCents);
        Assert.Equal(0, order.ShippingTaxBp);

        await _shop.AdminActionAsync(await _shop.AdminAsync(), order.Id, "paid");
        var invoice = Decode(await _app.Browser().GetStringAsync($"/shop/invoice/{order.Number}?t={token}"));
        Assert.Contains("Ausfuhr", invoice);
        Assert.DoesNotContain("Netto 19 %", invoice);
    }

    [Fact]
    public async Task At_home_nothing_changes_and_a_download_follows_the_billing_country()
    {
        var (home, _, homeItems) = await OrderAsync("Oss Heimat", 1000, "DE");
        Assert.Equal("", home.TaxNote);
        Assert.Equal(1900, homeItems.Single().TaxBp);
        Assert.Equal(Money.TaxIncluded(1000, 1900) + Money.TaxIncluded(home.ShippingCents, 1900), home.TaxCents);          // the cheaper German rate is the one picked

        var (digital, _, items) = await OrderAsync("Oss Download", 1000, "FR", kind: ProductKind.Digital);
        Assert.Equal("oss", digital.TaxNote);
        Assert.Equal(2100, items.Single().TaxBp);
        Assert.Equal(0, digital.ShippingCents);
    }

    [Fact]
    public async Task Goods_delivered_to_another_address_follow_that_address_not_the_billing_address()
    {
        var other = new Dictionary<string, string> { ["ship_other"] = "1", ["s_name"] = "Hans Muster", ["s_line1"] = "Bahnhofstrasse 1", ["s_zip"] = "3000", ["s_city"] = "Bern", ["s_country"] = "CH" };
        var (order, _, items) = await OrderAsync("Oss Anderswo", 1000, "DE", extra: other);
        Assert.Equal("export", order.TaxNote);                                                 // billed in Germany, delivered to Switzerland
        Assert.Equal(0, items.Single().TaxBp);
    }

    [Fact]
    public async Task The_checkout_page_shows_the_new_tax_when_the_country_is_changed()
    {
        await _shop.AddProductAsync("Oss Anzeige", 1000);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "oss-anzeige");
        var page = await c.GetStringAsync("/shop/checkout");
        var form = ShopApp.CheckoutForm(AppFactory.Token(page)!, country: "CH");
        form["action"] = "recalc";
        var res = await c.PostAsync("/shop/checkout", new FormUrlEncodedContent(form));
        var html = Decode(await res.Content.ReadAsStringAsync());
        Assert.Contains("Steuerfreie Ausfuhr", html);
        Assert.Contains("12,60 €", html);                                                       // 8,40 + 4,20

        form["b_country"] = "FR";
        var fr = Decode(await (await c.PostAsync("/shop/checkout", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync());
        Assert.Contains("15,00 €", fr);
        Assert.Contains($"Enthaltene MwSt.: {Money.Format(Money.TaxIncluded(1000, 2100) + Money.TaxIncluded(500, 2100))}", fr);
    }

    [Fact]
    public async Task The_settings_the_operator_edits_are_read_as_written()
    {
        using var scope = _app.Services.CreateScope();
        var cfg = await _shop.WithContextAsync((ctx, _) => Task.FromResult(ShopConfig.From(ctx.Settings)));
        Assert.True(cfg.Oss);
        Assert.Equal("DE", cfg.HomeCountry);
        Assert.True(cfg.OssKeepGross);                                                          // the default: the price shown stays
        Assert.Equal(new VatRate(2100, 600), cfg.TaxRates["FR"]);
        Assert.Equal("GS-", cfg.CreditPrefix);
    }
}
