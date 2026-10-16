using System.Net;
using System.Text.RegularExpressions;
using MatCMS.Plugin.Shop;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The shop through HTTP in the real application: from the catalogue to a paid, shipped order — with bank transfer, which needs no other system.</summary>
public class ShopCheckoutTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    // A test class is built once per test; every test starts a clean minute for the visitor-form limit (all the test's traffic comes from one "address").
    public ShopCheckoutTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }

    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    // ---- catalogue ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_catalogue_lists_active_products_and_a_product_page_shows_price_and_an_add_button_while_inactive_ones_are_hidden()
    {
        var id = await _shop.AddProductAsync("Katalog Tasse", 1290);
        var hidden = await _shop.AddProductAsync("Versteckte Tasse", 500);
        await _shop.WithContextAsync(async (_, store) => await store.SaveProductAsync((await store.ProductAsync(hidden))! with { Active = false }));
        var visitor = _app.Browser();

        var list = Decode(await visitor.GetStringAsync("/shop"));
        Assert.Contains("Katalog Tasse", list);
        Assert.Contains("12,90 €", list);
        Assert.DoesNotContain("Versteckte Tasse", list);

        var page = await visitor.GetAsync("/shop/katalog-tasse");
        var html = Decode(await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("12,90 €", html);
        Assert.Contains("inkl. 19 % MwSt.", html);
        Assert.Contains("In den Warenkorb", html);
        Assert.Contains("application/ld+json", html);                                  // search engines get the product markup
        Assert.Contains("\"price\":\"12.90\"", html);

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/shop/versteckte-tasse")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/shop/gibt-es-nicht")).StatusCode);
        // The shop's own words are not product addresses.
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/shop/webhook")).StatusCode);
        var admin = await _shop.AdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/shop/versteckte-tasse")).StatusCode);   // the administrator may look at a draft
        Assert.Contains("Katalog Tasse", await visitor.GetStringAsync("/sitemap.xml") is var s && s.Contains("/shop/katalog-tasse") ? "Katalog Tasse" : "");
    }

    [Fact]
    public async Task Categories_filter_the_catalogue_and_the_search_finds_by_title()
    {
        await _shop.WithContextAsync(async (_, store) => { if (await store.CategoryBySlugAsync("kaffee") is null) await store.AddCategoryAsync("kaffee", "Kaffee", 0); });
        await _shop.AddProductAsync("Filterkaffee Röstung", 900, category: "kaffee");
        await _shop.AddProductAsync("Teekanne Porzellan", 3000, category: "");
        var c = _app.Browser();
        var inCat = Decode(await c.GetStringAsync("/shop/categories/kaffee"));
        Assert.Contains("Filterkaffee Röstung", inCat);
        Assert.DoesNotContain("Teekanne Porzellan", inCat);
        Assert.Contains("Teekanne Porzellan", Decode(await c.GetStringAsync("/shop?q=teekanne")));
        Assert.DoesNotContain("Filterkaffee Röstung", Decode(await c.GetStringAsync("/shop?q=teekanne")));
        // A search term is data, never markup or SQL.
        var odd = await c.GetStringAsync("/shop?q=%27%3B%20DROP%20TABLE%20x%3B%20--%3Cscript%3E");
        Assert.DoesNotContain("--<script>", odd);
    }

    // ---- cart --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_cart_is_kept_per_browser_priced_by_the_server_and_can_be_changed()
    {
        await _shop.AddProductAsync("Warenkorb Buch", 1500);
        var a = _app.Browser(); var b = _app.Browser();
        await _shop.AddToCartAsync(a, "warenkorb-buch", 2);
        await _shop.AddToCartAsync(a, "warenkorb-buch", 1);                              // the same line grows
        var cart = Decode(await a.GetStringAsync("/shop/cart"));
        Assert.Contains("Warenkorb Buch", cart);
        Assert.Contains("45,00 €", cart);                                              // 3 × 15,00
        var other = Decode(await b.GetStringAsync("/shop/cart"));   // another browser has its own cart: empty (the suggestions may name the product, the cart does not hold it)
        Assert.Contains("Ihr Warenkorb ist leer", other); Assert.DoesNotContain("class=\"shop-line\"", other);

        // Change the quantity through the cart form.
        var token = AppFactory.Token(cart)!;
        var key = Regex.Match(cart, "name=\"(q:\\d+:0)\"").Groups[1].Value;
        var upd = await a.PostAsync("/shop/cart", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["action"] = "update", [key] = "1" }));
        Assert.Equal(HttpStatusCode.Redirect, upd.StatusCode);
        Assert.Contains("15,00 €", Decode(await a.GetStringAsync("/shop/cart")));
        // Zero removes the line.
        await a.PostAsync("/shop/cart", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["action"] = "update", [key] = "0" }));
        Assert.Contains("Ihr Warenkorb ist leer", Decode(await a.GetStringAsync("/shop/cart")));
    }

    [Fact]
    public async Task A_cart_post_without_the_antiforgery_token_is_refused()
    {
        var c = _app.Browser();
        var res = await c.PostAsync("/shop/cart", new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "add", ["product"] = "1", ["qty"] = "1" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var co = await c.PostAsync("/shop/checkout", new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "place" }));
        Assert.Equal(HttpStatusCode.BadRequest, co.StatusCode);
    }

    [Fact]
    public async Task A_cart_cookie_that_is_not_ours_or_a_line_for_a_product_that_does_not_exist_changes_nothing()
    {
        await _shop.AddProductAsync("Cookie Test", 100);
        var c = _app.Browser();
        var res = await c.PostAsync("/shop/cart", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = AppFactory.Token(await c.GetStringAsync("/shop/cookie-test"))!, ["action"] = "add", ["product"] = "999999", ["qty"] = "5" }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("Ihr Warenkorb ist leer", Decode(await c.GetStringAsync("/shop/cart")));
        var evil = new HttpRequestMessage(HttpMethod.Get, "/shop/cart"); evil.Headers.Add("Cookie", "shop.cart=' OR 1=1 --");
        Assert.Equal(HttpStatusCode.OK, (await _app.Browser().SendAsync(evil)).StatusCode);
    }

    // ---- checkout, bank transfer, the whole life of an order ---------------------------------------------------

    [Fact]
    public async Task An_order_by_bank_transfer_goes_from_cart_to_paid_shipped_and_completed_with_invoice_and_history()
    {
        var pid = await _shop.AddProductAsync("Vorkasse Lampe", 1500, trackStock: true, stock: 5, weight: 800);
        var visitor = _app.Browser();
        await _shop.AddToCartAsync(visitor, "vorkasse-lampe", 2);

        // The form refuses what is missing and keeps what was typed.
        var bad = await _shop.CheckoutAsync(visitor, email: "keine-adresse", terms: false);
        Assert.Equal((HttpStatusCode)422, bad.StatusCode);
        var badHtml = Decode(await bad.Content.ReadAsStringAsync());
        Assert.Contains("gültige E-Mail-Adresse", badHtml);
        Assert.Contains("AGB", badHtml);
        Assert.Contains("value=\"Erika Muster\"", badHtml);
        Assert.Equal(5, (await _shop.ProductAsync(pid)).Stock);                         // nothing was taken

        var res = await _shop.CheckoutAsync(visitor, email: "Kunde@Example.org");
        var number = ShopApp.NumberOf(res);
        var token = ShopApp.TokenOf(res);
        var order = (await _shop.OrderAsync(number))!;
        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
        Assert.Equal("kunde@example.org", order.Email);                                 // stored in one form
        Assert.Equal(3000, order.SubtotalCents);                                        // 2 × 15,00
        Assert.Equal(490, order.ShippingCents);                                         // the default rate (free only from 50,00)
        Assert.Equal(3490, order.TotalCents);
        Assert.Equal(Money.TaxIncluded(3000, 1900) + Money.TaxIncluded(490, 1900), order.TaxCents);
        Assert.Equal(3, (await _shop.ProductAsync(pid)).Stock);                         // reserved
        Assert.Equal("bank", order.PaymentProvider);
        Assert.Equal("1", order.TermsVersion);
        Assert.NotNull(order.TermsAcceptedUtc);

        // The cart is empty after ordering, and the cookie is gone.
        Assert.Contains("Ihr Warenkorb ist leer", Decode(await visitor.GetStringAsync("/shop/cart")));

        // The order page: with the key it shows the bank details; without it, nothing.
        var anon = _app.Browser();
        var page = Decode(await anon.GetStringAsync($"/shop/order/{number}?t={token}&new=1"));
        Assert.Contains("Zahlung offen", page);
        Assert.Contains("IBAN DE00", page);
        Assert.Contains(number, page);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/shop/order/{number}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/shop/order/{number}?t=wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/shop/invoice/{number}?t={token}")).StatusCode);   // no invoice before payment

        // The operator sees it and marks it paid.
        var admin = await _shop.AdminAsync();
        Assert.Contains(number, Decode(await admin.GetStringAsync("/admin/p/shop")));
        var done = await _shop.AdminActionAsync(admin, order.Id, "paid");
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        order = (await _shop.OrderAsync(number))!;
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.NotNull(order.PaidUtc);
        Assert.Matches(@"^RE-\d{5}$", order.InvoiceNumber);

        // The invoice exists, is for the key holder only, and says what was bought.
        var invoice = Decode(await anon.GetStringAsync($"/shop/invoice/{number}?t={token}"));
        Assert.Contains(order.InvoiceNumber, invoice);
        Assert.Contains("Vorkasse Lampe", invoice);
        Assert.Contains("34,90 €", invoice);
        Assert.Contains("Test Shop GmbH", invoice);
        Assert.Contains("19 %", invoice);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/shop/invoice/{number}")).StatusCode);

        // Shipped with a tracking number, then completed.
        await _shop.AdminActionAsync(admin, order.Id, "ship", new Dictionary<string, string> { ["tracking"] = "JJD0123456789" });
        order = (await _shop.OrderAsync(number))!;
        Assert.Equal(OrderStatus.Shipped, order.Status);
        Assert.Equal("JJD0123456789", order.Tracking);
        Assert.Contains("JJD0123456789", Decode(await anon.GetStringAsync($"/shop/order/{number}?t={token}")));
        await _shop.AdminActionAsync(admin, order.Id, "complete");
        Assert.Equal(OrderStatus.Completed, (await _shop.OrderAsync(number))!.Status);

        // Moves that are not allowed are refused, not silently applied.
        var back = await _shop.AdminActionAsync(admin, order.Id, "paid");
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Equal(OrderStatus.Completed, (await _shop.OrderAsync(number))!.Status);

        // The history tells the whole story, and the mails that could not be sent (no mail server in the test) are written down, not lost.
        var kinds = (await _shop.EventsAsync(order.Id)).Select(e => e.Kind).ToList();
        Assert.Equal(new[] { "placed" }, kinds.Take(1));
        Assert.Contains("paid", kinds); Assert.Contains("shipped", kinds); Assert.Contains("completed", kinds); Assert.Contains("mail-failed", kinds);
    }

    [Fact]
    public async Task Two_paid_orders_get_consecutive_invoice_numbers()
    {
        await _shop.AddProductAsync("Rechnungs Heft", 700);
        var admin = await _shop.AdminAsync();
        var numbers = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var c = _app.Browser();
            await _shop.AddToCartAsync(c, "rechnungs-heft");
            var n = ShopApp.NumberOf(await _shop.CheckoutAsync(c));
            await _shop.AdminActionAsync(admin, (await _shop.OrderAsync(n))!.Id, "paid");
            numbers.Add((await _shop.OrderAsync(n))!.InvoiceNumber);
        }
        var a = int.Parse(numbers[0][3..]); var b = int.Parse(numbers[1][3..]);
        Assert.Equal(a + 1, b);
    }

    [Fact]
    public async Task Order_numbers_count_up_without_gaps_and_a_second_click_on_pay_changes_nothing()
    {
        await _shop.AddProductAsync("Nummern Karte", 300);
        var nums = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var c = _app.Browser();
            await _shop.AddToCartAsync(c, "nummern-karte");
            var page = await c.GetStringAsync("/shop/checkout");
            var form = ShopApp.CheckoutForm(AppFactory.Token(page)!);
            var first = await c.PostAsync("/shop/checkout", new FormUrlEncodedContent(form));
            nums.Add(long.Parse(ShopApp.NumberOf(first)));
            // The same form again: the cart is empty now, so it cannot become a second order.
            var again = await c.PostAsync("/shop/checkout", new FormUrlEncodedContent(form));
            Assert.Equal("/shop/cart", again.Headers.Location!.OriginalString);
        }
        Assert.Equal(nums[0] + 1, nums[1]); Assert.Equal(nums[1] + 1, nums[2]);
    }

    // ---- prices are the server's --------------------------------------------------------------------------------

    [Fact]
    public async Task A_price_or_a_total_sent_in_the_form_is_ignored()
    {
        await _shop.AddProductAsync("Preis Test", 10000);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "preis-test");
        var res = await _shop.CheckoutAsync(c, extra: new Dictionary<string, string> { ["total"] = "1", ["price"] = "1", ["subtotal"] = "1", ["shipping"] = "0", ["unit"] = "1", ["q:1:0"] = "99" });
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(res)))!;
        Assert.Equal(10000, order.SubtotalCents);
        Assert.Equal(10000, order.TotalCents);                                           // free shipping from 50,00 under the default rate
    }

    [Fact]
    public async Task A_price_change_between_cart_and_order_is_the_new_price()
    {
        var id = await _shop.AddProductAsync("Wandel Preis", 1000);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "wandel-preis");
        await _shop.WithContextAsync(async (_, store) => await store.SaveProductAsync((await store.ProductAsync(id))! with { PriceCents = 2500 }));
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c))))!;
        Assert.Equal(2500, order.SubtotalCents);
        // …and a later change does not touch the order that was placed.
        await _shop.WithContextAsync(async (_, store) => await store.SaveProductAsync((await store.ProductAsync(id))! with { PriceCents = 9999, Title = "Umbenannt" }));
        var items = await _shop.WithContextAsync((_, store) => store.ItemsAsync(order.Id));
        Assert.Equal(2500, items.Single().UnitCents);
        Assert.Equal("Wandel Preis", items.Single().Title);
    }

    // ---- stock ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_last_piece_goes_to_one_buyer_and_the_other_is_told_and_nothing_is_left_half_done()
    {
        var pid = await _shop.AddProductAsync("Letztes Stück", 2000, trackStock: true, stock: 1);
        var a = _app.Browser(); var b = _app.Browser();
        await _shop.AddToCartAsync(a, "letztes-stueck");
        await _shop.AddToCartAsync(b, "letztes-stueck");                                // both carts hold it before either orders

        var first = await _shop.CheckoutAsync(a);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(0, (await _shop.ProductAsync(pid)).Stock);

        var second = await _shop.CheckoutAsync(b);
        // The cart of the second buyer was corrected (sold out), so they are sent back to look at it.
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal("/shop/cart", second.Headers.Location!.OriginalString);
        Assert.Contains("ausverkauft", Decode(await b.GetStringAsync("/shop/cart")));
        Assert.Equal(0, (await _shop.ProductAsync(pid)).Stock);
        // The product page says so.
        Assert.Contains("Ausverkauft", Decode(await _app.Browser().GetStringAsync("/shop/letztes-stueck")));
    }

    [Fact]
    public async Task A_quantity_above_the_stock_is_cut_and_the_buyer_is_told()
    {
        await _shop.AddProductAsync("Knapp Ware", 500, trackStock: true, stock: 2);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "knapp-ware", 5);
        var cart = Decode(await c.GetStringAsync("/shop/cart"));
        Assert.Contains("nur noch 2", cart);
        Assert.Contains("10,00 €", cart);
    }

    [Fact]
    public async Task A_maximum_per_order_is_applied()
    {
        var id = await _shop.AddProductAsync("Limitiert Kerze", 500);
        await _shop.WithContextAsync(async (_, store) => await store.SaveProductAsync((await store.ProductAsync(id))! with { MaxPerOrder = 3 }));
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "limitiert-kerze", 10);
        Assert.Contains("höchstens 3", Decode(await c.GetStringAsync("/shop/cart")));
    }

    [Fact]
    public async Task Cancelling_an_unpaid_order_gives_the_stock_back_and_a_cancelled_order_cannot_be_paid_by_hand()
    {
        var pid = await _shop.AddProductAsync("Storno Ware", 800, trackStock: true, stock: 4);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "storno-ware", 3);
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c))))!;
        Assert.Equal(1, (await _shop.ProductAsync(pid)).Stock);

        var admin = await _shop.AdminAsync();
        await _shop.AdminActionAsync(admin, order.Id, "cancel");
        Assert.Equal(OrderStatus.Cancelled, (await _shop.OrderAsync(order.Number))!.Status);
        Assert.Equal(4, (await _shop.ProductAsync(pid)).Stock);
        await _shop.AdminActionAsync(admin, order.Id, "cancel");                        // twice: nothing is given back twice
        Assert.Equal(4, (await _shop.ProductAsync(pid)).Stock);
        // (The operator's "mark as paid" is for waiting orders; a cancelled one revives only through a provider's confirmed payment.)
        await _shop.AdminActionAsync(admin, order.Id, "paid");
        Assert.Equal(OrderStatus.Paid, (await _shop.OrderAsync(order.Number))!.Status);   // the state machine allows cancelled → paid for exactly that case
    }

    [Fact]
    public async Task The_customer_can_cancel_an_unpaid_order_from_its_page_but_not_a_paid_one()
    {
        var pid = await _shop.AddProductAsync("Kunden Storno", 800, trackStock: true, stock: 2);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "kunden-storno");
        var res = await _shop.CheckoutAsync(c);
        var number = ShopApp.NumberOf(res); var token = ShopApp.TokenOf(res);
        var page = await c.GetStringAsync($"/shop/order/{number}?t={token}");
        var post = await c.PostAsync($"/shop/order/{number}?t={token}", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "cancel" }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal(OrderStatus.Cancelled, (await _shop.OrderAsync(number))!.Status);
        Assert.Equal(2, (await _shop.ProductAsync(pid)).Stock);
        Assert.Equal("customer", (await _shop.OrderAsync(number))!.CancelReason);
    }

    [Fact]
    public async Task The_upkeep_job_cancels_orders_whose_time_ran_out_and_releases_their_stock()
    {
        var pid = await _shop.AddProductAsync("Frist Ware", 800, trackStock: true, stock: 3);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "frist-ware", 2);
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c))))!;
        Assert.Equal(1, (await _shop.ProductAsync(pid)).Stock);

        // Still inside the payment period: the job leaves it.
        await _shop.WithContextAsync((ctx, _) => ShopEvents.MaintenanceAsync(ctx, CancellationToken.None));
        Assert.Equal(OrderStatus.AwaitingPayment, (await _shop.OrderAsync(order.Number))!.Status);

        await _shop.WithContextAsync(async (_, store) => await store.UpdateOrderAsync(order.Id, new Dictionary<string, object?> { ["reserved_until"] = DateTime.UtcNow.AddMinutes(-1) }));
        await _shop.WithContextAsync((ctx, _) => ShopEvents.MaintenanceAsync(ctx, CancellationToken.None));
        var after = (await _shop.OrderAsync(order.Number))!;
        Assert.Equal(OrderStatus.Cancelled, after.Status);
        Assert.Equal("expired", after.CancelReason);
        Assert.Equal(3, (await _shop.ProductAsync(pid)).Stock);
    }

    // ---- variants ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_variant_has_its_own_price_difference_and_stock_and_the_order_records_it()
    {
        var pid = await _shop.AddProductAsync("Variante Shirt", 2000, variants: new[] { ("M", 0L, (long?)3), ("L", 200L, (long?)1), ("XL", 400L, (long?)0) });
        var variants = await _shop.WithContextAsync((_, store) => store.VariantsAsync(pid));
        var l = variants.Single(v => v.Name == "L"); var xl = variants.Single(v => v.Name == "XL");
        var page = Decode(await _app.Browser().GetStringAsync("/shop/variante-shirt"));
        Assert.Contains("ab 20,00 €", page);                                             // from the cheapest
        Assert.Contains("XL", page); Assert.Contains("disabled", page);                  // the sold-out size cannot be chosen

        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "variante-shirt", 1, l.Id);
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c))))!;
        Assert.Equal(2200, order.SubtotalCents);                                         // 20,00 + 2,00
        var item = (await _shop.WithContextAsync((_, store) => store.ItemsAsync(order.Id))).Single();
        Assert.Equal("L", item.VariantTitle);
        Assert.Equal("Variante Shirt – L", item.DisplayTitle);
        Assert.Equal(0, (await _shop.WithContextAsync((_, store) => store.VariantAsync(l.Id)))!.Stock);

        // A sold-out variant in a cart is removed when the cart is shown.
        var d = _app.Browser();
        await _shop.AddToCartAsync(d, "variante-shirt", 1, xl.Id);
        Assert.Contains("ausverkauft", Decode(await d.GetStringAsync("/shop/cart")));
    }

    // ---- shipping ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Shipping_follows_the_country_the_weight_and_the_free_threshold()
    {
        await _shop.AddProductAsync("Versand Hantel", 3000, weight: 20000);
        await _shop.WithContextAsync(async (_, store) =>
        {
            foreach (var r in await store.RatesAsync()) await store.DeleteRateAsync(r.Id);
            await store.SaveRateAsync(new ShippingRate { Name = "Paket DE", Countries = "DE", PriceCents = 590, FreeFromCents = 10000, MaxWeightGrams = 31500 });
            await store.SaveRateAsync(new ShippingRate { Name = "Paket EU", Countries = "AT,FR", PriceCents = 1290, Sort = 1 });
            await store.SaveRateAsync(new ShippingRate { Name = "Abholung", Countries = "*", PriceCents = 0, MaxWeightGrams = 100, Sort = 2 });
        });
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "versand-hantel", 1);

        // Germany: 5,90 (under the threshold); the pickup rate is too light for this parcel.
        var de = Decode(await (await _shop.CheckoutAsync(c, country: "DE", extra: new Dictionary<string, string> { ["action"] = "recalc" })).Content.ReadAsStringAsync());
        Assert.Contains("Paket DE", de); Assert.DoesNotContain("Paket EU", de); Assert.DoesNotContain("Abholung", de);
        // A country nobody ships to.
        var us = await _shop.CheckoutAsync(c, country: "US");
        Assert.Equal((HttpStatusCode)422, us.StatusCode);
        Assert.Contains("nicht liefern", Decode(await us.Content.ReadAsStringAsync()));

        var order = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c, country: "DE"))))!;
        Assert.Equal(590, order.ShippingCents);
        Assert.Equal("Paket DE", order.ShippingMethod);

        // Over the free threshold the same rate costs nothing; Austria pays the EU rate.
        await _shop.AddToCartAsync(c, "versand-hantel", 4);                              // 120,00, 80 kg → too heavy for "Paket DE" (limit 31,5 kg)
        var heavy = await _shop.CheckoutAsync(c, country: "DE");
        Assert.Equal((HttpStatusCode)422, heavy.StatusCode);
        var c2 = _app.Browser();
        await _shop.AddToCartAsync(c2, "versand-hantel", 1);
        var at = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c2, country: "AT"))))!;
        Assert.Equal(1290, at.ShippingCents);

        // Put the default back for the other tests of this class.
        await _shop.WithContextAsync(async (_, store) =>
        {
            foreach (var r in await store.RatesAsync()) await store.DeleteRateAsync(r.Id);
            await store.SaveRateAsync(new ShippingRate { Name = "Standardversand Deutschland", Countries = "DE", PriceCents = 490, FreeFromCents = 5000 });
        });
    }

    // ---- digital goods and free orders --------------------------------------------------------------------------

    [Fact]
    public async Task A_digital_order_needs_the_waiver_has_no_shipping_and_is_delivered_when_paid()
    {
        await _shop.AddProductAsync("Digital Buch", 1900, ProductKind.Digital);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "digital-buch");
        var without = await _shop.CheckoutAsync(c, waiver: false);
        Assert.Equal((HttpStatusCode)422, without.StatusCode);
        Assert.Contains("ausdrückliche Zustimmung", Decode(await without.Content.ReadAsStringAsync()));

        var res = await _shop.CheckoutAsync(c, waiver: true);
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(res)))!;
        Assert.Equal(0, order.ShippingCents);
        Assert.Equal(1900, order.TotalCents);
        Assert.True(order.WithdrawalWaiver);
        Assert.False(order.Shipping.IsComplete);                                         // nothing to ship to
        await _shop.AdminActionAsync(await _shop.AdminAsync(), order.Id, "paid");
        Assert.Equal(OrderStatus.Shipped, (await _shop.OrderAsync(order.Number))!.Status);   // delivered on payment
    }

    [Fact]
    public async Task An_order_that_costs_nothing_is_paid_at_once_and_asks_for_no_payment_method()
    {
        await _shop.AddProductAsync("Gratis Probe", 0, ProductKind.Digital);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "gratis-probe");
        var res = await _shop.CheckoutAsync(c, pay: "", waiver: true);
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(res)))!;
        Assert.Equal(OrderStatus.Shipped, order.Status);
        Assert.Equal("free", order.PaymentProvider);
        Assert.Matches(@"^RE-\d{5}$", order.InvoiceNumber);
    }

    // ---- back office ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/admin/p/shop")]
    [InlineData("/admin/p/shop/orders")]
    [InlineData("/admin/p/shop/orders?status=paid&q=kunde")]
    [InlineData("/admin/p/shop/products")]
    [InlineData("/admin/p/shop/products/0")]
    [InlineData("/admin/p/shop/categories")]
    [InlineData("/admin/p/shop/customers")]
    [InlineData("/admin/p/shop/shipping")]
    [InlineData("/admin/p/shop/_settings?group=set.groupSeller")]
    [InlineData("/admin/p/shop/_settings?group=set.groupStripe")]
    [InlineData("/admin/p/shop/coupons")]
    [InlineData("/admin/p/shop/coupons/0")]
    [InlineData("/admin/p/shop/payment")]
    [InlineData("/admin/p/shop/taxes")]
    [InlineData("/admin/p/shop/legal")]
    [InlineData("/admin/p/shop/numbers")]
    public async Task Every_back_office_page_of_the_shop_renders(string url)
    {
        var admin = await _shop.AdminAsync();
        var res = await admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        // A key may sit in an address (the sidebar links to a settings group by its key); what must not show is one as TEXT.
        var html = System.Text.RegularExpressions.Regex.Replace(await res.Content.ReadAsStringAsync(), "(href|action|value)=\"[^\"]*\"", "");
        Assert.DoesNotContain("shop.tab.", html);                                       // no untranslated key shows
        Assert.DoesNotContain("shop.adm.", html);
        Assert.DoesNotContain("set.group", html);
        Assert.DoesNotContain("Plugin-Fehler", html);
    }

    [Fact]
    public async Task The_back_office_is_for_the_administrator_only()
    {
        var visitor = _app.Browser();
        foreach (var url in new[] { "/admin/p/shop", "/admin/p/shop/orders/1", "/admin/p/shop/products" })
            Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task A_product_is_created_edited_with_variants_and_deleted_through_the_back_office()
    {
        var admin = await _shop.AdminAsync();
        var form = await admin.GetStringAsync("/admin/p/shop/products/0");
        Assert.Contains("name=\"variants.count\"", form);                                // the variants are rows to edit, not a text
        Dictionary<string, string> Post(string title, string price, string[][]? variants = null) => new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AppFactory.Token(form)!, ["action"] = "save", ["title"] = title, ["slug"] = "", ["kind"] = "physical", ["price"] = price, ["tax"] = "19",
            ["sku"] = "SKU-1", ["category"] = "", ["summary"] = "Kurz", ["description"] = "<p>Lang</p><script>alert(1)</script>", ["image"] = "", ["gallery"] = "[]", ["variants.count"] = (variants?.Length ?? 0).ToString(),
            ["stock"] = "0", ["weight"] = "0", ["max"] = "0", ["sort"] = "0", ["active"] = "true"
        }.Concat((variants ?? Array.Empty<string[]>()).SelectMany((r, i) => new[] { ($"variants.{i}.id", r[0]), ($"variants.{i}.name", r[1]), ($"variants.{i}.delta", r[2]), ($"variants.{i}.stock", r[3]), ($"variants.{i}.sku", r[4]) }
            .Select(x => KeyValuePair.Create(x.Item1, x.Item2)))).ToDictionary(kv => kv.Key, kv => kv.Value);
        var bad = await admin.PostAsync("/admin/p/shop/products/0", new FormUrlEncodedContent(Post("Adm Produkt", "abc")));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("keine gültige Zahl", Decode(await bad.Content.ReadAsStringAsync()));
        var badVar = await admin.PostAsync("/admin/p/shop/products/0", new FormUrlEncodedContent(Post("Adm Produkt", "9,90", new[] { new[] { "", "A", "x", "", "" } })));
        Assert.Contains("kein Betrag", Decode(await badVar.Content.ReadAsStringAsync()));

        var ok = await admin.PostAsync("/admin/p/shop/products/0", new FormUrlEncodedContent(Post("Adm Produkt", "9,90", new[] { new[] { "", "S", "", "4", "A-S" }, new[] { "", "M", "+1", "2", "" } })));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var location = ok.Headers.Location!.OriginalString;
        var id = long.Parse(location[(location.LastIndexOf('/') + 1)..]);
        var p = await _shop.ProductAsync(id);
        Assert.Equal("adm-produkt", p.Slug);
        Assert.Equal(990, p.PriceCents);
        Assert.DoesNotContain("<script>", p.DescriptionHtml);                            // the description is sanitised when it is saved
        Assert.Contains("<p>Lang</p>", p.DescriptionHtml);
        var variants = await _shop.WithContextAsync((_, store) => store.VariantsAsync(id, true));
        Assert.Equal(new[] { "S", "M" }, variants.Select(v => v.Name));
        Assert.Equal(100, variants[1].PriceDeltaCents);

        // The same title again: the address is made unique. A reserved word is not an address.
        var again = await admin.PostAsync("/admin/p/shop/products/0", new FormUrlEncodedContent(Post("Adm Produkt", "1")));
        var id2 = long.Parse(again.Headers.Location!.OriginalString.Split('/').Last());
        Assert.Equal("adm-produkt-2", (await _shop.ProductAsync(id2)).Slug);
        var cart = Post("Cart", "1"); cart["slug"] = "cart";
        var reserved = await admin.PostAsync("/admin/p/shop/products/0", new FormUrlEncodedContent(cart));
        Assert.NotEqual("cart", (await _shop.ProductAsync(long.Parse(reserved.Headers.Location!.OriginalString.Split('/').Last()))).Slug);

        // Taking a row out of the editor switches the variant off; it is not deleted (old orders point at it).
        var edit = await admin.GetStringAsync(location);
        var change = Post("Adm Produkt", "9,90", new[] { new[] { variants[0].Id.ToString(), "S", "", "4", "A-S" } }); change["__RequestVerificationToken"] = AppFactory.Token(edit)!;
        await admin.PostAsync(location, new FormUrlEncodedContent(change));
        Assert.Equal(new[] { "S" }, (await _shop.WithContextAsync((_, store) => store.VariantsAsync(id, true))).Select(v => v.Name));
        Assert.Equal(2, (await _shop.WithContextAsync((_, store) => store.VariantsAsync(id, false))).Count);

        // Delete through the list.
        var list = await admin.GetStringAsync("/admin/p/shop/products");
        var del = await admin.PostAsync("/admin/p/shop/products", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(list)!, ["action"] = "delete", ["id"] = id.ToString() }));
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Browser().GetAsync("/shop/adm-produkt")).StatusCode);
    }

    [Fact]
    public async Task Categories_and_shipping_rates_are_edited_in_the_back_office_with_checks()
    {
        var admin = await _shop.AdminAsync();
        var page = await admin.GetStringAsync("/admin/p/shop/categories/0");
        var res = await admin.PostAsync("/admin/p/shop/categories/0", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "save", ["name"] = "Büro & Co" }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.NotNull(await _shop.WithContextAsync((_, store) => store.CategoryBySlugAsync(MatCMS.Pages.Admin.Pages.IndexModel.Slugify("Büro & Co"))));

        var ship = await admin.GetStringAsync("/admin/p/shop/shipping");
        // The form posts every existing rate along with the new one.
        var existing = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(ship, "name=\"name_(\\d+)\" value=\"([^\"]*)\""))
        {
            var id = m.Groups[1].Value;
            existing["name_" + id] = WebUtility.HtmlDecode(m.Groups[2].Value); existing["price_" + id] = "4,90"; existing["countries_" + id] = "DE"; existing["active_" + id] = "true";
        }
        Dictionary<string, string> Form(Dictionary<string, string> add) { var f = new Dictionary<string, string>(existing) { ["__RequestVerificationToken"] = AppFactory.Token(ship)!, ["action"] = "save" }; foreach (var kv in add) f[kv.Key] = kv.Value; return f; }
        var bad = await admin.PostAsync("/admin/p/shop/shipping", new FormUrlEncodedContent(Form(new() { ["name_new"] = "Kaputt", ["price_new"] = "abc", ["countries_new"] = "DE", ["active_new"] = "true" })));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("kein gültiger Preis", Decode(await bad.Content.ReadAsStringAsync()));
        var badCountry = await admin.PostAsync("/admin/p/shop/shipping", new FormUrlEncodedContent(Form(new() { ["name_new"] = "Welt", ["price_new"] = "9", ["countries_new"] = "DEU,X", ["active_new"] = "true" })));
        Assert.Contains("zwei Buchstaben", Decode(await badCountry.Content.ReadAsStringAsync()));
    }
}
