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
        string Back(string message) => "<h1>Anfrage nicht gesendet</h1><p class=\"bk-alert\">" + B.H(message) + "</p><p><a class=\"bk-btn bk-btn-ghost\" href=\""
            + B.H(SafeBack(f.Header("Referer"), ctx)) + "\">Zurück zum Formular</a></p>";
        // a bot fills every field it sees — this one is hidden from people
        if (f.F("website").Length > 0) return Shell("Danke", "<h1>Danke!</h1><p>Ihre Anfrage ist eingegangen.</p>");
        var store = new BookingStore(ctx.Data);
        var unit = await store.UnitBySlugAsync(B.Slug(f.F("einheit")), ct);
        if (unit is null || !unit.Active) return Shell("Anfrage", Back("Bitte eine Unterkunft wählen."));
        if (B.Day(f.F("anreise")) is not { } arrival || B.Day(f.F("abreise")) is not { } departure) return Shell("Anfrage", Back("Bitte An- und Abreise angeben."));
        if (arrival < DateOnly.FromDateTime(DateTime.UtcNow)) return Shell("Anfrage", Back("Die Anreise liegt in der Vergangenheit."));
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
              .Append("<p>Mit dem Klick buchen Sie verbindlich zu den genannten Bedingungen.</p><button class=\"bk-btn\" type=\"submit\">Verbindlich buchen</button></form>");
        if (b.Status == BookingStatus.Confirmed && ctx.Settings.Get(BookingPlugin.PaymentInfoKey) is { Length: > 0 } pay)
            sb.Append("<div class=\"bk-card\"><h2>Zahlung</h2><p class=\"bk-pre\">").Append(B.H(pay)).Append("</p><p class=\"bk-muted\">Verwendungszweck: ").Append(B.H(b.Number)).Append("</p></div>");
        sb.Append("<p class=\"bk-muted\">Diese Seite ist nur über den Link in Ihrer E-Mail erreichbar. Fragen? Antworten Sie einfach auf unsere E-Mail.</p>");
        return Shell(title, sb.ToString());
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
.bk-btn{display:inline-block;border:0;border-radius:999px;padding:12px 22px;background:var(--bk-accent);color:#fff;font:inherit;font-weight:600;cursor:pointer;text-decoration:none}
.bk-btn-ghost{background:transparent;color:var(--bk-accent);border:1px solid var(--bk-accent)}
.bk-btn[disabled]{opacity:.5;cursor:not-allowed}
.bk-muted{color:var(--bk-muted);font-size:.9em}.bk-pre{white-space:pre-line}
.bk-accept{margin:18px 0}
.bk-form{display:grid;gap:14px}.bk-row{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px}
.bk-form label{display:grid;gap:4px;font-size:.92em}.bk-form input,.bk-form select,.bk-form textarea{font:inherit;padding:10px 12px;border:1px solid var(--bk-line);border-radius:8px;background:var(--bk-bg);color:inherit;width:100%;box-sizing:border-box}
.bk-form .bk-check{display:flex;gap:8px;align-items:flex-start}.bk-form .bk-check input{width:auto;margin-top:3px}
.bk-hp{position:absolute!important;left:-9999px!important;width:1px;height:1px;overflow:hidden}
.bk-price{border:1px solid var(--bk-line);border-radius:12px;padding:14px 16px}.bk-price:empty{display:none}
.bk-price .bk-total{font-size:1.3em;font-weight:700}.bk-price .bk-no{color:#b91c1c}
.bk-cal{display:grid;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));gap:20px}
.bk-month h3{margin:0 0 8px;font-size:1em;text-align:center}
.bk-month table{width:100%;border-collapse:separate;border-spacing:3px;table-layout:fixed}
.bk-month th{font-size:.75em;color:var(--bk-muted);font-weight:500}
.bk-month td{text-align:center;padding:6px 0;border-radius:6px;font-size:.9em;background:color-mix(in srgb,#16a34a 12%,transparent)}
.bk-month td.bk-taken{background:color-mix(in srgb,#dc2626 16%,transparent);color:color-mix(in srgb,currentColor 55%,transparent);text-decoration:line-through}
.bk-month td.bk-past{background:transparent;color:var(--bk-muted);opacity:.5}
.bk-month td.bk-empty{background:transparent}
.bk-month td.bk-free{cursor:pointer}.bk-month td.bk-sel{background:var(--bk-accent);color:#fff}
.bk-legend{display:flex;gap:16px;flex-wrap:wrap;font-size:.85em;color:var(--bk-muted);margin-top:10px}
.bk-legend i{display:inline-block;width:12px;height:12px;border-radius:3px;margin-right:6px;vertical-align:-1px}
.bk-units{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:22px}
.bk-unit{border:1px solid var(--bk-line);border-radius:14px;overflow:hidden;display:flex;flex-direction:column;background:var(--bk-bg)}
.bk-unit img{width:100%;aspect-ratio:4/3;object-fit:cover;display:block}
.bk-unit-body{padding:14px 16px;display:grid;gap:6px;flex:1}.bk-unit h3{margin:0;font-size:1.15em}
.bk-unit .bk-from{font-weight:700}.bk-unit .bk-btn{justify-self:start;margin-top:6px}
""";
}
