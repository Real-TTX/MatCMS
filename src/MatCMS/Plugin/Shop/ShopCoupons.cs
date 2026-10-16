using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>What looking at a coupon answered: the cart with the coupon applied (or unchanged), the coupon, and — when it does not apply — the sentence that says why.</summary>
internal sealed record CouponResult(PricedCart Cart, Coupon? Coupon, string? Problem);

/// <summary>
/// Coupon codes: whether one applies to a cart, and what it takes off. The discount is worked out ON THE GOODS (never shipping, except the "free shipping" kind) and
/// spread over the lines in proportion to their value, so the VAT of every line is the VAT of what is actually paid — the invoice adds up to the cent at every rate.
/// </summary>
internal static class ShopCoupons
{
    // ---- guessing --------------------------------------------------------------------------------------

    /// <summary>A code like SOMMER10 can be guessed. Wrong tries are counted per address; after ten in ten minutes the address is told to wait (the route's own limit is far too loose for this).</summary>
    private const int MaxFailures = 10;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Window, int Fails)> Attempts = new();
    private static long Window() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 600;

    public static bool AttemptAllowed(string client) => !Attempts.TryGetValue(client, out var a) || a.Window != Window() || a.Fails < MaxFailures;

    public static void RecordFailure(string client)
    {
        var w = Window();
        if (Attempts.Count > 2000) foreach (var kv in Attempts.Where(kv => kv.Value.Window != w).ToList()) Attempts.TryRemove(kv.Key, out _);
        Attempts.AddOrUpdate(client, (w, 1), (_, cur) => cur.Window == w ? (w, cur.Fails + 1) : (w, 1));
    }

    /// <summary>Forgets every count (tests start clean).</summary>
    internal static void ResetAttempts() => Attempts.Clear();

    /// <summary>A coupon limited to a category covers that category and everything below it; <paramref name="scope"/> is that set (null = just the category itself).</summary>
    private static bool Eligible(Coupon c, PricedLine l, IReadOnlySet<string>? scope) => c.Category.Length == 0 || (scope?.Contains(l.Product.Category) ?? l.Product.Category == c.Category);

    private static async Task<IReadOnlySet<string>?> ScopeAsync(ShopStore store, Coupon c, CancellationToken ct) =>
        c.Category.Length == 0 ? null : ShopCategories.SlugsUnder(await store.CategoriesAsync(ct), c.Category);

    /// <summary>The goods this coupon may reduce.</summary>
    private static long EligibleGoods(Coupon c, PricedCart cart, IReadOnlySet<string>? scope) => cart.Lines.Where(l => Eligible(c, l, scope)).Sum(l => l.LineCents);

    /// <summary>The sentence that says why the coupon cannot be used for this cart, or null when it can. <paramref name="email"/> is empty while the buyer's address is not known yet.</summary>
    public static async Task<string?> ProblemAsync(IPluginContext ctx, ShopStore store, Coupon c, PricedCart cart, string email, ShopConfig cfg, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // A coupon that is switched off is "unknown" — nobody learns which codes exist.
        if (!c.Active) return ctx.T("shop.coupon.unknown");
        if (c.StartsUtc is { } from && from > now) return ctx.T("shop.coupon.notYet", from.ToLocalTime().ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture));
        if (c.ExpiresUtc is { } until && until < now) return ctx.T("shop.coupon.expired");
        if (c.MaxUses > 0 && c.Used >= c.MaxUses) return ctx.T("shop.coupon.usedUp");
        if (c.Kind == CouponKind.Shipping)
        {
            if (!cart.NeedsShipping) return ctx.T("shop.coupon.noShipping");
        }
        else if (EligibleGoods(c, cart, await ScopeAsync(store, c, ct)) <= 0) return ctx.T("shop.coupon.nothingEligible");
        if (c.MinOrderCents > 0 && cart.SubtotalCents < c.MinOrderCents) return ctx.T("shop.coupon.minOrder", Money.Format(c.MinOrderCents, cfg.Currency, ctx.Locale));
        if (c.OncePerCustomer && email.Length > 0 && await store.CouponOrdersOfAsync(c.Code, email, ct) > 0) return ctx.T("shop.coupon.onceOnly");
        return null;
    }

    /// <summary>The cart with the coupon applied. Call only for a coupon that <see cref="ProblemAsync"/> accepted.</summary>
    public static PricedCart Apply(PricedCart cart, Coupon c, IReadOnlySet<string>? scope = null)
    {
        if (c.Kind == CouponKind.Shipping) return cart with { CouponCode = c.Code, FreeShipping = true };

        var eligible = cart.Lines.Select(l => Eligible(c, l, scope) ? l.LineCents : 0L).ToList();
        var basis = eligible.Sum();
        var discount = c.Kind == CouponKind.Percent
            ? (long)Math.Round(basis * (decimal)Math.Clamp(c.Value, 0, 10000) / 10000m, MidpointRounding.AwayFromZero)
            : Math.Min(Math.Max(0, c.Value), basis);
        discount = Math.Min(discount, basis);
        var parts = Money.Allocate(discount, eligible);
        var lines = cart.Lines.Select((l, i) => l with { DiscountCents = parts[i], TaxCents = Money.TaxIncluded(l.LineCents - parts[i], l.TaxBp) }).ToList();
        return cart with { Lines = lines, LineTaxCents = lines.Sum(l => l.TaxCents), DiscountCents = parts.Sum(), CouponCode = c.Code };
    }

    /// <summary>Looks a typed code up and applies it to the cart; when it does not apply, the cart comes back unchanged with the reason.</summary>
    public static async Task<CouponResult> ResolveAsync(IPluginContext ctx, ShopStore store, PricedCart cart, string code, string email, CancellationToken ct)
    {
        if (code.Length == 0) return new CouponResult(cart, null, null);
        var cfg = ShopConfig.From(ctx.Settings);
        var coupon = await store.CouponByCodeAsync(code, ct);
        if (coupon is null) return new CouponResult(cart, null, ctx.T("shop.coupon.unknown"));
        var problem = await ProblemAsync(ctx, store, coupon, cart, email, cfg, ct);
        return problem is not null ? new CouponResult(cart, null, problem) : new CouponResult(Apply(cart, coupon, await ScopeAsync(store, coupon, ct)), coupon, null);
    }

    /// <summary>"10 %", "5,00 €" or "Versand frei" — what a coupon is worth, for lists and notes.</summary>
    public static string Describe(IPluginContext ctx, Coupon c, string currency) => c.Kind switch
    {
        CouponKind.Percent => Money.Percent((int)c.Value) + " %",
        CouponKind.Fixed => Money.Format(c.Value, currency, ctx.Locale),
        _ => ctx.T("shop.coupon.kind.shipping")
    };
}
