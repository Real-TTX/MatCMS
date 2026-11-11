using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Moments;

/// <summary>
/// The access cards (administrator only): eight per A4 page, cut along the dashed lines — the household's name, the address of the guest area, the code
/// and a QR code of the personal link (or, when the hosts give user names and passwords, the user name and a line to write the password on). A page of its
/// own without the site's layout, so the printout is only the cards.
/// <para>Every card can also leave as a file of its own — a picture or a one-page PDF — or as a WhatsApp message, for guests who get their access on the
/// phone. All of it is made in the browser: the QR codes by the QR library both back offices already ship, the picture on a canvas, the PDF by a few lines
/// that wrap that picture (no image or PDF library on the server). The password is never on a card — it is stored as a hash and cannot be shown.</para>
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
        var mode = MomentsSettings.Mode(ctx);
        var codes = mode != MomentsSettings.ModePassword;

        var lang = (ctx.Locale ?? "").Split('-')[0] is { Length: > 0 } l ? l : "de";
        // the canvas script takes its words from the body's data-l-* attributes and each card's data-lead (the page's language)
        var sb = new StringBuilder("<!doctype html><html lang=\"").Append(M.H(lang)).Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
            .Append(ctx.T("moments.cards.title")).Append(" – ").Append(M.H(ev)).Append("</title><style>").Append(Css).Append("</style></head><body data-l-code=\"").Append(M.H(ctx.T("moments.cards.code")))
            .Append("\" data-l-user=\"").Append(M.H(ctx.T("moments.login.user"))).Append("\" data-l-pass=\"").Append(M.H(ctx.T("moments.login.password"))).Append("\" data-l-scan=\"").Append(M.H(ctx.T("moments.cards.scan")))
            .Append("\" data-l-file=\"").Append(M.H(ctx.T("moments.cards.file"))).Append("\"><div class=\"bar\"><span><strong>").Append(ctx.T("moments.cards.title")).Append("</strong> · ")
            .Append(M.Count(ctx, guests.Count, "moments.count.household.")).Append(" · ").Append(ctx.T("moments.cards.howTo"))
            .Append("</span><button type=\"button\" id=\"print\">").Append(ctx.T("moments.cards.printAll")).Append("</button></div><div class=\"cards\">");
        foreach (var g in guests)
        {
            var link = codes ? origin + MomentsPublic.Base + "/z/" + g.Code : origin + MomentsPublic.Base;
            var lead = ctx.T(codes ? "moments.cards.leadCode" : "moments.cards.leadPassword");
            sb.Append("<div class=\"slot\"><section class=\"card\" data-card data-ev=\"").Append(M.H(ev)).Append("\" data-name=\"").Append(M.H(g.Name)).Append("\" data-url=\"").Append(M.H(host + MomentsPublic.Base))
              .Append("\" data-link=\"").Append(M.H(link)).Append("\" data-code=\"").Append(codes ? M.H(g.CodeShown) : "").Append("\" data-login=\"").Append(mode != MomentsSettings.ModeCode ? M.H(g.Login) : "")
              .Append("\" data-mode=\"").Append(mode).Append("\" data-lead=\"").Append(M.H(lead)).Append("\" data-file=\"").Append(M.H(FileName(ctx, g.Name))).Append("\"><div class=\"text\"><p class=\"ev\">").Append(M.H(ev)).Append("</p><h2>").Append(M.H(g.Name))
              .Append("</h2><p class=\"lead\">").Append(lead)
              .Append("</p><p class=\"url\">").Append(M.H(host)).Append(MomentsPublic.Base).Append("</p>");
            if (codes) sb.Append("<p class=\"code\"><span>").Append(ctx.T("moments.cards.code")).Append("</span> ").Append(M.H(g.CodeShown)).Append("</p>");
            if (mode != MomentsSettings.ModeCode && (g.Login.Length > 0 || !codes))
                sb.Append("<p class=\"login\"><span>").Append(ctx.T("moments.login.user")).Append("</span> ").Append(g.Login.Length > 0 ? M.H(g.Login) : "________________").Append("</p>");
            if (!codes) sb.Append("<p class=\"login\"><span>").Append(ctx.T("moments.login.password")).Append("</span> ________________</p>");
            sb.Append("</div><div class=\"qrbox\"><div class=\"qr\" data-qr=\"").Append(M.H(link)).Append("\" role=\"img\" aria-label=\"").Append(ctx.T(codes ? "moments.cards.qrLink" : "moments.cards.qrAddress"))
              .Append("\"></div><p class=\"scan\">").Append(ctx.T("moments.cards.scan")).Append("</p></div></section><div class=\"tools\">")
              .Append("<button type=\"button\" data-png>").Append(ctx.T("moments.cards.png")).Append("</button><button type=\"button\" data-pdf>PDF</button><button type=\"button\" data-share hidden>").Append(ctx.T("moments.cards.share")).Append("</button>")
              .Append("<a href=\"").Append(M.H(WhatsAppUrl(ctx, g))).Append("\" target=\"_blank\" rel=\"noopener\">WhatsApp</a></div></div>");
        }
        if (guests.Count == 0) sb.Append("<p>").Append(ctx.T("moments.cards.none")).Append("</p>");
        sb.Append("</div><script src=\"/_content/MatCMS.Shared.Web/lib/qrcode/qrcode.min.js\"></script><script>").Append(Script).Append("</script></body></html>");
        return PluginResult.Text(sb.ToString(), "text/html; charset=utf-8").WithHeader("Cache-Control", "no-store").WithHeader("X-Robots-Tag", "noindex");
    }

    /// <summary>"Zoe & Fabian" → "Zugang-Zoe-Fabian" (a file name every phone and WhatsApp accept; the word in front is the page's language).</summary>
    internal static string FileName(IPluginContext ctx, string name)
    {
        var s = new string(name.Normalize(NormalizationForm.FormC).Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        s = s.Trim('-');
        return ctx.T("moments.cards.file") + "-" + (s.Length > 0 ? s[..Math.Min(s.Length, 40)] : ctx.T("moments.cards.fileGuest"));
    }

    /// <summary>
    /// The message for WhatsApp: the personal link (one tap signs in) and the code — or the user name with "Passwort:" left for the hosts to complete,
    /// since the password itself is not known any more. WhatsApp opens with the text in place; the hosts pick the chat and may change it before sending.
    /// </summary>
    public static string WhatsAppText(IPluginContext ctx, Guest g)
    {
        var origin = ctx.Site.Origin.TrimEnd('/');
        var mode = MomentsSettings.Mode(ctx);
        var sb = new StringBuilder(ctx.T("moments.hello", g.Name)).Append("\n\n");
        if (mode != MomentsSettings.ModePassword)
        {
            sb.Append(ctx.T("moments.wa.code", MomentsSettings.Event(ctx))).Append('\n').Append(origin).Append(MomentsPublic.Base).Append("/z/").Append(g.Code)
              .Append("\n\n").Append(ctx.T("moments.wa.tap")).Append('\n').Append(ctx.T("moments.wa.yourCode")).Append(' ').Append(g.CodeShown);
            if (mode == MomentsSettings.ModeBoth && g.Login.Length > 0) sb.Append('\n').Append(ctx.T("moments.wa.orUser")).Append(' ').Append(g.Login);
        }
        else
            sb.Append(ctx.T("moments.wa.password", MomentsSettings.Event(ctx))).Append('\n').Append(origin).Append(MomentsPublic.Base)
              .Append("\n\n").Append(ctx.T("moments.login.user")).Append(": ").Append(g.Login.Length > 0 ? g.Login : "…").Append('\n').Append(ctx.T("moments.login.password")).Append(": ");
        if (mode != MomentsSettings.ModePassword) sb.Append("\n\n").Append(MomentsSettings.Hosts(ctx));
        return sb.ToString();
    }

    public static string WhatsAppUrl(IPluginContext ctx, Guest g) => "https://wa.me/?text=" + Uri.EscapeDataString(WhatsAppText(ctx, g));

    // The picture: the card drawn again on a canvas (1400 × 1000 px, the card's 95 × 68 mm), so it reads on a phone without the cut lines; the PDF is that
    // picture as a JPEG on one page of the card's size — a PDF needs nothing more than five objects and a cross-reference table.
    private const string Script = """
(function(){
var printBtn=document.getElementById('print');if(printBtn)printBtn.addEventListener('click',function(){window.print();});
function qrOf(text){if(typeof qrcode!=='function')return null;var q=qrcode(0,'M');q.addData(text);q.make();return q;}
document.querySelectorAll('[data-qr]').forEach(function(el){var q=qrOf(el.getAttribute('data-qr'));if(q)el.innerHTML=q.createSvgTag({cellSize:4,margin:0,scalable:true});});
var FONT='"Segoe UI",system-ui,-apple-system,Roboto,Arial,sans-serif',MONO='ui-monospace,Consolas,"Courier New",monospace';
function wrap(c,text,x,y,max,lh,maxLines){var words=(text||'').split(/\s+/),lines=[],line='';
 for(var i=0;i<words.length;i++){var t=line?line+' '+words[i]:words[i];if(line&&c.measureText(t).width>max){lines.push(line);line=words[i];}else line=t;}
 if(line)lines.push(line);if(lines.length>maxLines){lines=lines.slice(0,maxLines);lines[maxLines-1]+=' …';}
 lines.forEach(function(l){c.fillText(l,x,y);y+=lh;});return y;}
function spaced(c,v){if('letterSpacing' in c)c.letterSpacing=v;}
function draw(card){var d=card.dataset,W=1400,H=1000,cv=document.createElement('canvas');cv.width=W;cv.height=H;var c=cv.getContext('2d');
 c.fillStyle='#fff';c.fillRect(0,0,W,H);c.strokeStyle='#e4d8c8';c.lineWidth=6;c.strokeRect(24,24,W-48,H-48);
 var x=96,y=d.mode==='password'||d.login?175:205,col=760;c.textBaseline='alphabetic';
 c.fillStyle='#a07d4a';c.font='600 30px '+FONT;spaced(c,'6px');c.fillText((d.ev||'').toUpperCase(),x,y);spaced(c,'0px');
 y+=92;c.fillStyle='#2b211b';c.font='600 76px '+FONT;y=wrap(c,d.name||'',x,y,col,86,2);
 y+=14;c.fillStyle='#5b4d43';c.font='32px '+FONT;
 y=wrap(c,d.lead||'',x,y,col,44,3);
 y+=12;c.fillStyle='#2b211b';c.font='600 38px '+FONT;c.fillText(d.url||'',x,y);y+=96;
 function pair(label,value,mono){c.fillStyle='#8a7d72';c.font='26px '+FONT;spaced(c,'4px');c.fillText(label.toUpperCase(),x,y);var lw=c.measureText(label.toUpperCase()).width;spaced(c,mono?'6px':'0px');
  c.fillStyle='#2b211b';c.font=(mono?'700 62px '+MONO:'600 46px '+FONT);c.fillText(value,x+lw+26,y+(mono?6:2));spaced(c,'0px');y+=mono?92:70;}
 var L=document.body.dataset;if(d.code)pair(L.lCode||'Code',d.code,true);
 if(d.login||d.mode==='password')pair(L.lUser||'',d.login||'________________',false);
 if(d.mode==='password')pair(L.lPass||'','________________',false);
 var q=qrOf(d.link||''),size=440,qx=W-96-size,qy=(H-size)/2-30;
 if(q){var n=q.getModuleCount(),m=size/n;c.fillStyle='#2b211b';for(var r=0;r<n;r++)for(var k=0;k<n;k++)if(q.isDark(r,k))c.fillRect(Math.floor(qx+k*m),Math.floor(qy+r*m),Math.ceil(m),Math.ceil(m));}
 c.fillStyle='#8a7d72';c.font='26px '+FONT;c.textAlign='center';c.fillText(L.lScan||'',qx+size/2,qy+size+52);c.textAlign='left';
 return cv;}
function blobOf(cv,type,q){return new Promise(function(res){cv.toBlob(res,type,q);});}
function save(blob,name){var a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download=name;document.body.appendChild(a);a.click();setTimeout(function(){URL.revokeObjectURL(a.href);a.remove();},1500);}
function pdf(jpeg,w,h){var enc=new TextEncoder(),parts=[],len=0,off=[];function put(x){var b=typeof x==='string'?enc.encode(x):x;parts.push(b);len+=b.length;}
 var pw=(95*72/25.4).toFixed(2),ph=(68*72/25.4).toFixed(2);put('%PDF-1.4\n');
 function obj(i,body){off[i]=len;put(i+' 0 obj\n'+body+'\nendobj\n');}
 obj(1,'<< /Type /Catalog /Pages 2 0 R >>');obj(2,'<< /Type /Pages /Kids [3 0 R] /Count 1 >>');
 obj(3,'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 '+pw+' '+ph+'] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>');
 off[4]=len;put('4 0 obj\n<< /Type /XObject /Subtype /Image /Width '+w+' /Height '+h+' /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length '+jpeg.length+' >>\nstream\n');put(jpeg);put('\nendstream\nendobj\n');
 var content='q '+pw+' 0 0 '+ph+' 0 0 cm /Im0 Do Q';obj(5,'<< /Length '+content.length+' >>\nstream\n'+content+'\nendstream');
 var xref=len,t='xref\n0 6\n0000000000 65535 f \n';for(var i=1;i<=5;i++)t+=String(off[i]).padStart(10,'0')+' 00000 n \n';
 put(t+'trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n'+xref+'\n%%EOF\n');return new Blob(parts,{type:'application/pdf'});}
document.querySelectorAll('.slot').forEach(function(slot){var card=slot.querySelector('[data-card]'),name=card.dataset.file||document.body.dataset.lFile||'card';
 slot.querySelector('[data-png]').addEventListener('click',function(){blobOf(draw(card),'image/png').then(function(b){save(b,name+'.png');});});
 slot.querySelector('[data-pdf]').addEventListener('click',function(){var cv=draw(card);blobOf(cv,'image/jpeg',0.92).then(function(b){return b.arrayBuffer();}).then(function(buf){save(pdf(new Uint8Array(buf),cv.width,cv.height),name+'.pdf');});});
 var share=slot.querySelector('[data-share]');
 if(navigator.canShare&&navigator.canShare({files:[new File([new Blob(['x'],{type:'image/png'})],'x.png',{type:'image/png'})]})){share.hidden=false;
  share.addEventListener('click',function(){blobOf(draw(card),'image/png').then(function(b){var f=new File([b],name+'.png',{type:'image/png'});return navigator.share({files:[f],title:card.dataset.name||''});}).catch(function(){});});}
});
})();
""";

    private const string Css = """
@page{size:A4;margin:10mm}
*{box-sizing:border-box}
body{margin:0;font-family:"Josefin Sans","Segoe UI",system-ui,sans-serif;color:#2b211b;background:#f4f1ec}
.bar{position:sticky;top:0;z-index:2;display:flex;gap:10px 18px;align-items:center;flex-wrap:wrap;padding:12px 18px;background:#fff;border-bottom:1px solid #ddd;font-size:14px}
.bar span{flex:1 1 320px}.bar button{margin-left:auto;font:inherit;font-weight:600;padding:8px 18px;border-radius:999px;border:0;background:#8f6747;color:#fff;cursor:pointer}
.cards{display:grid;grid-template-columns:repeat(2,95mm);justify-content:center;gap:0 14px;padding:12px 0}
.slot{break-inside:avoid;page-break-inside:avoid}
.card{width:95mm;height:68mm;background:#fff;border:1px dashed #b9b0a6;padding:6mm 5mm 5mm 7mm;display:grid;grid-template-columns:1fr 31mm;gap:4mm;align-items:center}
.ev{margin:0 0 1.5mm;font-size:8pt;letter-spacing:.18em;text-transform:uppercase;color:#a07d4a}
h2{margin:0 0 2mm;font-size:15pt;font-weight:600;line-height:1.15}
.lead{margin:0 0 2mm;font-size:8.5pt;line-height:1.35;color:#5b4d43}
.url{margin:0 0 1.5mm;font-size:9.5pt;font-weight:600;word-break:break-all}
.code{margin:0;font-family:ui-monospace,Consolas,monospace;font-size:14pt;font-weight:700;letter-spacing:.12em}
.login{margin:1mm 0 0;font-size:10pt;font-weight:600}
.code span,.login span{font-family:"Josefin Sans","Segoe UI",sans-serif;font-size:7.5pt;font-weight:400;letter-spacing:.14em;text-transform:uppercase;color:#8a7d72;vertical-align:middle;margin-right:1.5mm}
.qrbox{text-align:center}.qr{width:30mm;height:30mm;margin:0 auto}.qr svg{width:100%;height:100%;display:block}
.scan{margin:1.5mm 0 0;font-size:6.5pt;color:#8a7d72;line-height:1.2}
.tools{display:flex;gap:6px;justify-content:center;padding:6px 0 14px}
.tools button,.tools a{font:inherit;font-size:13px;font-weight:600;padding:6px 14px;border-radius:999px;border:1px solid #c9b8a5;background:#fff;color:#6b4f37;cursor:pointer;text-decoration:none}
.tools button:hover,.tools a:hover{background:#f6efe7}
@media (max-width:420px){.cards{grid-template-columns:95mm}}
@media print{body{background:#fff}.bar,.tools{display:none}.cards{padding:0;gap:0}}
""";
}
