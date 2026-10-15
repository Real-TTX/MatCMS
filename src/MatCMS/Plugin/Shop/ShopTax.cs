using System.Globalization;

namespace MatCMS.Plugin.Shop;

/// <summary>A country's VAT: the standard rate and the one reduced rate that is most often used (food, books). Hundredths of a percent.</summary>
internal readonly record struct VatRate(int StandardBp, int ReducedBp);

/// <summary>What shipping is charged with once the country of delivery is known: the rate it ends up with, the rate its configured price was meant for, and whether the gross price stays.</summary>
internal sealed record ShipTax(int Bp, int FromBp, bool KeepGross)
{
    public static ShipTax Home(int bp) => new(bp, bp, true);

    /// <summary>The price to charge for a shipping price configured at <see cref="FromBp"/>.</summary>
    public long Price(long configuredCents) => ShopTax.Convert(configuredCents, FromBp, Bp, KeepGross);
}

/// <summary>
/// VAT by the country the goods go to (the EU's One-Stop-Shop). Off by default: the shop then charges the rate of the product everywhere, as it always did. On, a
/// delivery to another EU country is taxed at THAT country's rate and a delivery to a country outside the EU carries no VAT.
/// <para>What it does not do: a business customer with a VAT ID (reverse charge) and the €10,000 threshold below which a seller may still charge their own rate — whoever
/// switches the mode on has decided that already. The rates are the ones in force when this was written; the settings page can override each country.</para>
/// </summary>
internal static class ShopTax
{
    /// <summary>Standard and reduced rates of the EU member states. A starting point — see <see cref="ShopConfig.TaxRateOverrides"/>.</summary>
    public static readonly IReadOnlyDictionary<string, VatRate> Eu = new Dictionary<string, VatRate>
    {
        ["AT"] = new(2000, 1000), ["BE"] = new(2100, 600), ["BG"] = new(2000, 900), ["HR"] = new(2500, 500), ["CY"] = new(1900, 500), ["CZ"] = new(2100, 1200),
        ["DE"] = new(1900, 700), ["DK"] = new(2500, 0), ["EE"] = new(2400, 900), ["ES"] = new(2100, 1000), ["FI"] = new(2550, 1400), ["FR"] = new(2000, 550),
        ["GR"] = new(2400, 1300), ["HU"] = new(2700, 500), ["IE"] = new(2300, 1350), ["IT"] = new(2200, 1000), ["LT"] = new(2100, 900), ["LU"] = new(1700, 800),
        ["LV"] = new(2100, 1200), ["MT"] = new(1800, 500), ["NL"] = new(2100, 900), ["PL"] = new(2300, 800), ["PT"] = new(2300, 600), ["RO"] = new(2100, 1100),
        ["SE"] = new(2500, 1200), ["SI"] = new(2200, 950), ["SK"] = new(2300, 1900)
    };

    public static bool IsEu(string country) => Eu.ContainsKey((country ?? "").Trim().ToUpperInvariant());

    /// <summary>
    /// The overrides an operator typed, one country per line: <c>FR 20 5,5</c> (standard, then reduced) — also <c>FR=20/5.5</c>. A line that does not parse is skipped and
    /// reported in <paramref name="problems"/>; a rate may be 0 to 100 %.
    /// </summary>
    public static Dictionary<string, VatRate> ParseOverrides(string? text, List<string>? problems = null)
    {
        var result = new Dictionary<string, VatRate>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(new[] { ' ', '\t', '=', '/', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var code = parts[0].ToUpperInvariant();
            if (parts.Length < 2 || parts.Length > 3 || code.Length != 2 || !Money.TryParsePercent(parts[1], out var standard) || parts.Length == 3 && !Money.TryParsePercent(parts[2], out _))
            {
                problems?.Add(line);
                continue;
            }
            var reduced = parts.Length == 3 && Money.TryParsePercent(parts[2], out var r) ? r : Eu.TryGetValue(code, out var known) ? known.ReducedBp : 0;
            result[code] = new VatRate(standard, reduced);
        }
        return result;
    }

    /// <summary>The rates the shop uses for a country: its override, else the built-in one, else none (not an EU country).</summary>
    public static VatRate? RateOf(ShopConfig cfg, string country)
    {
        var c = (country ?? "").Trim().ToUpperInvariant();
        if (cfg.TaxRates.TryGetValue(c, out var own)) return own;
        return Eu.TryGetValue(c, out var r) ? r : null;
    }

    /// <summary>The price after moving a gross amount from one rate to another: kept as it is (the VAT inside changes) or with the NET kept (the gross changes).</summary>
    public static long Convert(long grossCents, int fromBp, int toBp, bool keepGross)
    {
        if (grossCents <= 0 || fromBp == toBp || keepGross) return grossCents;
        return (long)Math.Round(grossCents * 10000m / (10000 + fromBp) * (10000 + toBp) / 10000m, MidpointRounding.AwayFromZero);
    }

    /// <summary>The rate for goods taxed at <paramref name="homeBp"/> at home, going to <paramref name="country"/>, and what to call it ("" = home, "oss", "export").</summary>
    public static (int Bp, string Note, bool KeepGross) For(ShopConfig cfg, int homeBp, string country)
    {
        var c = (country ?? "").Trim().ToUpperInvariant();
        if (!cfg.Oss || c.Length == 0 || c == cfg.HomeCountry) return (homeBp, "", true);
        if (!IsEu(c) && !cfg.TaxRates.ContainsKey(c)) return (0, "export", false);               // outside the EU: no VAT, the price is the net price
        if (homeBp <= 0) return (0, "oss", true);                                                 // goods that are not taxed at home are not taxed there either
        var rate = RateOf(cfg, c)!.Value;
        // The class of the product comes from how it is taxed at home: at the standard rate or less.
        var bp = homeBp >= cfg.DefaultTaxBp ? rate.StandardBp : rate.ReducedBp > 0 ? rate.ReducedBp : rate.StandardBp;
        return (bp, "oss", cfg.OssKeepGross);
    }

    /// <summary>
    /// Re-taxes a priced cart for the country it goes to. Physical goods follow the DELIVERY address, digital goods the BILLING address (the place of supply of a
    /// download is the customer's country). Without OSS, or at home, nothing changes.
    /// </summary>
    public static PricedCart Apply(PricedCart cart, ShopConfig cfg, string shipCountry, string billCountry)
    {
        if (!cfg.Oss) return cart;
        var lines = new List<PricedLine>(cart.Lines.Count);
        var note = "";
        foreach (var l in cart.Lines)
        {
            var country = l.Product.IsDigital || shipCountry.Length == 0 ? billCountry : shipCountry;
            var (bp, n, keep) = For(cfg, l.TaxBp, country);
            if (n == "export" || note != "export" && n.Length > 0) note = n;
            if (n.Length == 0) { lines.Add(l); continue; }
            var unit = Convert(l.UnitCents, l.TaxBp, bp, keep);
            var gross = unit * l.Qty;
            lines.Add(l with { UnitCents = unit, LineCents = gross, TaxBp = bp, TaxCents = Money.TaxIncluded(gross, bp) });
        }
        var shipTo = shipCountry.Length > 0 ? shipCountry : billCountry;
        var (sbp, sn, skeep) = For(cfg, cfg.ShippingTaxBp, shipTo);
        if (cart.NeedsShipping && (sn == "export" || note != "export" && sn.Length > 0)) note = sn;
        return cart with
        {
            Lines = lines, SubtotalCents = lines.Sum(x => x.LineCents), LineTaxCents = lines.Sum(x => x.TaxCents), TaxNote = note,
            ShipTax = sn.Length == 0 ? ShipTax.Home(cfg.ShippingTaxBp) : new ShipTax(sbp, cfg.ShippingTaxBp, skeep)
        };
    }

    /// <summary>The sentence an invoice carries for a note, or "".</summary>
    public static string NoteText(string note, bool en) => note switch
    {
        "oss" => en ? "VAT is charged at the rate of the country of delivery (One-Stop-Shop)." : "Die Umsatzsteuer wird nach dem Satz des Bestimmungslandes berechnet (OSS).",
        "export" => en ? "Tax-free export delivery / supply outside the EU: no VAT is charged." : "Steuerfreie Ausfuhrlieferung bzw. Leistung außerhalb der EU: Es wird keine Umsatzsteuer berechnet.",
        _ => ""
    };

    public static string RatesText(IReadOnlyDictionary<string, VatRate> rates) => string.Join("\n", rates.OrderBy(r => r.Key)
        .Select(r => $"{r.Key} {Money.Percent(r.Value.StandardBp)} {Money.Percent(r.Value.ReducedBp)}".TrimEnd()));

    internal static string Invariant(int bp) => (bp / 100m).ToString("0.##", CultureInfo.InvariantCulture);
}
