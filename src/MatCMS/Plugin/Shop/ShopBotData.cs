using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using MatCMS.Services;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// What the shop tells a site-wide helper (the AI bot): its products for <c>site.search</c>, and — for <c>member.data</c> — the orders of ONE account.
/// <para>The account comes in the payload, and only from the HOST's side of the bot: it reads it from the signed-in member, never from the visitor's request
/// or the model. Only orders LINKED to the account are given (a guest order with the same address joins only once the address is confirmed, as in the
/// customer area), and only what the customer sees on his own order page — no payment references, no access keys, no other customer's address.</para>
/// </summary>
internal static class ShopBotData
{
    private const int MaxOrders = 10;

    public static async Task<JsonNode?> SearchAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        var terms = SiteSearch.Terms(payload?["q"]?.GetValue<string>() ?? "");
        if (terms.Count == 0) return null;
        var max = Math.Clamp(payload?["max"] is JsonValue m && m.TryGetValue<int>(out var n) ? n : 5, 1, 20);
        var cfg = ShopConfig.From(ctx.Settings);
        var (products, _) = await ShopStore.Public(ctx).FindProductsAsync(new ProductQuery { ActiveOnly = true, Take = 2000 }, ct);
        var items = new JsonArray();
        foreach (var x in products
                     .Select(p => (P: p, Text: SiteSearch.Plain(p.Summary + " " + p.DescriptionHtml + " " + p.Sku + " " + p.Category)))
                     .Select(x => (x.P, x.Text, Score: SiteSearch.Score(x.P.Title.ToLowerInvariant(), x.Text.ToLowerInvariant(), terms)))
                     .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(max))
        {
            var p = x.P;
            var facts = new StringBuilder(ctx.T("shop.bot.price", Money.Format(p.PriceCents, cfg.Currency, ctx.Locale)));
            if (p.CompareCents > p.PriceCents) facts.Append(' ').Append(ctx.T("shop.bot.was", Money.Format(p.CompareCents, cfg.Currency, ctx.Locale)));
            if (p.TrackStock) facts.Append(". ").Append(p.Stock > 0 ? ctx.T("shop.bot.inStock") : ctx.T("shop.bot.soldOut"));
            items.Add(new JsonObject
            {
                ["title"] = p.Title, ["url"] = ShopUrls.Product(p.Slug), ["score"] = x.Score, ["kind"] = ctx.T("shop.bot.kind"),
                ["text"] = facts + ". " + SiteSearch.ExcerptOf(System.Text.RegularExpressions.Regex.Replace(x.Text, @"\s+", " ").Trim(), terms)
            });
        }
        return items.Count == 0 ? null : new JsonObject { ["items"] = items };
    }

    public static async Task<JsonNode?> MemberDataAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        var id = payload?["member_id"] is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;
        if (id <= 0) return null;
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        var (orders, total) = await store.OrdersAsync(memberId: id, take: MaxOrders, ct: ct);
        var sb = new StringBuilder();
        if (orders.Count == 0) sb.Append(ctx.T("shop.bot.noOrders"));
        else sb.Append(ctx.T("shop.bot.ordersHead", orders.Count, total)).Append('\n');
        foreach (var o in orders)
        {
            var lines = await store.ItemsAsync(o.Id, ct);
            sb.Append("- ").Append(ctx.T("shop.orderNo", o.Number))
              .Append(" · ").Append(o.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))
              .Append(" · ").Append(ctx.T("shop.status." + o.Status))
              .Append(" · ").Append(Money.Format(o.TotalCents, o.Currency, ctx.Locale));
            if (o.RefundedCents > 0) sb.Append(" · ").Append(ctx.T("shop.bot.refunded", Money.Format(o.RefundedCents, o.Currency, ctx.Locale)));
            if (lines.Count > 0) sb.Append(" · ").Append(string.Join(", ", lines.Take(8).Select(l => $"{l.Qty}× {l.DisplayTitle}"))).Append(lines.Count > 8 ? " …" : "");
            if (o.Status == OrderStatus.Shipped && ShopTracking.UrlOf(cfg, o) is { Length: > 0 } track) sb.Append(" · ").Append(ctx.T("shop.bot.tracking", track));
            sb.Append(" · ").Append("/shop/order/").Append(Uri.EscapeDataString(o.Number)).Append('\n');
        }
        return new JsonObject { ["title"] = ctx.T("shop.myOrders"), ["text"] = sb.ToString().Trim(), ["url"] = "/konto/bestellungen" };
    }
}
