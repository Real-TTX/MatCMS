using System.Globalization;
using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// Search as you type, and "Meinten Sie …?" when nothing was found. The suggestions come from <c>/shop/suggest?q=</c> (JSON: a few products with picture and price, matching categories, and a corrected spelling
/// when nothing matches); the script that draws them attaches to every search box that sends the visitor to the shop — the catalogue's, the page block's and the menu's — and builds its list from text nodes
/// only, so nothing a product is called can become markup. Without script the boxes work as before (a plain search).
/// </summary>
internal static class ShopSuggest
{
    public const int MaxQuery = 60, MaxProducts = 6, MaxCategories = 3;

    /// <summary>Edit distance with transpositions (so "tsase" is one step from "tasse").</summary>
    public static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    /// <summary>The words of some texts: letters and digits, lower case, three letters or more, each once (first spelling kept by its lower-case form).</summary>
    public static List<string> Words(IEnumerable<string> texts)
    {
        var seen = new HashSet<string>(); var list = new List<string>();
        foreach (var t in texts)
        {
            var word = new System.Text.StringBuilder();
            void Flush() { if (word.Length >= 3 && seen.Add(word.ToString().ToLowerInvariant())) list.Add(word.ToString()); word.Clear(); }
            foreach (var c in t ?? "") { if (char.IsLetterOrDigit(c)) word.Append(c); else Flush(); }
            Flush();
        }
        return list;
    }

    /// <summary>
    /// A corrected spelling of <paramref name="query"/>: each word that is not in the vocabulary is replaced by the nearest vocabulary word (within a quarter of its length, at least one letter), and the
    /// whole is offered only when something changed. Short words (under 4 letters) are left alone — "TV" and "USB" are not typos. Null when there is nothing better to say.
    /// </summary>
    public static string? DidYouMean(string? query, IReadOnlyList<string> vocabulary)
    {
        var words = Words(new[] { query ?? "" });
        if (words.Count == 0 || vocabulary.Count == 0) return null;
        var known = new HashSet<string>(vocabulary.Select(v => v.ToLowerInvariant()));
        var changed = false; var parts = new List<string>();
        foreach (var w in words)
        {
            if (known.Contains(w.ToLowerInvariant()) || w.Length < 4) { parts.Add(w); continue; }
            var limit = Math.Max(1, w.Length / 4);
            string? best = null; var bestD = int.MaxValue;
            foreach (var v in vocabulary)
            {
                if (Math.Abs(v.Length - w.Length) > limit) continue;
                var dist = Distance(w, v);
                if (dist < bestD || dist == bestD && best is not null && string.CompareOrdinal(v, best) < 0) { best = v; bestD = dist; }
            }
            if (best is not null && bestD <= limit) { parts.Add(best); changed = true; } else parts.Add(w);
        }
        return changed ? string.Join(' ', parts) : null;
    }

    /// <summary>What the shop can be searched for: product titles, keywords and category names.</summary>
    public static async Task<List<string>> VocabularyAsync(ShopStore store, CancellationToken ct)
    {
        var products = (await store.ProductsAsync(true, ct: ct)).Items;
        var cats = await store.CategoriesAsync(ct);
        return Words(products.SelectMany(p => new[] { p.Title, p.Tags }).Concat(cats.Select(c => c.Name)));
    }

    public static async Task<PluginResult> SuggestAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = ShopConfig.From(ctx.Settings);
        var q = ctx.Request.Q("q").Trim();
        if (q.Length > MaxQuery) q = q[..MaxQuery];
        var answer = new JsonObject { ["q"] = q, ["products"] = new JsonArray(), ["categories"] = new JsonArray(), ["didYouMean"] = null };
        if (q.Length < 2 || !cfg.ShowSearch) return Json(answer);
        var store = ShopStore.Public(ctx);
        var found = (await store.FindProductsAsync(new ProductQuery { Search = q, SearchIn = cfg.SearchIn, Sort = "", Take = MaxProducts }, ct)).Items;
        var products = (JsonArray)answer["products"]!;
        foreach (var p in found)
            products.Add(new JsonObject { ["title"] = p.Title, ["url"] = ShopUrls.Product(p.Slug), ["image"] = ShopPublic.SafeUrl(p.Image), ["price"] = ShopPublic.M(ctx, cfg, p.PriceCents) });
        var cats = await store.CategoriesAsync(ct);
        var (counts, _) = await store.FacetsAsync(ct);
        var categories = (JsonArray)answer["categories"]!;
        foreach (var c in cats.Where(c => counts.ContainsKey(c.Slug) && c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(MaxCategories))
            categories.Add(new JsonObject { ["name"] = c.Name, ["url"] = ShopUrls.Category(c.Slug) });
        if (products.Count == 0 && categories.Count == 0 && DidYouMean(q, await VocabularyAsync(store, ct)) is { } fix) answer["didYouMean"] = fix;
        return Json(answer);
    }

    private static PluginResult Json(JsonObject o) => PluginResult.Json(o).WithHeader("Cache-Control", "private, no-store");

    /// <summary>The script. Idempotent (a flag on window), so each search box may carry it.</summary>
    public const string Script = "<script>(function(){if(window.__shopSuggest)return;window.__shopSuggest=1;"
        + "function path(f){try{return new URL(f.action,location.href).pathname}catch(e){return ''}}"
        + "function attach(input){if(input.__sg||path(input.form)!=='/shop')return;input.__sg=1;var box=null,items=[],cur=-1,timer=0,seq=0;input.setAttribute('autocomplete','off');input.setAttribute('role','combobox');input.setAttribute('aria-expanded','false');"
        + "var host=input.closest('.shop-find,.shop-searchbar,.mat-search')||input.parentNode;"
        + "function close(){if(box){box.remove();box=null}items=[];cur=-1;input.setAttribute('aria-expanded','false')}"
        + "function place(){if(!box)return;var r=host.getBoundingClientRect();box.style.left=(r.left+scrollX)+'px';box.style.top=(r.bottom+scrollY+4)+'px';box.style.minWidth=Math.max(r.width,280)+'px'}"
        + "function mk(tag,cls,txt){var e=document.createElement(tag);if(cls)e.className=cls;if(txt!=null)e.textContent=txt;return e}"
        + "function link(href,main,sub,img){var a=mk('a','shop-sg-item');a.href=href;a.setAttribute('role','option');if(img){var i=mk('img');i.src=img;i.alt='';i.loading='lazy';a.appendChild(i)}else if(sub){a.appendChild(mk('span','ph'))}var s=mk('span','t');s.appendChild(mk('span','n',main));if(sub)s.appendChild(mk('span','p',sub));a.appendChild(s);return a}"
        + "function draw(d){close();if(!d||(!d.products.length&&!d.categories.length&&!d.didYouMean))return;box=mk('div','shop-suggest');box.setAttribute('role','listbox');"
        + "d.products.forEach(function(p){box.appendChild(link(p.url,p.title,p.price,p.image))});"
        + "d.categories.forEach(function(c){box.appendChild(link(c.url,c.name,null,null))});"
        + "if(d.didYouMean){var u=new URL(input.form.action,location.href);u.searchParams.set('q',d.didYouMean);var a=mk('a','shop-sg-item shop-sg-fix');a.href=u.pathname+u.search;a.appendChild(mk('span','n',d.didYouMean));a.insertBefore(mk('span','p',(window.__shopSuggestFix||'Meinten Sie')+' '),a.firstChild);box.appendChild(a)}"
        + "place();box.addEventListener('mousedown',function(e){e.preventDefault()});"
        + "document.body.appendChild(box);items=[].slice.call(box.querySelectorAll('a'));input.setAttribute('aria-expanded','true')}"
        + "function mark(i){items.forEach(function(x){x.classList.remove('on')});cur=i;if(i>=0&&items[i]){items[i].classList.add('on');items[i].scrollIntoView({block:'nearest'})}}"
        + "function ask(){var q=input.value.trim();if(q.length<2){close();return}var n=++seq;fetch('/shop/suggest?q='+encodeURIComponent(q),{credentials:'same-origin'}).then(function(r){return r.ok?r.json():null}).then(function(d){if(n===seq&&input.value.trim()===q)draw(d)}).catch(function(){})}"
        + "input.addEventListener('input',function(){clearTimeout(timer);timer=setTimeout(ask,180)});"
        + "input.addEventListener('keydown',function(e){if(!box)return;if(e.key==='ArrowDown'){e.preventDefault();mark((cur+1)%items.length)}else if(e.key==='ArrowUp'){e.preventDefault();mark((cur-1+items.length)%items.length)}else if(e.key==='Escape'){close()}else if(e.key==='Enter'&&cur>=0){e.preventDefault();location.href=items[cur].href}});"
        + "document.addEventListener('click',function(e){if(box&&!host.contains(e.target)&&!box.contains(e.target))close()});window.addEventListener('resize',place);input.addEventListener('blur',function(){setTimeout(function(){if(!host.contains(document.activeElement))close()},150)})}"
        + "function scan(){[].slice.call(document.querySelectorAll('form input[name=q]')).forEach(attach)}"
        + "if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',scan);else scan()})();</script>";

    /// <summary>The script plus the words for its "did you mean" line, for the search boxes that carry it.</summary>
    public static string ScriptFor(IPluginContext ctx) => Script + "<script>window.__shopSuggestFix=" + System.Text.Json.JsonSerializer.Serialize(ctx.T("shop.sg.didYouMean")) + ";</script>";
}
