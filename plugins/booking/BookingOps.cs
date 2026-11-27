using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// The booking plugin's operations for AI agents (the site's assistant, the cloud's MCP, the site's own MCP server): houses, units, seasons with
/// their price table, fees, a price check — and the stays (requests and bookings, <see cref="BookingOps"/> in BookingOpsStays.cs). Every write goes
/// through the same store code and the same limits as the admin's forms, money is whole cents, and every write answers with its <c>undo</c>: an
/// INTERNAL operation of this plugin (<c>*.remove</c> for what was just made, <c>*.restore</c> with the state before for a change or a delete).
/// <para>Left out on purpose: a unit's way in (door code, key box) and its Wi-Fi — they are never shown to a guest before the day before arrival,
/// and an answer to the assistant goes to an outside model. A unit is switched off (<c>active: false</c>), never deleted from here; a house is
/// never deleted either (the admin deletes only an empty one).</para>
/// </summary>
internal static partial class BookingOps
{
    // ---- descriptions -------------------------------------------------------------------------------------------

    public static readonly PluginOperationSpec HouseList = new("house.list",
        "Objekte (Ferienhäuser, Villen) auflisten: id, Name, slug, Adresse, Land, Check-in/-out, Anzahl Einheiten.", "{}", Writes: false);
    public static readonly PluginOperationSpec HouseRead = new("house.read",
        "Ein Objekt vollständig lesen, mit seinen Einheiten, Saisons und Gebühren. Ohne Angabe: das einzige Objekt.",
        """{"house"?: 1 oder "villa-slug"}""", Writes: false);
    public static readonly PluginOperationSpec HouseCreate = new("house.create",
        "Neues Objekt anlegen. country: Ländercode (HR, DE, AT, IT, ES, PT, FR, GR, SI, ME, CH, NL, DK) — bestimmt die Meldefelder beim Check-in. " +
        "description ist intern; directions (Anfahrt), house_rules (Hausordnung), checkout_info (Abreise) sehen die Gäste.",
        """{"name": "Villa Mirta", "address"?: "…", "country"?: "HR", "check_in"?: "15:00", "check_out"?: "10:00", "description"?, "directions"?, "house_rules"?, "checkout_info"?, "sort"?: 0}""");
    public static readonly PluginOperationSpec HouseUpdate = new("house.update",
        "Ein Objekt ändern; nicht genannte Felder bleiben (der slug bleibt immer).",
        """{"house": 1, "name"?, "address"?, "country"?, "check_in"?, "check_out"?, "description"?, "directions"?, "house_rules"?, "checkout_info"?, "sort"?}""");

    public static readonly PluginOperationSpec UnitList = new("unit.list",
        "Buchbare Einheiten (Apartment, Zimmer, ganzes Haus) auflisten: id, Objekt, Name, slug, Personen (Standard/max.), Grundpreis je Nacht in Cent, Mindestnächte, aktiv, enthaltene Einheiten.",
        """{"house"?: 1, "includeInactive"?: true}""", Writes: false);
    public static readonly PluginOperationSpec UnitRead = new("unit.read",
        "Eine Einheit vollständig lesen, mit ihren Saisonpreisen.", """{"unit": 3 oder "apartment-slug"}""", Writes: false);
    public static readonly PluginOperationSpec UnitCreate = new("unit.create",
        "Neue Einheit anlegen. Beträge in Cent: base_price_cents = Preis je Nacht außerhalb jeder Saison, extra_person_cents = Aufschlag je Person und Nacht über guests_std. " +
        "image: url aus der Mediathek (/uploads/…) oder https. contains: ids der Einheiten desselben Objekts, aus denen ein „ganzes Haus“ besteht (es belegt sie mit). " +
        "buffer_days: Reinigungstage zwischen zwei Aufenthalten.",
        """{"house": 1, "name": "Apartment Meerblick", "slug"?, "description"?, "image"?: "/uploads/…", "bedrooms"?: 1, "guests_std"?: 2, "guests_max"?: 4, "base_price_cents": 8000, "base_min_nights"?: 1, "extra_person_cents"?: 0, "buffer_days"?: 0, "contains"?: [2, 3], "checklist"?: "Reinigungsliste", "sort"?: 0, "active"?: true}""");
    public static readonly PluginOperationSpec UnitUpdate = new("unit.update",
        "Eine Einheit ändern; nicht genannte Felder bleiben. active: false nimmt sie aus der Buchung (gelöscht wird nie).",
        """{"unit": 3, "name"?, "slug"?, "house"?, "description"?, "image"?, "bedrooms"?, "guests_std"?, "guests_max"?, "base_price_cents"?, "base_min_nights"?, "extra_person_cents"?, "buffer_days"?, "contains"?: [], "checklist"?, "sort"?, "active"?: false}""");

    public static readonly PluginOperationSpec SeasonList = new("season.list",
        "Saisons eines Objekts (ohne house: aller Objekte) mit der Preistabelle: je Einheit Preis je Nacht, Mindestnächte, Anreisetage (1 = Montag … 7 = Sonntag), Wochenpreis, Personenaufschlag — alles in Cent.",
        """{"house"?: 1}""", Writes: false);
    public static readonly PluginOperationSpec SeasonCreate = new("season.create",
        "Neue Saison anlegen, optional gleich mit den Preisen je Einheit. from/to: erste und letzte NACHT (einschließlich). Überlappen erlaubt: der höhere rank gewinnt (Silvester in der Nebensaison). " +
        "rates: je Einheit (id oder slug) price_cents je Nacht, min_nights, arrival_days ([6] = nur Samstag, [] = jeder Tag), week_price_cents (Preis für 7 Nächte, 0 = keiner), extra_person_cents. Mindestnächte und Anreisetage gelten nach der Saison der ANREISE-Nacht.",
        """{"house": 1, "name": "Hauptsaison", "from": "2027-07-01", "to": "2027-08-31", "rank"?: 2, "color"?: "#2563eb", "rates"?: [{"unit": 3, "price_cents": 20000, "min_nights"?: 7, "arrival_days"?: [6], "week_price_cents"?: 130000, "extra_person_cents"?: 2000}]}""");
    public static readonly PluginOperationSpec SeasonUpdate = new("season.update",
        "Eine Saison ändern; nicht genannte Felder bleiben. rates ändert nur die genannten Einheiten (nicht genannte Felder einer Rate bleiben); price_cents: null entfernt die Rate, dann gilt der Grundpreis der Einheit.",
        """{"season": 5, "name"?, "from"?, "to"?, "rank"?, "color"?, "rates"?: [{"unit": 3, "price_cents"?: 21000, "min_nights"?, "arrival_days"?, "week_price_cents"?, "extra_person_cents"?}]}""");
    public static readonly PluginOperationSpec SeasonDelete = new("season.delete",
        "Eine Saison samt ihren Preisen löschen (gebuchte Aufenthalte behalten ihre Rechnung).", """{"season": 5}""");

    public static readonly PluginOperationSpec FeeList = new("fee.list",
        "Gebühren und Kurtaxe eines Objekts (ohne house: aller Objekte): Betrag in Cent, per (stay = je Aufenthalt, night = je Nacht, person = je Person, person_night = je Person und Nacht), Einheit (0 = alle), wählbar, Kurtaxe, Kinder (full/half/free).",
        """{"house"?: 1}""", Writes: false);
    public static readonly PluginOperationSpec FeeCreate = new("fee.create",
        "Neue Gebühr (Endreinigung, Bettwäsche, Haustier …) oder Kurtaxe (tourist_tax: true) anlegen. optional: nur wenn der Gast sie wählt. children: wie Kinder zählen (full, half, free). unit: 0 = alle Einheiten des Objekts.",
        """{"house": 1, "name": "Endreinigung", "amount_cents": 6000, "per"?: "stay|night|person|person_night", "unit"?: 0, "optional"?: false, "tourist_tax"?: false, "children"?: "full", "sort"?: 0}""");
    public static readonly PluginOperationSpec FeeUpdate = new("fee.update",
        "Eine Gebühr ändern; nicht genannte Felder bleiben.",
        """{"fee": 7, "name"?, "amount_cents"?, "per"?, "unit"?, "optional"?, "tourist_tax"?, "children"?, "sort"?}""");
    public static readonly PluginOperationSpec FeeDelete = new("fee.delete", "Eine Gebühr löschen (gebuchte Aufenthalte behalten ihre Rechnung).", """{"fee": 7}""");

    public static readonly PluginOperationSpec PriceCheck = new("price.check",
        "Preis und Verfügbarkeit eines Aufenthalts berechnen — dieselbe Rechnung, die ein Gast bekommt (Zeilen, Summe in Cent, Gründe, wenn er nicht passt). Schreibt nichts. extras: ids wählbarer Gebühren.",
        """{"unit": 3 oder "apartment-slug", "arrival": "2027-07-03", "departure": "2027-07-10", "adults"?: 2, "children"?: 0, "extras"?: [9]}""", Writes: false);

    public static readonly PluginOperationSpec HouseRemove = new("house.remove", "Entfernt ein eben angelegtes Objekt (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec HouseRestore = new("house.restore", "Stellt ein Objekt wieder her (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec UnitRemove = new("unit.remove", "Entfernt eine eben angelegte Einheit (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec UnitRestore = new("unit.restore", "Stellt eine Einheit wieder her (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec SeasonRemove = new("season.remove", "Entfernt eine eben angelegte Saison (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec SeasonRestore = new("season.restore", "Stellt eine Saison mit ihren Preisen wieder her (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec FeeRemove = new("fee.remove", "Entfernt eine eben angelegte Gebühr (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec FeeRestore = new("fee.restore", "Stellt eine Gebühr wieder her (Rückgängig).", Internal: true);

    public static void Register(IPluginBuilder b)
    {
        b.Operation(HouseList, HouseListAsync);
        b.Operation(HouseRead, HouseReadAsync);
        b.Operation(HouseCreate, HouseCreateAsync);
        b.Operation(HouseUpdate, HouseUpdateAsync);
        b.Operation(HouseRemove, HouseRemoveAsync);
        b.Operation(HouseRestore, HouseRestoreAsync);
        b.Operation(UnitList, UnitListAsync);
        b.Operation(UnitRead, UnitReadAsync);
        b.Operation(UnitCreate, UnitCreateAsync);
        b.Operation(UnitUpdate, UnitUpdateAsync);
        b.Operation(UnitRemove, UnitRemoveAsync);
        b.Operation(UnitRestore, UnitRestoreAsync);
        b.Operation(SeasonList, SeasonListAsync);
        b.Operation(SeasonCreate, SeasonCreateAsync);
        b.Operation(SeasonUpdate, SeasonUpdateAsync);
        b.Operation(SeasonDelete, SeasonDeleteAsync);
        b.Operation(SeasonRemove, SeasonRemoveAsync);
        b.Operation(SeasonRestore, SeasonRestoreAsync);
        b.Operation(FeeList, FeeListAsync);
        b.Operation(FeeCreate, FeeCreateAsync);
        b.Operation(FeeUpdate, FeeUpdateAsync);
        b.Operation(FeeDelete, FeeDeleteAsync);
        b.Operation(FeeRemove, FeeRemoveAsync);
        b.Operation(FeeRestore, FeeRestoreAsync);
        b.Operation(PriceCheck, PriceCheckAsync);
        b.Operation(StayList, StayListAsync);
        b.Operation(StayRead, StayReadAsync);
        b.Operation(StayMove, StayMoveAsync);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>A refusal of an input, carried up to the operation's answer as "failed" with its text.</summary>
    private sealed class Refused(string message) : Exception(message);

    private static JsonObject Answer(string status, string message, JsonNode? data = null, JsonNode? undo = null)
    {
        var o = new JsonObject { ["status"] = status, ["message"] = message };
        if (data is not null) o["data"] = data;
        if (undo is not null) o["undo"] = undo;
        return o;
    }

    private static JsonObject Undo(string name, JsonNode args) => new() { ["name"] = name, ["args"] = args };
    private static JsonObject Args(JsonNode? args) => args as JsonObject ?? new JsonObject();
    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : null;
    private static bool? Flag(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>A whole number (a JSON number or a string of digits); absent = null; anything else is refused with the field's name.</summary>
    private static long? Whole(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is null) return null;
        if (n is JsonValue v)
        {
            // a JsonValue made in code from an int answers only to int; one parsed from text to any number type
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < 1e15) return (long)d;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
        }
        throw new Refused($"{key} muss eine ganze Zahl sein.");
    }

    private static int? IntIn(JsonObject o, string key, int min, int max) => Whole(o, key) is { } v ? (int)Math.Clamp(v, min, max) : null;

    /// <summary>An amount in whole cents, never below zero.</summary>
    private static long? Cents(JsonObject o, string key) =>
        Whole(o, key) is { } v ? v < 0 ? throw new Refused($"{key} darf nicht negativ sein (ganze Cent).") : v : null;

    private static string? Text(JsonObject o, string key, int max) => Str(o, key) is { } s ? B.Clip(s.Replace("\r", ""), max) : null;

    private static string Image(string? url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return "";
        if (url.StartsWith("/uploads/", StringComparison.Ordinal) && !url.Contains("..") || url.StartsWith("https://", StringComparison.Ordinal)) return B.Clip(url, 500);
        throw new Refused("image muss ein Bild aus der Mediathek (/uploads/…) oder eine https-Adresse sein.");
    }

    private static readonly Regex TimeOfDay = new(@"^([01]?\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);
    private static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    private static string Day(JsonObject o, string key) =>
        B.Day(Str(o, key)) is { } d ? B.Iso(d) : throw new Refused($"{key} braucht ein Datum (JJJJ-MM-TT).");

    private static async Task<JsonNode?> Guard(Func<Task<JsonNode?>> run)
    {
        try { return await run(); }
        catch (Refused r) { return Answer("failed", r.Message); }
        catch (UnavailableException u) { return Answer("failed", u.Message); }
    }

    private static string Names<T>(IEnumerable<T> items, Func<T, string> name) => string.Join(", ", items.Take(12).Select(name)) is { Length: > 0 } s ? s : "keine";

    // ---- finding things -------------------------------------------------------------------------------------------

    /// <summary>The house a request names (id, slug or name) — or, when it names none, the only one there is.</summary>
    private static async Task<Property> HouseAsync(BookingStore store, JsonObject o, CancellationToken ct, string key = "house", bool required = true)
    {
        var houses = await store.PropertiesAsync(ct);
        if (Str(o, key)?.Trim() is { Length: > 0 } s)
            return houses.FirstOrDefault(h => B.Num(h.Id) == s) ?? houses.FirstOrDefault(h => h.Slug == B.Slug(s)) ?? houses.FirstOrDefault(h => string.Equals(h.Name, s, StringComparison.OrdinalIgnoreCase))
                ?? throw new Refused($"Objekt „{s}“ nicht gefunden. Vorhanden: {Names(houses, h => h.Id + " " + h.Name)}.");
        if (houses.Count == 1 || (!required && houses.Count > 0)) return houses[0];
        throw new Refused(houses.Count == 0 ? "Es gibt noch kein Objekt – zuerst house.create." : $"Bitte das Objekt angeben (house: id oder slug). Vorhanden: {Names(houses, h => h.Id + " " + h.Name)}.");
    }

    /// <summary>A unit by id or slug, under <paramref name="key"/> (or <c>id</c>).</summary>
    private static Unit UnitOf(List<Unit> all, string? key)
    {
        key = (key ?? "").Trim();
        if (key.Length == 0) throw new Refused("Bitte die Einheit angeben (unit: id oder slug).");
        return all.FirstOrDefault(u => B.Num(u.Id) == key) ?? all.FirstOrDefault(u => u.Slug == B.Slug(key))
            ?? throw new Refused($"Einheit „{key}“ nicht gefunden. Vorhanden: {Names(all, u => u.Id + " " + u.Slug)}.");
    }

    private static string? KeyOf(JsonObject o, string key) => Str(o, key) ?? Str(o, "id");

    private static async Task<Fee?> FeeAsync(BookingStore store, long id, CancellationToken ct) =>
        await store.Fees.GetAsync(id, ct) is { } row ? (await store.FeesAsync(row.Int("property_id"), ct)).FirstOrDefault(f => f.Id == id) : null;

    // ---- houses ---------------------------------------------------------------------------------------------------

    private static readonly string[] CountryCodes = BookingAdmin.Countries.Select(c => c.Code).ToArray();

    private static JsonObject HouseJson(Property p) => new()
    {
        ["id"] = p.Id, ["name"] = p.Name, ["slug"] = p.Slug, ["address"] = p.Address, ["country"] = p.Country, ["check_in"] = p.CheckIn, ["check_out"] = p.CheckOut,
        ["description"] = p.Description, ["sort"] = p.Sort, ["directions"] = p.Directions, ["house_rules"] = p.HouseRules, ["checkout_info"] = p.CheckoutInfo,
    };

    /// <summary>The house fields a request sends, cleaned like the admin's form (same lengths); only what it names.</summary>
    private static Dictionary<string, object?> HouseValues(JsonObject o)
    {
        var v = new Dictionary<string, object?>();
        if (Str(o, "name") is { } n) v["name"] = B.Clip(n, 120) is { Length: > 0 } name ? name : throw new Refused("Der Name darf nicht leer sein.");
        if (Text(o, "address", 300) is { } a) v["address"] = a;
        if (Str(o, "country") is { } c)
            v["country"] = c.Trim().ToUpperInvariant() is var cc && CountryCodes.Contains(cc) ? cc : throw new Refused($"country: einer von {string.Join(", ", CountryCodes)}.");
        foreach (var k in new[] { "check_in", "check_out" })
            if (Str(o, k) is { } t) v[k] = t.Trim().Length == 0 || TimeOfDay.IsMatch(t.Trim()) ? t.Trim() : throw new Refused($"{k}: Uhrzeit wie 15:00.");
        if (Text(o, "description", 4000) is { } d) v["description"] = d;
        if (Text(o, "directions", 4000) is { } dir) v["directions"] = dir;
        if (Text(o, "house_rules", 4000) is { } r) v["house_rules"] = r;
        if (Text(o, "checkout_info", 2000) is { } ci) v["checkout_info"] = ci;
        if (IntIn(o, "sort", -999, 999) is { } s) v["sort"] = s;
        return v;
    }

    private static async Task<JsonNode?> HouseListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var units = await store.UnitsAsync(ct);
        var arr = new JsonArray();
        foreach (var h in await store.PropertiesAsync(ct))
            arr.Add(new JsonObject
            {
                ["id"] = h.Id, ["name"] = h.Name, ["slug"] = h.Slug, ["address"] = h.Address, ["country"] = h.Country, ["check_in"] = h.CheckIn, ["check_out"] = h.CheckOut,
                ["units"] = units.Count(u => u.PropertyId == h.Id)
            });
        return Answer("applied", arr.Count + " Objekt(e).", arr);
    }

    private static Task<JsonNode?> HouseReadAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var store = new BookingStore(ctx.Data);
        var o = Args(args);
        var h = await HouseAsync(store, o, ct, o.ContainsKey("house") ? "house" : "id");
        var data = HouseJson(h);
        var units = (await store.UnitsAsync(ct)).Where(u => u.PropertyId == h.Id).ToList();
        data["units"] = new JsonArray(units.Select(u => (JsonNode)UnitShort(u)).ToArray());
        data["seasons"] = await SeasonsJsonAsync(store, h, units, ct);
        data["fees"] = new JsonArray((await store.FeesAsync(h.Id, ct)).Select(f => (JsonNode)FeeJson(f)).ToArray());
        data["currency"] = BookingPlugin.Currency(ctx);
        return Answer("applied", h.Name, data);
    });

    private static Task<JsonNode?> HouseCreateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var o = Args(args);
        var v = HouseValues(o);
        if (v.GetValueOrDefault("name") is not string name) return Answer("failed", "Bitte einen Namen angeben (name).");
        var store = new BookingStore(ctx.Data);
        if ((await store.PropertiesAsync(ct)).FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)) is { } same)
            return Answer("skipped-exists", $"Das Objekt „{name}“ gibt es schon.", new JsonObject { ["id"] = same.Id });
        v.TryAdd("country", "HR"); v.TryAdd("check_in", "15:00"); v.TryAdd("check_out", "10:00"); v.TryAdd("sort", 0);
        v["slug"] = await store.FreeSlugAsync(BookingStore.TProperties, name, 0, ct);
        var id = await store.Properties.InsertAsync(v, ct);
        ctx.Log($"Objekt #{id} „{name}“ angelegt über eine Operation");
        return Answer("applied", $"Objekt „{name}“ angelegt.", new JsonObject { ["id"] = id, ["slug"] = (string)v["slug"]! }, Undo(HouseRemove.Name, new JsonObject { ["id"] = id }));
    });

    private static Task<JsonNode?> HouseUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var o = Args(args);
        var store = new BookingStore(ctx.Data);
        var h = await HouseAsync(store, o, ct, o.ContainsKey("house") ? "house" : "id");
        var v = HouseValues(o);
        if (v.Count == 0) return Answer("skipped", "Nichts zu ändern.");
        await store.Properties.UpdateAsync(h.Id, v, ct);
        return Answer("applied", $"Objekt „{(v.GetValueOrDefault("name") as string) ?? h.Name}“ geändert.", new JsonObject { ["id"] = h.Id }, Undo(HouseRestore.Name, HouseJson(h)));
    });

    /// <summary>Undo of a create: the admin's own delete — refused while the house has units (they would hang in the air).</summary>
    private static async Task<JsonNode?> HouseRemoveAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var id = Whole(Args(args), "id") ?? 0;
        if (await store.PropertyAsync(id, ct) is not { } p) return Answer("skipped", "Das Objekt gibt es nicht mehr.");
        if ((await store.UnitsAsync(ct)).Any(u => u.PropertyId == p.Id)) return Answer("failed", $"„{p.Name}“ hat inzwischen Einheiten und bleibt.");
        foreach (var s in await store.SeasonsAsync(p.Id, ct)) await store.DeleteSeasonAsync(s.Id, ct);
        foreach (var f in await store.FeesAsync(p.Id, ct)) await store.Fees.DeleteAsync(f.Id, ct);
        await store.Properties.DeleteAsync(p.Id, ct);
        return Answer("applied", $"Objekt „{p.Name}“ entfernt.");
    }

    private static async Task<JsonNode?> HouseRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = Args(args);
        var store = new BookingStore(ctx.Data);
        if (await store.PropertyAsync(Whole(o, "id") ?? 0, ct) is not { } p) return Answer("skipped", "Das Objekt gibt es nicht mehr.");
        var v = new Dictionary<string, object?>();
        foreach (var k in new[] { "name", "address", "country", "check_in", "check_out", "description", "directions", "house_rules", "checkout_info" })
            if (Str(o, k) is { } s) v[k] = s;
        if (Whole(o, "sort") is { } sort) v["sort"] = sort;
        await store.Properties.UpdateAsync(p.Id, v, ct);
        return Answer("applied", $"Objekt „{Str(o, "name") ?? p.Name}“ wiederhergestellt.");
    }

    // ---- units ----------------------------------------------------------------------------------------------------

    private static JsonObject UnitShort(Unit u) => new()
    {
        ["id"] = u.Id, ["house"] = u.PropertyId, ["name"] = u.Name, ["slug"] = u.Slug, ["guests_std"] = u.GuestsStd, ["guests_max"] = u.GuestsMax,
        ["bedrooms"] = u.Bedrooms, ["base_price_cents"] = u.BasePrice, ["base_min_nights"] = u.BaseMinNights, ["active"] = u.Active,
        ["contains"] = new JsonArray(u.Contains.Select(c => (JsonNode)JsonValue.Create(c)).ToArray()),
    };

    /// <summary>Everything an operation may write of a unit — also what <c>unit.restore</c> takes back. Not the way in and not the Wi-Fi (see the class).</summary>
    private static JsonObject UnitJson(Unit u)
    {
        var o = UnitShort(u);
        o["description"] = u.Description; o["image"] = u.Image; o["extra_person_cents"] = u.ExtraPerson; o["buffer_days"] = u.BufferDays;
        o["checklist"] = u.Checklist; o["sort"] = u.Sort;
        return o;
    }

    /// <summary>The unit fields a request sends, cleaned like the admin's form (same limits); only what it names. <paramref name="u"/> is the unit
    /// being changed (null for a new one), <paramref name="house"/> the house it will belong to.</summary>
    private static async Task<Dictionary<string, object?>> UnitValuesAsync(BookingStore store, JsonObject o, List<Unit> all, Unit? u, Property house, CancellationToken ct)
    {
        var v = new Dictionary<string, object?>();
        if (u is null || u.PropertyId != house.Id) v["property_id"] = house.Id;
        if (Str(o, "name") is { } n) v["name"] = B.Clip(n, 120) is { Length: > 0 } name ? name : throw new Refused("Der Name darf nicht leer sein.");
        if (Str(o, "slug") is { Length: > 0 } slug) v["slug"] = await store.FreeSlugAsync(BookingStore.TUnits, slug, u?.Id ?? 0, ct);
        if (Text(o, "description", 4000) is { } d) v["description"] = d;
        if (o.ContainsKey("image")) v["image"] = Image(Str(o, "image"));
        if (IntIn(o, "bedrooms", 0, 50) is { } bed) v["bedrooms"] = bed;
        var std = IntIn(o, "guests_std", 1, 50);
        var max = IntIn(o, "guests_max", 1, 50);
        if (std is not null || max is not null || u is null)
        {
            var s = std ?? u?.GuestsStd ?? 2;
            v["guests_std"] = s;
            v["guests_max"] = Math.Max(s, max ?? u?.GuestsMax ?? s);
        }
        if (IntIn(o, "buffer_days", 0, 14) is { } buf) v["buffer_days"] = buf;
        if (Cents(o, "base_price_cents") is { } bp) v["base_price"] = bp;
        if (IntIn(o, "base_min_nights", 1, 60) is { } mn) v["base_min_nights"] = mn;
        if (Cents(o, "extra_person_cents") is { } ep) v["extra_person"] = ep;
        if (IntIn(o, "sort", -999, 999) is { } sort) v["sort"] = sort;
        if (Text(o, "checklist", 4000) is { } cl) v["checklist"] = cl;
        if (o.ContainsKey("active")) v["active"] = Flag(o, "active") ?? throw new Refused("active ist true oder false.");

        // a "whole house" consists of units of the SAME house that are not whole houses themselves, never of itself (one level)
        if (o["contains"] is JsonArray list)
        {
            if (u is not null && list.Count > 0 && all.Any(x => x.Contains.Contains(u.Id)))
                throw new Refused($"„{u.Name}“ ist selbst Teil eines ganzen Hauses und kann keine Einheiten enthalten.");
            var choices = all.Where(x => x.Id != u?.Id && x.PropertyId == house.Id && x.Contains.Count == 0).ToList();
            var ids = new List<long>();
            foreach (var item in list)
            {
                var key = item is JsonValue iv ? (iv.TryGetValue<string>(out var s) ? s : iv.ToString()) : "";
                var hit = choices.FirstOrDefault(x => B.Num(x.Id) == key.Trim()) ?? choices.FirstOrDefault(x => x.Slug == B.Slug(key))
                    ?? throw new Refused($"contains: „{key}“ ist keine Einheit dieses Objekts, die ein ganzes Haus enthalten kann. Möglich: {Names(choices, x => x.Id + " " + x.Slug)}.");
                if (!ids.Contains(hit.Id)) ids.Add(hit.Id);
            }
            v["contains"] = string.Join(",", ids.Select(B.Num));
        }
        else if (o.ContainsKey("contains") && o["contains"] is not null) throw new Refused("contains ist eine Liste von Einheiten-ids.");
        else if (u is not null && u.PropertyId != house.Id)
            v["contains"] = "";                                                         // moved to another house: the old house's units are not its parts
        else if (u is null) v["contains"] = "";
        return v;
    }

    private static async Task<JsonNode?> UnitListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = Args(args);
        var store = new BookingStore(ctx.Data);
        long? house = null;
        if (Str(o, "house") is { Length: > 0 })
        {
            try { house = (await HouseAsync(store, o, ct)).Id; }
            catch (Refused r) { return Answer("failed", r.Message); }
        }
        var inactive = Flag(o, "includeInactive") ?? true;
        var units = (await store.UnitsAsync(ct)).Where(u => (house is null || u.PropertyId == house) && (inactive || u.Active)).ToList();
        return Answer("applied", units.Count + " Einheit(en), Beträge in Cent " + BookingPlugin.Currency(ctx) + ".", new JsonArray(units.Select(u => (JsonNode)UnitShort(u)).ToArray()));
    }

    private static Task<JsonNode?> UnitReadAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var store = new BookingStore(ctx.Data);
        var all = await store.UnitsAsync(ct);
        var u = UnitOf(all, KeyOf(Args(args), "unit"));
        var data = UnitJson(u);
        var seasons = await store.SeasonsAsync(u.PropertyId, ct);
        var rates = await store.RatesAsync(new[] { u.Id }, ct);
        data["rates"] = new JsonArray(seasons.Select(s => rates.FirstOrDefault(r => r.SeasonId == s.Id) is { } r ? (JsonNode)RateJson(r, u, s) : null).OfType<JsonNode>().ToArray());
        data["currency"] = BookingPlugin.Currency(ctx);
        return Answer("applied", u.Name, data);
    });

    private static Task<JsonNode?> UnitCreateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var o = Args(args);
        if (Str(o, "name") is not { } name || name.Trim().Length == 0) return Answer("failed", "Bitte einen Namen angeben (name).");
        var store = new BookingStore(ctx.Data);
        var house = await HouseAsync(store, o, ct);
        var all = await store.UnitsAsync(ct);
        if (all.FirstOrDefault(u => u.PropertyId == house.Id && string.Equals(u.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } same)
            return Answer("skipped-exists", $"Die Einheit „{same.Name}“ gibt es in „{house.Name}“ schon.", new JsonObject { ["id"] = same.Id, ["slug"] = same.Slug });
        var v = await UnitValuesAsync(store, o, all, null, house, ct);
        v.TryAdd("slug", await store.FreeSlugAsync(BookingStore.TUnits, (string)v["name"]!, 0, ct));
        v.TryAdd("active", true); v.TryAdd("base_min_nights", 1); v.TryAdd("bedrooms", 1);
        var id = await store.Units.InsertAsync(v, ct);
        ctx.Log($"Einheit #{id} „{v["name"]}“ angelegt über eine Operation");
        return Answer("applied", $"Einheit „{v["name"]}“ in „{house.Name}“ angelegt.", new JsonObject { ["id"] = id, ["slug"] = (string)v["slug"]! },
            Undo(UnitRemove.Name, new JsonObject { ["id"] = id }));
    });

    private static Task<JsonNode?> UnitUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var o = Args(args);
        var store = new BookingStore(ctx.Data);
        var all = await store.UnitsAsync(ct);
        var u = UnitOf(all, KeyOf(o, "unit"));
        var house = Str(o, "house") is { Length: > 0 } ? await HouseAsync(store, o, ct) : await store.PropertyAsync(u.PropertyId, ct) ?? await HouseAsync(store, o, ct);
        var v = await UnitValuesAsync(store, o, all, u, house, ct);
        if (v.Count == 0) return Answer("skipped", "Nichts zu ändern.");
        await store.Units.UpdateAsync(u.Id, v, ct);
        var off = v.GetValueOrDefault("active") is false && u.Active;
        return Answer("applied", $"Einheit „{(v.GetValueOrDefault("name") as string) ?? u.Name}“ geändert" + (off ? " (nicht mehr buchbar)." : "."),
            new JsonObject { ["id"] = u.Id, ["slug"] = (v.GetValueOrDefault("slug") as string) ?? u.Slug }, Undo(UnitRestore.Name, UnitJson(u)));
    });

    /// <summary>Undo of a create: the admin's own delete — refused once the unit holds nights (a booking or a block came since).</summary>
    private static async Task<JsonNode?> UnitRemoveAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var all = await store.UnitsAsync(ct);
        var id = Whole(Args(args), "id") ?? 0;
        if (all.FirstOrDefault(x => x.Id == id) is not { } u) return Answer("skipped", "Die Einheit gibt es nicht mehr.");
        if ((await store.NightsAsync(new[] { u.Id }, DateOnly.MinValue, DateOnly.MaxValue, ct)).Count > 0)
            return Answer("failed", $"„{u.Name}“ hat inzwischen belegte Nächte und bleibt (stattdessen mit active: false abschalten).");
        await store.Units.DeleteAsync(u.Id, ct);
        foreach (var other in all.Where(x => x.Contains.Contains(u.Id)))
            await store.Units.UpdateAsync(other.Id, new Dictionary<string, object?> { ["contains"] = string.Join(",", other.Contains.Where(c => c != u.Id)) }, ct);
        return Answer("applied", $"Einheit „{u.Name}“ entfernt.");
    }

    private static async Task<JsonNode?> UnitRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = Args(args);
        var store = new BookingStore(ctx.Data);
        if (await store.UnitAsync(Whole(o, "id") ?? 0, ct) is not { } u) return Answer("skipped", "Die Einheit gibt es nicht mehr.");
        var v = new Dictionary<string, object?>();
        foreach (var k in new[] { "name", "slug", "description", "image", "checklist" })
            if (Str(o, k) is { } s) v[k] = s;
        foreach (var (json, column) in new[] { ("house", "property_id"), ("bedrooms", "bedrooms"), ("guests_std", "guests_std"), ("guests_max", "guests_max"), ("buffer_days", "buffer_days"),
                     ("base_price_cents", "base_price"), ("base_min_nights", "base_min_nights"), ("extra_person_cents", "extra_person"), ("sort", "sort") })
            if (Whole(o, json) is { } n) v[column] = n;
        if (Flag(o, "active") is { } active) v["active"] = active;
        if (o["contains"] is JsonArray list) v["contains"] = string.Join(",", list.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0));
        await store.Units.UpdateAsync(u.Id, v, ct);
        return Answer("applied", $"Einheit „{Str(o, "name") ?? u.Name}“ wiederhergestellt.");
    }

    // ---- seasons and their price table ---------------------------------------------------------------------------

    private static JsonObject RateJson(Rate r, Unit u, Season s) => new()
    {
        ["season"] = s.Id, ["season_name"] = s.Name, ["unit"] = u.Id, ["unit_name"] = u.Name, ["price_cents"] = r.Price, ["min_nights"] = r.MinNights,
        ["arrival_days"] = new JsonArray(r.ArrivalDays.Select(d => (JsonNode)JsonValue.Create(d)).ToArray()), ["week_price_cents"] = r.WeekPrice, ["extra_person_cents"] = r.ExtraPerson,
    };

    /// <summary>A season with its whole price table (every unit of the house that has a rate there) — also what <c>season.restore</c> takes back.</summary>
    private static JsonObject SeasonJson(Season s, IReadOnlyList<Unit> units, IReadOnlyList<Rate> rates) => new()
    {
        ["id"] = s.Id, ["house"] = s.PropertyId, ["name"] = s.Name, ["from"] = B.Iso(s.From), ["to"] = B.Iso(s.To), ["rank"] = s.Rank, ["color"] = s.Color,
        ["rates"] = new JsonArray(rates.Where(r => r.SeasonId == s.Id).Select(r => units.FirstOrDefault(u => u.Id == r.UnitId) is { } u ? (JsonNode)RateJson(r, u, s) : null).OfType<JsonNode>().ToArray()),
    };

    private static async Task<JsonArray> SeasonsJsonAsync(BookingStore store, Property h, List<Unit> units, CancellationToken ct)
    {
        var rates = await store.RatesAsync(units.Select(u => u.Id), ct);
        return new JsonArray((await store.SeasonsAsync(h.Id, ct)).Select(s => (JsonNode)SeasonJson(s, units, rates)).ToArray());
    }

    private static IReadOnlyList<int> ArrivalDays(JsonNode? n)
    {
        IEnumerable<string> parts = n switch
        {
            null => Array.Empty<string>(),
            JsonArray a => a.Select(x => x?.ToString() ?? ""),
            JsonValue v when v.TryGetValue<string>(out var s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries),
            _ => new[] { n.ToString() }
        };
        var days = new List<int>();
        foreach (var p in parts.Select(x => x.Trim()).Where(x => x.Length > 0))
            days.Add(int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 7 ? d : throw new Refused($"arrival_days: Wochentage 1 (Montag) bis 7 (Sonntag), nicht „{p}“."));
        return days.Distinct().OrderBy(d => d).ToList();
    }

    /// <summary>One rate a request sends, checked BEFORE anything is written (so a bad line leaves the whole table as it was).</summary>
    private sealed record RateChange(Unit Unit, long? Price, int MinNights, IReadOnlyList<int> Days, long Week, long Extra);

    private static List<RateChange> RateChanges(JsonObject o, List<Unit> houseUnits, IReadOnlyList<Rate> existing, long seasonId)
    {
        var changes = new List<RateChange>();
        if (o["rates"] is null) return changes;
        if (o["rates"] is not JsonArray list) throw new Refused("rates ist eine Liste.");
        foreach (var item in list)
        {
            if (item is not JsonObject r) throw new Refused("Jede Rate ist ein Objekt mit unit und price_cents.");
            var unit = UnitOf(houseUnits, Str(r, "unit"));
            var old = existing.FirstOrDefault(x => x.UnitId == unit.Id && x.SeasonId == seasonId);
            long? price;
            if (r.TryGetPropertyValue("price_cents", out var pn) && pn is null) price = null;          // null takes the rate away: the base price applies
            else price = Cents(r, "price_cents") ?? old?.Price ?? throw new Refused($"Rate für „{unit.Name}“: price_cents fehlt.");
            changes.Add(new RateChange(unit, price,
                IntIn(r, "min_nights", 1, 60) ?? old?.MinNights ?? unit.BaseMinNights,
                r.ContainsKey("arrival_days") ? ArrivalDays(r["arrival_days"]) : old?.ArrivalDays ?? Array.Empty<int>(),
                Cents(r, "week_price_cents") ?? old?.WeekPrice ?? 0,
                Cents(r, "extra_person_cents") ?? old?.ExtraPerson ?? unit.ExtraPerson));
        }
        return changes;
    }

    private static async Task ApplyRatesAsync(BookingStore store, long seasonId, IEnumerable<RateChange> changes, CancellationToken ct)
    {
        foreach (var c in changes) await store.SetRateAsync(c.Unit.Id, seasonId, c.Price, c.MinNights, c.Days, c.Week, c.Extra, ct);
    }

    /// <summary>The season fields a request sends (only what it names), cleaned like the admin's form; a period given backwards is turned round, as there.</summary>
    private static Dictionary<string, object?> SeasonValues(JsonObject o, Season? s)
    {
        var v = new Dictionary<string, object?>();
        if (Str(o, "name") is { } n) v["name"] = B.Clip(n, 80) is { Length: > 0 } name ? name : throw new Refused("Der Name darf nicht leer sein.");
        if (o.ContainsKey("from") || o.ContainsKey("to") || s is null)
        {
            var from = o.ContainsKey("from") || s is null ? Day(o, "from") : B.Iso(s.From);
            var to = o.ContainsKey("to") || s is null ? Day(o, "to") : B.Iso(s.To);
            if (string.CompareOrdinal(to, from) < 0) (from, to) = (to, from);
            v["from_day"] = from; v["to_day"] = to;
        }
        if (IntIn(o, "rank", 0, 99) is { } rank) v["rank"] = rank;
        if (Str(o, "color") is { } c) v["color"] = HexColor.IsMatch(c.Trim()) ? c.Trim() : throw new Refused("color: eine Farbe wie #2563eb.");
        return v;
    }

    private static async Task<JsonNode?> SeasonListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = Args(args);
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        if (Str(o, "house") is { Length: > 0 })
        {
            try { houses = new List<Property> { await HouseAsync(store, o, ct) }; }
            catch (Refused r) { return Answer("failed", r.Message); }
        }
        var all = await store.UnitsAsync(ct);
        var arr = new JsonArray();
        foreach (var h in houses)
            foreach (var s in await SeasonsJsonAsync(store, h, all.Where(u => u.PropertyId == h.Id).ToList(), ct))
                arr.Add(s!.DeepClone());
        return Answer("applied", arr.Count + " Saison(s), Beträge in Cent " + BookingPlugin.Currency(ctx) + ".", arr);
    }

    private static Task<JsonNode?> SeasonCreateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var o = Args(args);
        var store = new BookingStore(ctx.Data);
        var house = await HouseAsync(store, o, ct);
        if (Str(o, "name") is not { } name || name.Trim().Length == 0) return Answer("failed", "Bitte einen Namen angeben (name).");
        var v = SeasonValues(o, null);
        var units = (await store.UnitsAsync(ct)).Where(u => u.PropertyId == house.Id).ToList();
        var changes = RateChanges(o, units, Array.Empty<Rate>(), 0);
        if ((await store.SeasonsAsync(house.Id, ct)).FirstOrDefault(s => string.Equals(s.Name, (string)v["name"]!, StringComparison.OrdinalIgnoreCase)
                && B.Iso(s.From) == (string)v["from_day"]! && B.Iso(s.To) == (string)v["to_day"]!) is { } same)
            return Answer("skipped-exists", $"Die Saison „{same.Name}“ gibt es in diesem Zeitraum schon (season.update ändert ihre Preise).", new JsonObject { ["id"] = same.Id });
        v["property_id"] = house.Id; v.TryAdd("rank", 1); v.TryAdd("color", "#2563eb");
        var id = await store.Seasons.InsertAsync(v, ct);
        await ApplyRatesAsync(store, id, changes, ct);
        ctx.Log($"Saison #{id} „{v["name"]}“ angelegt über eine Operation");
        return Answer("applied", $"Saison „{v["name"]}“ ({B.Date((string)v["from_day"]!)} – {B.Date((string)v["to_day"]!)}) angelegt" + (changes.Count > 0 ? $", {changes.Count} Preis(e)." : "."),
            new JsonObject { ["id"] = id }, Undo(SeasonRemove.Name, new JsonObject { ["id"] = id }));
    });

    private static async Task<(Season Season, List<Unit> Units, List<Rate> Rates)> SeasonOfAsync(BookingStore store, JsonObject o, CancellationToken ct)
    {
        var id = Whole(o, "season") ?? Whole(o, "id") ?? throw new Refused("Bitte die Saison angeben (season: id).");
        var s = await store.SeasonAsync(id, ct) ?? throw new Refused($"Saison {id} nicht gefunden.");
        var units = (await store.UnitsAsync(ct)).Where(u => u.PropertyId == s.PropertyId).ToList();
        return (s, units, await store.RatesAsync(units.Select(u => u.Id), ct));
    }

    private static Task<JsonNode?> SeasonUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var o = Args(args);
        var store = new BookingStore(ctx.Data);
        var (s, units, rates) = await SeasonOfAsync(store, o, ct);
        var v = SeasonValues(o, s);
        var changes = RateChanges(o, units, rates, s.Id);
        if (v.Count == 0 && changes.Count == 0) return Answer("skipped", "Nichts zu ändern.");
        var before = SeasonJson(s, units, rates);
        if (v.Count > 0) await store.Seasons.UpdateAsync(s.Id, v, ct);
        await ApplyRatesAsync(store, s.Id, changes, ct);
        return Answer("applied", $"Saison „{(v.GetValueOrDefault("name") as string) ?? s.Name}“ geändert" + (changes.Count > 0 ? $", {changes.Count} Preis(e)." : "."),
            new JsonObject { ["id"] = s.Id }, Undo(SeasonRestore.Name, before));
    });

    private static Task<JsonNode?> SeasonDeleteAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var store = new BookingStore(ctx.Data);
        var (s, units, rates) = await SeasonOfAsync(store, Args(args), ct);
        await store.DeleteSeasonAsync(s.Id, ct);
        return Answer("applied", $"Saison „{s.Name}“ gelöscht.", new JsonObject { ["id"] = s.Id }, Undo(SeasonRestore.Name, SeasonJson(s, units, rates)));
    });

    private static async Task<JsonNode?> SeasonRemoveAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        if (await store.SeasonAsync(Whole(Args(args), "id") ?? 0, ct) is not { } s) return Answer("skipped", "Die Saison gibt es nicht mehr.");
        await store.DeleteSeasonAsync(s.Id, ct);
        return Answer("applied", $"Saison „{s.Name}“ entfernt.");
    }

    /// <summary>Puts a season back as recorded — its fields and its WHOLE price table (rates added since are taken away again). A deleted season comes
    /// back as a new row (SQLite never reuses an id), with the same prices.</summary>
    private static async Task<JsonNode?> SeasonRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = Args(args);
        var store = new BookingStore(ctx.Data);
        var houseId = Whole(o, "house") ?? 0;
        if (await store.PropertyAsync(houseId, ct) is null) return Answer("skipped", "Das Objekt der Saison gibt es nicht mehr.");
        var v = new Dictionary<string, object?>
        {
            ["property_id"] = houseId, ["name"] = Str(o, "name") ?? "", ["from_day"] = Str(o, "from") ?? "", ["to_day"] = Str(o, "to") ?? "",
            ["rank"] = Whole(o, "rank") ?? 1, ["color"] = Str(o, "color") ?? ""
        };
        var id = await store.SeasonAsync(Whole(o, "id") ?? 0, ct) is { } s ? s.Id : 0;
        if (id > 0) await store.Seasons.UpdateAsync(id, v, ct); else id = await store.Seasons.InsertAsync(v, ct);
        var recorded = (o["rates"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        foreach (var u in (await store.UnitsAsync(ct)).Where(u => u.PropertyId == houseId))
        {
            var r = recorded.FirstOrDefault(x => Whole(x, "unit") == u.Id);
            if (r is null) await store.SetRateAsync(u.Id, id, null, 1, Array.Empty<int>(), 0, 0, ct);
            else await store.SetRateAsync(u.Id, id, Whole(r, "price_cents") ?? 0, (int)(Whole(r, "min_nights") ?? 1), ArrivalDays(r["arrival_days"]),
                Whole(r, "week_price_cents") ?? 0, Whole(r, "extra_person_cents") ?? 0, ct);
        }
        return Answer("applied", $"Saison „{v["name"]}“ wiederhergestellt.");
    }

    // ---- fees -----------------------------------------------------------------------------------------------------

    private static JsonObject FeeJson(Fee f) => new()
    {
        ["id"] = f.Id, ["house"] = f.PropertyId, ["unit"] = f.UnitId, ["name"] = f.Name, ["amount_cents"] = f.Amount, ["per"] = f.Per, ["optional"] = f.Optional,
        ["tourist_tax"] = f.TouristTax, ["children"] = f.Children, ["sort"] = f.Sort,
    };

    private static Dictionary<string, object?> FeeValues(JsonObject o, List<Unit> houseUnits)
    {
        var v = new Dictionary<string, object?>();
        if (Str(o, "name") is { } n) v["name"] = B.Clip(n, 120) is { Length: > 0 } name ? name : throw new Refused("Der Name darf nicht leer sein.");
        if (Cents(o, "amount_cents") is { } a) v["amount"] = a;
        if (Str(o, "per") is { } per) v["per"] = FeePer.Keys.Contains(per.Trim()) ? per.Trim() : throw new Refused($"per: einer von {string.Join(", ", FeePer.Keys)}.");
        if (Str(o, "unit") is { } unit) v["unit_id"] = unit.Trim() is "" or "0" ? 0L : UnitOf(houseUnits, unit).Id;
        foreach (var k in new[] { "optional", "tourist_tax" })
            if (o.ContainsKey(k)) v[k] = Flag(o, k) ?? throw new Refused($"{k} ist true oder false.");
        if (Str(o, "children") is { } c) v["children"] = c.Trim() is "full" or "half" or "free" ? c.Trim() : throw new Refused("children: full, half oder free.");
        if (IntIn(o, "sort", -999, 999) is { } sort) v["sort"] = sort;
        return v;
    }

    private static async Task<JsonNode?> FeeListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = Args(args);
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        if (Str(o, "house") is { Length: > 0 })
        {
            try { houses = new List<Property> { await HouseAsync(store, o, ct) }; }
            catch (Refused r) { return Answer("failed", r.Message); }
        }
        var arr = new JsonArray();
        foreach (var h in houses)
            foreach (var f in await store.FeesAsync(h.Id, ct)) arr.Add(FeeJson(f));
        return Answer("applied", arr.Count + " Gebühr(en), Beträge in Cent " + BookingPlugin.Currency(ctx) + ".", arr);
    }

    private static Task<JsonNode?> FeeCreateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var o = Args(args);
        var store = new BookingStore(ctx.Data);
        var house = await HouseAsync(store, o, ct);
        var units = (await store.UnitsAsync(ct)).Where(u => u.PropertyId == house.Id).ToList();
        var v = FeeValues(o, units);
        if (v.GetValueOrDefault("name") is not string name) return Answer("failed", "Bitte einen Namen angeben (name).");
        if (!v.ContainsKey("amount")) return Answer("failed", "Bitte den Betrag angeben (amount_cents, ganze Cent).");
        v["property_id"] = house.Id; v.TryAdd("per", FeePer.Stay); v.TryAdd("unit_id", 0L); v.TryAdd("optional", false); v.TryAdd("tourist_tax", false); v.TryAdd("children", "full"); v.TryAdd("sort", 0);
        if ((await store.FeesAsync(house.Id, ct)).FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) && f.UnitId == (long)v["unit_id"]!) is { } same)
            return Answer("skipped-exists", $"Die Gebühr „{same.Name}“ gibt es schon (fee.update ändert sie).", new JsonObject { ["id"] = same.Id });
        var id = await store.Fees.InsertAsync(v, ct);
        ctx.Log($"Gebühr #{id} „{name}“ angelegt über eine Operation");
        return Answer("applied", $"Gebühr „{name}“ angelegt.", new JsonObject { ["id"] = id }, Undo(FeeRemove.Name, new JsonObject { ["id"] = id }));
    });

    private static async Task<Fee> FeeOfAsync(BookingStore store, JsonObject o, CancellationToken ct)
    {
        var id = Whole(o, "fee") ?? Whole(o, "id") ?? throw new Refused("Bitte die Gebühr angeben (fee: id).");
        return await FeeAsync(store, id, ct) ?? throw new Refused($"Gebühr {id} nicht gefunden.");
    }

    private static Task<JsonNode?> FeeUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var o = Args(args);
        var store = new BookingStore(ctx.Data);
        var f = await FeeOfAsync(store, o, ct);
        var v = FeeValues(o, (await store.UnitsAsync(ct)).Where(u => u.PropertyId == f.PropertyId).ToList());
        if (v.Count == 0) return Answer("skipped", "Nichts zu ändern.");
        await store.Fees.UpdateAsync(f.Id, v, ct);
        return Answer("applied", $"Gebühr „{(v.GetValueOrDefault("name") as string) ?? f.Name}“ geändert.", new JsonObject { ["id"] = f.Id }, Undo(FeeRestore.Name, FeeJson(f)));
    });

    private static Task<JsonNode?> FeeDeleteAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var store = new BookingStore(ctx.Data);
        var f = await FeeOfAsync(store, Args(args), ct);
        await store.Fees.DeleteAsync(f.Id, ct);
        return Answer("applied", $"Gebühr „{f.Name}“ gelöscht.", new JsonObject { ["id"] = f.Id }, Undo(FeeRestore.Name, FeeJson(f)));
    });

    private static async Task<JsonNode?> FeeRemoveAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        if (await FeeAsync(store, Whole(Args(args), "id") ?? 0, ct) is not { } f) return Answer("skipped", "Die Gebühr gibt es nicht mehr.");
        await store.Fees.DeleteAsync(f.Id, ct);
        return Answer("applied", $"Gebühr „{f.Name}“ entfernt.");
    }

    /// <summary>Puts a fee back as recorded; a deleted one comes back as a new row.</summary>
    private static async Task<JsonNode?> FeeRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = Args(args);
        var store = new BookingStore(ctx.Data);
        var houseId = Whole(o, "house") ?? 0;
        if (await store.PropertyAsync(houseId, ct) is null) return Answer("skipped", "Das Objekt der Gebühr gibt es nicht mehr.");
        var v = new Dictionary<string, object?>
        {
            ["property_id"] = houseId, ["unit_id"] = Whole(o, "unit") ?? 0, ["name"] = Str(o, "name") ?? "", ["amount"] = Whole(o, "amount_cents") ?? 0,
            ["per"] = Str(o, "per") ?? FeePer.Stay, ["optional"] = Flag(o, "optional") ?? false, ["tourist_tax"] = Flag(o, "tourist_tax") ?? false,
            ["children"] = Str(o, "children") ?? "full", ["sort"] = Whole(o, "sort") ?? 0
        };
        if (await FeeAsync(store, Whole(o, "id") ?? 0, ct) is { } f) await store.Fees.UpdateAsync(f.Id, v, ct); else await store.Fees.InsertAsync(v, ct);
        return Answer("applied", $"Gebühr „{v["name"]}“ wiederhergestellt.");
    }

    // ---- the price of a stay ----------------------------------------------------------------------------------------

    /// <summary>The bill of a stay and whether it is free — exactly what <c>GET /buchung/preis</c> answers, from the same store calls.</summary>
    private static Task<JsonNode?> PriceCheckAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct) => Guard(async () =>
    {
        var o = Args(args);
        var store = new BookingStore(ctx.Data);
        var unit = UnitOf(await store.UnitsAsync(ct), KeyOf(o, "unit"));
        if (!unit.Active) return Answer("failed", $"„{unit.Name}“ ist nicht buchbar (abgeschaltet).");
        var arrival = B.Day(Day(o, "arrival"))!.Value;
        var departure = B.Day(Day(o, "departure"))!.Value;
        var extras = new HashSet<long>();
        if (o["extras"] is JsonArray ex)
            foreach (var x in ex) extras.Add(long.TryParse(x?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var e) && e > 0 ? e : throw new Refused("extras: ids wählbarer Gebühren."));
        var stay = new Stay(arrival, departure, IntIn(o, "adults", 0, 50) ?? 2, IntIn(o, "children", 0, 50) ?? 0, extras);
        var cur = BookingPlugin.Currency(ctx);
        var quote = (await store.QuoteAsync(unit.Id, stay, cur, ct, ctx.T))!;
        var free = stay.Nights is > 0 and <= Pricing.MaxNights && await store.IsFreeAsync(unit, arrival, departure, ct);
        var lines = new JsonArray();
        foreach (var l in quote.Lines)
            lines.Add(new JsonObject { ["kind"] = l.Kind, ["text"] = l.Text, ["quantity"] = l.Quantity, ["unit_cents"] = l.Unit, ["amount_cents"] = l.Amount, ["detail"] = l.Detail });
        var data = new JsonObject
        {
            ["ok"] = quote.Ok && free, ["available"] = free, ["unit"] = unit.Id, ["unit_slug"] = unit.Slug, ["nights"] = stay.Nights, ["currency"] = cur,
            ["total_cents"] = quote.Total, ["total_text"] = B.Money(ctx.T, quote.Total, cur), ["min_nights"] = quote.MinNights,
            ["arrival_days"] = new JsonArray(quote.ArrivalDays.Select(d => (JsonNode)JsonValue.Create(d)).ToArray()),
            ["problems"] = new JsonArray(quote.Problems.Select(p => (JsonNode)JsonValue.Create(p)).ToArray()), ["lines"] = lines
        };
        var verdict = !free ? "belegt" : quote.Ok ? "frei und buchbar" : "frei, passt aber nicht: " + string.Join(" ", quote.Problems);
        return Answer("applied", $"{unit.Name}, {stay.Nights} Nächte: {B.Money(quote.Total, cur)} — {verdict}.", data);
    });
}
