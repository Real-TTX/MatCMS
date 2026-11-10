using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Booking;

/// <summary>One person of a stay with what the authorities want to know. Which fields are asked depends on the house's country (<see cref="BookingCheckin.Profiles"/>).</summary>
public sealed record Traveller(int Idx, IReadOnlyDictionary<string, string> Values)
{
    public string Get(string key) => Values.TryGetValue(key, out var v) ? v : "";
    public string Name => (Get("first") + " " + Get("last")).Trim();
}

/// <summary>
/// The online check-in (phase B5): some days before arrival the guest gets a link to enter every traveller — the fields the country's registration asks
/// for (Croatia's eVisitor, Germany's Meldeschein, Italy's Alloggiati Web, or the bare minimum) —, the arrival time and how they come. The host exports
/// the list (CSV per booking or per period, a registration form as PDF). The data is deleted by a job after the retention period; the booking stays.
/// A guest may tick "remember for my next stay" — then (and only then) the travellers are kept on the guest card and fill the next check-in.
/// </summary>
public static class BookingCheckin
{
    public const string ProfileKey = "checkinProfile", DaysKey = "checkinDays", RetentionKey = "checkinRetentionMonths";

    /// <summary>Every field there is: key, label, kind (text, date, select), options.</summary>
    public static readonly (string Key, string Label, string Kind, string[] Options)[] Fields =
    {
        ("first", "Vorname", "text", Array.Empty<string>()), ("last", "Nachname", "text", Array.Empty<string>()),
        ("gender", "Geschlecht", "select", new[] { "weiblich", "männlich", "divers" }), ("born", "Geburtsdatum", "date", Array.Empty<string>()),
        ("birthplace", "Geburtsort", "text", Array.Empty<string>()), ("nationality", "Staatsangehörigkeit", "text", Array.Empty<string>()),
        ("doc_type", "Ausweisart", "select", new[] { "Personalausweis", "Reisepass" }), ("doc_no", "Ausweisnummer", "text", Array.Empty<string>()),
        ("street", "Straße und Hausnummer", "text", Array.Empty<string>()), ("zip", "PLZ", "text", Array.Empty<string>()),
        ("city", "Wohnort", "text", Array.Empty<string>()), ("res_country", "Wohnland", "text", Array.Empty<string>())
    };

    /// <summary>What a country asks: the fields for everybody and the ones only for the first (main) traveller.</summary>
    public static readonly Dictionary<string, (string Label, string[] All, string[] MainOnly)> Profiles = new()
    {
        ["simple"] = ("Einfach (Name, Geburtsdatum, Staatsangehörigkeit)", new[] { "first", "last", "born", "nationality" }, Array.Empty<string>()),
        ["hr"] = ("Kroatien (eVisitor)", new[] { "first", "last", "gender", "born", "nationality", "res_country", "city", "doc_type", "doc_no" }, Array.Empty<string>()),
        ["de"] = ("Deutschland/Österreich (Meldeschein)", new[] { "first", "last", "born", "nationality" }, new[] { "street", "zip", "city", "res_country" }),
        ["it"] = ("Italien (Alloggiati Web)", new[] { "first", "last", "gender", "born", "birthplace", "nationality", "doc_type", "doc_no" }, Array.Empty<string>())
    };

    public static void Declare(ISettingsBuilder s) => s
        .Add(new SettingSpec(ProfileKey, "Meldedaten", SettingKind.Choice)
        {
            Group = "Check-in und Gästemails", Default = "auto",
            Options = new[] { new FieldOption("auto", "nach dem Land des Objekts") }.Concat(Profiles.Select(p => new FieldOption(p.Key, p.Value.Label))).ToArray(),
            Help = "Welche Angaben der Online-Check-in je Person verlangt."
        })
        .Add(new SettingSpec(DaysKey, "Check-in-Link (Tage vor Anreise)", SettingKind.Number) { Default = "7", Min = 1, Max = 60 })
        .Add(new SettingSpec(RetentionKey, "Meldedaten löschen nach (Monaten)", SettingKind.Number) { Default = "12", Min = 1, Max = 120, Help = "Gezählt ab der Abreise. Die Buchung bleibt, nur die Angaben der Personen werden gelöscht." });

    public static string ProfileFor(IPluginContext ctx, Property? house)
    {
        var chosen = ctx.Settings.Get(ProfileKey) ?? "auto";
        if (Profiles.ContainsKey(chosen)) return chosen;
        return house?.Country switch { "HR" => "hr", "DE" or "AT" => "de", "IT" => "it", _ => "simple" };
    }

    public static IEnumerable<string> FieldsOf(string profile, int idx) =>
        Profiles[profile].All.Concat(idx == 0 ? Profiles[profile].MainOnly : Array.Empty<string>());

    public static string Label(string key) => Fields.FirstOrDefault(f => f.Key == key).Label ?? key;

    // ---- data -------------------------------------------------------------------------------------------------------

    public static readonly string[] Columns = Fields.Select(f => f.Key).ToArray();

    public static async Task<List<Traveller>> TravellersAsync(IPluginData data, long bookingId, CancellationToken ct = default) =>
        (await data.Table(BookingStore.TTravellers).QueryAsync(DataQuery.Everything.Eq("booking_id", bookingId).OrderBy("idx"), ct))
            .Select(r => new Traveller((int)r.Int("idx"), Columns.ToDictionary(c => c, c => r.Text(c)))).ToList();

    /// <summary>Writes the travellers of a booking (all at once, replacing what was there) and what the check-in says about the arrival.</summary>
    public static async Task SaveAsync(IPluginData data, Booking b, IReadOnlyList<Traveller> people, string arrivalTime, string travel, bool remember, CancellationToken ct = default)
    {
        await data.InTransactionAsync(async tx =>
        {
            await tx.Table(BookingStore.TTravellers).DeleteWhereAsync(DataQuery.Everything.Eq("booking_id", b.Id), ct);
            foreach (var t in people)
            {
                var row = new Dictionary<string, object?> { ["booking_id"] = b.Id, ["idx"] = t.Idx, ["created"] = DateTime.UtcNow };
                foreach (var c in Columns) row[c] = B.Clip(t.Get(c), 120);
                await tx.Table(BookingStore.TTravellers).InsertAsync(row, ct);
            }
            await tx.Table(BookingStore.TBookings).UpdateAsync(b.Id, new Dictionary<string, object?>
            {
                ["checkin_at"] = DateTime.UtcNow, ["arrival_time"] = B.Clip(arrivalTime, 40), ["travel"] = B.Clip(travel, 200)
            }, ct);
            if (b.GuestId > 0)
            {
                // kept on the guest card ONLY when the guest asked for it; otherwise what an earlier stay remembered goes too
                JsonArray? keep = null;
                if (remember)
                {
                    keep = new JsonArray();
                    foreach (var t in people)
                    {
                        var o = new JsonObject();
                        foreach (var c in Columns) o[c] = t.Get(c);
                        keep.Add(o);
                    }
                }
                var card = await tx.Table(BookingStore.TGuests).GetAsync(b.GuestId, ct);
                if (card is not null)
                    await tx.Table(BookingStore.TGuests).UpdateAsync(b.GuestId, BookingFederation.StampLocal(card, new Dictionary<string, object?> { ["checkin"] = keep?.ToJsonString() ?? "", ["updated"] = DateTime.UtcNow }, DateTime.UtcNow), ct);
            }
        }, ct);
        await BookingFlow.NoteAsync(data, b.Id, "Online-Check-in ausgefüllt (" + people.Count + (people.Count == 1 ? " Person" : " Personen") + ")" + (remember ? ", für den nächsten Aufenthalt gemerkt" : ""), ct);
    }

    /// <summary>What the guest card remembers from an earlier check-in (only when the guest ticked it).</summary>
    public static async Task<List<Traveller>> RememberedAsync(IPluginData data, long guestId, CancellationToken ct = default)
    {
        if (guestId <= 0 || await data.Table(BookingStore.TGuests).GetAsync(guestId, ct) is not { } g || g.Json("checkin") is not JsonArray arr) return new();
        return arr.Select((n, i) => new Traveller(i, Columns.ToDictionary(c => c, c => n?[c] is JsonValue v && v.TryGetValue<string>(out var x) ? x : ""))).ToList();
    }

    /// <summary>Deletes the travellers of stays that ended longer ago than the retention period. Returns how many bookings were cleared.</summary>
    public static async Task<int> PurgeAsync(IPluginContext ctx, CancellationToken ct)
    {
        var months = double.TryParse(ctx.Settings.Get(RetentionKey), NumberStyles.Number, CultureInfo.InvariantCulture, out var m) && m >= 1 ? (int)m : 12;
        var limit = B.Today.AddMonths(-months);
        var ids = (await ctx.Data.Table(BookingStore.TTravellers).QueryAsync(DataQuery.Everything, ct)).Select(r => r.Int("booking_id")).Distinct().ToList();
        var n = 0;
        foreach (var id in ids)
        {
            if (await BookingFlow.GetAsync(ctx.Data, id, ct) is { } b && b.Departure >= limit) continue;
            await ctx.Data.Table(BookingStore.TTravellers).DeleteWhereAsync(DataQuery.Everything.Eq("booking_id", id), ct);
            await ctx.Data.Table(BookingStore.TBookings).UpdateAsync(id, new Dictionary<string, object?> { ["travel"] = "" }, ct);
            await BookingFlow.NoteAsync(ctx.Data, id, "Meldedaten nach Ablauf der Frist (" + months + " Monate) gelöscht.", ct);
            n++;
        }
        return n;
    }

    // ---- the guest's form ---------------------------------------------------------------------------------------------

    public static readonly string[] ArrivalTimes = { "", "vor 14 Uhr (nach Absprache)", "14–16 Uhr", "16–18 Uhr", "18–20 Uhr", "20–22 Uhr", "nach 22 Uhr" };

    /// <summary>Reads the posted travellers. Returns them, or the first problem in words.</summary>
    public static (List<Traveller> People, string? Problem) Read(RequestData f, string profile, int count)
    {
        var list = new List<Traveller>();
        for (var i = 0; i < count; i++)
        {
            var values = new Dictionary<string, string>();
            foreach (var key in FieldsOf(profile, i))
            {
                var v = B.Clip(f.F("t" + i + "_" + key), 120);
                var def = Fields.First(x => x.Key == key);
                if (v.Length == 0) return (list, "Bitte bei Person " + (i + 1) + " „" + def.Label + "“ angeben.");
                if (def.Kind == "date" && (B.Day(v) is not { } d || d > B.Today || d < B.Today.AddYears(-120))) return (list, "Bitte bei Person " + (i + 1) + " ein gültiges Geburtsdatum angeben.");
                if (def.Kind == "select" && !def.Options.Contains(v)) return (list, "Bitte bei Person " + (i + 1) + " „" + def.Label + "“ auswählen.");
                values[key] = v;
            }
            list.Add(new Traveller(i, values));
        }
        return (list, null);
    }

    /// <summary>The form's HTML (the guest page's look — the site's variables). Prefilled from what was entered or remembered.</summary>
    public static string Form(string profile, int count, IReadOnlyList<Traveller> prefill, string arrivalTime, string travel, string antiforgery, string? problem)
    {
        var sb = new StringBuilder();
        if (problem is not null) sb.Append("<p class=\"bk-alert\">").Append(B.H(problem)).Append("</p>");
        sb.Append("<form method=\"post\" class=\"bk-form\">").Append(antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"checkin\">");
        for (var i = 0; i < count; i++)
        {
            var t = prefill.FirstOrDefault(x => x.Idx == i);
            sb.Append("<fieldset class=\"bk-card\"><legend><strong>Person ").Append(i + 1).Append(i == 0 ? " (Hauptgast)" : "").Append("</strong></legend><div class=\"bk-row\">");
            foreach (var key in FieldsOf(profile, i))
            {
                var def = Fields.First(x => x.Key == key);
                var name = "t" + i + "_" + key;
                var value = t?.Get(key) ?? "";
                sb.Append("<label>").Append(B.H(def.Label));
                if (def.Kind == "select")
                {
                    sb.Append("<select name=\"").Append(name).Append("\" required><option value=\"\">bitte wählen</option>");
                    foreach (var o in def.Options) sb.Append("<option").Append(o == value ? " selected" : "").Append('>').Append(B.H(o)).Append("</option>");
                    sb.Append("</select>");
                }
                else sb.Append("<input name=\"").Append(name).Append("\" type=\"").Append(def.Kind == "date" ? "date" : "text").Append("\" value=\"").Append(B.H(value)).Append("\" required maxlength=\"120\">");
                sb.Append("</label>");
            }
            sb.Append("</div></fieldset>");
        }
        sb.Append("<div class=\"bk-row\"><label>Voraussichtliche Ankunft<select name=\"arrival_time\">");
        foreach (var a in ArrivalTimes) sb.Append("<option").Append(a == arrivalTime ? " selected" : "").Append(" value=\"").Append(B.H(a)).Append("\">").Append(a.Length == 0 ? "noch unklar" : B.H(a)).Append("</option>");
        sb.Append("</select></label><label>Anreise mit (z. B. Auto mit Kennzeichen, Fähre, Flug)<input name=\"travel\" maxlength=\"200\" value=\"").Append(B.H(travel)).Append("\"></label></div>");
        sb.Append("<label class=\"bk-check\"><input type=\"checkbox\" name=\"remember\" value=\"1\"> <span>Für meinen nächsten Aufenthalt merken (sonst werden die Angaben nach der gesetzlichen Frist gelöscht).</span></label>");
        sb.Append("<p><button class=\"bk-btn\" type=\"submit\">Check-in absenden</button></p></form>");
        return sb.ToString();
    }

    // ---- exports ------------------------------------------------------------------------------------------------------

    private static string C(string s) { s ??= ""; if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }

    /// <summary>The registration list as CSV: one row per traveller, all fields (empty where the country does not ask), the stay in front.</summary>
    public static string Csv(IEnumerable<(Booking B, string Unit, IReadOnlyList<Traveller> People)> stays)
    {
        var sb = new StringBuilder("﻿" + string.Join(";", new[] { "Anreise", "Abreise", "Unterkunft", "Buchung", "Person" }.Concat(Fields.Select(f => f.Label)).Select(C)) + "\r\n");
        foreach (var (b, unit, people) in stays)
            foreach (var t in people)
                sb.Append(string.Join(";", new[] { B.Date(b.Arrival), B.Date(b.Departure), unit, b.Number, B.Num(t.Idx + 1) }
                    .Concat(Fields.Select(f => f.Kind == "date" ? B.Date(t.Get(f.Key)) : t.Get(f.Key))).Select(C))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>A registration form as PDF: the stay, every traveller, the main guest's address, a line to sign.</summary>
    internal static byte[] Pdf(BookingPrint.Letterhead lh, Booking b, Unit? unit, Property? house, string profile, IReadOnlyList<Traveller> people)
    {
        var p = new PrintLayout(lh, "Meldeschein " + b.Number);
        p.Kicker("Meldeschein · Gästeliste · Nr. " + b.Number);
        p.Title(unit?.Name ?? "Unterkunft");
        p.Facts(("Objekt", house is null ? "" : house.Name + (house.Address.Length > 0 ? ", " + house.Address.Replace("\n", ", ") : "")),
            ("Anreise", B.Date(b.Arrival)), ("Abreise", B.Date(b.Departure)), ("Personen", B.Num(people.Count)));
        var cols = Profiles[profile].All.Where(k => k is not ("first" or "last")).ToList();
        var share = 0.7 / Math.Max(1, cols.Count);
        p.Table(new[] { "Name" }.Concat(cols.Select(Label)).ToArray(), new[] { 0.3 }.Concat(cols.Select(_ => share)).ToArray(),
            people.Select(t => new[] { t.Name }.Concat(cols.Select(k => k == "born" ? B.Date(t.Get(k)) : t.Get(k))).ToArray()).ToList());
        var main = people.FirstOrDefault();
        if (main is not null && Profiles[profile].MainOnly.Length > 0)
        {
            p.Heading("Anschrift des Hauptgastes");
            p.Body(string.Join("\n", new[] { main.Name, main.Get("street"), (main.Get("zip") + " " + main.Get("city")).Trim(), main.Get("res_country") }.Where(x => x.Length > 0)));
        }
        p.Signatures("Ort, Datum", "Unterschrift des Gastes");
        return p.ToBytes();
    }
}
