using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Verein;

/// <summary>
/// Photos of an event, the way people actually take them: on the phone, right after the parade. A hint in the member area ("Gestern war der Umzug in
/// Brackenheim – magst du deine Bilder hochladen?") leads to <c>/mitglied/hochladen?termin=…</c>: the event at the top, ONE big button that opens the
/// phone's photos, and every chosen picture appears at once as a tile that fills while it uploads — no form to fill, no second button. The files go
/// one per request to the upload handler of <c>/mitglied/dateien</c> (the same checks as ever: type from the bytes, size, date from the photo).
/// </summary>
public static class VereinUpload
{
    /// <summary>Events that were on today or yesterday (a multi-day one while it lasts or the day after), not a draft, not cancelled, already begun — the
    /// ones somebody may have photos of right now. Not those the member said they would miss.</summary>
    public static IReadOnlyList<Event> Recent(IEnumerable<Event> events, IReadOnlyCollection<long> declined)
    {
        var now = V.Local(DateTime.UtcNow);
        var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var yesterday = now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var nowText = now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        return events.Where(e => !e.Draft && !e.Cancelled && !declined.Contains(e.Id))
            .Where(e =>
            {
                var end = e.Ends.Length >= 10 ? e.Ends[..10] : e.Day;
                var begun = e.Starts.Length < 16 ? string.CompareOrdinal(e.Day, today) <= 0 : string.CompareOrdinal(e.Starts, nowText) <= 0;
                return begun && string.CompareOrdinal(e.Day, today) <= 0 && string.CompareOrdinal(end, yesterday) >= 0;
            })
            .OrderByDescending(e => e.Starts).Take(3).ToList();
    }

    /// <summary>The friendly card: one per recent event, with what this member already uploaded for it.</summary>
    public static string Hint(Person me, IReadOnlyList<Event> recent, IReadOnlyList<Upload> uploads, IReadOnlyDictionary<long, string> myReplies)
    {
        if (recent.Count == 0) return "";
        var today = V.Local(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        foreach (var e in recent)
        {
            var when = e.Day == today ? "heute" : "gestern";
            var mine = uploads.Count(u => u.EventId == e.Id && u.MemberId == me.MemberId);
            var all = uploads.Count(u => u.EventId == e.Id && u.ForEveryone);
            var name = me.First.Length > 0 ? me.First : me.Name;
            var title = myReplies.TryGetValue(e.Id, out var a) && a == VereinStore.Yes
                ? "Hey " + name + ", du warst " + when + " dabei!"
                : "Hey " + name + ", " + when + " war „" + e.Title + "“!";
            var text = mine > 0
                ? "Danke für deine " + mine + (mine == 1 ? " Datei" : " Bilder") + " von „" + e.Title + "“. Noch mehr auf dem Handy?"
                : "Hast du Bilder von „" + e.Title + "“ gemacht? Lade sie hoch – dann haben alle was davon." + (all > 0 ? " " + all + (all == 1 ? " Bild ist" : " Bilder sind") + " schon da." : "");
            sb.Append("<section class=\"vm-hint\"><div class=\"vm-hint-icon\"><i class=\"ti ti-camera\" aria-hidden=\"true\"></i></div><div class=\"vm-hint-body\"><strong>")
              .Append(V.H(title)).Append("</strong><p>").Append(V.H(text)).Append("</p><div class=\"vm-actions\"><a class=\"vm-btn\" href=\"").Append(VereinPublic.Base)
              .Append("/hochladen?termin=").Append(e.Id).Append("\"><i class=\"ti ti-photo-up\"></i> Bilder hochladen</a>");
            if (all > 0) sb.Append("<a class=\"vm-btn ghost\" href=\"").Append(VereinPublic.Base).Append("/termine/").Append(e.Id).Append("\">Bilder ansehen</a>");
            sb.Append("</div></div></section>");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The upload panel: a big button (on a phone it opens the photo library or the camera), tiles that show each picture at once and fill while it
    /// uploads. <paramref name="fixedEvent"/> pins the event; without it the member picks one (or "automatically by the photo's date").
    /// </summary>
    public static string Panel(IPluginContext ctx, Event? fixedEvent, IReadOnlyList<Event> choices, bool board, bool materialToo)
    {
        var sb = new StringBuilder("<form method=\"post\" action=\"").Append(VereinPublic.Base).Append("/dateien\" enctype=\"multipart/form-data\" class=\"vm-up\" data-vm-up>")
            .Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"upload\"><input type=\"hidden\" name=\"title\" value=\"\">");
        if (fixedEvent is not null) sb.Append("<input type=\"hidden\" name=\"event\" value=\"").Append(fixedEvent.Id).Append("\">");
        sb.Append("<label class=\"vm-up-drop\"><input type=\"file\" name=\"file\" multiple accept=\"").Append(materialToo ? "" : "image/*").Append("\">")
          .Append("<span class=\"vm-up-big\"><i class=\"ti ti-photo-plus\" aria-hidden=\"true\"></i> ").Append(materialToo ? "Dateien auswählen" : "Fotos auswählen").Append("</span>")
          .Append("<small>").Append(materialToo ? "Fotos, PDFs, Dokumente – gern viele auf einmal" : "gern viele auf einmal – sie laden gleich hoch").Append("</small></label>");
        sb.Append("<div class=\"vm-up-tiles\" aria-live=\"polite\"></div><p class=\"vm-up-status\" role=\"status\"></p>");
        sb.Append("<details class=\"vm-up-more\"><summary>Optionen</summary>");
        if (fixedEvent is null)
        {
            sb.Append("<label class=\"vm-field\"><span>Termin</span><select name=\"event\"><option value=\"auto\">automatisch (nach dem Aufnahmedatum)</option><option value=\"0\">keinem Termin zuordnen</option>");
            foreach (var e in choices) sb.Append("<option value=\"").Append(e.Id).Append("\">").Append(V.H(e.Title + " – " + V.Date(e.Starts))).Append("</option>");
            sb.Append("</select></label>");
        }
        if (materialToo)
            sb.Append("<label class=\"vm-field\"><span>Was ist es?</span><select name=\"kind\"><option value=\"photo\">Bilder / Fotos</option><option value=\"material\">Material (PDF, Dokument …)</option></select></label>");
        else sb.Append("<input type=\"hidden\" name=\"kind\" value=\"photo\">");
        sb.Append("<label class=\"vm-check\"><input type=\"checkbox\" name=\"private\" value=\"1\"> <span>Nur für mich und den Vorstand sichtbar</span></label></details>");
        sb.Append("<p class=\"vm-muted small\">Bitte nur eigene Bilder oder solche, die du teilen darfst. Höchstens ").Append(V.Size(VereinSettings.MaxBytes(ctx))).Append(" pro Datei.");
        if (ctx.Settings.GetBool(VereinSettings.UploadApproval) && !board) sb.Append(" Die anderen sehen deine Bilder, sobald der Vorstand sie freigegeben hat.");
        sb.Append("</p><noscript><button class=\"vm-btn\" type=\"submit\">Hochladen</button></noscript></form>").Append(Script);
        return sb.ToString();
    }

    /// <summary>The page for one event (or none): the event on top, the panel, then what is already there.</summary>
    public static string Page(IPluginContext ctx, Event? e, IReadOnlyList<Event> choices, IReadOnlyList<Upload> visibleOfEvent, bool board, string doneUrl)
    {
        var sb = new StringBuilder();
        if (e is not null)
            sb.Append("<section class=\"vm-card vm-up-event\"><p class=\"vm-kicker\">").Append(V.H(e.Kind.Length > 0 ? e.Kind : "Termin")).Append("</p><h2>").Append(V.H(e.Title))
              .Append("</h2><p class=\"vm-muted\">").Append(V.H(V.When(e.Starts))).Append(e.Place.Length > 0 ? " · " + V.H(e.Place) : "").Append("</p></section>");
        sb.Append("<section class=\"vm-card\">").Append(Panel(ctx, e, choices, board, materialToo: false))
          .Append("<p class=\"vm-up-done\" hidden><a class=\"vm-btn\" href=\"").Append(V.H(doneUrl)).Append("\"><i class=\"ti ti-check\"></i> Fertig – Bilder ansehen</a></p></section>");
        if (e is not null && visibleOfEvent.Count > 0)
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-photo\"></i> Schon da (").Append(visibleOfEvent.Count).Append(")</h2><div class=\"vm-thumbs\">")
              .Append(string.Concat(visibleOfEvent.Take(18).Select(VereinPublic.ThumbHtml))).Append("</div></section>");
        return sb.ToString();
    }

    /// <summary>
    /// Picks → tiles at once (local previews), then one request per file with the tile filling up; a failed file says why on its tile, the rest go on.
    /// Pictures are NOT shrunk here: the club wants the originals (the date in the photo is read on the server).
    /// </summary>
    private const string Script = """
<script>(function(){document.querySelectorAll('form[data-vm-up]').forEach(function(f){if(f.dataset.ready)return;f.dataset.ready='1';
var inp=f.querySelector('input[type=file]'),tiles=f.querySelector('.vm-up-tiles'),status=f.querySelector('.vm-up-status'),done=f.parentNode.querySelector('.vm-up-done');
if(!window.FormData||!window.XMLHttpRequest){return;}
var queue=[],busy=false,ok=0,bad=0,pending=false;
function field(n){var el=f.elements[n];if(!el)return null;if(el.type==='checkbox')return el.checked?el.value:null;return el.value;}
function say(){var left=queue.length+(busy?1:0);status.textContent=left?('Lädt hoch … noch '+left+(left===1?' Datei':' Dateien')):(ok?(ok+(ok===1?' Datei':' Dateien')+' hochgeladen'+(pending?' – sichtbar für alle nach der Freigabe.':'. Danke!')):'')+(bad?(' '+bad+' ging'+(bad===1?'':'en')+' nicht.'):'');if(!left&&ok&&done)done.hidden=false;}
function next(){if(busy||!queue.length){say();return;}busy=true;var it=queue.shift();say();var fd=new FormData();
['__RequestVerificationToken','action','kind','event','title','private'].forEach(function(n){var v=field(n);if(v!==null&&v!==undefined)fd.append(n,v);});
fd.append('ajax','1');fd.append('file',it.file,it.file.name);var x=new XMLHttpRequest();x.open('POST',f.getAttribute('action'));
x.upload.onprogress=function(e){if(e.lengthComputable)it.bar.style.width=Math.round(e.loaded/e.total*100)+'%';};
x.onload=function(){var r={};try{r=JSON.parse(x.responseText)}catch(e){r={ok:false,error:'Fehler beim Hochladen.'}}
if(r.ok){ok++;it.tile.classList.add('ok');if(r.pending)pending=true;}else{bad++;it.tile.classList.add('bad');it.tile.title=r.error||'';var m=document.createElement('span');m.className='vm-up-err';m.textContent=r.error||'ging nicht';it.tile.appendChild(m);}
busy=false;next();};x.onerror=function(){bad++;it.tile.classList.add('bad');busy=false;next();};x.send(fd);}
inp.addEventListener('change',function(){Array.prototype.forEach.call(inp.files||[],function(file){var t=document.createElement('div');t.className='vm-up-tile';
if(/^image\//.test(file.type)){var img=new Image();img.src=URL.createObjectURL(file);img.alt='';t.appendChild(img);}else{var s=document.createElement('span');s.className='vm-up-name';s.textContent=file.name;t.appendChild(s);}
var p=document.createElement('i');p.className='vm-up-bar';var b=document.createElement('b');p.appendChild(b);t.appendChild(p);tiles.appendChild(t);queue.push({file:file,tile:t,bar:b});});inp.value='';next();});
});})();</script>
""";
}
