using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatCMS.Services;

/// <summary>
/// What the dashboard shows and in which order — per user, with the site's default (an admin may save his own as everybody's) behind it. Pure functions over a list of widgets so the
/// rules are testable without a page: a layout is only ever an ORDER and a set of HIDDEN ids, it never carries content, so an old layout cannot break the page when a widget disappears (a
/// plugin was switched off) or a new one appears (it is added at the end, shown).
/// </summary>
public static class DashboardLayout
{
    /// <summary>One thing on the dashboard. <paramref name="Partial"/> draws it; <paramref name="Model"/> is what it is given (null = the page itself).
    /// <paramref name="Present"/> is false while there is nothing to show (no errors, a site with content) — it is still listed in the editor.</summary>
    public sealed record Widget(string Id, string Label, bool Wide, string Partial, object? Model = null, bool Present = true, string? Note = null);

    /// <summary>A stored layout.</summary>
    public sealed record Config(IReadOnlyList<string> Order, IReadOnlySet<string> Hidden);

    private static readonly Regex IdRx = new("^[A-Za-z0-9:._-]{1,60}$", RegexOptions.Compiled);
    private const int MaxWidgets = 60;

    /// <summary>True for an id that may be stored (what the form posts is never trusted).</summary>
    public static bool ValidId(string? id) => id is not null && IdRx.IsMatch(id);

    /// <summary>Reads a stored layout; null for nothing stored or something unreadable.</summary>
    public static Config? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            List<string> Read(string name) => doc.RootElement.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(ValidId).Take(MaxWidgets).ToList()
                : new List<string>();
            var order = Read("order").Distinct().ToList();
            return new Config(order, Read("hidden").ToHashSet());
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The layout for storing: the order as posted, and what is NOT shown as hidden. Ids that are not valid are dropped, repeats too.</summary>
    public static string Serialize(IEnumerable<string>? order, IEnumerable<string>? shown)
    {
        var ids = (order ?? Array.Empty<string>()).Where(ValidId).Distinct().Take(MaxWidgets).ToList();
        var on = (shown ?? Array.Empty<string>()).ToHashSet();
        return JsonSerializer.Serialize(new { order = ids, hidden = ids.Where(i => !on.Contains(i)).ToList() });
    }

    /// <summary>
    /// The widgets in the layout's order, and which of them are hidden. What the layout names but no longer exists is skipped; what exists but the layout does not name
    /// (a new plugin card) comes after the others, shown. No layout = the order <paramref name="defaults"/> came in.
    /// </summary>
    public static (List<Widget> Ordered, HashSet<string> Hidden) Apply(IReadOnlyList<Widget> defaults, Config? config)
    {
        var known = defaults.GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.First());
        var ids = (config?.Order ?? Array.Empty<string>()).Where(known.ContainsKey).Distinct().ToList();
        ids.AddRange(defaults.Select(w => w.Id).Where(i => !ids.Contains(i)).Distinct());
        var hidden = (config?.Hidden ?? new HashSet<string>()).Where(known.ContainsKey).ToHashSet();
        return (ids.Select(i => known[i]).ToList(), hidden);
    }

    /// <summary>Groups what is drawn into rows: a wide widget (the tiles, the error list) is a row of its own, consecutive cards share a grid row.</summary>
    public static List<List<Widget>> Rows(IEnumerable<Widget> visible)
    {
        var rows = new List<List<Widget>>();
        foreach (var w in visible)
        {
            if (w.Wide || rows.Count == 0 || rows[^1][0].Wide) rows.Add(new List<Widget> { w });
            else rows[^1].Add(w);
        }
        return rows;
    }
}
