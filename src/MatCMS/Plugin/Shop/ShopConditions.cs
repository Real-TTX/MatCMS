using System.Globalization;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The shop's "Konditionen" in the back office: payment methods, taxes, legal texts and number ranges — each a LIST with its status, and "edit" leading to the settings of exactly that group
/// (the sidebar's own block names the pages; shipping and coupons are lists of their own). Nothing here stores anything: the lists read the settings and the buttons lead to the form.
/// </summary>
internal static class ShopConditions
{
    private const string Section = ShopAdmin.Section;
    private static string Settings(string group) => $"{Section}/_settings?group={Uri.EscapeDataString(group)}";

    private static UiNode Edit(IPluginContext ctx, string group, string? label = null) => Ui.Button(label ?? ctx.T("action.edit"), Settings(group), UiButtonKind.Ghost, small: true);

    private static UiNode Name(string text) => Ui.Text(text) with { Inline = true, Strong = true };

    private static UiNode State(IPluginContext ctx, bool on, bool complete = true) =>
        on && complete ? Ui.Badge(ctx.T("shop.adm.active"), UiTone.Success) : on ? Ui.Badge(ctx.T("shop.cond.incomplete"), UiTone.Warning) : Ui.Badge(ctx.T("shop.cond.off"), UiTone.Muted);

    /// <summary>The areas of "Konditionen", in the order of the sub-menu: each a list of its own (shipping and coupons have their own editors, the rest overviews that lead to the settings).</summary>
    private static readonly (string Area, string Page, string Label, string Icon)[] Areas =
    {
        ("shipping", "shipping", "shop.tab.shipping", "ti-truck-delivery"), ("payment", "payment", "shop.tab.payment", "ti-credit-card"), ("coupons", "coupons", "shop.tab.coupons", "ti-ticket"),
        ("taxes", "taxes", "shop.tab.taxes", "ti-percentage"), ("legal", "legal", "shop.tab.legal", "ti-scale"), ("numbers", "numbers", "shop.tab.numbers", "ti-123")
    };

    /// <summary>A page of the conditions: the sub-menu at the left, the page at the right.</summary>
    public static UiNode Frame(IPluginContext ctx, string area, params UiNode?[] body) =>
        Ui.Split(Areas.Select(a => Ui.NavItem(ctx.T(a.Label), $"{Section}/{a.Page}", a.Icon, a.Area == area)), body);

    private static PluginResult Page(IPluginContext ctx, string area, string title, string help, UiNode? action, params UiNode[] body)
    {
        var card = new List<UiNode?> { Ui.Muted(help) };
        card.AddRange(body);
        return PluginResult.Page(Frame(ctx, area, Ui.PageHead(null, title, action), Ui.Card(card.ToArray())), title);
    }

    // ---- payment methods ---------------------------------------------------------------------------------

    public static async Task<PluginResult> PaymentAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        UiTableRow Row(string title, string mode, UiNode state, string note, string group) => Ui.Row(Name(title), mode, state, note, Edit(ctx, group));
        var table = Ui.Table(new[] { Ui.Column(ctx.T("shop.cond.method")), Ui.Column(ctx.T("shop.cond.mode")), Ui.Column(ctx.T("shop.adm.status")), Ui.Column(ctx.T("shop.cond.note")), Ui.Column("", actions: true) },
            new[]
            {
                Row(ctx.T("shop.cond.stripe"), cfg.StripeReady ? ctx.T(cfg.StripeTestMode ? "shop.cond.test" : "shop.cond.live") : "—", State(ctx, cfg.StripeEnabled, cfg.StripeReady),
                    cfg.StripeReady ? ctx.T(cfg.StripeWebhookSecret.Length == 0 ? "shop.cond.webhookMissing" : "shop.cond.webhookSet") : ctx.T("shop.cond.stripeKey"), "set.groupStripe"),
                Row(ctx.T("shop.cond.paypal"), cfg.PayPalReady ? ctx.T(cfg.PayPalSandbox ? "shop.cond.sandbox" : "shop.cond.live") : "—", State(ctx, cfg.PayPalEnabled, cfg.PayPalReady),
                    cfg.PayPalReady ? ctx.T(cfg.PayPalWebhookId.Length == 0 ? "shop.cond.webhookMissing" : "shop.cond.webhookSet") : ctx.T("shop.cond.paypalKey"), "set.groupPayPal"),
                Row(ctx.T("shop.cond.bank"), "—", State(ctx, cfg.BankEnabled), cfg.BankEnabled ? ctx.T("shop.cond.bankHold", cfg.BankHoldDays) : ctx.T("shop.cond.bankDetails"), "set.groupBank")
            }, emptyText: "—");
        // "Verbindung prüfen": asks the provider with the stored credentials (read-only) and shows what works. A real test payment stays the owner's to make — the checklist says how.
        UiNode? result = null;
        if (ctx.Request.IsPost && ctx.Request.Action == "check")
        {
            var method = ctx.Request.F("method");
            if (method == "stripe") result = ShopConnectionCheck.Render(ctx, ctx.T("shop.cond.stripe"), await ShopConnectionCheck.StripeAsync(ctx, cfg, ct));
            else if (method == "paypal") result = ShopConnectionCheck.Render(ctx, ctx.T("shop.cond.paypal"), await ShopConnectionCheck.PayPalAsync(ctx, cfg, ct));
        }
        UiNode Check(string method, string label) => Ui.Action(label, new Dictionary<string, string> { ["action"] = "check", ["method"] = method }, UiButtonKind.Ghost);
        var tools = Ui.Card(ctx.T("shop.chk.title"), Ui.Muted(ctx.T("shop.chk.help")),
            Ui.Inline(Check("stripe", ctx.T("shop.chk.checkStripe")), Check("paypal", ctx.T("shop.chk.checkPayPal"))),
            Ui.KeyValue((ctx.T("shop.chk.urlStripe"), ShopConnectionCheck.StripeWebhookUrl(ctx.Site.Origin)), (ctx.T("shop.chk.eventsStripe"), string.Join(", ", ShopConnectionCheck.StripeEvents)),
                (ctx.T("shop.chk.urlPayPal"), ShopConnectionCheck.PayPalWebhookUrl(ctx.Site.Origin)), (ctx.T("shop.chk.eventsPayPal"), string.Join(", ", ShopConnectionCheck.PayPalEvents))),
            Ui.Heading(ctx.T("shop.chk.listTitle"), 3), Ui.Text(ctx.T("shop.chk.list")) with { Muted = true });
        return result is null ? PluginResult.Page(Frame(ctx, "payment", Ui.PageHead(null, ctx.T("shop.tab.payment")), Ui.Card(Ui.Muted(ctx.T("shop.cond.paymentHelp")), table), tools), ctx.T("shop.tab.payment"))
            : PluginResult.Page(Frame(ctx, "payment", Ui.PageHead(null, ctx.T("shop.tab.payment")), result, Ui.Card(Ui.Muted(ctx.T("shop.cond.paymentHelp")), table), tools), ctx.T("shop.tab.payment"));
    }

    // ---- taxes -------------------------------------------------------------------------------------------

    public static Task<PluginResult> TaxesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var rows = ShopTax.Eu.OrderBy(kv => Countries.Name(kv.Key, ctx.Locale)).Select(kv =>
        {
            var own = cfg.TaxRates.TryGetValue(kv.Key, out var o);
            var r = own ? o : kv.Value;
            return Ui.RowFor($"{Countries.Name(kv.Key, ctx.Locale)} {kv.Key}", Ui.Text(Countries.Name(kv.Key, ctx.Locale)) with { Inline = true, Strong = kv.Key == cfg.HomeCountry }, kv.Key,
                Money.Percent(r.StandardBp) + " %", r.ReducedBp > 0 ? Money.Percent(r.ReducedBp) + " %" : "—",
                kv.Key == cfg.HomeCountry ? Ui.Badge(ctx.T("shop.cond.home"), UiTone.Info) : own ? Ui.Badge(ctx.T("shop.cond.ownRate"), UiTone.Warning) : Ui.Badge(ctx.T("shop.cond.builtIn"), UiTone.Muted));
        });
        var mode = cfg.SmallBusiness ? ctx.T("shop.cond.modeSmall") : cfg.Oss ? ctx.T("shop.cond.modeOss") : ctx.T("shop.cond.modeProduct");
        return Task.FromResult(Page(ctx, "taxes", ctx.T("shop.tab.taxes"), ctx.T(cfg.Oss ? "shop.cond.taxesHelpOss" : "shop.cond.taxesHelp"), Edit(ctx, "set.groupMoney", ctx.T("shop.cond.editTax")),
            Ui.KeyValue((ctx.T("shop.cond.taxMode"), mode), (ctx.T("shop.cond.defaultTax"), Money.Percent(cfg.DefaultTaxBp) + " %"), (ctx.T("shop.cond.shippingTax"), Money.Percent(cfg.ShippingTaxBp) + " %"), (ctx.T("shop.cond.currency"), cfg.Currency)),
            Ui.Table(new[] { Ui.Column(ctx.T("shop.country")), Ui.Column(ctx.T("shop.cond.code")), Ui.Column(ctx.T("shop.cond.standard"), right: true), Ui.Column(ctx.T("shop.cond.reduced"), right: true), Ui.Column(ctx.T("shop.cond.origin")) },
                rows, searchPlaceholder: ctx.T("action.search"), pageSize: 30, emptyText: "—")));
    }

    // ---- legal texts -------------------------------------------------------------------------------------

    public static Task<PluginResult> LegalAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        UiTableRow Row(string title, string url) => Ui.Row(Name(title), url.Length > 0 ? url : "—", url.Length > 0 ? Ui.Badge(ctx.T("shop.cond.set"), UiTone.Success) : Ui.Badge(ctx.T("shop.cond.missing"), UiTone.Warning), Edit(ctx, "set.groupLegal"));
        var table = Ui.Table(new[] { Ui.Column(ctx.T("shop.cond.text")), Ui.Column(ctx.T("shop.cond.address")), Ui.Column(ctx.T("shop.adm.status")), Ui.Column("", actions: true) },
            new[]
            {
                Row(ctx.T("shop.terms"), cfg.TermsUrl), Row(ctx.T("shop.withdrawal"), cfg.WithdrawalUrl), Row(ctx.T("shop.privacy"), cfg.PrivacyUrl),
                Ui.Row(Name(ctx.T("shop.cond.version")), cfg.TermsVersion, Ui.Badge(ctx.T("shop.cond.versionKept"), UiTone.Info), Edit(ctx, "set.groupLegal"))
            }, emptyText: "—");
        return Task.FromResult(Page(ctx, "legal", ctx.T("shop.tab.legal"), ctx.T("shop.cond.legalHelp"), null, table));
    }

    // ---- number ranges -----------------------------------------------------------------------------------

    public static async Task<PluginResult> NumbersAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        async Task<long> Current(string name) => await ctx.Data.Table("sequences").FindByAsync("name", name, ct) is { } r ? r.Int("value") : 0;
        long order = await Current("order"), invoice = await Current("invoice"), credit = await Current("credit");
        UiTableRow Row(string title, string prefix, string next) => Ui.Row(Name(title), prefix.Length > 0 ? prefix : "—", next, Edit(ctx, "set.groupNumbers"));
        var table = Ui.Table(new[] { Ui.Column(ctx.T("shop.cond.number")), Ui.Column(ctx.T("shop.cond.prefix")), Ui.Column(ctx.T("shop.cond.next")), Ui.Column("", actions: true) },
            new[]
            {
                Row(ctx.T("shop.cond.orderNo"), cfg.OrderPrefix, cfg.OrderPrefix + (order == 0 ? cfg.OrderStart : order + 1).ToString(CultureInfo.InvariantCulture)),
                Row(ctx.T("shop.invoice"), cfg.InvoicePrefix, ShopInvoice.InvoiceNumber(cfg, invoice + 1)),
                Row(ctx.T("shop.creditNote"), cfg.CreditPrefix, ShopInvoice.CreditNumber(cfg, credit + 1))
            }, emptyText: "—");
        return Page(ctx, "numbers", ctx.T("shop.tab.numbers"), ctx.T("shop.cond.numbersHelp"), null, table);
    }
}
