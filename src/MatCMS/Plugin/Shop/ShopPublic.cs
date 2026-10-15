using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using static MatCMS.Plugin.Shop.ShopHtml;

namespace MatCMS.Plugin.Shop;

/// <summary>The pages a visitor sees: catalogue, product, cart, checkout, the order's own page and the invoice.</summary>
internal static class ShopPublic
{
    /// <summary>Words a product's address cannot be: they are the shop's own pages.</summary>
    public static readonly string[] ReservedSlugs = { "cart", "checkout", "order", "download", "invoice", "webhook", "pay", "account" };

    private static string M(IPluginContext ctx, ShopConfig cfg, long cents) => Money.Format(cents, cfg.Currency, ctx.Locale);
    private static string Csrf(IPluginContext ctx) => ctx.Antiforgery;
    private static string Intl(long n) => n.ToString(CultureInfo.InvariantCulture);

    // ---- catalogue ---------------------------------------------------------------------------------------

    /// <summary>An address of the catalogue with the filters that are set (and only those).</summary>
    private static string CatalogUrl(string cat, string tag, string q, string sort, int page = 1)
    {
        var parts = new List<string>();
        if (cat.Length > 0) parts.Add("cat=" + Uri.EscapeDataString(cat));
        if (tag.Length > 0) parts.Add("tag=" + Uri.EscapeDataString(tag));
        if (q.Length > 0) parts.Add("q=" + Uri.EscapeDataString(q));
        if (sort.Length > 0) parts.Add("sort=" + Uri.EscapeDataString(sort));
        if (page > 1) parts.Add("page=" + Intl(page));
        return parts.Count == 0 ? "/shop" : "/shop?" + string.Join("&", parts);
    }

    private static string SortLabel(IPluginContext ctx, string sort) => ctx.T(sort switch { "new" => "shop.sort.new", "price_asc" => "shop.sort.priceAsc", "price_desc" => "shop.sort.priceDesc", "name" => "shop.sort.name", _ => "shop.sort.manual" });

    public static async Task<PluginResult> CatalogAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        var cat = ctx.Request.Q("cat").Trim(); var tag = ctx.Request.Q("tag").Trim(); var q = ctx.Request.Q("q").Trim();
        var sort = ProductQuery.NormaliseSort(ctx.Request.Q("sort"));
        var page = int.TryParse(ctx.Request.Q("page"), out var pg) && pg > 0 ? pg : 1;
        var (items, total) = await store.FindProductsAsync(new ProductQuery
        {
            Category = cat.Length > 0 ? cat : null, Tag = tag.Length > 0 ? tag : null, Search = q.Length > 0 ? q : null, Sort = sort,
            Skip = (page - 1) * cfg.PerPage, Take = cfg.PerPage
        }, ct);
        var cats = await store.CategoriesAsync(ct);
        var (catCounts, tags) = await store.FacetsAsync(ct);
        var variants = (await store.VariantsOfAsync(items.Select(p => p.Id), ct)).Where(v => v.Active).GroupBy(v => v.ProductId).ToDictionary(g => g.Key, g => g.ToList());

        // The page speaks of the category it shows, when there is one (also the title a search engine sees).
        var current = cats.FirstOrDefault(c => c.Slug == cat);
        var title = current?.Name ?? ctx.T("shop.title");
        var sb = new StringBuilder($"<h1>{E(title)}</h1>");
        sb.Append($"<form method=\"get\" action=\"/shop\" class=\"shop-filter\"><input type=\"search\" name=\"q\" value=\"{E(q)}\" placeholder=\"{E(ctx.T("shop.search"))}\" aria-label=\"{E(ctx.T("shop.search"))}\">");
        if (cat.Length > 0) sb.Append($"<input type=\"hidden\" name=\"cat\" value=\"{E(cat)}\">");
        if (tag.Length > 0) sb.Append($"<input type=\"hidden\" name=\"tag\" value=\"{E(tag)}\">");
        sb.Append($"<select name=\"sort\" aria-label=\"{E(ctx.T("shop.sort.label"))}\" onchange=\"this.form.submit()\">");
        foreach (var s in ProductQuery.Sorts) sb.Append($"<option value=\"{E(s)}\"{(s == sort ? " selected" : "")}>{E(SortLabel(ctx, s))}</option>");
        sb.Append($"</select><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.searchButton"))}</button></form>");
        if (cats.Count > 0)
        {
            sb.Append("<div class=\"shop-cats\">");
            sb.Append($"<a href=\"{E(CatalogUrl("", tag, q, sort))}\" class=\"{(cat.Length == 0 ? "on" : "")}\">{E(ctx.T("shop.allCategories"))}</a>");
            foreach (var c in cats)
            {
                // A category without a visible product is not offered (an empty page is a dead end), unless it is the one being looked at.
                if (!catCounts.TryGetValue(c.Slug, out var n) && c.Slug != cat) continue;
                sb.Append($"<a href=\"{E(CatalogUrl(c.Slug, tag, q, sort))}\" class=\"{(c.Slug == cat ? "on" : "")}\">{E(c.Name)} <span class=\"shop-muted\">{Intl(n)}</span></a>");
            }
            sb.Append("</div>");
        }
        if (tags.Count > 0)
        {
            sb.Append($"<div class=\"shop-tags\" style=\"margin-bottom:20px\" aria-label=\"{E(ctx.T("shop.tags"))}\">");
            // The busiest keywords, and always the one being filtered by.
            foreach (var t in tags.Where((x, i) => i < 14 || string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase)))
            {
                var on = string.Equals(t.Tag, tag, StringComparison.OrdinalIgnoreCase);
                sb.Append($"<a href=\"{E(CatalogUrl(cat, on ? "" : t.Tag, q, sort))}\" class=\"{(on ? "on" : "")}\">{E(t.Tag)}</a>");
            }
            sb.Append("</div>");
        }
        if (cat.Length > 0 || tag.Length > 0 || q.Length > 0)
            sb.Append($"<p class=\"shop-small shop-muted\">{E(total == 1 ? ctx.T("shop.resultOne") : ctx.T("shop.resultMany", total))} · <a href=\"/shop\">{E(ctx.T("shop.resetFilter"))}</a></p>");
        if (items.Count == 0) sb.Append($"<p class=\"shop-muted\">{E(cat.Length > 0 || tag.Length > 0 || q.Length > 0 ? ctx.T("shop.noMatch") : ctx.T("shop.empty"))}</p>");
        else
        {
            sb.Append("<div class=\"shop-grid\">");
            foreach (var p in items) sb.Append(CardHtml(ctx, cfg, p, variants.TryGetValue(p.Id, out var vs) ? vs : new List<Variant>(), new CardView(true, true, true)));
            sb.Append("</div>");
            var pages = (int)Math.Ceiling(total / (double)cfg.PerPage);
            if (pages > 1)
            {
                sb.Append("<div class=\"shop-pager\">");
                for (var i = 1; i <= pages; i++)
                    sb.Append(i == page ? $"<span class=\"on\">{i}</span>" : $"<a href=\"{E(CatalogUrl(cat, tag, q, sort, i))}\">{i}</a>");
                sb.Append("</div>");
            }
        }
        return await PageAsync(ctx, cfg, title, sb.ToString());
    }

    internal static bool SoldOut(Product p, IReadOnlyList<Variant> variants) =>
        variants.Count > 0 ? variants.All(v => v.TrackStock ? v.Stock <= 0 : p.TrackStock && p.Stock <= 0) : p.TrackStock && p.Stock <= 0;

    /// <summary>The price as shown: "ab …" when variants differ.</summary>
    internal static string PriceText(IPluginContext ctx, ShopConfig cfg, Product p, IReadOnlyList<Variant> variants)
    {
        if (variants.Count == 0) return M(ctx, cfg, p.PriceCents);
        var prices = variants.Select(v => Math.Max(0, p.PriceCents + v.PriceDeltaCents)).ToList();
        return prices.Min() == prices.Max() ? M(ctx, cfg, prices[0]) : ctx.T("shop.from", M(ctx, cfg, prices.Min()));
    }

    /// <summary>The struck-through earlier price and the saving, after the price — when the product is on sale and has no variants (a variant's own price would make the comparison untrue).</summary>
    internal static string WasHtml(IPluginContext ctx, ShopConfig cfg, Product p, IReadOnlyList<Variant> variants) =>
        p.OnSale && variants.Count == 0
            ? $" <s class=\"shop-was\" aria-label=\"{E(ctx.T("shop.was", M(ctx, cfg, p.CompareCents)))}\">{E(M(ctx, cfg, p.CompareCents))}</s>{(p.SavePercent >= 1 ? $" <span class=\"shop-badge ok\">{E(ctx.T("shop.save", p.SavePercent))}</span>" : "")}"
            : "";

    /// <summary>What a card shows beyond the picture and the title.</summary>
    internal readonly record struct CardView(bool Summary, bool Price, bool Tags)
    {
        public static readonly CardView Default = new(true, true, false);
    }

    internal static string CardHtml(IPluginContext ctx, ShopConfig cfg, Product p, IReadOnlyList<Variant> variants, CardView? view = null)
    {
        var v = view ?? CardView.Default;
        var img = p.Image.Length > 0 ? $" style=\"background-image:url('{E(SafeUrl(p.Image))}')\"" : "";
        var sold = SoldOut(p, variants);
        return $"<div class=\"shop-card\"><a href=\"/shop/{E(Uri.EscapeDataString(p.Slug))}\" class=\"img\"{img} aria-label=\"{E(p.Title)}\"></a><div class=\"body\">" +
               $"<a class=\"title\" href=\"/shop/{E(Uri.EscapeDataString(p.Slug))}\">{E(p.Title)}</a>" +
               (v.Summary && p.Summary.Length > 0 ? $"<div class=\"shop-muted shop-small\">{E(Trunc(p.Summary, 110))}</div>" : "") +
               (v.Price ? $"<div class=\"shop-price\">{E(PriceText(ctx, cfg, p, variants))}{WasHtml(ctx, cfg, p, variants)}</div>" : "") +
               (v.Tags && ShopTags.Split(p.Tags) is { Count: > 0 } tags ? "<div class=\"shop-tags\">" + string.Concat(tags.Select(t => $"<a href=\"/shop?tag={E(Uri.EscapeDataString(t))}\">{E(t)}</a>")) + "</div>" : "") +
               (sold ? $"<div><span class=\"shop-badge bad\">{E(ctx.T("shop.soldOut"))}</span></div>" : p.IsDigital ? $"<div><span class=\"shop-badge\">{E(ctx.T("shop.digital"))}</span></div>" : "") +
               "</div></div>";
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";

    /// <summary>A picture address that is safe in a CSS url('…') and an img src: an http(s) or local path, no quotes or brackets.</summary>
    internal static string SafeUrl(string url)
    {
        var u = (url ?? "").Trim();
        if (u.IndexOfAny(new[] { '\'', '"', '(', ')', '\\', '<', '>', ' ' }) >= 0) return "";
        if (u.StartsWith('/') && !u.StartsWith("//")) return u;
        return u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? u : "";
    }

    // ---- product -----------------------------------------------------------------------------------------

    public static async Task<PluginResult> ProductAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        var slug = ctx.Request.R("slug");
        var p = ReservedSlugs.Contains(slug) ? null : await store.ProductBySlugAsync(slug, ct);
        if (p is null || !p.Active && !ctx.User.IsAdmin)
            return await PageAsync(ctx, cfg, ctx.T("shop.notFound"), $"<h1>{E(ctx.T("shop.notFound"))}</h1><p><a href=\"/shop\">{E(ctx.T("shop.backToShop"))}</a></p>", status: 404, noIndex: true);

        var variants = (await store.VariantsAsync(p.Id, true, ct)).ToList();
        var sold = SoldOut(p, variants);
        var sb = new StringBuilder();
        if (!p.Active) sb.Append(Alert(ctx.T("shop.inactiveNote"), "info"));
        var gallery = ParseGallery(p.GalleryJson);
        var hasPictures = SafeUrl(p.Image).Length > 0 || gallery.Count > 0;
        sb.Append(hasPictures ? "<div class=\"shop-product\"><div>" : "<div class=\"shop-product single\"><div style=\"display:none\">");
        if (SafeUrl(p.Image).Length > 0) sb.Append($"<img class=\"main-img\" src=\"{E(SafeUrl(p.Image))}\" alt=\"{E(p.Title)}\">");
        if (gallery is { Count: > 0 } gal)
        {
            sb.Append("<div class=\"shop-gallery\">");
            foreach (var g in gal) sb.Append($"<img src=\"{E(g)}\" alt=\"\" loading=\"lazy\">");
            sb.Append("</div>");
        }
        sb.Append("</div><div>");
        var category = p.Category.Length > 0 ? await store.CategoryBySlugAsync(p.Category, ct) : null;
        sb.Append($"<p class=\"shop-crumbs\"><a href=\"/shop\">{E(ctx.T("shop.title"))}</a>" + (category is null ? "" : $" › <a href=\"/shop?cat={E(Uri.EscapeDataString(category.Slug))}\">{E(category.Name)}</a>") + "</p>");
        sb.Append($"<h1>{E(p.Title)}</h1>");
        if (p.Summary.Length > 0) sb.Append($"<p class=\"shop-muted\">{E(p.Summary)}</p>");
        sb.Append($"<p class=\"shop-price\" style=\"font-size:1.5em\">{E(PriceText(ctx, cfg, p, variants))}{WasHtml(ctx, cfg, p, variants)}</p>");
        sb.Append($"<p class=\"shop-muted shop-small\">{E(cfg.SmallBusiness ? ctx.T("shop.smallBusinessNote") : ctx.T("shop.taxIncluded", Money.Percent(p.TaxBp)))}{(p.IsDigital ? "" : " · " + E(ctx.T("shop.plusShipping")))}</p>");
        if (sold) sb.Append($"<p><span class=\"shop-badge bad\">{E(ctx.T("shop.soldOut"))}</span></p>");
        else
        {
            sb.Append($"<form method=\"post\" action=\"/shop/cart\" class=\"shop-form\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"add\"><input type=\"hidden\" name=\"product\" value=\"{Intl(p.Id)}\">");
            if (variants.Count > 0)
            {
                sb.Append($"<div><label>{E(ctx.T("shop.variant"))}</label><select name=\"variant\">");
                foreach (var v in variants)
                {
                    var out_ = v.TrackStock && v.Stock <= 0;
                    var delta = v.PriceDeltaCents == 0 ? "" : " (" + (v.PriceDeltaCents > 0 ? "+" : "") + M(ctx, cfg, v.PriceDeltaCents) + ")";
                    sb.Append($"<option value=\"{Intl(v.Id)}\"{(out_ ? " disabled" : "")}>{E(v.Name + delta + (out_ ? " – " + ctx.T("shop.soldOut") : ""))}</option>");
                }
                sb.Append("</select></div>");
            }
            sb.Append($"<div class=\"shop-actions\"><input class=\"shop-qty\" type=\"number\" name=\"qty\" value=\"1\" min=\"1\" max=\"{(p.MaxPerOrder > 0 ? Intl(p.MaxPerOrder) : "999")}\"><button class=\"shop-btn\" type=\"submit\">{E(ctx.T("shop.addToCart"))}</button></div></form>");
            if (p.TrackStock && p.Stock > 0 && p.Stock <= cfg.LowStock && variants.Count == 0) sb.Append($"<p class=\"shop-small\">{E(ctx.T("shop.onlyLeft", p.Stock))}</p>");
        }
        if (p.IsDigital) sb.Append($"<p class=\"shop-small shop-muted\">{E(ctx.T("shop.digitalNote"))}</p>");
        if (ShopTags.Split(p.Tags) is { Count: > 0 } ptags)
            sb.Append("<div class=\"shop-tags\" style=\"margin-top:14px\">" + string.Concat(ptags.Select(t => $"<a href=\"/shop?tag={E(Uri.EscapeDataString(t))}\">{E(t)}</a>")) + "</div>");
        sb.Append("</div></div>");
        if (p.DescriptionHtml.Length > 0) sb.Append($"<h2>{E(ctx.T("shop.description"))}</h2><div class=\"shop-desc\">{p.DescriptionHtml}</div>");   // sanitised when it was saved
        sb.Append(await RelatedAsync(ctx, cfg, store, p, ct));
        sb.Append(JsonLd(ctx, cfg, p, variants, sold));
        return await PageAsync(ctx, cfg, p.Title, sb.ToString(), p.Summary.Length > 0 ? Trunc(p.Summary, 160) : null);
    }

    /// <summary>Up to four other products: first the ones of the same category, then those that share a keyword.</summary>
    private static async Task<string> RelatedAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, Product p, CancellationToken ct)
    {
        const int Want = 4;
        var found = new List<Product>();
        if (p.Category.Length > 0)
            found.AddRange((await store.FindProductsAsync(new ProductQuery { Category = p.Category, ExceptId = p.Id, Take = Want }, ct)).Items);
        foreach (var tag in ShopTags.Split(p.Tags))
        {
            if (found.Count >= Want) break;
            foreach (var other in (await store.FindProductsAsync(new ProductQuery { Tag = tag, ExceptId = p.Id, Take = Want }, ct)).Items)
                if (found.Count < Want && found.All(x => x.Id != other.Id)) found.Add(other);
        }
        if (found.Count == 0) return "";
        var variants = (await store.VariantsOfAsync(found.Select(x => x.Id), ct)).Where(v => v.Active).GroupBy(v => v.ProductId).ToDictionary(g => g.Key, g => g.ToList());
        var sb = new StringBuilder($"<h2>{E(ctx.T("shop.related"))}</h2><div class=\"shop-grid cols-4\">");
        foreach (var o in found) sb.Append(CardHtml(ctx, cfg, o, variants.TryGetValue(o.Id, out var vs) ? vs : new List<Variant>(), new CardView(false, true, false)));
        return sb.Append("</div>").ToString();
    }

    private static List<string> ParseGallery(string json)
    {
        var list = new List<string>();
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) is JsonArray arr)
                foreach (var n in arr)
                    if ((n is JsonObject o ? o["url"]?.GetValue<string>() : n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null) is { } url && SafeUrl(url).Length > 0)
                        list.Add(SafeUrl(url));
        }
        catch { /* a damaged gallery shows nothing */ }
        return list;
    }

    /// <summary>schema.org Product markup for search engines (name, picture, price, availability).</summary>
    private static string JsonLd(IPluginContext ctx, ShopConfig cfg, Product p, IReadOnlyList<Variant> variants, bool sold)
    {
        var o = new JsonObject
        {
            ["@context"] = "https://schema.org", ["@type"] = "Product", ["name"] = p.Title, ["description"] = p.Summary.Length > 0 ? p.Summary : p.Title,
            ["offers"] = new JsonObject
            {
                ["@type"] = "Offer", ["priceCurrency"] = cfg.Currency, ["price"] = (Math.Max(0, p.PriceCents) / 100m).ToString("0.00", CultureInfo.InvariantCulture),
                ["availability"] = sold ? "https://schema.org/OutOfStock" : "https://schema.org/InStock", ["url"] = $"{ctx.Site.Origin}/shop/{Uri.EscapeDataString(p.Slug)}"
            }
        };
        if (SafeUrl(p.Image).Length > 0) o["image"] = p.Image.StartsWith('/') ? ctx.Site.Origin + p.Image : p.Image;
        if (p.Sku.Length > 0) o["sku"] = p.Sku;
        return "<script type=\"application/ld+json\">" + o.ToJsonString().Replace("<", "\\u003c") + "</script>";
    }

    // ---- cart --------------------------------------------------------------------------------------------

    public static async Task<PluginResult> CartAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        var token = ShopCart.Token(ctx);

        // A coupon code is typed on the cart page. The page is drawn again with the answer (no redirect: the reason a code was refused is a sentence, not state).
        string? couponMessage = null; var couponTone = "info";
        var couponAction = ctx.Request.IsPost && ctx.Request.Action is "coupon" or "coupon-remove";
        if (couponAction && token.Length > 0 && await store.CartAsync(token, ct) is not null)
        {
            if (ctx.Request.Action == "coupon-remove") await store.SetCartCouponAsync(token, "", ct);
            else
            {
                var typed = Coupon.NormaliseCode(ctx.Request.F("code"));
                var pricedNow = await ShopOrders.PriceAsync(store, await ShopCart.LinesAsync(ctx, store, ct), cfg, ctx, ct);
                if (typed.Length == 0) { couponMessage = ctx.T("shop.coupon.empty"); couponTone = "error"; }
                else if (!ShopCoupons.AttemptAllowed(ctx.Request.ClientIp)) { couponMessage = ctx.T("shop.coupon.tooMany"); couponTone = "error"; }
                else
                {
                    var res = await ShopCoupons.ResolveAsync(ctx, store, pricedNow, typed, ctx.Member?.Email ?? "", ct);
                    if (res.Problem is not null) { ShopCoupons.RecordFailure(ctx.Request.ClientIp); couponMessage = res.Problem; couponTone = "error"; }
                    else { await store.SetCartCouponAsync(token, res.Coupon!.Code, ct); couponMessage = ctx.T("shop.coupon.applied", res.Coupon.Code); couponTone = "ok"; }
                }
            }
        }

        if (ctx.Request.IsPost && !couponAction)
        {
            var lines = (await ShopCart.LinesAsync(ctx, store, ct)).ToList();
            switch (ctx.Request.Action)
            {
                case "add":
                {
                    long.TryParse(ctx.Request.F("product"), out var pid); long.TryParse(ctx.Request.F("variant"), out var vid);
                    var qty = int.TryParse(ctx.Request.F("qty"), out var q) ? Math.Clamp(q, 1, 999) : 1;
                    var p = await store.ProductAsync(pid, ct);
                    if (p is null || !p.Active) return PluginResult.Redirect("/shop/cart");
                    var hasVariants = (await store.VariantsAsync(pid, true, ct)).Count > 0;
                    if (!hasVariants) vid = 0;
                    var i = lines.FindIndex(l => l.ProductId == pid && l.VariantId == vid);
                    if (i >= 0) lines[i] = lines[i] with { Qty = Math.Min(999, lines[i].Qty + qty) }; else lines.Add(new CartLine(pid, vid, qty));
                    break;
                }
                case "update":
                    foreach (var kv in ctx.Request.Form.Where(k => k.Key.StartsWith("q:", StringComparison.Ordinal)))
                    {
                        var parts = kv.Key.Split(':');
                        if (parts.Length != 3 || !long.TryParse(parts[1], out var pid) || !long.TryParse(parts[2], out var vid)) continue;
                        var i = lines.FindIndex(l => l.ProductId == pid && l.VariantId == vid);
                        if (i < 0) continue;
                        if (!int.TryParse(kv.Value, out var qty) || qty <= 0) lines.RemoveAt(i); else lines[i] = lines[i] with { Qty = Math.Min(qty, 999) };
                    }
                    break;
                case "remove":
                {
                    var parts = ctx.Request.F("line").Split(':');
                    if (parts.Length == 2 && long.TryParse(parts[0], out var pid) && long.TryParse(parts[1], out var vid)) lines.RemoveAll(l => l.ProductId == pid && l.VariantId == vid);
                    break;
                }
                case "clear": lines.Clear(); break;
            }
            if (token.Length == 0) token = ShopOrders.NewToken();
            await store.SaveCartAsync(token, ctx.Member?.Id ?? 0, lines, ct);
            return ShopCart.WithCartCookie(PluginResult.Redirect("/shop/cart"), token);
        }

        var current = await ShopCart.LinesAsync(ctx, store, ct);
        var priced = await ShopOrders.PriceAsync(store, current, cfg, ctx, ct);
        if (priced.Changed && token.Length > 0) await store.SaveCartAsync(token, ctx.Member?.Id ?? 0, priced.Lines.Select(l => new CartLine(l.Product.Id, l.Variant?.Id ?? 0, l.Qty)).ToList(), ct);

        // The cart's coupon, checked again every time it is looked at: it may have run out, or the cart may have shrunk below its minimum.
        var cartRow = token.Length > 0 ? await store.CartAsync(token, ct) : null;
        if (cartRow is { Coupon.Length: > 0 } && priced.Lines.Count > 0)
        {
            var res = await ShopCoupons.ResolveAsync(ctx, store, priced, cartRow.Coupon, ctx.Member?.Email ?? "", ct);
            if (res.Problem is not null) { await store.SetCartCouponAsync(token, "", ct); couponMessage ??= ctx.T("shop.coupon.removed", cartRow.Coupon, res.Problem); }
            else priced = res.Cart;
        }

        var sb = new StringBuilder($"<h1>{E(ctx.T("shop.cart"))}</h1>");
        if (couponMessage is not null) sb.Append(Alert(couponMessage, couponTone));
        foreach (var n in priced.Notes.Distinct()) sb.Append(Alert(n, "info"));
        if (priced.Lines.Count == 0)
            sb.Append($"<p class=\"shop-muted\">{E(ctx.T("shop.cartEmpty"))}</p><p><a class=\"shop-btn\" href=\"/shop\">{E(ctx.T("shop.continue"))}</a></p>");
        else
        {
            sb.Append($"<form method=\"post\" action=\"/shop/cart\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"update\"><table><thead><tr><th>{E(ctx.T("shop.item"))}</th><th class=\"r\">{E(ctx.T("shop.price"))}</th><th>{E(ctx.T("shop.qty"))}</th><th class=\"r\">{E(ctx.T("shop.total"))}</th><th></th></tr></thead><tbody>");
            foreach (var l in priced.Lines)
            {
                sb.Append($"<tr><td><a href=\"/shop/{E(Uri.EscapeDataString(l.Product.Slug))}\">{E(l.Title)}</a>{(l.VariantTitle.Length > 0 ? "<div class=\"shop-muted shop-small\">" + E(l.VariantTitle) + "</div>" : "")}{(l.Product.IsDigital ? "<div class=\"shop-small\"><span class=\"shop-badge\">" + E(ctx.T("shop.digital")) + "</span></div>" : "")}</td>");
                sb.Append($"<td class=\"r\">{E(M(ctx, cfg, l.UnitCents))}</td><td><input class=\"shop-qty\" type=\"number\" min=\"0\" max=\"999\" name=\"q:{Intl(l.Product.Id)}:{Intl(l.Variant?.Id ?? 0)}\" value=\"{l.Qty}\" style=\"padding:8px;border:1px solid rgba(0,0,0,.25);border-radius:6px;font:inherit\"></td>");
                sb.Append($"<td class=\"r\">{E(M(ctx, cfg, l.LineCents))}</td><td><button class=\"shop-btn ghost\" type=\"submit\" form=\"rm-{Intl(l.Product.Id)}-{Intl(l.Variant?.Id ?? 0)}\" title=\"{E(ctx.T("shop.remove"))}\">×</button></td></tr>");
            }
            sb.Append("</tbody></table>");
            sb.Append($"<div class=\"shop-sum\"><div><span>{E(ctx.T("shop.subtotal"))}</span><span>{E(M(ctx, cfg, priced.SubtotalCents))}</span></div>");
            if (priced.DiscountCents > 0)
                sb.Append($"<div><span>{E(ctx.T("shop.coupon.line", priced.CouponCode))}</span><span>−{E(M(ctx, cfg, priced.DiscountCents))}</span></div><div class=\"total\"><span>{E(ctx.T("shop.goodsTotal"))}</span><span>{E(M(ctx, cfg, priced.GoodsCents))}</span></div>");
            else if (priced.FreeShipping) sb.Append($"<div><span>{E(ctx.T("shop.coupon.line", priced.CouponCode))}</span><span>{E(ctx.T("shop.coupon.freeShipping"))}</span></div>");
            sb.Append($"<div class=\"shop-muted shop-small\"><span>{E(priced.NeedsShipping ? ctx.T("shop.shippingLater") : ctx.T("shop.noShipping"))}</span><span></span></div></div>");
            sb.Append($"<div class=\"shop-actions\" style=\"margin-top:20px\"><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.update"))}</button><a class=\"shop-btn\" href=\"/shop/checkout\">{E(ctx.T("shop.toCheckout"))}</a></div></form>");
            // Remove buttons are their own forms (a form inside the table form would not nest).
            foreach (var l in priced.Lines)
                sb.Append($"<form id=\"rm-{Intl(l.Product.Id)}-{Intl(l.Variant?.Id ?? 0)}\" method=\"post\" action=\"/shop/cart\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"remove\"><input type=\"hidden\" name=\"line\" value=\"{Intl(l.Product.Id)}:{Intl(l.Variant?.Id ?? 0)}\"></form>");
            sb.Append(CouponBox(ctx, priced));
        }
        return await PageAsync(ctx, cfg, ctx.T("shop.cart"), sb.ToString(), noIndex: true);
    }

    /// <summary>The field for a coupon code — or, once one is applied, what it does and a way to take it off.</summary>
    private static string CouponBox(IPluginContext ctx, PricedCart priced) =>
        priced.CouponCode.Length > 0
            ? $"<form method=\"post\" action=\"/shop/cart\" class=\"shop-coupon\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"coupon-remove\"><span class=\"shop-badge ok\">{E(ctx.T("shop.coupon.badge", priced.CouponCode))}</span> <button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.coupon.remove"))}</button></form>"
            : $"<form method=\"post\" action=\"/shop/cart\" class=\"shop-coupon\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"coupon\"><input type=\"text\" name=\"code\" maxlength=\"32\" autocomplete=\"off\" placeholder=\"{E(ctx.T("shop.coupon.placeholder"))}\" aria-label=\"{E(ctx.T("shop.coupon.label"))}\"> <button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.coupon.apply"))}</button></form>";

    // ---- checkout ----------------------------------------------------------------------------------------

    /// <summary>Text from a form, trimmed and cut to a sensible length: nobody has a 5000-letter street, and an order must not carry whatever a script posts.</summary>
    internal static string Clip(string? text, int max) { var t = (text ?? "").Trim(); return t.Length <= max ? t : t[..max]; }

    private static Address ReadAddress(RequestData f, string prefix, string fallbackName) => new()
    {
        Name = Clip(f.F(prefix + "name").Trim() is { Length: > 0 } n ? n : fallbackName, 120), Line1 = Clip(f.F(prefix + "line1"), 120), Line2 = Clip(f.F(prefix + "line2"), 120),
        Zip = Clip(f.F(prefix + "zip"), 20), City = Clip(f.F(prefix + "city"), 80), Country = Clip(f.F(prefix + "country"), 2).ToUpperInvariant()
    };

    public static async Task<PluginResult> CheckoutAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        var lines = await ShopCart.LinesAsync(ctx, store, ct);
        if (lines.Count == 0) return PluginResult.Redirect("/shop/cart");
        // No guest orders: the customer signs in — or, where the shop offers an account at checkout, creates one right here (then it is not optional).
        var accountRequired = !cfg.GuestCheckout && ctx.Member is null;
        if (accountRequired && !cfg.AccountsAtCheckout) return PluginResult.Redirect("/anmelden?returnUrl=%2Fshop%2Fcheckout");

        var priced = await ShopOrders.PriceAsync(store, lines, cfg, ctx, ct);
        var token = ShopCart.Token(ctx);
        // Something in the cart is no longer as it was (sold out, cheaper, gone): the cart page corrects it AND says so — this page only sends the buyer there. (Saving the
        // correction here would make the cart page see nothing to report.)
        if (priced.Changed) return PluginResult.Redirect("/shop/cart");

        // What the form starts with: the signed-in customer's own details, or what was typed before an error.
        var def = ctx.Member is { } m ? (await store.AddressesAsync(m.Id, ct)).FirstOrDefault(a => a.IsDefault) ?? (await store.AddressesAsync(m.Id, ct)).FirstOrDefault() : null;
        var f = ctx.Request;
        var posted = ctx.Request.IsPost && (f.Action is "place" or "recalc");
        var email = posted ? Clip(f.F("email"), 254) : ctx.Member?.Email ?? "";
        var billing = posted ? ReadAddress(f, "b_", f.F("b_name")) : def?.Address ?? new Address { Name = ctx.Member?.DisplayName ?? "", Country = "DE" };
        var shipOther = posted && priced.NeedsShipping && f.Form.ContainsKey("ship_other");
        var shipping = shipOther ? ReadAddress(f, "s_", billing.Name) : billing;
        var phone = posted ? Clip(f.F("phone"), 40) : def?.Address.Phone ?? "";
        var payMethod = posted ? f.F("pay") : "";
        long rateId = long.TryParse(f.F("rate"), out var rid) ? rid : 0;
        var country = (shipOther ? shipping.Country : billing.Country).ToUpperInvariant();
        // VAT by the country of delivery, then the cart's coupon (looked at again: it may no longer apply), then what shipping costs there.
        priced = ShopTax.Apply(priced, cfg, country, billing.Country.ToUpperInvariant());
        Coupon? coupon = null; string? couponProblem = null;
        var couponCode = (await store.CartAsync(token, ct))?.Coupon ?? "";
        if (couponCode.Length > 0)
        {
            var res = await ShopCoupons.ResolveAsync(ctx, store, priced, couponCode, email, ct);
            if (res.Problem is null) { priced = res.Cart; coupon = res.Coupon; }
            else { couponProblem = ctx.T("shop.coupon.removed", couponCode, res.Problem); await store.SetCartCouponAsync(token, "", ct); }
        }
        var options = ShopOrders.ShippingOptions(await store.RatesAsync(true, ct), country, priced);
        var chosen = ShopOrders.Pick(options, rateId);
        var totals = ShopOrders.Compute(priced, chosen, cfg);
        var methods = await Payments.MethodsAsync(ctx, cfg, totals.Total, ct);

        var errors = new List<string>();
        // A coupon that fell away is told to the buyer, who then sees the new total and decides again — an order is never placed at a price they did not see.
        if (couponProblem is not null) errors.Add(couponProblem);
        if (ctx.Request.IsPost && f.Action == "place" && couponProblem is null)
        {
            if (!ShopOrdersValidation.ValidEmail(email)) errors.Add(ctx.T("shop.err.email"));
            if (billing.Name.Trim().Length == 0) errors.Add(ctx.T("shop.err.name"));
            if (!billing.IsComplete) errors.Add(ctx.T("shop.err.address"));
            else if (!Countries.Known(billing.Country)) errors.Add(ctx.T("shop.err.country"));
            if (shipOther && (!shipping.IsComplete || !Countries.Known(shipping.Country))) errors.Add(ctx.T("shop.err.shipAddress"));
            if (priced.NeedsShipping && chosen is null) errors.Add(ctx.T("shop.err.noShipping", Countries.Name(country, ctx.Locale)));
            if (totals.Total > 0 && methods.All(x => x.Id != payMethod)) errors.Add(ctx.T("shop.err.payMethod"));
            if (!f.Form.ContainsKey("terms")) errors.Add(ctx.T("shop.err.terms"));
            if (priced.HasDigital && !f.Form.ContainsKey("waiver")) errors.Add(ctx.T("shop.err.waiver"));
            var createAccount = ctx.Member is null && cfg.AccountsAtCheckout && (accountRequired || f.Form.ContainsKey("create_account"));
            if (createAccount && f.F("password").Length < 8) errors.Add(ctx.T("shop.err.password", 8));
            if (ctx.Member is null && !cfg.GuestCheckout && !createAccount) errors.Add(ctx.T("shop.err.accountNeeded"));

            if (errors.Count == 0)
            {
                long memberId = ctx.Member?.Id ?? 0;
                if (createAccount)
                {
                    var created = await ctx.Members.CreateAsync(email, f.F("password"), billing.Name, ct);
                    if (created.Status == MemberCreateStatus.EmailTaken) errors.Add(ctx.T("shop.err.emailTaken"));
                    else if (created.Status != MemberCreateStatus.Created) errors.Add(ctx.T("shop.err.email"));
                    else { memberId = created.Account!.Id; await ctx.Members.SignInAsync(created.Account, false, ct); }
                }
                if (errors.Count == 0)
                {
                    var form = new CheckoutForm
                    {
                        Email = email, Name = billing.Name, Phone = phone, Billing = billing, Shipping = shipping, ShipToBilling = !shipOther, RateId = chosen?.Rate.Id ?? 0,
                        PayMethod = payMethod, Terms = true, Waiver = f.Form.ContainsKey("waiver"), Note = Clip(f.F("note"), 1000), Coupon = coupon?.Code ?? ""
                    };
                    var provider = totals.Total == 0 ? "free" : payMethod == Payments.Stripe || payMethod == Payments.PayPal || payMethod == Payments.Bank ? payMethod : "external";
                    var placed = await ShopOrders.PlaceAsync(ctx, cfg, form, lines, memberId, provider, ct);
                    if (placed.Order is null) errors.Add(placed.Error ?? ctx.T("shop.err.generic"));
                    else
                    {
                        var order = placed.Order;
                        var items = await store.ItemsAsync(order.Id, ct);
                        if (memberId > 0 && f.Form.ContainsKey("save_address"))
                            await store.SaveAddressAsync(0, memberId, billing.Line1, billing with { Phone = phone }, def is null, ct);
                        await ShopOrders.EmitPlacedAsync(ctx, order, ct);

                        if (totals.Total == 0)
                        {
                            await ShopOrders.MarkPaidAsync(ctx, cfg, order.Id, "system", "Bestellung ohne Zahlbetrag", ct: ct);
                            await store.DeleteCartAsync(token, ct);
                            return OrderRedirect(order).WithoutCookie(ShopCart.CookieName);
                        }
                        var start = await Payments.StartAsync(ctx, cfg, order, items, payMethod, ct);
                        if (start.Error is not null)
                        {
                            await ShopOrders.CancelAsync(ctx, cfg, order.Id, "payment_failed", "system", "Zahlung konnte nicht gestartet werden", notifyCustomer: false, ct: ct);
                            errors.Add(start.Error == "unknown" ? ctx.T("shop.err.payMethod") : start.Error);
                        }
                        else
                        {
                            await store.DeleteCartAsync(token, ct);
                            var go = start.RedirectUrl is { Length: > 0 } u ? (u.StartsWith('/') ? PluginResult.Redirect(u) : PluginResult.RedirectExternal(u)) : OrderRedirect(order);
                            return go.WithoutCookie(ShopCart.CookieName);
                        }
                    }
                }
            }
        }

        return await CheckoutPageAsync(ctx, cfg, store, priced, options, chosen, totals, methods, errors, email, billing, shipping, shipOther, phone, payMethod, accountRequired, ct);
    }

    private static PluginResult OrderRedirect(Order o) => PluginResult.Redirect($"/shop/order/{Uri.EscapeDataString(o.Number)}?t={Uri.EscapeDataString(o.AccessToken)}&new=1");

    private static string AddressFields(IPluginContext ctx, string prefix, Address a, bool name)
    {
        var sb = new StringBuilder();
        if (name) sb.Append($"<div><label>{E(ctx.T("shop.fullName"))}</label><input type=\"text\" name=\"{prefix}name\" value=\"{E(a.Name)}\" autocomplete=\"name\"></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.street"))}</label><input type=\"text\" name=\"{prefix}line1\" value=\"{E(a.Line1)}\" autocomplete=\"address-line1\"></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.street2"))}</label><input type=\"text\" name=\"{prefix}line2\" value=\"{E(a.Line2)}\" autocomplete=\"address-line2\"></div>");
        sb.Append($"<div class=\"shop-row\"><div><label>{E(ctx.T("shop.zip"))}</label><input type=\"text\" name=\"{prefix}zip\" value=\"{E(a.Zip)}\" autocomplete=\"postal-code\"></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.city"))}</label><input type=\"text\" name=\"{prefix}city\" value=\"{E(a.City)}\" autocomplete=\"address-level2\"></div></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.country"))}</label>{Select(prefix + "country", string.IsNullOrEmpty(a.Country) ? "DE" : a.Country, CountryOptions(ctx.Locale))}</div>");
        return sb.ToString();
    }

    private static async Task<PluginResult> CheckoutPageAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, PricedCart priced, IReadOnlyList<ShippingOption> options, ShippingOption? chosen,
        ShopOrders.Totals totals, IReadOnlyList<PayMethod> methods, List<string> errors, string email, Address billing, Address shipping, bool shipOther, string phone, string payMethod, bool accountRequired, CancellationToken ct)
    {
        var sb = new StringBuilder($"<h1>{E(ctx.T("shop.checkout"))}</h1>");
        foreach (var e in errors) sb.Append(Alert(e, "error"));
        sb.Append($"<form method=\"post\" action=\"/shop/checkout\" class=\"shop-form\" style=\"max-width:none\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"place\">");
        sb.Append("<div style=\"display:grid;grid-template-columns:minmax(0,3fr) minmax(0,2fr);gap:32px\" class=\"shop-checkout\"><div class=\"shop-form\">");

        sb.Append($"<h2 style=\"margin:0\">{E(ctx.T("shop.contact"))}</h2>");
        sb.Append($"<div><label>{E(ctx.T("shop.email"))}</label><input type=\"email\" name=\"email\" value=\"{E(email)}\" autocomplete=\"email\"{(ctx.Member is null ? "" : " readonly")}></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.phone"))}</label><input type=\"tel\" name=\"phone\" value=\"{E(phone)}\" autocomplete=\"tel\"></div>");
        sb.Append($"<h2 style=\"margin:8px 0 0\">{E(ctx.T("shop.billingAddress"))}</h2>").Append(AddressFields(ctx, "b_", billing, true));
        if (priced.NeedsShipping)
        {
            sb.Append($"<label class=\"shop-check\"><input type=\"checkbox\" name=\"ship_other\" value=\"1\"{(shipOther ? " checked" : "")}> <span>{E(ctx.T("shop.shipOther"))}</span></label>");
            // The second address is only there while the box is ticked (script); without script it is simply always shown.
            sb.Append($"<div id=\"ship-box\" class=\"shop-form\" style=\"padding-left:12px;border-left:3px solid rgba(0,0,0,.1){(shipOther ? "" : ";display:none")}\">").Append(AddressFields(ctx, "s_", shipping, true)).Append("</div>")
              .Append("<noscript><style>#ship-box{display:grid!important}</style></noscript>")
              .Append("<script>(function(){var c=document.querySelector('input[name=ship_other]'),b=document.getElementById('ship-box');if(c&&b)c.addEventListener('change',function(){b.style.display=c.checked?'':'none'})})()</script>");
            sb.Append($"<h2 style=\"margin:8px 0 0\">{E(ctx.T("shop.shippingMethod"))}</h2>");
            if (options.Count == 0) sb.Append($"<p class=\"shop-muted\">{E(ctx.T("shop.noShippingFor", Countries.Name((shipOther ? shipping : billing).Country, ctx.Locale)))}</p>");
            foreach (var o in options)
                sb.Append($"<label class=\"shop-check\"><input type=\"radio\" name=\"rate\" value=\"{Intl(o.Rate.Id)}\"{(chosen?.Rate.Id == o.Rate.Id ? " checked" : "")}> <span>{E(o.Rate.Name)} — {E(o.PriceCents == 0 ? ctx.T("shop.free") : M(ctx, cfg, o.PriceCents))}</span></label>");
            sb.Append($"<div><button class=\"shop-btn ghost\" type=\"submit\" name=\"action\" value=\"recalc\" formnovalidate>{E(ctx.T("shop.refreshShipping"))}</button></div>");
        }

        if (totals.Total > 0)
        {
            sb.Append($"<h2 style=\"margin:8px 0 0\">{E(ctx.T("shop.payment"))}</h2>");
            if (methods.Count == 0) sb.Append(Alert(ctx.T("shop.noPayment"), "error"));
            foreach (var m in methods)
                sb.Append($"<label class=\"shop-check\"><input type=\"radio\" name=\"pay\" value=\"{E(m.Id)}\"{(payMethod == m.Id || payMethod.Length == 0 && m == methods[0] ? " checked" : "")}> <span><strong>{E(m.Label)}</strong>{(m.Hint.Length > 0 ? "<br><span class=\"shop-muted shop-small\">" + E(m.Hint) + "</span>" : "")}</span></label>");
        }

        if (ctx.Member is null && cfg.AccountsAtCheckout && accountRequired)
            sb.Append($"<input type=\"hidden\" name=\"create_account\" value=\"1\"><p class=\"shop-muted\">{E(ctx.T("shop.accountRequired"))}</p>")
              .Append($"<div><label>{E(ctx.T("shop.password"))}</label><input type=\"password\" name=\"password\" autocomplete=\"new-password\"><div class=\"shop-small shop-muted\">{E(ctx.T("shop.passwordHint"))}</div></div>");
        else if (ctx.Member is null && cfg.AccountsAtCheckout)
            sb.Append($"<label class=\"shop-check\"><input type=\"checkbox\" name=\"create_account\" value=\"1\"{(ctx.Request.Form.ContainsKey("create_account") ? " checked" : "")}> <span>{E(ctx.T("shop.createAccount"))}</span></label>")
              .Append($"<div><label>{E(ctx.T("shop.password"))}</label><input type=\"password\" name=\"password\" autocomplete=\"new-password\"><div class=\"shop-small shop-muted\">{E(ctx.T("shop.passwordHint"))}</div></div>");
        else if (ctx.Member is not null)
            sb.Append($"<label class=\"shop-check\"><input type=\"checkbox\" name=\"save_address\" value=\"1\" checked> <span>{E(ctx.T("shop.saveAddress"))}</span></label>");
        sb.Append($"<div><label>{E(ctx.T("shop.note"))}</label><textarea name=\"note\" rows=\"3\">{E(ctx.Request.F("note"))}</textarea></div>");

        sb.Append("</div><div>");
        sb.Append($"<div class=\"shop-box\"><h2 style=\"margin-top:0\">{E(ctx.T("shop.yourOrder"))}</h2><table><tbody>");
        foreach (var l in priced.Lines)
            sb.Append($"<tr><td>{l.Qty} × {E(l.DisplayTitle)}</td><td class=\"r\">{E(M(ctx, cfg, l.LineCents))}</td></tr>");
        sb.Append("</tbody></table><div class=\"shop-sum\" style=\"max-width:none\">");
        sb.Append($"<div><span>{E(ctx.T("shop.subtotal"))}</span><span>{E(M(ctx, cfg, totals.Subtotal))}</span></div>");
        if (totals.Discount > 0) sb.Append($"<div><span>{E(ctx.T("shop.coupon.line", priced.CouponCode))}</span><span>−{E(M(ctx, cfg, totals.Discount))}</span></div>");
        if (priced.NeedsShipping) sb.Append($"<div><span>{E(ctx.T("shop.shipping"))}{(priced.FreeShipping ? " · " + E(ctx.T("shop.coupon.line", priced.CouponCode)) : "")}</span><span>{E(chosen is null ? "—" : M(ctx, cfg, totals.Shipping))}</span></div>");
        sb.Append($"<div class=\"total\"><span>{E(ctx.T("shop.total"))}</span><span>{E(M(ctx, cfg, totals.Total))}</span></div>");
        sb.Append($"<div class=\"shop-muted shop-small\"><span>{E(cfg.SmallBusiness ? ctx.T("shop.smallBusinessNote") : priced.TaxNote == "export" ? ctx.T("shop.taxExport") : ctx.T("shop.vatIncluded", M(ctx, cfg, totals.Tax)))}</span><span></span></div>");
        sb.Append($"<div class=\"shop-small\" style=\"margin-top:8px\"><a href=\"/shop/cart\">{E(priced.CouponCode.Length > 0 ? ctx.T("shop.coupon.change") : ctx.T("shop.coupon.have"))}</a></div>");
        sb.Append("</div></div>");

        var termsLink = cfg.TermsUrl.Length > 0 ? $"<a href=\"{E(cfg.TermsUrl)}\" target=\"_blank\" rel=\"noopener\">{E(ctx.T("shop.terms"))}</a>" : E(ctx.T("shop.terms"));
        var withdrawalLink = cfg.WithdrawalUrl.Length > 0 ? $"<a href=\"{E(cfg.WithdrawalUrl)}\" target=\"_blank\" rel=\"noopener\">{E(ctx.T("shop.withdrawal"))}</a>" : E(ctx.T("shop.withdrawal"));
        var privacyLink = cfg.PrivacyUrl.Length > 0 ? $"<a href=\"{E(cfg.PrivacyUrl)}\" target=\"_blank\" rel=\"noopener\">{E(ctx.T("shop.privacy"))}</a>" : E(ctx.T("shop.privacy"));
        sb.Append($"<label class=\"shop-check\" style=\"margin-top:12px\"><input type=\"checkbox\" name=\"terms\" value=\"1\"{(ctx.Request.Form.ContainsKey("terms") ? " checked" : "")}> <span>{string.Format(CultureInfo.InvariantCulture, E(ctx.T("shop.termsAccept")).Replace("{0}", "{0}").Replace("{1}", "{1}").Replace("{2}", "{2}"), termsLink, withdrawalLink, privacyLink)}</span></label>");
        if (priced.HasDigital)
            sb.Append($"<label class=\"shop-check\" style=\"margin-top:8px\"><input type=\"checkbox\" name=\"waiver\" value=\"1\"{(ctx.Request.Form.ContainsKey("waiver") ? " checked" : "")}> <span>{E(ctx.T("shop.waiver"))}</span></label>");
        sb.Append($"<p style=\"margin-top:16px\"><button class=\"shop-btn\" type=\"submit\" style=\"width:100%\">{E(totals.Total > 0 ? ctx.T("shop.placeOrder") : ctx.T("shop.placeFree"))}</button></p>");
        sb.Append("</div></div></form><style>@media(max-width:820px){.shop-checkout{grid-template-columns:1fr!important}}</style>");
        return await PageAsync(ctx, cfg, ctx.T("shop.checkout"), sb.ToString(), noIndex: true, status: errors.Count > 0 ? 422 : 200);
    }

    // ---- the order's own page ----------------------------------------------------------------------------

    public static async Task<PluginResult> OrderAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        var order = await store.OrderByNumberAsync(ctx.Request.R("number"), ct);
        if (order is null || !ShopAccess.May(ctx, order, ctx.Request.Q("t")))
            return await PageAsync(ctx, cfg, ctx.T("shop.notFound"), $"<h1>{E(ctx.T("shop.notFound"))}</h1>", status: 404, noIndex: true);
        var token = ctx.Request.Q("t");
        var self = $"/shop/order/{Uri.EscapeDataString(order.Number)}" + (token.Length > 0 ? "?t=" + Uri.EscapeDataString(token) : "");

        string? message = null; var tone = "info";
        if (ctx.Request.IsPost)
        {
            if (ctx.Request.Action == "cancel" && order.Status == OrderStatus.AwaitingPayment)
            {
                await ShopOrders.CancelAsync(ctx, cfg, order.Id, "customer", "customer", "Vom Kunden storniert", ct: ct);
                return PluginResult.Redirect(self);
            }
            if (ctx.Request.Action == "retry" && order.Status == OrderStatus.AwaitingPayment && (order.PaymentProvider == Payments.Stripe && cfg.StripeReady || order.PaymentProvider == Payments.PayPal && cfg.PayPalReady))
            {
                var start = order.PaymentProvider == Payments.PayPal
                    ? await PayPalPayments.RestartAsync(ctx, cfg, order, await store.ItemsAsync(order.Id, ct), ct)
                    : await StripePayments.RestartAsync(ctx, cfg, order, await store.ItemsAsync(order.Id, ct), ct);
                if (start.RedirectUrl is { Length: > 0 } u) return PluginResult.RedirectExternal(u);
                message = start.Error ?? ctx.T("shop.err.payment"); tone = "error";
            }
        }
        // The customer is back from Stripe before the notification was: ask Stripe, so the page tells the truth.
        if (order.Status == OrderStatus.AwaitingPayment && ctx.Request.Q("paid") == "1")
        {
            await StripePayments.ReconcileAsync(ctx, cfg, order, ct);
            order = (await store.OrderAsync(order.Id, ct))!;
        }
        // Back from PayPal's page: take the payment now (asked of the order's OWN PayPal order — the address only says "the customer returned").
        if (order.Status == OrderStatus.AwaitingPayment && ctx.Request.Q("paypal") == "1")
        {
            await PayPalPayments.CaptureAsync(ctx, cfg, order, ct);
            order = (await store.OrderAsync(order.Id, ct))!;
        }

        var items = await store.ItemsAsync(order.Id, ct);
        string Mo(long c) => Money.Format(c, order.Currency, ctx.Locale);
        var sb = new StringBuilder($"<h1>{E(ctx.T("shop.orderNo", order.Number))} {StatusBadge(ctx, order.Status)}</h1>");
        if (message is not null) sb.Append(Alert(message, tone));
        if (ctx.Request.Q("new") == "1" || ctx.Request.Q("paid") == "1" || ctx.Request.Q("paypal") == "1")
            sb.Append(Alert(order.Status switch
            {
                OrderStatus.AwaitingPayment => order.PaymentProvider == Payments.Bank ? ctx.T("shop.thanksBank") : ctx.T("shop.thanksPending"),
                _ => ctx.T("shop.thanksPaid")
            }, order.Status == OrderStatus.AwaitingPayment ? "info" : "ok"));
        if (ctx.Request.Q("cancelled") == "1" && order.Status == OrderStatus.AwaitingPayment) sb.Append(Alert(ctx.T("shop.paymentCancelled"), "info"));

        if (order.Status == OrderStatus.AwaitingPayment)
        {
            sb.Append("<div class=\"shop-box\">");
            if (order.PaymentProvider == Payments.Bank) sb.Append($"<div class=\"shop-pre\">{E(Payments.BankInstructions(ctx, cfg, order))}</div>");
            else if (order.PaymentProvider == Payments.Stripe && cfg.StripeReady || order.PaymentProvider == Payments.PayPal && cfg.PayPalReady)
                sb.Append($"<p>{E(ctx.T("shop.payNow"))}</p><form method=\"post\" action=\"{E(self)}\" class=\"shop-actions\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"retry\"><button class=\"shop-btn\" type=\"submit\">{E(ctx.T("shop.payAgain"))}</button></form>");
            sb.Append($"<form method=\"post\" action=\"{E(self)}\" style=\"margin-top:12px\" data-confirm=\"{E(ctx.T("shop.cancelConfirm"))}\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"cancel\"><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.cancelOrder"))}</button></form></div>");
        }

        sb.Append($"<table><thead><tr><th>{E(ctx.T("shop.item"))}</th><th class=\"r\">{E(ctx.T("shop.price"))}</th><th class=\"r\">{E(ctx.T("shop.qty"))}</th><th class=\"r\">{E(ctx.T("shop.total"))}</th></tr></thead><tbody>");
        foreach (var i in items) sb.Append($"<tr><td>{E(i.DisplayTitle)}</td><td class=\"r\">{E(Mo(i.UnitCents))}</td><td class=\"r\">{i.Qty}</td><td class=\"r\">{E(Mo(i.LineCents))}</td></tr>");
        sb.Append("</tbody></table><div class=\"shop-sum\">");
        sb.Append($"<div><span>{E(ctx.T("shop.subtotal"))}</span><span>{E(Mo(order.SubtotalCents))}</span></div>");
        if (order.DiscountCents > 0) sb.Append($"<div><span>{E(ctx.T("shop.coupon.line", order.CouponCode))}</span><span>−{E(Mo(order.DiscountCents))}</span></div>");
        if (order.ShippingCents > 0 || order.ShippingMethod.Length > 0) sb.Append($"<div><span>{E(ctx.T("shop.shipping"))}{(order.ShippingMethod.Length > 0 ? " (" + E(order.ShippingMethod) + ")" : "")}</span><span>{E(Mo(order.ShippingCents))}</span></div>");
        sb.Append($"<div class=\"total\"><span>{E(ctx.T("shop.total"))}</span><span>{E(Mo(order.TotalCents))}</span></div>");
        if (order.RefundedCents > 0) sb.Append($"<div><span>{E(ctx.T("shop.refunded"))}</span><span>−{E(Mo(order.RefundedCents))}</span></div>");
        sb.Append("</div>");

        if (OrderStatus.IsPaid(order.Status))
        {
            var downloads = (await store.DownloadsOfOrderAsync(order.Id, ct)).Where(d => !d.Revoked).ToList();
            if (downloads.Count > 0)
            {
                sb.Append($"<h2>{E(ctx.T("shop.downloads"))}</h2><div class=\"shop-box\">");
                foreach (var d in downloads)
                    if (await store.FileAsync(d.FileId, ct) is { } file)
                        sb.Append($"<div><a href=\"/shop/download/{E(Uri.EscapeDataString(order.Number))}/{Intl(file.Id)}{(token.Length > 0 ? "?t=" + E(Uri.EscapeDataString(token)) : "")}\">{E(file.FileName)}</a> <span class=\"shop-muted shop-small\">({E(FileSize(file.Size))}{(d.MaxDownloads > 0 ? ", " + E(ctx.T("shop.dlLeft", Math.Max(0, d.MaxDownloads - d.Count))) : "")}, {E(ctx.T("shop.dlUntil", d.ExpiresUtc.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)))})</span></div>");
                sb.Append("</div>");
            }
        }
        // The invoice and its credit notes stay reachable after a refund: a refunded order is no longer "paid", but its documents are still what the customer was billed.
        if (order.InvoiceNumber.Length > 0)
        {
            {
                var credits = await store.CreditNotesOfOrderAsync(order.Id, ct);
                string DocUrl(string? doc, bool pdf) => $"/shop/invoice/{E(Uri.EscapeDataString(order.Number))}?" + string.Join("&amp;", new[]
                    { token.Length > 0 ? "t=" + E(Uri.EscapeDataString(token)) : "", doc is null ? "" : "doc=" + E(Uri.EscapeDataString(doc)), pdf ? "pdf=1" : "" }.Where(x => x.Length > 0));
                sb.Append($"<p><a class=\"shop-btn ghost\" href=\"{DocUrl(null, false)}\" target=\"_blank\" rel=\"noopener\">{E(ctx.T("shop.invoice"))} {E(order.InvoiceNumber)}</a> <a href=\"{DocUrl(null, true)}\" style=\"margin-left:10px\">{E(ctx.T("shop.pdf"))}</a></p>");
                foreach (var c in credits)
                    sb.Append($"<p class=\"shop-small\"><a href=\"{DocUrl(c.Number, false)}\" target=\"_blank\" rel=\"noopener\">{E(ctx.T("shop.creditNote"))} {E(c.Number)}</a> ({E(Mo(c.AmountCents))}) · <a href=\"{DocUrl(c.Number, true)}\">{E(ctx.T("shop.pdf"))}</a></p>");
            }
        }
        if (order.Tracking.Length > 0) sb.Append($"<p>{E(ctx.T("shop.tracking"))}: <strong>{E(order.Tracking)}</strong></p>");

        sb.Append("<div class=\"shop-row\" style=\"margin-top:16px\">");
        sb.Append($"<div class=\"shop-box\"><strong>{E(ctx.T("shop.billingAddress"))}</strong><div class=\"shop-pre\">{E(order.Billing.Lines())}</div></div>");
        if (order.Shipping.IsComplete) sb.Append($"<div class=\"shop-box\"><strong>{E(ctx.T("shop.shippingAddress"))}</strong><div class=\"shop-pre\">{E(order.Shipping.Lines())}</div></div>");
        sb.Append("</div>");

        var visible = new[] { "placed", "paid", "shipped", "delivered", "cancelled", "refund" };
        var events = (await store.EventsAsync(order.Id, ct)).Where(e => visible.Contains(e.Kind)).ToList();
        if (events.Count > 0)
        {
            sb.Append($"<h2>{E(ctx.T("shop.history"))}</h2><div class=\"shop-small\">");
            foreach (var e in events) sb.Append($"<div><span class=\"shop-muted\">{e.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}</span> — {E(ctx.T("shop.event." + e.Kind))}</div>");
            sb.Append("</div>");
        }
        if (ctx.Member is not null) sb.Append($"<p style=\"margin-top:24px\"><a href=\"/konto/bestellungen\">← {E(ctx.T("shop.myOrders"))}</a></p>");
        return await PageAsync(ctx, cfg, ctx.T("shop.orderNo", order.Number), sb.ToString(), noIndex: true);
    }

    public static string FileSize(long bytes) => bytes switch
    {
        < 1024 => bytes + " B", < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB", _ => (bytes / 1073741824.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB"
    };

    // ---- invoice -----------------------------------------------------------------------------------------

    public static async Task<PluginResult> InvoiceAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var order = await store.OrderByNumberAsync(ctx.Request.R("number"), ct);
        if (order is null || !ShopAccess.May(ctx, order, ctx.Request.Q("t"))) return PluginResult.NotFound();
        // The invoice, or — with ?doc= — one of the order's credit notes; as a page, or as the PDF with ?pdf=1.
        var wanted = ctx.Request.Q("doc");
        var inv = wanted.Length == 0 ? await store.InvoiceOfOrderAsync(order.Id, ct) : await store.DocumentOfOrderAsync(order.Id, wanted, ct);
        if (inv is null) return PluginResult.NotFound();
        if (ctx.Request.Q("pdf") == "1")
        {
            var bytes = inv.Pdf;
            if (bytes.Length == 0) return PluginResult.NotFound();
            return PluginResult.File(bytes, "application/pdf", $"{(inv.IsCredit ? "Gutschrift" : "Rechnung")}-{inv.Number}.pdf").WithHeader("Cache-Control", "private, no-store").WithHeader("X-Robots-Tag", "noindex");
        }
        var self = $"/shop/invoice/{Uri.EscapeDataString(order.Number)}?pdf=1" + (ctx.Request.Q("t").Length > 0 ? "&t=" + Uri.EscapeDataString(ctx.Request.Q("t")) : "") + (wanted.Length > 0 ? "&doc=" + Uri.EscapeDataString(wanted) : "");
        return PluginResult.Fragment(inv.Html.Replace(ShopInvoice.PdfMarker, E(self))).WithHeader("Cache-Control", "private, no-store").WithHeader("X-Robots-Tag", "noindex");
    }
}

/// <summary>Checks on what a visitor typed.</summary>
internal static class ShopOrdersValidation
{
    public static bool ValidEmail(string? email)
    {
        var e = (email ?? "").Trim();
        if (e.Length is < 5 or > 254 || e.Any(char.IsControl) || e.Contains(' ')) return false;                      // no line break can slip into a mail header
        var at = e.IndexOf('@');
        return at > 0 && at == e.LastIndexOf('@') && e.IndexOf('.', at) > at + 1 && !e.EndsWith('.');
    }
}
