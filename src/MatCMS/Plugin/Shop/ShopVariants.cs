using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>An option a product is offered in — "Größe" with S, M, L. The options only serve to MAKE variants (every combination of their values); what is sold are the variants.</summary>
internal sealed record VariantOption(string Name, IReadOnlyList<string> Values);

/// <summary>One variant as the editor posts it: <see cref="Id"/> is 0 for a new row, and a row that keeps its id keeps its stock and its place in old orders when it is renamed.</summary>
internal sealed record VariantRow(long Id, string Name, long DeltaCents, long? Stock, string Sku, string Image = "");

/// <summary>The pure rules of the variant editor: reading what the form posted, making the combinations of the options, remembering the options on the product.</summary>
internal static class ShopVariants
{
    public const int MaxVariants = 60, MaxOptions = 3, MaxValues = 40, MaxName = 80;

    /// <summary>The options the form posted (<c>opt_name_1</c>/<c>opt_values_1</c> … up to three); an option with no value is left out.</summary>
    public static List<VariantOption> ReadOptions(Func<string, string> f)
    {
        var list = new List<VariantOption>();
        for (var i = 1; i <= MaxOptions; i++)
        {
            var values = Split(f("opt_values_" + i));
            if (values.Count == 0) continue;
            list.Add(new VariantOption(Clip(f("opt_name_" + i), MaxName), values));
        }
        return list;
    }

    /// <summary>Comma-separated values, trimmed, without repeats (case-insensitive), at most <see cref="MaxValues"/>.</summary>
    public static List<string> Split(string? text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var v = Clip(raw, MaxName);
            if (v.Length > 0 && seen.Add(v) && list.Count < MaxValues) list.Add(v);
        }
        return list;
    }

    private static string Clip(string? s, int max) { var t = (s ?? "").Trim(); return t.Length > max ? t[..max] : t; }

    public static string OptionsToJson(IEnumerable<VariantOption> options) =>
        new JsonArray(options.Select(o => (JsonNode)new JsonObject { ["name"] = o.Name, ["values"] = new JsonArray(o.Values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()) }).ToArray()).ToJsonString();

    /// <summary>What was stored on the product; damaged text reads as no options.</summary>
    public static List<VariantOption> OptionsFromJson(string? json)
    {
        var list = new List<VariantOption>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            if (JsonNode.Parse(json) is JsonArray arr)
                foreach (var n in arr.Take(MaxOptions))
                    if (n is JsonObject o)
                    {
                        var values = Split(string.Join(",", (o["values"] as JsonArray)?.Select(v => v?.GetValue<string>() ?? "").Where(v => !v.Contains(',')) ?? Enumerable.Empty<string>()));
                        if (values.Count > 0) list.Add(new VariantOption(Clip(o["name"]?.GetValue<string>(), MaxName), values));
                    }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { /* damaged text: no options */ }
        return list;
    }

    /// <summary>The name of every combination of the options' values, "M / Rot": the first option's values vary slowest. No options, no combinations; the count is capped so a careless option cannot make thousands.</summary>
    public static List<string> Combinations(IReadOnlyList<VariantOption> options)
    {
        if (options.Count == 0) return new List<string>();
        var result = new List<string> { "" };
        foreach (var o in options)
        {
            var next = new List<string>();
            foreach (var head in result)
                foreach (var v in o.Values)
                {
                    next.Add(head.Length == 0 ? v : head + " / " + v);
                    if (next.Count > 1000) return next;
                }
            result = next;
        }
        return result;
    }

    /// <summary>Reads the rows of the editor (<c>variants.count</c>, <c>variants.{i}.id/name/delta/stock/sku</c>). A row with nothing in it is skipped; a mistake comes back as a text key with its arguments (row number first).</summary>
    public static List<VariantRow> ReadRows(Func<string, string> f, out (string Key, object[] Args)? error)
    {
        error = null;
        var rows = new List<VariantRow>();
        var count = int.TryParse(f("variants.count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 0, 300) : 0;
        var shown = 0;
        for (var i = 0; i < count; i++)
        {
            string P(string k) => f($"variants.{i}.{k}").Trim();
            var name = P("name"); var deltaText = P("delta").Replace(" ", ""); var stockText = P("stock"); var sku = P("sku");
            if (name.Length == 0 && deltaText.Length == 0 && stockText.Length == 0 && sku.Length == 0) continue;
            shown++;
            if (name.Length == 0) { error = ("shop.adm.var.errName", new object[] { shown }); return rows; }
            if (name.Length > MaxName) name = name[..MaxName];
            long delta = 0;
            if (deltaText.Length > 0 && !Money.TryParse(deltaText.TrimStart('+'), out delta)) { error = ("shop.adm.var.errAmount", new object[] { shown, deltaText }); return rows; }
            long? stock = null;
            if (stockText.Length > 0)
            {
                if (!long.TryParse(stockText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var st) || st < 0) { error = ("shop.adm.var.errStock", new object[] { shown, stockText }); return rows; }
                stock = st;
            }
            if (rows.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))) { error = ("shop.adm.var.errDup", new object[] { shown, name }); return rows; }
            _ = long.TryParse(P("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id);
            rows.Add(new VariantRow(Math.Max(0, id), name, delta, stock, sku.Length > MaxName ? sku[..MaxName] : sku, ShopPublic.SafeUrl(P("image"))));
        }
        if (rows.Count > MaxVariants) error = ("shop.adm.var.errMany", new object[] { MaxVariants });
        return rows;
    }

    /// <summary>The rows plus a new row for every combination that is not among them yet (compared by name, without case); false when that would be more than <see cref="MaxVariants"/>.</summary>
    public static bool AddMissing(List<VariantRow> rows, IEnumerable<string> combinations, out int added)
    {
        added = 0;
        foreach (var name in combinations)
        {
            if (rows.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            if (rows.Count >= MaxVariants) return false;
            rows.Add(new VariantRow(0, name, 0, null, ""));
            added++;
        }
        return true;
    }
}
