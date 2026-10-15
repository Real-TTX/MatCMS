using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Plugin.Shop;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Coupon codes: what a customer sees, what the order and the invoice say, every rule that can refuse one — and that the money adds up to the cent.</summary>
public class ShopCouponTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopCouponTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); ShopCoupons.ResetAttempts(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private async Task<HttpClient> CartWithAsync(string slug, int qty = 1)
    {
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, slug, qty);
        return c;
    }

    // ---- what a coupon does ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_percentage_coupon_reduces_the_goods_shows_in_cart_checkout_order_and_invoice_and_the_pdf_carries_it_too()
    {
        await _shop.AddProductAsync("Gutschein Lampe", 2000);
        await _shop.AddCouponAsync("SOMMER10", value: 1000);
        var c = await CartWithAsync("gutschein-lampe", 2);

        var cart = await _shop.ApplyCouponAsync(c, "sommer10");                              // typed in lower case
        Assert.Contains("Der Gutschein SOMMER10 wurde angewendet", cart);
        Assert.Contains("−4,00 €", cart);                                                   // 10 % of 40,00
        Assert.Contains("36,00 €", cart);                                                   // the goods after it
        Assert.Contains("Gutschein SOMMER10", Decode(await c.GetStringAsync("/shop/cart"))); // it stays on the cart

        var checkout = Decode(await c.GetStringAsync("/shop/checkout"));
        Assert.Contains("−4,00 €", checkout);
        Assert.Contains("40,90 €", checkout);                                               // 36,00 + 4,90 shipping

        var res = await _shop.CheckoutAsync(c);
        var number = ShopApp.NumberOf(res); var token = ShopApp.TokenOf(res);
        var order = (await _shop.OrderAsync(number))!;
        Assert.Equal(4000, order.SubtotalCents);
        Assert.Equal(400, order.DiscountCents);
        Assert.Equal("SOMMER10", order.CouponCode);
        Assert.Equal(4090, order.TotalCents);
        Assert.Equal(Money.TaxIncluded(3600, 1900) + Money.TaxIncluded(490, 1900), order.TaxCents);   // the tax of what is paid, not of the list price
        Assert.Equal(400, (await _shop.ItemsAsync(order.Id)).Sum(i => i.DiscountCents));
        Assert.Equal(1, (await _shop.CouponAsync("SOMMER10")).Used);
        Assert.Contains("SOMMER10", string.Join("|", (await _shop.EventsAsync(order.Id)).Select(e => e.Text)));
        Assert.Contains("Ihr Warenkorb ist leer", Decode(await c.GetStringAsync("/shop/cart")));

        Assert.Contains("−4,00 €", Decode(await _app.Browser().GetStringAsync($"/shop/order/{number}?t={token}")));
        await _shop.AdminActionAsync(await _shop.AdminAsync(), order.Id, "paid");
        var visitor = _app.Browser();
        var invoice = Decode(await visitor.GetStringAsync($"/shop/invoice/{number}?t={token}"));
        Assert.Contains("Gutschein SOMMER10", invoice);
        Assert.Contains("−4,00 €", invoice);
        Assert.Contains("40,90 €", invoice);

        var pdf = await visitor.GetAsync($"/shop/invoice/{number}?t={token}&pdf=1");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        var text = Encoding.Latin1.GetString(await pdf.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("(Gutschein SOMMER10)", text);
    }

    [Fact]
    public async Task A_fixed_coupon_is_spread_over_the_lines_so_the_tax_of_every_rate_adds_up_to_the_total()
    {
        await _shop.AddProductAsync("Satz Neunzehn", 1000, taxBp: 1900);
        await _shop.AddProductAsync("Satz Sieben", 1000, taxBp: 700);
        await _shop.AddCouponAsync("FUENF", CouponKind.Fixed, 500);
        var c = await CartWithAsync("satz-neunzehn");
        await _shop.AddToCartAsync(c, "satz-sieben");
        await _shop.ApplyCouponAsync(c, "FUENF");

        var order = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c))))!;
        var items = await _shop.ItemsAsync(order.Id);
        Assert.Equal(new long[] { 250, 250 }, items.Select(i => i.DiscountCents).ToArray());
        Assert.Equal(Money.TaxIncluded(750, 1900), items.Single(i => i.TaxBp == 1900).TaxCents);
        Assert.Equal(Money.TaxIncluded(750, 700), items.Single(i => i.TaxBp == 700).TaxCents);
        Assert.Equal(2000 - 500 + 490, order.TotalCents);
        // The invoice's groups per rate are exactly what was paid.
        var groups = ShopInvoice.Groups(order, items, ShopConfig.From(new EmptySettings()));
        Assert.Equal(order.TotalCents, groups.Sum(g => g.GrossCents));
        Assert.Equal(order.TaxCents, groups.Sum(g => g.TaxCents));
    }

    [Fact]
    public async Task A_fixed_coupon_never_takes_more_than_the_goods_and_never_the_shipping()
    {
        await _shop.AddProductAsync("Klein Stift", 2000);
        await _shop.AddCouponAsync("RIESIG", CouponKind.Fixed, 10000);
        var c = await CartWithAsync("klein-stift");
        await _shop.ApplyCouponAsync(c, "RIESIG");
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c))))!;
        Assert.Equal(2000, order.DiscountCents);
        Assert.Equal(490, order.TotalCents);                                                // only the shipping is left to pay
    }

    [Fact]
    public async Task A_free_shipping_coupon_takes_the_shipping_off_and_nothing_else()
    {
        await _shop.AddProductAsync("Versand Heft", 1500);
        await _shop.AddCouponAsync("PORTOFREI", CouponKind.Shipping, 0);
        var c = await CartWithAsync("versand-heft");
        Assert.Contains("Versand kostenlos", await _shop.ApplyCouponAsync(c, "PORTOFREI"));
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c))))!;
        Assert.Equal(0, order.ShippingCents);
        Assert.Equal(0, order.DiscountCents);
        Assert.Equal(1500, order.TotalCents);
        Assert.Equal("PORTOFREI", order.CouponCode);
        Assert.False(string.IsNullOrEmpty(order.ShippingMethod));                           // the method is still named
    }

    [Fact]
    public async Task A_coupon_for_one_category_only_reduces_the_goods_of_that_category()
    {
        await _shop.WithContextAsync(async (_, s) => { if (await s.CategoryBySlugAsync("gutschein-kat") is null) await s.AddCategoryAsync("gutschein-kat", "Gutschein Kat", 0); });
        await _shop.AddProductAsync("Kat Eins", 1000, category: "gutschein-kat");
        await _shop.AddProductAsync("Kat Ohne", 1000);
        await _shop.AddCouponAsync("NURKAT", value: 5000, change: x => x with { Category = "gutschein-kat" });   // 50 % of that category
        var c = await CartWithAsync("kat-eins");
        await _shop.AddToCartAsync(c, "kat-ohne");
        await _shop.ApplyCouponAsync(c, "NURKAT");
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c))))!;
        Assert.Equal(500, order.DiscountCents);

        var other = await CartWithAsync("kat-ohne");
        Assert.Contains("keinen Artikel", await _shop.ApplyCouponAsync(other, "NURKAT"));
    }

    // ---- what refuses one -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_coupon_that_does_not_apply_says_why_and_is_not_kept()
    {
        await _shop.AddProductAsync("Regel Becher", 2000);
        await _shop.AddCouponAsync("MIN50", value: 1000, change: c => c with { MinOrderCents = 5000 });
        await _shop.AddCouponAsync("ABGELAUFEN", change: c => c with { ExpiresUtc = DateTime.UtcNow.AddDays(-1) });
        await _shop.AddCouponAsync("SPAETER", change: c => c with { StartsUtc = DateTime.UtcNow.AddDays(3) });
        await _shop.AddCouponAsync("AUSGESCHALTET", change: c => c with { Active = false });
        var c = await CartWithAsync("regel-becher");

        Assert.Contains("Warenwert von 50,00 €", await _shop.ApplyCouponAsync(c, "MIN50"));
        Assert.Contains("abgelaufen", await _shop.ApplyCouponAsync(c, "ABGELAUFEN"));
        Assert.Contains("gilt erst ab dem", await _shop.ApplyCouponAsync(c, "SPAETER"));
        Assert.Contains("gibt es nicht", await _shop.ApplyCouponAsync(c, "AUSGESCHALTET"));        // a switched-off code is "unknown": nobody learns which codes exist
        Assert.Contains("gibt es nicht", await _shop.ApplyCouponAsync(c, "NIEMALS"));
        Assert.Contains("Bitte geben Sie einen Gutschein-Code ein", await _shop.ApplyCouponAsync(c, "  "));
        var cart = Decode(await c.GetStringAsync("/shop/cart"));
        Assert.DoesNotContain("shop-badge ok\">Gutschein", cart);                            // none of them stuck
        Assert.DoesNotContain("−", cart);
    }

    [Fact]
    public async Task A_coupon_with_a_limit_can_be_used_that_often_and_a_buyer_who_loses_the_race_is_told()
    {
        await _shop.AddProductAsync("Rennen Gutschein", 1000);
        await _shop.AddCouponAsync("EINMALIG", change: c => c with { MaxUses = 1 });
        var a = await CartWithAsync("rennen-gutschein"); var b = await CartWithAsync("rennen-gutschein");
        await _shop.ApplyCouponAsync(a, "EINMALIG"); await _shop.ApplyCouponAsync(b, "EINMALIG");   // both may hold it: it is used up only when an order is placed
        MatCMS.Services.Plugins.PluginPostLimiter.Reset();

        var results = await Task.WhenAll(new[] { a, b }.Select((x, i) => Task.Run(() => _shop.CheckoutAsync(x, email: $"rennen{i}@example.org"))));
        Assert.DoesNotContain(results, r => (int)r.StatusCode >= 500);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Redirect);
        var lost = results.Single(r => r.StatusCode != HttpStatusCode.Redirect);
        Assert.Equal((HttpStatusCode)422, lost.StatusCode);
        Assert.Contains("zu oft eingelöst", Decode(await lost.Content.ReadAsStringAsync()));
        Assert.Equal(1, (await _shop.CouponAsync("EINMALIG")).Used);
        Assert.Equal(1, await _shop.WithContextAsync((_, s) => s.OrdersWithCouponAsync("EINMALIG")));

        var third = await CartWithAsync("rennen-gutschein");
        Assert.Contains("zu oft eingelöst", await _shop.ApplyCouponAsync(third, "EINMALIG"));
    }

    [Fact]
    public async Task A_coupon_once_per_customer_is_refused_for_the_same_address_at_checkout_but_not_for_another()
    {
        await _shop.AddProductAsync("Einmal Kunde", 1000);
        await _shop.AddCouponAsync("PROKUNDE", change: c => c with { OncePerCustomer = true });
        var first = await CartWithAsync("einmal-kunde");
        await _shop.ApplyCouponAsync(first, "PROKUNDE");
        Assert.Equal(HttpStatusCode.Redirect, (await _shop.CheckoutAsync(first, email: "treu@example.org")).StatusCode);

        var second = await CartWithAsync("einmal-kunde");
        Assert.DoesNotContain("schon eingelöst", await _shop.ApplyCouponAsync(second, "PROKUNDE"));   // the address is not known at the cart
        var again = await _shop.CheckoutAsync(second, email: "Treu@Example.org");                      // …but it is when the order is placed
        Assert.Equal((HttpStatusCode)422, again.StatusCode);
        Assert.Contains("schon eingelöst", Decode(await again.Content.ReadAsStringAsync()));
        Assert.Equal(1, await _shop.WithContextAsync((_, s) => s.OrdersWithCouponAsync("PROKUNDE")));

        var other = await _shop.CheckoutAsync(second, email: "anders@example.org");
        Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
    }

    [Fact]
    public async Task A_coupon_that_stops_applying_after_it_was_added_is_never_charged_silently()
    {
        await _shop.AddProductAsync("Sinnflut Vase", 3000);
        await _shop.AddCouponAsync("VORBEI", value: 2000);
        var c = await CartWithAsync("sinnflut-vase");
        await _shop.ApplyCouponAsync(c, "VORBEI");
        var token = AppFactory.Token(await c.GetStringAsync("/shop/cart"))!;
        await _shop.WithContextAsync(async (_, s) => await s.SaveCouponAsync((await s.CouponByCodeAsync("VORBEI"))! with { ExpiresUtc = DateTime.UtcNow.AddMinutes(-5) }));

        // A direct post: refused with the reason, no order.
        var res = await c.PostAsync("/shop/checkout", new FormUrlEncodedContent(ShopApp.CheckoutForm(token, email: "vorbei@example.org")));
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        var html = Decode(await res.Content.ReadAsStringAsync());
        Assert.Contains("gilt nicht mehr", html);
        Assert.Contains("abgelaufen", html);
        Assert.Equal(0, (await _shop.WithContextAsync((_, s) => s.OrdersAsync(search: "vorbei@example.org"))).Total);
        // The buyer then sees the full price and decides again.
        Assert.Contains("34,90 €", Decode(await c.GetStringAsync("/shop/checkout")));         // 30,00 + 4,90, no discount
    }

    [Fact]
    public async Task Guessing_codes_is_stopped_after_ten_wrong_tries_even_for_a_right_one_and_a_right_code_never_counts_against_you()
    {
        await _shop.AddProductAsync("Raten Lampe", 2000);
        await _shop.AddCouponAsync("ECHT", value: 500);
        var c = await CartWithAsync("raten-lampe");
        for (var i = 0; i < 4; i++) await _shop.ApplyCouponAsync(c, "FALSCH" + i);
        Assert.Contains("wurde angewendet", await _shop.ApplyCouponAsync(c, "ECHT"));            // a hit is no failure
        for (var i = 4; i < 10; i++) Assert.Contains("gibt es nicht", await _shop.ApplyCouponAsync(c, "FALSCH" + i));
        var blocked = await _shop.ApplyCouponAsync(c, "ECHT");
        Assert.Contains("Zu viele ungültige Versuche", blocked);
        Assert.DoesNotContain("wurde angewendet", blocked);
        ShopCoupons.ResetAttempts();                                                         // the ten minutes pass
        Assert.Contains("wurde angewendet", await _shop.ApplyCouponAsync(c, "ECHT"));
    }

    [Fact]
    public async Task A_coupon_that_makes_a_download_free_completes_the_order_at_once_with_an_invoice_for_nothing()
    {
        await _shop.AddProductAsync("Gratis Heft", 1500, ProductKind.Digital);
        await _shop.AddCouponAsync("ALLES", value: 10000);                                   // 100 %
        var c = await CartWithAsync("gratis-heft");
        await _shop.ApplyCouponAsync(c, "ALLES");
        var res = await _shop.CheckoutAsync(c, waiver: true);
        var number = ShopApp.NumberOf(res); var token = ShopApp.TokenOf(res);
        var order = (await _shop.OrderAsync(number))!;
        Assert.Equal(0, order.TotalCents);
        Assert.Equal(1500, order.DiscountCents);
        Assert.Equal("free", order.PaymentProvider);
        Assert.Equal(OrderStatus.Shipped, order.Status);                                      // paid and delivered the moment it was placed
        Assert.Matches(@"^RE-\d{5}$", order.InvoiceNumber);
        var visitor = _app.Browser();
        Assert.Contains("0,00 €", Decode(await visitor.GetStringAsync($"/shop/invoice/{number}?t={token}")));
        var pdf = await visitor.GetAsync($"/shop/invoice/{number}?t={token}&pdf=1");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.StartsWith("%PDF-1.4", Encoding.Latin1.GetString(await pdf.Content.ReadAsByteArrayAsync()));
    }

    // ---- cancelling gives it back ------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_an_unpaid_order_gives_the_coupons_use_back_and_paying_it_keeps_it_used()
    {
        await _shop.AddProductAsync("Zurueck Lampe", 1000);
        await _shop.AddCouponAsync("ZURUECK", change: c => c with { MaxUses = 5 });
        var admin = await _shop.AdminAsync();

        var a = await CartWithAsync("zurueck-lampe"); await _shop.ApplyCouponAsync(a, "ZURUECK");
        var orderA = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(a))))!;
        Assert.Equal(1, (await _shop.CouponAsync("ZURUECK")).Used);
        await _shop.AdminActionAsync(admin, orderA.Id, "cancel");
        Assert.Equal(0, (await _shop.CouponAsync("ZURUECK")).Used);

        var b = await CartWithAsync("zurueck-lampe"); await _shop.ApplyCouponAsync(b, "ZURUECK");
        var orderB = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(b))))!;
        await _shop.AdminActionAsync(admin, orderB.Id, "paid");
        Assert.Equal(1, (await _shop.CouponAsync("ZURUECK")).Used);
    }

    // ---- the back office ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_operator_makes_changes_and_deletes_coupons_in_the_back_office_and_the_form_refuses_what_is_wrong()
    {
        var admin = await _shop.AdminAsync();
        async Task<HttpResponseMessage> Post(string url, Dictionary<string, string> form)
        {
            var page = await admin.GetStringAsync(url);
            form["__RequestVerificationToken"] = AppFactory.Token(page)!;
            return await admin.PostAsync(url, new FormUrlEncodedContent(form));
        }
        Dictionary<string, string> Form(string code, string kind, string value, params (string K, string V)[] more)
        {
            var f = new Dictionary<string, string> { ["action"] = "save", ["code"] = code, ["kind"] = kind, ["value"] = value, ["min"] = "", ["category"] = "", ["max_uses"] = "", ["starts"] = "", ["expires"] = "", ["note"] = "", ["active"] = "true" };
            foreach (var (k, v) in more) f[k] = v;
            return f;
        }

        var saved = await Post("/admin/p/shop/coupons/0", Form("admin15", "percent", "15", ("min", "20,00"), ("max_uses", "7"), ("expires", "2099-12-31"), ("per_customer", "true")));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var c = await _shop.CouponAsync("ADMIN15");
        Assert.Equal(1500, c.Value); Assert.Equal(2000, c.MinOrderCents); Assert.Equal(7, c.MaxUses); Assert.True(c.OncePerCustomer); Assert.True(c.Active);
        Assert.True(c.ExpiresUtc > DateTime.UtcNow.AddYears(70));                                 // "until 31.12.2099" runs through that day

        Assert.Contains("ADMIN15", Decode(await admin.GetStringAsync("/admin/p/shop/coupons")));
        Assert.Contains("15 %", Decode(await admin.GetStringAsync("/admin/p/shop/coupons")));

        var bad = await Post("/admin/p/shop/coupons/0", Form("zuviel", "percent", "150"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("zwischen 0 und 100", Decode(await bad.Content.ReadAsStringAsync()));
        Assert.Contains("Festbetrag", Decode(await (await Post("/admin/p/shop/coupons/0", Form("nix", "fixed", "abc"))).Content.ReadAsStringAsync()));
        Assert.Contains("gibt es schon", Decode(await (await Post("/admin/p/shop/coupons/0", Form("Admin15", "percent", "5"))).Content.ReadAsStringAsync()));   // codes are compared in upper case
        Assert.Contains("liegt vor", Decode(await (await Post("/admin/p/shop/coupons/0", Form("rueckwaerts", "percent", "5", ("starts", "2030-01-02"), ("expires", "2030-01-01")))).Content.ReadAsStringAsync()));

        // No code typed: one is made.
        var made = await Post("/admin/p/shop/coupons/0", Form("", "shipping", ""));
        Assert.Equal(HttpStatusCode.Redirect, made.StatusCode);
        var madeId = long.Parse(made.Headers.Location!.OriginalString.Split('/').Last().Split('?')[0]);
        var generated = await _shop.WithContextAsync((_, s) => s.CouponAsync(madeId));
        Assert.Matches("^[A-Z2-9]{8}$", generated!.Code);

        // Change, then delete.
        Assert.Equal(HttpStatusCode.Redirect, (await Post($"/admin/p/shop/coupons/{c.Id}", Form("ADMIN15", "fixed", "3,50", ("active", "")))).StatusCode);
        var changed = await _shop.CouponAsync("ADMIN15");
        Assert.Equal(CouponKind.Fixed, changed.Kind); Assert.Equal(350, changed.Value);
        var list = await admin.GetStringAsync("/admin/p/shop/coupons");
        var del = await admin.PostAsync("/admin/p/shop/coupons", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(list)!, ["action"] = "delete", ["id"] = c.Id.ToString() }));
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);
        Assert.Null(await _shop.WithContextAsync((_, s) => s.CouponByCodeAsync("ADMIN15")));
    }

    [Fact]
    public async Task The_agent_operation_makes_a_coupon_skips_an_existing_code_and_refuses_what_makes_no_sense()
    {
        async Task<JsonObject> Run(string json) => (JsonObject)(await _shop.WithContextAsync((ctx, _) => ShopEvents.CreateCouponAsync(ctx, JsonNode.Parse(json), default)))!;
        var ok = await Run("""{"code":"agent20","kind":"percent","percent":20,"min_order_cents":3000,"max_uses":50,"expires":"2099-01-31","note":"Newsletter"}""");
        Assert.Equal("applied", ok["status"]!.GetValue<string>());
        var c = await _shop.CouponAsync("AGENT20");
        Assert.Equal(2000, c.Value); Assert.Equal(3000, c.MinOrderCents); Assert.Equal(50, c.MaxUses); Assert.Equal("Newsletter", c.Note);
        Assert.Equal("skipped-exists", (await Run("""{"code":"AGENT20","percent":5}"""))["status"]!.GetValue<string>());
        Assert.Equal("failed", (await Run("""{"code":"x1","percent":0}"""))["status"]!.GetValue<string>());
        Assert.Equal("failed", (await Run("""{"code":"x2","kind":"fixed"}"""))["status"]!.GetValue<string>());
        Assert.Equal("failed", (await Run("""{"code":"x3","percent":5,"expires":"morgen"}"""))["status"]!.GetValue<string>());
        Assert.Equal("failed", (await Run("""{"code":"x4","percent":5,"category":"gibt-es-nicht"}"""))["status"]!.GetValue<string>());
        Assert.Equal("applied", (await Run("""{"kind":"fixed","amount":"5,00"}"""))["status"]!.GetValue<string>());      // no code: one is made
    }

    // ---- units --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_discount_is_rounded_like_the_rest_of_the_shop_and_shared_out_without_losing_a_cent()
    {
        var p1 = new Product { Id = 1, Title = "A", Category = "x" }; var p2 = new Product { Id = 2, Title = "B", Category = "x" }; var p3 = new Product { Id = 3, Title = "C", Category = "y" };
        PricedLine L(Product p, long cents, int bp) => new(p, null, 1, cents, cents, bp, Money.TaxIncluded(cents, bp));
        var cart = new PricedCart(new[] { L(p1, 333, 1900), L(p2, 333, 1900), L(p3, 334, 700) }, 1000, 0, true, 0, Array.Empty<string>(), false);
        var applied = ShopCoupons.Apply(cart, new Coupon { Code = "T", Kind = CouponKind.Percent, Value = 1000 });
        Assert.Equal(100, applied.DiscountCents);                                          // 10 % of 10,00
        Assert.Equal(100, applied.Lines.Sum(l => l.DiscountCents));
        Assert.All(applied.Lines, l => Assert.Equal(Money.TaxIncluded(l.LineCents - l.DiscountCents, l.TaxBp), l.TaxCents));
        var onlyX = ShopCoupons.Apply(cart, new Coupon { Code = "T", Kind = CouponKind.Fixed, Value = 1000, Category = "x" });
        Assert.Equal(666, onlyX.DiscountCents);                                            // capped at what category x costs
        Assert.Equal(0, onlyX.Lines[2].DiscountCents);

        Assert.Equal(new long[] { 4, 3, 3 }, Money.Allocate(10, new long[] { 1, 1, 1 }));
        Assert.Equal(new long[] { 0, 0 }, Money.Allocate(0, new long[] { 5, 5 }));
        Assert.Equal(7, Money.Allocate(7, new long[] { 1, 2, 3, 4, 5 }).Sum());
    }

    [Theory]
    [InlineData("sommer-10 ", "SOMMER-10")]
    [InlineData("a b!c", "ABC")]
    [InlineData("", "")]
    public void A_code_is_stored_and_compared_in_one_form(string typed, string expected) => Assert.Equal(expected, Coupon.NormaliseCode(typed));
}

/// <summary>Settings as the shop reads them when nothing was saved: the defaults.</summary>
internal sealed class EmptySettings : IPluginSettings
{
    public string Get(string key) => key switch { Keys.ShippingTaxPercent => "19", Keys.DefaultTaxPercent => "19", Keys.Currency => "EUR", _ => "" };
    public bool GetBool(string key) => false;
    public long GetInt(string key, long fallback = 0) => fallback;
    public double GetNumber(string key, double fallback = 0) => fallback;
}
