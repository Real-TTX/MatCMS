using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using static MatCMS.Plugin.Shop.ShopHtml;

namespace MatCMS.Plugin.Shop;

/// <summary>The customer's own pages (<c>/konto</c>): orders, addresses, password, deleting the account, and the export of their data. All of it behind
/// <see cref="PublicRouteOptions.RequireMember"/>, so <c>ctx.Member</c> is never null here, and with antiforgery on every form.</summary>
internal static class ShopAccount
{
    private static string Csrf(IPluginContext ctx) => ctx.Antiforgery;

    private static string Menu(IPluginContext ctx, string current)
    {
        string Link(string key, string href, string label) => $"<a href=\"{href}\"{(key == current ? " style=\"font-weight:700\"" : "")}>{E(label)}</a>";
        return "<div class=\"shop-actions\" style=\"margin-bottom:20px\">" + Link("home", "/konto", ctx.T("shop.acc.overview")) + Link("orders", "/konto/bestellungen", ctx.T("shop.myOrders"))
               + Link("addresses", "/konto/adressen", ctx.T("shop.acc.addresses")) + Link("profile", "/konto/profil", ctx.T("shop.acc.profile")) + "</div>";
    }

    private static async Task<(long Id, string Email)> WhoAsync(IPluginContext ctx, ShopStore store, CancellationToken ct)
    {
        // The account as it is NOW (an address changed by the administrator is not the one in a cookie).
        var account = await ctx.Members.FindAsync(ctx.Member!.Id, ct);
        return (ctx.Member.Id, account?.Email ?? ctx.Member.Email);
    }

    public static async Task<PluginResult> HomeAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        var (id, email) = await WhoAsync(ctx, store, ct);
        var orders = (await store.OrdersAsync(memberId: id, email: null, take: 5, ct: ct)).Items;
        var sb = new StringBuilder($"<h1>{E(ctx.T("shop.acc.hello", ctx.Member!.DisplayName))}</h1>").Append(Menu(ctx, "home"));
        sb.Append($"<h2>{E(ctx.T("shop.acc.recent"))}</h2>");
        sb.Append(orders.Count == 0 ? $"<p class=\"shop-muted\">{E(ctx.T("shop.acc.noOrders"))}</p> <a class=\"shop-btn\" href=\"/shop\">{E(ctx.T("shop.continue"))}</a>" : OrdersTable(ctx, orders));
        sb.Append($"<form method=\"post\" action=\"/anmelden?handler=Logout\" style=\"margin-top:24px\">{Csrf(ctx)}<input type=\"hidden\" name=\"returnUrl\" value=\"/shop\"><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.acc.signOut"))}</button></form>");
        return await PageAsync(ctx, cfg, ctx.T("shop.nav.account"), sb.ToString(), noIndex: true);
    }

    private static string OrdersTable(IPluginContext ctx, IReadOnlyList<Order> orders)
    {
        var sb = new StringBuilder($"<table><thead><tr><th>{E(ctx.T("shop.acc.order"))}</th><th>{E(ctx.T("shop.acc.date"))}</th><th>{E(ctx.T("shop.acc.status"))}</th><th class=\"r\">{E(ctx.T("shop.total"))}</th></tr></thead><tbody>");
        foreach (var o in orders)
            sb.Append($"<tr><td><a href=\"/shop/order/{E(Uri.EscapeDataString(o.Number))}\">{E(o.Number)}</a></td><td>{o.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}</td><td>{StatusBadge(ctx, o.Status)}</td><td class=\"r\">{E(Money.Format(o.TotalCents, o.Currency, ctx.Locale))}</td></tr>");
        return sb.Append("</tbody></table>").ToString();
    }

    public static async Task<PluginResult> OrdersAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = new ShopStore(ctx.Data);
        var (id, email) = await WhoAsync(ctx, store, ct);
        // Only the orders linked to this account. An order somebody placed as a guest with the same address joins the account when it is CONFIRMED (the host tells the
        // shop), never merely because the address matches — an unconfirmed address proves nothing.
        var orders = (await store.OrdersAsync(memberId: id, ct: ct)).Items;
        var sb = new StringBuilder($"<h1>{E(ctx.T("shop.myOrders"))}</h1>").Append(Menu(ctx, "orders"));
        sb.Append(orders.Count == 0 ? $"<p class=\"shop-muted\">{E(ctx.T("shop.acc.noOrders"))}</p>" : OrdersTable(ctx, orders));
        return await PageAsync(ctx, cfg, ctx.T("shop.myOrders"), sb.ToString(), noIndex: true);
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
        var sb = new StringBuilder($"<h1>{E(ctx.T("shop.acc.addresses"))}</h1>").Append(Menu(ctx, "addresses"));
        if (error is not null) sb.Append(Alert(error, "error"));
        if (all.Count == 0) sb.Append($"<p class=\"shop-muted\">{E(ctx.T("shop.acc.noAddresses"))}</p>");
        sb.Append("<div class=\"shop-grid\">");
        foreach (var a in all)
        {
            sb.Append($"<div class=\"shop-box\"><strong>{E(a.Label)}</strong>{(a.IsDefault ? " <span class=\"shop-badge ok\">" + E(ctx.T("shop.acc.default")) + "</span>" : "")}<div class=\"shop-pre\" style=\"margin:8px 0\">{E(a.Address.Lines())}</div><div class=\"shop-actions\">");
            sb.Append($"<a class=\"shop-btn ghost\" href=\"/konto/adressen?edit={Intl(a.Id)}\">{E(ctx.T("shop.acc.edit"))}</a>");
            if (!a.IsDefault) sb.Append($"<form method=\"post\" action=\"/konto/adressen\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"default\"><input type=\"hidden\" name=\"id\" value=\"{Intl(a.Id)}\"><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.acc.makeDefault"))}</button></form>");
            sb.Append($"<form method=\"post\" action=\"/konto/adressen\" data-confirm=\"{E(ctx.T("shop.acc.deleteAddressConfirm"))}\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"delete\"><input type=\"hidden\" name=\"id\" value=\"{Intl(a.Id)}\"><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.acc.delete"))}</button></form></div></div>");
        }
        sb.Append("</div>");

        var cur = editing?.Address ?? new Address { Country = "DE" };
        sb.Append($"<h2>{E(ctx.T(editing is null ? "shop.acc.newAddress" : "shop.acc.editAddress"))}</h2><form method=\"post\" action=\"/konto/adressen\" class=\"shop-form\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"save\"><input type=\"hidden\" name=\"id\" value=\"{Intl(editing?.Id ?? 0)}\">");
        sb.Append($"<div><label>{E(ctx.T("shop.acc.label"))}</label><input type=\"text\" name=\"label\" value=\"{E(editing?.Label ?? "")}\"></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.fullName"))}</label><input type=\"text\" name=\"name\" value=\"{E(cur.Name)}\"></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.street"))}</label><input type=\"text\" name=\"line1\" value=\"{E(cur.Line1)}\"></div><div><label>{E(ctx.T("shop.street2"))}</label><input type=\"text\" name=\"line2\" value=\"{E(cur.Line2)}\"></div>");
        sb.Append($"<div class=\"shop-row\"><div><label>{E(ctx.T("shop.zip"))}</label><input type=\"text\" name=\"zip\" value=\"{E(cur.Zip)}\"></div><div><label>{E(ctx.T("shop.city"))}</label><input type=\"text\" name=\"city\" value=\"{E(cur.City)}\"></div></div>");
        sb.Append($"<div><label>{E(ctx.T("shop.country"))}</label>{Select("country", cur.Country, CountryOptions(ctx.Locale))}</div><div><label>{E(ctx.T("shop.phone"))}</label><input type=\"tel\" name=\"phone\" value=\"{E(cur.Phone)}\"></div>");
        sb.Append($"<label class=\"shop-check\"><input type=\"checkbox\" name=\"is_default\" value=\"1\"{(editing?.IsDefault == true ? " checked" : "")}> <span>{E(ctx.T("shop.acc.makeDefault"))}</span></label>");
        sb.Append($"<div class=\"shop-actions\"><button class=\"shop-btn\" type=\"submit\">{E(ctx.T("shop.acc.save"))}</button>{(editing is null ? "" : $"<a class=\"shop-btn ghost\" href=\"/konto/adressen\">{E(ctx.T("shop.acc.cancel"))}</a>")}</div></form>");
        return await PageAsync(ctx, cfg, ctx.T("shop.acc.addresses"), sb.ToString(), noIndex: true);
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
        var sb = new StringBuilder($"<h1>{E(ctx.T("shop.acc.profile"))}</h1>").Append(Menu(ctx, "profile"));
        if (ok is not null) sb.Append(Alert(ok, "ok"));
        if (error is not null) sb.Append(Alert(error, "error"));
        sb.Append($"<div class=\"shop-box\"><strong>{E(member.DisplayName)}</strong><div class=\"shop-muted\">{E(member.Email)}</div></div>");
        sb.Append($"<h2>{E(ctx.T("shop.acc.changePassword"))}</h2><form method=\"post\" action=\"/konto/profil\" class=\"shop-form\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"password\">");
        sb.Append($"<div><label>{E(ctx.T("shop.acc.currentPassword"))}</label><input type=\"password\" name=\"current\" autocomplete=\"current-password\"></div><div><label>{E(ctx.T("shop.acc.newPassword"))}</label><input type=\"password\" name=\"new\" autocomplete=\"new-password\"></div>");
        sb.Append($"<div><button class=\"shop-btn\" type=\"submit\">{E(ctx.T("shop.acc.save"))}</button></div></form>");
        sb.Append($"<h2>{E(ctx.T("shop.acc.yourData"))}</h2><p>{E(ctx.T("shop.acc.exportText"))}</p><p><a class=\"shop-btn ghost\" href=\"/konto/export\">{E(ctx.T("shop.acc.export"))}</a></p>");
        sb.Append($"<h2>{E(ctx.T("shop.acc.deleteAccount"))}</h2><p>{E(ctx.T("shop.acc.deleteText"))}</p><form method=\"post\" action=\"/konto/profil\" class=\"shop-form\">{Csrf(ctx)}<input type=\"hidden\" name=\"action\" value=\"delete\">");
        sb.Append($"<div><label>{E(ctx.T("shop.acc.deleteConfirm"))}</label><input type=\"email\" name=\"confirm\" autocomplete=\"off\"></div><div><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.acc.deleteAccount"))}</button></div></form>");
        return await PageAsync(ctx, cfg, ctx.T("shop.acc.profile"), sb.ToString(), noIndex: true);
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
