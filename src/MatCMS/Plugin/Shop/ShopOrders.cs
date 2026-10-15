using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>One line of a priced cart: the product as it is right now and what the line costs.</summary>
internal sealed record PricedLine(Product Product, Variant? Variant, int Qty, long UnitCents, long LineCents, int TaxBp, long TaxCents, long DiscountCents = 0)
{
    /// <summary>What the customer pays for the line, after the coupon's share.</summary>
    public long NetOfDiscountCents => LineCents - DiscountCents;
    public string Title => Product.Title;
    public string VariantTitle => Variant?.Name ?? "";
    public string DisplayTitle => VariantTitle.Length == 0 ? Title : Title + " – " + VariantTitle;
}

/// <summary>A cart priced by the SERVER: the prices of the moment, stock applied. <see cref="Changed"/> means something was adjusted (a line removed, a quantity cut) and the cart must be saved and shown again.</summary>
internal sealed record PricedCart(IReadOnlyList<PricedLine> Lines, long SubtotalCents, long LineTaxCents, bool NeedsShipping, int WeightGrams, IReadOnlyList<string> Notes, bool Changed)
{
    public int Count => Lines.Sum(l => l.Qty);
    public bool HasDigital => Lines.Any(l => l.Product.IsDigital);
    /// <summary>"" = the seller's own VAT, "oss" = by the country of delivery, "export" = no VAT (see <see cref="ShopTax"/>).</summary>
    public string TaxNote { get; init; } = "";
    /// <summary>How shipping is taxed for the country this cart goes to; null = as configured.</summary>
    public ShipTax? ShipTax { get; init; }
    /// <summary>What the coupon takes off the goods; <see cref="SubtotalCents"/> stays the goods BEFORE it.</summary>
    public long DiscountCents { get; init; }
    public string CouponCode { get; init; } = "";
    public bool FreeShipping { get; init; }
    public long GoodsCents => SubtotalCents - DiscountCents;
}

internal sealed record ShippingOption(ShippingRate Rate, long PriceCents);

/// <summary>What the checkout form sends.</summary>
internal sealed record CheckoutForm
{
    public string Email { get; init; } = "";
    public string Name { get; init; } = "";
    public string Phone { get; init; } = "";
    public Address Billing { get; init; } = new();
    public Address Shipping { get; init; } = new();
    public bool ShipToBilling { get; init; } = true;
    public long RateId { get; init; }
    public string PayMethod { get; init; } = "";
    public bool Terms { get; init; }
    public bool Waiver { get; init; }
    public string Note { get; init; } = "";
    /// <summary>The coupon code the customer's cart carries ("" = none). It is checked again when the order is placed.</summary>
    public string Coupon { get; init; } = "";
}

internal sealed record PlaceResult(Order? Order, string? Error);

/// <summary>Thrown inside the order transaction when the last piece went to somebody else: everything the transaction wrote is undone.</summary>
internal sealed class SoldOutException(string title) : Exception(title)
{
    public string Title { get; } = title;
}

/// <summary>
/// Prices a cart, places an order and moves it through its states. <b>Every change of an order's status goes through here</b> — it is the one place that
/// knows which moves are allowed and what each one does (stock, invoice, downloads, mails, events), so no screen and no webhook can do half of it.
/// </summary>
internal static class ShopOrders
{
    // ---- pricing -----------------------------------------------------------------------------------------

    public static async Task<PricedCart> PriceAsync(ShopStore store, IReadOnlyList<CartLine> lines, ShopConfig cfg, IPluginContext ctx, CancellationToken ct = default)
    {
        var merged = lines.GroupBy(l => (l.ProductId, l.VariantId)).Select(g => new CartLine(g.Key.ProductId, g.Key.VariantId, g.Sum(x => x.Qty))).ToList();
        var products = (await store.ProductsByIdsAsync(merged.Select(l => l.ProductId), ct)).ToDictionary(p => p.Id);
        var variants = (await store.VariantsOfAsync(products.Keys, ct)).GroupBy(v => v.ProductId).ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<PricedLine>();
        var notes = new List<string>();
        var changed = false;
        foreach (var line in merged)
        {
            if (!products.TryGetValue(line.ProductId, out var p) || !p.Active) { notes.Add(ctx.T("shop.cart.gone")); changed = true; continue; }
            var own = variants.TryGetValue(p.Id, out var vs) ? vs.Where(v => v.Active).ToList() : new List<Variant>();
            Variant? v = null;
            if (own.Count > 0)
            {
                v = own.FirstOrDefault(x => x.Id == line.VariantId);
                if (v is null) { notes.Add(ctx.T("shop.cart.variantGone", p.Title)); changed = true; continue; }
            }
            var qty = Math.Max(1, line.Qty);
            if (p.MaxPerOrder > 0 && qty > p.MaxPerOrder) { qty = p.MaxPerOrder; notes.Add(ctx.T("shop.cart.maxPerOrder", p.Title, p.MaxPerOrder)); changed = true; }
            var available = v is { TrackStock: true } ? v.Stock : p.TrackStock ? p.Stock : long.MaxValue;
            if (available <= 0) { notes.Add(ctx.T("shop.cart.soldOut", p.Title)); changed = true; continue; }
            if (qty > available) { qty = (int)available; notes.Add(ctx.T("shop.cart.lessAvailable", p.Title, available)); changed = true; }

            var unit = Math.Max(0, p.PriceCents + (v?.PriceDeltaCents ?? 0));
            var lineCents = unit * qty;
            var bp = cfg.SmallBusiness ? 0 : p.TaxBp;
            result.Add(new PricedLine(p, v, qty, unit, lineCents, bp, Money.TaxIncluded(lineCents, bp)));
        }
        var physical = result.Where(l => !l.Product.IsDigital).ToList();
        return new PricedCart(result, result.Sum(l => l.LineCents), result.Sum(l => l.TaxCents), physical.Count > 0, physical.Sum(l => l.Product.WeightGrams * l.Qty), notes, changed);
    }

    /// <summary>The ways to ship this cart to a country, cheapest first. Empty when nothing covers it (and the cart has something to ship).</summary>
    public static IReadOnlyList<ShippingOption> ShippingOptions(IReadOnlyList<ShippingRate> rates, string country, PricedCart cart)
    {
        if (!cart.NeedsShipping) return Array.Empty<ShippingOption>();
        return rates.Where(r => r.Active && r.Covers(country) && (r.MaxWeightGrams <= 0 || cart.WeightGrams <= r.MaxWeightGrams))
            .Select(r => new ShippingOption(r, r.FreeFromCents > 0 && cart.SubtotalCents >= r.FreeFromCents ? 0 : cart.ShipTax?.Price(r.PriceCents) ?? r.PriceCents))
            .OrderBy(o => o.PriceCents).ThenBy(o => o.Rate.Sort).ToList();
    }

    public static ShippingOption? Pick(IReadOnlyList<ShippingOption> options, long rateId) =>
        options.FirstOrDefault(o => o.Rate.Id == rateId) ?? options.FirstOrDefault();

    /// <summary><paramref name="Subtotal"/> is the goods before the coupon; the total is subtotal − discount + shipping. <paramref name="ShipTaxBp"/> is the rate shipping was taxed with.</summary>
    public sealed record Totals(long Subtotal, long Shipping, long Tax, long Total, long Discount = 0, int ShipTaxBp = 0);

    public static Totals Compute(PricedCart cart, ShippingOption? shipping, ShopConfig cfg)
    {
        var ship = cart.NeedsShipping && !cart.FreeShipping ? shipping?.PriceCents ?? 0 : 0;
        var shipBp = cfg.SmallBusiness ? 0 : cart.ShipTax?.Bp ?? cfg.ShippingTaxBp;
        var shipTax = Money.TaxIncluded(ship, shipBp);
        return new Totals(cart.SubtotalCents, ship, cart.LineTaxCents + shipTax, cart.SubtotalCents - cart.DiscountCents + ship, cart.DiscountCents, shipBp);
    }

    // ---- placing -----------------------------------------------------------------------------------------

    public static string NewToken(int bytes = 24) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Turns a cart into an order IN ONE TRANSACTION: prices are taken from the products again (never from the form), stock is taken atomically — if the last
    /// piece went to somebody else the whole thing is undone — the order number comes from a gapless sequence, and the order, its frozen lines and its first history
    /// entry are written. Nothing is sent and nobody is charged here.
    /// </summary>
    public static async Task<PlaceResult> PlaceAsync(IPluginContext ctx, ShopConfig cfg, CheckoutForm f, IReadOnlyList<CartLine> cart, long memberId, string paymentProvider, CancellationToken ct = default)
    {
        try
        {
            return await ctx.Data.InTransactionAsync(async d =>
            {
                var store = new ShopStore(d);
                await store.LockForWriteAsync(ct);
                var priced = await PriceAsync(store, cart, cfg, ctx, ct);
                if (priced.Lines.Count == 0) return new PlaceResult(null, ctx.T("shop.err.emptyCart"));
                if (priced.Changed) return new PlaceResult(null, ctx.T("shop.err.cartChanged"));

                var country = (priced.NeedsShipping && !f.ShipToBilling ? f.Shipping.Country : f.Billing.Country).ToUpperInvariant();
                // Tax by the country the goods go to, then the coupon (which is checked again here — the cart's word is not enough), then shipping.
                priced = ShopTax.Apply(priced, cfg, country, f.Billing.Country.ToUpperInvariant());
                Coupon? coupon = null;
                if (f.Coupon.Length > 0)
                {
                    var resolved = await ShopCoupons.ResolveAsync(ctx, store, priced, f.Coupon, f.Email, ct);
                    if (resolved.Problem is not null) return new PlaceResult(null, resolved.Problem);
                    priced = resolved.Cart; coupon = resolved.Coupon;
                }
                var options = ShippingOptions(await store.RatesAsync(true, ct), country, priced);
                var chosen = Pick(options, f.RateId);
                if (priced.NeedsShipping && chosen is null) return new PlaceResult(null, ctx.T("shop.err.noShipping", Countries.Name(country, ctx.Locale)));
                var totals = Compute(priced, chosen, cfg);

                var number = cfg.OrderPrefix + (await store.NextAsync("order", cfg.OrderStart, ct)).ToString(CultureInfo.InvariantCulture);
                foreach (var l in priced.Lines)
                    if (!await store.TakeStockAsync(l.Product, l.Variant, l.Qty, ct)) throw new SoldOutException(l.Title);

                var shipTo = priced.NeedsShipping ? (f.ShipToBilling ? f.Billing with { Phone = f.Phone } : f.Shipping with { Phone = f.Phone }) : new Address();
                var order = new Order
                {
                    Number = number, Status = OrderStatus.AwaitingPayment, MemberId = memberId, Email = f.Email.Trim().ToLowerInvariant(), Name = f.Name.Trim(), Phone = f.Phone.Trim(),
                    Billing = f.Billing, Shipping = shipTo, ShippingMethod = chosen?.Rate.Name ?? "",
                    SubtotalCents = totals.Subtotal, ShippingCents = totals.Shipping, TaxCents = totals.Tax, TotalCents = totals.Total, Currency = cfg.Currency,
                    PaymentProvider = paymentProvider, AccessToken = NewToken(), TermsVersion = cfg.TermsVersion, TermsAcceptedUtc = DateTime.UtcNow,
                    WithdrawalWaiver = priced.HasDigital && f.Waiver, Note = f.Note.Trim(), Locale = ctx.Locale == "en" ? "en" : "de", SmallBusiness = cfg.SmallBusiness,
                    DiscountCents = totals.Discount, CouponCode = coupon?.Code ?? "", TaxNote = cfg.SmallBusiness ? "" : priced.TaxNote, ShippingTaxBp = totals.ShipTaxBp,
                    ReservedUntilUtc = DateTime.UtcNow.AddMinutes(paymentProvider == Payments.Bank ? cfg.BankHoldDays * 24 * 60 : 31)
                };
                var id = await store.InsertOrderAsync(order, ct);
                if (coupon is not null) await store.UseCouponAsync(coupon.Code, ct);
                foreach (var l in priced.Lines)
                    await store.AddItemAsync(new OrderItem
                    {
                        OrderId = id, ProductId = l.Product.Id, VariantId = l.Variant?.Id ?? 0, Title = l.Title, VariantTitle = l.VariantTitle,
                        Sku = l.Variant is { Sku.Length: > 0 } ? l.Variant.Sku : l.Product.Sku, Kind = l.Product.Kind, Qty = l.Qty, UnitCents = l.UnitCents,
                        TaxBp = l.TaxBp, TaxCents = l.TaxCents, LineCents = l.LineCents, DiscountCents = l.DiscountCents
                    }, ct);
                await store.AddEventAsync(id, "placed", $"Bestellung aufgegeben ({paymentProvider}), {Money.Format(order.TotalCents, order.Currency)}" + (coupon is null ? "" : $", Gutschein {coupon.Code} (−{Money.Format(order.DiscountCents, order.Currency)})"), memberId > 0 ? "customer" : "guest", null, ct);
                return new PlaceResult(order with { Id = id }, null);
            }, ct);
        }
        catch (SoldOutException ex) { return new PlaceResult(null, ctx.T("shop.err.soldOut", ex.Title)); }
    }

    // ---- state changes -----------------------------------------------------------------------------------

    private static JsonObject Payload(Order o) => (JsonObject)JsonNode.Parse(new JsonObject
    {
        ["id"] = o.Id, ["number"] = o.Number, ["status"] = o.Status, ["total"] = o.TotalCents, ["currency"] = o.Currency, ["email"] = o.Email, ["member_id"] = o.MemberId
    }.ToJsonString())!;

    private static Task EmitAsync(IPluginContext ctx, string name, Order o, CancellationToken ct) => ctx.Events.EmitAsync(name, Payload(o), ct);

    /// <summary>The order is paid: invoice, downloads, delivery of digital goods, mails, events. Idempotent — a payment reported twice changes nothing the second time.</summary>
    public static async Task<bool> MarkPaidAsync(IPluginContext ctx, ShopConfig cfg, long orderId, string actor, string text, string? paymentRef = null,
        string? paymentIntent = null, CancellationToken ct = default)
    {
        Order? paidOrder = null; IReadOnlyList<OrderItem> items = Array.Empty<OrderItem>(); IReadOnlyList<ProductFile> files = Array.Empty<ProductFile>();
        var autoShipped = false; var oversold = false;

        var moved = await ctx.Data.InTransactionAsync(async d =>
        {
            var store = new ShopStore(d);
            await store.LockForWriteAsync(ct);
            var order = await store.OrderAsync(orderId, ct);
            if (order is null || !OrderStatus.CanMove(order.Status, OrderStatus.Paid)) return false;

            // Money arrived for an order that had already been cancelled (the customer paid while its time ran out): the sale is real, so the order comes back and
            // the stock is taken again — as far as there is any. If there is not, the order stays paid and the history says so for a human to settle.
            var revived = order.Status == OrderStatus.Cancelled;
            items = await store.ItemsAsync(orderId, ct);
            if (revived)
            {
                foreach (var i in items)
                {
                    var p = await store.ProductAsync(i.ProductId, ct);
                    if (p is null) continue;
                    var v = i.VariantId > 0 ? await store.VariantAsync(i.VariantId, ct) : null;
                    if (!await store.TakeStockAsync(p, v, i.Qty, ct)) oversold = true;
                }
                if (order.CouponCode.Length > 0) await store.UseCouponAsync(order.CouponCode, ct);          // the cancellation had given its use back
            }

            await store.SetStatusAsync(orderId, OrderStatus.Paid, "", ct);
            var changes = new Dictionary<string, object?> { ["paid"] = DateTime.UtcNow, ["reserved_until"] = null };
            if (!string.IsNullOrEmpty(paymentRef)) changes["payment_ref"] = paymentRef;
            if (!string.IsNullOrEmpty(paymentIntent)) changes["payment_intent"] = paymentIntent;
            await store.UpdateOrderAsync(orderId, changes, ct);
            await store.AddEventAsync(orderId, "paid", text, actor, null, ct);
            if (revived) await store.AddEventAsync(orderId, "warning", oversold ? "Die Zahlung kam nach der Stornierung an; der Bestand reichte nicht mehr — bitte prüfen." : "Die Zahlung kam nach der Stornierung an; die Bestellung ist wieder aktiv.", "system", null, ct);

            // The invoice, written once and kept.
            var invoiceNumber = ShopInvoice.InvoiceNumber(cfg, await store.NextAsync("invoice", 1, ct));
            var fresh = (await store.OrderAsync(orderId, ct))!;
            var bankInfo = fresh.PaymentProvider == Payments.Bank ? cfg.BankDetails : "";
            var invoiceDoc = ShopInvoice.Build(fresh, items, cfg, invoiceNumber, DateTime.UtcNow, bankInfo);
            await store.AddInvoiceAsync(orderId, invoiceNumber, InvoiceKinds.Invoice, ShopInvoice.Html(invoiceDoc), ShopPdf.Render(invoiceDoc), 0, ct);
            await store.UpdateOrderAsync(orderId, new Dictionary<string, object?> { ["invoice_number"] = invoiceNumber }, ct);

            // Digital goods are delivered at once: one download entry per file of every digital line.
            var fileList = new List<ProductFile>();
            foreach (var i in items.Where(i => i.Kind == ProductKind.Digital))
                foreach (var f in await store.FilesOfProductAsync(i.ProductId, ct))
                    if (fileList.All(x => x.Id != f.Id)) fileList.Add(f);
            foreach (var f in fileList)
                await store.AddDownloadAsync(orderId, f.Id, ShopDownloads.RowHash(orderId, f.Id), DateTime.UtcNow.AddDays(cfg.DownloadDays), cfg.DownloadLimit, ct);
            files = fileList;

            // An order of nothing but downloads is complete the moment it is paid.
            if (items.Count > 0 && items.All(i => i.Kind == ProductKind.Digital))
            {
                await store.SetStatusAsync(orderId, OrderStatus.Shipped, "", ct);
                await store.AddEventAsync(orderId, "delivered", "Digitale Ware ausgeliefert", "system", null, ct);
                autoShipped = true;
            }
            paidOrder = (await store.OrderAsync(orderId, ct))!;
            return true;
        }, ct);

        if (!moved || paidOrder is null) return false;
        var store2 = new ShopStore(ctx.Data);
        var o2 = paidOrder;
        await ShopMail.SendAsync(ctx, store2, ShopMail.Paid, o2, items, v =>
        {
            v["downloads"] = ShopMail.DownloadLines(ctx, o2, files, o2.Locale);
            v["invoice_url"] = $"{ctx.Site.Origin}/shop/invoice/{Uri.EscapeDataString(o2.Number)}?t={Uri.EscapeDataString(o2.AccessToken)}";
        }, ct: ct);
        if (cfg.NotifyEmail.Length > 0)
            await ShopMail.SendAsync(ctx, store2, ShopMail.AdminOrder, o2, items, v => v["admin_url"] = $"{ctx.Site.Origin}/admin/p/shop/orders/{o2.Id}", cfg.NotifyEmail, ct);
        await EmitAsync(ctx, "shop.order.paid", o2, ct);
        if (autoShipped) await EmitAsync(ctx, "shop.order.shipped", o2, ct);
        return true;
    }

    /// <summary>Cancels an order that was not paid and gives its stock back.</summary>
    public static async Task<bool> CancelAsync(IPluginContext ctx, ShopConfig cfg, long orderId, string reason, string actor, string text, bool notifyCustomer = true, CancellationToken ct = default)
    {
        Order? order = null; IReadOnlyList<OrderItem> items = Array.Empty<OrderItem>();
        var moved = await ctx.Data.InTransactionAsync(async d =>
        {
            var store = new ShopStore(d);
            await store.LockForWriteAsync(ct);
            var o = await store.OrderAsync(orderId, ct);
            if (o is null || o.Status != OrderStatus.AwaitingPayment) return false;
            items = await store.ItemsAsync(orderId, ct);
            foreach (var i in items) await store.ReturnStockAsync(i.ProductId, i.VariantId, i.Qty, ct);
            if (o.CouponCode.Length > 0) await store.ReleaseCouponAsync(o.CouponCode, ct);                 // an order that was never paid does not use up a coupon
            await store.SetStatusAsync(orderId, OrderStatus.Cancelled, reason, ct);
            await store.UpdateOrderAsync(orderId, new Dictionary<string, object?> { ["reserved_until"] = null }, ct);
            await store.AddEventAsync(orderId, "cancelled", text, actor, null, ct);
            order = (await store.OrderAsync(orderId, ct))!;
            return true;
        }, ct);
        if (!moved || order is null) return false;
        if (notifyCustomer) await ShopMail.SendAsync(ctx, new ShopStore(ctx.Data), ShopMail.Cancelled, order, items, ct: ct);
        await EmitAsync(ctx, "shop.order.cancelled", order, ct);
        return true;
    }

    /// <summary>Paid → shipped: the goods left. Tells the customer, with the tracking number when there is one.</summary>
    public static async Task<bool> ShipAsync(IPluginContext ctx, long orderId, string tracking, string actor, bool notifyCustomer = true, CancellationToken ct = default)
    {
        Order? order = null; IReadOnlyList<OrderItem> items = Array.Empty<OrderItem>();
        var moved = await ctx.Data.InTransactionAsync(async d =>
        {
            var store = new ShopStore(d);
            await store.LockForWriteAsync(ct);
            var o = await store.OrderAsync(orderId, ct);
            if (o is null || !OrderStatus.CanMove(o.Status, OrderStatus.Shipped)) return false;
            await store.SetStatusAsync(orderId, OrderStatus.Shipped, "", ct);
            await store.UpdateOrderAsync(orderId, new Dictionary<string, object?> { ["tracking"] = tracking.Trim() }, ct);
            await store.AddEventAsync(orderId, "shipped", tracking.Trim().Length > 0 ? "Versendet, Sendungsnummer " + tracking.Trim() : "Versendet", actor, null, ct);
            items = await store.ItemsAsync(orderId, ct);
            order = (await store.OrderAsync(orderId, ct))!;
            return true;
        }, ct);
        if (!moved || order is null) return false;
        if (notifyCustomer) await ShopMail.SendAsync(ctx, new ShopStore(ctx.Data), ShopMail.Shipped, order, items, ct: ct);
        await EmitAsync(ctx, "shop.order.shipped", order, ct);
        return true;
    }

    public static async Task<bool> CompleteAsync(IPluginContext ctx, long orderId, string actor, CancellationToken ct = default)
    {
        Order? order = null;
        var moved = await ctx.Data.InTransactionAsync(async d =>
        {
            var store = new ShopStore(d);
            await store.LockForWriteAsync(ct);
            var o = await store.OrderAsync(orderId, ct);
            if (o is null || !OrderStatus.CanMove(o.Status, OrderStatus.Completed)) return false;
            await store.SetStatusAsync(orderId, OrderStatus.Completed, "", ct);
            await store.AddEventAsync(orderId, "completed", "Abgeschlossen", actor, null, ct);
            order = (await store.OrderAsync(orderId, ct))!;
            return true;
        }, ct);
        if (moved && order is not null) await EmitAsync(ctx, "shop.order.completed", order, ct);
        return moved;
    }

    /// <summary>
    /// Records that <paramref name="refundedCents"/> in total has been given back. ABSOLUTE, not an increase: a payment provider reports "refunded so far", and a
    /// notification that arrives twice (or after the admin's own refund) must not count twice. A full refund moves the order to refunded and blocks its downloads;
    /// a partial one only writes the history.
    /// </summary>
    public static async Task<bool> ApplyRefundAsync(IPluginContext ctx, long orderId, long refundedCents, string actor, string text, bool restock = false, CancellationToken ct = default, bool isIncrease = false)
    {
        Order? order = null; IReadOnlyList<OrderItem> items = Array.Empty<OrderItem>(); long added = 0;
        string creditNumber = "", creditInvoice = "";
        var cfg = ShopConfig.From(ctx.Settings);
        var changed = await ctx.Data.InTransactionAsync(async d =>
        {
            var store = new ShopStore(d);
            await store.LockForWriteAsync(ct);
            var o = await store.OrderAsync(orderId, ct);
            if (o is null || !OrderStatus.IsPaid(o.Status) && o.Status != OrderStatus.Refunded) return false;
            // PayPal reports each refund on its own (a part), Stripe the total so far: an increase is added to what the transaction reads here, not to what a caller saw earlier.
            var now = Math.Clamp(isIncrease ? o.RefundedCents + refundedCents : refundedCents, 0, o.TotalCents);
            if (now <= o.RefundedCents) return false;
            added = now - o.RefundedCents;
            await store.UpdateOrderAsync(orderId, new Dictionary<string, object?> { ["refunded"] = now }, ct);
            await store.AddEventAsync(orderId, "refund", $"{text} ({Money.Format(added, o.Currency)})", actor, null, ct);
            items = await store.ItemsAsync(orderId, ct);
            // The money that goes back is documented: a credit note that corrects the invoice, with its own gapless number.
            if (await store.InvoiceOfOrderAsync(orderId, ct) is { } invoice)
            {
                creditNumber = ShopInvoice.CreditNumber(cfg, await store.NextAsync("credit", 1, ct));
                creditInvoice = invoice.Number;
                var doc = ShopInvoice.BuildCredit(o with { RefundedCents = now }, items, cfg, creditNumber, DateTime.UtcNow, invoice.Number, added);
                await store.AddInvoiceAsync(orderId, creditNumber, InvoiceKinds.Credit, ShopInvoice.Html(doc), ShopPdf.Render(doc), added, ct);
                await store.AddEventAsync(orderId, "credit", $"Gutschrift {creditNumber} zu Rechnung {invoice.Number} ({Money.Format(added, o.Currency)})", actor, null, ct);
            }
            if (now >= o.TotalCents && OrderStatus.CanMove(o.Status, OrderStatus.Refunded))
            {
                await store.SetStatusAsync(orderId, OrderStatus.Refunded, "", ct);
                await store.RevokeDownloadsAsync(orderId, ct);
                if (restock) foreach (var i in items) await store.ReturnStockAsync(i.ProductId, i.VariantId, i.Qty, ct);
            }
            order = (await store.OrderAsync(orderId, ct))!;
            return true;
        }, ct);
        if (!changed || order is null) return false;
        var creditLine = creditNumber.Length == 0 ? "" : (order.Locale == "en" ? $"Your credit note {creditNumber}: " : $"Ihre Gutschrift {creditNumber}: ") +
            $"{ctx.Site.Origin}/shop/invoice/{Uri.EscapeDataString(order.Number)}?t={Uri.EscapeDataString(order.AccessToken)}&doc={Uri.EscapeDataString(creditNumber)}";
        await ShopMail.SendAsync(ctx, new ShopStore(ctx.Data), ShopMail.Refunded, order, items, v => { v["refund_amount"] = Money.Format(added, order.Currency, order.Locale); v["credit_info"] = creditLine; }, ct: ct);
        await EmitAsync(ctx, "shop.order.refunded", order, ct);
        return true;
    }

    /// <summary>The customer's own account of events a plugin or an operator wants to react to is <c>shop.order.*</c>: placed, paid, shipped, completed, cancelled, refunded.</summary>
    public static Task EmitPlacedAsync(IPluginContext ctx, Order o, CancellationToken ct = default) => EmitAsync(ctx, "shop.order.placed", o, ct);
}
