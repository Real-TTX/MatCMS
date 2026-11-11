using System.Globalization;
using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>What starting a payment answered: send the customer somewhere (<see cref="RedirectUrl"/>), or it failed (<see cref="Error"/>), or neither — the order page tells how to pay.</summary>
internal sealed record PaymentStart(string? RedirectUrl, string? Error);

/// <summary>A way to pay the checkout offers.</summary>
internal sealed record PayMethod(string Id, string Label, string Hint);

/// <summary>
/// The payment providers. Stripe and bank transfer are built in; any other plugin can add a method through two events — <c>shop.payment.methods</c> (answer a list of
/// <c>{id,label,hint}</c>) and <c>shop.payment.start</c> (answer <c>{redirect}</c> or <c>{error}</c> for a method id of its own) — and report a payment later with
/// <c>shop.payment.paid</c>. The built-ins sit behind the same two questions, so PayPal is one more provider and not a change of the shop.
/// </summary>
internal static class Payments
{
    public const string Stripe = "stripe";
    public const string PayPal = "paypal";
    public const string Bank = "bank";
    private const string External = "x:";

    public static async Task<IReadOnlyList<PayMethod>> MethodsAsync(IPluginContext ctx, ShopConfig cfg, long totalCents, CancellationToken ct = default)
    {
        var list = new List<PayMethod>();
        if (cfg.StripeReady) list.Add(new PayMethod(Stripe, ctx.T("shop.pay.stripe"), ctx.T("shop.pay.stripeHint")));
        if (cfg.PayPalReady) list.Add(new PayMethod(PayPal, ctx.T("shop.pay.paypal"), ctx.T("shop.pay.paypalHint")));
        if (cfg.BankEnabled) list.Add(new PayMethod(Bank, ctx.T("shop.pay.bank"), ctx.T("shop.pay.bankHint")));
        foreach (var answer in await ctx.Events.CollectAsync("shop.payment.methods", new JsonObject { ["total"] = totalCents, ["currency"] = cfg.Currency }, ct))
            foreach (var m in answer is JsonArray arr ? arr.OfType<JsonObject>() : new[] { answer as JsonObject }.OfType<JsonObject>())
            {
                var id = m["id"]?.GetValue<string>() ?? "";
                if (id.Length is 0 or > 60) continue;
                list.Add(new PayMethod(External + id, m["label"]?.GetValue<string>() ?? id, m["hint"]?.GetValue<string>() ?? ""));
            }
        return list;
    }

    public static async Task<PaymentStart> StartAsync(IPluginContext ctx, ShopConfig cfg, Order order, IReadOnlyList<OrderItem> items, string method, CancellationToken ct = default)
    {
        switch (method)
        {
            case Stripe: return await StripePayments.StartAsync(ctx, cfg, order, items, ct);
            case PayPal: return await PayPalPayments.StartAsync(ctx, cfg, order, items, ct);
            case Bank: return await BankAsync(ctx, cfg, order, items, ct);
        }
        if (!method.StartsWith(External, StringComparison.Ordinal)) return new PaymentStart(null, "unknown");
        var payload = new JsonObject
        {
            ["method"] = method[External.Length..], ["order"] = order.Number, ["total"] = order.TotalCents, ["currency"] = order.Currency, ["email"] = order.Email,
            ["return_url"] = $"{ctx.Site.Origin}/shop/order/{Uri.EscapeDataString(order.Number)}?t={Uri.EscapeDataString(order.AccessToken)}"
        };
        foreach (var answer in await ctx.Events.CollectAsync("shop.payment.start", payload, ct))
        {
            if (answer is not JsonObject o) continue;
            if (o["error"]?.GetValue<string>() is { Length: > 0 } err) return new PaymentStart(null, err);
            if (o["redirect"]?.GetValue<string>() is { Length: > 0 } url) return new PaymentStart(url, null);
        }
        return new PaymentStart(null, null);
    }

    /// <summary>Bank transfer: nothing to start. The order waits; the customer gets the account details by mail and on the order page, and the operator marks it paid.</summary>
    private static async Task<PaymentStart> BankAsync(IPluginContext ctx, ShopConfig cfg, Order order, IReadOnlyList<OrderItem> items, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        await ShopMail.SendAsync(ctx, store, ShopMail.Placed, order, items, v => v["payment_info"] = BankInstructions(ctx, cfg, order), ct: ct);
        return new PaymentStart(null, null);
    }

    /// <summary>The instructions for paying by transfer: the operator's account details and the reference to put on it.</summary>
    public static string BankInstructions(IPluginContext ctx, ShopConfig cfg, Order order)
    {
        var en = order.Locale == "en";
        var head = en
            ? $"Please transfer {Money.Format(order.TotalCents, order.Currency, order.Locale)} to the following account, quoting the order number {order.Number}. We ship as soon as the money has arrived."
            : $"Bitte überweisen Sie {Money.Format(order.TotalCents, order.Currency, order.Locale)} auf das folgende Konto und geben Sie die Bestellnummer {order.Number} als Verwendungszweck an. Wir liefern, sobald das Geld eingegangen ist.";
        return head + (cfg.BankDetails.Length > 0 ? "\n\n" + cfg.BankDetails : "");
    }

    /// <summary>The line items of an order as Stripe wants them: every order line, then shipping — and the sum is exactly the order's total, so Stripe's amount can be checked against ours.</summary>
    public static string Currency(Order o) => o.Currency.ToLowerInvariant();
}

/// <summary>Stripe Checkout: the customer pays on Stripe's page — card data never touches this server.</summary>
internal static class StripePayments
{
    /// <summary>Creates the Checkout Session of an order and returns where to send the customer.</summary>
    public static async Task<PaymentStart> StartAsync(IPluginContext ctx, ShopConfig cfg, Order order, IReadOnlyList<OrderItem> items, CancellationToken ct = default, string attempt = "")
    {
        var client = new StripeClient(cfg.StripeSecret);
        var back = $"{ctx.Site.Origin}/shop/order/{Uri.EscapeDataString(order.Number)}?t={Uri.EscapeDataString(order.AccessToken)}";
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "payment"),
            // Stripe puts the real session id in place of the placeholder; the order page uses it to ask Stripe if the notification is late.
            new("success_url", back + "&paid=1&session_id={CHECKOUT_SESSION_ID}"), new("cancel_url", back + "&cancelled=1"),
            new("client_reference_id", order.Number), new("customer_email", order.Email),
            new("metadata[order_number]", order.Number), new("metadata[order_id]", order.Id.ToString(CultureInfo.InvariantCulture)),
            new("payment_intent_data[description]", $"{ctx.Site.Name} – {order.Number}"), new("payment_intent_data[metadata][order_number]", order.Number),
            new("locale", order.Locale == "en" ? "en" : "de"),
            new("expires_at", DateTimeOffset.UtcNow.AddMinutes(31).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
        };
        var n = 0;
        void Line(string name, long unit, int qty)
        {
            var p = $"line_items[{n++}]";
            form.Add(new($"{p}[price_data][currency]", Payments.Currency(order)));
            form.Add(new($"{p}[price_data][unit_amount]", unit.ToString(CultureInfo.InvariantCulture)));
            form.Add(new($"{p}[price_data][product_data][name]", name.Length > 250 ? name[..250] : name));
            form.Add(new($"{p}[quantity]", qty.ToString(CultureInfo.InvariantCulture)));
        }
        foreach (var i in items) Line(i.DisplayTitle, i.UnitCents, i.Qty);
        if (order.ShippingCents > 0) Line(string.IsNullOrWhiteSpace(order.ShippingMethod) ? ctx.T("shop.shipping") : ctx.T("shop.shippingNamed", order.ShippingMethod), order.ShippingCents, 1);

        try
        {
            // Stripe takes no negative lines: a coupon's discount goes in as a one-time Stripe coupon of exactly that amount, so Stripe's total is the order's total.
            if (order.DiscountCents > 0)
            {
                var name = ctx.T("shop.coupon.line", order.CouponCode.Length > 0 ? order.CouponCode : "");
                var coupon = await client.PostAsync("/v1/coupons", new[]
                {
                    new KeyValuePair<string, string>("amount_off", order.DiscountCents.ToString(CultureInfo.InvariantCulture)), new KeyValuePair<string, string>("currency", Payments.Currency(order)),
                    new KeyValuePair<string, string>("duration", "once"), new KeyValuePair<string, string>("max_redemptions", "1"),
                    new KeyValuePair<string, string>("name", name.Length > 40 ? name[..40] : name), new KeyValuePair<string, string>("metadata[order_number]", order.Number)
                }, "coupon-" + order.Number + (attempt.Length > 0 ? "-" + attempt : ""), ct);
                form.Add(new("discounts[0][coupon]", coupon["id"]?.GetValue<string>() ?? ""));
            }
            var session = await client.PostAsync("/v1/checkout/sessions", form, "order-" + order.Number + (attempt.Length > 0 ? "-" + attempt : ""), ct);
            var id = session["id"]?.GetValue<string>() ?? "";
            var url = session["url"]?.GetValue<string>() ?? "";
            if (id.Length == 0 || url.Length == 0) return new PaymentStart(null, ctx.T("shop.err.payment"));
            var store = new ShopStore(ctx.Data);
            await store.UpdateOrderAsync(order.Id, new Dictionary<string, object?> { ["payment_ref"] = id }, ct);
            await store.AddPaymentAsync(order.Id, Payments.Stripe, id, order.TotalCents, order.Currency, "created", ct);
            await store.AddEventAsync(order.Id, "payment", "Stripe Checkout gestartet" + (cfg.StripeTestMode ? " (Testmodus)" : ""), "system", null, ct);
            return new PaymentStart(url, null);
        }
        catch (StripeException ex)
        {
            ctx.Log($"Stripe Checkout für {order.Number}: {ex.Message}");
            await new ShopStore(ctx.Data).AddEventAsync(order.Id, "payment-failed", "Stripe: " + ex.Message, "system", null, ct);
            return new PaymentStart(null, ctx.T("shop.err.payment"));
        }
    }

    /// <summary>The customer wants to pay an order again (closed the tab, a failed card): a NEW session, and the old one is closed first so the order can never be paid twice.</summary>
    public static async Task<PaymentStart> RestartAsync(IPluginContext ctx, ShopConfig cfg, Order order, IReadOnlyList<OrderItem> items, CancellationToken ct = default)
    {
        var client = new StripeClient(cfg.StripeSecret);
        if (order.PaymentRef.Length > 0)
        {
            try
            {
                var old = await client.GetAsync("/v1/checkout/sessions/" + Uri.EscapeDataString(order.PaymentRef), ct);
                if (old["status"]?.GetValue<string>() == "complete") { await ApplySessionAsync(ctx, cfg, new ShopStore(ctx.Data), old, ct); return new PaymentStart(null, ctx.T("shop.err.alreadyPaid")); }
                if (old["status"]?.GetValue<string>() == "open") await client.PostAsync("/v1/checkout/sessions/" + Uri.EscapeDataString(order.PaymentRef) + "/expire", Array.Empty<KeyValuePair<string, string>>(), null, ct);
            }
            catch (StripeException ex) { ctx.Log($"Alte Sitzung von {order.Number}: {ex.Message}"); }
        }
        // A fresh idempotency key: the first session's key would hand back that very (now closed) session.
        return await StartAsync(ctx, cfg, order, items, ct, attempt: Guid.NewGuid().ToString("N")[..8]);
    }

    // ---- notifications -----------------------------------------------------------------------------------

    /// <summary>The route handler of <c>POST /shop/webhook/stripe</c>.</summary>
    public static async Task<PluginResult> WebhookAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        if (!cfg.StripeEnabled || cfg.StripeWebhookSecret.Length == 0)
            return PluginResult.Text("Stripe is not set up", status: 503);                       // Stripe retries; the operator sees the failures in the Stripe dashboard

        if (!StripeClient.VerifySignature(ctx.Request.Header("Stripe-Signature"), ctx.Request.Body, cfg.StripeWebhookSecret, DateTimeOffset.UtcNow))
        {
            ctx.Log("Stripe-Webhook mit ungültiger Signatur abgewiesen");
            return PluginResult.Text("invalid signature", status: 400);
        }

        JsonObject? evt;
        try { evt = JsonNode.Parse(ctx.Request.Body) as JsonObject; } catch (System.Text.Json.JsonException) { evt = null; }
        var eventId = evt?["id"]?.GetValue<string>() ?? "";
        var type = evt?["type"]?.GetValue<string>() ?? "";
        var obj = evt?["data"]?["object"] as JsonObject;
        if (evt is null || eventId.Length == 0 || obj is null) return PluginResult.Text("bad event", status: 400);

        var store = new ShopStore(ctx.Data);
        if (await store.HasSeenWebhookAsync(eventId, ct)) return PluginResult.Text("duplicate");
        try
        {
            await HandleAsync(ctx, cfg, store, type, obj, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not recorded as seen: a 500 makes Stripe deliver the event again, and handling is idempotent.
            ctx.Log($"Stripe-Ereignis {type} {eventId}: {ex.Message}");
            return PluginResult.Text("error", status: 500);
        }
        await store.TryRecordWebhookAsync(eventId, Payments.Stripe, ct);
        return PluginResult.Text("ok");
    }

    private static async Task HandleAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, string type, JsonObject obj, CancellationToken ct)
    {
        switch (type)
        {
            case "checkout.session.completed":
            case "checkout.session.async_payment_succeeded":
                await ApplySessionAsync(ctx, cfg, store, obj, ct); break;
            case "checkout.session.expired":
            case "checkout.session.async_payment_failed":
            {
                var order = await OrderOfSessionAsync(store, obj, ct);
                if (order is not null)
                    await ShopOrders.CancelAsync(ctx, cfg, order.Id, type.EndsWith("failed", StringComparison.Ordinal) ? "payment_failed" : "expired", "stripe",
                        type.EndsWith("failed", StringComparison.Ordinal) ? "Zahlung fehlgeschlagen" : "Stripe-Sitzung abgelaufen, nicht bezahlt", ct: ct);
                break;
            }
            case "charge.refunded":
            {
                var order = await store.OrderByPaymentIntentAsync(obj["payment_intent"]?.GetValue<string>() ?? "", ct);
                if (order is not null && obj["amount_refunded"] is JsonValue refunded && refunded.TryGetValue<long>(out var total))
                    await ShopOrders.ApplyRefundAsync(ctx, order.Id, total, "stripe", "Erstattung bei Stripe", ct: ct);
                break;
            }
        }
    }

    private static async Task<Order?> OrderOfSessionAsync(ShopStore store, JsonObject session, CancellationToken ct)
    {
        var order = await store.OrderByPaymentRefAsync(session["id"]?.GetValue<string>() ?? "", ct);
        if (order is null && session["client_reference_id"]?.GetValue<string>() is { Length: > 0 } number) order = await store.OrderByNumberAsync(number, ct);
        return order;
    }

    /// <summary>
    /// A Checkout Session says what happened to an order. Paid and for exactly the order's amount in its currency → the order is paid. Paid but for another amount is NOT
    /// accepted — the history gets a warning for a human instead — because "the session id matches" is not the same as "the customer paid what the order costs".
    /// </summary>
    internal static async Task ApplySessionAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, JsonObject session, CancellationToken ct)
    {
        var order = await OrderOfSessionAsync(store, session, ct);
        if (order is null) { ctx.Log("Stripe-Sitzung ohne passende Bestellung: " + (session["id"]?.GetValue<string>() ?? "?")); return; }
        var status = session["payment_status"]?.GetValue<string>() ?? "";
        var intent = session["payment_intent"]?.GetValue<string>() ?? "";
        if (status is "paid" or "no_payment_required")
        {
            var amount = session["amount_total"] is JsonValue a && a.TryGetValue<long>(out var cents) ? cents : -1;
            var currency = (session["currency"]?.GetValue<string>() ?? "").ToLowerInvariant();
            if (amount != order.TotalCents || currency != Payments.Currency(order))
            {
                await store.AddEventAsync(order.Id, "warning",
                    $"Stripe meldet Zahlung über {amount} {currency}, die Bestellung kostet {order.TotalCents} {Payments.Currency(order)} — nicht als bezahlt übernommen.", "stripe", null, ct);
                return;
            }
            await ShopOrders.MarkPaidAsync(ctx, cfg, order.Id, "stripe", "Zahlung per Stripe erhalten", session["id"]?.GetValue<string>(), intent, ct);
        }
        else if (order.Status == OrderStatus.AwaitingPayment)
        {
            // A payment method that settles later (a direct debit): the session is complete, the money is not. Hold the goods for as long as that takes.
            await store.UpdateOrderAsync(order.Id, new Dictionary<string, object?> { ["reserved_until"] = DateTime.UtcNow.AddDays(7), ["payment_intent"] = intent }, ct);
            await store.AddEventAsync(order.Id, "payment", "Stripe: Zahlung wurde veranlasst, das Geld ist noch nicht da (verzögerte Zahlungsart)", "stripe", null, ct);
        }
    }

    /// <summary>Asks Stripe what became of an order's session — for a customer who came back before the notification did, and for the hourly check of orders still waiting.</summary>
    public static async Task ReconcileAsync(IPluginContext ctx, ShopConfig cfg, Order order, CancellationToken ct = default)
    {
        if (!cfg.StripeReady || order.PaymentProvider != Payments.Stripe || order.PaymentRef.Length == 0 || order.Status != OrderStatus.AwaitingPayment) return;
        var store = new ShopStore(ctx.Data);
        try
        {
            var session = await new StripeClient(cfg.StripeSecret).GetAsync("/v1/checkout/sessions/" + Uri.EscapeDataString(order.PaymentRef), ct);
            var state = session["status"]?.GetValue<string>() ?? "";
            if (state == "complete") await ApplySessionAsync(ctx, cfg, store, session, ct);
            else if (state == "expired") await ShopOrders.CancelAsync(ctx, cfg, order.Id, "expired", "stripe", "Stripe-Sitzung abgelaufen, nicht bezahlt", ct: ct);
        }
        catch (StripeException ex) { ctx.Log($"Abgleich {order.Number}: {ex.Message}"); }
    }

    // ---- refund ------------------------------------------------------------------------------------------

    /// <summary>Refunds <paramref name="amountCents"/> through Stripe and records what Stripe says has been refunded in total. Throws <see cref="StripeException"/> with Stripe's reason.</summary>
    public static async Task RefundAsync(IPluginContext ctx, ShopConfig cfg, Order order, long amountCents, string actor, bool restock, CancellationToken ct = default)
    {
        if (order.PaymentIntent.Length == 0) throw new StripeException("Zu dieser Bestellung ist keine Stripe-Zahlung bekannt.", 0, "no_payment_intent");
        var client = new StripeClient(cfg.StripeSecret);
        // The key names the order, the amount already refunded and the amount: the same click twice is one refund, the next partial refund is another.
        await client.PostAsync("/v1/refunds", new[]
        {
            new KeyValuePair<string, string>("payment_intent", order.PaymentIntent),
            new KeyValuePair<string, string>("amount", amountCents.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("metadata[order_number]", order.Number)
        }, $"refund-{order.Number}-{order.RefundedCents}-{amountCents}", ct);
        var pi = await client.GetAsync("/v1/payment_intents/" + Uri.EscapeDataString(order.PaymentIntent) + "?expand[]=latest_charge", ct);
        var refunded = pi["latest_charge"]?["amount_refunded"] is JsonValue r && r.TryGetValue<long>(out var n) ? n : order.RefundedCents + amountCents;
        await ShopOrders.ApplyRefundAsync(ctx, order.Id, refunded, actor, "Erstattung über Stripe", restock, ct);
    }
}
