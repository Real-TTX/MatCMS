using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// Admin → Buchungen → Belegungsplan (units × the days of a month, bookings as bars by state, blocks hatched, a free day opens "Neue Buchung") and
/// → Gäste (the card index: contact, notes, every stay, turnover; erasing a guest anonymises the stays — they stay for the books — and kills the links).
/// </summary>
public static class BookingAdminMore
{
    private static string R => BookingAdmin.Root;

    public static IAdminSection Register(IAdminSection s) => s
        .Page("plan", null, PlanAsync)
        .Page("guests", null, GuestsAsync)
        .Page("guests/{id}", null, GuestAsync);

    // ---- the occupancy chart ----------------------------------------------------------------------------------------

    private static async Task<PluginResult> PlanAsync(IPluginContext ctx, CancellationToken ct)
    {
        var today = B.Today;
        var first = B.Day(ctx.Request.Q("monat") + "-01") is { } m ? new DateOnly(m.Year, m.Month, 1) : new DateOnly(today.Year, today.Month, 1);
        var end = first.AddMonths(1);
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        var units = (await store.UnitsAsync(ct)).Where(u => u.Active).ToList();
        var nights = await store.NightsAsync(units.Select(u => u.Id), first, end, ct);
        var bookings = (await BookingFlow.ListAsync(ctx.Data, null, ct)).ToDictionary(b => b.Id);
        var blocks = new Dictionary<long, Block>();
        foreach (var u in units) foreach (var bl in await store.BlocksAsync(u.Id, ct)) blocks[bl.Id] = bl;
        string Month(DateOnly d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        var sb = new StringBuilder("<style>").Append(Css).Append("</style><div class=\"bk-plan-wrap\"><table class=\"bk-plan\"><thead><tr><th class=\"bk-plan-unit\"></th>");
        for (var d = first; d < end; d = d.AddDays(1))
            sb.Append("<th class=\"").Append(d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "we" : "").Append(d == today ? " today" : "").Append("\"><small>")
              .Append(B.Weekday(d)[..1]).Append("</small>").Append(d.Day).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var house in houses)
        {
            var mine = units.Where(u => u.PropertyId == house.Id).ToList();
            if (mine.Count == 0) continue;
            if (houses.Count > 1) sb.Append("<tr class=\"bk-plan-house\"><th colspan=\"").Append(end.DayNumber - first.DayNumber + 1).Append("\">").Append(B.H(house.Name)).Append("</th></tr>");
            foreach (var u in mine)
            {
                sb.Append("<tr><th class=\"bk-plan-unit\"><a href=\"").Append(R).Append("units/").Append(u.Id).Append("\">").Append(B.H(u.Name)).Append("</a></th>");
                var byDay = nights.Where(n => n.UnitId == u.Id).GroupBy(n => n.Day).ToDictionary(g => g.Key, g => g.First());
                var d = first;
                while (d < end)
                {
                    // a "whole house" is not bookable while one of its apartments is taken — shown as a hatched bar, not as free
                    if (!byDay.ContainsKey(d) && u.Contains.Count > 0 && nights.Any(x => x.Day == d && u.Contains.Contains(x.UnitId)))
                    {
                        var run = 0;
                        while (d.AddDays(run) < end && !byDay.ContainsKey(d.AddDays(run)) && nights.Any(x => x.Day == d.AddDays(run) && u.Contains.Contains(x.UnitId))) run++;
                        sb.Append("<td colspan=\"").Append(run).Append("\" class=\"bar part\" title=\"Nicht buchbar: ein Teil des Hauses ist belegt\"><span>Teil belegt</span></td>");
                        d = d.AddDays(run);
                        continue;
                    }
                    if (!byDay.TryGetValue(d, out var n))
                    {
                        sb.Append("<td class=\"free").Append(d < today ? " past" : "").Append(d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? " we" : "").Append("\">");
                        if (d >= today) sb.Append("<a href=\"").Append(R).Append("bookings/new?unit=").Append(u.Id).Append("&amp;arrival=").Append(B.Iso(d)).Append("\" title=\"Neue Buchung ab ").Append(B.Date(d)).Append("\"></a>");
                        sb.Append("</td>");
                        d = d.AddDays(1);
                        continue;
                    }
                    // one bar for the run of nights of the same booking or block
                    var span = 0;
                    while (d.AddDays(span) < end && byDay.TryGetValue(d.AddDays(span), out var x) && x.BookingId == n.BookingId && x.BlockId == n.BlockId) span++;
                    if (n.BookingId > 0 && bookings.TryGetValue(n.BookingId, out var b))
                    {
                        var via = n.Origin != u.Id ? " (über " + (units.FirstOrDefault(o => o.Id == n.Origin)?.Name ?? "andere Einheit") + ")" : "";
                        sb.Append("<td colspan=\"").Append(span).Append("\" class=\"bar ").Append(B.H(b.Status)).Append("\"><a href=\"").Append(R).Append("bookings/").Append(b.Id)
                          .Append("\" title=\"").Append(B.H(b.Number + " · " + b.Name + " · " + B.Date(b.Arrival) + "–" + B.Date(b.Departure) + " · " + BookingStatus.Label(b.Status) + via)).Append("\">")
                          .Append(B.H(b.Name.Length > 0 ? b.Name : b.Number)).Append("</a></td>");
                    }
                    else
                    {
                        var reason = blocks.TryGetValue(n.BlockId, out var bl) ? bl.Reason : "";
                        var clash = bl?.Conflict ?? "";
                        sb.Append("<td colspan=\"").Append(span).Append("\" class=\"bar block").Append(bl?.Source.StartsWith("feed:", StringComparison.Ordinal) == true ? " feed" : "").Append(clash.Length > 0 ? " conflict" : "")
                          .Append("\" title=\"").Append(B.H((clash.Length > 0 ? clash : "Gesperrt" + (reason.Length > 0 ? ": " + reason : "")))).Append("\"><span>")
                          .Append(B.H(reason.Length > 0 ? reason : "gesperrt")).Append("</span></td>");
                    }
                    d = d.AddDays(span);
                }
                sb.Append("</tr>");
            }
        }
        sb.Append("</tbody></table></div><p class=\"bk-plan-legend\"><span class=\"request\">Anfrage</span><span class=\"offer\">Angebot</span><span class=\"confirmed\">bestätigt</span><span class=\"block\">gesperrt</span>")
          .Append("<span>Ein Klick auf einen freien Tag legt eine Buchung ab diesem Tag an.</span></p>");
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Alle Einheiten über einen Monat – jede Spalte ist eine Nacht", "Belegungsplan " + B.Month(first),
                Ui.Button("‹ " + B.MonthNames[first.AddMonths(-1).Month - 1], R + "plan?monat=" + Month(first.AddMonths(-1)), UiButtonKind.Ghost, small: true),
                Ui.Button("Heute", R + "plan", UiButtonKind.Ghost, small: true),
                Ui.Button(B.MonthNames[end.Month - 1] + " ›", R + "plan?monat=" + Month(end), UiButtonKind.Ghost, small: true)),
            units.Count == 0 ? Ui.Empty("Noch keine buchbare Einheit.") : Ui.Html(sb.ToString())), "Belegungsplan");
    }

    /// <summary>The chart's look — admin variables only, so it follows light and dark.</summary>
    private const string Css = """
.bk-plan-wrap{overflow-x:auto;border:1px solid var(--line);border-radius:10px;background:var(--surface)}
.bk-plan{border-collapse:collapse;width:100%;table-layout:fixed;font-size:12px;min-width:900px}
.bk-plan th,.bk-plan td{border-left:1px solid var(--line);border-bottom:1px solid var(--line);padding:0;height:34px;text-align:center}
.bk-plan thead th{font-weight:600;color:var(--text-2);height:38px;line-height:1.1}.bk-plan thead th small{display:block;font-weight:400;color:var(--text-muted)}
.bk-plan th.we,.bk-plan td.free.we{background:var(--surface-2)}.bk-plan th.today{color:var(--accent);box-shadow:inset 0 -2px 0 var(--accent)}
.bk-plan .bk-plan-unit{width:170px;text-align:left;padding:0 10px;position:sticky;left:0;background:var(--surface);z-index:1;border-left:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.bk-plan .bk-plan-house th{text-align:left;padding:6px 10px;background:var(--surface-2);font-weight:700}
.bk-plan td.free a{display:block;height:100%;min-height:34px}.bk-plan td.free a:hover{background:var(--hover)}.bk-plan td.past{opacity:.45}
.bk-plan td.bar{padding:3px 2px}.bk-plan td.bar a,.bk-plan td.bar span{display:block;border-radius:6px;padding:6px 6px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;text-decoration:none;font-weight:600}
.bk-plan td.request a{background:var(--warn-bg);color:var(--warn-fg);border:1px solid var(--warn-line)}
.bk-plan td.offer a{background:var(--info-bg);color:var(--info-fg);border:1px solid var(--info-line)}
.bk-plan td.confirmed a{background:var(--ok-bg);color:var(--ok-fg);border:1px solid var(--ok-line)}
.bk-plan td.feed span{background:var(--surface-2);border-style:solid;color:var(--text-2);font-weight:600}
.bk-plan td.conflict span{background:var(--err-bg);color:var(--err-fg);border:1px solid var(--err-line);font-weight:600}
.bk-plan td.part span{background:repeating-linear-gradient(135deg,transparent 0 5px,var(--hover) 5px 10px);color:var(--text-muted);border:1px dashed var(--line);font-weight:500}
.bk-plan td.block span{background:repeating-linear-gradient(135deg,var(--surface-2) 0 6px,var(--hover) 6px 12px);color:var(--text-2);border:1px dashed var(--border-strong);font-weight:500}
.bk-plan-legend{display:flex;flex-wrap:wrap;gap:14px;font-size:12px;color:var(--text-muted);margin-top:10px}
.bk-plan-legend span.request,.bk-plan-legend span.offer,.bk-plan-legend span.confirmed,.bk-plan-legend span.block{padding:2px 8px;border-radius:6px}
.bk-plan-legend .request{background:var(--warn-bg);color:var(--warn-fg)}.bk-plan-legend .offer{background:var(--info-bg);color:var(--info-fg)}
.bk-plan-legend .confirmed{background:var(--ok-bg);color:var(--ok-fg)}.bk-plan-legend .block{background:var(--surface-2);color:var(--text-2);border:1px dashed var(--border-strong)}
""";

    // ---- guests -----------------------------------------------------------------------------------------------------

    private sealed record Guest(long Id, string Name, string Email, string Phone, string Country, string Note);

    private static Guest G(DataRow r) => new(r.Id, r.Text("name"), r.Text("email"), r.Text("phone"), r.Text("country"), r.Text("note"));

    private static async Task<PluginResult> GuestsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var guests = (await ctx.Data.Table(BookingStore.TGuests).QueryAsync(DataQuery.Everything.OrderBy("name"), ct)).Select(G).ToList();
        var bookings = await BookingFlow.ListAsync(ctx.Data, null, ct);
        var cur = BookingPlugin.Currency(ctx);
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Wer angefragt oder gebucht hat – wiedererkannt an der E-Mail-Adresse", "Gäste"),
            Ui.Table(new[] { Ui.Column("Gast"), Ui.Column("E-Mail"), Ui.Column("Telefon"), Ui.Column("Aufenthalte", right: true), Ui.Column("Zuletzt"), Ui.Column("Umsatz", right: true) },
                guests.Select(g =>
                {
                    var mine = bookings.Where(b => b.GuestId == g.Id).ToList();
                    var stays = mine.Where(b => b.Status == BookingStatus.Confirmed).ToList();
                    return Ui.RowFor(g.Name + " " + g.Email + " " + g.Phone, Ui.Link(g.Name.Length > 0 ? g.Name : g.Email, R + "guests/" + g.Id), g.Email, g.Phone,
                        B.Num(stays.Count) + (mine.Count > stays.Count ? " (+" + (mine.Count - stays.Count) + " Anfr.)" : ""),
                        mine.Count == 0 ? "–" : B.Date(mine.Max(b => b.Arrival)), B.Money(stays.Sum(b => b.Total), cur));
                }),
                searchPlaceholder: "Name, E-Mail oder Telefon …", emptyText: "Noch keine Gäste – sie kommen mit der ersten Anfrage.")), "Gäste");
    }

    private static async Task<PluginResult> GuestAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!long.TryParse(ctx.Request.R("id"), out var id) || await ctx.Data.Table(BookingStore.TGuests).GetAsync(id, ct) is not { } row) return PluginResult.NotFound();
        var g = G(row);
        var self = R + "guests/" + g.Id;
        var bookings = (await BookingFlow.ListAsync(ctx.Data, null, ct)).Where(b => b.GuestId == g.Id).OrderByDescending(b => b.Arrival).ToList();
        if (ctx.Request.IsPost)
        {
            if (ctx.Request.Action == "erase")
            {
                var today = B.Today;
                if (bookings.Any(b => b.Holds && b.Departure >= today))
                    return PluginResult.Redirect(self, "Der Gast hat noch eine offene oder kommende Buchung – erst danach (oder nach einer Stornierung) löschen.", true);
                foreach (var b in bookings)
                {
                    await ctx.Data.Table(BookingStore.TBookings).UpdateAsync(b.Id, new Dictionary<string, object?>
                    {
                        ["name"] = "(gelöscht)", ["email"] = "", ["phone"] = "", ["message"] = "", ["guest_id"] = 0, ["updated"] = DateTime.UtcNow
                    }, ct);
                    await ctx.Data.Table(BookingStore.TKeys).DeleteWhereAsync(DataQuery.Everything.Eq("booking_id", b.Id), ct);
                    await BookingFlow.NoteAsync(ctx.Data, b.Id, "Daten des Gastes gelöscht (Name, Kontakt, Nachricht); die Buchung bleibt für die Buchhaltung.", ct);
                }
                await ctx.Data.Table(BookingStore.TGuests).DeleteAsync(g.Id, ct);
                return PluginResult.Redirect(R + "guests", "Gast gelöscht, " + bookings.Count + " Aufenthalt(e) anonymisiert.");
            }
            await ctx.Data.Table(BookingStore.TGuests).UpdateAsync(g.Id, BookingFederation.StampLocal(row, new Dictionary<string, object?>
            {
                ["name"] = B.Clip(ctx.Request.F("name"), 120), ["phone"] = B.Clip(ctx.Request.F("phone"), 60), ["country"] = B.Clip(ctx.Request.F("country"), 60),
                ["note"] = B.Clip(ctx.Request.F("note"), 4000), ["updated"] = DateTime.UtcNow
            }, DateTime.UtcNow), ct);
            return PluginResult.Redirect(self, "Gespeichert.");
        }
        var cur = BookingPlugin.Currency(ctx);
        var units = (await new BookingStore(ctx.Data).UnitsAsync(ct)).ToDictionary(u => u.Id);
        var form = Ui.Form(
            Ui.Grid(200, Ui.Input("name", "Name", g.Name), Ui.Input("phone", "Telefon", g.Phone, UiInputType.Tel), Ui.Input("country", "Land", g.Country)),
            Ui.TextArea("note", "Notiz (intern)", g.Note, 3) with { Help = "z. B. „Stammgast“, „kommt mit Hund“, „Allergie: Federn“" },
            Ui.FormActions(Ui.Submit("Speichern"),
                Ui.Submit("Daten löschen", UiButtonKind.Danger, "action", "erase", confirm: "Name, Kontakt und Nachrichten dieses Gastes löschen? Seine Aufenthalte bleiben anonym stehen, seine Links gehen nicht mehr.")));
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(g.Email, g.Name.Length > 0 ? g.Name : g.Email, Ui.Button("Alle Gäste", R + "guests", UiButtonKind.Ghost, "ti-arrow-left", small: true)),
            Ui.Columns(new UiNode?[]
            {
                Ui.Card("Aufenthalte", Ui.Table(new[] { Ui.Column("Nr."), Ui.Column("Unterkunft"), Ui.Column("Zeitraum"), Ui.Column("Betrag", right: true), Ui.Column("Status") },
                    bookings.Select(b => Ui.Row(Ui.Link(b.Number, R + "bookings/" + b.Id), units.TryGetValue(b.UnitId, out var u) ? u.Name : "–",
                        B.Date(b.Arrival) + " – " + B.Date(b.Departure), B.Money(b.Total, cur), Ui.Badge(BookingStatus.Label(b.Status), BookingStatus.Tone(b.Status)))),
                    emptyText: "Keine Aufenthalte."))
            }, new UiNode?[] { Ui.Card("Gast", form), await BookingAdminFederation.GuestCardAsync(ctx, g.Email, ct) })), g.Name.Length > 0 ? g.Name : g.Email);
    }
}
