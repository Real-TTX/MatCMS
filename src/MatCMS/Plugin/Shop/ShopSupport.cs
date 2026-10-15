using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>Who may see an order, an invoice or a download.</summary>
internal static class ShopAccess
{
    /// <summary>The order's secret link key, its owner (the signed-in customer it belongs to) or the administrator. The key is compared in constant time.</summary>
    public static bool May(IPluginContext ctx, Order o, string? token)
    {
        if (ctx.User.IsAdmin) return true;
        if (ctx.Member is { } m && o.MemberId > 0 && m.Id == o.MemberId) return true;
        if (string.IsNullOrEmpty(token) || o.AccessToken.Length == 0) return false;
        var a = Encoding.UTF8.GetBytes(token); var b = Encoding.UTF8.GetBytes(o.AccessToken);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>The cart cookie: an opaque token that names a cart row. Nothing about the cart is in the cookie.</summary>
internal static class ShopCart
{
    public const string CookieName = "cart";
    private static readonly System.Text.RegularExpressions.Regex Valid = new("^[A-Za-z0-9_-]{20,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The token the request carries, or "" (also for one that does not look like ours — a visitor can send anything in a cookie).</summary>
    public static string Token(IPluginContext ctx) => ctx.Request.Cookie(CookieName) is { } t && Valid.IsMatch(t) ? t : "";

    public static async Task<IReadOnlyList<CartLine>> LinesAsync(IPluginContext ctx, ShopStore store, CancellationToken ct = default)
    {
        var token = Token(ctx);
        return token.Length == 0 ? Array.Empty<CartLine>() : (await store.CartAsync(token, ct))?.Lines ?? Array.Empty<CartLine>();
    }

    public static PluginResult WithCartCookie(PluginResult r, string token) => r.WithCookie(CookieName, token, TimeSpan.FromDays(30));
}

/// <summary>Digital goods: who gets a file, how it is sent.</summary>
internal static class ShopDownloads
{
    /// <summary>The unique key of a (order, file) pair in the downloads table. Not a secret — the secret is the order's access key.</summary>
    public static string RowHash(long orderId, long fileId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"dl:{orderId}:{fileId}"))).ToLowerInvariant();

    private static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".zip" => "application/zip", ".epub" => "application/epub+zip", ".mp3" => "audio/mpeg", ".mp4" => "video/mp4",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".txt" => "text/plain; charset=utf-8", ".csv" => "text/csv; charset=utf-8", _ => "application/octet-stream"
    };

    /// <summary>The handler of <c>GET /shop/download/{number}/{fileId}?t=…</c>.</summary>
    public static async Task<PluginResult> ServeAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var order = await store.OrderByNumberAsync(ctx.Request.R("number"), ct);
        if (order is null || !long.TryParse(ctx.Request.R("fileId"), out var fileId) || !ShopAccess.May(ctx, order, ctx.Request.Q("t"))) return PluginResult.NotFound();
        // Only a paid order delivers: an order that is waiting, cancelled or refunded has no download, whatever link somebody holds.
        if (!OrderStatus.IsPaid(order.Status)) return PluginResult.Text(ctx.T("shop.dl.notPaid"), status: 403);

        var row = await store.DownloadByHashAsync(RowHash(order.Id, fileId), ct);
        if (row is null || row.Revoked) return PluginResult.NotFound();
        if (row.ExpiresUtc < DateTime.UtcNow) return PluginResult.Text(ctx.T("shop.dl.expired"), status: 410);
        var file = await store.FileAsync(fileId, ct);
        if (file is null) return PluginResult.NotFound();

        // The administrator looking at a customer's file does not use up the customer's downloads.
        if (!ctx.User.IsAdmin && !await store.TryCountDownloadAsync(row, ct)) return PluginResult.Text(ctx.T("shop.dl.limit"), status: 410);
        var stream = await ctx.Files.OpenAsync(file.StorageName, ct);
        if (stream is null) { ctx.Log($"Download {order.Number}: Datei {file.StorageName} fehlt"); return PluginResult.NotFound(); }
        await store.AddEventAsync(order.Id, "download", $"Download: {file.FileName}", ctx.User.IsAdmin ? "admin" : "customer", null, ct);
        return PluginResult.FromStream(stream, ContentType(file.FileName), file.FileName).WithHeader("X-Content-Type-Options", "nosniff").WithHeader("Cache-Control", "private, no-store");
    }
}

/// <summary>The HTML of the public pages: one small stylesheet, the page shell, and the helpers every page uses. The classes are the shop's own (<c>shop-*</c>); colours
/// come from variables with plain fallbacks, so a template can restyle everything with a few lines (and the operator can add their own CSS in the settings).</summary>
internal static class ShopHtml
{
    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    public static string Intl(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>What a list of products needs, on the shop's own pages AND inside a page block: cards, the list layout, columns, keywords, the buttons. The blocks bring it themselves
    /// (a page has no shop stylesheet), scoped under <c>.shop</c> so it cannot touch the rest of the site.</summary>
    internal const string CardCss = """
        .shop{max-width:1100px;margin:0 auto;padding:24px 16px;color:inherit}.shop.shop-block{max-width:none;padding:0}
        .shop h1{margin:0 0 16px}.shop h2{margin:28px 0 12px}.shop.shop-block h2{margin-top:0}.shop a{color:var(--shop-link,inherit)}
        .shop-muted{opacity:.7}.shop-small{font-size:.85em}
        .shop-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:20px}
        .shop-grid.cols-2{grid-template-columns:repeat(2,minmax(0,1fr))}.shop-grid.cols-3{grid-template-columns:repeat(3,minmax(0,1fr))}.shop-grid.cols-4{grid-template-columns:repeat(4,minmax(0,1fr))}
        @media(max-width:900px){.shop-grid.cols-3,.shop-grid.cols-4{grid-template-columns:repeat(2,minmax(0,1fr))}}
        @media(max-width:520px){.shop-grid.cols-2,.shop-grid.cols-3,.shop-grid.cols-4{grid-template-columns:minmax(0,1fr)}}
        .shop-card{display:flex;flex-direction:column;border:1px solid var(--shop-line,rgba(0,0,0,.12));border-radius:10px;overflow:hidden;background:var(--shop-card,transparent)}
        .shop-card .img{aspect-ratio:4/3;background:rgba(0,0,0,.06) center/cover no-repeat}.shop-card .body{padding:12px 14px;display:flex;flex-direction:column;gap:6px;flex:1}
        .shop-card .title{font-weight:600;text-decoration:none}.shop-price{font-weight:700}
        .shop-list{display:grid;gap:14px}.shop-list .shop-card{flex-direction:row}.shop-list .shop-card .img{aspect-ratio:auto;width:170px;min-height:130px;flex:none}
        @media(max-width:520px){.shop-list .shop-card .img{width:100px;min-height:100px}}
        .shop-feature{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:28px;align-items:center}@media(max-width:700px){.shop-feature{grid-template-columns:minmax(0,1fr)}}
        .shop-feature .img{display:block;aspect-ratio:4/3;border-radius:10px;background:rgba(0,0,0,.06) center/cover no-repeat}.shop-feature h3{margin:0 0 8px;font-size:1.5em}
        .shop-tags{display:flex;gap:6px;flex-wrap:wrap}.shop-tags a{padding:2px 10px;border:1px solid var(--shop-line,rgba(0,0,0,.2));border-radius:99px;font-size:.8em;text-decoration:none}
        .shop-tags a.on{background:var(--shop-accent,#1a1a1a);color:var(--shop-on-accent,#fff)}
        .shop-btn,.shop a.shop-btn{display:inline-block;padding:10px 18px;border-radius:8px;border:1px solid var(--shop-accent,#1a1a1a);background:var(--shop-accent,#1a1a1a);color:var(--shop-on-accent,#fff);font:inherit;cursor:pointer;text-decoration:none}
        .shop-btn.ghost,.shop a.shop-btn.ghost{background:transparent;color:inherit}.shop-btn[disabled]{opacity:.5;cursor:not-allowed}
        .shop-badge{display:inline-block;padding:2px 10px;border-radius:99px;font-size:.8em;background:rgba(0,0,0,.08)}.shop-badge.ok{background:#d9f0dc;color:#1d5b28}.shop-badge.warn{background:#fdf0cf;color:#6b4e00}.shop-badge.bad{background:#f8d9d9;color:#8a1f1f}
        .shop-more{margin-top:16px}.shop-was{font-weight:400;opacity:.6;margin-left:4px}.shop-coupon{margin-top:18px;display:flex;gap:8px;align-items:center;flex-wrap:wrap}
        .shop-coupon input[type=text]{padding:9px 12px;border:1px solid rgba(0,0,0,.25);border-radius:6px;font:inherit;text-transform:uppercase}
        """;

    private const string BaseCss = CardCss + """
        .shop-nav{display:flex;gap:18px;flex-wrap:wrap;align-items:center;margin-bottom:24px;padding-bottom:12px;border-bottom:1px solid var(--shop-line,rgba(0,0,0,.12))}
        .shop-nav .spacer{flex:1}
        .shop-filter{display:flex;gap:10px;flex-wrap:wrap;align-items:center;margin-bottom:16px}.shop-filter input,.shop-filter select{padding:10px 12px;border:1px solid rgba(0,0,0,.25);border-radius:8px;font:inherit;background:transparent;color:inherit}
        .shop-crumbs{font-size:.9em;margin:0 0 10px;opacity:.8}
        .shop-product{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:32px}@media(max-width:760px){.shop-product{grid-template-columns:1fr}}
        .shop-product.single{grid-template-columns:1fr;max-width:720px}.shop-product .main-img{width:100%;border-radius:10px;background:rgba(0,0,0,.06)}.shop-gallery{display:flex;gap:8px;flex-wrap:wrap;margin-top:8px}.shop-gallery img{width:72px;height:72px;object-fit:cover;border-radius:6px}
        .shop table{width:100%;border-collapse:collapse}.shop th,.shop td{padding:10px 8px;border-bottom:1px solid var(--shop-line,rgba(0,0,0,.12));text-align:left;vertical-align:top}
        .shop td.r,.shop th.r{text-align:right;white-space:nowrap}.shop-sum{max-width:380px;margin-left:auto;margin-top:16px}.shop-sum div{display:flex;justify-content:space-between;gap:14px;padding:4px 0}.shop-sum .total{font-weight:700;border-top:2px solid currentColor;margin-top:6px;padding-top:8px}
        .shop-form{display:grid;gap:14px;max-width:640px}.shop-form label{display:block;font-size:.9em;margin-bottom:4px}
        .shop-form input[type=text],.shop-form input[type=email],.shop-form input[type=password],.shop-form input[type=tel],.shop-form input[type=number],.shop-form select,.shop-form textarea{width:100%;padding:10px 12px;border:1px solid var(--shop-line,rgba(0,0,0,.25));border-radius:8px;font:inherit;box-sizing:border-box;background:transparent;color:inherit}
        .shop-row{display:grid;grid-template-columns:1fr 1fr;gap:12px}@media(max-width:560px){.shop-row{grid-template-columns:1fr}}
        .shop-check{display:flex;gap:8px;align-items:flex-start}.shop-check input{margin-top:4px}
        .shop-alert{padding:12px 14px;border-radius:8px;margin:0 0 16px;border:1px solid}.shop-alert.error{background:#fdecec;color:#8a1f1f;border-color:#f3c0c0}.shop-alert.ok{background:#eef8ee;color:#1d5b28;border-color:#bfe3c4}.shop-alert.info{background:#eef3fb;color:#1f3f6e;border-color:#c5d6ef}
        .shop-box{border:1px solid var(--shop-line,rgba(0,0,0,.12));border-radius:10px;padding:16px;margin:0 0 16px}.shop-pre{white-space:pre-line}
        .shop-pager{display:flex;gap:6px;margin-top:24px;flex-wrap:wrap}.shop-pager a,.shop-pager span{padding:6px 12px;border:1px solid var(--shop-line,rgba(0,0,0,.2));border-radius:6px;text-decoration:none}.shop-pager .on{background:var(--shop-accent,#1a1a1a);color:var(--shop-on-accent,#fff)}
        .shop-cats{display:flex;gap:8px;flex-wrap:wrap;margin-bottom:20px}.shop-cats a{padding:6px 14px;border:1px solid var(--shop-line,rgba(0,0,0,.2));border-radius:99px;text-decoration:none}.shop-cats a.on{background:var(--shop-accent,#1a1a1a);color:var(--shop-on-accent,#fff)}
        .shop-qty{width:70px!important}.shop-actions{display:flex;gap:10px;flex-wrap:wrap;align-items:center}
        """;

    public static string Alert(string text, string kind = "info") => $"<div class=\"shop-alert {kind}\">{E(text)}</div>";

    /// <summary>The page: the shop's stylesheet, its small navigation, the content. Raw HTML by design — every value in <paramref name="body"/> was encoded where it was written.</summary>
    public static async Task<PluginResult> PageAsync(IPluginContext ctx, ShopConfig cfg, string title, string body, string? description = null, int status = 200, bool noIndex = false)
    {
        var store = new ShopStore(ctx.Data);
        var count = (await ShopCart.LinesAsync(ctx, store)).Sum(l => l.Qty);
        var nav = new StringBuilder("<nav class=\"shop-nav\">");
        nav.Append($"<a href=\"/shop\">{E(ctx.T("shop.nav.shop"))}</a>");
        nav.Append($"<a href=\"/shop/cart\">{E(ctx.T("shop.nav.cart"))}{(count > 0 ? " (" + count.ToString(CultureInfo.InvariantCulture) + ")" : "")}</a>");
        nav.Append("<span class=\"spacer\"></span>");
        if (ctx.Member is { } m) nav.Append($"<a href=\"/konto\">{E(ctx.T("shop.nav.account"))}</a>");
        else nav.Append($"<a href=\"/anmelden?returnUrl=%2Fkonto\">{E(ctx.T("shop.nav.signin"))}</a>");
        nav.Append("</nav>");
        var html = $"<style>{BaseCss}{cfg.CustomCss.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase)}</style><div class=\"shop\">{nav}{body}</div>";
        var meta = noIndex ? new[] { new KeyValuePair<string, string>("robots", "noindex") } : null;
        var result = PluginResult.Page(html, title, description, meta);
        return status == 200 ? result : result.WithStatus(status);
    }

    public static string Select(string name, string current, IEnumerable<(string Value, string Label)> options, string? cls = null)
    {
        var sb = new StringBuilder($"<select name=\"{E(name)}\"{(cls is null ? "" : " class=\"" + cls + "\"")}>");
        foreach (var (v, l) in options) sb.Append($"<option value=\"{E(v)}\"{(v == current ? " selected" : "")}>{E(l)}</option>");
        return sb.Append("</select>").ToString();
    }

    public static IEnumerable<(string, string)> CountryOptions(string locale) => Countries.All.Select(c => (c.Code, locale == "de" ? c.De : c.En));

    public static string StatusBadge(IPluginContext ctx, string status)
    {
        var cls = status switch { OrderStatus.Paid or OrderStatus.Shipped or OrderStatus.Completed => "ok", OrderStatus.AwaitingPayment => "warn", _ => "bad" };
        return $"<span class=\"shop-badge {cls}\">{E(ctx.T("shop.status." + status))}</span>";
    }
}
