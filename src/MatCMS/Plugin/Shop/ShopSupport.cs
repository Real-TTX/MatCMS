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
        .shop h1,.shop h2,.shop h3{font-family:var(--shop-font-head,var(--font-head,inherit))}.shop h1{margin:0 0 16px}.shop h2{margin:28px 0 12px}.shop.shop-block h2{margin-top:0}.shop a{color:var(--shop-link,inherit)}
        .shop-muted{opacity:.7}.shop-small{font-size:.85em}
        .shop-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:20px}
        .shop-grid.cols-2{grid-template-columns:repeat(2,minmax(0,1fr))}.shop-grid.cols-3{grid-template-columns:repeat(3,minmax(0,1fr))}.shop-grid.cols-4{grid-template-columns:repeat(4,minmax(0,1fr))}
        @media(max-width:900px){.shop-grid.cols-3,.shop-grid.cols-4{grid-template-columns:repeat(2,minmax(0,1fr))}}
        @media(max-width:520px){.shop-grid,.shop-grid.cols-2,.shop-grid.cols-3,.shop-grid.cols-4{grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.shop-card .body{padding:9px 10px;gap:3px}.shop-card .title{font-size:.94em;line-height:1.3}.shop-card .shop-tags{display:none}.shop-card .shop-btn{padding:8px 10px}}
        @media(max-width:340px){.shop-grid,.shop-grid.cols-2,.shop-grid.cols-3,.shop-grid.cols-4{grid-template-columns:minmax(0,1fr)}}
        .shop-card{display:flex;flex-direction:column;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.12)));border-radius:10px;overflow:hidden;background:var(--shop-card,transparent)}
        .shop-card .img{aspect-ratio:4/3;background:rgba(0,0,0,.06) center/cover no-repeat}.shop-card .body{padding:12px 14px;display:flex;flex-direction:column;gap:6px;flex:1}
        .shop-card .title{font-weight:600;text-decoration:none}.shop-price{font-weight:700}
        .shop-list{display:grid;gap:14px}.shop-list .shop-card{flex-direction:row}.shop-list .shop-card .img{aspect-ratio:auto;width:170px;min-height:130px;flex:none}
        @media(max-width:520px){.shop-list .shop-card .img{width:100px;min-height:100px}}
        .shop-feature{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:28px;align-items:center}@media(max-width:700px){.shop-feature{grid-template-columns:minmax(0,1fr)}}
        .shop-feature .img{display:block;aspect-ratio:4/3;border-radius:10px;background:rgba(0,0,0,.06) center/cover no-repeat}.shop-feature h3{margin:0 0 8px;font-size:1.5em}
        .shop-express{display:flex;flex-wrap:wrap;gap:8px;align-items:center;margin-top:10px}.shop-express .shop-btn{flex:1 1 auto;text-align:center}
        .shop-remind{margin-top:14px;padding-top:12px;border-top:1px solid var(--shop-line,var(--line,rgba(0,0,0,.15)))}.shop-remind-row{display:flex;gap:8px;margin:6px 0}.shop-remind-row input{flex:1;min-width:0}
        .shop-est{margin:14px 0;padding:12px 14px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.15)));border-radius:10px}.shop-est-form{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin:0 0 8px}.shop-est-form label{font-weight:600}.shop-est select{padding:7px 30px 7px 10px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.2)));border-radius:8px;font:inherit;background-color:transparent;color:inherit;cursor:pointer;max-width:100%}
        .shop-est-list{list-style:none;margin:0 0 8px;padding:0}.shop-est-list li,.shop-est-total{display:flex;justify-content:space-between;gap:12px;padding:3px 0}.shop-est-total{border-top:1px solid var(--shop-line,var(--line,rgba(0,0,0,.15)));margin:6px 0}
        .shop-opts{display:grid;gap:14px;margin-bottom:14px}.shop-optlabel{font-size:.85em;opacity:.7;margin-bottom:6px}.shop-chips{display:flex;gap:8px;flex-wrap:wrap}
        .shop-chip{padding:7px 14px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.25)));border-radius:8px;background:transparent;color:inherit;font:inherit;cursor:pointer}
        .shop-chip[aria-checked=true]{border-color:var(--shop-accent,var(--accent,#1a1a1a));box-shadow:0 0 0 1px var(--shop-accent,var(--accent,#1a1a1a));font-weight:600}
        .shop-chip.na{opacity:.45;text-decoration:line-through}.shop-chip:focus-visible{outline:2px solid var(--shop-accent,var(--accent,#1a1a1a));outline-offset:2px}.shop-form button[disabled]{opacity:.5;cursor:not-allowed}
        .shop-tags{display:flex;gap:6px;flex-wrap:wrap}.shop-tags a{padding:2px 10px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.2)));border-radius:99px;font-size:.8em;text-decoration:none}
        .shop-tags a.on{background:var(--shop-accent,var(--accent,#1a1a1a));color:var(--shop-on-accent,#fff)}
        .shop-btn,.shop a.shop-btn{display:inline-block;padding:10px 18px;border-radius:8px;border:1px solid var(--shop-accent,var(--accent,#1a1a1a));background:var(--shop-accent,var(--accent,#1a1a1a));color:var(--shop-on-accent,#fff);font:inherit;cursor:pointer;text-decoration:none}
        .shop-btn.ghost,.shop a.shop-btn.ghost{background:transparent;color:inherit}.shop-btn[disabled]{opacity:.5;cursor:not-allowed}
        .shop-badge{display:inline-block;padding:2px 10px;border-radius:99px;font-size:.8em;background:rgba(0,0,0,.08)}.shop-badge.ok{background:#d9f0dc;color:#1d5b28}.shop-badge.warn{background:#fdf0cf;color:#6b4e00}.shop-badge.bad{background:#f8d9d9;color:#8a1f1f}
        .shop-carousel{position:relative}.shop-carousel .track{display:flex;gap:20px;overflow-x:auto;scroll-snap-type:x mandatory;scroll-behavior:smooth;padding:2px 2px 12px;scrollbar-width:thin}
        .shop-carousel .track .shop-card{flex:0 0 min(260px,78%);scroll-snap-align:start}
        .shop-carousel button{position:absolute;top:38%;z-index:2;width:42px;height:42px;border-radius:50%;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.2)));background:var(--shop-bg,var(--bg,#fff));color:inherit;font-size:26px;line-height:1;cursor:pointer;box-shadow:0 2px 8px rgba(0,0,0,.15)}
        .shop-carousel button[hidden]{display:none}.shop-carousel .prev{left:-10px}.shop-carousel .next{right:-10px}
        .shop-more{margin-top:16px}.shop-was{font-weight:400;opacity:.6;margin-left:4px}.shop-coupon{margin-top:18px;display:flex;gap:8px;align-items:center;flex-wrap:wrap}
        .shop-coupon input[type=text]{padding:9px 12px;border:1px solid rgba(0,0,0,.25);border-radius:6px;font:inherit;text-transform:uppercase}
        """;

    private const string BaseCss = CardCss + """
        .shop-nav{display:flex;gap:18px;flex-wrap:wrap;align-items:center;margin-bottom:24px;padding-bottom:12px;border-bottom:1px solid var(--shop-line,var(--line,rgba(0,0,0,.12)))}
        .shop-nav .spacer{flex:1}body[data-els~="shop.cart"] .shop-nav{display:none}
        .shop-filter{display:flex;gap:10px;flex-wrap:wrap;align-items:center;margin-bottom:16px}.shop-filter input,.shop-filter select{padding:10px 12px;border:1px solid rgba(0,0,0,.25);border-radius:8px;font:inherit;background:transparent;color:inherit}
        .shop-crumbs{font-size:.9em;margin:0 0 10px;opacity:.8}
        .shop-product{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:32px}@media(max-width:760px){.shop-product{grid-template-columns:1fr}}
        .shop-product.single{grid-template-columns:1fr;max-width:720px}.shop-product .main-img{width:100%;border-radius:10px;background:rgba(0,0,0,.06)}.shop-gallery{display:flex;gap:8px;flex-wrap:wrap;margin-top:8px}.shop-gallery img{width:72px;height:72px;object-fit:cover;border-radius:6px}
        .shop table{width:100%;border-collapse:collapse}.shop th,.shop td{padding:10px 8px;border-bottom:1px solid var(--shop-line,var(--line,rgba(0,0,0,.12)));text-align:left;vertical-align:top}
        .shop td.r,.shop th.r{text-align:right;white-space:nowrap}.shop-sum{max-width:380px;margin-left:auto;margin-top:16px}.shop-sum div{display:flex;justify-content:space-between;gap:14px;padding:4px 0}.shop-sum .total{font-weight:700;border-top:2px solid currentColor;margin-top:6px;padding-top:8px}
        .shop-form{display:grid;gap:14px;max-width:640px}.shop-form label{display:block;font-size:.9em;margin-bottom:4px}
        .shop-form input[type=text],.shop-form input[type=email],.shop-form input[type=password],.shop-form input[type=tel],.shop-form input[type=number],.shop-form select,.shop-form textarea{width:100%;padding:10px 12px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.25)));border-radius:8px;font:inherit;box-sizing:border-box;background:transparent;color:inherit}
        .shop-row{display:grid;grid-template-columns:1fr 1fr;gap:12px}@media(max-width:560px){.shop-row{grid-template-columns:1fr}}.shop-row.shop-zipcity{grid-template-columns:minmax(84px,128px) minmax(0,1fr)}
        .shop-check{display:flex;gap:8px;align-items:flex-start}.shop-check input{margin-top:4px}
        .shop-alert{padding:12px 14px;border-radius:8px;margin:0 0 16px;border:1px solid}.shop-alert.error{background:#fdecec;color:#8a1f1f;border-color:#f3c0c0}.shop-alert.ok{background:#eef8ee;color:#1d5b28;border-color:#bfe3c4}.shop-alert.info{background:#eef3fb;color:#1f3f6e;border-color:#c5d6ef}
        .shop-box{border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.12)));border-radius:10px;padding:16px;margin:0 0 16px}.shop-pre{white-space:pre-line}
        .shop-pager{display:flex;gap:6px;margin-top:24px;flex-wrap:wrap}.shop-pager a,.shop-pager span{padding:6px 12px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.2)));border-radius:6px;text-decoration:none}.shop-pager .on{background:var(--shop-accent,var(--accent,#1a1a1a));color:var(--shop-on-accent,#fff)}
        .shop-cats{display:flex;gap:8px;flex-wrap:wrap;margin-bottom:20px}.shop-cats a{padding:6px 14px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.2)));border-radius:99px;text-decoration:none}.shop-cats a.on{background:var(--shop-accent,var(--accent,#1a1a1a));color:var(--shop-on-accent,#fff)}
        .shop-cathero{height:200px;background:#eee center/cover no-repeat;border-radius:10px;margin:0 0 18px}.shop-catdesc{max-width:720px;margin:-4px 0 18px}
        .shop-subcats{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:14px;margin:0 0 24px}.shop-subcat{display:flex;flex-direction:column;gap:2px;text-decoration:none;color:inherit}
        .shop-subcat .img{display:block;aspect-ratio:4/3;background:var(--shop-line,var(--line,rgba(0,0,0,.08))) center/cover no-repeat;border-radius:8px;margin-bottom:6px}.shop-subcat .name{font-weight:600}.shop-subcat:hover .name{text-decoration:underline}
        .shop-qty{width:70px!important}.shop-actions{display:flex;gap:10px;flex-wrap:wrap;align-items:center}
        .shop-cartcount{font-size:.55em;font-weight:400;margin-left:6px}
        .shop-steps-mini{max-width:520px;margin:12px 0 28px}.shop-steps-mini li a{color:inherit;text-decoration:none}
        .shop-checkout{display:grid;grid-template-columns:minmax(0,1fr) 390px;gap:30px;align-items:start}@media(max-width:900px){.shop-checkout{grid-template-columns:1fr}}
        .shop-checkout-main,.shop-checkout-side{display:flex;flex-direction:column;gap:18px;min-width:0}.shop-checkout-side{position:sticky;top:20px}@media(max-width:900px){.shop-checkout-side{position:static}}
        .shop-sec h2{display:flex;align-items:center;gap:10px}.shop-sec h2 .n{display:inline-flex;align-items:center;justify-content:center;width:26px;height:26px;border-radius:50%;background:var(--shop-accent,var(--accent,#1a1a1a));color:var(--shop-on-accent,#fff);font-size:.8em}
        .shop-sec .shop-form{max-width:none}
        .shop-form .shop-opt,.shop-opt{display:flex;align-items:center;gap:12px;font-size:1em;padding:12px 14px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.2)));border-radius:10px;cursor:pointer;margin:0!important}
        .shop-opt .t{display:flex;flex-direction:column;flex:1;gap:2px}.shop-opt .p{font-weight:600;white-space:nowrap}.shop-opt input{margin:0;width:auto!important}
        .shop-opt:hover{border-color:var(--shop-accent,var(--accent,#1a1a1a))}.shop-opt:has(input:checked){border-color:var(--shop-accent,var(--accent,#1a1a1a));box-shadow:0 0 0 1px var(--shop-accent,var(--accent,#1a1a1a));background:color-mix(in srgb,var(--shop-accent,var(--accent,#1a1a1a)) 5%,transparent)}
        .shop-summary h2{margin:0 0 12px}.shop-sline{display:grid;grid-template-columns:52px minmax(0,1fr) auto;gap:12px;align-items:center;padding:8px 0}
        .shop-sline .img{position:relative;display:block;width:52px;height:52px;border-radius:8px;background:var(--shop-line,var(--line,rgba(0,0,0,.08))) center/cover no-repeat}
        .shop-sline .img b{position:absolute;top:-6px;right:-6px;min-width:20px;height:20px;padding:0 5px;box-sizing:border-box;border-radius:99px;background:var(--shop-accent,var(--accent,#1a1a1a));color:var(--shop-on-accent,#fff);font-size:.72em;line-height:20px;text-align:center}
        .shop-sline .t{font-size:.95em}.shop-sline .p{font-weight:600;white-space:nowrap}.shop-summary .shop-sum{margin-top:10px;padding-top:10px;border-top:1px solid var(--shop-line,var(--line,rgba(0,0,0,.12)))}
        .shop-summary .shop-confirm{margin-top:14px;font-size:.9em}.shop-summary .shop-btn.big{margin-top:18px}
        .shop-gal .main-img{display:block}.shop-thumbs{display:flex;gap:8px;flex-wrap:wrap;margin-top:10px}.shop-thumbs .th{display:block;border:2px solid transparent;border-radius:9px;opacity:.7;line-height:0;transition:opacity .15s}
        .shop-thumbs .th:hover{opacity:1}.shop-thumbs .th.on{border-color:var(--shop-accent,var(--accent,#1a1a1a));opacity:1}.shop-thumbs .th img{width:72px;height:72px;object-fit:cover;border-radius:6px;display:block}
        .shop-lightbox{position:fixed;inset:0;z-index:2000;background:rgba(0,0,0,.86);display:flex;align-items:center;justify-content:center;padding:24px}.shop-lightbox img{max-width:100%;max-height:100%;border-radius:8px;box-shadow:0 10px 40px rgba(0,0,0,.5)}
        .shop-lightbox button{position:absolute;border:0;background:rgba(255,255,255,.14);color:#fff;font-size:34px;line-height:1;width:48px;height:48px;border-radius:50%;cursor:pointer}.shop-lightbox button:hover{background:rgba(255,255,255,.28)}
        .shop-lightbox .x{top:18px;right:18px;font-size:30px}.shop-lightbox .p{left:18px;top:50%;margin-top:-24px}.shop-lightbox .n{right:18px;top:50%;margin-top:-24px}
        .shop-find{display:flex;align-items:center;max-width:720px;margin:0 0 26px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:99px;background:var(--shop-bg,var(--bg,#fff));padding:4px 4px 4px 18px;transition:box-shadow .15s,border-color .15s}
        .shop-find:focus-within{border-color:var(--shop-accent,var(--accent,#1a1a1a));box-shadow:0 0 0 3px color-mix(in srgb,var(--shop-accent,var(--accent,#1a1a1a)) 18%,transparent)}
        .shop-find .ico{flex:none;width:20px;height:20px;opacity:.5;margin-right:10px}.shop-find input[type=search]{flex:1;min-width:0;border:0;outline:0;background:transparent;font:inherit;color:inherit;padding:10px 4px}
        .shop-find .shop-btn{border-radius:99px;padding:10px 24px}
        .shop-layout{display:grid;grid-template-columns:268px minmax(0,1fr);gap:36px;align-items:start}.shop-layout.no-side{grid-template-columns:minmax(0,1fr)}
        .shop-ftoggle{position:absolute;opacity:0;pointer-events:none;width:0;height:0}.shop-ftbtn{display:none}
        .shop-side{position:sticky;top:16px;display:flex;flex-direction:column;min-width:0}.shop-side:empty{display:none}
        .shop-side h3{margin:0 0 10px;font-size:1em;font-weight:600}
        .shop-catnav,.shop-fgroup{padding:16px 0;border-bottom:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)))}.shop-catnav{padding-top:0}.shop-side>:last-child,.shop-filters>.shop-fgroup:last-of-type{border-bottom:0}
        .shop-catnav ul{list-style:none;margin:0;padding:0}.shop-catnav li a{display:flex;justify-content:space-between;gap:10px;padding:5px 0;text-decoration:none;color:inherit}
        .shop-catnav li a:hover,.shop-catnav li a[aria-current]{color:var(--shop-accent,var(--accent,#1a1a1a))}.shop-catnav li a.on{font-weight:600}.shop-catnav li a span{opacity:.5;font-size:.85em;font-weight:400}
        .shop-catnav ul ul{margin:2px 0 6px 3px;padding-left:12px;border-left:2px solid var(--shop-line,var(--line,rgba(0,0,0,.14)))}.shop-catnav ul ul a{font-size:.95em}
        .shop-fgroup>summary{display:flex;justify-content:space-between;align-items:center;gap:10px;cursor:pointer;list-style:none;font-weight:600}.shop-fgroup>summary::-webkit-details-marker{display:none}
        .shop-fgroup>summary::after{content:"";flex:none;width:7px;height:7px;border-right:2px solid currentColor;border-bottom:2px solid currentColor;transform:rotate(45deg);opacity:.55;transition:transform .15s}.shop-fgroup[open]>summary::after{transform:rotate(-135deg)}
        .shop-fgroup .opts{display:flex;flex-direction:column;gap:1px;margin-top:10px}.shop-fgroup .opts a{display:flex;align-items:center;gap:10px;padding:5px 0;text-decoration:none;color:inherit;font-size:.95em}
        .shop-fgroup .opts a span{margin-left:auto;opacity:.5;font-size:.85em}
        .shop-fgroup .box{flex:none;position:relative;width:17px;height:17px;border:1.5px solid color-mix(in srgb,currentColor 40%,transparent);border-radius:4px;background:var(--shop-bg,var(--bg,#fff));box-sizing:border-box}
        .shop-fgroup .opts a:hover .box{border-color:var(--shop-accent,var(--accent,#1a1a1a))}.shop-fgroup .opts a.on .box{background:var(--shop-accent,var(--accent,#1a1a1a));border-color:var(--shop-accent,var(--accent,#1a1a1a))}
        .shop-fgroup .opts a.on .box::after{content:"";position:absolute;left:4.5px;top:1px;width:4px;height:8px;border:solid var(--shop-on-accent,#fff);border-width:0 2px 2px 0;transform:rotate(45deg)}
        .shop-frange{display:flex;gap:6px;align-items:center;margin-top:10px}.shop-frange input[type=number]{flex:1 1 0;min-width:0;width:auto;padding:7px 9px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:8px;font:inherit;background:transparent;color:inherit;box-sizing:border-box}
        .shop-frange .cur{opacity:.6;font-size:.85em}.shop-frange .shop-btn{flex:none;padding:7px 12px}
        .shop-filters .clear{display:inline-block;margin-top:12px;font-size:.88em}.shop-tagsgroup .shop-tags{margin:0}
        .shop-toolbar{display:flex;align-items:center;gap:14px;margin:0 0 14px;padding-bottom:12px;border-bottom:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)))}.shop-count{margin:0;font-weight:600}
        .shop-sort{margin-left:auto}.shop-sort label{display:flex;align-items:center;gap:8px}.shop-sort label span{opacity:.65;font-size:.9em;white-space:nowrap}
        .shop-sort select{padding:8px 32px 8px 12px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:8px;font:inherit;background-color:transparent;color:inherit;cursor:pointer}
        .shop-active{display:flex;flex-wrap:wrap;gap:8px;align-items:center;margin:0 0 18px}
        .shop-active .chip{display:inline-flex;align-items:center;gap:8px;padding:4px 5px 4px 12px;border-radius:99px;background:color-mix(in srgb,var(--shop-accent,var(--accent,#1a1a1a)) 9%,transparent);border:1px solid color-mix(in srgb,var(--shop-accent,var(--accent,#1a1a1a)) 28%,transparent);text-decoration:none;color:inherit;font-size:.88em}
        .shop-active .chip span{display:inline-flex;align-items:center;justify-content:center;width:18px;height:18px;border-radius:50%;background:color-mix(in srgb,var(--shop-accent,var(--accent,#1a1a1a)) 22%,transparent);line-height:1}.shop-active .chip:hover span{background:var(--shop-accent,var(--accent,#1a1a1a));color:var(--shop-on-accent,#fff)}
        .shop-active .clear{font-size:.88em;margin-left:4px}
        .shop-acc{display:grid;grid-template-columns:262px minmax(0,1fr);gap:36px;align-items:start}
        .shop-accside{position:sticky;top:16px;display:flex;flex-direction:column;gap:14px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:16px;padding:18px;background:var(--shop-bg,var(--bg,#fff))}
        .shop-accwho{display:flex;align-items:center;gap:12px;padding-bottom:16px;border-bottom:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));min-width:0}.shop-accwho>div{min-width:0}
        .shop-accwho .avatar{flex:none;display:flex;align-items:center;justify-content:center;width:46px;height:46px;border-radius:50%;background:var(--shop-accent,var(--accent,#1a1a1a));color:var(--shop-on-accent,#fff);font-weight:700;letter-spacing:.02em}
        .shop-accwho strong{display:block;overflow:hidden;text-overflow:ellipsis}.shop-accwho .mail{display:block;font-size:.85em;opacity:.65;overflow:hidden;text-overflow:ellipsis}
        .shop-accnav{display:flex;flex-direction:column;gap:2px}
        .shop-accnav a,.shop-accout button{display:flex;align-items:center;gap:12px;box-sizing:border-box;width:100%;padding:10px 12px;border:0;border-radius:10px;background:none;color:inherit;font:inherit;text-align:left;text-decoration:none;cursor:pointer}
        .shop-accnav a:hover,.shop-accout button:hover{background:color-mix(in srgb,var(--shop-accent,var(--accent,#1a1a1a)) 8%,transparent)}.shop-accnav a.on{background:color-mix(in srgb,var(--shop-accent,var(--accent,#1a1a1a)) 12%,transparent);color:var(--shop-accent,var(--accent,#1a1a1a));font-weight:600}
        .shop-accnav .ico,.shop-accout .ico{flex:none;width:20px;height:20px}.shop-accout{margin:0;padding-top:12px;border-top:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)))}
        .shop-accmain{min-width:0}.shop-accmain h1{margin:0 0 20px}
        .shop-stats{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:14px;margin:0 0 28px}
        .shop-stats>div{border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:14px;padding:16px 18px;background:linear-gradient(135deg,color-mix(in srgb,var(--shop-accent,var(--accent,#1a1a1a)) 8%,transparent),transparent)}
        .shop-stats .n{display:block;font-size:1.55em;font-weight:700;line-height:1.25}.shop-stats .l{font-size:.85em;opacity:.65}
        .shop-sechead{display:flex;flex-wrap:wrap;justify-content:space-between;align-items:baseline;gap:12px;margin:0 0 12px}.shop-sechead h2{margin:0}
        .shop-ocards{display:flex;flex-direction:column;gap:16px}
        .shop-ocard{border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:16px;padding:18px 20px;background:var(--shop-bg,var(--bg,#fff));transition:box-shadow .15s}.shop-ocard:hover{box-shadow:0 6px 22px rgba(0,0,0,.07)}
        .shop-ocard>header{display:flex;justify-content:space-between;align-items:flex-start;gap:12px;margin-bottom:12px}.shop-ocard>header>div{display:flex;flex-direction:column}.shop-ocard .num{font-weight:700;font-size:1.05em;text-decoration:none}
        .shop-prog{display:flex;gap:6px;margin:0 0 16px;padding:0;list-style:none}.shop-prog li{flex:1;display:flex;flex-direction:column;gap:5px;font-size:.78em;opacity:.55}
        .shop-prog li i{display:block;height:5px;border-radius:99px;background:var(--shop-line,var(--line,rgba(0,0,0,.14)))}.shop-prog li.on{opacity:1}.shop-prog li.on i{background:var(--shop-accent,var(--accent,#1a1a1a))}
        .shop-ocard .body{display:grid;grid-template-columns:auto minmax(0,1fr) auto;gap:16px;align-items:center}.shop-ocard .thumbs{display:flex;gap:6px}
        .shop-ocard .th{position:relative;display:flex;align-items:center;justify-content:center;width:56px;height:56px;border-radius:10px;background:var(--shop-line,var(--line,rgba(0,0,0,.14))) center/cover no-repeat;color:inherit;font-weight:600;text-decoration:none}
        .shop-ocard .th b{position:absolute;top:-6px;right:-6px;min-width:19px;height:19px;padding:0 5px;box-sizing:border-box;border-radius:99px;background:var(--shop-accent,var(--accent,#1a1a1a));color:var(--shop-on-accent,#fff);font-size:.7em;line-height:19px;text-align:center}
        .shop-ocard .th.more{font-size:.9em;background:color-mix(in srgb,var(--shop-accent,var(--accent,#1a1a1a)) 10%,transparent)}.shop-ocard .what .t{font-weight:600}.shop-ocard .sum{font-weight:700;font-size:1.1em;white-space:nowrap}
        .shop-ocard footer{display:flex;flex-wrap:wrap;gap:8px;margin-top:16px;padding-top:14px;border-top:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)))}
        .shop-ocard footer .shop-btn{display:inline-flex;align-items:center;gap:8px;padding:8px 16px;font-size:.92em}.shop-ocard footer .ico{width:18px;height:18px}
        .shop-accgrid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:16px;margin:28px 0 0}.shop-accgrid .shop-card-box{margin:0}.shop-accgrid .shop-card-box>a{display:inline-block;margin-top:12px;font-size:.92em}
        .shop-card-box h2 .ico{width:20px;height:20px;vertical-align:-4px;margin-right:8px;color:var(--shop-accent,var(--accent,#1a1a1a))}.shop-track .num{margin:2px 0 14px;font-size:1.1em;word-break:break-all}
        .shop-accempty{text-align:center;padding:44px 16px;border:1px dashed var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:16px;margin:0 0 20px}.shop-accempty .ico{width:64px;height:64px;opacity:.35}.shop-accempty h2{margin:10px 0 6px}
        .shop-addrs{display:grid;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));gap:16px;margin:0 0 8px}
        .shop-addr{display:flex;flex-direction:column;gap:8px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:14px;padding:16px;background:var(--shop-bg,var(--bg,#fff))}.shop-addr.default{border-color:var(--shop-accent,var(--accent,#1a1a1a));box-shadow:0 0 0 1px var(--shop-accent,var(--accent,#1a1a1a))}
        .shop-addr .head{display:flex;flex-wrap:wrap;align-items:center;gap:8px}.shop-addr .shop-actions{margin-top:auto;padding-top:6px}.shop-addr .shop-actions .shop-btn{padding:6px 12px;font-size:.88em}
        .shop-accform{margin-top:20px}.shop-accform .shop-form{max-width:560px}.shop-danger{border-color:color-mix(in srgb,#c0392b 40%,transparent)}.shop-danger h2{color:#b3261e}
        @media(max-width:860px){.shop-acc{grid-template-columns:minmax(0,1fr);gap:18px}.shop-accside{position:static;padding:12px 14px;display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:center;gap:8px 10px}.shop-accwho{grid-area:1/1;padding-bottom:0;min-width:0}.shop-accout{grid-area:1/2;padding-top:0;border-top:0;display:flex;justify-content:flex-end;margin:-4px 0 0}.shop-accout button{width:auto;padding:6px 4px;font-size:.92em;opacity:.8}.shop-accnav{grid-area:2/1/3/3;display:flex;flex-direction:row;gap:6px;overflow-x:auto;scrollbar-width:none;margin:0 -2px;padding:2px}.shop-accnav::-webkit-scrollbar{display:none}.shop-accnav a{flex:none;width:auto;white-space:nowrap;padding:8px 12px;font-size:.95em;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:99px}.shop-accnav a.on{border-color:transparent}}
        @media(max-width:600px){.shop-accgrid{grid-template-columns:minmax(0,1fr)}.shop-stats{gap:8px}.shop-stats>div{padding:12px;min-width:0}.shop-stats .n{font-size:1.1em}.shop-stats .l{font-size:.78em}.shop-ocard .body{grid-template-columns:minmax(0,1fr) auto}.shop-ocard .thumbs{grid-column:1/-1}.shop-ocard{padding:14px}.shop-ocard>header{flex-wrap:wrap}.shop-ocard .num{white-space:nowrap}.shop-ocard footer .shop-btn{flex:1 1 calc(50% - 8px);justify-content:center;text-align:center}}
        .shop-fpanel{position:sticky;top:16px;min-width:0}.shop-fpanel>summary{display:none;list-style:none;cursor:pointer}.shop-fpanel>summary::-webkit-details-marker{display:none}
        .shop-fpanel>:last-child{border-bottom:0}.shop-fpanel h3{margin:0 0 10px;font-size:1em;font-weight:600}
        @media(max-width:900px){.shop-fpanel{position:static;margin-bottom:14px}.shop-fpanel>summary{display:inline-flex;align-items:center;padding:8px 16px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:99px;font-weight:600}
        .shop-fpanel[open]>summary{background:var(--shop-accent,var(--accent,#1a1a1a));color:var(--shop-on-accent,#fff);border-color:var(--shop-accent,var(--accent,#1a1a1a));margin-bottom:12px}}
        .shop-slot{margin:0 0 16px}.shop-slot:last-child{margin-bottom:0}
        .shop-nomatch{text-align:center;padding:52px 16px;border:1px dashed var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:14px}.shop-nomatch p{margin:0 0 16px;font-size:1.1em}
        @media(max-width:900px){.shop-layout{display:flex;flex-direction:column;align-items:stretch;gap:14px}.shop-main{display:contents}.shop-main>*{order:3}.shop-main>.shop-toolbar{order:1;margin:0}.shop-side{order:2;display:none;position:static;padding:6px 18px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:12px}.shop-ftoggle:checked ~ .shop-side{display:flex}
        .shop-ftbtn{display:inline-flex;align-items:center;gap:8px;padding:8px 16px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:99px;cursor:pointer;font-weight:600}.shop-ftoggle:checked ~ .shop-main .shop-ftbtn{background:var(--shop-accent,var(--accent,#1a1a1a));color:var(--shop-on-accent,#fff);border-color:var(--shop-accent,var(--accent,#1a1a1a))}
        .shop-layout.no-side .shop-ftbtn{display:none}.shop-toolbar{flex-wrap:wrap}.shop-count{order:1;margin:0}.shop-sort{order:2;margin-left:auto}}
        @media(max-width:520px){.shop-sort label span{display:none}.shop-grid .shop-card .body>p.shop-muted{display:none}}
        .shop-attrh{font-size:1.05em;margin:18px 0 6px}.shop-attrs{width:100%;border-collapse:collapse}.shop-attrs th{width:40%;font-weight:500;opacity:.7;padding:7px 8px 7px 0}.shop-attrs td{padding:7px 0}
        .shop-ordhead{display:flex;align-items:center;gap:14px;flex-wrap:wrap;margin-bottom:6px}.shop-ordhead h1{margin:0}
        .shop-sum>div>span:last-child{white-space:nowrap}@media(max-width:480px){.shop-steps li{font-size:.78em;overflow-wrap:anywhere}.shop-steps li small{font-size:.8em}}
        .shop-steps{display:flex;margin:26px 0 30px;padding:0;list-style:none}.shop-steps li{flex:1;text-align:center;position:relative;font-size:.92em;opacity:.55}
        .shop-steps li small{display:block;font-size:.82em;opacity:.8;margin-top:2px}
        .shop-steps li::before{content:"";display:block;width:26px;height:26px;border-radius:50%;border:2px solid var(--shop-line,var(--line,rgba(0,0,0,.25)));margin:0 auto 8px;background:var(--shop-bg,var(--bg,#fff));position:relative;z-index:1;box-sizing:border-box;line-height:22px;font-size:14px;color:var(--shop-on-accent,#fff)}
        .shop-steps li::after{content:"";position:absolute;top:12px;left:calc(50% + 15px);right:calc(-50% + 15px);height:2px;background:var(--shop-line,var(--line,rgba(0,0,0,.2)))}.shop-steps li:last-child::after{display:none}
        .shop-steps li.done{opacity:1}.shop-steps li.done::before{content:"\2713";background:var(--shop-accent,var(--accent,#1a1a1a));border-color:var(--shop-accent,var(--accent,#1a1a1a))}.shop-steps li.done::after{background:var(--shop-accent,var(--accent,#1a1a1a))}
        .shop-steps li.now{font-weight:600}.shop-steps li.now::before{box-shadow:0 0 0 5px color-mix(in srgb,var(--shop-accent,var(--accent,#1a1a1a)) 18%,transparent)}.shop-steps li.now::after{background:var(--shop-line,var(--line,rgba(0,0,0,.2)))}
        .shop-paybox{margin-bottom:24px}
        .shop-orderpage{display:grid;grid-template-columns:minmax(0,1fr) 340px;gap:28px;align-items:start}@media(max-width:860px){.shop-orderpage{grid-template-columns:1fr}}
        .shop-order-main,.shop-order-side{display:flex;flex-direction:column;gap:20px;min-width:0}
        .shop-card-box{border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:12px;padding:18px 20px}.shop-card-box h2{margin:0 0 12px;font-size:1.05em}.shop-card-box .shop-sum{max-width:none;margin:0}
        .shop-oline{display:grid;grid-template-columns:64px minmax(0,1fr) auto;gap:6px 16px;align-items:center;padding:12px 0;border-bottom:1px solid var(--shop-line,var(--line,rgba(0,0,0,.1)))}.shop-oline:last-child{border-bottom:0;padding-bottom:0}
        .shop-oline .img{display:block;width:64px;height:64px;border-radius:8px;background:var(--shop-line,var(--line,rgba(0,0,0,.08))) center/cover no-repeat}.shop-oline .t{font-weight:600}.shop-oline .p{font-weight:700;white-space:nowrap}
        .shop-catintro{margin:-6px 0 18px}.shop-catgrid{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:24px;margin:0 0 36px}
        .shop-catcard{display:flex;flex-direction:column;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:14px;overflow:hidden;background:var(--shop-bg,var(--bg,#fff));transition:box-shadow .18s,transform .18s}
        .shop-catcard:hover{box-shadow:0 12px 30px rgba(0,0,0,.13);transform:translateY(-2px)}
        .shop-catcard .img{display:block;aspect-ratio:16/10;background:var(--shop-line,var(--line,rgba(0,0,0,.08))) center/cover no-repeat;text-decoration:none}
        .shop-catcard .img.empty{display:flex;align-items:center;justify-content:center;background:linear-gradient(135deg,color-mix(in srgb,var(--shop-accent,var(--accent,#1a1a1a)) 16%,transparent),color-mix(in srgb,var(--shop-accent,var(--accent,#1a1a1a)) 4%,transparent));color:var(--shop-accent,var(--accent,#1a1a1a))}
        .shop-catcard .img.empty span{font-size:3.4em;font-weight:700;opacity:.45}
        .shop-catcard .body{padding:16px 18px 20px;display:flex;flex-direction:column;gap:6px;flex:1}.shop-catcard h3{margin:0;font-size:1.2em}.shop-catcard h3 a{color:inherit;text-decoration:none}.shop-catcard h3 a:hover{text-decoration:underline}
        .shop-catcard .count{font-size:.85em;opacity:.65}.shop-catcard .desc{margin:2px 0 0;font-size:.92em;opacity:.8;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden}
        .shop-catcard .subs{display:flex;flex-wrap:wrap;gap:6px;margin-top:auto;padding-top:10px}.shop-catcard .subs a{padding:3px 11px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.2)));border-radius:99px;font-size:.82em;text-decoration:none;color:inherit}
        .shop-catcard .subs a:hover{background:var(--shop-accent,var(--accent,#1a1a1a));color:var(--shop-on-accent,#fff);border-color:transparent}
        .shop-qty{padding:8px 10px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.25)));border-radius:6px;font:inherit;background:transparent;color:inherit}
        .shop-tagcloud a{font-size:1em;padding:6px 16px}
        .shop-cartpage{display:grid;grid-template-columns:minmax(0,1fr) 340px;gap:36px;align-items:start}@media(max-width:860px){.shop-cartpage{grid-template-columns:1fr;gap:24px}}
        .shop-cartlines{border-top:1px solid var(--shop-line,var(--line,rgba(0,0,0,.12)))}
        .shop-line{display:grid;grid-template-columns:96px minmax(0,1fr) auto;gap:6px 18px;padding:18px 0;border-bottom:1px solid var(--shop-line,var(--line,rgba(0,0,0,.12)))}
        .shop-line .img{display:block;width:96px;height:96px;border-radius:8px;background:var(--shop-line,var(--line,rgba(0,0,0,.08))) center/cover no-repeat}
        .shop-line .title{font-weight:600;text-decoration:none;color:inherit}.shop-line .title:hover{text-decoration:underline}.shop-line .lineprice{font-weight:700;white-space:nowrap;text-align:right}
        .shop-line .controls{display:flex;gap:18px;align-items:center;flex-wrap:wrap;margin-top:10px}.shop-qtylabel{display:inline-flex;gap:8px;align-items:center}
        .shop-linkbtn{background:none;border:0;padding:0;font:inherit;font-size:.9em;color:inherit;text-decoration:underline;opacity:.75;cursor:pointer}.shop-linkbtn:hover{opacity:1}@media(pointer:coarse){.shop-linkbtn{padding:10px 6px}}
        .shop-cartupdate{display:flex;gap:16px;align-items:center;flex-wrap:wrap;margin:18px 0 0}
        .shop-cartsum{border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:12px;padding:20px;position:sticky;top:20px}@media(max-width:860px){.shop-cartsum{position:static}}
        .shop-cartsum h2{margin:0 0 10px;font-size:1.15em}.shop-cartsum .shop-sum{max-width:none;margin:0}
        .shop-btn.big,.shop a.shop-btn.big{display:block;box-sizing:border-box;width:100%;padding:14px 20px;text-align:center;font-weight:600;font-size:1.05em}.shop-cartsum .shop-btn.big{margin-top:18px}
        .shop-cartsum .shop-coupon{margin-top:16px;padding-top:16px;border-top:1px solid var(--shop-line,var(--line,rgba(0,0,0,.12)))}.shop-cartsum .shop-coupon input[type=text]{flex:1 1 120px;min-width:0}
        .shop-cart-empty{text-align:center;max-width:520px;margin:28px auto 40px}.shop-cart-empty .icon svg{width:84px;height:84px;opacity:.35}.shop-cart-empty h2{margin:10px 0 6px}
        .shop-cart-empty .shop-btn.big{display:inline-block;width:auto;min-width:240px}.shop-cart-empty .shop-cats{justify-content:center;margin:22px 0 0}
        .shop-cartpicks{margin:8px 0 16px;font-size:1.2em}
        @media(max-width:560px){.shop-line{grid-template-columns:72px minmax(0,1fr);}.shop-line .img{width:72px;height:72px}.shop-line .lineprice{grid-column:2;text-align:left}}
        """;

    public static string Alert(string text, string kind = "info") => $"<div class=\"shop-alert {kind}\">{E(text)}</div>";

    /// <summary>The shop's small navigation: shop, cart (with its count), account — shown only where the site's menu has no cart element of its own.</summary>
    public static async Task<string> NavAsync(IPluginContext ctx)
    {
        var store = ShopStore.Public(ctx);
        var count = (await ShopCart.LinesAsync(ctx, store)).Sum(l => l.Qty);
        // The cart and the way to sign in are menu elements of the site (Admin → Menü, any template: a layout without a strip gets the icon strip floating) — the shop's small bar steps aside
        // when the page says the cart element is there (data-els); it only shows where the editor took the element out of every menu.
        var nav = new StringBuilder($"<nav class=\"shop-nav\">");
        {
            nav.Append($"<a href=\"/shop\">{E(ctx.T("shop.nav.shop"))}</a>");
            nav.Append($"<a href=\"/shop/cart\">{E(ctx.T("shop.nav.cart"))}{(count > 0 ? " (" + count.ToString(CultureInfo.InvariantCulture) + ")" : "")}</a>");
            nav.Append("<span class=\"spacer\"></span>");
            if (ctx.Member is { } m) nav.Append($"<a href=\"/konto\">{E(ctx.T("shop.nav.account"))}</a>");
            else nav.Append($"<a href=\"/anmelden?returnUrl=%2Fkonto\">{E(ctx.T("shop.nav.signin"))}</a>");
            nav.Append("</nav>");
        }
        return nav.ToString();
    }

    /// <summary>The page: the shop's stylesheet, its small navigation, the content. Raw HTML by design — every value in <paramref name="body"/> was encoded where it was written.
    /// A page the editors laid out in blocks (<paramref name="blocks"/>) is only given the stylesheet: its blocks bring their own frame, and the shop's pieces inside them their own <c>.shop</c> wrapper.</summary>
    public static async Task<PluginResult> PageAsync(IPluginContext ctx, ShopConfig cfg, string title, string body, string? description = null, int status = 200, bool noIndex = false, bool blocks = false, IReadOnlyList<KeyValuePair<string, string>>? meta = null)
    {
        var css = $"<style>{BaseCss}{cfg.CustomCss.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase)}</style>";
        var html = blocks ? css + body : css + $"<div class=\"shop\">{await NavAsync(ctx)}{body}</div>";
        var heads = new List<KeyValuePair<string, string>>();
        if (noIndex) heads.Add(new("robots", "noindex"));
        if (meta is not null) heads.AddRange(meta);
        var result = PluginResult.Page(html, title, description, heads.Count > 0 ? heads : null);
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
