using System.Globalization;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>The shop's back office, part one: the order list, one order with everything that can be done to it, and the dashboard figures. Screens are trees of library
/// components — nothing here writes a class name.</summary>
internal static class ShopAdmin
{
    public const string Section = "/admin/p/shop";
    private static string OrderUrl(long id) => $"{Section}/orders/{id}";
    private static string Intl(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static UiTone ToneOf(string status) => status switch
    {
        OrderStatus.Paid or OrderStatus.Shipped or OrderStatus.Completed => UiTone.Success,
        OrderStatus.AwaitingPayment => UiTone.Warning, OrderStatus.Refunded => UiTone.Info, _ => UiTone.Muted
    };

    public static UiBadge StatusBadge(IPluginContext ctx, string status) => Ui.Badge(ctx.T("shop.status." + status), ToneOf(status));

    // ---- list --------------------------------------------------------------------------------------------

    public static async Task<PluginResult> OrdersAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var status = ctx.Request.Q("status");
        var search = ctx.Request.Q("q");
        var (orders, total) = await store.OrdersAsync(OrderStatus.All.Contains(status) ? status : null, search.Length > 0 ? search : null, take: 400, ct: ct);

        var filters = new List<UiNode> { Ui.Button(ctx.T("shop.adm.all"), $"{Section}/orders", status.Length == 0 ? UiButtonKind.Primary : UiButtonKind.Ghost, small: true) };
        foreach (var s in OrderStatus.All)
            filters.Add(Ui.Button($"{ctx.T("shop.status." + s)} ({await store.CountOrdersAsync(s, ct)})", $"{Section}/orders?status={s}", status == s ? UiButtonKind.Primary : UiButtonKind.Ghost, small: true));

        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("shop.adm.number")), Ui.Column(ctx.T("shop.adm.date")), Ui.Column(ctx.T("shop.adm.customer")), Ui.Column(ctx.T("shop.adm.payment")),
                    Ui.Column(ctx.T("shop.total"), right: true), Ui.Column(ctx.T("shop.adm.status")), Ui.Column("", actions: true) },
            orders.Select(o => Ui.RowFor($"{o.Number} {o.Email} {o.Name} {o.InvoiceNumber}",
                Ui.Text(o.Number) with { Inline = true, Strong = true },
                o.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                Ui.Text(string.IsNullOrWhiteSpace(o.Name) ? o.Email : o.Name + " · " + o.Email) with { Inline = true },
                ctx.T("shop.pay." + (o.PaymentProvider.Length == 0 ? "none" : o.PaymentProvider)),
                Money.Format(o.TotalCents, o.Currency, "de"),
                StatusBadge(ctx, o.Status),
                Ui.Button(ctx.T("shop.adm.open"), OrderUrl(o.Id), UiButtonKind.Ghost, small: true))),
            searchPlaceholder: ctx.T("action.search"), pageSize: 25, emptyText: ctx.T("shop.adm.noOrders"));

        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(total > orders.Count ? ctx.T("shop.adm.showingLatest", orders.Count, total) : null, ctx.T("shop.adm.orders")),
            Ui.Inline(filters.ToArray()),
            table), ctx.T("shop.adm.orders"));
    }

    // ---- one order ---------------------------------------------------------------------------------------

    public static async Task<PluginResult> OrderAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var cfg = ShopConfig.From(ctx.Settings);
        if (!long.TryParse(ctx.Request.R("id"), out var id) || await store.OrderAsync(id, ct) is not { } order) return PluginResult.Redirect($"{Section}/orders");
        var who = "admin:" + ctx.User.Name;

        if (ctx.Request.IsPost)
        {
            var back = OrderUrl(id);
            switch (ctx.Request.Action)
            {
                case "paid":
                    return await ShopOrders.MarkPaidAsync(ctx, cfg, id, who, "Vom Betreiber als bezahlt markiert", ct: ct)
                        ? PluginResult.Redirect(back, ctx.T("shop.adm.markedPaid")) : PluginResult.Redirect(back, ctx.T("shop.adm.notPossible"), true);
                case "cancel":
                    return await ShopOrders.CancelAsync(ctx, cfg, id, "admin", who, "Vom Betreiber storniert", ctx.Request.Form.ContainsKey("notify"), ct)
                        ? PluginResult.Redirect(back, ctx.T("shop.adm.cancelled")) : PluginResult.Redirect(back, ctx.T("shop.adm.notPossible"), true);
                case "ship":
                    return await ShopOrders.ShipAsync(ctx, id, ctx.Request.F("tracking"), who, ctx.Request.Form.ContainsKey("notify"), ct)
                        ? PluginResult.Redirect(back, ctx.T("shop.adm.shipped")) : PluginResult.Redirect(back, ctx.T("shop.adm.notPossible"), true);
                case "complete":
                    return await ShopOrders.CompleteAsync(ctx, id, who, ct) ? PluginResult.Redirect(back, ctx.T("shop.adm.completed")) : PluginResult.Redirect(back, ctx.T("shop.adm.notPossible"), true);
                case "note":
                    if (ctx.Request.F("text").Trim().Length > 0) await store.AddEventAsync(id, "note", ctx.Request.F("text").Trim(), who, null, ct);
                    return PluginResult.Redirect(back);
                case "tracking":
                    await store.UpdateOrderAsync(id, new Dictionary<string, object?> { ["tracking"] = ctx.Request.F("tracking").Trim() }, ct);
                    return PluginResult.Redirect(back, ctx.T("shop.adm.saved"));
                case "resend":
                {
                    var key = ctx.Request.F("mail");
                    if (!new[] { ShopMail.Placed, ShopMail.Paid, ShopMail.Shipped, ShopMail.Cancelled }.Contains(key)) return PluginResult.Redirect(back);
                    var items = await store.ItemsAsync(id, ct);
                    var files = new List<ProductFile>();
                    foreach (var d in await store.DownloadsOfOrderAsync(id, ct)) if (!d.Revoked && await store.FileAsync(d.FileId, ct) is { } f) files.Add(f);
                    var ok = await ShopMail.SendAsync(ctx, store, key, order, items, v =>
                    {
                        v["downloads"] = ShopMail.DownloadLines(ctx, order, files, order.Locale);
                        v["invoice_url"] = $"{ctx.Site.Origin}/shop/invoice/{Uri.EscapeDataString(order.Number)}?t={Uri.EscapeDataString(order.AccessToken)}";
                        v["payment_info"] = order.PaymentProvider == Payments.Bank ? Payments.BankInstructions(ctx, cfg, order) : "";
                    }, ct: ct);
                    return PluginResult.Redirect(back, ok ? ctx.T("shop.adm.mailSent") : ctx.T("shop.adm.mailFailed"), !ok);
                }
                case "refund":
                {
                    var amount = Money.TryParse(ctx.Request.F("amount"), out var cents) ? cents : order.RefundableCents;
                    if (amount <= 0 || amount > order.RefundableCents) return PluginResult.Redirect(back, ctx.T("shop.adm.refundRange", Money.Format(order.RefundableCents, order.Currency)), true);
                    var restock = ctx.Request.Form.ContainsKey("restock");
                    try
                    {
                        if (order.PaymentProvider == Payments.Stripe && order.PaymentIntent.Length > 0 && cfg.StripeReady)
                            await StripePayments.RefundAsync(ctx, cfg, order, amount, who, restock, ct);
                        else if (order.PaymentProvider == Payments.PayPal && order.PaymentIntent.Length > 0 && cfg.PayPalReady)
                            await PayPalPayments.RefundAsync(ctx, cfg, order, amount, who, restock, ct);
                        else
                            await ShopOrders.ApplyRefundAsync(ctx, id, order.RefundedCents + amount, who, "Erstattung manuell erfasst", restock, ct);
                    }
                    catch (StripeException ex) { return PluginResult.Redirect(back, ctx.T("shop.adm.refundFailed", ex.Message), true); }
                    catch (PayPalException ex) { return PluginResult.Redirect(back, ctx.T("shop.adm.refundFailed", ex.Message), true); }
                    return PluginResult.Redirect(back, ctx.T("shop.adm.refunded"));
                }
            }
        }

        var items2 = await store.ItemsAsync(id, ct);
        var events = await store.EventsAsync(id, ct);
        var invoice = await store.InvoiceOfOrderAsync(id, ct);
        string Mo(long c) => Money.Format(c, order.Currency, "de");
        string D(DateTime? d) => d is null ? "—" : d.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

        var head = new List<UiNode?> { Ui.Button(ctx.T("shop.adm.back"), $"{Section}/orders", UiButtonKind.Ghost, icon: "ti-arrow-left", small: true) };
        var credits = await store.CreditNotesOfOrderAsync(id, ct);
        if (invoice is not null)
        {
            head.Add(Ui.Button(ctx.T("shop.invoice") + " " + invoice.Number, $"/shop/invoice/{Uri.EscapeDataString(order.Number)}", UiButtonKind.Ghost, icon: "ti-file-invoice", small: true) with { NewTab = true });
            head.Add(Ui.Button(ctx.T("shop.pdf"), $"/shop/invoice/{Uri.EscapeDataString(order.Number)}?pdf=1", UiButtonKind.Ghost, icon: "ti-file-type-pdf", small: true) with { NewTab = true });
        }
        foreach (var c in credits)
            head.Add(Ui.Button(ctx.T("shop.creditNote") + " " + c.Number, $"/shop/invoice/{Uri.EscapeDataString(order.Number)}?doc={Uri.EscapeDataString(c.Number)}&pdf=1", UiButtonKind.Ghost, icon: "ti-file-type-pdf", small: true) with { NewTab = true });
        head.Add(Ui.Button(ctx.T("shop.adm.customerView"), $"/shop/order/{Uri.EscapeDataString(order.Number)}", UiButtonKind.Ghost, icon: "ti-external-link", small: true) with { NewTab = true });

        var info = Ui.KeyValue(
            (ctx.T("shop.adm.status"), ctx.T("shop.status." + order.Status) + (order.CancelReason.Length > 0 ? " (" + ctx.T("shop.cancel." + order.CancelReason) + ")" : "")),
            (ctx.T("shop.adm.date"), D(order.CreatedUtc)),
            (ctx.T("shop.adm.customer"), (string.IsNullOrWhiteSpace(order.Name) ? "" : order.Name + " · ") + order.Email + (order.Phone.Length > 0 ? " · " + order.Phone : "") + (order.MemberId > 0 ? " · " + ctx.T("shop.adm.hasAccount") : " · " + ctx.T("shop.adm.guest"))),
            (ctx.T("shop.adm.payment"), ctx.T("shop.pay." + (order.PaymentProvider.Length == 0 ? "none" : order.PaymentProvider)) + (order.PaidUtc is { } p ? " · " + D(p) : "")),
            (ctx.T("shop.billingAddress"), order.Billing.Lines().Replace("\n", ", ")),
            (ctx.T("shop.shippingAddress"), order.Shipping.IsComplete ? order.Shipping.Lines().Replace("\n", ", ") : "—"),
            (ctx.T("shop.shippingMethod"), order.ShippingMethod.Length > 0 ? order.ShippingMethod : "—"),
            (ctx.T("shop.adm.terms"), order.TermsVersion + " · " + D(order.TermsAcceptedUtc) + (order.WithdrawalWaiver ? " · " + ctx.T("shop.adm.waiverGiven") : "")),
            (ctx.T("shop.tracking"), order.Tracking.Length > 0 ? order.Tracking : "—"),
            (ctx.T("shop.adm.customerNote"), order.Note.Length > 0 ? order.Note : "—"));

        var itemTable = Ui.Table(
            new[] { Ui.Column(ctx.T("shop.item")), Ui.Column(ctx.T("shop.adm.sku")), Ui.Column(ctx.T("shop.qty"), right: true), Ui.Column(ctx.T("shop.price"), right: true), Ui.Column(ctx.T("shop.adm.vat"), right: true), Ui.Column(ctx.T("shop.total"), right: true) },
            items2.Select(i => Ui.Row(i.DisplayTitle + (i.Kind == ProductKind.Digital ? " · " + ctx.T("shop.digital") : ""), i.Sku, i.Qty, Mo(i.UnitCents), Money.Percent(i.TaxBp) + " %", Mo(i.LineCents))), emptyText: "—");

        var totals = Ui.KeyValue(
            (ctx.T("shop.subtotal"), Mo(order.SubtotalCents)),
            (ctx.T("shop.adm.coupon"), order.DiscountCents > 0 || order.CouponCode.Length > 0 ? $"{order.CouponCode} (−{Mo(order.DiscountCents)})" : "—"),
            (ctx.T("shop.shipping"), Mo(order.ShippingCents)),
            (ctx.T("shop.adm.vatIncluded"), order.SmallBusiness ? ctx.T("shop.adm.smallBusiness") : Mo(order.TaxCents) + (order.TaxNote.Length > 0 ? " · " + ctx.T("shop.adm.taxNote." + order.TaxNote) : "")),
            (ctx.T("shop.total"), Mo(order.TotalCents)), (ctx.T("shop.refunded"), order.RefundedCents > 0 ? Mo(order.RefundedCents) : "—"));

        var actions = new List<UiNode?>();
        void Card(string title, params UiNode?[] children) => actions.Add(Ui.Card(title, children));

        if (order.Status == OrderStatus.AwaitingPayment)
        {
            Card(ctx.T("shop.adm.actions"), Ui.Inline(
                Ui.Action(ctx.T("shop.adm.markPaid"), "paid", null, UiButtonKind.Primary, confirm: ctx.T("shop.adm.markPaidConfirm")),
                Ui.Form(Ui.Checkbox("notify", ctx.T("shop.adm.notifyCustomer"), true), Ui.FormActions(Ui.Submit(ctx.T("shop.adm.cancelOrder"), UiButtonKind.Danger, "action", "cancel", confirm: ctx.T("shop.adm.cancelConfirm"))))));
        }
        if (order.Status == OrderStatus.Paid)
            Card(ctx.T("shop.adm.shipping"), Ui.Form(
                Ui.Input("tracking", ctx.T("shop.tracking"), order.Tracking), Ui.Checkbox("notify", ctx.T("shop.adm.notifyCustomer"), true),
                Ui.FormActions(Ui.Submit(ctx.T("shop.adm.markShipped"), UiButtonKind.Primary, "action", "ship"))));
        if (order.Status is OrderStatus.Shipped or OrderStatus.Paid)
            Card(ctx.T("shop.adm.finish"), Ui.Inline(Ui.Action(ctx.T("shop.adm.complete"), "complete", null, UiButtonKind.Ghost)));
        if (OrderStatus.IsPaid(order.Status) && order.RefundableCents > 0)
        {
            var stripe = order.PaymentIntent.Length > 0 && (order.PaymentProvider == Payments.Stripe && cfg.StripeReady || order.PaymentProvider == Payments.PayPal && cfg.PayPalReady);
            Card(ctx.T("shop.adm.refund"), Ui.Form(
                Ui.Input("amount", ctx.T("shop.adm.refundAmount", order.Currency), Money.ForInput(order.RefundableCents)) with { Help = ctx.T(stripe ? "shop.adm.refundStripeHelp" : "shop.adm.refundManualHelp", Money.Format(order.RefundableCents, order.Currency)) },
                Ui.Checkbox("restock", ctx.T("shop.adm.restock"), false),
                Ui.FormActions(Ui.Submit(ctx.T("shop.adm.doRefund"), UiButtonKind.Danger, "action", "refund", confirm: ctx.T("shop.adm.refundConfirm")))));
        }
        Card(ctx.T("shop.adm.mails"), Ui.Form(
            Ui.Select("mail", ctx.T("shop.adm.mail"), ShopMail.Paid, Ui.Option(ShopMail.Placed, ctx.T("shop.adm.mailPlaced")), Ui.Option(ShopMail.Paid, ctx.T("shop.adm.mailPaid")), Ui.Option(ShopMail.Shipped, ctx.T("shop.adm.mailShipped")), Ui.Option(ShopMail.Cancelled, ctx.T("shop.adm.mailCancelled"))),
            Ui.FormActions(Ui.Submit(ctx.T("shop.adm.resend"), UiButtonKind.Ghost, "action", "resend"))));
        Card(ctx.T("shop.adm.noteTitle"), Ui.Form(Ui.TextArea("text", ctx.T("shop.adm.note"), "", 2), Ui.FormActions(Ui.Submit(ctx.T("shop.adm.addNote"), UiButtonKind.Ghost, "action", "note"))));

        var history = Ui.Table(
            new[] { Ui.Column(ctx.T("shop.adm.date")), Ui.Column(ctx.T("shop.adm.by")), Ui.Column(ctx.T("shop.adm.what")) },
            events.Select(e => Ui.Row(e.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture), e.Actor,
                e.Kind is "warning" or "mail-failed" or "payment-failed" ? Ui.Badge(e.Text, UiTone.Warning) : Ui.Text(e.Text) with { Inline = true })), emptyText: "—");

        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("shop.orderNo", order.Number), head.ToArray()),
            Ui.Card(ctx.T("shop.adm.details"), info),
            Ui.Card(ctx.T("shop.adm.items"), itemTable, totals),
            Ui.Grid(320, actions.ToArray()),
            Ui.Card(ctx.T("shop.history"), history)), ctx.T("shop.orderNo", order.Number));
    }

    // ---- start page --------------------------------------------------------------------------------------

    public static async Task<UiStat?> TileAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var toShip = await store.CountOrdersAsync(OrderStatus.Paid, ct);
        var open = await store.CountOrdersAsync(OrderStatus.AwaitingPayment, ct);
        return Ui.Stat(ctx.T("shop.adm.tile"), Intl(toShip), ctx.T("shop.adm.tileHint", open), toShip > 0 ? UiTone.Warning : UiTone.Default, $"{Section}/orders?status={OrderStatus.Paid}");
    }

    public static async Task<IEnumerable<AttentionNote>> AttentionAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var cfg = ShopConfig.From(ctx.Settings);
        var notes = new List<AttentionNote>();

        var stale = await store.OrdersWhereAsync(DataQuery.Everything.Eq("status", OrderStatus.Paid).Where("paid", DataOp.Lt, DateTime.UtcNow.AddDays(-3)), ct);
        if (stale.Count > 0) notes.Add(new AttentionNote(ctx.T("shop.adm.attStale", stale.Count), ctx.T("shop.adm.attStaleText"), $"{Section}/orders?status={OrderStatus.Paid}") { Tone = UiTone.Warning, Icon = "ti-package" });

        var waiting = await store.OrdersWhereAsync(DataQuery.Everything.Eq("status", OrderStatus.AwaitingPayment).Eq("payment_provider", Payments.Bank).Where("created", DataOp.Lt, DateTime.UtcNow.AddDays(-Math.Max(2, cfg.BankHoldDays / 2))), ct);
        if (waiting.Count > 0) notes.Add(new AttentionNote(ctx.T("shop.adm.attBank", waiting.Count), ctx.T("shop.adm.attBankText"), $"{Section}/orders?status={OrderStatus.AwaitingPayment}") { Tone = UiTone.Info, Icon = "ti-building-bank" });

        var low = await store.LowStockAsync(cfg.LowStock, ct);
        if (low.Count > 0) notes.Add(new AttentionNote(ctx.T("shop.adm.attLow", low.Count), string.Join(", ", low.Take(5).Select(p => $"{p.Title} ({p.Stock})")), $"{Section}/products") { Tone = UiTone.Warning, Icon = "ti-alert-triangle" });

        if (cfg.StripeEnabled && !cfg.StripeReady) notes.Add(new AttentionNote(ctx.T("shop.adm.attStripeKey"), ctx.T("shop.adm.attStripeKeyText"), $"{Section}/_settings") { Tone = UiTone.Error });
        if (cfg.StripeReady && cfg.StripeWebhookSecret.Length == 0) notes.Add(new AttentionNote(ctx.T("shop.adm.attWebhook"), ctx.T("shop.adm.attWebhookText", ctx.Site.Origin + "/shop/webhook/stripe"), $"{Section}/_settings") { Tone = UiTone.Error });
        if (cfg.Oss)
        {
            var bad = new List<string>();
            ShopTax.ParseOverrides(cfg.TaxRateOverrides, bad);
            if (bad.Count > 0) notes.Add(new AttentionNote(ctx.T("shop.adm.attTaxRates", bad.Count), ctx.T("shop.adm.attTaxRatesText", bad[0]), $"{Section}/_settings") { Tone = UiTone.Warning });
        }
        if (cfg.PayPalEnabled && !cfg.PayPalReady) notes.Add(new AttentionNote(ctx.T("shop.adm.attPayPalKey"), ctx.T("shop.adm.attPayPalKeyText"), $"{Section}/_settings") { Tone = UiTone.Error });
        if (cfg.PayPalReady && cfg.PayPalWebhookId.Length == 0) notes.Add(new AttentionNote(ctx.T("shop.adm.attPayPalWebhook"), ctx.T("shop.adm.attPayPalWebhookText", ctx.Site.Origin + "/shop/webhook/paypal"), $"{Section}/_settings") { Tone = UiTone.Warning });
        if (!cfg.StripeReady && !cfg.PayPalReady && !cfg.BankEnabled) notes.Add(new AttentionNote(ctx.T("shop.adm.attNoPayment"), ctx.T("shop.adm.attNoPaymentText"), $"{Section}/_settings") { Tone = UiTone.Error });
        if (cfg.SellerName.Length == 0 || cfg.SellerAddress.Length == 0) notes.Add(new AttentionNote(ctx.T("shop.adm.attSeller"), ctx.T("shop.adm.attSellerText"), $"{Section}/_settings") { Tone = UiTone.Info });
        var physical = await store.ProductsAsync(true, ct: ct);
        if (physical.Items.Any(p => !p.IsDigital) && (await store.RatesAsync(true, ct)).Count == 0)
            notes.Add(new AttentionNote(ctx.T("shop.adm.attShipping"), ctx.T("shop.adm.attShippingText"), $"{Section}/shipping") { Tone = UiTone.Error });
        return notes;
    }

    public static async Task<string?> BadgeAsync(IPluginContext ctx, CancellationToken ct)
    {
        var n = await new ShopStore(ctx.Data).CountOrdersAsync(OrderStatus.Paid, ct);
        return n > 0 ? Intl(n) : null;
    }
}
