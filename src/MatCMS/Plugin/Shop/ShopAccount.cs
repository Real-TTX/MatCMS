using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using static MatCMS.Plugin.Shop.ShopHtml;

namespace MatCMS.Plugin.Shop;

/// <summary>The customer's own pages (<c>/konto</c>): orders, addresses, password, deleting the account, and the export of their data. All of it behind
/// <see cref="PublicRouteOptions.RequireMember"/>, so <c>ctx.Member</c> is never null here, and with antiforgery on every form. The frame (who is signed in, the navigation) is the template
/// part <c>account</c>, so a site can restyle or rearrange the whole area; the pages' content is the shop's own markup on the site's colours.</summary>
internal static class ShopAccount
{
    private static string Csrf(IPluginContext ctx) => ctx.Antiforgery;

    private static string Icon(string name) =>
        "<svg class=\"ico\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">" + name switch
        {
            "home" => "<path d=\"M5 12H3l9-9 9 9h-2\"/><path d=\"M5 12v7a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-7\"/><path d=\"M9 21v-6a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2v6\"/>",
            "package" => "<path d=\"M12 3l8 4.5v9L12 21l-8-4.5v-9L12 3\"/><path d=\"M12 12l8-4.5\"/><path d=\"M12 12v9\"/><path d=\"M12 12L4 7.5\"/>",
            "pin" => "<circle cx=\"12\" cy=\"11\" r=\"3\"/><path d=\"M17.657 16.657l-4.243 4.243a2 2 0 0 1-2.827 0l-4.244-4.243a8 8 0 1 1 11.314 0z\"/>",
            "user" => "<circle cx=\"12\" cy=\"7\" r=\"4\"/><path d=\"M6 21v-2a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v2\"/>",
            "out" => "<path d=\"M14 8V6a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h7a2 2 0 0 0 2-2v-2\"/><path d=\"M9 12h12l-3-3\"/><path d=\"m18 15 3-3\"/>",
            "truck" => "<circle cx=\"7\" cy=\"17\" r=\"2\"/><circle cx=\"17\" cy=\"17\" r=\"2\"/><path d=\"M5 17H3V6a1 1 0 0 1 1-1h9v12m-4 0h6m4 0h2v-6h-8m0-5h5l3 5\"/>",
            "file" => "<path d=\"M14 3v4a1 1 0 0 0 1 1h4\"/><path d=\"M17 21H7a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h7l5 5v11a2 2 0 0 1-2 2z\"/><path d=\"M9 13h6\"/><path d=\"M9 17h6\"/>",
            "bag" => "<path d=\"M6.331 8H17.67a2 2 0 0 1 1.977 2.304l-1.255 8.152A3 3 0 0 1 15.426 21H8.574a3 3 0 0 1-2.965-2.544l-1.255-8.152A2 2 0 0 1 6.331 8z\"/><path d=\"M9 11V6a3 3 0 0 1 6 0v5\"/>",
            _ => ""
        } + "</svg>";

    /// <summary>The letters in the round picture: first letters of the first and last word of the name, or the first letter of the address.</summary>
    internal static string Initials(string name, string email)
    {
        var words = (name ?? "").Split(new[] { ' ', '-', '.', '@' }, StringSplitOptions.RemoveEmptyEntries);
        var s = words.Length >= 2 ? string.Concat(words[0][0], words[^1][0]) : words.Length == 1 ? words[0][..Math.Min(2, words[0].Length)] : (email.Length > 0 ? email[..1] : "?");
        return s.ToUpperInvariant();
    }

    /// <summary>The account's navigation, for the sample the block editor shows.</summary>
    internal static string NavFor(IPluginContext ctx, string current) => Nav(ctx, current);

    private static string Nav(IPluginContext ctx, string current)
    {
        string Item(string key, string href, string icon, string label) =>
            $"<a href=\"{href}\"{(key == current ? " class=\"on\" aria-current=\"page\"" : "")}>{Icon(icon)}<span>{E(label)}</span></a>";
        return $"<nav class=\"shop-accnav\" aria-label=\"{E(ctx.T("shop.acc.navLabel"))}\">" + Item("home", "/konto", "home", ctx.T("shop.acc.overview")) + Item("orders", "/konto/bestellungen", "package", ctx.T("shop.myOrders"))
               + Item("addresses", "/konto/adressen", "pin", ctx.T("shop.acc.addresses")) + Item("profile", "/konto/profil", "user", ctx.T("shop.acc.profile")) + "</nav>";
    }

    /// <summary>One account page: the content inside the template part <c>account</c> (who is signed in, the navigation, the way out) inside the shop's page.</summary>
    private static async Task<PluginResult> ShellAsync(IPluginContext ctx, ShopConfig cfg, string section, string heading, string pageTitle, string content)
    {
        var m = ctx.Member!;
        var name = string.IsNullOrWhiteSpace(m.DisplayName) ? m.Username : m.DisplayName;
        var signOut = $"<form method=\"post\" action=\"/anmelden?handler=Logout\" class=\"shop-accout\">{Csrf(ctx)}<input type=\"hidden\" name=\"returnUrl\" value=\"/shop\"><button type=\"submit\">{Icon("out")}<span>{E(ctx.T("shop.acc.signOut"))}</span></button></form>";
        var initials = Initials(name, m.Email);
        var values = new Dictionary<string, string>
        {
            ["title"] = heading, ["section"] = section, ["name"] = name, ["email"] = m.Email, ["initials"] = initials,
            ["profile_html"] = $"<div class=\"shop-accwho\"><span class=\"avatar\" aria-hidden=\"true\">{E(initials)}</span><div><strong>{E(name)}</strong><span class=\"mail\">{E(m.Email)}</span></div></div>",
            ["nav_html"] = Nav(ctx, section), ["signout_html"] = signOut, ["content_html"] = content
        };
        return await ShopPageHost.RunAsync(ctx, "account", new PageView(null, pageTitle, null, values, ctx.RenderPart("account", values)) { NoIndex = true }, default);
    }

    private static async Task<(long Id, string Email)> WhoAsync(IPluginContext ctx, ShopStore store, CancellationToken ct)
    {
        // The account as it is NOW (an address changed by the administrator is not the one in a cookie).
        var account = await ctx.Members.FindAsync(ctx.Member!.Id, ct);
        return (ctx.Member.Id, account?.Email ?? ctx.Member.Email);
    }

    private static string Day(DateTime d) => d.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    /// <summary>The empty state of a list of orders: a bag, what the area is for and the way into the shop.</summary>
    private static string EmptyOrders(IPluginContext ctx) =>
        $"<div class=\"shop-accempty\">{Icon("bag")}<h2>{E(ctx.T("shop.acc.emptyTitle"))}</h2><p class=\"shop-muted\">{E(ctx.T("shop.acc.emptyText"))}</p><a class=\"shop-btn\" href=\"/shop\">{E(ctx.T("shop.continue"))}</a></div>";

    /// <summary>An order as a card: number and date, its state (and, while it is on its way, a progress bar), the pictures of what was bought, the total, and what can be done with it — look at it,
    /// follow the parcel, open the invoice.</summary>
    private static async Task<string> OrderCardAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, Order o, CancellationToken ct)
    {
        var items = await store.ItemsAsync(o.Id, ct);
        var byId = (await store.ProductsByIdsAsync(items.Select(i => i.ProductId).Where(id => id > 0).Distinct(), ct)).ToDictionary(p => p.Id);
        var url = "/shop/order/" + Uri.EscapeDataString(o.Number);
        var sb = new StringBuilder($"<article class=\"shop-ocard\"><header><div><a class=\"num\" href=\"{E(url)}\">{E(ctx.T("shop.orderNo", o.Number))}</a><span class=\"shop-muted shop-small\">{E(Day(o.CreatedUtc))}</span></div>{StatusBadge(ctx, o.Status)}</header>");
        if (o.Status is not (OrderStatus.Cancelled or OrderStatus.Refunded))
        {
            var reached = o.Status switch { OrderStatus.AwaitingPayment => 0, OrderStatus.Paid => 1, OrderStatus.Shipped => 2, _ => 3 };
            var keys = new[] { "placed", "paid", "shipped", "done" };
            sb.Append($"<ol class=\"shop-prog\" aria-label=\"{E(ctx.T("shop.acc.progress"))}\">");
            for (var i = 0; i < keys.Length; i++) sb.Append($"<li class=\"{(i <= reached ? "on" : "")}\"><i></i><span>{E(ctx.T("shop.step." + keys[i]))}</span></li>");
            sb.Append("</ol>");
        }
        sb.Append("<div class=\"body\"><div class=\"thumbs\">");
        foreach (var i in items.Take(5))
        {
            var img = byId.TryGetValue(i.ProductId, out var p) ? ShopPublic.SafeUrl(p.Image) : "";
            sb.Append($"<a class=\"th\" href=\"{E(url)}\" title=\"{E(i.DisplayTitle)}\"{(img.Length > 0 ? $" style=\"background-image:url('{E(img)}')\"" : "")}>{(img.Length == 0 && i.DisplayTitle.Length > 0 ? E(i.DisplayTitle[..1].ToUpperInvariant()) : "")}{(i.Qty > 1 ? $"<b>{i.Qty}</b>" : "")}</a>");
        }
        if (items.Count > 5) sb.Append($"<a class=\"th more\" href=\"{E(url)}\">+{items.Count - 5}</a>");
        sb.Append("</div><div class=\"what\">");
        if (items.Count > 0) sb.Append($"<div class=\"t\">{E(items[0].DisplayTitle)}{(items.Count > 1 ? " <span class=\"shop-muted\">" + E(ctx.T("shop.acc.andMore", items.Count - 1)) + "</span>" : "")}</div>");
        sb.Append($"<div class=\"shop-muted shop-small\">{E(items.Sum(i => i.Qty) == 1 ? ctx.T("shop.acc.itemOne") : ctx.T("shop.acc.itemMany", items.Sum(i => i.Qty)))}</div></div>");
        sb.Append($"<div class=\"sum\">{E(Money.Format(o.TotalCents, o.Currency, ctx.Locale))}</div></div><footer>");
        sb.Append($"<a class=\"shop-btn ghost\" href=\"{E(url)}\">{E(ctx.T("shop.acc.details"))}</a>");
        if (o.Status == OrderStatus.Shipped && ShopTracking.UrlOf(cfg, o) is { Length: > 0 } track)
            sb.Append($"<a class=\"shop-btn\" href=\"{E(track)}\" target=\"_blank\" rel=\"noopener noreferrer\">{Icon("truck")}{E(ctx.T("shop.trackShipment"))}</a>");
        if (o.InvoiceNumber.Length > 0)
            sb.Append($"<a class=\"shop-btn ghost\" href=\"/shop/invoice/{E(Uri.EscapeDataString(o.Number))}\" target=\"_blank\" rel=\"noopener\">{Icon("file")}{E(ctx.T("shop.invoice"))}</a>");
        return sb.Append("</footer></article>").ToString();
    }

    public static async Task<PluginResult> HomeAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        var (id, email) = await WhoAsync(ctx, store, ct);
        var orders = (await store.OrdersAsync(memberId: id, email: null, ct: ct)).Items;
        var open = orders.Count(o => o.Status is OrderStatus.AwaitingPayment or OrderStatus.Paid or OrderStatus.Shipped);
        var sb = new StringBuilder();
        sb.Append($"<div class=\"shop-stats\"><div><span class=\"n\">{orders.Count}</span><span class=\"l\">{E(ctx.T("shop.acc.statOrders"))}</span></div>"
                  + $"<div><span class=\"n\">{open}</span><span class=\"l\">{E(ctx.T("shop.acc.statOpen"))}</span></div>"
                  + $"<div><span class=\"n\">{E(orders.Count > 0 ? Day(orders.Max(o => o.CreatedUtc)) : "—")}</span><span class=\"l\">{E(ctx.T("shop.acc.statLast"))}</span></div></div>");
        if (orders.Count == 0) sb.Append(EmptyOrders(ctx));
        else
        {
            sb.Append($"<div class=\"shop-sechead\"><h2>{E(ctx.T("shop.acc.recent"))}</h2><a href=\"/konto/bestellungen\">{E(ctx.T("shop.acc.allOrders"))} →</a></div><div class=\"shop-ocards\">");
            foreach (var o in orders.Take(3)) sb.Append(await OrderCardAsync(ctx, cfg, store, o, ct));
            sb.Append("</div>");
        }
        var addresses = await store.AddressesAsync(id, ct);
        var main = addresses.FirstOrDefault(a => a.IsDefault) ?? addresses.FirstOrDefault();
        sb.Append("<div class=\"shop-accgrid\">");
        sb.Append($"<div class=\"shop-card-box\"><h2>{Icon("pin")}{E(ctx.T("shop.acc.shipTo"))}</h2>{(main is null ? $"<p class=\"shop-muted\">{E(ctx.T("shop.acc.noAddresses"))}</p>" : $"<strong>{E(main.Label)}</strong><div class=\"shop-pre\">{E(main.Address.Lines())}</div>")}<a href=\"/konto/adressen\">{E(ctx.T("shop.acc.manage"))} →</a></div>");
        sb.Append($"<div class=\"shop-card-box\"><h2>{Icon("user")}{E(ctx.T("shop.acc.account"))}</h2><strong>{E(ctx.Member!.DisplayName)}</strong><div class=\"shop-muted\">{E(email)}</div><a href=\"/konto/profil\">{E(ctx.T("shop.acc.editProfile"))} →</a></div></div>");
        return await ShellAsync(ctx, cfg, "home", ctx.T("shop.acc.hello", ctx.Member!.DisplayName), ctx.T("shop.nav.account"), sb.ToString());
    }

    public static async Task<PluginResult> OrdersAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        var (id, email) = await WhoAsync(ctx, store, ct);
        // Only the orders linked to this account. An order somebody placed as a guest with the same address joins the account when it is CONFIRMED (the host tells the
        // shop), never merely because the address matches — an unconfirmed address proves nothing.
        var orders = (await store.OrdersAsync(memberId: id, ct: ct)).Items;
        var sb = new StringBuilder();
        if (orders.Count == 0) sb.Append(EmptyOrders(ctx));
        else
        {
            sb.Append("<div class=\"shop-ocards\">");
            foreach (var o in orders) sb.Append(await OrderCardAsync(ctx, cfg, store, o, ct));
            sb.Append("</div>");
        }
        return await ShellAsync(ctx, cfg, "orders", ctx.T("shop.myOrders"), ctx.T("shop.myOrders"), sb.ToString());
    }

    public static async Task<PluginResult> AddressesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        var memberId = ctx.Member!.Id;
        string? error = null;
        if (ctx.Request.IsPost)
        {
            switch (ctx.Request.Action)
            {
                case "delete" when long.TryParse(ctx.Request.F("id"), out var del):
                    await store.DeleteAddressAsync(del, memberId, ct);
                    return PluginResult.Redirect("/konto/adressen");
                case "default" when long.TryParse(ctx.Request.F("id"), out var def) && await store.AddressAsync(def, memberId, ct) is { } a:
                    await store.SaveAddressAsync(a.Id, memberId, a.Label, a.Address, true, ct);
                    return PluginResult.Redirect("/konto/adressen");
                case "save":
                {
                    var f = ctx.Request;
                    long.TryParse(f.F("id"), out var id);
                    var addr = new Address { Name = f.F("name").Trim(), Line1 = f.F("line1").Trim(), Line2 = f.F("line2").Trim(), Zip = f.F("zip").Trim(), City = f.F("city").Trim(), Country = f.F("country").Trim().ToUpperInvariant(), Phone = f.F("phone").Trim() };
                    if (!addr.IsComplete || !Countries.Known(addr.Country)) { error = ctx.T("shop.err.address"); break; }
                    if (id > 0 && await store.AddressAsync(id, memberId, ct) is null) return PluginResult.Redirect("/konto/adressen");
                    var isFirst = (await store.AddressesAsync(memberId, ct)).Count == 0;
                    await store.SaveAddressAsync(id, memberId, f.F("label").Trim().Length > 0 ? f.F("label").Trim() : addr.Line1, addr, f.Form.ContainsKey("is_default") || isFirst, ct);
                    return PluginResult.Redirect("/konto/adressen");
                }
            }
        }

        var all = await store.AddressesAsync(memberId, ct);
        long.TryParse(ctx.Request.Q("edit"), out var editId);
        var editing = all.FirstOrDefault(a => a.Id == editId);
        var sb = new StringBuilder();
        if (error is not null) sb.Append(Alert(error, "error"));
        if (all.Count == 0) sb.Append($"<div class=\"shop-accempty\">{Icon("pin")}<p class=\"shop-muted\">{E(ctx.T("shop.acc.noAddresses"))}</p></div>");
        sb.Append("<div class=\"shop-addrs\">");
        foreach (var a in all)
        {
            sb.Append($"<div class=\"shop-addr{(a.IsDefault ? " default" : "")}\"><div class=\"head\"><strong>{E(a.Label)}</strong>{(a.IsDefault ? " <span class=\"shop-badge ok\">" + E(ctx.T("shop.acc.default")) + "</span>" : "")}</div><div class=\"shop-pre\">{E(a.Address.Lines())}</div><div class=\"shop-actions\">");
            sb.Append($"<a class=\"shop-btn ghost\" href=\"/konto/adressen?edit={Intl(a.Id)}\">{E(ctx.T("shop.acc.edit"))}</a>");
            if (!a.IsDefault) sb.Append($"<form method=\"post\" action=\"/konto/adressen\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"default\"><input type=\"hidden\" name=\"id\" value=\"{Intl(a.Id)}\"><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.acc.makeDefault"))}</button></form>");
            sb.Append($"<form method=\"post\" action=\"/konto/adressen\" data-confirm=\"{E(ctx.T("shop.acc.deleteAddressConfirm"))}\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"delete\"><input type=\"hidden\" name=\"id\" value=\"{Intl(a.Id)}\"><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.acc.delete"))}</button></form></div></div>");
        }
        sb.Append("</div>");

        var cur = editing?.Address ?? new Address { Country = "DE" };
        sb.Append($"<div class=\"shop-card-box shop-accform\"><h2>{E(ctx.T(editing is null ? "shop.acc.newAddress" : "shop.acc.editAddress"))}</h2><form method=\"post\" action=\"/konto/adressen\" class=\"shop-form\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"save\"><input type=\"hidden\" name=\"id\" value=\"{Intl(editing?.Id ?? 0)}\">");
        sb.Append($"<div><label>{E(ctx.T("shop.acc.label"))}</label><input type=\"text\" name=\"label\" value=\"{E(editing?.Label ?? "")}\"></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.fullName"))}</label><input type=\"text\" name=\"name\" value=\"{E(cur.Name)}\"></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.street"))}</label><input type=\"text\" name=\"line1\" value=\"{E(cur.Line1)}\"></div><div><label>{E(ctx.T("shop.street2"))}</label><input type=\"text\" name=\"line2\" value=\"{E(cur.Line2)}\"></div>");
        sb.Append($"<div class=\"shop-row shop-zipcity\"><div><label>{E(ctx.T("shop.zip"))}</label><input type=\"text\" name=\"zip\" value=\"{E(cur.Zip)}\"></div><div><label>{E(ctx.T("shop.city"))}</label><input type=\"text\" name=\"city\" value=\"{E(cur.City)}\"></div></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.country"))}</label>{Select("country", cur.Country, CountryOptions(ctx.Locale))}</div><div><label>{E(ctx.T("shop.phone"))}</label><input type=\"tel\" name=\"phone\" value=\"{E(cur.Phone)}\"></div>");
        sb.Append($"<label class=\"shop-check\"><input type=\"checkbox\" name=\"is_default\" value=\"1\"{(editing?.IsDefault == true ? " checked" : "")}> <span>{E(ctx.T("shop.acc.makeDefault"))}</span></label>");
        sb.Append($"<div class=\"shop-actions\"><button class=\"shop-btn\" type=\"submit\">{E(ctx.T("shop.acc.save"))}</button>{(editing is null ? "" : $"<a class=\"shop-btn ghost\" href=\"/konto/adressen\">{E(ctx.T("shop.acc.cancel"))}</a>")}</div></form></div>");
        return await ShellAsync(ctx, cfg, "addresses", ctx.T("shop.acc.addresses"), ctx.T("shop.acc.addresses"), sb.ToString());
    }

    public static async Task<PluginResult> ProfileAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        var member = ctx.Member!;
        string? ok = null, error = null;
        if (ctx.Request.IsPost)
        {
            if (ctx.Request.Action == "password")
            {
                var changed = await ctx.Members.ChangePasswordAsync(member.Id, ctx.Request.F("current"), ctx.Request.F("new"), ct);
                if (changed) ok = ctx.T("shop.acc.passwordChanged"); else error = ctx.T("shop.acc.passwordWrong", 8);
            }
            else if (ctx.Request.Action == "delete")
            {
                var account = await ctx.Members.FindAsync(member.Id, ct);
                // Typing the account's own address is the confirmation: deleting is not undone, and a stolen session should not be enough to do it by one click.
                if (account is null || !string.Equals(ctx.Request.F("confirm").Trim(), account.Email, StringComparison.OrdinalIgnoreCase) || account.Email.Length == 0) error = ctx.T("shop.acc.deleteWrong");
                else
                {
                    await ctx.Members.SignOutAsync(ct);
                    await ctx.Members.DeleteAsync(member.Id, ct);
                    return PluginResult.Redirect("/shop");
                }
            }
        }
        var sb = new StringBuilder();
        if (ok is not null) sb.Append(Alert(ok, "ok"));
        if (error is not null) sb.Append(Alert(error, "error"));
        sb.Append("<div class=\"shop-accgrid\">");
        sb.Append($"<div class=\"shop-card-box\"><h2>{Icon("user")}{E(ctx.T("shop.acc.account"))}</h2><strong>{E(member.DisplayName)}</strong><div class=\"shop-muted\">{E(member.Email)}</div></div>");
        sb.Append($"<div class=\"shop-card-box\"><h2>{E(ctx.T("shop.acc.yourData"))}</h2><p>{E(ctx.T("shop.acc.exportText"))}</p><a class=\"shop-btn ghost\" href=\"/konto/export\">{E(ctx.T("shop.acc.export"))}</a></div></div>");
        sb.Append($"<div class=\"shop-card-box shop-accform\"><h2>{E(ctx.T("shop.acc.changePassword"))}</h2><form method=\"post\" action=\"/konto/profil\" class=\"shop-form\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"password\">");
        sb.Append($"<div><label>{E(ctx.T("shop.acc.currentPassword"))}</label><input type=\"password\" name=\"current\" autocomplete=\"current-password\"></div><div><label>{E(ctx.T("shop.acc.newPassword"))}</label><input type=\"password\" name=\"new\" autocomplete=\"new-password\"></div>");
        sb.Append($"<div><button class=\"shop-btn\" type=\"submit\">{E(ctx.T("shop.acc.save"))}</button></div></form></div>");
        sb.Append($"<div class=\"shop-card-box shop-accform shop-danger\"><h2>{E(ctx.T("shop.acc.deleteAccount"))}</h2><p>{E(ctx.T("shop.acc.deleteText"))}</p><form method=\"post\" action=\"/konto/profil\" class=\"shop-form\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"delete\">");
        sb.Append($"<div><label>{E(ctx.T("shop.acc.deleteConfirm"))}</label><input type=\"email\" name=\"confirm\" autocomplete=\"off\"></div><div><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.acc.deleteAccount"))}</button></div></form></div>");
        return await ShellAsync(ctx, cfg, "profile", ctx.T("shop.acc.profile"), ctx.T("shop.acc.profile"), sb.ToString());
    }

    /// <summary>What the shop holds about a customer, as JSON — the data-protection export (<c>/konto/export</c> and the host's export of an account).</summary>
    public static async Task<JsonObject> ExportAsync(ShopStore store, long memberId, string email, CancellationToken ct = default)
    {
        var addresses = await store.AddressesAsync(memberId, ct);
        var orders = (await store.OrdersAsync(memberId: memberId, ct: ct)).Items;
        var o = new JsonObject
        {
            ["addresses"] = new JsonArray(addresses.Select(a => (JsonNode)new JsonObject
            {
                ["label"] = a.Label, ["name"] = a.Address.Name, ["line1"] = a.Address.Line1, ["line2"] = a.Address.Line2, ["zip"] = a.Address.Zip, ["city"] = a.Address.City,
                ["country"] = a.Address.Country, ["phone"] = a.Address.Phone, ["default"] = a.IsDefault
            }).ToArray())
        };
        var list = new JsonArray();
        foreach (var order in orders)
        {
            var items = await store.ItemsAsync(order.Id, ct);
            list.Add(new JsonObject
            {
                ["number"] = order.Number, ["date"] = order.CreatedUtc.ToString("o", CultureInfo.InvariantCulture), ["status"] = order.Status, ["total_cents"] = order.TotalCents, ["currency"] = order.Currency,
                ["email"] = order.Email, ["billing"] = order.Billing.Lines(), ["shipping"] = order.Shipping.Lines(), ["invoice"] = order.InvoiceNumber,
                ["coupon"] = order.CouponCode, ["discount_cents"] = order.DiscountCents,
                ["credit_notes"] = new JsonArray((await store.CreditNotesOfOrderAsync(order.Id, ct)).Select(c => (JsonNode)new JsonObject { ["number"] = c.Number, ["amount_cents"] = c.AmountCents, ["date"] = c.IssuedUtc.ToString("o", CultureInfo.InvariantCulture) }).ToArray()),
                ["items"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject { ["title"] = i.DisplayTitle, ["qty"] = i.Qty, ["line_cents"] = i.LineCents }).ToArray())
            });
        }
        o["orders"] = list;
        return o;
    }

    public static async Task<PluginResult> ExportDownloadAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var (id, email) = await WhoAsync(ctx, store, ct);
        var json = (await ExportAsync(store, id, email, ct)).ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return PluginResult.File(Encoding.UTF8.GetBytes(json), "application/json", "meine-daten.json").WithHeader("Cache-Control", "private, no-store");
    }
}
