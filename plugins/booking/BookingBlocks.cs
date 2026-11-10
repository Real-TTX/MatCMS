using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// The page blocks: "Buchungsanfrage" (the form with the live bill), "Belegungskalender" (months, free and taken nights; two clicks fill the form on the
/// same page) and "Unterkünfte" (cards of the units with "ab … € / Nacht"). A block is drawn for every visitor: it shows availability, never a guest.
/// </summary>
public static class BookingBlocks
{
    public const string RequestBlock = "booking_request", CalendarBlock = "booking_calendar", UnitsBlock = "booking_units";

    public const string BusyPath = "/buchung/belegung";

    public static void Configure(IPluginBuilder b)
    {
        b.Route(BusyPath, BusyAsync, new PublicRouteOptions { Methods = PluginMethod.Get });
        b.OptionsSource("units", async (ctx, ct) =>
            (await new BookingStore(ctx.Data).UnitsAsync(ct)).Where(u => u.Active).Select(u => new FieldOption(u.Slug, u.Name)).ToList());

        b.Block(new BlockSpec(RequestBlock, "Buchungsanfrage", "Das Anfrageformular: Unterkunft, Zeitraum, Personen – der Preis erscheint sofort, die Anfrage merkt den Zeitraum vor.")
        {
            Category = "content",
            IconSvg = "<rect x=\"3\" y=\"4\" width=\"18\" height=\"17\" rx=\"2\"/><path d=\"M3 9h18\"/><path d=\"M8 2v4\"/><path d=\"M16 2v4\"/><path d=\"M9 15l2 2 4-4\"/>",
            Fields = new[]
            {
                new FieldSpec("heading", "Überschrift") { Placeholder = "Jetzt anfragen" },
                new FieldSpec("intro", "Einleitung", FieldKind.Textarea),
                new FieldSpec("unit", "Unterkunft", FieldKind.Select) { OptionsSource = "units", Help = "Leer = der Gast wählt im Formular." }
            }
        }, RequestBlockAsync);

        b.Block(new BlockSpec(CalendarBlock, "Belegungskalender", "Freie und belegte Nächte einer Unterkunft, zwei Monate zum Blättern. Steht das Anfrageformular auf derselben Seite, füllen zwei Klicks An- und Abreise.")
        {
            Category = "content",
            IconSvg = "<rect x=\"3\" y=\"4\" width=\"18\" height=\"17\" rx=\"2\"/><path d=\"M3 9h18\"/><path d=\"M8 13h2\"/><path d=\"M14 13h2\"/><path d=\"M8 17h2\"/>",
            Fields = new[]
            {
                new FieldSpec("heading", "Überschrift") { Placeholder = "Belegung" },
                new FieldSpec("unit", "Unterkunft", FieldKind.Select) { OptionsSource = "units" },
                new FieldSpec("months", "Monate zum Blättern", FieldKind.Select)
                {
                    Default = "12", Help = "Zu sehen sind zwei Monate nebeneinander (auf dem Handy einer), die Pfeile blättern weiter.", Options = new[] { new FieldOption("2", "2"), new FieldOption("3", "3"), new FieldOption("6", "6"), new FieldOption("12", "12") }
                }
            }
        }, CalendarBlockAsync);

        b.Block(new BlockSpec(UnitsBlock, "Unterkünfte", "Alle buchbaren Unterkünfte als Karten mit Bild, Personen und „ab … € / Nacht“.")
        {
            Category = "content",
            IconSvg = "<path d=\"M3 21h18\"/><path d=\"M5 21V8l7-5 7 5v13\"/><path d=\"M9 21v-6h6v6\"/>",
            Fields = new[]
            {
                new FieldSpec("heading", "Überschrift") { Placeholder = "Unsere Unterkünfte" },
                new FieldSpec("intro", "Einleitung", FieldKind.Textarea),
                new FieldSpec("link", "Ziel des Knopfs") { Default = "#anfrage", Help = "Die Seite mit dem Anfrageformular, z. B. „/anfrage“ oder „#anfrage“ auf derselben Seite. Die Unterkunft wird vorausgewählt." },
                new FieldSpec("button", "Text des Knopfs") { Placeholder = "Anfragen" }
            }
        }, UnitsBlockAsync);
    }

    private static string F(IPluginContext ctx, string k) => ctx.Block?.Get(k) ?? "";
    private static string Q(IPluginContext ctx, string k) { try { return ctx.Request.Q(k); } catch (Exception) { return ""; } }

    private static void Head(StringBuilder sb, string css, string heading, string intro)
    {
        sb.Append("<style>").Append(BookingCss.Css).Append("</style><section class=\"section bk ").Append(css).Append("\"><div class=\"container\">");
        if (heading.Length > 0 || intro.Length > 0)
        {
            sb.Append("<div class=\"section__intro\">");
            if (heading.Length > 0) sb.Append("<h2>").Append(B.H(heading)).Append("</h2>");
            if (intro.Length > 0) sb.Append("<p>").Append(B.H(intro)).Append("</p>");
            sb.Append("</div>");
        }
    }

    // ---- availability for the date picker -----------------------------------------------------------------------

    /// <summary>
    /// What the site's date picker (mat-datepicker.js, <c>data-dp-busy-url</c>) needs for one unit and the months on screen
    /// (<c>?einheit=&amp;von=&amp;bis=</c>, at most 400 days): the nights that are not free — widened by the unit's buffer days, so a stay the picker
    /// allows is one <see cref="BookingStore.IsFreeAsync"/> allows —, the days no arrival is allowed on, and the least stay for an arrival on each day
    /// (both from the ARRIVAL night's season, as <see cref="Pricing.Calculate"/> rules). Nights only, never who: it is drawn for every visitor.
    /// </summary>
    private static async Task<PluginResult> BusyAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var key = ctx.Request.Q("einheit").Trim();
        var units = await store.UnitsAsync(ct);
        var unit = units.FirstOrDefault(u => u.Slug == B.Slug(key));
        if (unit is null || !unit.Active) return PluginResult.Json(new JsonObject { ["ok"] = false, ["error"] = "Unbekannte Einheit." }, 404);
        var today = B.Today;
        var from = B.Day(ctx.Request.Q("von")) is { } f && f > today ? f : today;
        var to = B.Day(ctx.Request.Q("bis")) is { } t && t > from ? t : from.AddMonths(3);
        if (to.DayNumber - from.DayNumber > 400) to = from.AddDays(400);

        var buffer = Math.Max(0, unit.BufferDays);
        var nights = await store.NightsAsync(BookingStore.Occupies(unit, units), from.AddDays(-buffer), to.AddDays(buffer), ct);
        var taken = new SortedSet<DateOnly>();
        foreach (var n in nights)
            for (var k = -buffer; k <= buffer; k++)
                if (n.Day.AddDays(k) is var d && d >= from && d < to) taken.Add(d);

        var seasons = await store.SeasonsAsync(unit.PropertyId, ct);
        var rates = await store.RatesAsync(new[] { unit.Id }, ct);
        var baseMin = Math.Max(1, unit.BaseMinNights);
        var min = new JsonObject();
        var noStart = new JsonArray();
        for (var d = from; d < to; d = d.AddDays(1))
        {
            var (_, rate) = Pricing.RateOf(unit, d, seasons, rates);
            if (Math.Max(1, rate.MinNights) != baseMin) min[B.Iso(d)] = Math.Max(1, rate.MinNights);
            if (rate.ArrivalDays.Count > 0 && !rate.ArrivalDays.Contains(B.IsoDay(d.DayOfWeek))) noStart.Add(B.Iso(d));
        }
        var list = new JsonArray();
        foreach (var d in taken) list.Add(B.Iso(d));
        return PluginResult.Json(new JsonObject { ["ok"] = true, ["unit"] = unit.Slug, ["minNights"] = baseMin, ["taken"] = list, ["noStart"] = noStart, ["min"] = min });
    }

    /// <summary>The site's date-range field (mat-datepicker.js / .css, the same one the form builder's "Anreise–Abreise" field uses), asking this unit's availability.</summary>
    private static void Picker(StringBuilder sb, string name, string label, string unitSlug, string value, string placeholder)
    {
        sb.Append("<div class=\"form-field bk-dates\"><label>").Append(B.H(label)).Append("</label>")
          .Append("<div class=\"mat-dp\" data-dp-mode=\"range\" data-dp-min=\"today\" data-dp-busy-url=\"").Append(B.H(BusyPath + "?einheit=" + Uri.EscapeDataString(unitSlug))).Append("\">")
          .Append("<button type=\"button\" class=\"mat-input mat-dp-btn-trigger\" data-dp-btn>")
          .Append("<span class=\"mat-dp-display mat-dp-placeholder\" data-dp-display data-placeholder=\"").Append(B.H(placeholder)).Append("\">").Append(B.H(placeholder)).Append("</span>")
          .Append("<span class=\"mat-dp-icon\" aria-hidden=\"true\"><svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\">")
          .Append("<rect x=\"3\" y=\"5\" width=\"18\" height=\"16\" rx=\"2\"/><path d=\"M3 9h18\"/><path d=\"M8 3v4\"/><path d=\"M16 3v4\"/></svg></span></button>")
          .Append("<input type=\"hidden\" name=\"").Append(name).Append("\" value=\"").Append(B.H(value)).Append("\" data-dp-input></div></div>");
    }

    // ?v= so a browser that kept the older picker (no availability) fetches this one
    private const string PickerAssets = "<link rel=\"stylesheet\" href=\"/css/mat-datepicker.css?v=busy1\"><script src=\"/js/mat-datepicker.js?v=busy1\" defer></script>";

    // ---- the request form ---------------------------------------------------------------------------------------

    private static async Task<PluginResult> RequestBlockAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var units = (await store.UnitsAsync(ct)).Where(u => u.Active).ToList();
        var sb = new StringBuilder();
        Head(sb, "bk-request-block", F(ctx, "heading").Trim() is { Length: > 0 } h ? h : "Jetzt anfragen", F(ctx, "intro").Trim());
        if (units.Count == 0) return PluginResult.Fragment(sb.Append("<p class=\"bk-muted\">Noch keine Unterkunft eingerichtet.</p></div></section>").ToString());
        var fixedUnit = units.FirstOrDefault(u => u.Slug == F(ctx, "unit"));
        var chosen = fixedUnit ?? units.FirstOrDefault(u => u.Slug == B.Slug(Q(ctx, "einheit"))) ?? units[0];
        var fees = new List<Fee>();
        foreach (var house in units.Select(u => u.PropertyId).Distinct()) fees.AddRange((await store.FeesAsync(house, ct)).Where(f => f.Optional));
        var privacy = ctx.Settings.Get(BookingPlugin.PrivacyKey) is { Length: > 0 } pv ? pv : "/datenschutz";
        string Val(string k) => B.Day(Q(ctx, k)) is { } d ? B.Iso(d) : "";
        var today = B.Iso(B.Today);
        var range = Val("anreise") is { Length: > 0 } a && Val("abreise") is { Length: > 0 } dp && string.CompareOrdinal(dp, a) > 0 ? a + ".." + dp : "";
        static string Field(string label, string control) => "<div class=\"form-field\"><label>" + B.H(label) + "</label>" + control + "</div>";

        sb.Append(PickerAssets);
        sb.Append("<form id=\"anfrage\" class=\"bk-form\" method=\"post\" action=\"").Append(BookingPublic.RequestPath).Append("\" data-price=\"").Append(BookingPlugin.PricePath)
          .Append("\" data-busy=\"").Append(BusyPath).Append("\">");
        if (fixedUnit is not null) sb.Append("<input type=\"hidden\" name=\"einheit\" value=\"").Append(B.H(fixedUnit.Slug)).Append("\">");
        else
        {
            var opts = new StringBuilder("<select name=\"einheit\" required>");
            foreach (var u in units)
                opts.Append("<option value=\"").Append(B.H(u.Slug)).Append('"').Append(u.Id == chosen.Id ? " selected" : "").Append('>').Append(B.H(u.Name))
                    .Append(" (bis ").Append(u.GuestsMax).Append(" Personen)</option>");
            sb.Append(Field("Unterkunft", opts.Append("</select>").ToString()));
        }
        sb.Append("<div class=\"bk-row bk-row-stay\">");
        Picker(sb, "zeitraum", "Anreise – Abreise", chosen.Slug, range, "Zeitraum wählen");
        // without script the picker cannot open: two plain date fields instead (the request takes either)
        sb.Append("<noscript><div class=\"bk-row\">")
          .Append(Field("Anreise", "<input type=\"date\" name=\"anreise\" min=\"" + today + "\" value=\"" + B.H(Val("anreise")) + "\">"))
          .Append(Field("Abreise", "<input type=\"date\" name=\"abreise\" min=\"" + today + "\" value=\"" + B.H(Val("abreise")) + "\">")).Append("</div></noscript>");
        sb.Append(Field("Erwachsene", "<input type=\"number\" name=\"erwachsene\" min=\"1\" max=\"50\" value=\"2\" required>"))
          .Append(Field("Kinder", "<input type=\"number\" name=\"kinder\" min=\"0\" max=\"50\" value=\"0\">")).Append("</div>");
        foreach (var f in fees)
            sb.Append("<label class=\"bk-check\" data-house=\"").Append(f.PropertyId).Append("\" data-unit=\"").Append(f.UnitId).Append("\"><input type=\"checkbox\" name=\"extra_").Append(f.Id)
              .Append("\" value=\"1\" data-fee=\"").Append(f.Id).Append("\"> ").Append(B.H(f.Name)).Append(" (").Append(B.H(B.Money(f.Amount, BookingPlugin.Currency(ctx)))).Append(' ')
              .Append(B.H(FeePer.Label(f.Per))).Append(")</label>");
        sb.Append("<div class=\"bk-price\" aria-live=\"polite\"></div>");
        sb.Append("<div class=\"bk-row\">")
          .Append(Field("Name", "<input name=\"name\" autocomplete=\"name\" required maxlength=\"120\">"))
          .Append(Field("E-Mail", "<input type=\"email\" name=\"email\" autocomplete=\"email\" required maxlength=\"200\">"))
          .Append(Field("Telefon", "<input type=\"tel\" name=\"telefon\" autocomplete=\"tel\" maxlength=\"60\">")).Append("</div>")
          .Append(Field("Nachricht (freiwillig)", "<textarea name=\"nachricht\" rows=\"4\" maxlength=\"3000\"></textarea>"))
          .Append("<label class=\"bk-hp\" aria-hidden=\"true\">Website<input name=\"website\" tabindex=\"-1\" autocomplete=\"off\"></label>")
          .Append("<label class=\"bk-check\"><input type=\"checkbox\" name=\"consent\" value=\"1\" required> <span>Ich bin einverstanden, dass meine Angaben zur Bearbeitung der Anfrage gespeichert werden (<a href=\"")
          .Append(B.H(privacy)).Append("\">Datenschutz</a>).</span></label>")
          .Append("<p><button class=\"bk-btn\" type=\"submit\">Unverbindlich anfragen</button></p>")
          .Append("<p class=\"bk-muted\">Die Anfrage ist noch keine Buchung: wir merken den Zeitraum vor und melden uns mit einer Bestätigung.</p></form>");
        // data for the script: which house each unit belongs to (the optional fees of the chosen unit's house show)
        sb.Append("<script type=\"application/json\" class=\"bk-units-json\">{")
          .Append(string.Join(",", units.Select(u => "\"" + u.Slug + "\":[" + u.PropertyId + "," + u.Id + "]"))).Append("}</script>");
        sb.Append("<script>").Append(Script).Append("</script></div></section>");
        return PluginResult.Fragment(sb.ToString());
    }

    /// <summary>
    /// The form's script: asks the price route on every change and draws the answer with DOM text only (never innerHTML of what came back), shows the
    /// optional fees of the chosen unit's house, and lets the calendar on the same page fill the dates.
    /// </summary>
    private const string Script = """
(function(){var f=document.getElementById('anfrage');if(!f||f.dataset.bk)return;f.dataset.bk='1';
var box=f.querySelector('.bk-price'),map={};try{map=JSON.parse(f.parentNode.querySelector('.bk-units-json').textContent)}catch(e){}
function unit(){return f.elements.einheit.value}
function fees(){var m=map[unit()]||[0,0];f.querySelectorAll('[data-fee]').forEach(function(c){var l=c.closest('label');var ok=l.dataset.house==m[0]&&(l.dataset.unit=='0'||l.dataset.unit==m[1]);l.hidden=!ok;if(!ok)c.checked=false;});}
function el(t,c,x){var e=document.createElement(t);if(c)e.className=c;if(x!=null)e.textContent=x;return e}
var timer;function price(){clearTimeout(timer);timer=setTimeout(run,250)}
var pk=f.querySelector('.mat-dp');
function dates(){var z=f.elements.zeitraum&&f.elements.zeitraum.value.split('..');if(z&&z.length==2)return z;return[f.elements.anreise?f.elements.anreise.value:'',f.elements.abreise?f.elements.abreise.value:'']}
function busy(){if(pk)pk.setAttribute('data-dp-busy-url',f.dataset.busy+'?einheit='+encodeURIComponent(unit()))}
function run(){var z=dates(),a=z[0],d=z[1];box.textContent='';if(!a||!d)return;
var ex=[].map.call(f.querySelectorAll('[data-fee]:checked'),function(c){return c.dataset.fee}).join(',');
var q='?einheit='+encodeURIComponent(unit())+'&anreise='+a+'&abreise='+d+'&erwachsene='+(f.elements.erwachsene.value||1)+'&kinder='+(f.elements.kinder.value||0)+'&extras='+ex;
fetch(f.dataset.price+q,{headers:{Accept:'application/json'}}).then(function(r){return r.json()}).then(function(j){box.textContent='';
if(j.error){box.appendChild(el('p','bk-no',j.error));return}
if(!j.available)box.appendChild(el('p','bk-no','In diesem Zeitraum ist die Unterkunft leider belegt.'));
(j.problems||[]).forEach(function(p){box.appendChild(el('p','bk-no',p))});
var t=el('table','bk-bill'),b=el('tbody');(j.lines||[]).forEach(function(l){var r=el('tr'),c=el('td',null,l.text);if(l.detail)c.appendChild(el('small',null,l.detail));r.appendChild(c);r.appendChild(el('td',null,l.amountText));b.appendChild(r)});
t.appendChild(b);var ft=el('tfoot'),fr=el('tr');fr.appendChild(el('td',null,'Gesamt ('+j.nights+(j.nights==1?' Nacht)':' Nächte)')));fr.appendChild(el('td','bk-total',j.totalText));ft.appendChild(fr);t.appendChild(ft);
if((j.lines||[]).length)box.appendChild(t);f.querySelector('button[type=submit]').disabled=!j.ok;}).catch(function(){})}
f.addEventListener('input',price);f.addEventListener('change',function(e){if(e.target.name=='einheit'){fees();busy()}price()});
document.addEventListener('bk-dates',function(e){if(e.detail.unit&&f.elements.einheit.tagName=='SELECT'){f.elements.einheit.value=e.detail.unit;busy()}
if(pk&&window.MatDatepicker)MatDatepicker.setValue(pk,e.detail.from+'..'+e.detail.to);else if(f.elements.zeitraum)f.elements.zeitraum.value=e.detail.from+'..'+e.detail.to;fees();price();});
fees();price();})();
""";

    // ---- the calendar -------------------------------------------------------------------------------------------

    private static async Task<PluginResult> CalendarBlockAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var units = (await store.UnitsAsync(ct)).Where(u => u.Active).ToList();
        var unit = units.FirstOrDefault(u => u.Slug == F(ctx, "unit")) ?? units.FirstOrDefault();
        var sb = new StringBuilder();
        Head(sb, "bk-calendar-block", F(ctx, "heading").Trim() is { Length: > 0 } h ? h : unit is null ? "Belegung" : "Belegung – " + unit.Name, "");
        if (unit is null) return PluginResult.Fragment(sb.Append("<p class=\"bk-muted\">Noch keine Unterkunft eingerichtet.</p></div></section>").ToString());
        var months = int.TryParse(F(ctx, "months"), out var m) ? Math.Clamp(m, 1, 12) : 12;
        var today = B.Today;
        var first = new DateOnly(today.Year, today.Month, 1);
        var end = first.AddMonths(months);
        var taken = (await store.NightsAsync(BookingStore.Occupies(unit, units), first, end, ct)).Select(n => n.Day).ToHashSet();
        sb.Append("<div class=\"bk-cal-wrap\"><div class=\"bk-cal-nav\" hidden><button type=\"button\" class=\"bk-cal-prev\" aria-label=\"Vorheriger Monat\">‹</button>")
          .Append("<button type=\"button\" class=\"bk-cal-next\" aria-label=\"Nächster Monat\">›</button></div>");
        sb.Append("<div class=\"bk-cal\" data-unit=\"").Append(B.H(unit.Slug)).Append("\">");
        for (var mo = first; mo < end; mo = mo.AddMonths(1))
        {
            sb.Append("<div class=\"bk-month\"><h3>").Append(B.H(B.Month(mo))).Append("</h3><table><thead><tr>");
            foreach (var d in B.DayNames) sb.Append("<th>").Append(d).Append("</th>");
            sb.Append("</tr></thead><tbody><tr>");
            var lead = B.IsoDay(mo.DayOfWeek) - 1;
            for (var i = 0; i < lead; i++) sb.Append("<td class=\"bk-empty\"></td>");
            var col = lead;
            for (var d = mo; d.Month == mo.Month; d = d.AddDays(1))
            {
                if (col == 7) { sb.Append("</tr><tr>"); col = 0; }
                var css = d < today ? "bk-past" : taken.Contains(d) ? "bk-taken" : "bk-free";
                sb.Append("<td class=\"").Append(css).Append('"').Append(css == "bk-past" ? "" : " data-day=\"" + B.Iso(d) + "\"")
                  .Append(css == "bk-taken" ? " title=\"belegt\"" : "").Append('>').Append(d.Day).Append("</td>");
                col++;
            }
            while (col++ < 7) sb.Append("<td class=\"bk-empty\"></td>");
            sb.Append("</tr></tbody></table></div>");
        }
        sb.Append("</div></div><p class=\"bk-legend\"><span><i style=\"background:color-mix(in srgb,#16a34a 25%,transparent)\"></i>frei</span>")
          .Append("<span><i style=\"background:color-mix(in srgb,#dc2626 30%,transparent)\"></i>belegt</span><span>Ein Tag steht für die Nacht ab diesem Tag – zwei Klicks wählen An- und Abreise.</span></p>");
        sb.Append("<script>").Append(CalendarScript).Append("</script></div></section>");
        return PluginResult.Fragment(sb.ToString());
    }

    /// <summary>
    /// Two clicks: the first (a free day) is the arrival, the second the departure — which may itself be taken (someone arrives that day), but no taken
    /// night may lie between. Tells the request form on the page by an event, so the two blocks know nothing of each other's markup.
    /// </summary>
    private const string CalendarScript = """
(function(){document.querySelectorAll('.bk-cal:not([data-bk])').forEach(function(cal){cal.dataset.bk='1';var from=null;
var ms=[].slice.call(cal.querySelectorAll('.bk-month')),nav=cal.parentNode.querySelector('.bk-cal-nav'),at=0,mq=window.matchMedia('(max-width:640px)');
function per(){return mq.matches?1:2}
function show(){var n=per();at=Math.max(0,Math.min(at,ms.length-n));ms.forEach(function(x,i){x.hidden=i<at||i>=at+n});
if(nav){nav.hidden=ms.length<=n;nav.querySelector('.bk-cal-prev').disabled=at==0;nav.querySelector('.bk-cal-next').disabled=at>=ms.length-n}}
if(nav){nav.querySelector('.bk-cal-prev').onclick=function(){at-=per();show()};nav.querySelector('.bk-cal-next').onclick=function(){at+=per();show()}}
cal.classList.add('bk-cal-paged');show();if(mq.addEventListener)mq.addEventListener('change',show);
function clear(){cal.querySelectorAll('.bk-sel').forEach(function(c){c.classList.remove('bk-sel')})}
function start(td){clear();from=td.dataset.day;td.classList.add('bk-sel')}
cal.addEventListener('click',function(e){var td=e.target.closest('td[data-day]');if(!td)return;var day=td.dataset.day,taken=td.classList.contains('bk-taken');
if(!from||day<=from){if(taken){from=null;clear();return}start(td);return}
var blocked=[].some.call(cal.querySelectorAll('td.bk-taken[data-day]'),function(c){return c.dataset.day>=from&&c.dataset.day<day});
if(blocked){if(taken){from=null;clear()}else start(td);return}
td.classList.add('bk-sel');document.dispatchEvent(new CustomEvent('bk-dates',{detail:{unit:cal.dataset.unit,from:from,to:day}}));from=null;
var form=document.getElementById('anfrage');if(form)form.scrollIntoView({behavior:'smooth',block:'start'});});});})();
""";

    // ---- the units ----------------------------------------------------------------------------------------------

    private static async Task<PluginResult> UnitsBlockAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var units = (await store.UnitsAsync(ct)).Where(u => u.Active).ToList();
        var rates = await store.RatesAsync(units.Select(u => u.Id), ct);
        var cur = BookingPlugin.Currency(ctx);
        var sb = new StringBuilder();
        Head(sb, "bk-units-block", F(ctx, "heading").Trim() is { Length: > 0 } h ? h : "Unsere Unterkünfte", F(ctx, "intro").Trim());
        var link = F(ctx, "link").Trim() is { Length: > 0 } l && (l.StartsWith('/') || l.StartsWith('#')) && !l.StartsWith("//") ? l : "#anfrage";
        var button = F(ctx, "button").Trim() is { Length: > 0 } bt ? bt : "Anfragen";
        sb.Append("<div class=\"bk-units\">");
        foreach (var u in units)
        {
            var from = rates.Where(r => r.UnitId == u.Id && r.Price > 0).Select(r => r.Price).Append(u.BasePrice).Where(p => p > 0).DefaultIfEmpty(0).Min();
            var (path, hash) = link.Contains('#') ? (link[..link.IndexOf('#')], link[link.IndexOf('#')..]) : (link, "");
            var href = (path.Length == 0 ? "" : path) + (path.Contains('?') ? "&" : "?") + "einheit=" + Uri.EscapeDataString(u.Slug) + hash;
            sb.Append("<article class=\"bk-unit\">");
            if (u.Image.Length > 0) sb.Append("<img src=\"").Append(B.H(ctx.Site.ImageUrl(u.Image, 640))).Append("\" alt=\"").Append(B.H(u.Name)).Append("\" loading=\"lazy\">");
            sb.Append("<div class=\"bk-unit-body\"><h3>").Append(B.H(u.Name)).Append("</h3><p class=\"bk-muted\">")
              .Append(u.GuestsMax == u.GuestsStd ? u.GuestsMax + " Personen" : u.GuestsStd + "–" + u.GuestsMax + " Personen")
              .Append(u.Bedrooms > 0 ? " · " + u.Bedrooms + (u.Bedrooms == 1 ? " Schlafzimmer" : " Schlafzimmer") : "").Append("</p>");
            if (u.Description.Length > 0) sb.Append("<p>").Append(B.H(u.Description.Length > 220 ? u.Description[..220].TrimEnd() + " …" : u.Description)).Append("</p>");
            if (from > 0) sb.Append("<p class=\"bk-from\">ab ").Append(B.H(B.Money(from, cur))).Append(" / Nacht</p>");
            sb.Append("<a class=\"bk-btn\" href=\"").Append(B.H(href)).Append("\">").Append(B.H(button)).Append("</a></div></article>");
        }
        sb.Append("</div></div></section>");
        return PluginResult.Fragment(sb.ToString());
    }
}
