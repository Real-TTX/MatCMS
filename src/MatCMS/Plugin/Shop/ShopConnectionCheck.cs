using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

internal enum CheckState { Ok, Warn, Fail }
internal sealed record CheckLine(CheckState State, string Label, string Detail);

/// <summary>
/// "Verbindung prüfen" for the payment providers: asks the provider with the stored credentials and reports, step by step, what works and what does not — the key is accepted, test or live mode matches the
/// setting, the webhook is set up with the address and events the shop needs. It places no order and moves no money: a real test payment with the provider's test cards is still the owner's to make
/// (the page says so). Read-only calls only.
/// </summary>
internal static class ShopConnectionCheck
{
    public static readonly string[] StripeEvents = { "checkout.session.completed", "checkout.session.expired", "charge.refunded" };
    public static readonly string[] PayPalEvents = { "CHECKOUT.ORDER.APPROVED", "PAYMENT.CAPTURE.COMPLETED", "PAYMENT.CAPTURE.DENIED", "PAYMENT.CAPTURE.REFUNDED", "PAYMENT.CAPTURE.REVERSED" };

    public static string StripeWebhookUrl(string origin) => origin.TrimEnd('/') + "/shop/webhook/stripe";
    public static string PayPalWebhookUrl(string origin) => origin.TrimEnd('/') + "/shop/webhook/paypal";

    private static CheckLine Ok(string label, string detail = "") => new(CheckState.Ok, label, detail);
    private static CheckLine Warn(string label, string detail) => new(CheckState.Warn, label, detail);
    private static CheckLine Fail(string label, string detail) => new(CheckState.Fail, label, detail);

    /// <summary>True when an endpoint list entry subscribes to every event of <paramref name="needed"/> (or to all of them with "*").</summary>
    internal static bool Covers(IEnumerable<string> subscribed, IEnumerable<string> needed)
    {
        var have = subscribed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return have.Contains("*") || needed.All(have.Contains);
    }

    internal static bool SameUrl(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    public static async Task<IReadOnlyList<CheckLine>> StripeAsync(IPluginContext ctx, ShopConfig cfg, CancellationToken ct)
    {
        var lines = new List<CheckLine>();
        string T(string key, params object[] a) => ctx.T(key, a);
        if (cfg.StripeSecret.Length == 0) return new[] { Fail(T("shop.chk.key"), T("shop.chk.keyMissing")) };
        if (!cfg.StripeSecret.StartsWith("sk_", StringComparison.Ordinal) && !cfg.StripeSecret.StartsWith("rk_", StringComparison.Ordinal))
            return new[] { Fail(T("shop.chk.key"), T("shop.chk.stripeKeyShape")) };
        lines.Add(Ok(T("shop.chk.key"), T(cfg.StripeTestMode ? "shop.chk.modeTest" : "shop.chk.modeLive")));

        var client = new StripeClient(cfg.StripeSecret);
        bool? live = null;
        try
        {
            var balance = await client.GetAsync("/v1/balance", ct);
            live = balance["livemode"]?.GetValue<bool>();
            lines.Add(Ok(T("shop.chk.reach"), T("shop.chk.accepted")));
            if (live is { } l && l == cfg.StripeTestMode) lines.Add(Fail(T("shop.chk.mode"), T("shop.chk.modeMismatch")));
            else lines.Add(Ok(T("shop.chk.mode"), T(cfg.StripeTestMode ? "shop.chk.modeTest" : "shop.chk.modeLive")));
        }
        catch (StripeException ex) when (ex.Status == 401) { return lines.Append(Fail(T("shop.chk.reach"), T("shop.chk.rejected", ex.Message))).ToList(); }
        catch (StripeException ex) when (ex.Status == 403) { lines.Add(Warn(T("shop.chk.reach"), T("shop.chk.restricted"))); }
        catch (StripeException ex) { return lines.Append(Fail(T("shop.chk.reach"), ex.Message)).ToList(); }

        if (cfg.StripeWebhookSecret.Length == 0) lines.Add(Fail(T("shop.chk.secret"), T("shop.chk.secretMissing")));
        else if (!cfg.StripeWebhookSecret.StartsWith("whsec_", StringComparison.Ordinal)) lines.Add(Warn(T("shop.chk.secret"), T("shop.chk.secretShape")));
        else lines.Add(Ok(T("shop.chk.secret"), T("shop.chk.set")));

        var url = StripeWebhookUrl(ctx.Site.Origin);
        try
        {
            var list = await client.GetAsync("/v1/webhook_endpoints?limit=100", ct);
            var mine = (list["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(e => SameUrl(e["url"]?.GetValue<string>() ?? "", url));
            if (mine is null) lines.Add(Warn(T("shop.chk.hook"), T("shop.chk.hookNone", url)));
            else
            {
                var events = (mine["enabled_events"] as JsonArray ?? new JsonArray()).Select(e => e?.GetValue<string>() ?? "").ToList();
                if (mine["status"]?.GetValue<string>() is { } st && st != "enabled") lines.Add(Fail(T("shop.chk.hook"), T("shop.chk.hookDisabled")));
                else if (!Covers(events, StripeEvents)) lines.Add(Warn(T("shop.chk.hook"), T("shop.chk.hookEvents", string.Join(", ", StripeEvents))));
                else lines.Add(Ok(T("shop.chk.hook"), url));
            }
        }
        catch (StripeException ex) when (ex.Status is 401 or 403) { lines.Add(Warn(T("shop.chk.hook"), T("shop.chk.hookUnreadable", url))); }
        catch (StripeException ex) { lines.Add(Warn(T("shop.chk.hook"), ex.Message)); }
        return lines;
    }

    public static async Task<IReadOnlyList<CheckLine>> PayPalAsync(IPluginContext ctx, ShopConfig cfg, CancellationToken ct)
    {
        var lines = new List<CheckLine>();
        string T(string key, params object[] a) => ctx.T(key, a);
        if (cfg.PayPalClientId.Length == 0 || cfg.PayPalSecret.Length == 0) return new[] { Fail(T("shop.chk.key"), T("shop.chk.paypalMissing")) };
        lines.Add(Ok(T("shop.chk.key"), T(cfg.PayPalSandbox ? "shop.chk.modeSandbox" : "shop.chk.modeLive")));

        var client = new PayPalClient(cfg.PayPalClientId, cfg.PayPalSecret, cfg.PayPalSandbox);
        PayPalClient.ForgetTokens();                       // a check must ask PayPal, not remember a token from before the key was changed
        var url = PayPalWebhookUrl(ctx.Site.Origin);
        try
        {
            var list = await client.GetAsync("/v1/notifications/webhooks", ct);
            lines.Add(Ok(T("shop.chk.reach"), T("shop.chk.accepted")));
            var hooks = (list["webhooks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            if (cfg.PayPalWebhookId.Length == 0) lines.Add(Fail(T("shop.chk.hookId"), T("shop.chk.hookIdMissing")));
            else if (hooks.FirstOrDefault(h => h["id"]?.GetValue<string>() == cfg.PayPalWebhookId) is not { } mine) lines.Add(Fail(T("shop.chk.hookId"), T("shop.chk.hookIdUnknown")));
            else
            {
                var events = (mine["event_types"] as JsonArray ?? new JsonArray()).Select(e => e?["name"]?.GetValue<string>() ?? "").ToList();
                if (!SameUrl(mine["url"]?.GetValue<string>() ?? "", url)) lines.Add(Warn(T("shop.chk.hook"), T("shop.chk.hookOtherUrl", mine["url"]?.GetValue<string>() ?? "", url)));
                else if (!Covers(events, PayPalEvents)) lines.Add(Warn(T("shop.chk.hook"), T("shop.chk.hookEvents", string.Join(", ", PayPalEvents))));
                else lines.Add(Ok(T("shop.chk.hook"), url));
            }
        }
        catch (PayPalException ex) when (ex.Status == 401) { lines.Add(Fail(T("shop.chk.reach"), T("shop.chk.rejected", ex.Message))); }
        catch (PayPalException ex) when (ex.Status == 403)
        {
            lines.Add(Warn(T("shop.chk.reach"), T("shop.chk.restricted")));
            lines.Add(cfg.PayPalWebhookId.Length == 0 ? Fail(T("shop.chk.hookId"), T("shop.chk.hookIdMissing")) : Warn(T("shop.chk.hook"), T("shop.chk.hookUnreadable", url)));
        }
        catch (PayPalException ex) { lines.Add(Fail(T("shop.chk.reach"), ex.Message)); }
        return lines;
    }

    public static UiNode Render(IPluginContext ctx, string title, IReadOnlyList<CheckLine> lines)
    {
        var worst = lines.Any(l => l.State == CheckState.Fail) ? UiTone.Error : lines.Any(l => l.State == CheckState.Warn) ? UiTone.Warning : UiTone.Success;
        return Ui.Card(title,
            Ui.Alert(ctx.T(worst == UiTone.Error ? "shop.chk.bad" : worst == UiTone.Warning ? "shop.chk.mostly" : "shop.chk.good"), worst),
            Ui.Table(new[] { Ui.Column(ctx.T("shop.chk.step")), Ui.Column(ctx.T("shop.adm.status")), Ui.Column(ctx.T("shop.cond.note")) },
                lines.Select(l => Ui.Row(Ui.Text(l.Label) with { Inline = true, Strong = true },
                    Ui.Badge(ctx.T(l.State == CheckState.Ok ? "shop.chk.ok" : l.State == CheckState.Warn ? "shop.chk.warn" : "shop.chk.fail"), l.State == CheckState.Ok ? UiTone.Success : l.State == CheckState.Warn ? UiTone.Warning : UiTone.Error),
                    l.Detail))),
            Ui.Muted(ctx.T("shop.chk.noMoney")));
    }
}
