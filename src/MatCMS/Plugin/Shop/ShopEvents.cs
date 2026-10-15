using System.Globalization;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using MatCMS.Services;

namespace MatCMS.Plugin.Shop;

/// <summary>What the shop does on its own: the host's account events (an account deleted, exported, confirmed), a payment another plugin reports, the timed upkeep, and the
/// "write a product" operation the cloud and agents may ask for.</summary>
internal static class ShopEvents
{
    private static long IdOf(JsonNode? payload) => payload?["id"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : 0;

    /// <summary>
    /// An account is being deleted. The shop forgets what it can — saved addresses and carts — and unlinks the orders. The ORDERS stay, with their invoices and the address
    /// they were billed to: a seller is bound by law to keep those for years, and that duty is a lawful ground for keeping exactly that data (and no more).
    /// </summary>
    public static async Task EraseAsync(IPluginContext ctx, JsonNode? payload)
    {
        var id = IdOf(payload);
        if (id <= 0) return;
        var store = new ShopStore(ctx.Data);
        await store.DeleteAddressesOfMemberAsync(id);
        await store.DeleteCartsOfMemberAsync(id);
        await store.UnlinkMemberAsync(id);
    }

    public static async Task<JsonNode?> ExportAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        var id = IdOf(payload);
        if (id <= 0) return null;
        var data = await ShopAccount.ExportAsync(new ShopStore(ctx.Data), id, payload?["email"]?.GetValue<string>() ?? "", ct);
        return new JsonObject { ["plugin"] = "shop", ["data"] = data };
    }

    /// <summary>An account's address was confirmed: the orders somebody placed as a guest with that address are theirs now, and show in their account.</summary>
    public static async Task ConfirmedAsync(IPluginContext ctx, JsonNode? payload)
    {
        var id = IdOf(payload);
        var email = (payload?["email"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
        if (id > 0 && email.Length > 0) await new ShopStore(ctx.Data).LinkGuestOrdersAsync(email, id);
    }

    /// <summary>Another plugin's payment provider reports that an order was paid: <c>{ "order": "1001", "ref": "…" }</c>.</summary>
    public static async Task ExternalPaidAsync(IPluginContext ctx, JsonNode? payload)
    {
        var number = payload?["order"]?.GetValue<string>() ?? "";
        var store = new ShopStore(ctx.Data);
        if (number.Length == 0 || await store.OrderByNumberAsync(number) is not { } order) return;
        await ShopOrders.MarkPaidAsync(ctx, ShopConfig.From(ctx.Settings), order.Id, "extern", "Zahlung gemeldet" + (payload?["ref"]?.GetValue<string>() is { Length: > 0 } r ? " (" + r + ")" : ""), payload?["ref"]?.GetValue<string>());
    }

    // ---- upkeep ------------------------------------------------------------------------------------------

    /// <summary>
    /// Every fifteen minutes: an order whose time ran out is cancelled and its stock goes back (for Stripe, Stripe is asked first — the money may have arrived while the
    /// notification did not), an order Stripe has not reported for ten minutes is checked with Stripe, and old carts are dropped.
    /// </summary>
    public static async Task MaintenanceAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        var now = DateTime.UtcNow;

        var due = await store.OrdersWhereAsync(DataQuery.Everything.Eq("status", OrderStatus.AwaitingPayment).Where("reserved_until", DataOp.Lt, now).Page(0, 200), ct);
        foreach (var o in due)
        {
            if (o.PaymentProvider == Payments.Stripe) await StripePayments.ReconcileAsync(ctx, cfg, o, ct);
            else if (o.PaymentProvider == Payments.PayPal) await PayPalPayments.ReconcileAsync(ctx, cfg, o, ct);
            var fresh = await store.OrderAsync(o.Id, ct);
            if (fresh is { Status: OrderStatus.AwaitingPayment })
                await ShopOrders.CancelAsync(ctx, cfg, o.Id, "expired", "system", o.PaymentProvider == Payments.Bank ? "Zahlungsfrist abgelaufen" : "Nicht bezahlt, Reservierung abgelaufen", notifyCustomer: o.PaymentProvider == Payments.Bank, ct: ct);
        }

        if (cfg.StripeReady)
        {
            var waiting = await store.OrdersWhereAsync(DataQuery.Everything.Eq("status", OrderStatus.AwaitingPayment).Eq("payment_provider", Payments.Stripe)
                .Where("created", DataOp.Lt, now.AddMinutes(-10)).Where("reserved_until", DataOp.Ge, now).Page(0, 50), ct);
            foreach (var o in waiting) await StripePayments.ReconcileAsync(ctx, cfg, o, ct);
        }
        if (cfg.PayPalReady)
        {
            var waiting = await store.OrdersWhereAsync(DataQuery.Everything.Eq("status", OrderStatus.AwaitingPayment).Eq("payment_provider", Payments.PayPal)
                .Where("created", DataOp.Lt, now.AddMinutes(-10)).Where("reserved_until", DataOp.Ge, now).Page(0, 50), ct);
            foreach (var o in waiting) await PayPalPayments.ReconcileAsync(ctx, cfg, o, ct);
        }
        await store.DeleteExpiredCartsAsync(ct);
    }

    // ---- the cloud's / an agent's "write a product" ----------------------------------------------------------

    public static async Task<JsonNode?> CreateProductAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        static JsonObject Answer(string status, string message) => new() { ["status"] = status, ["message"] = message };
        var cfg = ShopConfig.From(ctx.Settings);
        var title = (o["title"]?.GetValue<string>() ?? "").Trim();
        if (title.Length == 0) return Answer("failed", "Ein Produkt braucht einen Titel.");
        long cents;
        if (o["price_cents"] is JsonValue pc && pc.TryGetValue<long>(out var c)) cents = c;
        else if (!Money.TryParse(o["price"]?.ToString(), out cents)) return Answer("failed", "Der Preis fehlt (price_cents oder price).");
        if (cents < 0) return Answer("failed", "Der Preis darf nicht negativ sein.");
        var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(o["slug"]?.GetValue<string>() ?? title);
        if (slug.Length == 0 || ShopPublic.ReservedSlugs.Contains(slug)) slug = "produkt-" + Guid.NewGuid().ToString("N")[..6];
        var store = new ShopStore(ctx.Data);
        if (await store.ProductBySlugAsync(slug, ct) is not null) return Answer("skipped-exists", $"Es gibt schon ein Produkt „{slug}“.");
        var bp = o["tax_percent"] is { } tp && Money.TryParsePercent(tp.ToString(), out var b) ? b : cfg.DefaultTaxBp;
        var stock = o["stock"] is JsonValue sv && sv.TryGetValue<long>(out var s) && s >= 0 ? s : (long?)null;
        // A category named by an agent is found by its address and made when it is new, so "put it in Kaffee" just works.
        var category = "";
        if ((o["category"]?.GetValue<string>() ?? "").Trim() is { Length: > 0 } catName)
        {
            var catSlug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(catName);
            if (catSlug.Length > 0)
            {
                if (await store.CategoryBySlugAsync(catSlug, ct) is null) await store.AddCategoryAsync(catSlug, catName, (await store.CategoriesAsync(ct)).Count, ct);
                category = catSlug;
            }
        }
        long compare = 0;
        if (o["compare_cents"] is JsonValue cc && cc.TryGetValue<long>(out var cmp) && cmp > 0) compare = cmp;
        else if (o["compare_price"] is { } cp && Money.TryParse(cp.ToString(), out var cmp2) && cmp2 > 0) compare = cmp2;
        var tags = o["tags"] is JsonArray ta ? string.Join(", ", ta.Select(x => x?.ToString() ?? "")) : o["tags"]?.ToString() ?? "";
        await store.SaveProductAsync(new Product
        {
            Category = category, Tags = ShopTags.Normalise(tags), Featured = o["featured"] is JsonValue fv && fv.TryGetValue<bool>(out var feat) && feat,
            Slug = slug, Title = title, Summary = (o["summary"]?.GetValue<string>() ?? "").Trim(), DescriptionHtml = SafeHtml.Sanitize(o["description_html"]?.GetValue<string>()),
            Kind = ProductKind.Normalise(o["kind"]?.GetValue<string>()), PriceCents = cents, CompareCents = compare > cents ? compare : 0, TaxBp = bp, Sku = o["sku"]?.GetValue<string>() ?? "",
            TrackStock = stock is not null, Stock = stock ?? 0, Active = o["publish"]?.GetValue<bool>() ?? true
        }, ct);
        return Answer("applied", $"Produkt „{title}“ angelegt ({Money.Format(cents, cfg.Currency)}, /shop/{slug}).");
    }

    /// <summary>The cloud's / an agent's "write a coupon": the same rules as the form (a code that exists is skipped, never overwritten).</summary>
    public static async Task<JsonNode?> CreateCouponAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        static JsonObject Answer(string status, string message) => new() { ["status"] = status, ["message"] = message };
        var cfg = ShopConfig.From(ctx.Settings);
        var kind = CouponKind.Normalise(o["kind"]?.GetValue<string>());
        long value = 0;
        if (kind == CouponKind.Percent)
        {
            if (!Money.TryParsePercent(o["percent"]?.ToString(), out var bp) || bp <= 0) return Answer("failed", "Ein Prozent-Gutschein braucht \"percent\" zwischen 0 und 100.");
            value = bp;
        }
        else if (kind == CouponKind.Fixed)
        {
            if (o["amount_cents"] is JsonValue ac && ac.TryGetValue<long>(out var cents) && cents > 0) value = cents;
            else if (!Money.TryParse(o["amount"]?.ToString(), out value) || value <= 0) return Answer("failed", "Ein Festbetrag-Gutschein braucht \"amount_cents\" oder \"amount\" größer als 0.");
        }
        var code = Coupon.NormaliseCode(o["code"]?.GetValue<string>());
        if (code.Length == 0) code = ShopAdminCoupons.RandomCode();
        long min = 0;
        if (o["min_order_cents"] is JsonValue mc && mc.TryGetValue<long>(out var m) && m > 0) min = m;
        DateTime? expires = null;
        if (o["expires"]?.GetValue<string>() is { Length: > 0 } ex)
        {
            if (!DateTime.TryParseExact(ex, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return Answer("failed", "\"expires\" muss ein Datum der Form JJJJ-MM-TT sein.");
            expires = DateTime.SpecifyKind(d.AddDays(1).AddSeconds(-1), DateTimeKind.Local).ToUniversalTime();
        }
        var store = new ShopStore(ctx.Data);
        var category = "";
        if ((o["category"]?.GetValue<string>() ?? "").Trim() is { Length: > 0 } cat)
        {
            var catSlug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(cat);
            if (await store.CategoryBySlugAsync(catSlug, ct) is null) return Answer("failed", $"Die Kategorie „{cat}“ gibt es nicht.");
            category = catSlug;
        }
        var saved = await store.SaveCouponAsync(new Coupon
        {
            Code = code, Kind = kind, Value = value, MinOrderCents = min, Category = category, ExpiresUtc = expires, Note = (o["note"]?.GetValue<string>() ?? "").Trim(),
            MaxUses = o["max_uses"] is JsonValue mu && mu.TryGetValue<int>(out var u) && u > 0 ? u : 0,
            OncePerCustomer = o["once_per_customer"] is JsonValue oc && oc.TryGetValue<bool>(out var once) && once, Active = true
        }, ct);
        return saved.Ok ? Answer("applied", $"Gutschein {code} angelegt ({ShopCoupons.Describe(ctx, new Coupon { Kind = kind, Value = value }, cfg.Currency)}).")
                        : Answer("skipped-exists", $"Es gibt schon einen Gutschein {code}.");
    }
}
