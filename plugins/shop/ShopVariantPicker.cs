using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The product page's variant choice as one picker per option (Größe: S M L — Farbe: Rot Blau) instead of one long list "S / Rot, S / Blau …". It is derived from what is stored, not new data: the
/// product's options (name → values) and the variants' names, which are the values joined by " / " (that is how "Kombinationen erzeugen" names them). A product whose variants do not fit its options
/// — a name that does not split, a value that is no option — keeps the plain list: a picker must never offer something the shop cannot sell. The chosen combination sets the hidden <c>variant</c> field,
/// the price shown and, when the variant has one, the picture. Without script the plain list is inside a <c>noscript</c>, so the form still posts.
/// </summary>
internal static class ShopVariantPicker
{
    public const string Separator = " / ";

    /// <summary>One sellable combination: its variant, the option values it is made of (the options' own spelling), what it costs and shows.</summary>
    public sealed record Choice(long Id, string[] Parts, long PriceCents, bool Available, string Image);

    /// <summary>The combinations in the options' terms, or null when the variants cannot be shown that way (no options, wrong number of parts, a part that is no value of its option, a name twice).</summary>
    public static IReadOnlyList<Choice>? Plan(IReadOnlyList<VariantOption> options, IReadOnlyList<Variant> variants, long basePriceCents)
    {
        if (options.Count == 0 || options.Count > ShopVariants.MaxOptions || variants.Count == 0) return null;
        var list = new List<Choice>();
        foreach (var v in variants)
        {
            var parts = v.Name.Split(Separator, StringSplitOptions.TrimEntries);
            if (parts.Length != options.Count) return null;
            var canon = new string[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                var hit = options[i].Values.FirstOrDefault(x => string.Equals(x, parts[i], StringComparison.OrdinalIgnoreCase));
                if (hit is null) return null;
                canon[i] = hit;
            }
            if (list.Any(c => c.Parts.SequenceEqual(canon, StringComparer.Ordinal))) return null;
            list.Add(new Choice(v.Id, canon, Math.Max(0, basePriceCents + v.PriceDeltaCents), !(v.TrackStock && v.Stock <= 0), ShopPublic.SafeUrl(v.Image)));
        }
        return list;
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>The picker's markup and its script. <paramref name="priceText"/> formats a price in cents the way the page does; <paramref name="notAvailable"/> is the words for a combination that is not for sale.</summary>
    public static string Html(IReadOnlyList<VariantOption> options, IReadOnlyList<Choice> choices, Func<long, string> priceText, string notAvailable)
    {
        var data = new JsonArray(choices.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id, ["parts"] = new JsonArray(c.Parts.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()), ["price"] = priceText(c.PriceCents), ["ok"] = c.Available, ["img"] = c.Image
        }).ToArray()).ToJsonString();
        var sb = new StringBuilder();
        sb.Append("<div class=\"shop-opts\" data-choices=\"").Append(E(data)).Append("\">");
        for (var i = 0; i < options.Count; i++)
        {
            sb.Append("<div class=\"shop-optgroup\"><div class=\"shop-optlabel\">").Append(E(options[i].Name)).Append("</div><div class=\"shop-chips\" role=\"radiogroup\" aria-label=\"").Append(E(options[i].Name)).Append("\">");
            foreach (var v in options[i].Values.Where(v => choices.Any(c => string.Equals(c.Parts[i], v, StringComparison.Ordinal))))
                sb.Append("<button type=\"button\" class=\"shop-chip\" role=\"radio\" aria-checked=\"false\" data-opt=\"").Append(i.ToString(CultureInfo.InvariantCulture)).Append("\" data-val=\"").Append(E(v)).Append("\">").Append(E(v)).Append("</button>");
            sb.Append("</div></div>");
        }
        sb.Append("<input type=\"hidden\" name=\"variant\" value=\"\"><p class=\"shop-small shop-optnote\" hidden>").Append(E(notAvailable)).Append("</p></div>");
        sb.Append("<script>(function(){var box=document.currentScript.previousElementSibling;if(!box||!box.dataset.choices)return;var ch=JSON.parse(box.dataset.choices),form=box.closest('form'),hid=box.querySelector('input[name=variant]'),note=box.querySelector('.shop-optnote'),"
            + "chips=[].slice.call(box.querySelectorAll('.shop-chip')),n=box.querySelectorAll('.shop-optgroup').length,sel=[],first=null;"
            + "function btn(){return form.querySelector('button[type=submit]')}function mainImg(){return document.querySelector('.main-img')}"
            + "function find(){return ch.find(function(c){return c.parts.every(function(p,i){return p===sel[i]})})}"
            + "function refresh(){var c=find();hid.value=c&&c.ok?c.id:'';note.hidden=!(sel.length===n&&sel.every(Boolean))||(c&&c.ok);var b=btn();if(b)b.disabled=!(c&&c.ok);"
            + "if(c){[].slice.call(document.querySelectorAll('[data-shop-price]')).forEach(function(p){p.textContent=c.price});var m=mainImg();if(m){if(first===null)first=m.getAttribute('src');m.src=c.img||first}}"
            + "chips.forEach(function(b){var i=+b.dataset.opt,on=sel[i]===b.dataset.val;b.setAttribute('aria-checked',on);b.tabIndex=on?0:-1;"
            + "b.classList.toggle('na',!ch.some(function(c2){return c2.ok&&c2.parts[i]===b.dataset.val&&c2.parts.every(function(p,j){return j===i||!sel[j]||p===sel[j]})}))})}"
            + "chips.forEach(function(b){b.addEventListener('click',function(){sel[+b.dataset.opt]=b.dataset.val;refresh()});"
            + "b.addEventListener('keydown',function(e){var k=e.key;if(k!=='ArrowRight'&&k!=='ArrowLeft'&&k!=='ArrowDown'&&k!=='ArrowUp')return;e.preventDefault();var g=chips.filter(function(x){return x.dataset.opt===b.dataset.opt}),i=g.indexOf(b)+(k==='ArrowRight'||k==='ArrowDown'?1:-1);var t=g[(i+g.length)%g.length];t.focus();t.click()})});"
            + "var start=ch.find(function(c){return c.ok})||ch[0];if(start)start.parts.forEach(function(p,i){sel[i]=p});refresh();if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',function(){var m=mainImg();if(m&&first===null)first=m.getAttribute('src');refresh()});})();</script>");
        return sb.ToString();
    }
}
