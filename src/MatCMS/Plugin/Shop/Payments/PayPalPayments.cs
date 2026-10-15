using System.Globalization;
using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// PayPal Checkout (Orders v2): the customer pays on PayPal's page, comes back, and the shop captures the payment. As with Stripe the order is paid by what PayPal says about the
/// ORDER'S OWN PayPal order — never by what the browser's address claims — and only when amount and currency are exactly the order's. A signed notification (checked by PayPal
/// itself) covers a customer who approved and then closed the tab.
/// </summary>
internal static class PayPalPayments
{
    private static PayPalClient Client(ShopConfig cfg) => new(cfg.PayPalClientId, cfg.PayPalSecret, cfg.PayPalSandbox);

    /// <summary>An amount the way PayPal writes it: <c>40.90</c>.</summary>
    internal static string Value(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Reads PayPal's <c>{ currency_code, value }</c> as cents.</summary>
    internal static bool TryAmount(JsonNode? amount, out long cents, out string currency)
    {
        cents = -1; currency = (amount?["currency_code"]?.GetValue<string>() ?? "").ToUpperInvariant();
        if (!decimal.TryParse(amount?["value"]?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) return false;
        cents = (long)Math.Round(v * 100m, MidpointRounding.AwayFromZero);
        return true;
    }

    private static JsonObject Money_(string currency, long cents) => new() { ["currency_code"] = currency, ["value"] = Value(cents) };

    // ---- starting ----------------------------------------------------------------------------------------

    public static async Task<PaymentStart> StartAsync(IPluginContext ctx, ShopConfig cfg, Order order, IReadOnlyList<OrderItem> items, CancellationToken ct = default, string attempt = "")
    {
        var back = $"{ctx.Site.Origin}/shop/order/{Uri.EscapeDataString(order.Number)}?t={Uri.EscapeDataString(order.AccessToken)}";
        var currency = order.Currency.ToUpperInvariant();
        // Our prices are gross, so the sum is simple: the goods, plus shipping, minus the coupon — which is the order's total. (No tax line: it is inside the prices.)
        var breakdown = new JsonObject { ["item_total"] = Money_(currency, order.SubtotalCents) };
        if (order.ShippingCents > 0) breakdown["shipping"] = Money_(currency, order.ShippingCents);
        if (order.DiscountCents > 0) breakdown["discount"] = Money_(currency, order.DiscountCents);
        var lines = new JsonArray();
        foreach (var i in items)
        {
            var title = i.DisplayTitle;
            var line = new JsonObject
            {
                ["name"] = title.Length > 127 ? title[..127] : title, ["quantity"] = i.Qty.ToString(CultureInfo.InvariantCulture), ["unit_amount"] = Money_(currency, i.UnitCents),
                ["category"] = i.Kind == ProductKind.Digital ? "DIGITAL_GOODS" : "PHYSICAL_GOODS"
            };
            if (i.Sku.Length > 0) line["sku"] = i.Sku.Length > 127 ? i.Sku[..127] : i.Sku;
            lines.Add(line);
        }
        var body = new JsonObject
        {
            ["intent"] = "CAPTURE",
            ["purchase_units"] = new JsonArray(new JsonObject
            {
                ["reference_id"] = "default", ["custom_id"] = order.Number, ["description"] = $"{ctx.Site.Name} – {order.Number}",
                ["amount"] = new JsonObject { ["currency_code"] = currency, ["value"] = Value(order.TotalCents), ["breakdown"] = breakdown },
                ["items"] = lines
            }),
            ["payment_source"] = new JsonObject
            {
                ["paypal"] = new JsonObject
                {
                    ["experience_context"] = new JsonObject
                    {
                        ["brand_name"] = ctx.Site.Name.Length > 127 ? ctx.Site.Name[..127] : ctx.Site.Name, ["locale"] = order.Locale == "en" ? "en-US" : "de-DE", ["landing_page"] = "NO_PREFERENCE",
                        // The shop collects the delivery address itself; PayPal's page does not ask again.
                        ["shipping_preference"] = "NO_SHIPPING", ["user_action"] = "PAY_NOW", ["return_url"] = back + "&paypal=1", ["cancel_url"] = back + "&cancelled=1"
                    }
                }
            }
        };
        var store = new ShopStore(ctx.Data);
        try
        {
            var created = await Client(cfg).PostAsync("/v2/checkout/orders", body, "order-" + order.Number + (attempt.Length > 0 ? "-" + attempt : ""), ct);
            var id = created["id"]?.GetValue<string>() ?? "";
            var url = (created["links"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(l => l["rel"]?.GetValue<string>() is "payer-action" or "approve")?["href"]?.GetValue<string>() ?? "";
            if (id.Length == 0 || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return new PaymentStart(null, ctx.T("shop.err.payment"));
            await store.UpdateOrderAsync(order.Id, new Dictionary<string, object?> { ["payment_ref"] = id }, ct);
            await store.AddPaymentAsync(order.Id, Payments.PayPal, id, order.TotalCents, order.Currency, "created", ct);
            await store.AddEventAsync(order.Id, "payment", "PayPal-Zahlung gestartet" + (cfg.PayPalSandbox ? " (Sandbox)" : ""), "system", null, ct);
            return new PaymentStart(url, null);
        }
        catch (PayPalException ex)
        {
            ctx.Log($"PayPal-Bestellung für {order.Number}: {ex.Message}");
            await store.AddEventAsync(order.Id, "payment-failed", "PayPal: " + ex.Message, "system", null, ct);
            return new PaymentStart(null, ctx.T("shop.err.payment"));
        }
    }

    /// <summary>The customer wants to pay again (closed the tab, cancelled on PayPal's page). If PayPal has the money after all that is settled first; otherwise a NEW PayPal order is made.</summary>
    public static async Task<PaymentStart> RestartAsync(IPluginContext ctx, ShopConfig cfg, Order order, IReadOnlyList<OrderItem> items, CancellationToken ct = default)
    {
        await CaptureAsync(ctx, cfg, order, ct);
        var fresh = await new ShopStore(ctx.Data).OrderAsync(order.Id, ct);
        if (fresh is null || fresh.Status != OrderStatus.AwaitingPayment) return new PaymentStart(null, ctx.T("shop.err.alreadyPaid"));
        return await StartAsync(ctx, cfg, fresh, items, ct, attempt: Guid.NewGuid().ToString("N")[..8]);
    }

    // ---- capturing ---------------------------------------------------------------------------------------

    /// <summary>
    /// Settles an order with PayPal: if its PayPal order is approved the payment is captured, and a captured payment pays the order. Safe to call any number of times — the
    /// capture request carries a fixed key, a COMPLETED order is only read, and paying is idempotent.
    /// </summary>
    public static async Task CaptureAsync(IPluginContext ctx, ShopConfig cfg, Order order, CancellationToken ct = default)
    {
        if (!cfg.PayPalReady || order.PaymentProvider != Payments.PayPal || order.PaymentRef.Length == 0 || order.Status != OrderStatus.AwaitingPayment) return;
        var store = new ShopStore(ctx.Data);
        var client = Client(cfg);
        var path = "/v2/checkout/orders/" + Uri.EscapeDataString(order.PaymentRef);
        try
        {
            var state = await client.GetAsync(path, ct);
            var status = state["status"]?.GetValue<string>() ?? "";
            if (status == "APPROVED") state = await client.PostAsync(path + "/capture", new JsonObject(), "capture-" + order.Number, ct);
            else if (status != "COMPLETED") return;                                                    // not approved (yet): nothing to take
            await ApplyOrderAsync(ctx, cfg, store, order, state, ct);
        }
        catch (PayPalException ex)
        {
            ctx.Log($"PayPal {order.Number}: {ex.Message}");
            await store.AddEventAsync(order.Id, "payment-failed", "PayPal: " + ex.Message, "system", null, ct);
        }
    }

    /// <summary>
    /// What PayPal says about an order (read or just captured) → the order. Paid only when the capture is COMPLETED, for exactly the order's amount in its currency and with the
    /// order's number on it; otherwise the history gets a warning for a human instead.
    /// </summary>
    internal static async Task ApplyOrderAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, Order order, JsonObject paypalOrder, CancellationToken ct)
    {
        var unit = (paypalOrder["purchase_units"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var capture = ((unit?["payments"] as JsonObject)?["captures"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        if (capture is null) return;
        await ApplyCaptureAsync(ctx, cfg, store, order, capture, paypalOrder["id"]?.GetValue<string>() ?? order.PaymentRef, unit?["custom_id"]?.GetValue<string>(), ct);
    }

    internal static async Task ApplyCaptureAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, Order order, JsonObject capture, string paypalOrderId, string? customId, CancellationToken ct)
    {
        var status = capture["status"]?.GetValue<string>() ?? "";
        var id = capture["id"]?.GetValue<string>() ?? "";
        if (status == "COMPLETED")
        {
            var okAmount = TryAmount(capture["amount"], out var cents, out var currency);
            if (!okAmount || cents != order.TotalCents || currency != order.Currency.ToUpperInvariant() || customId is { Length: > 0 } && customId != order.Number)
            {
                await store.AddEventAsync(order.Id, "warning",
                    $"PayPal meldet eine Zahlung über {(okAmount ? Value(cents) : "?")} {currency}, die Bestellung kostet {Value(order.TotalCents)} {order.Currency} — nicht als bezahlt übernommen.", "paypal", null, ct);
                return;
            }
            await ShopOrders.MarkPaidAsync(ctx, cfg, order.Id, "paypal", "Zahlung per PayPal erhalten", paypalOrderId, id, ct);
        }
        else if (status is "PENDING" or "")
        {
            // A payment that settles later (an e-check): hold the goods for as long as that takes.
            if (order.Status == OrderStatus.AwaitingPayment)
            {
                await store.UpdateOrderAsync(order.Id, new Dictionary<string, object?> { ["reserved_until"] = DateTime.UtcNow.AddDays(7), ["payment_intent"] = id }, ct);
                await store.AddEventAsync(order.Id, "payment", "PayPal: Zahlung wurde veranlasst, das Geld ist noch nicht da", "paypal", null, ct);
            }
        }
        else if (status is "DECLINED" or "FAILED")
            await store.AddEventAsync(order.Id, "payment-failed", $"PayPal: Zahlung {status}", "paypal", null, ct);
    }

    /// <summary>Asks PayPal what became of an order still waiting — the customer who came back before the notification did, and the upkeep job.</summary>
    public static Task ReconcileAsync(IPluginContext ctx, ShopConfig cfg, Order order, CancellationToken ct = default) => CaptureAsync(ctx, cfg, order, ct);

    // ---- notifications -----------------------------------------------------------------------------------

    /// <summary>The route handler of <c>POST /shop/webhook/paypal</c>.</summary>
    public static async Task<PluginResult> WebhookAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        if (!cfg.PayPalReady || cfg.PayPalWebhookId.Length == 0) return PluginResult.Text("PayPal is not set up", status: 503);

        var raw = System.Text.Encoding.UTF8.GetString(ctx.Request.Body);
        var headers = new[] { "PAYPAL-AUTH-ALGO", "PAYPAL-CERT-URL", "PAYPAL-TRANSMISSION-ID", "PAYPAL-TRANSMISSION-SIG", "PAYPAL-TRANSMISSION-TIME" }.ToDictionary(h => h, h => ctx.Request.Header(h));
        if (!await Client(cfg).VerifyWebhookAsync(headers, cfg.PayPalWebhookId, raw, ct))
        {
            ctx.Log("PayPal-Webhook mit ungültiger Signatur abgewiesen");
            return PluginResult.Text("invalid signature", status: 400);
        }

        JsonObject? evt;
        try { evt = JsonNode.Parse(raw) as JsonObject; } catch (System.Text.Json.JsonException) { evt = null; }
        var eventId = evt?["id"]?.GetValue<string>() ?? "";
        var type = evt?["event_type"]?.GetValue<string>() ?? "";
        var resource = evt?["resource"] as JsonObject;
        if (evt is null || eventId.Length == 0 || resource is null) return PluginResult.Text("bad event", status: 400);

        var store = new ShopStore(ctx.Data);
        if (await store.HasSeenWebhookAsync(eventId, ct)) return PluginResult.Text("duplicate");
        try { await HandleAsync(ctx, cfg, store, type, resource, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not recorded as seen: a 500 makes PayPal send the event again, and handling is idempotent.
            ctx.Log($"PayPal-Ereignis {type} {eventId}: {ex.Message}");
            return PluginResult.Text("error", status: 500);
        }
        await store.TryRecordWebhookAsync(eventId, Payments.PayPal, ct);
        return PluginResult.Text("ok");
    }

    private static async Task<Order?> OrderOfAsync(ShopStore store, JsonObject resource, CancellationToken ct)
    {
        // A capture knows the PayPal order it belongs to and the custom id (our order number) it inherited; an order resource is itself the PayPal order.
        var paypalOrder = (resource["supplementary_data"]?["related_ids"] as JsonObject)?["order_id"]?.GetValue<string>() ?? (resource["intent"] is not null ? resource["id"]?.GetValue<string>() : null) ?? "";
        var order = await store.OrderByPaymentRefAsync(paypalOrder, ct);
        if (order is null && resource["custom_id"]?.GetValue<string>() is { Length: > 0 } number) order = await store.OrderByNumberAsync(number, ct);
        return order is null || order.PaymentProvider != Payments.PayPal ? null : order;
    }

    private static async Task HandleAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, string type, JsonObject resource, CancellationToken ct)
    {
        switch (type)
        {
            case "CHECKOUT.ORDER.APPROVED":
            {
                // Approved, maybe never returned to the shop: take the payment now.
                var order = await OrderOfAsync(store, resource, ct);
                if (order is not null) await CaptureAsync(ctx, cfg, order, ct);
                break;
            }
            case "PAYMENT.CAPTURE.COMPLETED":
            case "PAYMENT.CAPTURE.PENDING":
            case "PAYMENT.CAPTURE.DENIED":
            {
                var order = await OrderOfAsync(store, resource, ct);
                if (order is null) { ctx.Log("PayPal-Zahlung ohne passende Bestellung: " + (resource["id"]?.GetValue<string>() ?? "?")); break; }
                if (type.EndsWith("DENIED", StringComparison.Ordinal)) resource["status"] = "DECLINED";
                await ApplyCaptureAsync(ctx, cfg, store, order, resource, (resource["supplementary_data"]?["related_ids"] as JsonObject)?["order_id"]?.GetValue<string>() ?? order.PaymentRef, resource["custom_id"]?.GetValue<string>(), ct);
                break;
            }
            case "PAYMENT.CAPTURE.REFUNDED":
            {
                // A refund PayPal made (the operator's own click in PayPal, or the shop's): found through the capture it belongs to, counted once by its id.
                var capture = ((resource["links"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(l => l["rel"]?.GetValue<string>() == "up")?["href"]?.GetValue<string>() ?? "").TrimEnd('/').Split('/').LastOrDefault() ?? "";
                var order = capture.Length == 0 ? null : await store.OrderByPaymentIntentAsync(capture, ct);
                if (order is null || order.PaymentProvider != Payments.PayPal || !TryAmount(resource["amount"], out var cents, out _) || cents <= 0) break;
                await CountRefundAsync(ctx, store, order, resource["id"]?.GetValue<string>() ?? "", cents, "paypal", "Erstattung bei PayPal", false, ct);
                break;
            }
            case "PAYMENT.CAPTURE.REVERSED":
            {
                var order = await OrderOfAsync(store, resource, ct);
                if (order is not null) await store.AddEventAsync(order.Id, "warning", "PayPal: Die Zahlung wurde rückgängig gemacht (Rückbuchung) — bitte im PayPal-Konto prüfen.", "paypal", null, ct);
                break;
            }
        }
    }

    // ---- refunds -----------------------------------------------------------------------------------------

    /// <summary>A refund counts ONCE, whoever reports it first: the shop (it just made it) or PayPal's notification. The refund's id is the gate — an id can be recorded only once.</summary>
    private static async Task CountRefundAsync(IPluginContext ctx, ShopStore store, Order order, string refundId, long cents, string actor, string text, bool restock, CancellationToken ct)
    {
        if (refundId.Length == 0 || !await store.TryRecordWebhookAsync("refund:" + refundId, Payments.PayPal, ct)) return;
        await ShopOrders.ApplyRefundAsync(ctx, order.Id, cents, actor, text, restock, ct, isIncrease: true);
    }

    /// <summary>Refunds <paramref name="amountCents"/> of the order's PayPal capture. Throws <see cref="PayPalException"/> with PayPal's reason.</summary>
    public static async Task RefundAsync(IPluginContext ctx, ShopConfig cfg, Order order, long amountCents, string actor, bool restock, CancellationToken ct = default)
    {
        if (order.PaymentIntent.Length == 0) throw new PayPalException("Zu dieser Bestellung ist keine PayPal-Zahlung bekannt.", 0, "no_capture");
        var refund = await Client(cfg).PostAsync($"/v2/payments/captures/{Uri.EscapeDataString(order.PaymentIntent)}/refund", new JsonObject
        {
            ["amount"] = Money_(order.Currency.ToUpperInvariant(), amountCents), ["note_to_payer"] = $"{order.Number}"
        },
        // The key names the order, the amount already refunded and the amount: the same click twice is one refund, the next partial refund is another.
        $"refund-{order.Number}-{order.RefundedCents}-{amountCents}", ct);
        var status = refund["status"]?.GetValue<string>() ?? "";
        if (status is "FAILED" or "CANCELLED") throw new PayPalException("PayPal hat die Erstattung abgelehnt (" + status + ").", 0, "refund_failed");
        await CountRefundAsync(ctx, new ShopStore(ctx.Data), order, refund["id"]?.GetValue<string>() ?? "", amountCents, actor, "Erstattung über PayPal", restock, ct);
    }
}
