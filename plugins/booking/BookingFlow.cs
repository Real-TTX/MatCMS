using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Booking;

/// <summary>A stay asked for or booked. <see cref="Lines"/>/<see cref="Total"/> are the bill as it was made (a snapshot — later price changes never touch it).</summary>
public sealed record Booking(long Id, string Number, long UnitId, long GuestId, DateOnly Arrival, DateOnly Departure, int Adults, int Children,
    IReadOnlyList<long> Extras, string Status, string Source, IReadOnlyList<QuoteLine> Lines, long Total, string Name, string Email, string Phone,
    string Message, string HostNote, string Locale, DateTime? HoldUntil, DateTime? OfferUntil, DateTime? Created, IReadOnlyList<(DateTime At, string Text)> History,
    DateTime? ConfirmedAt = null)
{
    public int Nights => Departure.DayNumber - Arrival.DayNumber;
    /// <summary>Its nights are held in the calendar (asked, offered or booked).</summary>
    public bool Holds => Status is BookingStatus.Request or BookingStatus.Offer or BookingStatus.Confirmed;
}

/// <summary>The states of a booking and the ONLY list of moves between them (<see cref="CanMove"/>).</summary>
public static class BookingStatus
{
    public const string Request = "request", Offer = "offer", Confirmed = "confirmed", Declined = "declined", Expired = "expired", Cancelled = "cancelled";

    public static bool CanMove(string from, string to) => (from, to) switch
    {
        (Request, Offer or Confirmed or Declined or Expired or Cancelled) => true,
        (Offer, Confirmed or Declined or Expired or Cancelled) => true,
        (Confirmed, Cancelled) => true,
        _ => false
    };

    public static bool Known(string s) => s is Request or Offer or Confirmed or Declined or Expired or Cancelled;
    public static string Label(string s) => Label(BookingStrings.German, s);
    public static string Label(Tr t, string s) => Known(s) ? t("booking.status." + s) : s;

    public static UiTone Tone(string s) => s switch
    {
        Request => UiTone.Warning, Offer => UiTone.Info, Confirmed => UiTone.Success, _ => UiTone.Default
    };
}

/// <summary>A refusal a guest or the host can read ("Belegt", "Die Anfrage ist abgelaufen").</summary>
/// <remarks>Carries a key of <see cref="BookingStrings"/> (its German text is the Message) or a finished text; <see cref="Text"/> gives it in the request's language.</remarks>
public sealed class BookingException(string key, params object[] args) : Exception(BookingStrings.German(key, args))
{
    public string Text(IPluginContext ctx) => BookingStrings.Has(key) ? ctx.T(key, args) : Message;

    /// <summary>The key with its arguments for <see cref="BookingStrings.Shown"/> (the finished text when it carries none) — for a message stored and read later.</summary>
    public string Packed => BookingStrings.Has(key) ? BookingStrings.Pack(key, args) : Message;
}

/// <summary>
/// The way of a booking: a guest's request holds the nights for a while, the host offers (possibly at another price) or confirms or declines, the
/// guest accepts an offer through the secret link, and what nobody answers in time runs out. EVERY change of state goes through <see cref="MoveAsync"/>
/// (the one place that checks <see cref="BookingStatus.CanMove"/>, frees the nights and writes the history); mails are sent by the caller AFTER it.
/// </summary>
public static class BookingFlow
{
    // ---- mapping ------------------------------------------------------------------------------------------------

    public static Booking Map(DataRow r)
    {
        var lines = new List<QuoteLine>();
        if (r.Json("quote") is JsonArray arr)
            foreach (var n in arr)
                if (n is JsonObject o)
                    lines.Add(new QuoteLine(Str(o, "kind"), Str(o, "text"), (int)Num(o, "quantity"), Num(o, "unit"), Num(o, "amount")) { Detail = Str(o, "detail") });
        var history = new List<(DateTime, string)>();
        if (r.Json("history") is JsonArray h)
            foreach (var n in h)
                if (n is JsonObject o && DateTime.TryParse(Str(o, "at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                    history.Add((at, Str(o, "text")));
        return new Booking(r.Id, r.Text("number"), r.Int("unit_id"), r.Int("guest_id"), B.Day(r.Text("arrival")) ?? default, B.Day(r.Text("departure")) ?? default,
            (int)r.Int("adults"), (int)r.Int("children"),
            r.Text("extras").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out var v) ? v : 0).Where(v => v > 0).ToList(),
            r.Text("status"), r.Text("source"), lines, r.Int("total"), r.Text("name"), r.Text("email"), r.Text("phone"), r.Text("message"), r.Text("host_note"),
            r.Text("locale"), r.Timestamp("hold_until"), r.Timestamp("offer_until"), r.Timestamp("created"), history, r.Timestamp("confirmed_at"));
    }

    private static string Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static long Num(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    public static string QuoteJson(IEnumerable<QuoteLine> lines)
    {
        var arr = new JsonArray();
        foreach (var l in lines)
            arr.Add(new JsonObject { ["kind"] = l.Kind, ["text"] = l.Text, ["quantity"] = l.Quantity, ["unit"] = l.Unit, ["amount"] = l.Amount, ["detail"] = l.Detail });
        return arr.ToJsonString();
    }

    private static string AddHistory(Booking? b, string text)
    {
        var arr = new JsonArray();
        foreach (var (at, t) in b?.History ?? Array.Empty<(DateTime, string)>()) arr.Add(new JsonObject { ["at"] = at.ToString("o"), ["text"] = t });
        arr.Add(new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["text"] = text });
        return arr.ToJsonString();
    }

    // ---- the secret link ----------------------------------------------------------------------------------------

    /// <summary>A new guest key: 32 random bytes, url-safe. Only its hash is stored — a stolen database opens no guest page.</summary>
    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    public const string GuestPath = "/buchung/gast/";

    // ---- reading ------------------------------------------------------------------------------------------------

    public static async Task<Booking?> GetAsync(IPluginData data, long id, CancellationToken ct = default) =>
        await data.Table(BookingStore.TBookings).GetAsync(id, ct) is { } r ? Map(r) : null;

    public static async Task<Booking?> ByKeyAsync(IPluginData data, string key, CancellationToken ct = default) =>
        key.Length is < 20 or > 100 ? null : await data.Table(BookingStore.TKeys).FindAsync(DataQuery.Everything.Eq("hash", Hash(key)), ct) is { } k ? await GetAsync(data, k.Int("booking_id"), ct) : null;

    /// <summary>A fresh secret link for a booking (for the next mail). Earlier links keep working.</summary>
    public static async Task<string> NewKeyAsync(IPluginData data, long bookingId, CancellationToken ct = default)
    {
        var key = NewKey();
        await data.Table(BookingStore.TKeys).InsertAsync(new Dictionary<string, object?> { ["booking_id"] = bookingId, ["hash"] = Hash(key), ["created"] = DateTime.UtcNow }, ct);
        return key;
    }

    public static async Task<List<Booking>> ListAsync(IPluginData data, IEnumerable<string>? statuses = null, CancellationToken ct = default)
    {
        var q = DataQuery.Everything.OrderBy("arrival");
        if (statuses is not null) q = q.Where("status", DataOp.In, statuses.ToList());
        return (await data.Table(BookingStore.TBookings).QueryAsync(q, ct)).Select(Map).ToList();
    }

    // ---- a guest's request --------------------------------------------------------------------------------------

    public sealed record RequestInput(Unit Unit, Stay Stay, string Name, string Email, string Phone, string Message, string Locale, string Source = "website");

    /// <summary>
    /// Makes a request: prices it (the server's bill, never the form's), takes its nights for <paramref name="holdHours"/> and numbers it, all in one
    /// transaction — a taken night throws and nothing is kept. Returns the booking and the guest key (shown once: the guest page's address).
    /// </summary>
    public static async Task<(Booking Booking, string Key)> RequestAsync(IPluginData data, RequestInput input, int holdHours, string currency, CancellationToken ct = default, bool enforceRules = true, Tr? tr = null)
    {
        var store = new BookingStore(data);
        var quote = await store.QuoteAsync(input.Unit.Id, input.Stay, currency, ct, tr) ?? throw new BookingException("booking.err.notBookable");
        // the host booking by phone may make an exception (a shorter stay in the high season); a guest may not. An impossible stay is refused for both.
        if (input.Stay.Nights <= 0 || input.Stay.Nights > Pricing.MaxNights || (enforceRules && !quote.Ok)) throw new BookingException(string.Join(" ", quote.Problems));
        var key = NewKey();
        var guestId = input.Email.Contains('@') ? await GuestAsync(data, input.Name, input.Email, input.Phone, input.Locale, ct) : 0;
        long id;
        try
        {
            id = await store.HoldAsync(input.Unit, input.Stay.Arrival, input.Stay.Departure, async tx =>
            {
                var year = input.Stay.Arrival.Year;
                var n = await tx.Table(BookingStore.TSequences).NextNumberAsync("booking-" + year, ct: ct);
                var bid = await tx.Table(BookingStore.TBookings).InsertAsync(new Dictionary<string, object?>
                {
                    ["number"] = year + "-" + n.ToString("0000", CultureInfo.InvariantCulture), ["unit_id"] = input.Unit.Id, ["guest_id"] = guestId,
                    ["arrival"] = B.Iso(input.Stay.Arrival), ["departure"] = B.Iso(input.Stay.Departure), ["adults"] = input.Stay.Adults, ["children"] = input.Stay.Children,
                    ["extras"] = string.Join(",", input.Stay.OptionalFees ?? Array.Empty<long>()), ["status"] = BookingStatus.Request, ["source"] = input.Source,
                    ["quote"] = QuoteJson(quote.Lines), ["total"] = quote.Total,
                    ["name"] = B.Clip(input.Name, 120), ["email"] = B.Clip(input.Email, 200).ToLowerInvariant(), ["phone"] = B.Clip(input.Phone, 60),
                    ["message"] = B.Clip(input.Message, 3000), ["locale"] = B.Clip(input.Locale, 8),
                    ["hold_until"] = DateTime.UtcNow.AddHours(Math.Clamp(holdHours, 1, 24 * 14)), ["created"] = DateTime.UtcNow, ["updated"] = DateTime.UtcNow,
                    ["history"] = AddHistory(null, input.Source == "website" ? "Anfrage über die Website" : "Angelegt im Admin")
                }, ct);
                await tx.Table(BookingStore.TKeys).InsertAsync(new Dictionary<string, object?> { ["booking_id"] = bid, ["hash"] = Hash(key), ["created"] = DateTime.UtcNow }, ct);
                return (bid, 0L);
            }, ct);
        }
        catch (UnavailableException) { throw new BookingException("booking.err.noLongerFree"); }
        return ((await GetAsync(data, id, ct))!, key);
    }

    /// <summary>The guest card by e-mail: found and refreshed, or made. Returns its id.</summary>
    public static async Task<long> GuestAsync(IPluginData data, string name, string email, string phone, string locale, CancellationToken ct = default)
    {
        var t = data.Table(BookingStore.TGuests);
        email = B.Clip(email, 200).ToLowerInvariant();
        var row = await t.FindAsync(DataQuery.Everything.Eq("email", email), ct);
        var values = new Dictionary<string, object?> { ["name"] = B.Clip(name, 120), ["phone"] = B.Clip(phone, 60), ["locale"] = B.Clip(locale, 8), ["updated"] = DateTime.UtcNow };
        // each shared field gets its own time stamp when it changes — the federation's later-change-wins (BookingFederation)
        if (row is not null) { await t.UpdateAsync(row.Id, BookingFederation.StampLocal(row, values, DateTime.UtcNow), ct); return row.Id; }
        values["email"] = email; values["created"] = DateTime.UtcNow;
        BookingFederation.StampLocal(null, values, DateTime.UtcNow);
        try { return await t.InsertAsync(values, ct); }
        catch { return (await t.FindAsync(DataQuery.Everything.Eq("email", email), ct))?.Id ?? 0; }      // a parallel request made it first
    }

    // ---- moves --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Moves a booking to <paramref name="to"/>: refused unless <see cref="BookingStatus.CanMove"/> allows it; a state that no longer holds the calendar
    /// frees the nights in the same transaction. <paramref name="change"/> adds fields (an adjusted bill, the offer's deadline). Returns the new state.
    /// </summary>
    public static async Task<Booking> MoveAsync(IPluginData data, long id, string to, string history, Dictionary<string, object?>? change = null, CancellationToken ct = default)
    {
        await data.InTransactionAsync(async tx =>
        {
            await tx.Table(BookingStore.TMeta).UpdateWhereAsync(DataQuery.Everything.Eq("name", "__lock"), new Dictionary<string, object?> { ["value"] = "" }, ct);
            var b = await GetAsync(tx, id, ct) ?? throw new BookingException("booking.err.noSuchBooking");
            if (!BookingStatus.CanMove(b.Status, to))
                throw new BookingException("booking.err.cannotMove", BookingStatus.Label(b.Status));
            var values = change ?? new Dictionary<string, object?>();
            values["status"] = to; values["updated"] = DateTime.UtcNow; values["history"] = AddHistory(b, history);
            if (to == BookingStatus.Confirmed) values["confirmed_at"] = DateTime.UtcNow;            // the payment plan counts from here
            await tx.Table(BookingStore.TBookings).UpdateAsync(id, values, ct);
            if (to is BookingStatus.Declined or BookingStatus.Expired or BookingStatus.Cancelled)
                await tx.Table(BookingStore.TNights).DeleteWhereAsync(DataQuery.Everything.Eq("booking_id", id), ct);
        }, ct);
        return (await GetAsync(data, id, ct))!;
    }

    /// <summary>Writes a line into a booking's history (a mail that went out or did not, a note) without changing its state.</summary>
    public static async Task NoteAsync(IPluginData data, long id, string text, CancellationToken ct = default)
    {
        var b = await GetAsync(data, id, ct);
        if (b is null) return;
        await data.Table(BookingStore.TBookings).UpdateAsync(id, new Dictionary<string, object?> { ["history"] = AddHistory(b, text) }, ct);
    }

    /// <summary>The host's offer: the bill as made, plus an optional adjustment (discount or surcharge, with a text), valid for some days.</summary>
    public static Task<Booking> OfferAsync(IPluginData data, Booking b, long adjustment, string adjustmentText, int validDays, CancellationToken ct = default, Tr? tr = null)
    {
        var lines = b.Lines.Where(l => l.Kind != "adjust").ToList();
        if (adjustment != 0) lines.Add(new QuoteLine("adjust", adjustmentText.Length > 0 ? adjustmentText : (tr ?? BookingStrings.German)(adjustment < 0 ? "booking.price.discount" : "booking.price.surcharge"), 1, adjustment, adjustment));
        var total = lines.Sum(l => l.Amount);
        if (total < 0) throw new BookingException("booking.err.negativeTotal");
        return MoveAsync(data, b.Id, BookingStatus.Offer, "Angebot geschickt" + (adjustment != 0 ? " (" + lines[^1].Text + ")" : ""), new Dictionary<string, object?>
        {
            ["quote"] = QuoteJson(lines), ["total"] = total, ["offer_until"] = DateTime.UtcNow.AddDays(Math.Clamp(validDays, 1, 30))
        }, ct);
    }

    /// <summary>Runs out what nobody answered in time: requests past their hold, offers past their deadline. Returns what ran out (to tell the guests).</summary>
    public static async Task<List<Booking>> ExpireAsync(IPluginData data, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var due = (await ListAsync(data, new[] { BookingStatus.Request, BookingStatus.Offer }, ct))
            .Where(b => b.Status == BookingStatus.Request ? b.HoldUntil < now : b.OfferUntil < now).ToList();
        var done = new List<Booking>();
        foreach (var b in due)
        {
            try { done.Add(await MoveAsync(data, b.Id, BookingStatus.Expired, b.Status == BookingStatus.Request ? "Anfrage abgelaufen (keine Antwort)" : "Angebot abgelaufen (nicht angenommen)", null, ct)); }
            catch (BookingException) { /* answered in the meantime */ }
        }
        return done;
    }
}
