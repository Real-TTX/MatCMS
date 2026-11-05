using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Plugin;
using static MatCMS.Plugin.Shop.ShopHtml;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// "Vertrag widerrufen" — the withdrawal function a shop must offer since 19 June 2026 (§ 356a BGB, Directive (EU) 2023/2673): a page reachable at all times
/// (<c>/shop/widerrufen</c>, a menu element added to the footer once, links on the order page and in the account) where a consumer states name, contract and
/// e-mail, sees what he is about to send and confirms it with "Widerruf bestätigen"; the receipt (content, date and time) goes to him by mail at once.
/// <para>A signed-in customer picks the order in a dialog with a search over HIS orders and ticks the items; a guest types the order number (and, with the
/// link of his order, gets its items too). Every declaration is stored — also one whose order cannot be matched: a withdrawal is valid because it was declared,
/// not because our data agrees — and the back office follows it through the return (Shop → Widerrufe). Nothing about an order is shown to anybody who does not
/// already hold it (member, or the order's own key).</para>
/// </summary>
internal static class ShopWithdrawals
{
    public const string Path = "/shop/widerrufen";
    public const string MailCustomer = "shop.withdrawal.received";
    public const string MailShop = "shop.admin.withdrawal";

    public const string Received = "received", ReturnExpected = "return", Returned = "returned", Refunded = "refunded", Rejected = "rejected", Closed = "closed";
    public static readonly string[] Statuses = { Received, ReturnExpected, Returned, Refunded, Rejected, Closed };

    private static string Intl(long n) => n.ToString(CultureInfo.InvariantCulture);

    public static void Register(IPluginBuilder b)
    {
        b.Route(Path, PageAsync, new PublicRouteOptions { Antiforgery = true, PostsPerMinute = 20 });
        // The dialog's list: the signed-in customer's own orders, nobody else's (the handler never runs for a guest).
        b.Route(Path + "/orders", OrdersAsync, new PublicRouteOptions { Methods = PluginMethod.Get, RequireMember = true });
        // Always reachable: the footer gets the link once (an editor may move it; deleting it is the operator's decision and sticks).
        b.MenuElement(new MenuElementSpec("withdraw", "shop.el.withdraw") { Help = "shop.el.withdrawHelp", Icon = "arrow-back-up", DefaultMenu = "footer" },
            (ctx, ct) => Task.FromResult<MenuEntry?>(new MenuEntry(ctx.T("shop.wd.button"), Path)));
        b.TemplatePart(new TemplatePartSpec("withdraw", "shop.part.withdraw", PartDefault)
        {
            Help = "shop.part.withdrawHelp",
            Placeholders = new[] { new TemplatePlaceholder("title", "shop.ph.title", false), new TemplatePlaceholder("intro_html", "shop.ph.intro_html", true), new TemplatePlaceholder("content_html", "shop.ph.content_html", true) }
        });
        foreach (var spec in MailSpecs) b.MailTemplate(spec);
    }

    public const string PartDefault = """
        <h1>{{title}}</h1>
        {{intro_html}}
        {{content_html}}
        """;

    public static readonly MailTemplateSpec[] MailSpecs =
    {
        new(MailCustomer, "Shop: Eingangsbestätigung Widerruf", "Geht sofort an den Kunden, der über „Vertrag widerrufen“ einen Widerruf erklärt hat (gesetzlich vorgeschrieben: Inhalt, Datum und Uhrzeit).",
            "Eingangsbestätigung Ihres Widerrufs {{withdrawal_number}}",
            """
            Hallo {{customer_name}},

            hiermit bestätigen wir den Eingang Ihres Widerrufs.

            Eingegangen am: {{received_at}}
            Vorgangsnummer: {{withdrawal_number}}
            Bestellung: {{order_number}}
            Widerrufen: {{items}}
            Name: {{customer_name}}
            E-Mail: {{customer_email}}
            {{message}}

            {{return_info}}

            Mit freundlichen Grüßen
            {{site_name}}
            """)
        {
            Placeholders = new MailPlaceholder[]
            {
                new("withdrawal_number", "Die Vorgangsnummer des Widerrufs"), new("received_at", "Datum und Uhrzeit des Eingangs"), new("order_number", "Die angegebene Bestellung"),
                new("items", "Was widerrufen wurde (Artikel oder „die gesamte Bestellung“)"), new("customer_name", "Name"), new("customer_email", "E-Mail-Adresse"),
                new("message", "Die Nachricht des Kunden (leer, wenn keine)"), new("return_info", "Die Hinweise zur Rücksendung aus den Einstellungen"), new("site_name", "Name der Website")
            }
        },
        new(MailShop, "Shop: Neuer Widerruf (an den Shop)", "Geht an die Benachrichtigungsadresse des Shops, wenn ein Widerruf eingeht.",
            "Widerruf {{withdrawal_number}} zu Bestellung {{order_number}}",
            """
            Ein Widerruf ist eingegangen.

            Eingegangen am: {{received_at}}
            Bestellung: {{order_number}} {{matched}}
            Widerrufen: {{items}}
            Name: {{customer_name}}
            E-Mail: {{customer_email}}
            {{message}}

            Bearbeiten: {{admin_url}}
            """)
        {
            Placeholders = new MailPlaceholder[]
            {
                new("withdrawal_number", "Die Vorgangsnummer"), new("received_at", "Datum und Uhrzeit des Eingangs"), new("order_number", "Die angegebene Bestellung"),
                new("matched", "Ob die Bestellung gefunden wurde"), new("items", "Was widerrufen wurde"), new("customer_name", "Name"), new("customer_email", "E-Mail-Adresse"),
                new("message", "Die Nachricht"), new("admin_url", "Link in den Verwaltungsbereich")
            }
        }
    };

    // ---- the order a request may speak about --------------------------------------------------------------------------------

    /// <summary>The order the visitor may see: his own as a member, or the one whose key the link carries. Null for anybody else — a guest never learns
    /// whether a number exists.</summary>
    private static async Task<Order?> VisibleOrderAsync(IPluginContext ctx, ShopStore store, string number, string token, CancellationToken ct)
    {
        if (number.Length == 0) return null;
        var o = await store.OrderByNumberAsync(number, ct);
        if (o is null) return null;
        if (ctx.Member is { } m && (o.MemberId == m.Id || o.MemberId == 0 && string.Equals(o.Email, m.Email, StringComparison.OrdinalIgnoreCase))) return o;
        return token.Length > 0 && o.AccessToken.Length > 0 && string.Equals(o.AccessToken, token, StringComparison.Ordinal) ? o : null;
    }

    /// <summary>The withdrawal function for an order: the page with the order chosen (and its key, so a guest's link shows its items).</summary>
    public static string LinkFor(Order o, bool withKey) =>
        Path + "?order=" + Uri.EscapeDataString(o.Number) + (withKey && o.AccessToken.Length > 0 ? "&t=" + Uri.EscapeDataString(o.AccessToken) : "");

    /// <summary>Orders that can still be withdrawn from in the customer's eyes (not cancelled, not fully refunded).</summary>
    public static bool Offered(Order o) => o.Status is not (OrderStatus.Cancelled or OrderStatus.Refunded);

    // ---- the page ---------------------------------------------------------------------------------------------------------------

    private sealed record Form(string Name, string Email, string Order, string Token, string ItemsText, string Message, IReadOnlyList<long> ItemIds);

    private static Form Read(IPluginContext ctx)
    {
        var r = ctx.Request;
        var ids = (r.Form.TryGetValue("item", out var v) ? v : "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => long.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0).Where(n => n > 0).Distinct().Take(200).ToList();
        static string Clip(string s, int n) => s.Length > n ? s[..n] : s;
        return new Form(Clip(r.F("name").Trim(), 200), Clip(r.F("email").Trim(), 200), Clip(r.F("order").Trim(), 60), Clip(r.F("t").Trim(), 100),
            Clip(r.F("items_text").Trim(), 2000), Clip(r.F("message").Trim(), 2000), ids);
    }

    public static async Task<PluginResult> PageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var store = ShopStore.Public(ctx);
        var title = ctx.T("shop.wd.title");
        var intro = $"<div class=\"shop-wd-intro\">{RichOrText(cfg.WithdrawIntro.Length > 0 ? cfg.WithdrawIntro : ctx.T("shop.wd.intro"))}</div>";
        var content = new StringBuilder();

        if (ctx.Request.IsPost)
        {
            var f = Read(ctx);
            var order = await VisibleOrderAsync(ctx, store, f.Order, f.Token, ct);
            var items = order is null ? new List<OrderItem>() : (await store.ItemsAsync(order.Id, ct)).ToList();
            var chosen = items.Where(i => f.ItemIds.Contains(i.Id)).ToList();
            var errors = new List<string>();
            if (f.Name.Length == 0) errors.Add(ctx.T("shop.wd.errName"));
            if (!ShopOrdersValidation.ValidEmail(f.Email)) errors.Add(ctx.T("shop.err.email"));
            if (f.Order.Length == 0) errors.Add(ctx.T("shop.wd.errOrder"));
            var what = chosen.Count > 0 ? string.Join(", ", chosen.Select(i => $"{i.Qty} × {i.DisplayTitle}")) : f.ItemsText.Length > 0 ? f.ItemsText : ctx.T("shop.wd.whole");

            if (errors.Count == 0 && ctx.Request.Action == "confirm")
            {
                var (number, at) = await SaveAsync(ctx, store, f, order, chosen, what, ct);
                await SendAsync(ctx, cfg, f, order, number, at, what, ct);
                content.Append($"<div class=\"shop-card-box shop-wd-done\"><h2>{E(ctx.T("shop.wd.doneTitle"))}</h2><p>{E(ctx.T("shop.wd.done", Local(at).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), Local(at).ToString("HH:mm", CultureInfo.InvariantCulture), f.Email))}</p>");
                content.Append(Summary(ctx, number, at, f, what));
                content.Append($"<h3>{E(ctx.T("shop.wd.returnTitle"))}</h3><div class=\"shop-pre\">{E(ReturnInfo(ctx, cfg))}</div></div>");
                return await Draw(ctx, cfg, title, "", content.ToString());
            }
            if (errors.Count == 0 && ctx.Request.Action == "review")
            {
                // What he is about to send, and the one button that sends it ("Widerruf bestätigen", as the law names it).
                content.Append($"<form method=\"post\" action=\"{Path}\" class=\"shop-card-box shop-wd-review\">{ctx.Antiforgery}<h2>{E(ctx.T("shop.wd.reviewTitle"))}</h2>");
                content.Append(Summary(ctx, null, null, f, what));
                content.Append(Hidden(f));
                content.Append($"<p class=\"shop-wd-actions\"><button class=\"shop-btn big\" type=\"submit\" name=\"action\" value=\"confirm\">{E(ctx.T("shop.wd.confirm"))}</button> <button class=\"shop-btn ghost\" type=\"submit\" name=\"action\" value=\"edit\">{E(ctx.T("shop.wd.back"))}</button></p></form>");
                return await Draw(ctx, cfg, title, intro, content.ToString());
            }
            if (errors.Count > 0 && ctx.Request.Action != "edit") content.Append(string.Concat(errors.Select(e => Alert(e, "error"))));
            content.Append(FormHtml(ctx, f, order, items));
            return await Draw(ctx, cfg, title, intro, content.ToString());
        }

        // The first visit: a member's own data, the order from the link (?order=…&t=… or, for a member, ?order=…).
        var q = ctx.Request.Q;
        var first = await VisibleOrderAsync(ctx, store, q("order").Trim(), q("t").Trim(), ct);
        var form = new Form(first?.Name ?? ctx.Member?.DisplayName ?? "", first?.Email ?? ctx.Member?.Email ?? "", first?.Number ?? "", first is null ? "" : q("t").Trim(), "", "", Array.Empty<long>());
        var firstItems = first is null ? new List<OrderItem>() : (await store.ItemsAsync(first.Id, ct)).ToList();
        content.Append(FormHtml(ctx, form, first, firstItems));
        return await Draw(ctx, cfg, title, intro, content.ToString());
    }

    private static async Task<PluginResult> Draw(IPluginContext ctx, ShopConfig cfg, string title, string intro, string content)
    {
        var html = ctx.RenderPart("withdraw", new Dictionary<string, string> { ["title"] = title, ["intro_html"] = intro, ["content_html"] = content });
        // Never cached, never indexed with a customer's data in it; the page itself is no secret.
        return await ShopHtml.PageAsync(ctx, cfg, title, $"<div class=\"shop shop-wd\">{html}</div>{PickerScript}", ctx.T("shop.wd.description"), noIndex: ctx.Request.IsPost);
    }

    /// <summary>A text from the settings: kept as written (line breaks), never as markup.</summary>
    private static string RichOrText(string s) => string.Concat(s.Replace("\r", "").Split("\n\n").Select(p => $"<p>{E(p.Trim()).Replace("\n", "<br>")}</p>"));

    private static string Hidden(Form f)
    {
        var sb = new StringBuilder();
        void H(string n, string v) { if (v.Length > 0) sb.Append($"<input type=\"hidden\" name=\"{n}\" value=\"{E(v)}\">"); }
        H("name", f.Name); H("email", f.Email); H("order", f.Order); H("t", f.Token); H("items_text", f.ItemsText); H("message", f.Message);
        foreach (var id in f.ItemIds) H("item", Intl(id));
        return sb.ToString();
    }

    private static string Summary(IPluginContext ctx, string? number, DateTime? at, Form f, string what)
    {
        var sb = new StringBuilder("<dl class=\"shop-wd-sum\">");
        void Row(string k, string v) { if (v.Length > 0) sb.Append($"<dt>{E(ctx.T(k))}</dt><dd>{E(v)}</dd>"); }
        if (number is not null) Row("shop.wd.number", number);
        if (at is { } a) Row("shop.wd.receivedAt", Local(a).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
        Row("shop.wd.name", f.Name); Row("shop.wd.email", f.Email); Row("shop.wd.order", f.Order); Row("shop.wd.what", what); Row("shop.wd.message", f.Message);
        return sb.Append("</dl>").ToString();
    }

    private static string FormHtml(IPluginContext ctx, Form f, Order? order, IReadOnlyList<OrderItem> items)
    {
        var sb = new StringBuilder($"<form method=\"post\" action=\"{Path}\" class=\"shop-card-box shop-wd-form\">{ctx.Antiforgery}<div class=\"shop-form\">");
        sb.Append($"<div><label for=\"wd-name\">{E(ctx.T("shop.wd.name"))} *</label><input type=\"text\" id=\"wd-name\" name=\"name\" value=\"{E(f.Name)}\" required autocomplete=\"name\"></div>");
        sb.Append($"<div><label for=\"wd-email\">{E(ctx.T("shop.wd.email"))} *</label><input type=\"email\" id=\"wd-email\" name=\"email\" value=\"{E(f.Email)}\" required autocomplete=\"email\"><div class=\"shop-muted shop-small\">{E(ctx.T("shop.wd.emailHelp"))}</div></div>");
        // The order: typed — or, for a signed-in customer, picked in a dialog with a search over his orders.
        sb.Append($"<div><label for=\"wd-order\">{E(ctx.T("shop.wd.order"))} *</label><div class=\"shop-wd-orderrow\"><input type=\"text\" id=\"wd-order\" name=\"order\" value=\"{E(f.Order)}\" required placeholder=\"{E(ctx.T("shop.wd.orderPh"))}\">");
        if (ctx.Member is not null)
            sb.Append($"<a class=\"shop-btn ghost\" href=\"{Path}?pick=1\" data-wd-pick=\"{Path}/orders\" data-title=\"{E(ctx.T("shop.wd.pickTitle"))}\" data-search=\"{E(ctx.T("shop.filterSearch"))}\" data-close=\"{E(ctx.T("shop.filterClose"))}\">{E(ctx.T("shop.wd.pick"))}</a>");
        sb.Append("</div></div>");
        if (f.Token.Length > 0) sb.Append($"<input type=\"hidden\" name=\"t\" value=\"{E(f.Token)}\">");
        if (order is not null && items.Count > 0)
        {
            // Items of an order the visitor holds: ticked = withdrawn; none ticked = the whole order.
            sb.Append($"<fieldset class=\"shop-wd-items\"><legend>{E(ctx.T("shop.wd.itemsLegend"))}</legend><div class=\"shop-muted shop-small\">{E(ctx.T("shop.wd.itemsHelp"))}</div>");
            foreach (var i in items)
                sb.Append($"<label class=\"shop-check\"><input type=\"checkbox\" name=\"item\" value=\"{Intl(i.Id)}\"{(f.ItemIds.Contains(i.Id) ? " checked" : "")}> {E(i.Qty + " × " + i.DisplayTitle)}</label>");
            sb.Append("</fieldset>");
        }
        else
            sb.Append($"<div><label for=\"wd-items\">{E(ctx.T("shop.wd.itemsText"))}</label><textarea id=\"wd-items\" name=\"items_text\" rows=\"3\" placeholder=\"{E(ctx.T("shop.wd.itemsTextPh"))}\">{E(f.ItemsText)}</textarea></div>");
        sb.Append($"<div><label for=\"wd-msg\">{E(ctx.T("shop.wd.message"))}</label><textarea id=\"wd-msg\" name=\"message\" rows=\"3\">{E(f.Message)}</textarea></div>");
        sb.Append($"<p><button class=\"shop-btn big\" type=\"submit\" name=\"action\" value=\"review\">{E(ctx.T("shop.wd.next"))}</button></p></div></form>");
        // Without script, "Bestellung wählen" lands here with ?pick=1: the list right on the page.
        if (ctx.Member is not null && ctx.Request.Q("pick") == "1") sb.Append("<div class=\"shop-card-box\">").Append(OrdersHtmlPlaceholder).Append("</div>");
        return sb.ToString();
    }

    private const string OrdersHtmlPlaceholder = "<p class=\"shop-muted\" data-wd-inline></p>";

    /// <summary>/shop/widerrufen/orders — the signed-in customer's orders as a list for the dialog (number, date, total, the first items), each a link that
    /// chooses it. A member only, and only his own.</summary>
    public static async Task<PluginResult> OrdersAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (ctx.Member is not { } m) return PluginResult.Status(403);
        var store = ShopStore.Public(ctx);
        var orders = (await store.OrdersOfMemberAsync(m.Id, m.Email, ct)).Where(Offered).OrderByDescending(o => o.CreatedUtc).Take(100).ToList();
        if (orders.Count == 0) return PluginResult.Fragment($"<p class=\"shop-muted\">{E(ctx.T("shop.wd.noOrders"))}</p>");
        var sb = new StringBuilder("<div class=\"shop-wd-orders\">");
        foreach (var o in orders)
        {
            var items = await store.ItemsAsync(o.Id, ct);
            var titles = string.Join(", ", items.Take(3).Select(i => i.DisplayTitle)) + (items.Count > 3 ? " …" : "");
            sb.Append($"<a class=\"shop-wd-order\" href=\"{E(LinkFor(o, false))}\"><strong>{E(o.Number)}</strong> <span class=\"shop-muted\">{E(Local(o.CreatedUtc).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))} · {E(Money.Format(o.TotalCents, o.Currency, ctx.Locale))}</span><span class=\"t\">{E(titles)}</span></a>");
        }
        return PluginResult.Fragment(sb.Append("</div>").ToString()).WithHeader("Cache-Control", "private, no-store");
    }

    /// <summary>The dialog: fetches the member's orders only when opened, a search narrows them, a click chooses one (the page reloads with its items).</summary>
    private const string PickerScript = """
        <script>(function(){var a=document.querySelector('[data-wd-pick]');if(!a||!window.fetch||!window.HTMLDialogElement)return;
        a.addEventListener('click',function(e){e.preventDefault();var d=document.createElement('dialog');d.className='shop-fdialog';
        d.innerHTML='<div class="hd"><strong></strong><button type="button" class="x">×</button></div><input type="search" class="q"><div class="body"><p class="shop-muted">…</p></div>';
        d.querySelector('strong').textContent=a.getAttribute('data-title')||'';var x=d.querySelector('.x');x.setAttribute('aria-label',a.getAttribute('data-close')||'');
        var q=d.querySelector('.q');q.placeholder=a.getAttribute('data-search')||'';q.setAttribute('aria-label',q.placeholder);var body=d.querySelector('.body');
        document.body.appendChild(d);d.showModal();d.addEventListener('close',function(){d.remove()});x.addEventListener('click',function(){d.close()});
        d.addEventListener('click',function(ev){if(ev.target===d)d.close()});
        fetch(a.getAttribute('data-wd-pick'),{credentials:'same-origin'}).then(function(r){if(!r.ok)throw 0;return r.text()}).then(function(h){body.innerHTML=h;q.focus()}).catch(function(){location.href=a.href});
        q.addEventListener('input',function(){var v=q.value.trim().toLowerCase();Array.prototype.forEach.call(body.querySelectorAll('.shop-wd-order'),function(o){o.style.display=!v||o.textContent.toLowerCase().indexOf(v)>=0?'':'none'})});
        });
        var inl=document.querySelector('[data-wd-inline]');if(inl&&window.fetch)fetch(a.getAttribute('data-wd-pick'),{credentials:'same-origin'}).then(function(r){return r.ok?r.text():''}).then(function(h){inl.outerHTML=h});
        })();</script>
        """;

    // ---- storing and telling ----------------------------------------------------------------------------------------------

    /// <summary>German time for what a customer reads (the shop is German law's business); UTC where the zone is unknown.</summary>
    internal static DateTime Local(DateTime utc)
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")); }
        catch { return utc; }
    }

    private static async Task<(string Number, DateTime At)> SaveAsync(IPluginContext ctx, ShopStore store, Form f, Order? order, IReadOnlyList<OrderItem> chosen, string what, CancellationToken ct)
    {
        var at = DateTime.UtcNow;
        var items = new JsonArray(chosen.Select(i => (JsonNode)new JsonObject { ["id"] = i.Id, ["title"] = i.DisplayTitle, ["qty"] = i.Qty }).ToArray());
        var id = await store.AddWithdrawalAsync(new Withdrawal
        {
            OrderId = order?.Id ?? 0, OrderNumber = f.Order, MemberId = ctx.Member?.Id ?? 0, Name = f.Name, Email = f.Email, ItemsJson = items.ToJsonString(),
            ItemsText = chosen.Count > 0 ? "" : f.ItemsText, What = what, Message = f.Message, Status = Received, CreatedUtc = at
        }, ct);
        var number = Withdrawal.NumberOf(id, at);
        if (order is not null)
            await store.AddEventAsync(order.Id, "withdrawal", $"Widerruf erklärt ({number}): {what}", ctx.Member is null ? "guest" : "customer", null, ct);
        return (number, at);
    }

    private static async Task SendAsync(IPluginContext ctx, ShopConfig cfg, Form f, Order? order, string number, DateTime at, string what, CancellationToken ct)
    {
        var values = new Dictionary<string, string>
        {
            ["withdrawal_number"] = number, ["received_at"] = Local(at).ToString("dd.MM.yyyy, HH:mm", CultureInfo.InvariantCulture) + " Uhr", ["order_number"] = f.Order, ["items"] = what,
            ["customer_name"] = f.Name, ["customer_email"] = f.Email, ["message"] = f.Message.Length > 0 ? ctx.T("shop.wd.message") + ": " + f.Message : "",
            ["return_info"] = ReturnInfo(ctx, cfg), ["site_name"] = ctx.Site.Name, ["matched"] = order is null ? ctx.T("shop.wd.unmatched") : "",
            ["admin_url"] = $"{ctx.Site.Origin}/admin/p/shop/withdrawals"
        };
        var r = await ctx.Mail.SendAsync(MailCustomer, new[] { f.Email }, values, ct: ct);
        if (!r.Ok) ctx.Log($"Widerruf {number}: Eingangsbestätigung an {f.Email} nicht gesendet – {r.Error}");
        if (order is not null)
            await new ShopStore(ctx.Data).AddEventAsync(order.Id, r.Ok ? "mail" : "mail-failed", r.Ok ? $"Eingangsbestätigung Widerruf an {f.Email} gesendet" : $"Eingangsbestätigung Widerruf an {f.Email} fehlgeschlagen – {r.Error}", "system", null, ct);
        if (cfg.NotifyEmail.Length > 0) await ctx.Mail.SendAsync(MailShop, new[] { cfg.NotifyEmail }, values, replyTo: f.Email, ct: ct);
    }

    /// <summary>What a customer is told about sending the goods back: the operator's text, else a plain one with the return address (else the seller's).</summary>
    internal static string ReturnInfo(IPluginContext ctx, ShopConfig cfg)
    {
        if (cfg.ReturnInfo.Length > 0) return cfg.ReturnInfo;
        var address = cfg.ReturnAddress.Length > 0 ? cfg.ReturnAddress : string.Join("\n", new[] { cfg.SellerName, cfg.SellerAddress }.Where(s => s.Length > 0));
        return ctx.T("shop.wd.returnDefault") + (address.Length > 0 ? "\n\n" + address : "");
    }
}

/// <summary>One declared withdrawal and where its return stands.</summary>
internal sealed record Withdrawal
{
    public long Id { get; init; }
    public long OrderId { get; init; }
    public string OrderNumber { get; init; } = "";
    public long MemberId { get; init; }
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    /// <summary>The order items withdrawn (<c>[{id, title, qty}]</c>), empty = the whole order or what <see cref="ItemsText"/> says.</summary>
    public string ItemsJson { get; init; } = "[]";
    public string ItemsText { get; init; } = "";
    /// <summary>The line the customer confirmed ("2 × Kabel, 1 × Laufwerk" / "die gesamte Bestellung").</summary>
    public string What { get; init; } = "";
    public string Message { get; init; } = "";
    public string Status { get; init; } = ShopWithdrawals.Received;
    public string Note { get; init; } = "";
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
    public string Number => NumberOf(Id, CreatedUtc);
    public static string NumberOf(long id, DateTime at) => "WR-" + at.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" + id.ToString(CultureInfo.InvariantCulture);
}
