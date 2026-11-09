namespace MatCMS.Plugin.Booking;

/// <summary>A house (villa, holiday home). A site usually has one; it may have several.</summary>
public sealed record Property(long Id, string Name, string Slug, string Address, string Country, string CheckIn, string CheckOut, string Description, int Sort);

/// <summary>
/// What is booked: an apartment, a room or the whole house. <see cref="Contains"/> lists the units a "whole house" consists of — booking it occupies
/// them, booking one of them occupies the house. Prices outside every season: <see cref="BasePrice"/> (cents per night) and <see cref="BaseMinNights"/>.
/// </summary>
public sealed record Unit(long Id, long PropertyId, string Name, string Slug, string Description, string Image, int Bedrooms, int GuestsStd, int GuestsMax,
    IReadOnlyList<long> Contains, int BufferDays, long BasePrice, int BaseMinNights, long ExtraPerson, int Sort, bool Active);

/// <summary>A named period of a house, from and to INCLUSIVE (nights). Overlaps are allowed: the higher rank wins (Silvester inside the low season).</summary>
public sealed record Season(long Id, long PropertyId, string Name, DateOnly From, DateOnly To, int Rank, string Color)
{
    public bool Covers(DateOnly night) => night >= From && night <= To;
}

/// <summary>
/// The price of a unit in a season: per night, the minimum stay and the allowed arrival days (ISO 1–7; empty = any) — both judged by the season of the
/// ARRIVAL night —, an optional price for seven nights in a row, and a surcharge per person and night above the unit's standard occupancy.
/// </summary>
public sealed record Rate(long Id, long UnitId, long SeasonId, long Price, int MinNights, IReadOnlyList<int> ArrivalDays, long WeekPrice, long ExtraPerson);

/// <summary>How a fee counts.</summary>
public static class FeePer
{
    public const string Stay = "stay", Night = "night", Person = "person", PersonNight = "person_night";
    public static readonly (string Key, string Label)[] All = { (Stay, "je Aufenthalt"), (Night, "je Nacht"), (Person, "je Person"), (PersonNight, "je Person und Nacht") };
    public static string Label(string k) => All.FirstOrDefault(x => x.Key == k).Label ?? k;
}

/// <summary>
/// A fee (final cleaning, bed linen, pet …) or the tourist tax (<see cref="TouristTax"/>: shown on its own line, children by <see cref="Children"/>:
/// "free", "half" or "full"). <see cref="UnitId"/> 0 = every unit of the house. An optional fee is only charged when the guest picks it.
/// </summary>
public sealed record Fee(long Id, long PropertyId, long UnitId, string Name, long Amount, string Per, bool Optional, bool TouristTax, string Children, int Sort);

/// <summary>What a guest asks for.</summary>
public sealed record Stay(DateOnly Arrival, DateOnly Departure, int Adults, int Children, IReadOnlyCollection<long>? OptionalFees = null)
{
    public int Nights => Departure.DayNumber - Arrival.DayNumber;
    public int Persons => Adults + Children;
    public IEnumerable<DateOnly> EachNight() { for (var d = Arrival; d < Departure; d = d.AddDays(1)) yield return d; }
}

/// <summary>One line of the bill. <see cref="Kind"/>: nights, extra, fee, tax.</summary>
public sealed record QuoteLine(string Kind, string Text, int Quantity, long Unit, long Amount)
{
    /// <summary>How a fee was counted, in words ("1,35 € je Person und Nacht · 3 Erw., 1 Kind zur Hälfte · 9 Nächte"); empty for nights.</summary>
    public string Detail { get; init; } = "";
}

/// <summary>The bill of a stay — or why there is none (<see cref="Problems"/>, in words a guest understands).</summary>
public sealed record Quote(IReadOnlyList<QuoteLine> Lines, IReadOnlyList<string> Problems, int MinNights, IReadOnlyList<int> ArrivalDays)
{
    public long Total => Lines.Sum(l => l.Amount);
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// The price of a stay — pure, no data access, so every rule is testable on its own. Night by night: the season with the highest rank that covers the
/// night (ties: the shorter one, then the later start), its rate for the unit or the unit's base price; the minimum stay and the arrival days come from
/// the ARRIVAL night's season; seven nights in a row in one season with a week price cost the week price; persons above the standard occupancy pay the
/// surcharge of the night's rate; then the fees and the tourist tax.
/// </summary>
public static class Pricing
{
    public const int MaxNights = 90;

    public static Season? SeasonOf(DateOnly night, IEnumerable<Season> seasons) =>
        seasons.Where(s => s.Covers(night)).OrderByDescending(s => s.Rank).ThenBy(s => s.To.DayNumber - s.From.DayNumber).ThenByDescending(s => s.From).FirstOrDefault();

    /// <summary>The rate a night is charged at: the unit's rate in that season, or — no season or no rate there — the base price.</summary>
    public static (Season? Season, Rate Rate) RateOf(Unit unit, DateOnly night, IReadOnlyList<Season> seasons, IReadOnlyList<Rate> rates)
    {
        var s = SeasonOf(night, seasons);
        var r = s is null ? null : rates.FirstOrDefault(x => x.UnitId == unit.Id && x.SeasonId == s.Id);
        return (s, r ?? new Rate(0, unit.Id, 0, unit.BasePrice, unit.BaseMinNights, Array.Empty<int>(), 0, unit.ExtraPerson));
    }

    public static Quote Calculate(Unit unit, Stay stay, IReadOnlyList<Season> seasons, IReadOnlyList<Rate> rates, IReadOnlyList<Fee> fees, string currency = "EUR")
    {
        var problems = new List<string>();
        var lines = new List<QuoteLine>();
        if (stay.Nights <= 0) return new Quote(lines, new[] { "Die Abreise muss nach der Anreise liegen." }, 1, Array.Empty<int>());
        if (stay.Nights > MaxNights) problems.Add("Höchstens " + MaxNights + " Nächte am Stück.");
        if (stay.Adults < 1) problems.Add("Mindestens ein Erwachsener.");
        if (stay.Persons > unit.GuestsMax) problems.Add(unit.Name + " ist für höchstens " + unit.GuestsMax + " Personen.");

        var (firstSeason, first) = RateOf(unit, stay.Arrival, seasons, rates);
        var min = Math.Max(1, first.MinNights);
        if (stay.Nights < min) problems.Add((firstSeason is null ? "" : firstSeason.Name + ": ") + "mindestens " + min + " Nächte.");
        if (first.ArrivalDays.Count > 0 && !first.ArrivalDays.Contains(B.IsoDay(stay.Arrival.DayOfWeek)))
            problems.Add((firstSeason is null ? "Anreise" : firstSeason.Name + ": Anreise") + " nur " + string.Join(", ", first.ArrivalDays.OrderBy(d => d).Select(d => B.DayNames[d - 1])) + ".");
        if (stay.Nights > MaxNights) return new Quote(lines, problems, min, first.ArrivalDays);

        // nights, grouped into runs of the same season — a week price applies to whole weeks inside one run
        var nights = stay.EachNight().Select(n => (Night: n, R: RateOf(unit, n, seasons, rates))).ToList();
        var extraPersons = Math.Max(0, stay.Persons - unit.GuestsStd);
        long extra = 0;
        var i = 0;
        while (i < nights.Count)
        {
            var season = nights[i].R.Season;
            var j = i;
            while (j < nights.Count && nights[j].R.Season?.Id == season?.Id) j++;
            var run = nights.GetRange(i, j - i);
            var rate = run[0].R.Rate;
            var name = season?.Name ?? "Grundpreis";
            var weeks = rate.WeekPrice > 0 ? run.Count / 7 : 0;
            if (weeks > 0) lines.Add(new QuoteLine("nights", weeks == 1 ? "1 Woche " + name : weeks + " Wochen " + name, weeks, rate.WeekPrice, weeks * rate.WeekPrice));
            var rest = run.Count - weeks * 7;
            if (rest > 0) lines.Add(new QuoteLine("nights", rest + (rest == 1 ? " Nacht " : " Nächte ") + name, rest, rate.Price, rest * rate.Price));
            extra += run.Sum(n => n.R.Rate.ExtraPerson) * extraPersons;
            i = j;
        }
        if (extra > 0) lines.Add(new QuoteLine("extra", extraPersons + (extraPersons == 1 ? " weitere Person" : " weitere Personen") + " (ab " + (unit.GuestsStd + 1) + ". Person)", 1, extra, extra));

        foreach (var f in fees.Where(f => f.PropertyId == unit.PropertyId && (f.UnitId == 0 || f.UnitId == unit.Id)).OrderBy(f => f.TouristTax).ThenBy(f => f.Sort))
        {
            if (f.Optional && !(stay.OptionalFees?.Contains(f.Id) ?? false)) continue;
            // children count fully for a fee unless it is the tourist tax with its own rule; counted in HALF persons so "half" stays exact,
            // and a half cent at the end is rounded up (commercial rounding)
            var halves = f.TouristTax ? stay.Adults * 2 + stay.Children * (f.Children == "free" ? 0 : f.Children == "half" ? 1 : 2) : stay.Persons * 2;
            var (qty, amount) = f.Per switch
            {
                FeePer.Night => (stay.Nights, f.Amount * stay.Nights),
                FeePer.Person => (1, (f.Amount * halves + 1) / 2),
                FeePer.PersonNight => (stay.Nights, (f.Amount * halves * stay.Nights + 1) / 2),
                _ => (1, f.Amount)
            };
            if (amount <= 0) continue;
            lines.Add(new QuoteLine(f.TouristTax ? "tax" : "fee", f.Name, qty, f.Amount, amount) { Detail = FeeDetail(f, stay, currency) });
        }
        return new Quote(lines, problems, min, first.ArrivalDays);
    }

    private static string FeeDetail(Fee f, Stay stay, string currency)
    {
        var parts = new List<string> { B.Money(f.Amount, currency) + " " + FeePer.Label(f.Per) };
        if (f.Per is FeePer.Person or FeePer.PersonNight)
        {
            var who = stay.Adults + (stay.Adults == 1 ? " Erw." : " Erw.");
            if (stay.Children > 0)
                who += ", " + stay.Children + (stay.Children == 1 ? " Kind" : " Kinder") + (!f.TouristTax || f.Children == "full" ? "" : f.Children == "half" ? " zur Hälfte" : " frei");
            parts.Add(who);
        }
        if (f.Per is FeePer.Night or FeePer.PersonNight) parts.Add(stay.Nights + (stay.Nights == 1 ? " Nacht" : " Nächte"));
        return string.Join(" · ", parts);
    }
}
