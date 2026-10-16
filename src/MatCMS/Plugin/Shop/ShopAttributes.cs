using System.Globalization;

namespace MatCMS.Plugin.Shop;

/// <summary>The kinds of attribute a product can carry: one or several of a list of options (colour), free text, a number (weight, with a unit), yes/no.</summary>
internal static class AttributeKind
{
    public const string Choice = "choice", Text = "text", Number = "number", Bool = "bool";
    public static readonly string[] All = { Choice, Text, Number, Bool };
    public static string Normalise(string? kind) => All.Contains(kind ?? "") ? kind! : Choice;
}

/// <summary>
/// An attribute — the "A" of an entity–attribute–value model: a named property that products may have (colour, size, material, weight). It may be offered as a filter in the shop and may be limited
/// to one category (and everything below it). Which product has which value is a row of <c>product_attrs</c> (product, attribute, value; a choice can hold several values).
/// </summary>
internal sealed record ShopAttribute
{
    public long Id { get; init; }
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = AttributeKind.Choice;
    /// <summary>The options of a choice, one per line.</summary>
    public string Options { get; init; } = "";
    /// <summary>Shown behind a number ("kg", "cm").</summary>
    public string Unit { get; init; } = "";
    /// <summary>The shop offers it as a filter.</summary>
    public bool Filterable { get; init; } = true;
    /// <summary>The category (slug) it is limited to; empty = every product.</summary>
    public string Category { get; init; } = "";
    public int Sort { get; init; }

    public IReadOnlyList<string> OptionList => ShopAttributes.SplitOptions(Options);
}

/// <summary>One value of one product.</summary>
internal sealed record AttrRow(long ProductId, long AttrId, string Value);

/// <summary>A filter a visitor set: these values of one attribute, or a range of a number.</summary>
internal sealed record AttributeFilter(ShopAttribute Attr, IReadOnlyList<string> Values, double? Min, double? Max);

/// <summary>What a filter can offer for one attribute, counted over the products at hand.</summary>
internal sealed record AttributeFacet(IReadOnlyList<(string Value, int Count)> Values, double? Min, double? Max);

/// <summary>The rules of attributes as pure functions (no database): reading what a form or an address says, which attribute applies where, what matches a filter, what a filter can offer.</summary>
internal static class ShopAttributes
{
    public const int MaxValueLength = 80;
    public const int MaxOptions = 80;

    /// <summary>A value as it is stored and compared: one line, no commas (a list of values is comma-separated in addresses and forms), at most 80 characters.</summary>
    public static string CleanValue(string? v)
    {
        var t = string.Join(' ', (v ?? "").Replace(',', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length > MaxValueLength ? t[..MaxValueLength].TrimEnd() : t;
    }

    /// <summary>The options of a choice from the text typed: one per line, cleaned, no repeats (the first spelling wins).</summary>
    public static List<string> SplitOptions(string? text)
    {
        var list = new List<string>();
        foreach (var line in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var o = CleanValue(line);
            if (o.Length == 0 || list.Any(x => string.Equals(x, o, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(o);
            if (list.Count >= MaxOptions) break;
        }
        return list;
    }

    public static string NormaliseOptions(string? text) => string.Join("\n", SplitOptions(text));

    /// <summary>A number as typed ("12,5" or "12.5"): the invariant text that is stored, or null.</summary>
    public static string? ParseNumber(string? text, out double value)
    {
        value = 0;
        var t = (text ?? "").Trim().Replace(',', '.');
        if (t.Length == 0 || !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1e12) return null;
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>The attribute applies to a product of this category: it is for every product, or for the category it is limited to and everything below it.</summary>
    public static bool AppliesTo(ShopAttribute a, IReadOnlyList<Category> cats, string categorySlug) =>
        a.Category.Length == 0 || (categorySlug.Length > 0 && ShopCategories.SlugsUnder(cats, a.Category).Contains(categorySlug));

    /// <summary>The values to store for what a form posted. A choice keeps only its own options (in their spelling), a number only a number, a yes/no only "1".</summary>
    public static List<string> ReadValues(ShopAttribute a, string? posted, bool present)
    {
        switch (a.Kind)
        {
            case AttributeKind.Bool:
                return present ? new List<string> { "1" } : new List<string>();
            case AttributeKind.Number:
                return ParseNumber(posted, out _) is { } n ? new List<string> { n } : new List<string>();
            case AttributeKind.Text:
                return CleanValue(posted) is { Length: > 0 } t ? new List<string> { t } : new List<string>();
            default:
            {
                var options = a.OptionList;
                var list = new List<string>();
                foreach (var raw in (posted ?? "").Split(','))
                {
                    var v = CleanValue(raw);
                    var canon = options.FirstOrDefault(o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase));
                    if (canon is not null && !list.Contains(canon)) list.Add(canon);
                }
                return list;
            }
        }
    }

    /// <summary>The value as text for a page: a number with its unit (comma as the decimal sign in German), a yes/no as the given words.</summary>
    public static string Show(ShopAttribute a, IEnumerable<string> values, string yes, string locale)
    {
        var vs = values.ToList();
        if (vs.Count == 0) return "";
        switch (a.Kind)
        {
            case AttributeKind.Bool: return yes;
            case AttributeKind.Number:
            {
                var text = double.TryParse(vs[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d.ToString("#,0.##", locale == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("de-DE")) : vs[0];
                return a.Unit.Length > 0 ? text + " " + a.Unit : text;
            }
            default: return string.Join(", ", vs);
        }
    }

    // ---- filters ---------------------------------------------------------------------------------------

    public static string Key(ShopAttribute a) => "a." + a.Slug;

    /// <summary>The filters an address asks for: <c>a.farbe=Rot,Blau</c> (any of these values) and <c>a.gewicht.min=1&amp;a.gewicht.max=5</c> for a number. Only the given attributes count; unknown values are dropped.</summary>
    public static List<AttributeFilter> ReadFilters(IEnumerable<ShopAttribute> attrs, Func<string, string> query)
    {
        var filters = new List<AttributeFilter>();
        foreach (var a in attrs)
        {
            if (a.Kind == AttributeKind.Number)
            {
                double? min = ParseNumber(query(Key(a) + ".min"), out var lo) is not null ? lo : null;
                double? max = ParseNumber(query(Key(a) + ".max"), out var hi) is not null ? hi : null;
                if (min is not null || max is not null) filters.Add(new AttributeFilter(a, Array.Empty<string>(), min, max));
                continue;
            }
            var values = new List<string>();
            foreach (var raw in query(Key(a)).Split(','))
            {
                var v = CleanValue(raw);
                if (v.Length == 0) continue;
                if (a.Kind == AttributeKind.Choice) v = a.OptionList.FirstOrDefault(o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase)) ?? "";
                else if (a.Kind == AttributeKind.Bool) v = "1";
                if (v.Length > 0 && !values.Contains(v, StringComparer.OrdinalIgnoreCase)) values.Add(v);
            }
            if (values.Count > 0) filters.Add(new AttributeFilter(a, values, null, null));
        }
        return filters;
    }

    /// <summary>The filters as query parameters (for a link that keeps them).</summary>
    public static List<KeyValuePair<string, string>> ToQuery(IEnumerable<AttributeFilter> filters)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var f in filters)
        {
            if (f.Attr.Kind == AttributeKind.Number)
            {
                if (f.Min is { } lo) list.Add(new(Key(f.Attr) + ".min", lo.ToString("R", CultureInfo.InvariantCulture)));
                if (f.Max is { } hi) list.Add(new(Key(f.Attr) + ".max", hi.ToString("R", CultureInfo.InvariantCulture)));
            }
            else list.Add(new(Key(f.Attr), string.Join(",", f.Values)));
        }
        return list;
    }

    /// <summary>The products among <paramref name="candidates"/> that satisfy EVERY filter: within one attribute any of its values (or inside the range), across attributes all of them.</summary>
    public static HashSet<long> Match(IEnumerable<long> candidates, IReadOnlyList<AttributeFilter> filters, IReadOnlyList<AttrRow> rows)
    {
        var result = new HashSet<long>(candidates);
        foreach (var f in filters)
        {
            var ok = new HashSet<long>();
            foreach (var r in rows.Where(r => r.AttrId == f.Attr.Id))
            {
                if (f.Attr.Kind == AttributeKind.Number)
                {
                    if (double.TryParse(r.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && (f.Min is not { } lo || n >= lo) && (f.Max is not { } hi || n <= hi)) ok.Add(r.ProductId);
                }
                else if (f.Values.Any(v => string.Equals(v, r.Value, StringComparison.OrdinalIgnoreCase))) ok.Add(r.ProductId);
            }
            result.IntersectWith(ok);
        }
        return result;
    }

    /// <summary>What a visitor can pick, per attribute, counted over the products at hand: each value with how many products have it (a choice in the order of its options, the rest by count), a number's lowest and highest.</summary>
    public static Dictionary<long, AttributeFacet> Facets(IEnumerable<ShopAttribute> attrs, IReadOnlyList<AttrRow> rows)
    {
        var result = new Dictionary<long, AttributeFacet>();
        foreach (var a in attrs)
        {
            var mine = rows.Where(r => r.AttrId == a.Id).ToList();
            if (a.Kind == AttributeKind.Number)
            {
                var nums = mine.Select(r => double.TryParse(r.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? (double?)n : null).Where(n => n is not null).Select(n => n!.Value).ToList();
                result[a.Id] = nums.Count == 0 ? new AttributeFacet(Array.Empty<(string, int)>(), null, null) : new AttributeFacet(Array.Empty<(string, int)>(), nums.Min(), nums.Max());
                continue;
            }
            var counts = new Dictionary<string, (string Value, int Count)>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in mine.GroupBy(r => r.Value, StringComparer.OrdinalIgnoreCase))
                counts[g.Key] = (g.First().Value, g.Select(r => r.ProductId).Distinct().Count());
            IEnumerable<(string Value, int Count)> ordered = a.Kind == AttributeKind.Choice
                ? a.OptionList.Where(counts.ContainsKey).Select(o => counts[o]).Concat(counts.Values.Where(c => !a.OptionList.Contains(c.Value, StringComparer.OrdinalIgnoreCase)).OrderBy(c => c.Value, StringComparer.OrdinalIgnoreCase))
                : counts.Values.OrderByDescending(c => c.Count).ThenBy(c => c.Value, StringComparer.OrdinalIgnoreCase);
            result[a.Id] = new AttributeFacet(ordered.ToList(), null, null);
        }
        return result;
    }
}

internal sealed partial class ShopStore
{
    private static ShopAttribute AttributeOf(DataRow r) => new()
    {
        Id = r.Id, Slug = r.Text("slug"), Name = r.Text("name"), Kind = AttributeKind.Normalise(r.Text("kind")), Options = r.Text("options"), Unit = r.Text("unit"),
        Filterable = r.Bool("filterable"), Category = r.Text("category"), Sort = (int)r.Int("sort")
    };

    public async Task<IReadOnlyList<ShopAttribute>> AttributesAsync(CancellationToken ct = default) =>
        (await T("attributes").QueryAsync(DataQuery.Everything.OrderBy("sort").OrderBy("name"), ct)).Select(AttributeOf).ToList();

    public async Task<ShopAttribute?> AttributeAsync(long id, CancellationToken ct = default) =>
        id > 0 && await T("attributes").GetAsync(id, ct) is { } r ? AttributeOf(r) : null;

    public async Task<ShopAttribute?> AttributeBySlugAsync(string slug, CancellationToken ct = default) =>
        await T("attributes").FindByAsync("slug", slug, ct) is { } r ? AttributeOf(r) : null;

    /// <summary>Writes an attribute: new when <c>Id</c> is 0, else everything but the slug (filters in addresses refer to it). Returns the id.</summary>
    public async Task<long> SaveAttributeAsync(ShopAttribute a, CancellationToken ct = default)
    {
        var values = new Dictionary<string, object?>
        {
            ["name"] = a.Name, ["kind"] = AttributeKind.Normalise(a.Kind), ["options"] = ShopAttributes.NormaliseOptions(a.Options), ["unit"] = a.Unit, ["filterable"] = a.Filterable,
            ["category"] = a.Category, ["sort"] = a.Sort
        };
        if (a.Id > 0) { await T("attributes").UpdateAsync(a.Id, values, ct); return a.Id; }
        values["slug"] = a.Slug;
        return await T("attributes").InsertAsync(values, ct);
    }

    /// <summary>Deletes an attribute and every value any product has of it.</summary>
    public async Task DeleteAttributeAsync(long id, CancellationToken ct = default)
    {
        await T("product_attrs").DeleteWhereAsync(DataQuery.Everything.Eq("attr_id", id), ct);
        await T("attributes").DeleteAsync(id, ct);
    }

    /// <summary>The values of the given attributes, of the given products (null = all).</summary>
    public async Task<IReadOnlyList<AttrRow>> AttrRowsAsync(IReadOnlyCollection<long>? productIds, IReadOnlyCollection<long>? attrIds = null, CancellationToken ct = default)
    {
        if (productIds is { Count: 0 } || attrIds is { Count: 0 }) return Array.Empty<AttrRow>();
        var q = DataQuery.Everything;
        if (productIds is not null) q = q.Where("product_id", DataOp.In, productIds.ToList());
        if (attrIds is not null) q = q.Where("attr_id", DataOp.In, attrIds.ToList());
        return (await T("product_attrs").QueryAsync(q, ct)).Select(r => new AttrRow(r.Int("product_id"), r.Int("attr_id"), r.Text("value"))).ToList();
    }

    /// <summary>One product's values, by attribute.</summary>
    public async Task<Dictionary<long, List<string>>> ProductValuesAsync(long productId, CancellationToken ct = default) =>
        (await AttrRowsAsync(new[] { productId }, null, ct)).GroupBy(r => r.AttrId).ToDictionary(g => g.Key, g => g.Select(r => r.Value).ToList());

    /// <summary>Replaces what a product has of one attribute.</summary>
    public async Task SetProductAttrAsync(long productId, long attrId, IReadOnlyList<string> values, CancellationToken ct = default)
    {
        await T("product_attrs").DeleteWhereAsync(DataQuery.Everything.Eq("product_id", productId).Eq("attr_id", attrId), ct);
        foreach (var v in values)
            await T("product_attrs").InsertAsync(new Dictionary<string, object?> { ["product_id"] = productId, ["attr_id"] = attrId, ["value"] = v }, ct);
    }

    /// <summary>How many products carry a value of each attribute.</summary>
    public async Task<Dictionary<long, int>> AttrUseAsync(CancellationToken ct = default) =>
        (await AttrRowsAsync(null, null, ct)).GroupBy(r => r.AttrId).ToDictionary(g => g.Key, g => g.Select(r => r.ProductId).Distinct().Count());
}
