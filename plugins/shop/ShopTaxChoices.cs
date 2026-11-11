using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>The tax rates a person may pick from — a drop-down instead of a number typed into a box. The shop's own country comes first (standard, reduced, none), then every other rate the tables know;
/// a rate a product already has stays pickable even when no table lists it, so opening and saving a product never changes its tax.</summary>
internal static class ShopTaxChoices
{
    private static string Label(int bp, string note = "") => Money.Percent(bp) + " %" + (note.Length > 0 ? " – " + note : "");

    /// <summary>Every rate the built-in table knows (and 0), highest first — the choices of the shop-wide settings.</summary>
    public static IReadOnlyList<int> AllRates() =>
        ShopTax.Eu.Values.SelectMany(r => new[] { r.StandardBp, r.ReducedBp }).Append(0).Where(b => b >= 0).Distinct().OrderByDescending(b => b).ToList();

    /// <summary>The choices of the settings that hold one rate (the default, the one on shipping).</summary>
    public static List<FieldOption> ForSettings() => AllRates().Select(b => new FieldOption(Money.Percent(b), Label(b))).ToList();

    /// <summary>The choices on a product: the home country's standard and reduced rate and none first, then the others, then the product's own when it is none of them.</summary>
    public static UiOption[] ForProduct(IPluginContext ctx, ShopConfig cfg, int currentBp)
    {
        var chosen = new List<(int Bp, string Note)>();
        void Add(int bp, string note) { if (bp >= 0 && chosen.All(c => c.Bp != bp)) chosen.Add((bp, note)); }
        var home = cfg.TaxRates.TryGetValue(cfg.HomeCountry, out var own) ? own : ShopTax.Eu.TryGetValue(cfg.HomeCountry, out var builtIn) ? builtIn : new VatRate(1900, 700);
        Add(home.StandardBp, ctx.T("shop.adm.taxStandard"));
        if (home.ReducedBp > 0) Add(home.ReducedBp, ctx.T("shop.adm.taxReduced"));
        Add(0, ctx.T("shop.adm.taxZero"));
        foreach (var bp in AllRates()) Add(bp, "");
        if (chosen.All(c => c.Bp != currentBp)) Add(currentBp, "");
        return chosen.Select(c => Ui.Option(Money.Percent(c.Bp), Label(c.Bp, c.Note))).ToArray();
    }
}
