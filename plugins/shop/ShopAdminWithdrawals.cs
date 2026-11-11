using System.Globalization;
using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The back office's withdrawals (Shop → Widerrufe): every declaration as the customer confirmed it, linked to its order where it could be matched, and the way of
/// the goods back — received → return expected → goods back → refunded (or rejected / closed). The refund itself is the order's ("Erstatten" on the order page):
/// it writes the credit note and pays the money back through the provider, so it is never done twice from here.
/// </summary>
internal static class ShopAdminWithdrawals
{
    private const string Section = ShopAdmin.Section;
    private static string Url(long id) => $"{Section}/withdrawals/{id}";
    private static string Intl(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static UiTone Tone(string status) => status switch
    {
        ShopWithdrawals.Received => UiTone.Error,
        ShopWithdrawals.ReturnExpected or ShopWithdrawals.Returned => UiTone.Warning,
        ShopWithdrawals.Refunded or ShopWithdrawals.Closed => UiTone.Success,
        _ => UiTone.Muted
    };

    private static string When(DateTime utc) => ShopWithdrawals.Local(utc).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    public static async Task<PluginResult> ListAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        var all = await store.WithdrawalsAsync(ct);
        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("shop.wd.number")), Ui.Column(ctx.T("shop.wd.order")), Ui.Column(ctx.T("shop.wd.name")), Ui.Column(ctx.T("shop.wd.what")), Ui.Column(ctx.T("shop.adm.status")), Ui.Column("", actions: true) },
            all.Select(w => Ui.RowFor($"{w.Number} {w.OrderNumber} {w.Name} {w.Email} {w.What}",
                Ui.Html($"<a href=\"{Url(w.Id)}\"><strong>{E(w.Number)}</strong></a><div class=\"muted\">{E(When(w.CreatedUtc))}</div>"),
                w.OrderId > 0 ? Ui.Link(w.OrderNumber, $"{Section}/orders/{w.OrderId}") : Ui.Html($"{E(w.OrderNumber)} <span class=\"muted\">({E(ctx.T("shop.wd.unmatchedShort"))})</span>"),
                Ui.Html($"{E(w.Name)}<div class=\"muted\">{E(w.Email)}</div>"),
                Ui.Text(w.What.Length > 80 ? w.What[..80] + "…" : w.What) with { Inline = true },
                Ui.Badge(ctx.T("shop.wd.st." + w.Status), Tone(w.Status)),
                Ui.Button(ctx.T("action.edit"), Url(w.Id), UiButtonKind.Ghost, small: true)) with { Tags = w.Status }),
            searchPlaceholder: ctx.T("action.search"), pageSize: 50, emptyText: ctx.T("shop.wd.none")) with
        {
            Filter = Ui.ListFilter(ctx.T("shop.adm.filter"), new[] { ("", $"{ctx.T("shop.adm.all")} ({all.Count})") }
                .Concat(ShopWithdrawals.Statuses.Select(s => (s, $"{ctx.T("shop.wd.st." + s)} ({all.Count(w => w.Status == s)})"))).ToArray()),
            Below = new UiNode[] { Ui.Button(ctx.T("shop.wd.publicPage"), ShopWithdrawals.Path, UiButtonKind.Ghost, icon: "ti-external-link") with { NewTab = true } }
        };
        return PluginResult.Page(Ui.Stack(Ui.Muted(ctx.T("shop.wd.adminIntro")), table), ctx.T("shop.tab.withdrawals"));
    }

    private static string E(string? t) => System.Net.WebUtility.HtmlEncode(t ?? "");

    public static async Task<PluginResult> OneAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new ShopStore(ctx.Data);
        long.TryParse(ctx.Request.R("id"), out var id);
        if (await store.WithdrawalAsync(id, ct) is not { } w) return PluginResult.Redirect($"{Section}/withdrawals");
        if (ctx.Request.IsPost && ctx.Request.Action == "save")
        {
            var status = ShopWithdrawals.Statuses.Contains(ctx.Request.F("status")) ? ctx.Request.F("status") : w.Status;
            var note = ctx.Request.F("note").Trim();
            if (note.Length > 4000) note = note[..4000];
            await store.SetWithdrawalAsync(w.Id, status, note, ct);
            if (status != w.Status && w.OrderId > 0)
                await store.AddEventAsync(w.OrderId, "withdrawal", $"Widerruf {w.Number}: {ctx.T("shop.wd.st." + status)}", ctx.User.Name, null, ct);
            return PluginResult.Redirect(Url(w.Id), ctx.T("shop.adm.saved"));
        }

        var items = new List<string>();
        try { if (JsonNode.Parse(w.ItemsJson) is JsonArray arr) items.AddRange(arr.OfType<JsonObject>().Select(o => $"{o["qty"]} × {o["title"]}")); } catch { /* damaged */ }
        var declared = Ui.Card(ctx.T("shop.wd.declared"),
            Ui.KeyValue(
                (ctx.T("shop.wd.receivedAt"), When(w.CreatedUtc)), (ctx.T("shop.wd.name"), w.Name), (ctx.T("shop.wd.email"), w.Email),
                (ctx.T("shop.wd.order"), w.OrderNumber + (w.OrderId > 0 ? "" : " – " + ctx.T("shop.wd.unmatched"))),
                (ctx.T("shop.wd.what"), w.What), (ctx.T("shop.wd.message"), w.Message.Length > 0 ? w.Message : "–")),
            w.OrderId > 0 ? Ui.Button(ctx.T("shop.wd.toOrder"), $"{Section}/orders/{w.OrderId}", UiButtonKind.Primary, icon: "ti-receipt") : Ui.Muted(ctx.T("shop.wd.unmatchedHelp")));
        var handle = Ui.Card(ctx.T("shop.wd.handling"),
            Ui.Form(
                Ui.Hidden("action", "save"),
                Ui.Select("status", ctx.T("shop.adm.status"), w.Status, ShopWithdrawals.Statuses.Select(s => Ui.Option(s, ctx.T("shop.wd.st." + s))).ToArray()) with { Help = ctx.T("shop.wd.statusHelp") },
                Ui.TextArea("note", ctx.T("shop.wd.note"), w.Note, 4),
                Ui.FormActions(Ui.Submit(ctx.T("action.save")))));
        return PluginResult.Page(Ui.Columns(new UiNode?[] { declared }, new UiNode?[] { handle }), ctx.T("shop.wd.one", w.Number));
    }
}
