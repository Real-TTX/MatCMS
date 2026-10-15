using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Plugin.Shop;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// Payment by Stripe Checkout, against a stand-in for Stripe: what the shop asks Stripe for, what it does with each notification — and what it refuses to do (a forged or
/// stale notification, a payment for the wrong amount, the same event twice).
/// </summary>
[Collection("shop-stripe")]
public class ShopStripeTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;

    public ShopStripeTests(AppFactory app) { _app = app; PluginPostLimiterReset(); }
    private static void PluginPostLimiterReset() => MatCMS.Services.Plugins.PluginPostLimiter.Reset();

    public async Task InitializeAsync()
    {
        _shop = await ShopApp.StartAsync(_app, stripe: true);
        StripeClient.HandlerFactory = () => _shop.Stripe;
        _shop.Stripe.FailNextSessionWith = null; _shop.Stripe.FailRefundWith = null; _shop.Stripe.AmountRefunded = 0;
    }

    public Task DisposeAsync() { StripeClient.HandlerFactory = null; return Task.CompletedTask; }

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    /// <summary>Puts a product in a cart and pays with Stripe. Returns the order and the Stripe session it got.</summary>
    private async Task<(Order Order, string Session, string Token, HttpClient Visitor)> OrderViaStripeAsync(string title, long price, int qty = 1, string kind = ProductKind.Physical,
        bool trackStock = false, long stock = 0)
    {
        await _shop.AddProductAsync(title, price, kind, trackStock, stock);
        var visitor = _app.Browser();
        await _shop.AddToCartAsync(visitor, MatCMS.Pages.Admin.Pages.IndexModel.Slugify(title), qty);
        var res = await _shop.CheckoutAsync(visitor, pay: "stripe", waiver: kind == ProductKind.Digital);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var loc = res.Headers.Location!.OriginalString;
        Assert.StartsWith("https://checkout.stripe.com/c/pay/cs_test_", loc);
        var session = loc[(loc.LastIndexOf('/') + 1)..];
        var call = _shop.Stripe.Calls.Last(c => c.Path == "/v1/checkout/sessions" && c.Method == HttpMethod.Post);
        var number = call.Form["client_reference_id"];
        return ((await _shop.OrderAsync(number))!, session, "", visitor);
    }

    // ---- starting ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Paying_with_stripe_creates_a_checkout_session_whose_lines_add_up_to_the_orders_total_and_sends_the_customer_there()
    {
        var (order, session, _, visitor) = await OrderViaStripeAsync("Stripe Lampe", 2000, 2, trackStock: true, stock: 10);
        var call = _shop.Stripe.Calls.Last(c => c.Path == "/v1/checkout/sessions" && c.Method == HttpMethod.Post);
        var f = call.Form;

        Assert.Equal("payment", f["mode"]);
        Assert.Equal("eur", f["line_items[0][price_data][currency]"]);
        Assert.Equal("2000", f["line_items[0][price_data][unit_amount]"]);
        Assert.Equal("2", f["line_items[0][quantity]"]);
        Assert.Equal("Stripe Lampe", f["line_items[0][price_data][product_data][name]"]);
        Assert.Equal("490", f["line_items[1][price_data][unit_amount]"]);                 // shipping is a line of its own
        var sum = Enumerable.Range(0, 2).Sum(i => long.Parse(f[$"line_items[{i}][price_data][unit_amount]"]) * long.Parse(f[$"line_items[{i}][quantity]"]));
        Assert.Equal(order.TotalCents, sum);                                                // what Stripe charges is what the order costs
        Assert.Equal(order.Number, f["client_reference_id"]);
        Assert.Equal(order.Number, f["metadata[order_number]"]);
        Assert.Equal("kunde@example.org", f["customer_email"]);
        Assert.Contains($"/shop/order/{order.Number}?t={order.AccessToken}&paid=1&session_id={{CHECKOUT_SESSION_ID}}", f["success_url"]);
        Assert.Contains("&cancelled=1", f["cancel_url"]);
        Assert.StartsWith("http", f["success_url"]);                                        // an absolute address Stripe can send the customer back to
        Assert.True(long.Parse(f["expires_at"]) > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal("order-" + order.Number, call.Headers["Idempotency-Key"]);
        Assert.Equal("Bearer " + ShopApp.StripeKey, call.Headers["Authorization"]);

        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);                            // NOT paid because the customer left for Stripe
        Assert.Equal(session, order.PaymentRef);
        Assert.Equal("stripe", order.PaymentProvider);
        Assert.Equal(8, (await _shop.ProductAsync((await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("stripe-lampe")))!.Id)).Stock);   // reserved for the 30 minutes
        Assert.Contains("Ihr Warenkorb ist leer", Decode(await visitor.GetStringAsync("/shop/cart")));
    }

    [Fact]
    public async Task Stripe_is_not_offered_until_it_is_on_and_a_forged_method_is_refused()
    {
        await _shop.AddProductAsync("Methoden Tasse", 1000);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "methoden-tasse");
        var page = Decode(await c.GetStringAsync("/shop/checkout"));
        Assert.Contains("Online bezahlen", page);                                           // on, with a key
        Assert.Contains("Vorkasse", page);
        var bad = await _shop.CheckoutAsync(c, pay: "paypal-evil");
        Assert.Equal((HttpStatusCode)422, bad.StatusCode);
        Assert.Contains("Zahlungsart", Decode(await bad.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task When_stripe_refuses_to_start_the_order_is_cancelled_the_stock_comes_back_and_the_cart_is_kept()
    {
        var pid = await _shop.AddProductAsync("Fehler Kiste", 1500, trackStock: true, stock: 3);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "fehler-kiste", 2);
        _shop.Stripe.FailNextSessionWith = "Your account cannot currently make live charges.";
        var res = await _shop.CheckoutAsync(c, pay: "stripe");
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        var html = Decode(await res.Content.ReadAsStringAsync());
        Assert.Contains("Zahlung konnte nicht gestartet werden", html);
        Assert.DoesNotContain("live charges", html);                                       // Stripe's words are for the operator's log, not the customer
        Assert.Equal(3, (await _shop.ProductAsync(pid)).Stock);
        Assert.Contains("Fehler Kiste", Decode(await c.GetStringAsync("/shop/cart")));      // the cart is still there to try again
        var orders = await _shop.WithContextAsync((_, s) => s.OrdersAsync(OrderStatus.Cancelled));
        var failed = orders.Items.First(o => o.CancelReason == "payment_failed");
        Assert.Contains((await _shop.EventsAsync(failed.Id)), e => e.Kind == "payment-failed" && e.Text.Contains("live charges"));   // the operator can read why
    }

    // ---- notifications -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_signed_completed_notification_pays_the_order_once_with_invoice_and_the_same_event_again_changes_nothing()
    {
        var (order, session, _, _) = await OrderViaStripeAsync("Webhook Buch", 1900, 1);
        var res = await _shop.WebhookAsync(_app.Browser(), "checkout.session.completed", ShopApp.PaidSession(session, order), eventId: "evt_paid_1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var paid = (await _shop.OrderAsync(order.Number))!;
        Assert.Equal(OrderStatus.Paid, paid.Status);
        Assert.Equal("pi_test_1", paid.PaymentIntent);
        Assert.Matches(@"^RE-\d{5}$", paid.InvoiceNumber);
        Assert.NotNull(paid.PaidUtc);

        var again = await _shop.WebhookAsync(_app.Browser(), "checkout.session.completed", ShopApp.PaidSession(session, order), eventId: "evt_paid_1");
        Assert.Equal("duplicate", await again.Content.ReadAsStringAsync());
        // A DIFFERENT event for an order that is paid already is not a second payment either (no second invoice, no second "paid" entry).
        await _shop.WebhookAsync(_app.Browser(), "checkout.session.completed", ShopApp.PaidSession(session, order), eventId: "evt_paid_2");
        var after = (await _shop.OrderAsync(order.Number))!;
        Assert.Equal(paid.InvoiceNumber, after.InvoiceNumber);
        Assert.Single((await _shop.EventsAsync(order.Id)).Where(e => e.Kind == "paid"));
    }

    [Fact]
    public async Task A_forged_stale_or_unsigned_notification_is_refused_and_pays_nothing()
    {
        var (order, session, _, _) = await OrderViaStripeAsync("Fälschung Buch", 1900);
        var c = _app.Browser();
        var evt = ShopApp.PaidSession(session, order);

        Assert.Equal(HttpStatusCode.BadRequest, (await _shop.WebhookAsync(c, "checkout.session.completed", evt, secret: "whsec_not_ours")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _shop.WebhookAsync(c, "checkout.session.completed", evt, at: DateTimeOffset.UtcNow.AddMinutes(-30))).StatusCode);   // a recording, replayed
        var unsigned = new HttpRequestMessage(HttpMethod.Post, "/shop/webhook/stripe") { Content = new StringContent("{\"id\":\"evt_x\",\"type\":\"checkout.session.completed\",\"data\":{\"object\":{}}}", Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(unsigned)).StatusCode);
        // A body changed after signing.
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["id"] = "evt_t", ["type"] = "checkout.session.completed", ["data"] = new JsonObject { ["object"] = evt.DeepClone() } }.ToJsonString());
        var tampered = new HttpRequestMessage(HttpMethod.Post, "/shop/webhook/stripe") { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("evt_t", "evt_u"))) };
        tampered.Headers.Add("Stripe-Signature", StripeClient.Sign(body, ShopApp.WebhookSecret, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(tampered)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/shop/webhook/stripe")).StatusCode);                 // POST only

        Assert.Equal(OrderStatus.AwaitingPayment, (await _shop.OrderAsync(order.Number))!.Status);
    }

    [Fact]
    public async Task A_payment_for_the_wrong_amount_or_currency_is_not_accepted_and_the_history_says_why()
    {
        var (order, session, _, _) = await OrderViaStripeAsync("Betrag Buch", 1900);
        await _shop.WebhookAsync(_app.Browser(), "checkout.session.completed", ShopApp.PaidSession(session, order, amount: 100));
        await _shop.WebhookAsync(_app.Browser(), "checkout.session.completed", ShopApp.PaidSession(session, order, currency: "usd"));
        var o = (await _shop.OrderAsync(order.Number))!;
        Assert.Equal(OrderStatus.AwaitingPayment, o.Status);
        Assert.Equal(2, (await _shop.EventsAsync(order.Id)).Count(e => e.Kind == "warning" && e.Text.Contains("nicht als bezahlt")));
        // The right amount, afterwards, still works.
        await _shop.WebhookAsync(_app.Browser(), "checkout.session.completed", ShopApp.PaidSession(session, order));
        Assert.Equal(OrderStatus.Paid, (await _shop.OrderAsync(order.Number))!.Status);
    }

    [Fact]
    public async Task A_notification_for_an_order_we_do_not_know_or_an_event_we_do_not_use_is_answered_ok_and_does_nothing()
    {
        var c = _app.Browser();
        var ghost = new JsonObject { ["id"] = "cs_ghost", ["payment_status"] = "paid", ["amount_total"] = 100, ["currency"] = "eur", ["client_reference_id"] = "no-such-order" };
        Assert.Equal(HttpStatusCode.OK, (await _shop.WebhookAsync(c, "checkout.session.completed", ghost)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _shop.WebhookAsync(c, "customer.created", new JsonObject { ["id"] = "cus_1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _shop.WebhookAsync(c, "charge.refunded", new JsonObject { ["payment_intent"] = "pi_unknown", ["amount_refunded"] = 5 })).StatusCode);
    }

    [Fact]
    public async Task A_notification_body_over_the_limit_is_refused()
    {
        var big = new StringContent("{\"pad\":\"" + new string('a', RequestData.MaxWebhookBody + 10) + "\"}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _app.Browser().PostAsync("/shop/webhook/stripe", big)).StatusCode);
    }

    [Fact]
    public async Task A_payment_that_settles_later_keeps_the_order_waiting_then_pays_or_cancels_it()
    {
        var pid = await _shop.AddProductAsync("Lastschrift Ding", 3000, trackStock: true, stock: 5);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "lastschrift-ding", 2);
        var res = await _shop.CheckoutAsync(c, pay: "stripe");
        var order = (await _shop.OrderAsync(_shop.Stripe.Calls.Last(x => x.Path == "/v1/checkout/sessions").Form["client_reference_id"]))!;
        var session = res.Headers.Location!.OriginalString.Split('/').Last();

        // The session is complete but the money is not there yet.
        await _shop.WebhookAsync(_app.Browser(), "checkout.session.completed", ShopApp.PaidSession(session, order, status: "unpaid"));
        var waiting = (await _shop.OrderAsync(order.Number))!;
        Assert.Equal(OrderStatus.AwaitingPayment, waiting.Status);
        Assert.True(waiting.ReservedUntilUtc > DateTime.UtcNow.AddDays(5));                 // held for as long as a debit takes
        Assert.Equal(3, (await _shop.ProductAsync(pid)).Stock);

        await _shop.WebhookAsync(_app.Browser(), "checkout.session.async_payment_succeeded", ShopApp.PaidSession(session, order));
        Assert.Equal(OrderStatus.Paid, (await _shop.OrderAsync(order.Number))!.Status);

        // …and the failing case, for a second order.
        var c2 = _app.Browser();
        await _shop.AddToCartAsync(c2, "lastschrift-ding", 1);
        var res2 = await _shop.CheckoutAsync(c2, pay: "stripe");
        var order2 = (await _shop.OrderAsync(_shop.Stripe.Calls.Last(x => x.Path == "/v1/checkout/sessions").Form["client_reference_id"]))!;
        var session2 = res2.Headers.Location!.OriginalString.Split('/').Last();
        Assert.Equal(2, (await _shop.ProductAsync(pid)).Stock);
        await _shop.WebhookAsync(_app.Browser(), "checkout.session.async_payment_failed", ShopApp.PaidSession(session2, order2, status: "unpaid"));
        var failed = (await _shop.OrderAsync(order2.Number))!;
        Assert.Equal(OrderStatus.Cancelled, failed.Status);
        Assert.Equal("payment_failed", failed.CancelReason);
        Assert.Equal(3, (await _shop.ProductAsync(pid)).Stock);
    }

    [Fact]
    public async Task An_expired_session_cancels_the_order_and_gives_the_stock_back_and_a_payment_that_still_arrives_brings_it_back()
    {
        var pid = await _shop.AddProductAsync("Ablauf Ding", 2000, trackStock: true, stock: 4);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "ablauf-ding", 3);
        var res = await _shop.CheckoutAsync(c, pay: "stripe");
        var order = (await _shop.OrderAsync(_shop.Stripe.Calls.Last(x => x.Path == "/v1/checkout/sessions").Form["client_reference_id"]))!;
        var session = res.Headers.Location!.OriginalString.Split('/').Last();
        Assert.Equal(1, (await _shop.ProductAsync(pid)).Stock);

        await _shop.WebhookAsync(_app.Browser(), "checkout.session.expired", new JsonObject { ["id"] = session, ["client_reference_id"] = order.Number });
        var cancelled = (await _shop.OrderAsync(order.Number))!;
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Equal("expired", cancelled.CancelReason);
        Assert.Equal(4, (await _shop.ProductAsync(pid)).Stock);

        // The customer paid in the last second; Stripe says so after the expiry was recorded. The sale is real: the order is back and the goods are taken again.
        await _shop.WebhookAsync(_app.Browser(), "checkout.session.completed", ShopApp.PaidSession(session, cancelled));
        var revived = (await _shop.OrderAsync(order.Number))!;
        Assert.Equal(OrderStatus.Paid, revived.Status);
        Assert.Equal(1, (await _shop.ProductAsync(pid)).Stock);
        Assert.Contains((await _shop.EventsAsync(order.Id)), e => e.Kind == "warning" && e.Text.Contains("nach der Stornierung"));
    }

    // ---- refunds ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Refund_notifications_count_in_total_so_a_repeat_changes_nothing_and_a_full_refund_closes_the_order_and_blocks_downloads()
    {
        var (order, session, _, _) = await OrderViaStripeAsync("Erstattung Heft", 2000, 1, ProductKind.Digital);
        await _shop.WebhookAsync(_app.Browser(), "checkout.session.completed", ShopApp.PaidSession(session, order, intent: "pi_refund_1"));
        var before = (await _shop.OrderAsync(order.Number))!;
        Assert.Equal(OrderStatus.Shipped, before.Status);
        var dls = await _shop.WithContextAsync((_, s) => s.DownloadsOfOrderAsync(order.Id));
        Assert.All(dls, d => Assert.False(d.Revoked));

        await _shop.WebhookAsync(_app.Browser(), "charge.refunded", new JsonObject { ["payment_intent"] = "pi_refund_1", ["amount_refunded"] = 500 });
        await _shop.WebhookAsync(_app.Browser(), "charge.refunded", new JsonObject { ["payment_intent"] = "pi_refund_1", ["amount_refunded"] = 500 });   // a new event, the same total
        var part = (await _shop.OrderAsync(order.Number))!;
        Assert.Equal(500, part.RefundedCents);
        Assert.Equal(OrderStatus.Shipped, part.Status);                                    // a partial refund is not "refunded"
        Assert.Single((await _shop.EventsAsync(order.Id)).Where(e => e.Kind == "refund"));

        await _shop.WebhookAsync(_app.Browser(), "charge.refunded", new JsonObject { ["payment_intent"] = "pi_refund_1", ["amount_refunded"] = before.TotalCents });
        var full = (await _shop.OrderAsync(order.Number))!;
        Assert.Equal(OrderStatus.Refunded, full.Status);
        Assert.Equal(before.TotalCents, full.RefundedCents);
        Assert.All(await _shop.WithContextAsync((_, s) => s.DownloadsOfOrderAsync(order.Id)), d => Assert.True(d.Revoked));
        // More than the total is cut to the total, never beyond.
        await _shop.WebhookAsync(_app.Browser(), "charge.refunded", new JsonObject { ["payment_intent"] = "pi_refund_1", ["amount_refunded"] = before.TotalCents * 5 });
        Assert.Equal(before.TotalCents, (await _shop.OrderAsync(order.Number))!.RefundedCents);
    }

    [Fact]
    public async Task The_operator_refunds_through_stripe_and_the_order_shows_what_stripe_says_was_refunded()
    {
        var (order, session, _, _) = await OrderViaStripeAsync("Admin Erstattung", 4000);
        await _shop.WebhookAsync(_app.Browser(), "checkout.session.completed", ShopApp.PaidSession(session, order, intent: "pi_admin_1"));
        var admin = await _shop.AdminAsync();
        var paid = (await _shop.OrderAsync(order.Number))!;

        _shop.Stripe.AmountRefunded = 1000;
        var res = await _shop.AdminActionAsync(admin, order.Id, "refund", new Dictionary<string, string> { ["amount"] = "10,00" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var refund = _shop.Stripe.Calls.Last(c => c.Path == "/v1/refunds");
        Assert.Equal("pi_admin_1", refund.Form["payment_intent"]);
        Assert.Equal("1000", refund.Form["amount"]);
        Assert.Equal($"refund-{order.Number}-0-1000", refund.Headers["Idempotency-Key"]);
        Assert.Equal(1000, (await _shop.OrderAsync(order.Number))!.RefundedCents);
        Assert.Equal(OrderStatus.Paid, (await _shop.OrderAsync(order.Number))!.Status);

        // More than is left is refused before Stripe is asked.
        var calls = _shop.Stripe.Calls.Count(c => c.Path == "/v1/refunds");
        await _shop.AdminActionAsync(admin, order.Id, "refund", new Dictionary<string, string> { ["amount"] = Money.ForInput(paid.TotalCents) });
        Assert.Equal(calls, _shop.Stripe.Calls.Count(c => c.Path == "/v1/refunds"));
        Assert.Equal(1000, (await _shop.OrderAsync(order.Number))!.RefundedCents);

        // Stripe saying no leaves the order as it was and the operator gets Stripe's reason.
        _shop.Stripe.FailRefundWith = "Charge ch_1 has already been refunded.";
        var page = await _shop.AdminActionAsync(admin, order.Id, "refund", new Dictionary<string, string> { ["amount"] = "5,00" });
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal(1000, (await _shop.OrderAsync(order.Number))!.RefundedCents);
        _shop.Stripe.FailRefundWith = null;
        Assert.Contains("already been refunded", Decode(await admin.GetStringAsync($"/admin/p/shop/orders/{order.Id}")));   // the flash carries Stripe's sentence
    }

    // ---- coming back and waiting ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_customer_back_from_stripe_before_the_notification_sees_the_truth_because_the_page_asks_stripe()
    {
        var (order, session, _, _) = await OrderViaStripeAsync("Rückkehr Buch", 1900);
        var token = order.AccessToken;
        var c = _app.Browser();

        // Stripe says the session is still open: the order stays waiting.
        _shop.Stripe.Sessions[session] = new JsonObject { ["id"] = session, ["status"] = "open", ["payment_status"] = "unpaid" };
        var waiting = Decode(await c.GetStringAsync($"/shop/order/{order.Number}?t={token}&paid=1&session_id={session}"));
        Assert.Contains("Zahlung offen", waiting);

        // Stripe says it is complete and paid — before any webhook came.
        _shop.Stripe.Sessions[session] = ShopApp.PaidSession(session, order);
        var paid = Decode(await c.GetStringAsync($"/shop/order/{order.Number}?t={token}&paid=1&session_id={session}"));
        Assert.Contains("Bezahlt", paid);
        Assert.Equal(OrderStatus.Paid, (await _shop.OrderAsync(order.Number))!.Status);
        Assert.Contains("Rechnung", paid);
    }

    [Fact]
    public async Task The_page_does_not_trust_a_session_id_from_the_address_it_asks_about_the_orders_own_session()
    {
        var (order, session, _, _) = await OrderViaStripeAsync("Fremde Sitzung", 1900);
        // Somebody else's paid session id in the address changes nothing: the shop asks Stripe about THIS order's session only.
        _shop.Stripe.Sessions["cs_other_paid"] = ShopApp.PaidSession("cs_other_paid", order);
        _shop.Stripe.Sessions[session] = new JsonObject { ["id"] = session, ["status"] = "open", ["payment_status"] = "unpaid" };
        await _app.Browser().GetStringAsync($"/shop/order/{order.Number}?t={order.AccessToken}&paid=1&session_id=cs_other_paid");
        Assert.Equal(OrderStatus.AwaitingPayment, (await _shop.OrderAsync(order.Number))!.Status);
        Assert.DoesNotContain(_shop.Stripe.Calls, c => c.Path.EndsWith("cs_other_paid"));
    }

    [Fact]
    public async Task Paying_again_closes_the_old_session_and_opens_a_new_one()
    {
        var (order, session, _, _) = await OrderViaStripeAsync("Nochmal Buch", 1900);
        var c = _app.Browser();
        _shop.Stripe.Sessions[session] = new JsonObject { ["id"] = session, ["status"] = "open", ["payment_status"] = "unpaid" };
        var page = await c.GetStringAsync($"/shop/order/{order.Number}?t={order.AccessToken}&cancelled=1");
        Assert.Contains("Jetzt bezahlen", Decode(page));
        var res = await c.PostAsync($"/shop/order/{order.Number}?t={order.AccessToken}", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "retry" }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith("https://checkout.stripe.com/c/pay/cs_test_", res.Headers.Location!.OriginalString);
        Assert.Contains(_shop.Stripe.Calls, x => x.Path == $"/v1/checkout/sessions/{session}/expire" && x.Method == HttpMethod.Post);   // the old one can never be paid now
        var newCreate = _shop.Stripe.Calls.Last(x => x.Path == "/v1/checkout/sessions" && x.Method == HttpMethod.Post);
        Assert.NotEqual("order-" + order.Number, newCreate.Headers["Idempotency-Key"]);                                                   // a new key, or Stripe would hand the old session back
        Assert.NotEqual(session, (await _shop.OrderAsync(order.Number))!.PaymentRef);
    }

    [Fact]
    public async Task Retrying_an_order_that_was_paid_meanwhile_does_not_open_a_second_payment()
    {
        var (order, session, _, _) = await OrderViaStripeAsync("Doppelt Buch", 1900);
        var c = _app.Browser();
        var page = await c.GetStringAsync($"/shop/order/{order.Number}?t={order.AccessToken}&cancelled=1");
        _shop.Stripe.Sessions[session] = ShopApp.PaidSession(session, order);              // it WAS paid; the notification is late
        var before = _shop.Stripe.Calls.Count(x => x.Path == "/v1/checkout/sessions" && x.Method == HttpMethod.Post);
        await c.PostAsync($"/shop/order/{order.Number}?t={order.AccessToken}", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "retry" }));
        Assert.Equal(before, _shop.Stripe.Calls.Count(x => x.Path == "/v1/checkout/sessions" && x.Method == HttpMethod.Post));
        Assert.Equal(OrderStatus.Paid, (await _shop.OrderAsync(order.Number))!.Status);
    }

    [Fact]
    public async Task The_upkeep_job_asks_stripe_about_orders_whose_notification_never_came()
    {
        var (order, session, _, _) = await OrderViaStripeAsync("Verloren Buch", 1900);
        await _shop.WithContextAsync(async (_, s) => await s.UpdateOrderAsync(order.Id, new Dictionary<string, object?> { ["created"] = DateTime.UtcNow.AddMinutes(-20) }));
        _shop.Stripe.Sessions[session] = ShopApp.PaidSession(session, order);
        await _shop.WithContextAsync((ctx, _) => ShopEvents.MaintenanceAsync(ctx, CancellationToken.None));
        Assert.Equal(OrderStatus.Paid, (await _shop.OrderAsync(order.Number))!.Status);
    }

    [Fact]
    public async Task An_order_past_its_time_is_checked_with_stripe_before_it_is_cancelled()
    {
        var pid = await _shop.AddProductAsync("Spät Ding", 2500, trackStock: true, stock: 2);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "spaet-ding");
        var res = await _shop.CheckoutAsync(c, pay: "stripe");
        var order = (await _shop.OrderAsync(_shop.Stripe.Calls.Last(x => x.Path == "/v1/checkout/sessions").Form["client_reference_id"]))!;
        var session = res.Headers.Location!.OriginalString.Split('/').Last();
        await _shop.WithContextAsync(async (_, s) => await s.UpdateOrderAsync(order.Id, new Dictionary<string, object?> { ["reserved_until"] = DateTime.UtcNow.AddMinutes(-5) }));

        // Paid at Stripe, notification lost: the job finds out and pays the order instead of cancelling it.
        _shop.Stripe.Sessions[session] = ShopApp.PaidSession(session, order);
        await _shop.WithContextAsync((ctx, _) => ShopEvents.MaintenanceAsync(ctx, CancellationToken.None));
        Assert.Equal(OrderStatus.Paid, (await _shop.OrderAsync(order.Number))!.Status);
        Assert.Equal(1, (await _shop.ProductAsync(pid)).Stock);
    }

    [Fact]
    public async Task A_coupons_discount_goes_to_stripe_as_a_one_time_coupon_so_stripes_total_is_the_orders_total()
    {
        await _shop.AddProductAsync("Stripe Gutschein", 2000);
        await _shop.AddCouponAsync("STRIPE10", value: 1000);
        var visitor = _app.Browser();
        await _shop.AddToCartAsync(visitor, "stripe-gutschein", 2);
        await _shop.ApplyCouponAsync(visitor, "STRIPE10");
        var res = await _shop.CheckoutAsync(visitor, pay: "stripe");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var session = res.Headers.Location!.OriginalString.Split('/').Last();
        var call = _shop.Stripe.Calls.Last(c => c.Path == "/v1/checkout/sessions" && c.Method == HttpMethod.Post);
        var order = (await _shop.OrderAsync(call.Form["client_reference_id"]))!;
        Assert.Equal(4090, order.TotalCents);                                                // 40,00 − 4,00 + 4,90

        var coupon = _shop.Stripe.Calls.Last(c => c.Path == "/v1/coupons");
        Assert.Equal("400", coupon.Form["amount_off"]);
        Assert.Equal("eur", coupon.Form["currency"]);
        Assert.Equal("once", coupon.Form["duration"]);
        Assert.Equal("1", coupon.Form["max_redemptions"]);
        Assert.Equal("Gutschein STRIPE10", coupon.Form["name"]);
        Assert.Equal($"coupon-{order.Number}", coupon.Headers["Idempotency-Key"]);
        Assert.StartsWith("co_test_", call.Form["discounts[0][coupon]"]);

        // What Stripe would charge: the lines minus the coupon — exactly the order's total.
        long lines = 0;
        for (var i = 0; call.Form.ContainsKey($"line_items[{i}][quantity]"); i++)
            lines += long.Parse(call.Form[$"line_items[{i}][price_data][unit_amount]"]) * long.Parse(call.Form[$"line_items[{i}][quantity]"]);
        Assert.Equal(order.TotalCents, lines - long.Parse(coupon.Form["amount_off"]));

        // And a payment of that amount is accepted.
        await _shop.WebhookAsync(_app.Browser(), "checkout.session.completed", ShopApp.PaidSession(session, order));
        Assert.Equal(OrderStatus.Paid, (await _shop.OrderAsync(order.Number))!.Status);

        // An order without a coupon creates none at Stripe.
        var before = _shop.Stripe.Calls.Count(c => c.Path == "/v1/coupons");
        await OrderViaStripeAsync("Stripe Ohne Gutschein", 1000);
        Assert.Equal(before, _shop.Stripe.Calls.Count(c => c.Path == "/v1/coupons"));
    }
}

[CollectionDefinition("shop-stripe", DisableParallelization = true)]
public sealed class ShopStripeCollection;
