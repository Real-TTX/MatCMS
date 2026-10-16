using System.Globalization;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The rule of an automatic collection: a category that, besides the products filed in it, collects every product a rule picks — "all keyword Neuware", "everything on sale", "new in the last 30
/// days", "under 20 euros". Clauses are separated by a semicolon and must ALL hold. A rule is a few words, not a program: unknown words are dropped when it is saved, so a rule can only ever be
/// one of the kinds below.
/// <list type="bullet">
/// <item><c>tag:Neuware</c> — the product has this keyword (any case)</item>
/// <item><c>sale</c> — it is on offer (an earlier, higher price)</item>
/// <item><c>featured</c> — it is highlighted</item>
/// <item><c>new:30</c> — it was added in the last 30 days (1 to 3650)</item>
/// <item><c>price&lt;20</c>, <c>price&gt;100</c> — its price in euros is below / above (comma or point)</item>
/// <item><c>instock</c> — it can be bought now (not counted, or stock above zero)</item>
/// </list>
/// </summary>
internal static class ShopCollectionRule
{
    public const int MaxClauses = 5;

    /// <summary>The clauses that are valid, in a normal spelling, joined with "; " (empty when none is).</summary>
    public static string Clean(string? rule) => string.Join("; ", Clauses(rule).Select(c => c.Text));

    /// <summary>True when there is at least one valid clause.</summary>
    public static bool Any(string? rule) => Clauses(rule).Count > 0;

    private sealed record Clause(string Kind, string Text, string Arg = "", double Number = 0);

    private static List<Clause> Clauses(string? rule)
    {
        var list = new List<Clause>();
        foreach (var raw in (rule ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (list.Count >= MaxClauses) break;
            var c = raw.Trim();
            if (c.Length == 0 || c.Length > 80) continue;
            var lower = c.ToLowerInvariant();
            if (lower == "sale") list.Add(new Clause("sale", "sale"));
            else if (lower == "featured") list.Add(new Clause("featured", "featured"));
            else if (lower == "instock") list.Add(new Clause("instock", "instock"));
            else if (lower.StartsWith("tag:", StringComparison.Ordinal) && ShopTags.Split(c[4..]) is { Count: > 0 } tags) list.Add(new Clause("tag", "tag:" + tags[0], tags[0]));
            else if (lower.StartsWith("new:", StringComparison.Ordinal) && int.TryParse(c[4..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 3650)
                list.Add(new Clause("new", "new:" + days.ToString(CultureInfo.InvariantCulture), Number: days));
            else if ((lower.StartsWith("price<", StringComparison.Ordinal) || lower.StartsWith("price>", StringComparison.Ordinal)) && ShopCatalogUi.Cents(c[6..].Trim()) is { } cents)
                list.Add(new Clause(lower[5] == '<' ? "below" : "above", "price" + lower[5] + ShopCatalogUi.Euros(cents), Number: cents));
        }
        return list;
    }

    /// <summary>Whether the product meets every clause of the rule (a rule with no valid clause picks nothing).</summary>
    public static bool Matches(string? rule, Product p, DateTime nowUtc)
    {
        var clauses = Clauses(rule);
        if (clauses.Count == 0) return false;
        foreach (var c in clauses)
        {
            var ok = c.Kind switch
            {
                "sale" => p.OnSale,
                "featured" => p.Featured,
                "instock" => !p.TrackStock || p.Stock > 0,
                "tag" => ShopTags.Has(p, c.Arg),
                "new" => p.CreatedUtc >= nowUtc.AddDays(-c.Number),
                "below" => p.PriceCents < c.Number,
                "above" => p.PriceCents > c.Number,
                _ => false
            };
            if (!ok) return false;
        }
        return true;
    }
}
