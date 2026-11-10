using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// The website side of a booking: the request (posted by the block "Buchungsanfrage") and the guest's own page behind the secret link, where an offer is
/// accepted. Everything is drawn with the site's own variables (<c>--accent</c>, <c>--line</c> …) so it follows the template.
/// </summary>
public static partial class BookingPublic
{
    public const string RequestPath = "/buchung/anfrage";

    public static void Configure(IPluginBuilder b)
    {
        // an anonymous form with nothing to forge: no token (a block cannot carry one), rate-limited by the host (20 a minute per address, like a contact form) and with a honeypot
        b.Route(RequestPath, RequestAsync, new PublicRouteOptions { Methods = PluginMethod.Post });
        b.Route(BookingFlow.GuestPath + "{key}", GuestAsync, new PublicRouteOptions { Antiforgery = true });
        b.Route(BookingFlow.GuestPath + "{key}/pdf", GuestPdfAsync, new PublicRouteOptions { Methods = PluginMethod.Get });
        b.Route(BookingFlow.GuestPath + "{key}/checkin", CheckinAsync, new PublicRouteOptions { Antiforgery = true });
    }

    /// <summary>The online check-in behind the guest's link: only for a confirmed stay that has not ended; may be corrected until the day of arrival.</summary>
    private static async Task<PluginResult> CheckinAsync(IPluginContext ctx, CancellationToken ct)
    {
        var key = ctx.Request.R("key");
        var b = await BookingFlow.ByKeyAsync(ctx.Data, key, ct);
        if (b is null) return PluginResult.NotFound();
        var back = BookingFlow.GuestPath + key;
        if (b.Status != BookingStatus.Confirmed || b.Departure < B.Today) return PluginResult.Redirect(back);
        var store = new BookingStore(ctx.Data);
        var unit = await store.UnitAsync(b.UnitId, ct);
        var house = unit is null ? null : await store.PropertyAsync(unit.PropertyId, ct);
        var profile = BookingCheckin.ProfileFor(ctx, house);
        var count = Math.Max(1, b.Adults + b.Children);
        var row = await ctx.Data.Table(BookingStore.TBookings).GetAsync(b.Id, ct);
        string? problem = null;
        if (ctx.Request.IsPost && ctx.Request.Action == "checkin")
        {
            if (b.Arrival < B.Today) return PluginResult.Redirect(back);                       // after arrival the host has the list; no more changes
            var (people, err) = BookingCheckin.Read(ctx.Request, profile, count);
            if (err is null)
            {
                await BookingCheckin.SaveAsync(ctx.Data, b, people, BookingCheckin.ArrivalTimes.Contains(ctx.Request.F("arrival_time")) ? ctx.Request.F("arrival_time") : "",
                    ctx.Request.F("travel"), B.Checked(ctx.Request.F("remember")), ct);
                return PluginResult.Redirect(back + "?checkin=1");
            }
            problem = err;
        }
        var prefill = await BookingCheckin.TravellersAsync(ctx.Data, b.Id, ct);
        if (prefill.Count == 0) prefill = await BookingCheckin.RememberedAsync(ctx.Data, b.GuestId, ct);
        if (problem is not null) prefill = Enumerable.Range(0, count).Select(i => new Traveller(i,
            BookingCheckin.FieldsOf(profile, i).ToDictionary(k => k, k => ctx.Request.F("t" + i + "_" + k)))).ToList();
        var body = "<p class=\"bk-kicker\">" + B.H(house?.Name ?? ctx.Site.Name) + " · Nr. " + B.H(b.Number) + "</p><h1>Online-Check-in</h1>"
            + "<p>" + B.H(unit?.Name ?? "") + ", " + B.Date(b.Arrival) + " – " + B.Date(b.Departure) + ". Bitte für jede Person ausfüllen – wir sind gesetzlich verpflichtet, "
            + "unsere Gäste zu melden. Die Angaben gehen nur an uns und werden nach der gesetzlichen Frist gelöscht.</p>"
            + BookingCheckin.Form(profile, count, prefill, row?.Text("arrival_time") ?? "", row?.Text("travel") ?? "", ctx.Antiforgery, problem)
            + "<p><a href=\"" + B.H(back) + "\">Zurück zur Buchung</a></p>";
        return Shell("Online-Check-in", body);
    }

    private static readonly KeyValuePair<string, string>[] NoIndex = { new("robots", "noindex, nofollow") };

    internal static PluginResult Shell(string title, string body) =>
        PluginResult.Page("<style>" + BookingCss.Css + "</style><section class=\"section bk\"><div class=\"container bk-narrow\">" + body + "</div></section>", title, null, NoIndex);

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex MailRe();

    // ---- the request ------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> RequestAsync(IPluginContext ctx, CancellationToken ct)
    {
        var f = ctx.Request;
        string Back(string message) => "<h1>Anfrage nicht gesendet</h1><p class=\"bk-alert\">" + B.H(message) + "</p><p><a class=\"btn btn-outline bk-btn bk-btn-ghost\" href=\""
            + B.H(SafeBack(f.Header("Referer"), ctx)) + "\">Zurück zum Formular</a></p>";
        // a bot fills every field it sees — this one is hidden from people
        if (f.F("website").Length > 0) return Shell("Danke", "<h1>Danke!</h1><p>Ihre Anfrage ist eingegangen.</p>");
        var store = new BookingStore(ctx.Data);
        var unit = await store.UnitBySlugAsync(B.Slug(f.F("einheit")), ct);
        if (unit is null || !unit.Active) return Shell("Anfrage", Back("Bitte eine Unterkunft wählen."));
        // the date picker posts one field "zeitraum" (YYYY-MM-DD..YYYY-MM-DD); without script the form has the two plain date fields
        var (rawFrom, rawTo) = f.F("zeitraum").Split("..") is [var zf, var zt] ? (zf, zt) : (f.F("anreise"), f.F("abreise"));
        if (B.Day(rawFrom) is not { } arrival || B.Day(rawTo) is not { } departure) return Shell("Anfrage", Back("Bitte An- und Abreise angeben."));
        if (arrival < B.Today) return Shell("Anfrage", Back("Die Anreise liegt in der Vergangenheit."));
        var name = B.Clip(f.F("name"), 120);
        var email = B.Clip(f.F("email"), 200);
        if (name.Length < 2) return Shell("Anfrage", Back("Bitte Ihren Namen angeben."));
        if (!MailRe().IsMatch(email)) return Shell("Anfrage", Back("Bitte eine gültige E-Mail-Adresse angeben."));
        if (!B.Checked(f.F("consent"))) return Shell("Anfrage", Back("Bitte der Verarbeitung Ihrer Angaben zustimmen."));
        int N(string k, int def) => int.TryParse(f.F(k), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 50) : def;
        var fees = await store.FeesAsync(unit.PropertyId, ct);
        var extras = fees.Where(x => x.Optional && B.Checked(f.F("extra_" + x.Id))).Select(x => x.Id).ToList();
        var stay = new Stay(arrival, departure, Math.Max(1, N("erwachsene", 2)), N("kinder", 0), extras);

        Booking booking; string key;
        try
        {
            (booking, key) = await BookingFlow.RequestAsync(ctx.Data, new BookingFlow.RequestInput(unit, stay, name, email, f.F("telefon"), f.F("nachricht"), ctx.Locale),
                BookingPlugin.HoldHours(ctx), BookingPlugin.Currency(ctx), ct);
        }
        catch (BookingException ex) { return Shell("Anfrage", Back(ex.Message)); }

        var house = await store.PropertyAsync(unit.PropertyId, ct);
        var values = BookingMail.Values(ctx, booking, unit, house, key);
        await BookingFlow.NoteAsync(ctx.Data, booking.Id, "Mail an den Gast: " + await BookingMail.SendAsync(ctx, BookingMail.RequestGuest, booking.Email, values, ct), ct);
        if (BookingPlugin.NotifyAddress(ctx) is { Length: > 0 } host)
            await BookingFlow.NoteAsync(ctx.Data, booking.Id, "Mail an den Gastgeber: " + await BookingMail.SendAsync(ctx, BookingMail.RequestHost, host, values, ct), ct);
        return PluginResult.Redirect(BookingFlow.GuestPath + key + "?neu=1");
    }

    /// <summary>Where "back" goes: the page the form was on, when it is this site's own; else the home page.</summary>
    private static string SafeBack(string referer, IPluginContext ctx)
    {
        var origin = (ctx.Site.Origin ?? "").TrimEnd('/');
        if (Uri.TryCreate(referer, UriKind.Absolute, out var u) && origin.Length > 0 && referer.StartsWith(origin + "/", StringComparison.OrdinalIgnoreCase)) return u.PathAndQuery;
        return "/";
    }

    // ---- the guest's page -----------------------------------------------------------------------------------------

    private static async Task<PluginResult> GuestAsync(IPluginContext ctx, CancellationToken ct)
    {
        var key = ctx.Request.R("key");
        var b = await BookingFlow.ByKeyAsync(ctx.Data, key, ct);
        if (b is null) return PluginResult.NotFound();
        var store = new BookingStore(ctx.Data);
        var unit = await store.UnitAsync(b.UnitId, ct);
        var house = unit is null ? null : await store.PropertyAsync(unit.PropertyId, ct);
        var self = BookingFlow.GuestPath + key;

        if (ctx.Request.IsPost && ctx.Request.Action == "accept")
        {
            if (b.Status != BookingStatus.Offer || b.OfferUntil < DateTime.UtcNow) return PluginResult.Redirect(self);
            try { b = await BookingFlow.MoveAsync(ctx.Data, b.Id, BookingStatus.Confirmed, "Angebot vom Gast angenommen", null, ct); }
            catch (BookingException) { return PluginResult.Redirect(self); }
            var values = BookingMail.Values(ctx, b, unit, house, key);
            await BookingFlow.NoteAsync(ctx.Data, b.Id, "Bestätigung an den Gast: " + await BookingMail.SendAsync(ctx, BookingMail.Confirmed, b.Email, values, ct), ct);
            if (BookingPlugin.NotifyAddress(ctx) is { Length: > 0 } host)
                await BookingFlow.NoteAsync(ctx.Data, b.Id, "Mail an den Gastgeber: " + await BookingMail.SendAsync(ctx, BookingMail.AcceptedHost, host, values, ct), ct);
            return PluginResult.Redirect(self);
        }

        var cur = BookingPlugin.Currency(ctx);
        var sb = new StringBuilder();
        var (title, lead) = b.Status switch
        {
            BookingStatus.Request => ("Ihre Anfrage", ctx.Request.Q("neu") == "1"
                ? "Vielen Dank! Ihre Anfrage ist eingegangen und der Zeitraum für Sie vorgemerkt bis " + BookingMail.Local(b.HoldUntil) + ". Wir melden uns per E-Mail."
                : "Ihre Anfrage liegt uns vor. Der Zeitraum ist für Sie vorgemerkt bis " + BookingMail.Local(b.HoldUntil) + "."),
            BookingStatus.Offer => ("Unser Angebot", b.OfferUntil < DateTime.UtcNow ? "Dieses Angebot ist abgelaufen." : "Gerne können Sie bei uns wohnen. Das Angebot gilt bis " + BookingMail.Local(b.OfferUntil) + "."),
            BookingStatus.Confirmed => ("Ihre Buchung", "Ihre Buchung ist bestätigt – wir freuen uns auf Sie!"),
            BookingStatus.Declined => ("Ihre Anfrage", "Leider können wir Ihre Anfrage nicht annehmen."),
            BookingStatus.Expired => ("Ihre Anfrage", "Diese Anfrage ist abgelaufen, der Zeitraum ist wieder frei."),
            BookingStatus.Cancelled => ("Ihre Buchung", "Diese Buchung ist storniert."),
            _ => ("Ihre Buchung", "")
        };
        sb.Append("<p class=\"bk-kicker\">").Append(B.H(house?.Name ?? ctx.Site.Name)).Append(" · Nr. ").Append(B.H(b.Number)).Append("</p>");
        sb.Append("<h1>").Append(B.H(title)).Append("</h1>");
        sb.Append("<p class=\"bk-status bk-status-").Append(B.H(b.Status)).Append("\">").Append(B.H(lead)).Append("</p>");
        sb.Append("<div class=\"bk-card\"><h2>").Append(B.H(unit?.Name ?? "")).Append("</h2><dl class=\"bk-facts\">")
          .Append("<dt>Anreise</dt><dd>").Append(B.Weekday(b.Arrival)).Append(' ').Append(B.Date(b.Arrival)).Append(house?.CheckIn is { Length: > 0 } ci ? ", ab " + B.H(ci) + " Uhr" : "").Append("</dd>")
          .Append("<dt>Abreise</dt><dd>").Append(B.Weekday(b.Departure)).Append(' ').Append(B.Date(b.Departure)).Append(house?.CheckOut is { Length: > 0 } co ? ", bis " + B.H(co) + " Uhr" : "").Append("</dd>")
          .Append("<dt>Nächte</dt><dd>").Append(b.Nights).Append("</dd><dt>Gäste</dt><dd>").Append(B.H(BookingMail.Persons(b.Adults, b.Children))).Append("</dd></dl>");
        sb.Append(Bill(b.Lines, b.Total, cur)).Append("</div>");

        if (b.Status == BookingStatus.Offer && b.OfferUntil >= DateTime.UtcNow)
            sb.Append("<form method=\"post\" class=\"bk-accept\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"accept\">")
              .Append("<p>Mit dem Klick buchen Sie verbindlich zu den genannten Bedingungen.</p><button class=\"btn bk-btn\" type=\"submit\">Verbindlich buchen</button></form>");
        if (b.Status == BookingStatus.Confirmed)
        {
            var plan = await BookingPayments.PlanAsync(ctx, b, ct);
            var two = plan.Deposit > 0 && plan.Deposit < plan.Total;
            sb.Append("<div class=\"bk-card\"><h2>Zahlung</h2><dl class=\"bk-facts\">");
            if (two) sb.Append("<dt>Anzahlung</dt><dd>").Append(B.H(B.Money(plan.Deposit, cur))).Append(" bis ").Append(B.Date(plan.DepositDue)).Append("</dd>")
                       .Append("<dt>Rest</dt><dd>").Append(B.H(B.Money(plan.Rest, cur))).Append(" bis ").Append(B.Date(plan.RestDue)).Append("</dd>");
            else sb.Append("<dt>Gesamtbetrag</dt><dd>").Append(B.H(B.Money(plan.Total, cur))).Append(" bis ").Append(B.Date(plan.DepositDue)).Append("</dd>");
            sb.Append("<dt>Bezahlt</dt><dd>").Append(B.H(B.Money(plan.Paid, cur))).Append("</dd><dt>Noch offen</dt><dd><strong>").Append(B.H(B.Money(plan.Open, cur))).Append("</strong></dd></dl>");
            if (ctx.Settings.Get(BookingPlugin.PaymentInfoKey) is { Length: > 0 } pay)
                sb.Append("<p class=\"bk-pre\">").Append(B.H(pay)).Append("</p><p class=\"bk-muted\">Verwendungszweck: ").Append(B.H(b.Number)).Append("</p>");
            sb.Append("<p><a class=\"btn btn-outline bk-btn bk-btn-ghost\" href=\"").Append(B.H(self + "/pdf")).Append("\">Buchungsbestätigung (PDF)</a></p></div>");

            if (b.Departure >= B.Today)
            {
                var row = await ctx.Data.Table(BookingStore.TBookings).GetAsync(b.Id, ct);
                var done = row?.Timestamp("checkin_at") is not null;
                sb.Append("<div class=\"bk-card\"><h2>Online-Check-in</h2>");
                if (ctx.Request.Q("checkin") == "1") sb.Append("<p class=\"bk-status bk-status-confirmed\">Danke! Ihr Check-in ist bei uns angekommen.</p>");
                sb.Append(done ? "<p>Erledigt" + (row!.Text("arrival_time") is { Length: > 0 } at ? " – Ankunft " + B.H(at) : "") + ".</p>" : "<p>Bitte vor der Anreise die Angaben aller Personen eintragen.</p>");
                if (b.Arrival >= B.Today)
                    sb.Append("<p><a class=\"btn bk-btn").Append(done ? " btn-outline bk-btn-ghost" : "").Append("\" href=\"").Append(B.H(self + "/checkin")).Append("\">")
                      .Append(done ? "Angaben ändern" : "Jetzt einchecken").Append("</a></p>");
                sb.Append("</div>");

                // how to get there and in: directions and rules always; the way in (code, key box) and the Wi-Fi only from the day before arrival
                var near = B.Today >= b.Arrival.AddDays(-1);
                var parts = new List<(string, string)>();
                if (house?.Directions is { Length: > 0 } dir) parts.Add(("Anfahrt", dir));
                if (near && unit?.AccessInfo is { Length: > 0 } acc) parts.Add(("Zugang", acc));
                if (near && unit?.Wifi is { Length: > 0 } wifi) parts.Add(("WLAN", wifi));
                if (house?.HouseRules is { Length: > 0 } rules) parts.Add(("Hausregeln", rules));
                if (parts.Count > 0 || unit?.AccessInfo is { Length: > 0 } || unit?.Wifi is { Length: > 0 })
                {
                    sb.Append("<div class=\"bk-card\"><h2>Ihre Anreise</h2>");
                    foreach (var (h, text) in parts) sb.Append("<h3>").Append(B.H(h)).Append("</h3><p class=\"bk-pre\">").Append(B.H(text)).Append("</p>");
                    if (!near && unit?.AccessInfo is { Length: > 0 }) sb.Append("<p class=\"bk-muted\">Den Zugang (Schlüssel/Code) und das WLAN finden Sie hier ab dem Tag vor Ihrer Anreise.</p>");
                    sb.Append("</div>");
                }
            }
        }
        sb.Append("<p class=\"bk-muted\">Diese Seite ist nur über den Link in Ihrer E-Mail erreichbar. Fragen? Antworten Sie einfach auf unsere E-Mail.</p>");
        return Shell(title, sb.ToString());
    }

    /// <summary>The confirmation as PDF — only for a confirmed (or cancelled, to keep the record) booking, only with the key.</summary>
    private static async Task<PluginResult> GuestPdfAsync(IPluginContext ctx, CancellationToken ct)
    {
        var b = await BookingFlow.ByKeyAsync(ctx.Data, ctx.Request.R("key"), ct);
        if (b is null || b.Status is not (BookingStatus.Confirmed or BookingStatus.Cancelled)) return PluginResult.NotFound();
        return (await BookingAdminBookings.ConfirmationPdfAsync(ctx, b, ct)).WithHeader("X-Robots-Tag", "noindex");
    }

    /// <summary>The bill as a table (lines, the way each was counted, total).</summary>
    internal static string Bill(IReadOnlyList<QuoteLine> lines, long total, string cur)
    {
        var sb = new StringBuilder("<table class=\"bk-bill\"><tbody>");
        foreach (var l in lines)
            sb.Append("<tr><td>").Append(B.H(l.Text)).Append(l.Detail.Length > 0 ? "<small>" + B.H(l.Detail) + "</small>" : "").Append("</td><td>").Append(B.H(B.Money(l.Amount, cur))).Append("</td></tr>");
        sb.Append("</tbody><tfoot><tr><td>Gesamt</td><td>").Append(B.H(B.Money(total, cur))).Append("</td></tr></tfoot></table>");
        return sb.ToString();
    }
}

/// <summary>The public look: the site's variables first (accent, line, font), so the template decides; fallbacks only where a template has none.</summary>
public static class BookingCss
{
    public const string Css = """
.bk{--bk-accent:var(--accent,#2563eb);--bk-line:var(--line,#e5e7eb);--bk-muted:var(--muted,#6b7280);--bk-bg:var(--bg,#fff)}
.bk-narrow{max-width:760px}
.bk h1{margin:.2em 0 .4em}.bk-kicker{color:var(--bk-muted);font-size:.9em;margin:0}
.bk-card{border:1px solid var(--bk-line);border-radius:12px;padding:18px 20px;margin:16px 0;background:var(--bk-bg)}
.bk-card h2{margin:0 0 10px;font-size:1.2em}
.bk-facts{display:grid;grid-template-columns:max-content 1fr;gap:4px 16px;margin:0 0 14px}.bk-facts dt{color:var(--bk-muted)}.bk-facts dd{margin:0}
.bk-bill{width:100%;border-collapse:collapse}.bk-bill td{padding:6px 0;border-top:1px solid var(--bk-line);vertical-align:top}.bk-bill td:last-child{text-align:right;white-space:nowrap;padding-left:12px}
.bk-bill small{display:block;color:var(--bk-muted);font-size:.85em}.bk-bill tfoot td{font-weight:700;border-top:2px solid currentColor}
.bk-status{padding:12px 16px;border-radius:10px;background:color-mix(in srgb,var(--bk-accent) 10%,transparent)}
.bk-status-declined,.bk-status-expired,.bk-status-cancelled{background:color-mix(in srgb,#9ca3af 18%,transparent)}
.bk-status-confirmed{background:color-mix(in srgb,#16a34a 14%,transparent)}
.bk-alert{padding:12px 16px;border-radius:10px;background:color-mix(in srgb,#dc2626 12%,transparent)}
.bk-btn{cursor:pointer;text-align:center}button.bk-btn{font-family:inherit}
.bk-btn:not(.btn){display:inline-block;border:0;border-radius:999px;padding:12px 22px;background:var(--bk-accent);color:#fff;font:inherit;font-weight:600;text-decoration:none}
.bk-btn-ghost:not(.btn){background:transparent;color:var(--bk-accent);border:1px solid var(--bk-accent)}
.bk-btn[disabled]{opacity:.5;cursor:not-allowed}
.bk-muted{color:var(--bk-muted);font-size:.9em}.bk-pre{white-space:pre-line}
.bk-accept{margin:18px 0}
.bk-form{display:grid;gap:18px;max-width:880px;margin:0 auto}.bk-form .form-field{margin:0}
.bk-row{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:16px}
.bk-row-stay{grid-template-columns:minmax(0,2fr) minmax(0,1fr) minmax(0,1fr)}
@media(max-width:640px){.bk-row-stay{grid-template-columns:1fr 1fr}.bk-row-stay .bk-dates{grid-column:1/-1}}
.bk-form .bk-check{display:flex;gap:10px;align-items:flex-start;font-size:.95em}.bk-form .bk-check input{width:auto;margin-top:4px;accent-color:var(--bk-accent)}

.bk-hp{position:absolute!important;left:-9999px!important;width:1px;height:1px;overflow:hidden}
.bk-price{border:1px solid var(--bk-line);border-radius:12px;padding:14px 16px}.bk-price:empty{display:none}
.bk-price .bk-total{font-size:1.3em;font-weight:700}.bk-price .bk-no{color:#b91c1c}
.bk-cal{display:grid;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));gap:20px}
.bk-cal-wrap{position:relative;max-width:760px;margin:0 auto}.bk-cal.bk-cal-paged{grid-template-columns:1fr 1fr;gap:28px}.bk-month[hidden]{display:none}
@media(max-width:640px){.bk-cal.bk-cal-paged{grid-template-columns:1fr}}
.bk-cal-nav{position:absolute;inset:-4px 0 auto 0;display:flex;justify-content:space-between;pointer-events:none}
.bk-cal-nav button{pointer-events:auto;width:34px;height:34px;border-radius:50%;border:1px solid var(--bk-line);background:var(--bk-bg);color:inherit;font-size:20px;line-height:1;cursor:pointer}
.bk-cal-nav button:disabled{opacity:.3;cursor:default}
.bk-month h3{margin:0 0 8px;font-size:1em;text-align:center}
.bk-month table{width:100%;border-collapse:separate;border-spacing:3px;table-layout:fixed}
.bk-month th{font-size:.75em;color:var(--bk-muted);font-weight:500}
.bk-month td{text-align:center;padding:6px 0;border-radius:6px;font-size:.9em;background:color-mix(in srgb,#16a34a 12%,transparent)}
.bk-month td.bk-taken{background:color-mix(in srgb,#dc2626 16%,transparent);color:color-mix(in srgb,currentColor 55%,transparent);text-decoration:line-through}
.bk-month td.bk-past{background:transparent;color:var(--bk-muted);opacity:.5}
.bk-month td.bk-empty{background:transparent}
.bk-month td.bk-free{cursor:pointer}.bk-month td.bk-sel{background:var(--bk-accent);color:#fff}
.bk-legend{display:flex;gap:16px;flex-wrap:wrap;justify-content:center;font-size:.85em;color:var(--bk-muted);margin-top:14px}
.bk-legend i{display:inline-block;width:12px;height:12px;border-radius:3px;margin-right:6px;vertical-align:-1px}
.bk-units{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,230px),1fr));gap:22px}
.bk-unit{border:1px solid var(--bk-line);border-radius:14px;overflow:hidden;display:flex;flex-direction:column;background:var(--bk-bg)}
.bk-unit img{width:100%;aspect-ratio:4/3;object-fit:cover;display:block}
.bk-unit-body{padding:16px 18px 18px;display:flex;flex-direction:column;gap:8px;flex:1}.bk-unit h3{margin:0;font-size:1.15em}.bk-unit-body p{margin:0}
.bk-unit .bk-from{font-weight:700;margin-top:auto;padding-top:6px}.bk-unit .bk-btn{align-self:flex-start;flex:0 0 auto;margin-top:auto}.bk-unit .bk-from+.bk-btn{margin-top:4px}
""";
}
