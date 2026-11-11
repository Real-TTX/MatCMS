using System.Globalization;
using System.Text;
using MatCMS.Plugin;
using static MatCMS.Plugin.Shop.ShopHtml;

namespace MatCMS.Plugin.Shop;

/// <summary>What the catalogue page was asked for: the category, keyword, search words, sort order, the attribute filters and the price range. Every link and every hidden field of the page's
/// forms is built from it, so a filter that is switched on is carried through the other controls and one that is switched off disappears from the address.</summary>
internal sealed record CatalogState(string Cat, string Tag, string Q, string Sort, IReadOnlyList<AttributeFilter> Filters, long? PriceMin, long? PriceMax, IReadOnlyList<string>? Brands = null)
{
    /// <summary>The brands filtered by (slugs, any of them; <c>?brand=a,b</c>).</summary>
    public IReadOnlyList<string> BrandList => Brands ?? Array.Empty<string>();
    public string Action => Cat.Length > 0 ? ShopUrls.Category(Cat) : ShopUrls.Root;
    public bool HasPrice => PriceMin is not null || PriceMax is not null;
    /// <summary>The keywords filtered by (a product must carry all of them; <c>?tag=a,b</c>).</summary>
    public IReadOnlyList<string> TagList => ShopTags.Selection(Tag);
    public bool Filtered => Cat.Length > 0 || Tag.Length > 0 || Q.Length > 0 || Filters.Count > 0 || HasPrice || BrandList.Count > 0;

    /// <summary>The attribute filters (except <paramref name="withoutAttr"/>) and the price as address pairs.</summary>
    public List<KeyValuePair<string, string>> Extra(long? withoutAttr = null, bool attrs = true, bool price = true, bool brand = true)
    {
        var l = attrs ? ShopAttributes.ToQuery(Filters.Where(f => f.Attr.Id != withoutAttr).ToList()) : new List<KeyValuePair<string, string>>();
        if (brand && BrandList.Count > 0) l.Insert(0, new("brand", string.Join(",", BrandList)));
        if (price) l.AddRange(ShopCatalogUi.PricePairs(PriceMin, PriceMax));
        return l;
    }

    /// <summary>The catalogue address with some parts replaced: null keeps a part, "" drops it.</summary>
    public string Url(string? tag = null, string? q = null, string? sort = null, IEnumerable<KeyValuePair<string, string>>? extra = null) =>
        ShopUrls.Catalog(Cat, tag ?? Tag, q ?? Q, sort ?? Sort, 1, extra ?? Extra());

    /// <summary>The other filters plus the brands <paramref name="brands"/> instead of the ones set now (none = the brand filter is off).</summary>
    public List<KeyValuePair<string, string>> WithBrands(IReadOnlyCollection<string> brands)
    {
        var l = Extra(brand: false);
        if (brands.Count > 0) l.Insert(0, new("brand", string.Join(",", brands)));
        return l;
    }

    /// <summary>The state as hidden fields for a form that changes one part of it.</summary>
    public string Hidden(bool q = true, bool tag = true, bool sort = true, long? withoutAttr = null, bool attrs = true, bool price = true, bool brand = true)
    {
        var sb = new StringBuilder();
        void Add(string name, string value) => sb.Append($"<input type=\"hidden\" name=\"{E(name)}\" value=\"{E(value)}\">");
        if (q && Q.Length > 0) Add("q", Q);
        if (tag && Tag.Length > 0) Add("tag", Tag);
        if (sort && Sort.Length > 0) Add("sort", Sort);
        foreach (var kv in Extra(withoutAttr, attrs, price, brand)) Add(kv.Key, kv.Value);
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

    /// <summary>The brands as a filter group: each one that has something on this page, as a box that switches it on and off (with how many products it has).</summary>
    public static string BrandGroup(IPluginContext ctx, CatalogState s, IReadOnlyList<Brand> brands, IReadOnlyDictionary<string, int> counts, int limit = 0)
    {
        var all = BrandEntries(s, brands, counts);
        if (all.Count == 0 || (all.Count == 1 && s.BrandList.Count == 0)) return "";
        return $"<details class=\"shop-fgroup\" open><summary>{E(ctx.T("shop.brand"))}</summary>{Entries(ctx, s, BrandKey, ctx.T("shop.brand"), all, limit)}</details>";
    }

    // ---- filter groups: entries, the limit and "Weitere …" -----------------------------------------------

    /// <summary>The group key of the brand filter and of the keywords (an attribute's is its address key, <c>a.farbe</c>).</summary>
    public const string BrandKey = "brand", TagKey = "tag";

    /// <summary>"Weitere …" and its dialog, on the site's own variables.</summary>
    public const string MoreCss = """
        .shop-fmore{display:inline-block;margin-top:8px;font-size:.9em;color:var(--shop-accent,var(--accent,inherit));text-decoration:underline;text-underline-offset:3px;cursor:pointer}
        .shop-tagsgroup .shop-fmore{display:block}
        .shop-fdialog{border:0;border-radius:var(--shop-radius,12px);padding:0;width:min(440px,92vw);max-height:80vh;background:var(--shop-bg,var(--bg,#fff));color:inherit;box-shadow:0 20px 60px rgba(0,0,0,.25)}
        .shop-fdialog[open]{display:flex;flex-direction:column}.shop-fdialog::backdrop{background:rgba(0,0,0,.4)}
        .shop-fdialog .hd{display:flex;justify-content:space-between;align-items:center;gap:12px;padding:14px 18px;border-bottom:1px solid var(--shop-line,var(--line,rgba(0,0,0,.12)))}
        .shop-fdialog .x{border:0;background:none;color:inherit;font-size:1.6em;line-height:1;cursor:pointer;padding:0 4px}
        .shop-fdialog .q{margin:12px 18px 4px;padding:9px 12px;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.2)));border-radius:8px;font:inherit;background:transparent;color:inherit}
        .shop-fdialog .body{overflow:auto;padding:0 18px 16px;border:0}.shop-fdialog .body .opts{margin-top:4px}
        .shop-wd-orderrow{display:flex;gap:8px;align-items:center}.shop-wd-orderrow input{flex:1}.shop-wd-items{border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.14)));border-radius:10px;padding:12px 14px;display:grid;gap:6px}
        .shop-wd-items legend{padding:0 6px;font-weight:600}.shop-check{display:flex;gap:8px;align-items:center}.shop-wd-sum{display:grid;grid-template-columns:max-content 1fr;gap:6px 18px;margin:12px 0}.shop-wd-sum dt{font-weight:600}.shop-wd-sum dd{margin:0;white-space:pre-wrap}
        .shop-wd-orders{display:grid;gap:4px;margin-top:6px}.shop-wd-order{display:grid;gap:2px;padding:10px 12px;border-radius:8px;text-decoration:none;color:inherit;border:1px solid var(--shop-line,var(--line,rgba(0,0,0,.12)))}
        .shop-wd-order:hover{border-color:var(--shop-accent,var(--accent,#1a1a1a))}.shop-wd-order .t{font-size:.9em;opacity:.75}.shop-wd-actions{display:flex;gap:10px;flex-wrap:wrap;align-items:center}.shop-wd .shop-card-box{max-width:760px}
        """;

    /// <summary>Opens "Weitere …" as a dialog: the group's entries are fetched from <c>data-more</c> only now, a search box narrows them as one types. Without script (or
    /// when the fetch fails) the link leads to the page with that group in full. The fetched markup is the shop's own, encoded on the server.</summary>
    public const string MoreScript = """
        <script>(function(){if(window.__shopMore)return;window.__shopMore=1;
        document.addEventListener('click',function(e){var a=e.target.closest&&e.target.closest('a.shop-fmore');if(!a||!window.fetch||!window.HTMLDialogElement)return;e.preventDefault();
        var d=document.createElement('dialog');d.className='shop-fdialog';
        d.innerHTML='<div class="hd"><strong></strong><button type="button" class="x">×</button></div><input type="search" class="q"><div class="shop-fgroup body"><p class="shop-muted">…</p></div>';
        d.querySelector('strong').textContent=a.getAttribute('data-title')||'';var x=d.querySelector('.x');x.setAttribute('aria-label',a.getAttribute('data-close')||'');
        var q=d.querySelector('.q');q.placeholder=a.getAttribute('data-search')||'';q.setAttribute('aria-label',q.placeholder);var body=d.querySelector('.body');
        document.body.appendChild(d);d.showModal();d.addEventListener('close',function(){d.remove()});x.addEventListener('click',function(){d.close()});
        d.addEventListener('click',function(ev){if(ev.target===d)d.close()});
        fetch(a.getAttribute('data-more'),{credentials:'same-origin'}).then(function(r){if(!r.ok)throw 0;return r.text()}).then(function(h){body.innerHTML=h;q.focus()}).catch(function(){location.href=a.href});
        q.addEventListener('input',function(){var v=q.value.trim().toLowerCase();Array.prototype.forEach.call(body.querySelectorAll('.opts a'),function(o){o.style.display=!v||o.textContent.toLowerCase().indexOf(v)>=0?'':'none'})});
        });})();</script>
        """;

    /// <summary>One entry of a filter group: where a click leads (it is switched on or off there), its words, whether it is on, how many products it has.</summary>
    internal readonly record struct FilterEntry(string Href, string Label, bool On, int Count);

    private static string Box(FilterEntry e) =>
        $"<a href=\"{E(e.Href)}\" class=\"{(e.On ? "on" : "")}\" rel=\"nofollow\"><i class=\"box\" aria-hidden=\"true\"></i>{E(e.Label)} <span>{Intl(e.Count)}</span></a>";

    private static string Chip(FilterEntry e) => $"<a href=\"{E(e.Href)}\" class=\"{(e.On ? "on" : "")}\" rel=\"nofollow\">{E(e.Label)}</a>";

    /// <summary>All entries of a group as boxes — what the "Weitere …" dialog shows (<c>/shop/filters?group=…</c>).</summary>
    public static string AllBoxes(IEnumerable<FilterEntry> entries) => "<div class=\"opts\">" + string.Concat(entries.Select(Box)) + "</div>";

    /// <summary>A group's entries: all of them, or — with a <paramref name="limit"/> — the switched-on ones and the busiest up to the limit (in the group's own order) and a
    /// "Weitere …" that opens the rest in a dialog with a search. The rest is fetched only when the dialog opens; without script the link shows this group in full.</summary>
    internal static string Entries(IPluginContext ctx, CatalogState s, string group, string title, IReadOnlyList<FilterEntry> all, int limit, bool chips = false)
    {
        IReadOnlyList<FilterEntry> shown = all;
        if (limit > 0 && all.Count > limit)
        {
            var keep = all.Where(e => e.On).Concat(all.Where(e => !e.On).OrderByDescending(e => e.Count))
                .Take(Math.Max(limit, all.Count(e => e.On))).Select(e => e.Href).ToHashSet(StringComparer.Ordinal);
            shown = all.Where(e => keep.Contains(e.Href)).ToList();
        }
        var sb = new StringBuilder(chips ? $"<div class=\"shop-tags\" aria-label=\"{E(title)}\">" : "<div class=\"opts\">");
        foreach (var e in shown) sb.Append(chips ? Chip(e) : Box(e));
        sb.Append("</div>");
        if (shown.Count < all.Count)
        {
            var full = s.Url(extra: s.Extra().Append(new("more", group)));
            var u = s.Url();
            var qi = u.IndexOf('?');
            var frag = "/shop/filters?group=" + Uri.EscapeDataString(group) + (s.Cat.Length > 0 ? "&cat=" + Uri.EscapeDataString(s.Cat) : "") + (qi >= 0 ? "&" + u[(qi + 1)..] : "");
            sb.Append($"<a class=\"shop-fmore\" href=\"{E(full)}\" data-more=\"{E(frag)}\" data-title=\"{E(title)}\" data-search=\"{E(ctx.T("shop.filterSearch"))}\" data-close=\"{E(ctx.T("shop.filterClose"))}\" rel=\"nofollow\">{E(ctx.T("shop.filterMore", all.Count - shown.Count))}</a>");
        }
        return sb.ToString();
    }

    /// <summary>The brands as filter entries: each one that has something on this page (alphabetical), switched on and off one by one.</summary>
    public static List<FilterEntry> BrandEntries(CatalogState s, IReadOnlyList<Brand> brands, IReadOnlyDictionary<string, int> counts) =>
        brands.Where(b => counts.ContainsKey(b.Slug) || s.BrandList.Contains(b.Slug)).OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).Select(b =>
        {
            var on = s.BrandList.Contains(b.Slug);
            var next = on ? s.BrandList.Where(x => x != b.Slug).ToList() : s.BrandList.Append(b.Slug).ToList();
            return new FilterEntry(s.Url(extra: s.WithBrands(next)), b.Name, on, counts.GetValueOrDefault(b.Slug));
        }).ToList();

    /// <summary>The values of a choice attribute as filter entries (any of them; each switched on and off on its own).</summary>
    public static List<FilterEntry> AttrEntries(IPluginContext ctx, CatalogState s, ShopAttribute a, AttributeFacet fc)
    {
        var mine = s.Filters.FirstOrDefault(f => f.Attr.Id == a.Id);
        return fc.Values.Select(vc =>
        {
            var on = mine?.Values.Contains(vc.Value, StringComparer.OrdinalIgnoreCase) == true;
            var next = on ? mine!.Values.Where(x => !string.Equals(x, vc.Value, StringComparison.OrdinalIgnoreCase)).ToList() : (mine?.Values ?? Array.Empty<string>()).Append(vc.Value).ToList();
            var extra = s.Extra(a.Id);
            if (next.Count > 0) extra.Add(new(ShopAttributes.Key(a), string.Join(",", next)));
            return new FilterEntry(s.Url(extra: extra), a.Kind == AttributeKind.Bool ? ctx.T("shop.attrYes") : vc.Value, on, vc.Count);
        }).ToList();
    }

    /// <summary>The keywords as filter entries: those the products of this page carry (busiest first) and the ones switched on. A click adds a keyword to the selection or
    /// takes it off — a product must carry all of them, so the counts say what is left with that keyword added.</summary>
    public static List<FilterEntry> TagEntries(CatalogState s, IReadOnlyList<(string Tag, int Count)> tags)
    {
        var sel = s.TagList;
        var list = tags.ToList();
        foreach (var t in sel) if (!list.Any(x => string.Equals(x.Tag, t, StringComparison.OrdinalIgnoreCase))) list.Add((t, 0));
        return list.Select(t => new FilterEntry(s.Url(tag: ShopTags.Toggle(sel, t.Tag)), t.Tag, sel.Any(x => string.Equals(x, t.Tag, StringComparison.OrdinalIgnoreCase)), t.Count)).ToList();
    }

    /// <summary>The keywords group of the side column ("" when there are none).</summary>
    public static string TagGroup(IPluginContext ctx, CatalogState s, IReadOnlyList<(string Tag, int Count)> tags, int limit)
    {
        var all = TagEntries(s, tags);
        if (all.Count == 0) return "";
        return $"<div class=\"shop-fgroup shop-tagsgroup\"><h3>{E(ctx.T("shop.tags"))}</h3>{Entries(ctx, s, TagKey, ctx.T("shop.tags"), all, limit, chips: true)}</div>";
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
    public static string Filters(IPluginContext ctx, ShopConfig cfg, CatalogState s, IReadOnlyList<ShopAttribute> attrs, IReadOnlyDictionary<long, AttributeFacet> facets, (long Lo, long Hi)? priceExtent, string brandGroup = "", Func<string, int>? limitFor = null)
    {
        var sb = new StringBuilder();
        if (priceExtent is { } pe && (pe.Lo != pe.Hi || s.HasPrice))
        {
            sb.Append($"<details class=\"shop-fgroup\" open><summary>{E(ctx.T("shop.price"))}</summary><form method=\"get\" action=\"{E(s.Action)}\" class=\"shop-frange\">{s.Hidden(price: false)}");
            sb.Append($"<input type=\"number\" inputmode=\"decimal\" step=\"any\" min=\"0\" name=\"price.min\" value=\"{(s.PriceMin is { } a ? Euros(a) : "")}\" placeholder=\"{E(PricePlaceholder(ctx, pe.Lo))}\" aria-label=\"{E(ctx.T("shop.price"))} {E(ctx.T("shop.attrFrom"))}\">");
            sb.Append($"<span aria-hidden=\"true\">–</span><input type=\"number\" inputmode=\"decimal\" step=\"any\" min=\"0\" name=\"price.max\" value=\"{(s.PriceMax is { } b ? Euros(b) : "")}\" placeholder=\"{E(PricePlaceholder(ctx, pe.Hi))}\" aria-label=\"{E(ctx.T("shop.price"))} {E(ctx.T("shop.attrTo"))}\">");
            sb.Append($"<span class=\"cur\">{E(cfg.Currency)}</span><button class=\"shop-btn ghost\" type=\"submit\">{E(ctx.T("shop.attrApply"))}</button></form></details>");
        }
        sb.Append(brandGroup);
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
            var key = ShopAttributes.Key(a);
            sb.Append($"<details class=\"shop-fgroup\" open><summary>{title}</summary>{Entries(ctx, s, key, a.Name, AttrEntries(ctx, s, a, fc), limitFor?.Invoke(key) ?? 0)}</details>");
        }
        if (sb.Length == 0) return "";
        var reset = s.Filters.Count > 0 || s.BrandList.Count > 0 ? $"<a class=\"clear\" href=\"{E(s.Url(extra: s.Extra(attrs: false, brand: false)))}\" rel=\"nofollow\">{E(ctx.T("shop.attrFilterReset"))}</a>" : "";
        return $"<div class=\"shop-filters\">{sb}{reset}</div>";
    }

    /// <summary>What is switched on — the search words, the keyword, each attribute value and the price range — as chips, each with its own × that takes just that one off, and one link that clears all.</summary>
    public static string Active(IPluginContext ctx, ShopConfig cfg, CatalogState s, IReadOnlyDictionary<string, string>? brandNames = null)
    {
        var chips = new List<(string Label, string Url)>();
        foreach (var b in s.BrandList)
            chips.Add((ctx.T("shop.chip.brand", brandNames?.GetValueOrDefault(b) ?? b), s.Url(extra: s.WithBrands(s.BrandList.Where(x => x != b).ToList()))));
        if (s.Q.Length > 0) chips.Add((ctx.T("shop.chip.search", s.Q), s.Url(q: "")));
        foreach (var t in s.TagList) chips.Add((ctx.T("shop.chip.tag", t), s.Url(tag: ShopTags.Toggle(s.TagList, t))));
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
