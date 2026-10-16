using System.Globalization;
using System.Text;
using MatCMS.Plugin;
using static MatCMS.Plugin.Shop.ShopHtml;

namespace MatCMS.Plugin.Shop;

/// <summary>What the catalogue page was asked for: the category, keyword, search words, sort order, the attribute filters and the price range. Every link and every hidden field of the page's
/// forms is built from it, so a filter that is switched on is carried through the other controls and one that is switched off disappears from the address.</summary>
internal sealed record CatalogState(string Cat, string Tag, string Q, string Sort, IReadOnlyList<AttributeFilter> Filters, long? PriceMin, long? PriceMax)
{
    public string Action => Cat.Length > 0 ? ShopUrls.Category(Cat) : ShopUrls.Root;
    public bool HasPrice => PriceMin is not null || PriceMax is not null;
    public bool Filtered => Cat.Length > 0 || Tag.Length > 0 || Q.Length > 0 || Filters.Count > 0 || HasPrice;

    /// <summary>The attribute filters (except <paramref name="withoutAttr"/>) and the price as address pairs.</summary>
    public List<KeyValuePair<string, string>> Extra(long? withoutAttr = null, bool attrs = true, bool price = true)
    {
        var l = attrs ? ShopAttributes.ToQuery(Filters.Where(f => f.Attr.Id != withoutAttr).ToList()) : new List<KeyValuePair<string, string>>();
        if (price) l.AddRange(ShopCatalogUi.PricePairs(PriceMin, PriceMax));
        return l;
    }

    /// <summary>The catalogue address with some parts replaced: null keeps a part, "" drops it.</summary>
    public string Url(string? tag = null, string? q = null, string? sort = null, IEnumerable<KeyValuePair<string, string>>? extra = null) =>
        ShopUrls.Catalog(Cat, tag ?? Tag, q ?? Q, sort ?? Sort, 1, extra ?? Extra());

    /// <summary>The state as hidden fields for a form that changes one part of it.</summary>
    public string Hidden(bool q = true, bool tag = true, bool sort = true, long? withoutAttr = null, bool attrs = true, bool price = true)
    {
        var sb = new StringBuilder();
        void Add(string name, string value) => sb.Append($"<input type=\"hidden\" name=\"{E(name)}\" value=\"{E(value)}\">");
        if (q && Q.Length > 0) Add("q", Q);
        if (tag && Tag.Length > 0) Add("tag", Tag);
        if (sort && Sort.Length > 0) Add("sort", Sort);
        foreach (var kv in Extra(withoutAttr, attrs, price)) Add(kv.Key, kv.Value);
        return sb.ToString();
    }
}

/// <summary>The controls of the catalogue page — search field, sort drop-down, category navigation, filters and the chips of what is switched on. Each is one placeholder of the template part
/// <c>catalog</c>, so a template can place them as it likes or leave them out; the look is the shop's classes on the site's own variables (<c>--accent</c>, <c>--line</c> …).</summary>
internal static class ShopCatalogUi
{
    private const string Magnifier = "<svg class=\"ico\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><circle cx=\"11\" cy=\"11\" r=\"7\"/><path d=\"m20 20-3.5-3.5\"/></svg>";

    // ---- the price range -------------------------------------------------------------------------------------

    /// <summary>Euros as a visitor types them ("12", "12,5", "12.50") in cents; null for nothing usable.</summary>
    internal static long? Cents(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!decimal.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) || v < 0 || v > 1_000_000m) return null;
        return (long)Math.Round(v * 100m, MidpointRounding.AwayFromZero);
    }

    internal static string Euros(long cents) => (cents / 100m).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The range the address asks for (<c>price.min</c>, <c>price.max</c>, in euros); a reversed pair is turned around.</summary>
    public static (long? Min, long? Max) ReadPrice(Func<string, string> q)
    {
        var lo = Cents(q("price.min")); var hi = Cents(q("price.max"));
        return lo is { } a && hi is { } b && a > b ? (b, a) : (lo, hi);
    }

    public static IEnumerable<KeyValuePair<string, string>> PricePairs(long? min, long? max)
    {
        if (min is { } a) yield return new("price.min", Euros(a));
        if (max is { } b) yield return new("price.max", Euros(b));
    }

    // ---- the controls ----------------------------------------------------------------------------------------

    /// <summary>A price as a hint in a price box: whole euros without decimals, otherwise two, with the visitor's decimal sign.</summary>
    internal static string PricePlaceholder(IPluginContext ctx, long cents)
    {
        var text = cents % 100 == 0 ? (cents / 100).ToString(CultureInfo.InvariantCulture) : (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        return ctx.Locale == "de" ? text.Replace('.', ',') : text;
    }

    /// <summary>The search field: a rounded box with the magnifier inside and the button at its end. "" when the shop switched it off.</summary>
    public static string Find(IPluginContext ctx, ShopConfig cfg, CatalogState s) => FindForm(ctx, cfg, s) is { Length: > 0 } form ? form + ShopSuggest.ScriptFor(ctx) : "";

    private static string FindForm(IPluginContext ctx, ShopConfig cfg, CatalogState s)
    {
        if (!cfg.ShowSearch) return "";
        var ph = E(ctx.T("shop.search"));
        return $"<form method=\"get\" action=\"{E(s.Action)}\" class=\"shop-find\" role=\"search\">{Magnifier}<input type=\"search\" name=\"q\" value=\"{E(s.Q)}\" placeholder=\"{ph}\" aria-label=\"{ph}\">{s.Hidden(q: false)}<button class=\"shop-btn\" type=\"submit\">{E(ctx.T("shop.searchButton"))}</button></form>";
    }

    /// <summary>The sort drop-down (submits itself; without script a button appears).</summary>
    public static string Sort(IPluginContext ctx, ShopConfig cfg, CatalogState s)
    {
        if (!cfg.ShowSort) return "";
        var sb = new StringBuilder($"<form method=\"get\" action=\"{E(s.Action)}\" class=\"shop-sort\"><label><span>{E(ctx.T("shop.sort.label"))}</span><select name=\"sort\" onchange=\"this.form.submit()\">");
        foreach (var x in ProductQuery.Sorts) sb.Append($"<option value=\"{E(x)}\"{(x == s.Sort ? " selected" : "")}>{E(ShopPublic.SortLabel(ctx, x))}</option>");
        return sb.Append("</select></label>").Append(s.Hidden(sort: false)).Append($"<noscript><button class=\"shop-btn ghost\" type=\"submit\">OK</button></noscript></form>").ToString();
    }

    /// <summary>The categories as a list down the side: all, the top level with counts, and — for the branch being looked at — its children one step in. The branch stays open.</summary>
    public static string CategoryNav(IPluginContext ctx, ShopConfig cfg, CatalogState s, IReadOnlyList<Category> cats, IReadOnlyDictionary<string, int> counts, IReadOnlyList<Category> path)
    {
        var roots = ShopCategories.Roots(cats);
        if (!cfg.ShowCategories || roots.Count == 0) return "";
        var heading = E(ctx.T("shop.categories"));
        var sb = new StringBuilder($"<nav class=\"shop-catnav\" aria-label=\"{heading}\"><h3>{heading}</h3><ul>");
        sb.Append($"<li><a href=\"{E(ShopUrls.Catalog("", s.Tag, s.Q, s.Sort))}\"{(s.Cat.Length == 0 ? " class=\"on\" aria-current=\"page\"" : "")}>{E(ctx.T("shop.allCategories"))}</a></li>");
        void Level(IEnumerable<Category> items)
        {
            foreach (var c in items)
            {
                counts.TryGetValue(c.Slug, out var n);
                var inPath = path.Any(x => x.Id == c.Id);
                if (n == 0 && !inPath) continue;       // a category is only offered while something below it can be bought
                var here = c.Slug == s.Cat;
                sb.Append($"<li><a href=\"{E(ShopUrls.Catalog(c.Slug, s.Tag, s.Q, s.Sort))}\"{(inPath ? " class=\"on\"" : "")}{(here ? " aria-current=\"page\"" : "")}>{E(c.Name)} <span>{Intl(n)}</span></a>");
                if (inPath && ShopCategories.Children(cats, c.Id) is { Count: > 0 } kids) { sb.Append("<ul>"); Level(kids); sb.Append("</ul>"); }
                sb.Append("</li>");
            }
        }
        Level(roots);
        return sb.Append("</ul></nav>").ToString();
    }

    /// <summary>The filters: the price range, then each attribute that has something to pick — its values as boxes that switch them on and off (with how many products have them), or a from–to for a number.</summary>
    public static string Filters(IPluginContext ctx, ShopConfig cfg, CatalogState s, IReadOnlyList<ShopAttribute> attrs, IReadOnlyDictionary<long, AttributeFacet> facets, (long Lo, long Hi)? priceExtent)
    {
        var sb = new StringBuilder();
        if (priceExtent is { } pe && (pe.Lo != pe.Hi || s.HasPrice))
        {
            sb.Append($"<details class=\"shop-fgroup\" open><summary>{E(ctx.T("shop.price"))}</summary><form method=\"get\" action=\"{E(s.Action)}\" class=\"shop-frange\">{s.Hidden(price: false)}");
            sb.Append($"<input type=\"number\" inputmode=\"decimal\" step=\"any\" min=\"0\" name=\"price.min\" value=\"{(s.PriceMin is { } a ? Euros(a) : "")}\" placeholder=\"{E(PricePlaceholder(ctx, pe.Lo))}\" aria-label=\"{E(ctx.T("shop.price"))} {E(ctx.T("shop.attrFrom"))}\">");
            sb.Append($"<span aria-hidden=\"true\">–</span><input type=\"number\" inputmode=\"decimal\" step=\"any\" min=\"0\" name=\"price.max\" value=\"{(s.PriceMax is { } b ? Euros(b) : "")}\" placeholder=\"{E(PricePlaceholder(ctx, pe.Hi))}\" aria-label=\"{E(ctx.T("shop.price"))} {E(ctx.T("shop.attrTo"))}\">");
            sb.Append($"<span class=\"cur\">{E(cfg.Currency)}</span><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.attrApply"))}</button></form></details>");
        }
        foreach (var a in attrs)
        {
            if (!facets.TryGetValue(a.Id, out var fc)) continue;
            var mine = s.Filters.FirstOrDefault(f => f.Attr.Id == a.Id);
            var title = E(a.Name) + (a.Unit.Length > 0 && a.Kind == AttributeKind.Number ? " (" + E(a.Unit) + ")" : "");
            if (a.Kind == AttributeKind.Number)
            {
                if (fc.Min is not { } lo || fc.Max is not { } hi || (lo == hi && mine is null)) continue;
                string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
                sb.Append($"<details class=\"shop-fgroup\" open><summary>{title}</summary><form method=\"get\" action=\"{E(s.Action)}\" class=\"shop-frange\">{s.Hidden(withoutAttr: a.Id)}");
                sb.Append($"<input type=\"number\" step=\"any\" name=\"{E(ShopAttributes.Key(a))}.min\" value=\"{(mine?.Min is { } mn ? N(mn) : "")}\" placeholder=\"{E(ctx.T("shop.attrFrom"))} {N(lo)}\" aria-label=\"{E(a.Name)} {E(ctx.T("shop.attrFrom"))}\">");
                sb.Append($"<span aria-hidden=\"true\">–</span><input type=\"number\" step=\"any\" name=\"{E(ShopAttributes.Key(a))}.max\" value=\"{(mine?.Max is { } mx ? N(mx) : "")}\" placeholder=\"{E(ctx.T("shop.attrTo"))} {N(hi)}\" aria-label=\"{E(a.Name)} {E(ctx.T("shop.attrTo"))}\">");
                sb.Append($"<button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.attrApply"))}</button></form></details>");
                continue;
            }
            if (fc.Values.Count == 0) continue;
            sb.Append($"<details class=\"shop-fgroup\" open><summary>{title}</summary><div class=\"opts\">");
            foreach (var (value, count) in fc.Values)
            {
                var on = mine?.Values.Contains(value, StringComparer.OrdinalIgnoreCase) == true;
                var next = on ? mine!.Values.Where(x => !string.Equals(x, value, StringComparison.OrdinalIgnoreCase)).ToList() : (mine?.Values ?? Array.Empty<string>()).Append(value).ToList();
                var extra = s.Extra(a.Id);
                if (next.Count > 0) extra.Add(new(ShopAttributes.Key(a), string.Join(",", next)));
                sb.Append($"<a href=\"{E(s.Url(extra: extra))}\" class=\"{(on ? "on" : "")}\" rel=\"nofollow\"><i class=\"box\" aria-hidden=\"true\"></i>{E(a.Kind == AttributeKind.Bool ? ctx.T("shop.attrYes") : value)} <span>{Intl(count)}</span></a>");
            }
            sb.Append("</div></details>");
        }
        if (sb.Length == 0) return "";
        var reset = s.Filters.Count > 0 ? $"<a class=\"clear\" href=\"{E(s.Url(extra: s.Extra(attrs: false)))}\" rel=\"nofollow\">{E(ctx.T("shop.attrFilterReset"))}</a>" : "";
        return $"<div class=\"shop-filters\">{sb}{reset}</div>";
    }

    /// <summary>What is switched on — the search words, the keyword, each attribute value and the price range — as chips, each with its own × that takes just that one off, and one link that clears all.</summary>
    public static string Active(IPluginContext ctx, ShopConfig cfg, CatalogState s)
    {
        var chips = new List<(string Label, string Url)>();
        if (s.Q.Length > 0) chips.Add((ctx.T("shop.chip.search", s.Q), s.Url(q: "")));
        if (s.Tag.Length > 0) chips.Add((ctx.T("shop.chip.tag", s.Tag), s.Url(tag: "")));
        string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
        foreach (var f in s.Filters)
        {
            var others = s.Extra(f.Attr.Id);
            var unit = f.Attr.Unit.Length > 0 ? " " + f.Attr.Unit : "";
            if (f.Attr.Kind == AttributeKind.Number)
            {
                var range = f.Min is { } lo && f.Max is { } hi ? $"{N(lo)}–{N(hi)}" : f.Min is { } l2 ? "≥ " + N(l2) : f.Max is { } h2 ? "≤ " + N(h2) : "";
                if (range.Length > 0) chips.Add(($"{f.Attr.Name}: {range}{unit}", s.Url(extra: others)));
                continue;
            }
            foreach (var v in f.Values)
            {
                var rest = f.Values.Where(x => !string.Equals(x, v, StringComparison.OrdinalIgnoreCase)).ToList();
                var ex = new List<KeyValuePair<string, string>>(others);
                if (rest.Count > 0) ex.Add(new(ShopAttributes.Key(f.Attr), string.Join(",", rest)));
                chips.Add(($"{f.Attr.Name}: {(f.Attr.Kind == AttributeKind.Bool ? ctx.T("shop.attrYes") : v)}", s.Url(extra: ex)));
            }
        }
        if (s.HasPrice)
        {
            string M(long c) => Money.Format(c, cfg.Currency, ctx.Locale);
            var range = s.PriceMin is { } a && s.PriceMax is { } b ? $"{M(a)} – {M(b)}" : s.PriceMin is { } a2 ? "≥ " + M(a2) : "≤ " + M(s.PriceMax!.Value);
            chips.Add(($"{ctx.T("shop.price")}: {range}", s.Url(extra: s.Extra(price: false))));
        }
        if (chips.Count == 0) return "";
        var sb = new StringBuilder("<div class=\"shop-active\">");
        foreach (var (label, url) in chips)
            sb.Append($"<a class=\"chip\" href=\"{E(url)}\" rel=\"nofollow\" aria-label=\"{E(ctx.T("shop.chip.remove", label))}\">{E(label)}<span aria-hidden=\"true\">×</span></a>");
        sb.Append($"<a class=\"clear\" href=\"{E(ShopUrls.Catalog(s.Cat, "", "", ""))}\" rel=\"nofollow\">{E(ctx.T("shop.resetFilter"))}</a></div>");
        return sb.ToString();
    }
}
