using System.Globalization;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// The stays — a guest's request, the host's offer, a booking — for agents with the right to personal data (every op here is <c>PersonalData</c>:
/// names, addresses, messages; never offered to the site's AI assistant, whose answers go to an outside model). Reading only, and moving a stay
/// along through <see cref="BookingFlow.MoveAsync"/> / <see cref="BookingFlow.OfferAsync"/> — the one place that checks the allowed moves and frees the
/// nights; the guest mails are the admin's own (<see cref="BookingAdminBookings.SendAsync"/>). The registration data of the travellers never comes out.
/// A move has no undo: the way back is not a move the flow allows, and the freed nights may be taken by then.
/// </summary>
internal static partial class BookingOps
{
    public static readonly PluginOperationSpec StayList = new("stay.list",
        "Anfragen und Buchungen auflisten (nach Anreise): id, Nummer, Status (request = Anfrage, offer = Angebot verschickt, confirmed = gebucht, declined, expired, cancelled), " +
        "Einheit, Anreise, Abreise, Personen, Betrag und bezahlt in Cent, Gast (Name, E-Mail), Quelle. open: nur offene (Anfrage/Angebot); from/to: Aufenthalte, die den Zeitraum berühren.",
        """{"status"?: "request" oder ["request","offer"], "open"?: true, "from"?: "2027-07-01", "to"?: "2027-08-31", "unit"?: 3, "search"?: "Name, E-Mail oder Nummer", "limit"?: 50}""",
        Writes: false, PersonalData: true);
    public static readonly PluginOperationSpec StayRead = new("stay.read",
        "Eine Anfrage oder Buchung vollständig lesen: Rechnung (Zeilen in Cent), Gast mit Telefon und Nachricht, interne Notiz, Fristen, Zahlungen und Zahlungsplan, Verlauf. Ohne Meldedaten der Reisenden.",
        """{"stay": 12} oder {"number": "2027-0004"}""", Writes: false, PersonalData: true);
    public static readonly PluginOperationSpec StayMove = new("stay.move",
        "Eine Anfrage oder Buchung weiterschalten (wie die Knöpfe im Admin): to = offer (Angebot, optional adjust_cents als Rabatt < 0 oder Aufschlag > 0 mit adjust_text, gültig valid_days Tage — " +
        "schickt dem Gast IMMER die Angebotsmail mit seinem Link), confirmed (bestätigen), declined (ablehnen, reason), cancelled (eine Buchung stornieren, reason; gibt die Nächte frei). " +
        "Bei confirmed/declined/cancelled geht eine Mail an den Gast NUR mit notify: true. Erlaubt: Anfrage → Angebot/bestätigt/abgelehnt/storniert, Angebot → bestätigt/abgelehnt/storniert, bestätigt → storniert. Nicht rückgängig zu machen.",
        """{"stay": 12 oder "number": "2027-0004", "to": "offer|confirmed|declined|cancelled", "notify"?: false, "reason"?: "…", "adjust_cents"?: -5000, "adjust_text"?: "Stammgast-Rabatt", "valid_days"?: 3}""",
        PersonalData: true);

    private static async Task<Booking> StayOfAsync(IPluginContext ctx, JsonObject o, CancellationToken ct)
    {
        if ((Whole(o, "stay") ?? Whole(o, "id")) is { } id)
            return await BookingFlow.GetAsync(ctx.Data, id, ct) ?? throw new Refused($"Anfrage/Buchung {id} nicht gefunden.");
        if (Str(o, "number")?.Trim() is { Length: > 0 } number)
            return await ctx.Data.Table(BookingStore.TBookings).FindAsync(DataQuery.Everything.Eq("number", number), ct) is { } row
                ? BookingFlow.Map(row) : throw new Refused($"Buchung {number} nicht gefunden.");
        throw new Refused("Bitte die Anfrage/Buchung angeben (stay: id oder number).");
    }

    private static JsonObject StayShort(Booking b, IReadOnlyDictionary<long, Unit> units, IReadOnlyDictionary<long, long> paid) => new()
    {
        ["id"] = b.Id, ["number"] = b.Number, ["status"] = b.Status, ["unit"] = b.UnitId, ["unit_name"] = units.TryGetValue(b.UnitId, out var u) ? u.Name : "",
        ["arrival"] = B.Iso(b.Arrival), ["departure"] = B.Iso(b.Departure), ["nights"] = b.Nights, ["adults"] = b.Adults, ["children"] = b.Children,
        ["total_cents"] = b.Total, ["paid_cents"] = paid.GetValueOrDefault(b.Id), ["name"] = b.Name, ["email"] = b.Email, ["source"] = b.Source,
        ["created"] = b.Created?.ToString("o", CultureInfo.InvariantCulture),
    };

    private static Task<JsonNode?> StayListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var o = Args(args);
        var statuses = o["status"] switch
        {
            null => null,
            JsonArray a => a.Select(x => x?.ToString() ?? "").ToList(),
            var n => new List<string> { n.ToString() }
        };
        if (statuses?.FirstOrDefault(s => !BookingStatus.Known(s)) is { } bad)
            return Answer("failed", $"Unbekannter Status „{bad}“: request, offer, confirmed, declined, expired oder cancelled.");
        if (Flag(o, "open") == true) statuses = new List<string> { BookingStatus.Request, BookingStatus.Offer };
        DateOnly? from = o.ContainsKey("from") ? B.Day(Day(o, "from")) : null, to = o.ContainsKey("to") ? B.Day(Day(o, "to")) : null;
        var store = new BookingStore(ctx.Data);
        var units = (await store.UnitsAsync(ct)).ToDictionary(u => u.Id);
        long? unit = Str(o, "unit") is { Length: > 0 } key ? UnitOf(units.Values.ToList(), key).Id : null;
        var search = (Str(o, "search") ?? "").Trim();
        var limit = IntIn(o, "limit", 1, 500) ?? 50;
        var paid = await BookingPayments.PaidAsync(ctx.Data, ct);
        var hits = (await BookingFlow.ListAsync(ctx.Data, statuses, ct))
            .Where(b => unit is null || b.UnitId == unit)
            .Where(b => from is null || b.Departure > from.Value)
            .Where(b => to is null || b.Arrival <= to.Value)
            .Where(b => search.Length == 0 || (b.Number + " " + b.Name + " " + b.Email).Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Answer("applied", $"{hits.Count} Anfrage(n)/Buchung(en), Beträge in Cent {BookingPlugin.Currency(ctx)}" + (hits.Count > limit ? $", die ersten {limit}." : "."),
            new JsonArray(hits.Take(limit).Select(b => (JsonNode)StayShort(b, units, paid)).ToArray()));
    });

    private static Task<JsonNode?> StayReadAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var b = await StayOfAsync(ctx, Args(args), ct);
        var store = new BookingStore(ctx.Data);
        var units = (await store.UnitsAsync(ct)).ToDictionary(u => u.Id);
        var data = StayShort(b, units, await BookingPayments.PaidAsync(ctx.Data, ct));
        var house = units.TryGetValue(b.UnitId, out var u) ? await store.PropertyAsync(u.PropertyId, ct) : null;
        data["house"] = house?.Id; data["house_name"] = house?.Name ?? "";
        data["phone"] = b.Phone; data["message"] = b.Message; data["host_note"] = b.HostNote; data["locale"] = b.Locale; data["guest_card"] = b.GuestId;
        data["hold_until"] = b.Status == BookingStatus.Request ? b.HoldUntil?.ToString("o", CultureInfo.InvariantCulture) : null;
        data["offer_until"] = b.Status == BookingStatus.Offer ? b.OfferUntil?.ToString("o", CultureInfo.InvariantCulture) : null;
        data["confirmed_at"] = b.ConfirmedAt?.ToString("o", CultureInfo.InvariantCulture);
        data["extras"] = new JsonArray(b.Extras.Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
        data["lines"] = new JsonArray(b.Lines.Select(l => (JsonNode)new JsonObject
        {
            ["kind"] = l.Kind, ["text"] = l.Text, ["quantity"] = l.Quantity, ["unit_cents"] = l.Unit, ["amount_cents"] = l.Amount, ["detail"] = l.Detail
        }).ToArray());
        data["payments"] = new JsonArray((await BookingPayments.ListAsync(ctx.Data, b.Id, ct)).Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id, ["day"] = B.Iso(p.Day), ["amount_cents"] = p.Amount, ["method"] = p.Method, ["note"] = p.Note
        }).ToArray());
        if (b.Status == BookingStatus.Confirmed)
        {
            var plan = await BookingPayments.PlanAsync(ctx, b, ct);
            data["plan"] = new JsonObject
            {
                ["deposit_cents"] = plan.Deposit, ["deposit_due"] = B.Iso(plan.DepositDue), ["rest_cents"] = plan.Rest, ["rest_due"] = B.Iso(plan.RestDue),
                ["paid_cents"] = plan.Paid, ["open_cents"] = plan.Open
            };
        }
        data["history"] = new JsonArray(b.History.Select(h => (JsonNode)new JsonObject { ["at"] = h.At.ToString("o", CultureInfo.InvariantCulture), ["text"] = h.Text }).ToArray());
        data["currency"] = BookingPlugin.Currency(ctx);
        return Answer("applied", $"{b.Number}: {BookingStatus.Label(b.Status)}, {b.Name}", data);
    });

    /// <summary>The admin's buttons, nothing more: the same flow calls, the same history line (marked as coming from an operation), the same mails —
    /// the offer always, the others only when asked — and the house hands the change on to its central site at once, as after a POST in the admin.</summary>
    private static Task<JsonNode?> StayMoveAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var o = Args(args);
        var b = await StayOfAsync(ctx, o, ct);
        var to = (Str(o, "to") ?? "").Trim().ToLowerInvariant();
        var notify = Flag(o, "notify") ?? false;
        var reason = B.Clip(Str(o, "reason"), 1000);
        var store = new BookingStore(ctx.Data);
        const string Via = " (über eine Aktion)";
        string mail = "";
        try
        {
            switch (to)
            {
                case BookingStatus.Offer:
                {
                    var adjust = Whole(o, "adjust_cents") ?? 0;
                    b = await BookingFlow.OfferAsync(ctx.Data, b, adjust, B.Clip(Str(o, "adjust_text"), 120), IntIn(o, "valid_days", 1, 30) ?? BookingPlugin.OfferDays(ctx), ct, ctx.T);
                    var key = await BookingFlow.NewKeyAsync(ctx.Data, b.Id, ct);
                    mail = await BookingAdminBookings.SendAsync(ctx, store, b, BookingMail.Offer, key, null, ct);
                    await BookingFlow.NoteAsync(ctx.Data, b.Id, "Angebot an den Gast" + Via + ": " + mail, ct);
                    break;
                }
                case BookingStatus.Confirmed:
                    b = await BookingFlow.MoveAsync(ctx.Data, b.Id, BookingStatus.Confirmed, "Vom Gastgeber bestätigt" + Via, null, ct);
                    if (notify)
                    {
                        var key = await BookingFlow.NewKeyAsync(ctx.Data, b.Id, ct);
                        mail = await BookingAdminBookings.SendAsync(ctx, store, b, BookingMail.Confirmed, key, null, ct);
                        await BookingFlow.NoteAsync(ctx.Data, b.Id, "Bestätigung an den Gast: " + mail, ct);
                    }
                    break;
                case BookingStatus.Declined:
                    b = await BookingFlow.MoveAsync(ctx.Data, b.Id, BookingStatus.Declined, "Abgelehnt" + Via + (reason.Length > 0 ? ": " + reason : ""), null, ct);
                    if (notify)
                    {
                        mail = await BookingAdminBookings.SendAsync(ctx, store, b, BookingMail.Declined, null, reason, ct);
                        await BookingFlow.NoteAsync(ctx.Data, b.Id, "Absage an den Gast: " + mail, ct);
                    }
                    break;
                case BookingStatus.Cancelled:
                    b = await BookingFlow.MoveAsync(ctx.Data, b.Id, BookingStatus.Cancelled, "Storniert" + Via + (reason.Length > 0 ? ": " + reason : ""), null, ct);
                    if (notify)
                    {
                        mail = await BookingAdminBookings.SendAsync(ctx, store, b, BookingMail.Cancelled, null, reason, ct);
                        await BookingFlow.NoteAsync(ctx.Data, b.Id, "Stornierung an den Gast: " + mail, ct);
                    }
                    break;
                default:
                    return Answer("failed", "to: offer, confirmed, declined oder cancelled.");
            }
        }
        catch (BookingException e) { return Answer("failed", e.Text(ctx)); }
        ctx.Log($"Buchung {b.Number} → {b.Status} über eine Operation");
        await BookingFederation.PushNowAsync(ctx, ct);
        return Answer("applied", $"{b.Number}: jetzt {BookingStatus.Label(b.Status)}" + (mail.Length > 0 ? $" — Mail an den Gast: {mail}" : "") + ". Nicht rückgängig zu machen.",
            new JsonObject { ["id"] = b.Id, ["number"] = b.Number, ["status"] = b.Status, ["total_cents"] = b.Total });
    });
}
