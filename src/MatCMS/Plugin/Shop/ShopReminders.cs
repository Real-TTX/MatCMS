using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The reminder for an abandoned cart. Opt-in only (a customer asks for it on the cart page, with their address, and can switch it off again), off in the shop's settings until the operator turns it on,
/// ONE mail per cart and ask, only for a cart that was left alone for the set time and is not older than two weeks, never when the same address has ordered since. The mail carries a link that puts the
/// cart back in the browser it is opened in; that link is the only thing that reveals the cart, and its key is long and random.
/// </summary>
internal static class ShopReminders
{
    public const string MailKey = "shop.cart.reminder";
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    public const int PerRun = 25;

    /// <summary>Takes the place of the real mail send (tests only): to, values, items.</summary>
    internal static Func<string, IReadOnlyDictionary<string, string>, IReadOnlyList<IReadOnlyDictionary<string, string>>, Task<MailResult>>? MailSender { get; set; }

    private static readonly MailLoop ItemsLoop = new("items", "Einmal je Artikel im Warenkorb",
        new MailPlaceholder[] { new("title", "Name des Artikels (mit Variante)"), new("qty", "Menge"), new("line", "Zeilensumme") });

    public static MailTemplateSpec Spec { get; } = new(MailKey, "Shop: Erinnerung an den Warenkorb", "Geht einmal an Kunden, die sich im Warenkorb eine Erinnerung gewünscht haben und nicht bestellt haben.",
        "Sie haben etwas im Warenkorb vergessen – {{site_name}}",
        """
        Hallo,

        Sie haben sich im Warenkorb von {{site_name}} eine Erinnerung gewünscht. Das liegt noch darin:

        {{#items}}{{qty}} × {{title}}  –  {{line}}
        {{/items}}
        Warenwert: {{total}}

        Hier geht es mit Ihrem Warenkorb weiter:
        {{restore_url}}

        Das ist die einzige Erinnerung; wir schreiben Ihnen deswegen nicht noch einmal.

        Mit freundlichen Grüßen
        {{site_name}}
        """)
    {
        Placeholders = new MailPlaceholder[] { new("site_name", "Name der Website"), new("total", "Warenwert des Warenkorbs"), new("restore_url", "Link, der den Warenkorb wieder öffnet") },
        Loops = new[] { ItemsLoop }
    };

    /// <summary>The address a reminder may go to: one plausible address, no line breaks, not absurdly long.</summary>
    public static bool ValidEmail(string? email) =>
        email is { Length: >= 5 and <= 200 } e && !e.Any(char.IsWhiteSpace) && e.Count(c => c == '@') == 1 && e.IndexOf('@') is > 0 and var at && e.IndexOf('.', at) > at + 1 && !e.EndsWith('.') && !e.Contains(',') && !e.Contains(';');

    /// <summary>When the reminder is due for a cart last touched at <paramref name="updatedUtc"/>.</summary>
    public static DateTime DueAt(DateTime updatedUtc, ShopConfig cfg) => updatedUtc.AddHours(cfg.RemindHours);

    /// <summary>The job's step: send the reminders that are due. Returns how many mails went out.</summary>
    public static async Task<int> RunAsync(IPluginContext ctx, ShopConfig cfg, ShopStore store, DateTime nowUtc, CancellationToken ct)
    {
        if (!cfg.RemindEnabled) return 0;
        var sent = 0;
        foreach (var cart in await store.CartsDueForReminderAsync(nowUtc.AddHours(-cfg.RemindHours), nowUtc - MaxAge, PerRun, ct))
        {
            // Whatever happens next, this cart is not asked about again (a failing mail server must not make a retry storm of it): mark first.
            await store.MarkCartRemindedAsync(cart.Id, ct);
            if (await store.HasOrderSinceAsync(cart.Email, cart.UpdatedUtc, ct)) continue;
            var priced = await ShopOrders.PriceAsync(store, cart.Lines, cfg, ctx, ct);
            if (priced.Lines.Count == 0) continue;
            string M(long c) => Money.Format(c, cfg.Currency, ctx.Locale);
            var values = new Dictionary<string, string>
            {
                ["site_name"] = ctx.Site.Name, ["total"] = M(priced.SubtotalCents), ["restore_url"] = $"{ctx.Site.Origin}/shop/cart?restore={cart.RestoreKey}"
            };
            var items = priced.Lines.Select(l => (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["title"] = l.DisplayTitle, ["qty"] = l.Qty.ToString(CultureInfo.InvariantCulture), ["line"] = M(l.LineCents) }).ToList();
            var result = MailSender is { } fake ? await fake(cart.Email, values, items) : await ctx.Mail.SendAsync(MailKey, new[] { cart.Email }, values, new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>> { ["items"] = items }, ct: ct);
            if (result.Ok) sent++; else ctx.Log($"Warenkorb-Erinnerung an {cart.Email}: {result.Error}");
        }
        return sent;
    }

    /// <summary>The box on the cart page: ask for the reminder, or — once asked — say so and offer to switch it off. "" when the operator has not turned reminders on.</summary>
    public static string BoxHtml(IPluginContext ctx, ShopConfig cfg, ShopStore.CartRow? cart)
    {
        if (!cfg.RemindEnabled || cart is null) return "";
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder("<form method=\"post\" action=\"/shop/cart\" class=\"shop-remind\">").Append(ctx.Antiforgery);
        if (cart.Remind && cart.Email.Length > 0)
            sb.Append($"<input type=\"hidden\" name=\"action\" value=\"remind-off\"><p class=\"shop-small\"><span class=\"shop-badge ok\">{E(ctx.T("shop.rem.on"))}</span> {E(ctx.T("shop.rem.onText", cart.Email))}</p><button class=\"shop-linkbtn\" type=\"submit\">{E(ctx.T("shop.rem.off"))}</button>");
        else
        {
            var email = cart.Email.Length > 0 ? cart.Email : ctx.Member?.Email ?? "";
            sb.Append($"<input type=\"hidden\" name=\"action\" value=\"remind\"><label for=\"shop-remind-mail\" class=\"shop-small\"><strong>{E(ctx.T("shop.rem.title"))}</strong></label>")
              .Append($"<div class=\"shop-remind-row\"><input id=\"shop-remind-mail\" type=\"email\" name=\"email\" value=\"{E(email)}\" maxlength=\"200\" autocomplete=\"email\" placeholder=\"{E(ctx.T("shop.rem.placeholder"))}\" required><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.rem.button"))}</button></div>")
              .Append($"<p class=\"shop-small shop-muted\">{E(ctx.T("shop.rem.privacy", cfg.RemindHours))}</p>");
        }
        return sb.Append("</form>").ToString();
    }

    /// <summary>The cart a restore link opens, or null. The key is hex of 24 random bytes; anything else is not even looked up.</summary>
    public static async Task<ShopStore.CartRow?> FindForRestoreAsync(ShopStore store, string key, CancellationToken ct) =>
        key.Length == 48 && key.All(Uri.IsHexDigit) ? await store.CartByRestoreKeyAsync(key.ToLowerInvariant(), ct) : null;
}
