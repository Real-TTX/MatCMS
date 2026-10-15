using System.Net;
using System.Text.Json.Nodes;
using MatCMS.Plugin.Shop;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// Payment by PayPal Checkout, against a stand-in for PayPal that keeps the state of its orders: what the shop asks PayPal for, what it does when the customer comes back or
/// PayPal calls — and what it refuses to do (a forged notification, a payment for the wrong amount, another customer's PayPal order named in an address).
/// </summary>
[Collection("shop-paypal")]
public class ShopPayPalTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopPayPalTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }

    public async Task InitializeAsync()
    {
        _shop = await ShopApp.StartAsync(_app, paypal: true);
        PayPalClient.HandlerFactory = () => _shop.PayPal;
        PayPalClient.ForgetTokens();
        _shop.PayPal.FailNextCreateWith = null; _shop.PayPal.FailCaptureWith = null; _shop.PayPal.FailRefundWith = null;
        _shop.PayPal.VerifyStatus = "SUCCESS"; _shop.PayPal.CaptureValueOverride = null; _shop.PayPal.CaptureStatus = "COMPLETED";
    }

    public Task DisposeAsync() { PayPalClient.HandlerFactory = null; return Task.CompletedTask; }

    private static string Decode(string html) => WebUtility.HtmlDecode(html);
    private static string Slug(string title) => MatCMS.Pages.Admin.Pages.IndexModel.Slugify(title);

    private sealed record Placed(Order Order, string PayPalId, HttpClient Visitor);

    /// <summary>Puts a product in a cart and pays with PayPal: returns the order and the PayPal order it got.</summary>
    private async Task<Placed> OrderViaPayPalAsync(string title, long price, int qty = 1, string? coupon = null, string kind = ProductKind.Physical)
    {
        await _shop.AddProductAsync(title, price, kind);
        var visitor = _app.Browser();
        await _shop.AddToCartAsync(visitor, Slug(title), qty);
        if (coupon is not null) await _shop.ApplyCouponAsync(visitor, coupon);
        var res = await _shop.CheckoutAsync(visitor, pay: "paypal", waiver: kind == ProductKind.Digital);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var loc = res.Headers.Location!.OriginalString;
        Assert.StartsWith("https://www.sandbox.paypal.com/checkoutnow?token=PP-ORDER-", loc);
        var id = loc[(loc.IndexOf("token=", StringComparison.Ordinal) + 6)..];
        var create = _shop.PayPal.Calls.Last(c => c.Path == "/v2/checkout/orders" && c.Method == HttpMethod.Post);
        var order = (await _shop.OrderAsync(create.Json!["purchase_units"]![0]!["custom_id"]!.GetValue<string>()))!;
        return new Placed(order, id, visitor);
    }

    private static string ReturnUrl(Order o, string extra = "&paypal=1") => $"/shop/order/{o.Number}?t={o.AccessToken}{extra}";

    private Task<Order> FreshAsync(Order o) => _shop.OrderAsync(o.Number)!.ContinueWith(t => t.Result!);

    // ---- starting ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Paying_with_paypal_creates_a_paypal_order_whose_amounts_add_up_and_sends_the_customer_there()
    {
        var p = await OrderViaPayPalAsync("PayPal Lampe", 2000, 2);
        var create = _shop.PayPal.Calls.Last(c => c.Path == "/v2/checkout/orders" && c.Method == HttpMethod.Post);
        var json = create.Json!;
        var unit = json["purchase_units"]![0]!;

        Assert.Equal("CAPTURE", json["intent"]!.GetValue<string>());
        Assert.Equal("EUR", unit["amount"]!["currency_code"]!.GetValue<string>());
        Assert.Equal("44.90", unit["amount"]!["value"]!.GetValue<string>());               // 2 × 20,00 + 4,90 shipping
        Assert.Equal("40.00", unit["amount"]!["breakdown"]!["item_total"]!["value"]!.GetValue<string>());
        Assert.Equal("4.90", unit["amount"]!["breakdown"]!["shipping"]!["value"]!.GetValue<string>());
        Assert.Null(unit["amount"]!["breakdown"]!["discount"]);
        var item = unit["items"]![0]!;
        Assert.Equal("PayPal Lampe", item["name"]!.GetValue<string>());
        Assert.Equal("2", item["quantity"]!.GetValue<string>());
        Assert.Equal("20.00", item["unit_amount"]!["value"]!.GetValue<string>());
        Assert.Equal("PHYSICAL_GOODS", item["category"]!.GetValue<string>());
        Assert.Equal(p.Order.Number, unit["custom_id"]!.GetValue<string>());
        var ctxt = json["payment_source"]!["paypal"]!["experience_context"]!;
        Assert.Equal("NO_SHIPPING", ctxt["shipping_preference"]!.GetValue<string>());      // the shop has the address already
        Assert.Equal("PAY_NOW", ctxt["user_action"]!.GetValue<string>());
        Assert.Contains($"/shop/order/{p.Order.Number}?t=", ctxt["return_url"]!.GetValue<string>());
        Assert.EndsWith("&paypal=1", ctxt["return_url"]!.GetValue<string>());
        Assert.EndsWith("&cancelled=1", ctxt["cancel_url"]!.GetValue<string>());
        Assert.Equal("order-" + p.Order.Number, create.Headers["PayPal-Request-Id"]);

        Assert.Equal(OrderStatus.AwaitingPayment, p.Order.Status);
        Assert.Equal(Payments.PayPal, p.Order.PaymentProvider);
        Assert.Equal(p.PayPalId, p.Order.PaymentRef);
        Assert.Equal(4490, p.Order.TotalCents);

        // One access token serves the next order too (it lives for hours).
        var tokens = _shop.PayPal.TokenCalls;
        await OrderViaPayPalAsync("PayPal Zweite", 1000);
        Assert.Equal(tokens, _shop.PayPal.TokenCalls);
    }

    [Fact]
    public async Task A_coupon_is_a_discount_in_the_breakdown_so_paypals_total_is_the_orders_total()
    {
        await _shop.AddCouponAsync("PAYPAL5", CouponKind.Fixed, 500);
        var p = await OrderViaPayPalAsync("PayPal Gutschein", 2000, 2, coupon: "PAYPAL5");
        var amount = _shop.PayPal.Calls.Last(c => c.Path == "/v2/checkout/orders").Json!["purchase_units"]![0]!["amount"]!;
        Assert.Equal("5.00", amount["breakdown"]!["discount"]!["value"]!.GetValue<string>());
        Assert.Equal("44.90".Replace("44.90", "39.90"), amount["value"]!.GetValue<string>());      // 40,00 − 5,00 + 4,90
        Assert.Equal(3990, p.Order.TotalCents);
        decimal V(string name) => decimal.Parse(amount["breakdown"]![name]!["value"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(decimal.Parse(amount["value"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture), V("item_total") + V("shipping") - V("discount"));
    }

    [Fact]
    public async Task If_paypal_refuses_to_start_the_order_is_cancelled_and_the_buyer_is_told()
    {
        await _shop.AddProductAsync("PayPal Fehler", 1000, trackStock: true, stock: 3);
        var visitor = _app.Browser();
        await _shop.AddToCartAsync(visitor, "paypal-fehler");
        _shop.PayPal.FailNextCreateWith = "Currency not supported.";
        var res = await _shop.CheckoutAsync(visitor, pay: "paypal");
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
        Assert.Contains("Zahlung", Decode(await res.Content.ReadAsStringAsync()));
        var order = (await _shop.WithContextAsync((_, s) => s.OrdersAsync(OrderStatus.Cancelled, take: 1))).Items.First();
        Assert.Equal("payment_failed", order.CancelReason);
        Assert.Contains(await _shop.EventsAsync(order.Id), e => e.Kind == "payment-failed" && e.Text.Contains("Currency not supported."));
        Assert.Equal(3, (await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("paypal-fehler")))!.Stock);     // the reserved piece is back
    }

    [Fact]
    public async Task PayPal_is_offered_at_checkout_when_it_is_set_up()
    {
        await _shop.AddProductAsync("PayPal Angebot", 1000);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "paypal-angebot");
        var html = Decode(await c.GetStringAsync("/shop/checkout"));
        Assert.Contains("value=\"paypal\"", html);
        Assert.Contains("weitergeleitet", html);
        Assert.False(new ShopConfig { PayPalEnabled = true, PayPalClientId = "abcdefgh", PayPalSecret = "" }.PayPalReady);
        Assert.False(new ShopConfig { PayPalEnabled = false, PayPalClientId = "abcdefgh", PayPalSecret = "abcdefgh" }.PayPalReady);
        Assert.True(new ShopConfig { PayPalEnabled = true, PayPalClientId = "abcdefgh", PayPalSecret = "abcdefgh" }.PayPalReady);
    }

    // ---- coming back ------------------------------------------------------------------------------------

    [Fact]
    public async Task Coming_back_approved_captures_the_payment_and_pays_the_order_exactly_once()
    {
        var p = await OrderViaPayPalAsync("PayPal Rückkehr", 1500);
        _shop.PayPal.Approve(p.PayPalId);

        var page = Decode(await p.Visitor.GetStringAsync(ReturnUrl(p.Order)));
        var paid = await FreshAsync(p.Order);
        Assert.Equal(OrderStatus.Paid, paid.Status);
        Assert.Equal(p.PayPalId, paid.PaymentRef);
        Assert.Matches(@"^CAP-\d+$", paid.PaymentIntent);                                  // the capture is what a refund needs
        Assert.Matches(@"^RE-\d{5}$", paid.InvoiceNumber);
        Assert.NotNull(paid.PaidUtc);
        Assert.Contains("Zahlung erhalten", page);
        var capture = _shop.PayPal.Calls.Single(c => c.Path == $"/v2/checkout/orders/{p.PayPalId}/capture");
        Assert.Equal("capture-" + p.Order.Number, capture.Headers["PayPal-Request-Id"]);

        await p.Visitor.GetStringAsync(ReturnUrl(p.Order));                                 // reload: nothing more happens
        Assert.Single(_shop.PayPal.Calls, c => c.Path.EndsWith("/capture") && c.Path.Contains(p.PayPalId));
        Assert.Single((await _shop.EventsAsync(p.Order.Id)).Where(e => e.Kind == "paid"));
        Assert.Equal(1, await _shop.WithContextAsync((ctx, _) => ctx.Data.Table("invoices").CountAsync(DataQuery.Everything.Eq("order_id", p.Order.Id))));
    }

    [Fact]
    public async Task Coming_back_without_approving_takes_nothing_and_the_order_stays_open()
    {
        var p = await OrderViaPayPalAsync("PayPal Abbruch", 1500);
        await p.Visitor.GetStringAsync(ReturnUrl(p.Order, "&cancelled=1"));
        await p.Visitor.GetStringAsync(ReturnUrl(p.Order));                                 // back without having approved
        Assert.Equal(OrderStatus.AwaitingPayment, (await FreshAsync(p.Order)).Status);
        Assert.DoesNotContain(_shop.PayPal.Calls, c => c.Path.EndsWith("/capture") && c.Path.Contains(p.PayPalId));
    }

    [Fact]
    public async Task The_address_cannot_name_another_paypal_order_to_pay_this_one()
    {
        var a = await OrderViaPayPalAsync("PayPal Eigene A", 1500);
        var b = await OrderViaPayPalAsync("PayPal Fremde B", 1500);
        _shop.PayPal.Approve(b.PayPalId);                                                    // somebody else's approved order
        await a.Visitor.GetStringAsync(ReturnUrl(a.Order, $"&paypal=1&token={b.PayPalId}&PayerID=EVIL"));
        Assert.Equal(OrderStatus.AwaitingPayment, (await FreshAsync(a.Order)).Status);      // A asked about ITS OWN PayPal order, which is not approved
        Assert.Equal(OrderStatus.AwaitingPayment, (await FreshAsync(b.Order)).Status);      // and B was not touched either
        Assert.DoesNotContain(_shop.PayPal.Calls, c => c.Path == $"/v2/checkout/orders/{b.PayPalId}/capture");
    }

    [Fact]
    public async Task A_capture_for_the_wrong_amount_is_not_accepted_and_a_human_is_told()
    {
        var p = await OrderViaPayPalAsync("PayPal Betrag", 1500);
        _shop.PayPal.Approve(p.PayPalId);
        _shop.PayPal.CaptureValueOverride = "1.00";
        await p.Visitor.GetStringAsync(ReturnUrl(p.Order));
        Assert.Equal(OrderStatus.AwaitingPayment, (await FreshAsync(p.Order)).Status);
        Assert.Contains(await _shop.EventsAsync(p.Order.Id), e => e.Kind == "warning" && e.Text.Contains("nicht als bezahlt übernommen"));
    }

    [Fact]
    public async Task A_declined_capture_is_written_down_and_the_customer_can_pay_again_with_a_new_paypal_order()
    {
        var p = await OrderViaPayPalAsync("PayPal Abgelehnt", 1500);
        _shop.PayPal.Approve(p.PayPalId);
        _shop.PayPal.FailCaptureWith = "Instrument declined.";
        Assert.Equal(HttpStatusCode.OK, (await p.Visitor.GetAsync(ReturnUrl(p.Order))).StatusCode);
        Assert.Contains(await _shop.EventsAsync(p.Order.Id), e => e.Kind == "payment-failed" && e.Text.Contains("Instrument declined."));
        Assert.Equal(OrderStatus.AwaitingPayment, (await FreshAsync(p.Order)).Status);

        // "Jetzt bezahlen": a NEW PayPal order (the first one is spoiled), the old one is asked about first.
        _shop.PayPal.FailCaptureWith = null;
        _shop.PayPal.Orders[p.PayPalId].Status = "PAYER_ACTION_REQUIRED";
        var page = await p.Visitor.GetStringAsync(ReturnUrl(p.Order, ""));
        var retry = await p.Visitor.PostAsync($"/shop/order/{p.Order.Number}?t={p.Order.AccessToken}", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "retry" }));
        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
        var again = retry.Headers.Location!.OriginalString;
        Assert.StartsWith("https://www.sandbox.paypal.com/checkoutnow?token=PP-ORDER-", again);
        Assert.NotEqual(p.PayPalId, again[(again.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        var created = _shop.PayPal.Calls.Where(c => c.Path == "/v2/checkout/orders" && c.Method == HttpMethod.Post).Last();
        Assert.StartsWith("order-" + p.Order.Number + "-", created.Headers["PayPal-Request-Id"]);   // a fresh key: the first one would hand the spoiled order back
        Assert.Equal(again[(again.IndexOf("token=", StringComparison.Ordinal) + 6)..], (await FreshAsync(p.Order)).PaymentRef);
    }

    [Fact]
    public async Task Pay_again_after_the_money_was_already_approved_settles_the_first_payment_instead_of_charging_twice()
    {
        var p = await OrderViaPayPalAsync("PayPal Doppelt", 1500);
        _shop.PayPal.Approve(p.PayPalId);
        var page = await p.Visitor.GetStringAsync(ReturnUrl(p.Order, ""));                   // the customer approved, then went away without returning
        var retry = await p.Visitor.PostAsync($"/shop/order/{p.Order.Number}?t={p.Order.AccessToken}", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "retry" }));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);                                   // no new PayPal page: told it is paid
        Assert.Equal(OrderStatus.Paid, (await FreshAsync(p.Order)).Status);
        Assert.Single(_shop.PayPal.Calls.Where(c => c.Path == "/v2/checkout/orders" && c.Method == HttpMethod.Post && c.Json!["purchase_units"]![0]!["custom_id"]!.GetValue<string>() == p.Order.Number));
    }

    // ---- notifications -----------------------------------------------------------------------------------

    private static JsonObject OrderResource(string id) => new() { ["id"] = id, ["intent"] = "CAPTURE", ["status"] = "APPROVED" };

    private static JsonObject CaptureResource(Order order, string captureId, string paypalOrderId, string? value = null, string status = "COMPLETED") => new()
    {
        ["id"] = captureId, ["status"] = status, ["custom_id"] = order.Number,
        ["amount"] = new JsonObject { ["currency_code"] = order.Currency, ["value"] = value ?? PayPalPayments.Value(order.TotalCents) },
        ["supplementary_data"] = new JsonObject { ["related_ids"] = new JsonObject { ["order_id"] = paypalOrderId } }
    };

    [Fact]
    public async Task A_genuine_approved_notification_captures_for_a_customer_who_never_came_back()
    {
        var p = await OrderViaPayPalAsync("PayPal Webhook A", 1500);
        _shop.PayPal.Approve(p.PayPalId);
        var res = await _shop.PayPalWebhookAsync(_app.Browser(), "CHECKOUT.ORDER.APPROVED", OrderResource(p.PayPalId));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(OrderStatus.Paid, (await FreshAsync(p.Order)).Status);

        // PayPal itself was asked to vouch for it: the five headers, the webhook's id and the event.
        var verify = _shop.PayPal.Calls.Last(c => c.Path == "/v1/notifications/verify-webhook-signature").Json!;
        Assert.Equal(FakePayPal.WebhookId, verify["webhook_id"]!.GetValue<string>());
        Assert.Equal("SHA256withRSA", verify["auth_algo"]!.GetValue<string>());
        Assert.Equal("c2lnbmF0dXJl", verify["transmission_sig"]!.GetValue<string>());
        Assert.Equal("CHECKOUT.ORDER.APPROVED", verify["webhook_event"]!["event_type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_capture_completed_notification_pays_the_order_and_the_same_event_twice_changes_nothing()
    {
        var p = await OrderViaPayPalAsync("PayPal Webhook B", 1500);
        var resource = CaptureResource(p.Order, "CAP-WEBHOOK-1", p.PayPalId);
        var visitor = _app.Browser();
        Assert.Equal(HttpStatusCode.OK, (await _shop.PayPalWebhookAsync(visitor, "PAYMENT.CAPTURE.COMPLETED", resource, "WH-EVT-B1")).StatusCode);
        var paid = await FreshAsync(p.Order);
        Assert.Equal(OrderStatus.Paid, paid.Status);
        Assert.Equal("CAP-WEBHOOK-1", paid.PaymentIntent);
        Assert.Matches(@"^RE-\d{5}$", paid.InvoiceNumber);

        var again = await _shop.PayPalWebhookAsync(visitor, "PAYMENT.CAPTURE.COMPLETED", resource, "WH-EVT-B1");
        Assert.Equal("duplicate", await again.Content.ReadAsStringAsync());
        // A DIFFERENT event for an order that is paid already is not a second payment either.
        await _shop.PayPalWebhookAsync(visitor, "PAYMENT.CAPTURE.COMPLETED", resource, "WH-EVT-B2");
        Assert.Equal(paid.InvoiceNumber, (await FreshAsync(p.Order)).InvoiceNumber);
        Assert.Single((await _shop.EventsAsync(p.Order.Id)).Where(e => e.Kind == "paid"));
    }

    [Fact]
    public async Task A_notification_paypal_does_not_vouch_for_is_refused_and_so_is_one_from_a_certificate_that_is_not_paypals()
    {
        var p = await OrderViaPayPalAsync("PayPal Gefälscht", 1500);
        var resource = CaptureResource(p.Order, "CAP-FAKE", p.PayPalId);
        _shop.PayPal.VerifyStatus = "FAILURE";
        var bad = await _shop.PayPalWebhookAsync(_app.Browser(), "PAYMENT.CAPTURE.COMPLETED", resource);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(OrderStatus.AwaitingPayment, (await FreshAsync(p.Order)).Status);

        // A sender who names his own certificate address would be vouching for himself: refused before PayPal is even asked.
        _shop.PayPal.VerifyStatus = "SUCCESS";
        var before = _shop.PayPal.Calls.Count(c => c.Path == "/v1/notifications/verify-webhook-signature");
        var evt = new JsonObject { ["id"] = "WH-EVIL", ["event_type"] = "PAYMENT.CAPTURE.COMPLETED", ["resource"] = resource.DeepClone() };
        var req = new HttpRequestMessage(HttpMethod.Post, "/shop/webhook/paypal") { Content = new StringContent(evt.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add("PAYPAL-AUTH-ALGO", "SHA256withRSA"); req.Headers.Add("PAYPAL-CERT-URL", "https://evil.example/paypal.com/cert"); req.Headers.Add("PAYPAL-TRANSMISSION-ID", "x");
        req.Headers.Add("PAYPAL-TRANSMISSION-SIG", "y"); req.Headers.Add("PAYPAL-TRANSMISSION-TIME", "2026-01-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.BadRequest, (await _app.Browser().SendAsync(req)).StatusCode);
        Assert.Equal(before, _shop.PayPal.Calls.Count(c => c.Path == "/v1/notifications/verify-webhook-signature"));
        Assert.Equal(OrderStatus.AwaitingPayment, (await FreshAsync(p.Order)).Status);

        // Not an event at all.
        var empty = new HttpRequestMessage(HttpMethod.Post, "/shop/webhook/paypal") { Content = new StringContent("not json", System.Text.Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.BadRequest, (await _app.Browser().SendAsync(empty)).StatusCode);
    }

    [Fact]
    public async Task A_notification_for_the_wrong_amount_or_another_orders_number_is_not_accepted()
    {
        var p = await OrderViaPayPalAsync("PayPal Webhook Betrag", 1500);
        await _shop.PayPalWebhookAsync(_app.Browser(), "PAYMENT.CAPTURE.COMPLETED", CaptureResource(p.Order, "CAP-LOW", p.PayPalId, value: "1.00"));
        Assert.Equal(OrderStatus.AwaitingPayment, (await FreshAsync(p.Order)).Status);
        Assert.Contains(await _shop.EventsAsync(p.Order.Id), e => e.Kind == "warning" && e.Text.Contains("nicht als bezahlt übernommen"));

        var other = CaptureResource(p.Order, "CAP-OTHER", p.PayPalId); other["custom_id"] = "99999";
        await _shop.PayPalWebhookAsync(_app.Browser(), "PAYMENT.CAPTURE.COMPLETED", other);
        Assert.Equal(OrderStatus.AwaitingPayment, (await FreshAsync(p.Order)).Status);
    }

    [Fact]
    public async Task A_denied_or_pending_capture_and_a_chargeback_are_written_down()
    {
        var p = await OrderViaPayPalAsync("PayPal Ereignisse", 1500);
        var v = _app.Browser();
        await _shop.PayPalWebhookAsync(v, "PAYMENT.CAPTURE.DENIED", CaptureResource(p.Order, "CAP-D", p.PayPalId, status: "DENIED"));
        Assert.Contains(await _shop.EventsAsync(p.Order.Id), e => e.Kind == "payment-failed" && e.Text.Contains("DECLINED"));
        Assert.Equal(OrderStatus.AwaitingPayment, (await FreshAsync(p.Order)).Status);

        await _shop.PayPalWebhookAsync(v, "PAYMENT.CAPTURE.PENDING", CaptureResource(p.Order, "CAP-P", p.PayPalId, status: "PENDING"));
        var held = await FreshAsync(p.Order);
        Assert.Equal(OrderStatus.AwaitingPayment, held.Status);
        Assert.True(held.ReservedUntilUtc > DateTime.UtcNow.AddDays(6));                     // the goods are held while the money is on its way

        await _shop.PayPalWebhookAsync(v, "PAYMENT.CAPTURE.COMPLETED", CaptureResource(p.Order, "CAP-D", p.PayPalId));
        Assert.Equal(OrderStatus.Paid, (await FreshAsync(p.Order)).Status);
        await _shop.PayPalWebhookAsync(v, "PAYMENT.CAPTURE.REVERSED", CaptureResource(p.Order, "CAP-D", p.PayPalId));
        Assert.Contains(await _shop.EventsAsync(p.Order.Id), e => e.Kind == "warning" && e.Text.Contains("Rückbuchung"));
    }

    // ---- refunds ----------------------------------------------------------------------------------------

    private async Task<Placed> PaidViaPayPalAsync(string title, long price)
    {
        var p = await OrderViaPayPalAsync(title, price);
        _shop.PayPal.Approve(p.PayPalId);
        await p.Visitor.GetStringAsync(ReturnUrl(p.Order));
        return p with { Order = await FreshAsync(p.Order) };
    }

    private static JsonObject RefundResource(string refundId, string captureId, string value) => new()
    {
        ["id"] = refundId, ["status"] = "COMPLETED", ["amount"] = new JsonObject { ["currency_code"] = "EUR", ["value"] = value },
        ["links"] = new JsonArray(new JsonObject { ["rel"] = "up", ["href"] = "https://api.sandbox.paypal.com/v2/payments/captures/" + captureId }, new JsonObject { ["rel"] = "self", ["href"] = "https://api.sandbox.paypal.com/v2/payments/refunds/" + refundId })
    };

    [Fact]
    public async Task The_operator_refunds_through_paypal_and_the_notification_that_follows_counts_nothing_twice()
    {
        var p = await PaidViaPayPalAsync("PayPal Erstattung", 2000);
        var admin = await _shop.AdminAsync();
        var res = await _shop.AdminActionAsync(admin, p.Order.Id, "refund", new Dictionary<string, string> { ["amount"] = "5,00" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var call = _shop.PayPal.Calls.Last(c => c.Path.EndsWith("/refund"));
        Assert.Equal($"/v2/payments/captures/{p.Order.PaymentIntent}/refund", call.Path);
        Assert.Equal("5.00", call.Json!["amount"]!["value"]!.GetValue<string>());
        Assert.Equal("EUR", call.Json!["amount"]!["currency_code"]!.GetValue<string>());
        Assert.Equal($"refund-{p.Order.Number}-0-500", call.Headers["PayPal-Request-Id"]);
        var after = await FreshAsync(p.Order);
        Assert.Equal(500, after.RefundedCents);
        Assert.Equal(OrderStatus.Paid, after.Status);                                       // a part is not a full refund
        Assert.Single(await _shop.WithContextAsync((_, s) => s.CreditNotesOfOrderAsync(p.Order.Id)));   // and it is documented

        // PayPal then tells the shop about the very same refund: nothing is counted again.
        await _shop.PayPalWebhookAsync(_app.Browser(), "PAYMENT.CAPTURE.REFUNDED", RefundResource(_shop.PayPal.LastRefundId, p.Order.PaymentIntent, "5.00"));
        Assert.Equal(500, (await FreshAsync(p.Order)).RefundedCents);
        Assert.Single(await _shop.WithContextAsync((_, s) => s.CreditNotesOfOrderAsync(p.Order.Id)));

        // A refund the operator makes in PayPal's own dashboard arrives by notification only — and counts once, however often PayPal repeats it.
        var dash = RefundResource("RF-DASHBOARD-1", p.Order.PaymentIntent, "3.00");
        await _shop.PayPalWebhookAsync(_app.Browser(), "PAYMENT.CAPTURE.REFUNDED", dash, "WH-REF-1");
        await _shop.PayPalWebhookAsync(_app.Browser(), "PAYMENT.CAPTURE.REFUNDED", dash, "WH-REF-2");
        Assert.Equal(800, (await FreshAsync(p.Order)).RefundedCents);
        Assert.Equal(2, (await _shop.WithContextAsync((_, s) => s.CreditNotesOfOrderAsync(p.Order.Id))).Count);

        // The rest closes the order and blocks nothing it should not.
        var rest = (await FreshAsync(p.Order)).RefundableCents;
        await _shop.PayPalWebhookAsync(_app.Browser(), "PAYMENT.CAPTURE.REFUNDED", RefundResource("RF-DASHBOARD-2", p.Order.PaymentIntent, PayPalPayments.Value(rest)));
        var done = await FreshAsync(p.Order);
        Assert.Equal(OrderStatus.Refunded, done.Status);
        Assert.Equal(done.TotalCents, done.RefundedCents);
        Assert.Equal(done.TotalCents, (await _shop.WithContextAsync((_, s) => s.CreditNotesOfOrderAsync(p.Order.Id))).Sum(c => c.AmountCents));
    }

    [Fact]
    public async Task A_refund_paypal_refuses_changes_nothing_and_tells_the_operator_why()
    {
        var p = await PaidViaPayPalAsync("PayPal Abgelehnte Erstattung", 2000);
        _shop.PayPal.FailRefundWith = "Capture is fully refunded already.";
        var res = await _shop.AdminActionAsync(await _shop.AdminAsync(), p.Order.Id, "refund", new Dictionary<string, string> { ["amount"] = "5,00" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(0, (await FreshAsync(p.Order)).RefundedCents);
        Assert.Empty(await _shop.WithContextAsync((_, s) => s.CreditNotesOfOrderAsync(p.Order.Id)));
        var admin = await _shop.AdminAsync();
        var follow = Decode(await admin.GetStringAsync(res.Headers.Location!.OriginalString));
        Assert.Contains("Der Zahlungsanbieter hat die Erstattung abgelehnt", follow);
    }

    // ---- upkeep -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_upkeep_job_asks_paypal_about_orders_whose_customer_never_came_back_and_cancels_the_ones_nobody_approved()
    {
        var approved = await OrderViaPayPalAsync("PayPal Verloren", 1900);
        var abandoned = await OrderViaPayPalAsync("PayPal Verlassen", 1900);
        _shop.PayPal.Approve(approved.PayPalId);
        await _shop.WithContextAsync(async (_, s) => await s.UpdateOrderAsync(approved.Order.Id, new Dictionary<string, object?> { ["created"] = DateTime.UtcNow.AddMinutes(-20) }));
        await _shop.WithContextAsync((ctx, _) => ShopEvents.MaintenanceAsync(ctx, CancellationToken.None));
        Assert.Equal(OrderStatus.Paid, (await FreshAsync(approved.Order)).Status);
        Assert.Equal(OrderStatus.AwaitingPayment, (await FreshAsync(abandoned.Order)).Status);   // still within its time

        await _shop.WithContextAsync(async (_, s) => await s.UpdateOrderAsync(abandoned.Order.Id, new Dictionary<string, object?> { ["reserved_until"] = DateTime.UtcNow.AddMinutes(-5) }));
        await _shop.WithContextAsync((ctx, _) => ShopEvents.MaintenanceAsync(ctx, CancellationToken.None));
        Assert.Equal(OrderStatus.Cancelled, (await FreshAsync(abandoned.Order)).Status);

        // An order whose time ran out but which PayPal has approved meanwhile is paid, not cancelled.
        var late = await OrderViaPayPalAsync("PayPal Spät", 1900);
        _shop.PayPal.Approve(late.PayPalId);
        await _shop.WithContextAsync(async (_, s) => await s.UpdateOrderAsync(late.Order.Id, new Dictionary<string, object?> { ["reserved_until"] = DateTime.UtcNow.AddMinutes(-5) }));
        await _shop.WithContextAsync((ctx, _) => ShopEvents.MaintenanceAsync(ctx, CancellationToken.None));
        Assert.Equal(OrderStatus.Paid, (await FreshAsync(late.Order)).Status);
    }

    // ---- units ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(4490, "44.90")]
    [InlineData(5, "0.05")]
    [InlineData(100000, "1000.00")]
    [InlineData(0, "0.00")]
    public void An_amount_is_written_the_way_paypal_reads_it(long cents, string text) => Assert.Equal(text, PayPalPayments.Value(cents));

    [Fact]
    public void An_amount_from_paypal_is_read_back_exactly_and_nonsense_is_refused()
    {
        Assert.True(PayPalPayments.TryAmount(new JsonObject { ["currency_code"] = "eur", ["value"] = "44.90" }, out var cents, out var cur));
        Assert.Equal(4490, cents); Assert.Equal("EUR", cur);
        Assert.True(PayPalPayments.TryAmount(new JsonObject { ["currency_code"] = "EUR", ["value"] = "0.1" }, out cents, out _));
        Assert.Equal(10, cents);
        Assert.False(PayPalPayments.TryAmount(new JsonObject { ["currency_code"] = "EUR", ["value"] = "viel" }, out cents, out _));
        Assert.False(PayPalPayments.TryAmount(null, out cents, out _));
    }
}

[CollectionDefinition("shop-paypal", DisableParallelization = true)]
public sealed class ShopPayPalCollection;
