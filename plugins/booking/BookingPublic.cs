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
        b.Route(RequestPath, BookingFederation.Pushing(RequestAsync), new PublicRouteOptions { Methods = PluginMethod.Post });   // a request reaches the central inbox at once
        b.Route(BookingFlow.GuestPath + "{key}", BookingFederation.Pushing(GuestAsync), new PublicRouteOptions { Antiforgery = true });
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
            var (people, err) = BookingCheckin.Read(ctx.Request, profile, count, ctx.T);
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
        var body = "<p class=\"bk-kicker\">" + B.H(house?.Name ?? ctx.Site.Name) + " · " + ctx.T("booking.public.number", B.H(b.Number)) + "</p><h1>" + ctx.T("booking.public.checkinTitle") + "</h1>"
            + "<p>" + B.H(unit?.Name ?? "") + ", " + B.Date(b.Arrival) + " – " + B.Date(b.Departure) + ". " + ctx.T("booking.public.checkinIntro") + "</p>"
            + BookingCheckin.Form(profile, count, prefill, row?.Text("arrival_time") ?? "", row?.Text("travel") ?? "", ctx.Antiforgery, problem, ctx.T)
            + "<p><a href=\"" + B.H(back) + "\">" + ctx.T("booking.public.backToBooking") + "</a></p>";
        return Shell(ctx.T("booking.public.checkinTitle"), body);
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
        string Back(string message) => "<h1>" + ctx.T("booking.public.notSent") + "</h1><p class=\"bk-alert\">" + B.H(message) + "</p><p><a class=\"btn btn-outline bk-btn bk-btn-ghost\" href=\""
            + B.H(SafeBack(f.Header("Referer"), ctx)) + "\">" + ctx.T("booking.public.backToForm") + "</a></p>";
        var title = ctx.T("booking.public.requestTitle");
        // a bot fills every field it sees — this one is hidden from people
        if (f.F("website").Length > 0) return Shell(ctx.T("booking.public.thanksTitle"), "<h1>" + ctx.T("booking.public.thanks") + "</h1><p>" + ctx.T("booking.public.received") + "</p>");
        var store = new BookingStore(ctx.Data);
        var unit = await store.UnitBySlugAsync(B.Slug(f.F("einheit")), ct);
        if (unit is null || !unit.Active) return Shell(title, Back(ctx.T("booking.public.chooseUnit")));
        // the date picker posts one field "zeitraum" (YYYY-MM-DD..YYYY-MM-DD); without script the form has the two plain date fields
        var (rawFrom, rawTo) = f.F("zeitraum").Split("..") is [var zf, var zt] ? (zf, zt) : (f.F("anreise"), f.F("abreise"));
        if (B.Day(rawFrom) is not { } arrival || B.Day(rawTo) is not { } departure) return Shell(title, Back(ctx.T("booking.public.datesNeeded")));
        if (arrival < B.Today) return Shell(title, Back(ctx.T("booking.public.pastArrival")));
        var name = B.Clip(f.F("name"), 120);
        var email = B.Clip(f.F("email"), 200);
        if (name.Length < 2) return Shell(title, Back(ctx.T("booking.public.nameNeeded")));
        if (!MailRe().IsMatch(email)) return Shell(title, Back(ctx.T("booking.public.emailNeeded")));
        if (!B.Checked(f.F("consent"))) return Shell(title, Back(ctx.T("booking.public.consentNeeded")));
        int N(string k, int def) => int.TryParse(f.F(k), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 50) : def;
        var fees = await store.FeesAsync(unit.PropertyId, ct);
        var extras = fees.Where(x => x.Optional && B.Checked(f.F("extra_" + x.Id))).Select(x => x.Id).ToList();
        var stay = new Stay(arrival, departure, Math.Max(1, N("erwachsene", 2)), N("kinder", 0), extras);

        Booking booking; string key;
        try
        {
            (booking, key) = await BookingFlow.RequestAsync(ctx.Data, new BookingFlow.RequestInput(unit, stay, name, email, f.F("telefon"), f.F("nachricht"), ctx.Locale),
                BookingPlugin.HoldHours(ctx), BookingPlugin.Currency(ctx), ct, tr: ctx.T);
        }
        catch (BookingException ex) { return Shell(title, Back(ex.Text(ctx))); }

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
        string T(string k) => ctx.T(k);
        var (title, lead) = b.Status switch
        {
            BookingStatus.Request => (T("booking.public.yourRequest"), ctx.Request.Q("neu") == "1"
                ? ctx.T("booking.public.leadNew", When(ctx, b.HoldUntil))
                : ctx.T("booking.public.leadRequest", When(ctx, b.HoldUntil))),
            BookingStatus.Offer => (T("booking.public.ourOffer"), b.OfferUntil < DateTime.UtcNow ? T("booking.public.offerExpired") : ctx.T("booking.public.leadOffer", When(ctx, b.OfferUntil))),
            BookingStatus.Confirmed => (T("booking.public.yourBooking"), T("booking.public.leadConfirmed")),
            BookingStatus.Declined => (T("booking.public.yourRequest"), T("booking.public.leadDeclined")),
            BookingStatus.Expired => (T("booking.public.yourRequest"), T("booking.public.leadExpired")),
            BookingStatus.Cancelled => (T("booking.public.yourBooking"), T("booking.public.leadCancelled")),
            _ => (T("booking.public.yourBooking"), "")
        };
        sb.Append("<p class=\"bk-kicker\">").Append(B.H(house?.Name ?? ctx.Site.Name)).Append(" · ").Append(ctx.T("booking.public.number", B.H(b.Number))).Append("</p>");
        sb.Append("<h1>").Append(B.H(title)).Append("</h1>");
        sb.Append("<p class=\"bk-status bk-status-").Append(B.H(b.Status)).Append("\">").Append(B.H(lead)).Append("</p>");
        sb.Append("<div class=\"bk-card\"><h2>").Append(B.H(unit?.Name ?? "")).Append("</h2><dl class=\"bk-facts\">")
          .Append("<dt>").Append(T("booking.public.arrival")).Append("</dt><dd>").Append(B.Weekday(ctx.T, b.Arrival)).Append(' ').Append(B.Date(b.Arrival)).Append(house?.CheckIn is { Length: > 0 } ci ? ctx.T("booking.public.fromTime", B.H(ci)) : "").Append("</dd>")
          .Append("<dt>").Append(T("booking.public.departure")).Append("</dt><dd>").Append(B.Weekday(ctx.T, b.Departure)).Append(' ').Append(B.Date(b.Departure)).Append(house?.CheckOut is { Length: > 0 } co ? ctx.T("booking.public.untilTime", B.H(co)) : "").Append("</dd>")
          .Append("<dt>").Append(T("booking.public.nights")).Append("</dt><dd>").Append(b.Nights).Append("</dd><dt>").Append(T("booking.public.guests")).Append("</dt><dd>").Append(B.H(Persons(ctx.T, b.Adults, b.Children))).Append("</dd></dl>");
        sb.Append(Bill(b.Lines, b.Total, cur, ctx.T)).Append("</div>");

        if (b.Status == BookingStatus.Offer && b.OfferUntil >= DateTime.UtcNow)
            sb.Append("<form method=\"post\" class=\"bk-accept\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"accept\">")
              .Append("<p>").Append(T("booking.public.acceptNote")).Append("</p><button class=\"btn bk-btn\" type=\"submit\">").Append(T("booking.public.acceptButton")).Append("</button></form>");
        if (b.Status == BookingStatus.Confirmed)
        {
            var plan = await BookingPayments.PlanAsync(ctx, b, ct);
            var two = plan.Deposit > 0 && plan.Deposit < plan.Total;
            string Due(long amount, DateOnly day) => ctx.T("booking.public.dueBy", B.H(B.Money(amount, cur)), B.Date(day));
            sb.Append("<div class=\"bk-card\"><h2>").Append(T("booking.public.payment")).Append("</h2><dl class=\"bk-facts\">");
            if (two) sb.Append("<dt>").Append(T("booking.public.deposit")).Append("</dt><dd>").Append(Due(plan.Deposit, plan.DepositDue)).Append("</dd>")
                       .Append("<dt>").Append(T("booking.public.rest")).Append("</dt><dd>").Append(Due(plan.Rest, plan.RestDue)).Append("</dd>");
            else sb.Append("<dt>").Append(T("booking.public.totalAmount")).Append("</dt><dd>").Append(Due(plan.Total, plan.DepositDue)).Append("</dd>");
            sb.Append("<dt>").Append(T("booking.public.paid")).Append("</dt><dd>").Append(B.H(B.Money(plan.Paid, cur))).Append("</dd><dt>").Append(T("booking.public.open")).Append("</dt><dd><strong>").Append(B.H(B.Money(plan.Open, cur))).Append("</strong></dd></dl>");
            if (ctx.Settings.Get(BookingPlugin.PaymentInfoKey) is { Length: > 0 } pay)
                sb.Append("<p class=\"bk-pre\">").Append(B.H(pay)).Append("</p><p class=\"bk-muted\">").Append(ctx.T("booking.public.reference", B.H(b.Number))).Append("</p>");
            sb.Append("<p><a class=\"btn btn-outline bk-btn bk-btn-ghost\" href=\"").Append(B.H(self + "/pdf")).Append("\">").Append(T("booking.public.pdf")).Append("</a></p></div>");

            if (b.Departure >= B.Today)
            {
                var row = await ctx.Data.Table(BookingStore.TBookings).GetAsync(b.Id, ct);
                var done = row?.Timestamp("checkin_at") is not null;
                sb.Append("<div class=\"bk-card\"><h2>").Append(T("booking.public.checkinTitle")).Append("</h2>");
                if (ctx.Request.Q("checkin") == "1") sb.Append("<p class=\"bk-status bk-status-confirmed\">").Append(T("booking.public.checkinThanks")).Append("</p>");
                sb.Append(done ? "<p>" + T("booking.public.checkinDone") + (row!.Text("arrival_time") is { Length: > 0 } at ? " " + ctx.T("booking.public.arrivalAt", B.H(at)) : "") + ".</p>" : "<p>" + T("booking.public.checkinPrompt") + "</p>");
                if (b.Arrival >= B.Today)
                    sb.Append("<p><a class=\"btn bk-btn").Append(done ? " btn-outline bk-btn-ghost" : "").Append("\" href=\"").Append(B.H(self + "/checkin")).Append("\">")
                      .Append(done ? T("booking.public.checkinChange") : T("booking.public.checkinNow")).Append("</a></p>");
                sb.Append("</div>");

                // how to get there and in: directions and rules always; the way in (code, key box) and the Wi-Fi only from the day before arrival
                var near = B.Today >= b.Arrival.AddDays(-1);
                var parts = new List<(string, string)>();
                if (house?.Directions is { Length: > 0 } dir) parts.Add((T("booking.public.directions"), dir));
                if (near && unit?.AccessInfo is { Length: > 0 } acc) parts.Add((T("booking.public.access"), acc));
                if (near && unit?.Wifi is { Length: > 0 } wifi) parts.Add((T("booking.public.wifi"), wifi));
                if (house?.HouseRules is { Length: > 0 } rules) parts.Add((T("booking.public.houseRules"), rules));
                if (parts.Count > 0 || unit?.AccessInfo is { Length: > 0 } || unit?.Wifi is { Length: > 0 })
                {
                    sb.Append("<div class=\"bk-card\"><h2>").Append(T("booking.public.yourArrival")).Append("</h2>");
                    foreach (var (h, text) in parts) sb.Append("<h3>").Append(B.H(h)).Append("</h3><p class=\"bk-pre\">").Append(B.H(text)).Append("</p>");
                    if (!near && unit?.AccessInfo is { Length: > 0 }) sb.Append("<p class=\"bk-muted\">").Append(T("booking.public.accessLater")).Append("</p>");
                    sb.Append("</div>");
                }
            }
        }
        sb.Append("<p class=\"bk-muted\">").Append(T("booking.public.linkOnly")).Append("</p>");
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
    internal static string Bill(IReadOnlyList<QuoteLine> lines, long total, string cur, Tr? tr = null)
    {
        var sb = new StringBuilder("<table class=\"bk-bill\"><tbody>");
        foreach (var l in lines)
            sb.Append("<tr><td>").Append(B.H(l.Text)).Append(l.Detail.Length > 0 ? "<small>" + B.H(l.Detail) + "</small>" : "").Append("</td><td>").Append(B.H(B.Money(l.Amount, cur))).Append("</td></tr>");
        sb.Append("</tbody><tfoot><tr><td>").Append((tr ?? BookingStrings.German)("booking.public.total")).Append("</td><td>").Append(B.H(B.Money(total, cur))).Append("</td></tr></tfoot></table>");
        return sb.ToString();
    }

    /// <summary>"2 Erwachsene, 1 Kind" in the language of the request.</summary>
    internal static string Persons(Tr t, int adults, int children) =>
        (adults == 1 ? t("booking.public.adult") : t("booking.public.adults", adults))
        + (children > 0 ? ", " + (children == 1 ? t("booking.public.child") : t("booking.public.children", children)) : "");

    /// <summary>A moment in the houses' time ("12.08.2026 18:00 Uhr" in German, without "Uhr" in English).</summary>
    private static string When(IPluginContext ctx, DateTime? utc)
    {
        var s = BookingMail.Local(utc);
        return s.EndsWith(" Uhr", StringComparison.Ordinal) ? ctx.T("booking.public.time", s[..^4]) : s;
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
@media(max-width:640px){.bk-row-stay{grid-template-columns:1fr 1fr}.bk-row-stay .bk-dates{grid-column:1/-1}.bk-form button.bk-btn{width:100%}}
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
