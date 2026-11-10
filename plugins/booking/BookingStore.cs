using System.Globalization;

namespace MatCMS.Plugin.Booking;

/// <summary>A night that is not free: why (a booking or a block) and since when it is held. <see cref="Origin"/>: the unit the occupation was made for.</summary>
public sealed record Night(long UnitId, DateOnly Day, long BookingId, long BlockId, long Origin);

/// <summary>Occupied without a booking here: owner's use, repairs — or (phase B4) what a portal's calendar says.</summary>
public sealed record Block(long Id, long UnitId, DateOnly From, DateOnly To, string Reason, string Source, string Conflict = "")
{
    /// <summary>Nights from <see cref="From"/> to the day BEFORE <see cref="To"/> — "to" is the day the unit is free again, like a departure.</summary>
    public int Nights => To.DayNumber - From.DayNumber;
}

/// <summary>Thrown when a night of a stay is taken (also when a parallel write took it first) — nothing of the stay was written.</summary>
public sealed class UnavailableException(string message) : Exception(message);

/// <summary>The plugin's tables and everything that reads or writes them.</summary>
public sealed class BookingStore(IPluginData data)
{
    public const string TProperties = "properties", TUnits = "units", TSeasons = "seasons", TRates = "rates", TFees = "fees", TNights = "nights",
        TBlocks = "blocks", TBookings = "bookings", TMeta = "meta", TGuests = "guests", TSequences = "sequences", TKeys = "keys", TPayments = "payments", TFeeds = "feeds", TTravellers = "travellers", TTasks = "tasks", TRemoteStays = "remote_stays";

    public static void Declare(IDataBuilder b)
    {
        b.Table(TProperties, t => t.Text("name", required: true).Text("slug", required: true).Text("address").Text("country").Text("check_in").Text("check_out")
            .Text("description").Int("sort", def: "0").Text("directions").Text("house_rules").Text("checkout_info").UniqueIndex("slug"));
        b.Table(TUnits, t => t.Int("property_id", required: true).Text("name", required: true).Text("slug", required: true).Text("description").Text("image")
            .Int("bedrooms", def: "0").Int("guests_std", def: "2").Int("guests_max", def: "2").Text("contains").Int("buffer_days", def: "0")
            .Int("base_price", def: "0").Int("base_min_nights", def: "1").Int("extra_person", def: "0").Int("sort", def: "0").Bool("active", def: "1")
            .Text("ical_token").Text("access_info").Text("wifi").Text("checklist").UniqueIndex("slug").Index("property_id"));
        b.Table(TSeasons, t => t.Int("property_id", required: true).Text("name", required: true).Text("from_day", required: true).Text("to_day", required: true)
            .Int("rank", def: "1").Text("color").Index("property_id"));
        b.Table(TRates, t => t.Int("unit_id", required: true).Int("season_id", required: true).Int("price", def: "0").Int("min_nights", def: "1")
            .Text("arrival_days").Int("week_price", def: "0").Int("extra_person", def: "0").UniqueIndex("unit_id", "season_id").Index("season_id"));
        b.Table(TFees, t => t.Int("property_id", required: true).Int("unit_id", def: "0").Text("name", required: true).Int("amount", def: "0").Text("per", required: true)
            .Bool("optional", def: "0").Bool("tourist_tax", def: "0").Text("children").Int("sort", def: "0").Index("property_id"));
        // the availability: one row per occupied night and unit. The unique index is what makes a double booking impossible, not a check before.
        b.Table(TNights, t => t.Int("unit_id", required: true).Text("night", required: true).Int("booking_id", def: "0").Int("block_id", def: "0").Int("origin", def: "0")
            .UniqueIndex("unit_id", "night").Index("booking_id").Index("block_id"));
        b.Table(TBlocks, t => t.Int("unit_id", required: true).Text("from_day", required: true).Text("to_day", required: true).Text("reason").Text("source")
            .Text("uid").Text("conflict").Timestamp("created").Index("unit_id").Index("source"));
        // a portal's calendar (Airbnb, Booking.com, Rentlio …) for one unit: fetched every 15 minutes, its events become blocks with source "feed:{id}";
        // export_token is the address THIS portal gets back — the unit's nights without the ones that came from the portal itself
        b.Table(TFeeds, t => t.Int("unit_id", required: true).Text("name", required: true).Text("url").Text("export_token", required: true)
            .Timestamp("last_run").Timestamp("last_ok").Text("last_error").Int("events", def: "0").Text("conflicts").Timestamp("created")
            .UniqueIndex("export_token").Index("unit_id"));
        // a stay asked for or booked. The guest's contact is kept ON the booking too (what they typed for THIS stay); the guest row is the card index
        b.Table(TBookings, t => t.Text("number").Int("unit_id", required: true).Int("guest_id", def: "0").Text("arrival", required: true).Text("departure", required: true)
            .Int("adults", def: "1").Int("children", def: "0").Text("extras").Text("status", required: true).Text("source").Json("quote").Int("total", def: "0")
            .Text("name").Text("email").Text("phone").Text("message").Text("host_note").Text("locale")
            .Timestamp("hold_until").Timestamp("offer_until").Timestamp("confirmed_at").Timestamp("reminded_deposit").Timestamp("reminded_rest").Timestamp("checkin_at").Text("arrival_time").Text("travel").Text("mails").Timestamp("created").Timestamp("updated").Json("history")
            .Index("unit_id").Index("status").Index("arrival").Index("guest_id"));
        b.Table(TGuests, t => t.Text("name").Text("email", required: true).Text("phone").Text("country").Text("locale").Text("note")
            .Json("checkin").Json("stamps").Timestamp("created").Timestamp("updated").UniqueIndex("email").Index("updated"));
        // the leading site of a federation (BookingFederation): the stays the attached sites reported, as summaries — no registration data
        b.Table(TRemoteStays, t => t.Text("site", required: true).Text("site_name").Text("ref", required: true).Text("email").Text("name").Text("number")
            .Text("property").Text("unit").Text("arrival").Text("departure").Text("status").Int("total", def: "0").Text("currency").Timestamp("updated")
            .UniqueIndex("site", "ref").Index("email"));
        // the people of a stay with what the authorities want to know (the fields depend on the country of the house — BookingCheckin.Profiles);
        // deleted by a job after the retention period, the booking stays
        b.Table(TTravellers, t => t.Int("booking_id", required: true).Int("idx", def: "0").Text("first").Text("last").Text("gender").Text("born").Text("birthplace")
            .Text("nationality").Text("doc_type").Text("doc_no").Text("street").Text("zip").Text("city").Text("res_country").Timestamp("created").Index("booking_id"));
        // the cleaning of a unit on a day: who does it, whether it is done — the list itself is derived from the bookings (BookingCleaning)
        b.Table(TTasks, t => t.Int("unit_id", required: true).Text("day", required: true).Text("assignee").Timestamp("done_at").Text("note").UniqueIndex("unit_id", "day"));
        // the guest's secret links, as hashes. Each mail may carry a fresh one (only a hash is kept, so an old one cannot be sent again); all stay valid
        b.Table(TKeys, t => t.Int("booking_id", required: true).Text("hash", required: true).Timestamp("created").UniqueIndex("hash").Index("booking_id"));
        b.Table(TPayments, t => t.Int("booking_id", required: true).Int("amount", required: true).Text("day", required: true).Text("method").Text("note")
            .Timestamp("created").Index("booking_id"));
        b.Table(TSequences, t => t.Text("name", required: true).Int("value", def: "0").UniqueIndex("name"));
        b.Table(TMeta, t => t.Text("name", required: true).Text("value").UniqueIndex("name"));
    }

    private IDataTable T(string name) => data.Table(name);

    // ---- mapping ------------------------------------------------------------------------------------------------

    private static IReadOnlyList<long> Ids(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0).Where(v => v > 0).Distinct().ToList();
    private static IReadOnlyList<int> Days(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0).Where(v => v is >= 1 and <= 7).Distinct().OrderBy(v => v).ToList();
    private static int I(DataRow r, string c) => (int)Math.Clamp(r.Int(c), int.MinValue, int.MaxValue);

    private static Property P(DataRow r) => new(r.Id, r.Text("name"), r.Text("slug"), r.Text("address"), r.Text("country"), r.Text("check_in"), r.Text("check_out"), r.Text("description"), I(r, "sort"),
        r.Text("directions"), r.Text("house_rules"), r.Text("checkout_info"));
    private static Unit U(DataRow r) => new(r.Id, r.Int("property_id"), r.Text("name"), r.Text("slug"), r.Text("description"), r.Text("image"), I(r, "bedrooms"),
        Math.Max(1, I(r, "guests_std")), Math.Max(1, I(r, "guests_max")), Ids(r.Text("contains")), Math.Max(0, I(r, "buffer_days")), r.Int("base_price"),
        Math.Max(1, I(r, "base_min_nights")), r.Int("extra_person"), I(r, "sort"), r.Bool("active"), r.Text("access_info"), r.Text("wifi"), r.Text("checklist"));
    private static Season? S(DataRow r) => B.Day(r.Text("from_day")) is { } f && B.Day(r.Text("to_day")) is { } t
        ? new Season(r.Id, r.Int("property_id"), r.Text("name"), f, t, I(r, "rank"), r.Text("color")) : null;
    private static Rate R(DataRow r) => new(r.Id, r.Int("unit_id"), r.Int("season_id"), r.Int("price"), Math.Max(1, I(r, "min_nights")), Days(r.Text("arrival_days")), r.Int("week_price"), r.Int("extra_person"));
    private static Fee F(DataRow r) => new(r.Id, r.Int("property_id"), r.Int("unit_id"), r.Text("name"), r.Int("amount"), r.Text("per"), r.Bool("optional"), r.Bool("tourist_tax"),
        r.Text("children") is { Length: > 0 } c ? c : "full", I(r, "sort"));
    private static Block? Bl(DataRow r) => B.Day(r.Text("from_day")) is { } f && B.Day(r.Text("to_day")) is { } t
        ? new Block(r.Id, r.Int("unit_id"), f, t, r.Text("reason"), r.Text("source"), r.Text("conflict")) : null;

    // ---- reading ------------------------------------------------------------------------------------------------

    public async Task<List<Property>> PropertiesAsync(CancellationToken ct = default) =>
        (await T(TProperties).QueryAsync(DataQuery.Everything.OrderBy("sort").OrderBy("name"), ct)).Select(P).ToList();
    public async Task<Property?> PropertyAsync(long id, CancellationToken ct = default) => await T(TProperties).GetAsync(id, ct) is { } r ? P(r) : null;

    public async Task<List<Unit>> UnitsAsync(CancellationToken ct = default) =>
        (await T(TUnits).QueryAsync(DataQuery.Everything.OrderBy("sort").OrderBy("name"), ct)).Select(U).ToList();
    public async Task<Unit?> UnitAsync(long id, CancellationToken ct = default) => await T(TUnits).GetAsync(id, ct) is { } r ? U(r) : null;
    public async Task<Unit?> UnitBySlugAsync(string slug, CancellationToken ct = default) => await T(TUnits).FindAsync(DataQuery.Everything.Eq("slug", slug), ct) is { } r ? U(r) : null;

    public async Task<List<Season>> SeasonsAsync(long propertyId, CancellationToken ct = default) =>
        (await T(TSeasons).QueryAsync(DataQuery.Everything.Eq("property_id", propertyId).OrderBy("from_day"), ct)).Select(S).OfType<Season>().ToList();
    public async Task<Season?> SeasonAsync(long id, CancellationToken ct = default) => await T(TSeasons).GetAsync(id, ct) is { } r ? S(r) : null;

    public async Task<List<Rate>> RatesAsync(IEnumerable<long> unitIds, CancellationToken ct = default)
    {
        var ids = unitIds.Distinct().Select(x => (object?)x).ToList();
        if (ids.Count == 0) return new();
        return (await T(TRates).QueryAsync(DataQuery.Everything.Where("unit_id", DataOp.In, ids), ct)).Select(R).ToList();
    }

    public async Task<List<Fee>> FeesAsync(long propertyId, CancellationToken ct = default) =>
        (await T(TFees).QueryAsync(DataQuery.Everything.Eq("property_id", propertyId).OrderBy("sort"), ct)).Select(F).ToList();

    public async Task<List<Block>> BlocksAsync(long unitId, CancellationToken ct = default) =>
        (await T(TBlocks).QueryAsync(DataQuery.Everything.Eq("unit_id", unitId).OrderBy("from_day"), ct)).Select(Bl).OfType<Block>().ToList();

    /// <summary>The occupied nights of some units in [from, to).</summary>
    public async Task<List<Night>> NightsAsync(IEnumerable<long> unitIds, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        await NightsIn(data, unitIds, from, to, ct);

    internal static async Task<List<Night>> NightsIn(IPluginData d, IEnumerable<long> unitIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var ids = unitIds.Distinct().Select(x => (object?)x).ToList();
        if (ids.Count == 0) return new();
        var rows = await d.Table(TNights).QueryAsync(DataQuery.Everything.Where("unit_id", DataOp.In, ids)
            .Where("night", DataOp.Ge, B.Iso(from)).Where("night", DataOp.Lt, B.Iso(to)), ct);
        return rows.Select(r => B.Day(r.Text("night")) is { } n ? new Night(r.Int("unit_id"), n, r.Int("booking_id"), r.Int("block_id"), r.Int("origin")) : null).OfType<Night>().ToList();
    }

    /// <summary>The bill of a stay in a unit — everything read here, computed by <see cref="Pricing"/>.</summary>
    public async Task<Quote?> QuoteAsync(long unitId, Stay stay, string currency = "EUR", CancellationToken ct = default)
    {
        var unit = await UnitAsync(unitId, ct);
        if (unit is null || !unit.Active) return null;
        return Pricing.Calculate(unit, stay, await SeasonsAsync(unit.PropertyId, ct), await RatesAsync(new[] { unit.Id }, ct), await FeesAsync(unit.PropertyId, ct), currency);
    }

    // ---- availability -------------------------------------------------------------------------------------------

    /// <summary>
    /// The units a stay in <paramref name="unit"/> writes its nights for and checks: itself and the units it contains (the whole house writes into each
    /// apartment). NOT the units that contain it — an apartment's night written onto the house would make the house's OTHER apartments look taken.
    /// The house is still taken when one apartment is: it checks its apartments' rows. And a house and an apartment booked at the same moment both
    /// write the apartment's row, so the unique index stops one of them. One level deep — a house of houses is not a thing.
    /// </summary>
    public static IReadOnlyList<long> Occupies(Unit unit, IEnumerable<Unit> all) => new[] { unit.Id }.Concat(unit.Contains).Distinct().ToList();

    /// <summary>The nights a stay would need free: its own, widened by the unit's buffer (cleaning days) on both sides.</summary>
    public static (DateOnly From, DateOnly To) Needed(Unit unit, DateOnly arrival, DateOnly departure) =>
        (arrival.AddDays(-unit.BufferDays), departure.AddDays(unit.BufferDays));

    /// <summary>Whether a stay fits: none of the units it occupies has a taken night in the needed range.</summary>
    public async Task<bool> IsFreeAsync(Unit unit, DateOnly arrival, DateOnly departure, CancellationToken ct = default)
    {
        var (from, to) = Needed(unit, arrival, departure);
        return (await NightsAsync(Occupies(unit, await UnitsAsync(ct)), from, to, ct)).Count == 0;
    }

    /// <summary>
    /// Takes the nights of a stay for a booking or a block, in ONE serialisable transaction: it starts with a write (so a parallel one queues instead of
    /// being refused), checks every unit the stay occupies, then writes a row per night and unit. A night that is taken — before, or by a parallel write
    /// the unique index stops — throws <see cref="UnavailableException"/> and nothing is kept.
    /// </summary>
    public async Task<long> HoldAsync(Unit unit, DateOnly arrival, DateOnly departure, Func<IPluginData, Task<(long BookingId, long BlockId)>> owner, CancellationToken ct = default)
    {
        var all = await UnitsAsync(ct);
        var units = Occupies(unit, all);
        try
        {
            return await data.InTransactionAsync(async tx =>
            {
                await tx.Table(TMeta).UpdateWhereAsync(DataQuery.Everything.Eq("name", "__lock"), new Dictionary<string, object?> { ["value"] = "" }, ct);
                var (from, to) = Needed(unit, arrival, departure);
                var taken = await NightsIn(tx, units, from, to, ct);
                if (taken.Count > 0) throw new UnavailableException("Belegt: " + string.Join(", ", taken.Select(n => n.Day).Distinct().OrderBy(d => d).Take(5).Select(B.Date)) + (taken.Count > 5 ? " …" : ""));
                var (bookingId, blockId) = await owner(tx);
                for (var d = arrival; d < departure; d = d.AddDays(1))
                    foreach (var u in units)
                        await tx.Table(TNights).InsertAsync(new Dictionary<string, object?>
                        {
                            ["unit_id"] = u, ["night"] = B.Iso(d), ["booking_id"] = bookingId, ["block_id"] = blockId, ["origin"] = unit.Id
                        }, ct);
                return bookingId > 0 ? bookingId : blockId;
            }, ct);
        }
        catch (UnavailableException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException && ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnavailableException("Belegt – gerade eben von einer anderen Buchung.");
        }
    }

    /// <summary>A block (owner's use, repairs) over [from, to). Throws <see cref="UnavailableException"/> when a night is taken.</summary>
    public Task<long> AddBlockAsync(Unit unit, DateOnly from, DateOnly to, string reason, string source, CancellationToken ct = default) =>
        HoldAsync(unit, from, to, async tx =>
        {
            var id = await tx.Table(TBlocks).InsertAsync(new Dictionary<string, object?>
            {
                ["unit_id"] = unit.Id, ["from_day"] = B.Iso(from), ["to_day"] = B.Iso(to), ["reason"] = B.Clip(reason, 200), ["source"] = source, ["created"] = DateTime.UtcNow
            }, ct);
            return (0, id);
        }, ct);

    public async Task RemoveBlockAsync(long id, CancellationToken ct = default) =>
        await data.InTransactionAsync(async tx =>
        {
            await tx.Table(TNights).DeleteWhereAsync(DataQuery.Everything.Eq("block_id", id), ct);
            await tx.Table(TBlocks).DeleteAsync(id, ct);
        }, ct);

    // ---- writing the setup ------------------------------------------------------------------------------------------

    public IDataTable Properties => T(TProperties);
    public IDataTable Units => T(TUnits);
    public IDataTable Seasons => T(TSeasons);
    public IDataTable Fees => T(TFees);
    public IDataTable Meta => T(TMeta);
    public IDataTable Bookings => T(TBookings);
    public IDataTable Guests => T(TGuests);

    /// <summary>Writes a unit's rate in a season (one row per pair; an empty price removes it, so the base price applies again).</summary>
    public async Task SetRateAsync(long unitId, long seasonId, long? price, int minNights, IEnumerable<int> arrivalDays, long weekPrice, long extraPerson, CancellationToken ct = default)
    {
        var existing = await T(TRates).FindAsync(DataQuery.Everything.Eq("unit_id", unitId).Eq("season_id", seasonId), ct);
        if (price is null)
        {
            if (existing is not null) await T(TRates).DeleteAsync(existing.Id, ct);
            return;
        }
        var values = new Dictionary<string, object?>
        {
            ["unit_id"] = unitId, ["season_id"] = seasonId, ["price"] = Math.Max(0, price.Value), ["min_nights"] = Math.Clamp(minNights, 1, 60),
            ["arrival_days"] = string.Join(",", arrivalDays.Where(d => d is >= 1 and <= 7).Distinct().OrderBy(d => d)), ["week_price"] = Math.Max(0, weekPrice), ["extra_person"] = Math.Max(0, extraPerson)
        };
        if (existing is null) await T(TRates).InsertAsync(values, ct);
        else await T(TRates).UpdateAsync(existing.Id, values, ct);
    }

    /// <summary>Deletes a season and its rates.</summary>
    public async Task DeleteSeasonAsync(long id, CancellationToken ct = default) =>
        await data.InTransactionAsync(async tx =>
        {
            await tx.Table(TRates).DeleteWhereAsync(DataQuery.Everything.Eq("season_id", id), ct);
            await tx.Table(TSeasons).DeleteAsync(id, ct);
        }, ct);

    /// <summary>Copies a house's seasons (with their rates) one year on — the plan for next year as a starting point. Returns how many.</summary>
    public async Task<int> CopySeasonsAsync(long propertyId, int fromYear, CancellationToken ct = default)
    {
        var seasons = (await SeasonsAsync(propertyId, ct)).Where(s => s.From.Year == fromYear).ToList();
        var units = (await UnitsAsync(ct)).Where(u => u.PropertyId == propertyId).Select(u => u.Id).ToList();
        var rates = await RatesAsync(units, ct);
        var n = 0;
        foreach (var s in seasons)
        {
            var id = await T(TSeasons).InsertAsync(new Dictionary<string, object?>
            {
                ["property_id"] = propertyId, ["name"] = s.Name, ["from_day"] = B.Iso(s.From.AddYears(1)), ["to_day"] = B.Iso(s.To.AddYears(1)), ["rank"] = s.Rank, ["color"] = s.Color
            }, ct);
            foreach (var r in rates.Where(r => r.SeasonId == s.Id))
                await SetRateAsync(r.UnitId, id, r.Price, r.MinNights, r.ArrivalDays, r.WeekPrice, r.ExtraPerson, ct);
            n++;
        }
        return n;
    }

    /// <summary>A slug not used by another row of the table (adds -2, -3 …).</summary>
    public async Task<string> FreeSlugAsync(string table, string wanted, long self, CancellationToken ct = default)
    {
        var baseSlug = B.Slug(wanted) is { Length: > 0 } s ? s : "einheit";
        for (var i = 1; i < 100; i++)
        {
            var slug = i == 1 ? baseSlug : baseSlug + "-" + i;
            var hit = await T(table).FindAsync(DataQuery.Everything.Eq("slug", slug), ct);
            if (hit is null || hit.Id == self) return slug;
        }
        return baseSlug + "-" + Guid.NewGuid().ToString("N")[..6];
    }
}
