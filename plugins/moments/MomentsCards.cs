using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Moments;

/// <summary>
/// The access cards to print (administrator only): eight per A4 page, cut along the dashed lines — the household's name, the address of the guest area,
/// the code and a QR code of the personal link. A page of its own without the site's layout, so the printout is only the cards. The QR codes are drawn in
/// the browser by the QR library both back offices already ship (no image library on the server).
/// </summary>
public static class MomentsCards
{
    public static async Task<PluginResult> PageAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (ctx.User?.IsAdmin != true) return PluginResult.NotFound();
        var store = new MomentsStore(ctx.Data);
        var only = ctx.Request.Q("ids").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(id => id > 0).ToHashSet();
        var guests = (await store.GuestsAsync(ct)).Where(g => g.Active && (only.Count == 0 || only.Contains(g.Id))).ToList();
        var origin = ctx.Site.Origin.TrimEnd('/');
        var host = Uri.TryCreate(origin, UriKind.Absolute, out var u) ? u.Host : origin;
        var ev = MomentsSettings.Event(ctx);

        var sb = new StringBuilder("<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Zugangskarten – ")
            .Append(M.H(ev)).Append("</title><style>").Append(Css).Append("</style></head><body><div class=\"bar\"><strong>Zugangskarten</strong> · ")
            .Append(M.Plural(guests.Count, "Haushalt", "Haushalte")).Append(" · acht Karten pro A4-Seite, an den gestrichelten Linien ausschneiden <button type=\"button\" id=\"print\">Drucken</button></div><div class=\"cards\">");
        foreach (var g in guests)
            sb.Append("<section class=\"card\"><div class=\"text\"><p class=\"ev\">").Append(M.H(ev)).Append("</p><h2>").Append(M.H(g.Name))
              .Append("</h2><p class=\"lead\">Euer persönlicher Zugang zu unseren Fotos – hier könnt ihr auch eure eigenen hochladen:</p><p class=\"url\">").Append(M.H(host)).Append(MomentsPublic.Base)
              .Append("</p><p class=\"code\"><span>Code</span> ").Append(M.H(g.CodeShown)).Append("</p></div><div class=\"qr\" data-qr=\"").Append(M.H(origin + MomentsPublic.Base + "/z/" + g.Code))
              .Append("\" role=\"img\" aria-label=\"QR-Code: persönlicher Link\"></div></section>");
        if (guests.Count == 0) sb.Append("<p>Noch keine Gäste angelegt.</p>");
        sb.Append("</div><script src=\"/_content/MatCMS.Shared.Web/lib/qrcode/qrcode.min.js\"></script><script>(function(){document.getElementById('print').addEventListener('click',function(){window.print();});")
          .Append("if(typeof qrcode!=='function')return;document.querySelectorAll('[data-qr]').forEach(function(el){var q=qrcode(0,'M');q.addData(el.getAttribute('data-qr'));q.make();el.innerHTML=q.createSvgTag({cellSize:4,margin:0,scalable:true});});})();</script></body></html>");
        return PluginResult.Text(sb.ToString(), "text/html; charset=utf-8").WithHeader("Cache-Control", "no-store").WithHeader("X-Robots-Tag", "noindex");
    }

    private const string Css = """
@page{size:A4;margin:10mm}
*{box-sizing:border-box}
body{margin:0;font-family:"Josefin Sans","Segoe UI",system-ui,sans-serif;color:#2b211b;background:#f4f1ec}
.bar{position:sticky;top:0;display:flex;gap:10px;align-items:center;flex-wrap:wrap;padding:12px 18px;background:#fff;border-bottom:1px solid #ddd;font-size:14px}
.bar button{margin-left:auto;font:inherit;font-weight:600;padding:8px 18px;border-radius:999px;border:0;background:#8f6747;color:#fff;cursor:pointer}
.cards{display:grid;grid-template-columns:repeat(2,95mm);justify-content:center;gap:0;padding:12px 0}
.card{width:95mm;height:68mm;background:#fff;border:1px dashed #b9b0a6;padding:7mm 6mm 6mm 7mm;display:grid;grid-template-columns:1fr 30mm;gap:5mm;align-items:center;break-inside:avoid;page-break-inside:avoid}
.ev{margin:0 0 1.5mm;font-size:8pt;letter-spacing:.18em;text-transform:uppercase;color:#a07d4a}
h2{margin:0 0 2.5mm;font-size:15pt;font-weight:600;line-height:1.15}
.lead{margin:0 0 2.5mm;font-size:8.5pt;line-height:1.35;color:#5b4d43}
.url{margin:0 0 1.5mm;font-size:9.5pt;font-weight:600;word-break:break-all}
.code{margin:0;font-family:ui-monospace,Consolas,monospace;font-size:14pt;font-weight:700;letter-spacing:.12em}
.code span{font-family:"Josefin Sans","Segoe UI",sans-serif;font-size:7.5pt;font-weight:400;letter-spacing:.14em;text-transform:uppercase;color:#8a7d72;vertical-align:middle}
.qr{width:30mm;height:30mm}.qr svg{width:100%;height:100%;display:block}
@media print{body{background:#fff}.bar{display:none}.cards{padding:0}}
""";
}
