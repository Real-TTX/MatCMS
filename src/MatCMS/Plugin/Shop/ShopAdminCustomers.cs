using System.Globalization;
using System.Text;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>One customer in the back office: who they are, where they live (the addresses their orders used and the ones saved in their account), what they bought and what they are worth.
/// There is no customer table — a customer is the orders of one address (and of the account, when there is one) — so the page is derived from them, and its address in the URL is the
/// e-mail address in a route-safe form (<see cref="Key"/>), never a number that could point at somebody else.</summary>
internal static class ShopAdminCustomers
{
    private const string Section = "/admin/p/shop";

    /// <summary>The e-mail address as one URL segment: lower case, base64url.</summary>
    public static string Key(string email) => Convert.ToBase64String(Encoding.UTF8.GetBytes((email ?? "").Trim().ToLowerInvariant())).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The address behind a <see cref="Key"/>; "" for anything that is not one.</summary>
    public static string FromKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 400) return "";
        try
        {
            var b = key.Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            var s = Encoding.UTF8.GetString(Convert.FromBase64String(b));
            return s.Contains('@') && s.Length <= 300 ? s : "";
        }
        catch (FormatException) { return ""; }
    }

    public static string Url(string email) => $"{Section}/customers/{Key(email)}";

    public static async Task<PluginResult> CustomerAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var cfg = ShopConfig.From(ctx.Settings);
        var email = FromKey(ctx.Request.R("id"));
        if (email.Length == 0) return PluginResult.Redirect($"{Section}/customers");

        // The orders of this address (the search is a substring match, the exact test is made here, without case), plus — for an account — everything placed under it with another address.
        var orders = (await store.OrdersAsync(search: email, ct: ct)).Items.Where(o => string.Equals(o.Email, email, StringComparison.OrdinalIgnoreCase)).ToList();
        if (orders.Count == 0) return PluginResult.Redirect($"{Section}/customers");
        var memberId = orders.Select(o => o.MemberId).FirstOrDefault(m => m > 0);
        if (memberId > 0)
            foreach (var o in (await store.OrdersAsync(memberId: memberId, ct: ct)).Items)
                if (orders.All(x => x.Id != o.Id)) orders.Add(o);
        orders = orders.OrderByDescending(o => o.CreatedUtc).ThenByDescending(o => o.Id).ToList();
        var latest = orders[0];
        var account = memberId > 0 ? await ctx.Members.FindAsync(memberId, ct) : null;
        var saved = memberId > 0 ? await store.AddressesAsync(memberId, ct) : Array.Empty<ShopStore.SavedAddress>();

        string D(DateTime d) => d.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        string Mo(long c) => Money.Format(c, cfg.Currency, "de");
        string OneLine(Address a) => a.Lines().Replace("\r", "").Replace("\n", ", ");
        var paid = orders.Where(o => OrderStatus.IsPaid(o.Status)).ToList();
        var spent = paid.Sum(o => o.TotalCents - o.RefundedCents);
        var name = orders.Select(o => o.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? latest.Billing.Name;
        var phone = orders.Select(o => o.Phone).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";

        // The people-facing facts.
        var contact = new List<(string, string)>
        {
            (ctx.T("shop.adm.cust.name"), string.IsNullOrWhiteSpace(name) ? "—" : name),
            (ctx.T("shop.email"), email),
            (ctx.T("shop.adm.cust.phone"), phone.Length > 0 ? phone : "—"),
            (ctx.T("shop.adm.account"), account is null
                ? (memberId > 0 ? ctx.T("shop.adm.hasAccount") : ctx.T("shop.adm.guest"))
                : $"{ctx.T("shop.adm.hasAccount")} · {D(account.CreatedUtc)}{(account.EmailConfirmed ? "" : " · " + ctx.T("shop.adm.cust.unconfirmed"))}{(account.IsActive ? "" : " · " + ctx.T("shop.adm.cust.inactive"))}")
        };
        var addresses = new List<(string, string)>
        {
            (ctx.T("shop.billingAddress"), latest.Billing.IsComplete ? OneLine(latest.Billing) : "—"),
            (ctx.T("shop.shippingAddress"), latest.Shipping.IsComplete ? OneLine(latest.Shipping) : ctx.T("shop.adm.cust.sameAsBilling"))
        };
        // Every other address the orders went to or were billed to — an address a customer used once is still where a parcel went.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OneLine(latest.Billing), OneLine(latest.Shipping) };
        foreach (var o in orders.Skip(1))
            foreach (var a in new[] { o.Billing, o.Shipping })
                if (a.IsComplete && seen.Add(OneLine(a))) addresses.Add((ctx.T("shop.adm.cust.earlier") + " · " + o.Number, OneLine(a)));
        var savedRows = saved.Select(a => (a.Label + (a.IsDefault ? " · " + ctx.T("shop.acc.default") : ""), OneLine(a.Address))).ToList();

        var overview = Ui.KeyValue(
            (ctx.T("shop.adm.orderCount"), orders.Count.ToString(CultureInfo.InvariantCulture)),
            (ctx.T("shop.adm.spent"), Mo(spent)),
            (ctx.T("shop.adm.cust.average"), paid.Count > 0 ? Mo(spent / paid.Count) : "—"),
            (ctx.T("shop.adm.cust.first"), D(orders[^1].CreatedUtc)),
            (ctx.T("shop.adm.lastOrder"), D(latest.CreatedUtc)));

        var table = Ui.Table(
            new[] { Ui.Column("", actions: true), Ui.Column(ctx.T("shop.adm.number")), Ui.Column(ctx.T("shop.adm.date")), Ui.Column(ctx.T("shop.total"), right: true), Ui.Column(ctx.T("shop.adm.status")), Ui.Column("", actions: true) },
            orders.Select(o => Ui.RowFor($"{o.Number} {o.InvoiceNumber}",
                Ui.Html(ShopAdmin.Light(ctx, o.Status)),
                Ui.Link(o.Number, $"{Section}/orders/{o.Id}"),
                D(o.CreatedUtc), Money.Format(o.TotalCents, o.Currency, "de"), ShopAdmin.StatusBadge(ctx, o.Status),
                Ui.Button(ctx.T("shop.adm.open"), $"{Section}/orders/{o.Id}", UiButtonKind.Ghost, small: true))),
            searchPlaceholder: ctx.T("action.search"), pageSize: 15, emptyText: ctx.T("shop.adm.noOrders"));

        var head = new List<UiNode?>
        {
            Ui.Button(ctx.T("shop.adm.back"), $"{Section}/customers", UiButtonKind.Ghost, icon: "ti-arrow-left", small: true),
            Ui.Button(ctx.T("shop.adm.cust.mail"), "mailto:" + email, UiButtonKind.Ghost, icon: "ti-mail", small: true),
            Ui.Button(ctx.T("shop.adm.cust.allOrders"), $"{Section}/orders?q={Uri.EscapeDataString(email)}", UiButtonKind.Ghost, icon: "ti-receipt", small: true)
        };
        var side = new List<UiNode?> { Ui.Card(ctx.T("shop.adm.cust.contact"), Ui.KeyValue(contact.ToArray())), Ui.Card(ctx.T("shop.adm.cust.addresses"), Ui.KeyValue(addresses.ToArray())) };
        if (savedRows.Count > 0) side.Add(Ui.Card(ctx.T("shop.adm.cust.saved"), Ui.KeyValue(savedRows.ToArray())));

        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, string.IsNullOrWhiteSpace(name) ? email : name, head.ToArray()),
            Ui.Columns(new UiNode?[] { Ui.Card(ctx.T("shop.adm.cust.overview"), overview), Ui.Card(ctx.T("shop.adm.orders"), table) }, side)),
            string.IsNullOrWhiteSpace(name) ? email : name);
    }
}
