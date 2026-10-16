using System.Globalization;
using System.Text;
using MatCMS.Plugin;
using static MatCMS.Plugin.Shop.ShopHtml;

namespace MatCMS.Plugin.Shop;

internal static partial class ShopPublic
{
    /// <summary>
    /// "Direct" buttons under the cart's checkout button: the same checkout, with the way to pay already chosen. This is NOT a one-click wallet checkout — the address is still asked — but a visitor who
    /// wants to pay with PayPal or a card does not have to find the option; Stripe's own page offers Apple Pay and Google Pay where the device and the account allow it. Only for methods that are set up.
    /// </summary>
    internal static string ExpressHtml(IPluginContext ctx, ShopConfig cfg)
    {
        var buttons = new StringBuilder();
        if (cfg.PayPalReady) buttons.Append($"<a class=\"shop-btn ghost\" href=\"/shop/checkout?pay=paypal\" rel=\"nofollow\">{E(ctx.T("shop.express.paypal"))}</a>");
        if (cfg.StripeReady) buttons.Append($"<a class=\"shop-btn ghost\" href=\"/shop/checkout?pay=stripe\" rel=\"nofollow\">{E(ctx.T("shop.express.card"))}</a>");
        return buttons.Length == 0 ? "" : $"<div class=\"shop-express\"><span class=\"shop-small shop-muted\">{E(ctx.T("shop.express.or"))}</span>{buttons}</div>";
    }

    private const string TruckIcon = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M3 7h11v9H3zM14 10h4l3 3v3h-7\"/><circle cx=\"7\" cy=\"17.5\" r=\"1.7\"/><circle cx=\"17\" cy=\"17.5\" r=\"1.7\"/></svg>";
    private const string LockIcon = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><rect x=\"5\" y=\"11\" width=\"14\" height=\"9\" rx=\"2\"/><path d=\"M8 11V8a4 4 0 018 0v3\"/></svg>";
    private const string BackIcon = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M9 14L4 9l5-5\"/><path d=\"M4 9h10a6 6 0 010 12h-3\"/></svg>";
    private const string DownloadIcon = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M12 4v11M7 11l5 5 5-5M5 20h14\"/></svg>";

    /// <summary>
    /// The short list of reasons to buy that sits under the buy button: only what the shop's own settings make true — a free-shipping threshold that exists, a withdrawal page that is set, a payment method that is on.
    /// Nothing here is a claim the operator has not configured.
    /// </summary>
    internal static async Task<string> TrustHtmlAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, Product p, CancellationToken ct)
    {
        var items = new List<(string Icon, string Html)>();
        if (p.IsDigital) items.Add((DownloadIcon, E(ctx.T("shop.trust.instant"))));
        else
        {
            var rates = (await store.RatesAsync(true, ct)).Where(r => r.Active && r.Covers(cfg.HomeCountry)).ToList();
            var free = rates.Where(r => r.FreeFromCents > 0).Select(r => r.FreeFromCents).DefaultIfEmpty(0).Min();
            if (free > 0) items.Add((TruckIcon, E(ctx.T("shop.trust.freeFrom", M(ctx, cfg, free)))));
            else if (rates.Count > 0) items.Add((TruckIcon, E(ctx.T("shop.trust.ships"))));
        }
        if (cfg.StripeReady || cfg.PayPalReady || cfg.BankEnabled) items.Add((LockIcon, E(ctx.T("shop.trust.secure"))));
        if (cfg.WithdrawalUrl.Length > 0 && SafeUrl(cfg.WithdrawalUrl).Length > 0) items.Add((BackIcon, $"<a href=\"{E(SafeUrl(cfg.WithdrawalUrl))}\">{E(ctx.T("shop.trust.withdrawal"))}</a>"));
        return items.Count == 0 ? "" : "<ul class=\"shop-trust\">" + string.Concat(items.Select(i => $"<li>{i.Icon}<span>{i.Html}</span></li>")) + "</ul>";
    }

    /// <summary>The countries a visitor can ask about in the cart: those some active shipping rate covers (all known ones when a rate says "everywhere").</summary>
    internal static IReadOnlyList<string> EstimateCountries(IReadOnlyList<ShippingRate> rates)
    {
        var active = rates.Where(r => r.Active).ToList();
        if (active.Any(r => r.Countries.Trim() == "*")) return Countries.All.Select(c => c.Code).ToList();
        return Countries.All.Select(c => c.Code).Where(c => active.Any(r => r.Covers(c))).ToList();
    }

    /// <summary>The country the estimate is made for: the one asked for if it is on offer, else the shop's home country if it is, else the first on offer — or "" when nothing is on offer.</summary>
    internal static string EstimateCountry(string? asked, string home, IReadOnlyList<string> offered)
    {
        var a = (asked ?? "").Trim().ToUpperInvariant();
        if (a.Length > 0 && offered.Contains(a)) return a;
        if (offered.Contains(home.ToUpperInvariant())) return home.ToUpperInvariant();
        return offered.FirstOrDefault() ?? "";
    }

    /// <summary>How much more the goods must cost for the nearest free-shipping threshold of a rate that serves this country and weight; 0 when shipping is free already or no threshold applies.</summary>
    internal static long UntilFreeShipping(IReadOnlyList<ShippingRate> rates, string country, PricedCart cart) =>
        rates.Where(r => r.Active && r.Covers(country) && r.FreeFromCents > cart.SubtotalCents && (r.MaxWeightGrams <= 0 || cart.WeightGrams <= r.MaxWeightGrams))
            .Select(r => r.FreeFromCents - cart.SubtotalCents).DefaultIfEmpty(0).Min();

    /// <summary>
    /// "Versand schätzen" in the cart: pick a country, see what each way of shipping costs there and what the order would come to — before the checkout asks for an address. The page is a plain GET
    /// (<c>?ship=AT</c>), so it works without script and can be linked; nothing is stored. The price is the one the checkout will charge: the same rates, the same tax rule for that country.
    /// </summary>
    internal static async Task<string> ShippingEstimateAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, PricedCart priced, CancellationToken ct)
    {
        if (!priced.NeedsShipping) return "";
        var rates = await store.RatesAsync(true, ct);
        var offered = EstimateCountries(rates);
        var country = EstimateCountry(ctx.Request.Q("ship"), cfg.HomeCountry, offered);
        if (country.Length == 0) return "";
        var forCountry = ShopTax.Apply(priced, cfg, country, country);
        var options = ShopOrders.ShippingOptions(rates, country, forCountry);
        var sb = new StringBuilder("<div class=\"shop-est\"><form method=\"get\" action=\"/shop/cart\" class=\"shop-est-form\">");
        sb.Append($"<label for=\"shop-est-country\">{E(ctx.T("shop.est.title"))}</label><select id=\"shop-est-country\" name=\"ship\" onchange=\"this.form.submit()\">");
        foreach (var c in offered) sb.Append($"<option value=\"{E(c)}\"{(c == country ? " selected" : "")}>{E(Countries.Name(c, ctx.Locale))}</option>");
        sb.Append($"</select><noscript> <button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.est.show"))}</button></noscript></form>");
        if (options.Count == 0) sb.Append($"<p class=\"shop-small shop-muted\">{E(ctx.T("shop.est.none", Countries.Name(country, ctx.Locale)))}</p>");
        else
        {
            sb.Append("<ul class=\"shop-est-list\">");
            foreach (var o in options) sb.Append($"<li><span>{E(o.Rate.Name)}</span><span>{E(o.PriceCents == 0 ? ctx.T("shop.pay.free") : M(ctx, cfg, o.PriceCents))}</span></li>");
            sb.Append("</ul>");
            var cheapest = options[0];
            if (!forCountry.FreeShipping)
            {
                var total = ShopOrders.Compute(forCountry, cheapest, cfg).Total;
                sb.Append($"<p class=\"shop-est-total\"><span>{E(ctx.T("shop.est.total"))}</span><strong>{E(M(ctx, cfg, total))}</strong></p>");
            }
            if (!forCountry.FreeShipping && UntilFreeShipping(rates, country, forCountry) is > 0 and var missing)
                sb.Append($"<p class=\"shop-small\">{E(ctx.T("shop.est.untilFree", M(ctx, cfg, missing)))}</p>");
        }
        sb.Append($"<p class=\"shop-small shop-muted\">{E(ctx.T("shop.est.note"))}</p></div>");
        return sb.ToString();
    }
}
