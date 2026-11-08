namespace MatCMS.Plugin.Moments;

/// <summary>The two small scripts of the guest area. Both only improve pages that work without them (the forms post, the links navigate).</summary>
public static class MomentsScript
{
    /// <summary>
    /// Picks → a tile at once, then per photo (one after the other, so a phone never holds forty decoded pictures): decode with the photo's own
    /// orientation, draw the 2048 px view and the 640 px tile into a canvas, show the tile, send original + view + tile with a progress bar. A photo the
    /// browser cannot decode goes as it is when the server can take it (JPEG/PNG/WebP), else the tile says why.
    /// </summary>
    public const string Upload = """
<script>(function(){var f=document.querySelector('form[data-mo-up]');if(!f||f.dataset.ready)return;f.dataset.ready='1';
var inp=f.querySelector('input[type=file]'),tiles=f.querySelector('.mo-up-tiles'),status=f.querySelector('.mo-up-status'),done=document.querySelector('.mo-up-done'),max=+f.getAttribute('data-max')||0;
if(!window.FormData||!window.XMLHttpRequest||!window.Promise)return;
var queue=[],busy=false,ok=0,bad=0,pending=false,plain=/^image\/(jpeg|png|webp)$/;
function say(){var left=queue.length+(busy?1:0);status.textContent=left?('Lädt hoch … noch '+left+(left===1?' Foto':' Fotos')):(ok?(ok+(ok===1?' Foto':' Fotos')+' hochgeladen'+(pending?' – die anderen sehen sie nach der Freigabe.':'. Danke!')):'')+(bad?(' '+bad+(bad===1?' ging':' gingen')+' nicht.'):'');if(!left&&ok&&done)done.hidden=false;}
function viaImg(file){return new Promise(function(res,rej){var u=URL.createObjectURL(file),i=new Image();i.onload=function(){res({src:i,w:i.naturalWidth,h:i.naturalHeight,close:function(){URL.revokeObjectURL(u);}});};i.onerror=function(){URL.revokeObjectURL(u);rej(new Error('img'));};i.src=u;});}
function decode(file){if(!window.createImageBitmap)return viaImg(file);
return createImageBitmap(file,{imageOrientation:'from-image'}).catch(function(){return createImageBitmap(file);}).then(function(b){return{src:b,w:b.width,h:b.height,close:function(){if(b.close)b.close();}};}).catch(function(){return viaImg(file);});}
function scale(d,edge,q){return new Promise(function(res){var s=Math.min(1,edge/Math.max(d.w,d.h)),c=document.createElement('canvas');c.width=Math.max(1,Math.round(d.w*s));c.height=Math.max(1,Math.round(d.h*s));
var g=c.getContext('2d');g.imageSmoothingQuality='high';g.drawImage(d.src,0,0,c.width,c.height);c.toBlob(function(b){res(b?{blob:b,w:c.width,h:c.height}:null);},'image/jpeg',q);});}
function prepare(file){return decode(file).then(function(d){return scale(d,2048,0.86).then(function(v){return scale(d,640,0.8).then(function(t){d.close();if(!v||!t)throw new Error('canvas');return{view:v,thumb:t};});});});}
function fail(it,msg){bad++;it.tile.classList.add('bad');var m=document.createElement('span');m.className='mo-up-err';m.textContent=msg;it.tile.appendChild(m);busy=false;next();}
function next(){if(busy||!queue.length){say();return;}busy=true;var it=queue.shift();say();
prepare(it.file).then(function(p){send(it,p);},function(){if(plain.test(it.file.type)&&(!max||it.file.size<=max))send(it,null);else fail(it,'Format geht nicht');});}
function send(it,p){if(p){var img=new Image();img.alt='';img.src=URL.createObjectURL(p.thumb.blob);it.tile.insertBefore(img,it.tile.firstChild);}
var fd=new FormData(),tok=f.querySelector('input[name=__RequestVerificationToken]');if(tok)fd.append(tok.name,tok.value);
fd.append('action','upload');fd.append('ajax','1');fd.append('modified',String(it.file.lastModified||0));fd.append('name',it.file.name||'');
var orig=plain.test(it.file.type)&&(!max||it.file.size<=max);if(orig)fd.append('file',it.file,it.file.name||'foto.jpg');
if(p){fd.append('view',p.view.blob,'view.jpg');fd.append('thumb',p.thumb.blob,'thumb.jpg');fd.append('w',String(p.view.w));fd.append('h',String(p.view.h));}
var x=new XMLHttpRequest();x.open('POST',f.getAttribute('action'));
x.upload.onprogress=function(e){if(e.lengthComputable)it.bar.style.width=Math.round(e.loaded/e.total*100)+'%';};
x.onload=function(){var r={};try{r=JSON.parse(x.responseText);}catch(e){r={ok:false,error:x.status===413?'zu groß':'Fehler beim Hochladen'};}
if(r.ok){ok++;it.tile.classList.add('ok');if(r.pending)pending=true;busy=false;next();}else fail(it,r.error||'ging nicht');};
x.onerror=function(){fail(it,'Verbindung unterbrochen');};x.send(fd);}
inp.addEventListener('change',function(){Array.prototype.forEach.call(inp.files||[],function(file){var t=document.createElement('div');t.className='mo-up-tile';var bar=document.createElement('i');bar.className='mo-up-bar';
var b=document.createElement('b');bar.appendChild(b);t.appendChild(bar);tiles.appendChild(t);queue.push({file:file,tile:t,bar:b});});inp.value='';next();});
})();</script>
""";

    /// <summary>
    /// The photo page: arrow keys and a swipe go to the previous/next photo, the heart answers in place (no reload that jumps to the top), and after an
    /// action the page scrolls back to where it happened (a redirect cannot carry a #fragment).
    /// </summary>
    public const string Stage = """
<script>(function(){var st=document.querySelector('[data-mo-stage]');if(!st||st.dataset.ready)return;st.dataset.ready='1';
function go(rel){var a=st.querySelector('a[rel='+rel+']');if(a)location.href=a.href;}
document.addEventListener('keydown',function(e){var t=e.target&&e.target.tagName;if(t==='INPUT'||t==='TEXTAREA'||e.altKey||e.ctrlKey||e.metaKey)return;if(e.key==='ArrowLeft')go('prev');else if(e.key==='ArrowRight')go('next');});
var x0=null,y0=0;st.addEventListener('touchstart',function(e){if(e.touches.length===1){x0=e.touches[0].clientX;y0=e.touches[0].clientY;}},{passive:true});
st.addEventListener('touchend',function(e){if(x0===null)return;var dx=e.changedTouches[0].clientX-x0,dy=e.changedTouches[0].clientY-y0;x0=null;if(Math.abs(dx)>50&&Math.abs(dx)>Math.abs(dy)*1.5)go(dx>0?'prev':'next');},{passive:true});
var hb=document.querySelector('.mo-heart');if(hb&&window.fetch){hb.form.addEventListener('submit',function(e){e.preventDefault();var fd=new FormData(hb.form);fd.append('action','heart');fd.append('ajax','1');
fetch(location.pathname,{method:'POST',body:fd,credentials:'same-origin'}).then(function(r){return r.json();}).then(function(r){if(!r.ok)return;hb.classList.toggle('on',r.on);hb.setAttribute('aria-pressed',r.on?'true':'false');
hb.innerHTML='<i class="ti ti-heart'+(r.on?'-filled':'')+'" aria-hidden="true"></i> '+(r.count>0?r.count:'Gefällt mir');}).catch(function(){hb.form.submit();});});}
var ok=new URLSearchParams(location.search).get('ok'),target={markiert:'mo-tags',entfernt:'mo-tags',kommentar:'mo-comments','kommentar-pruefung':'mo-comments','kommentar-weg':'mo-comments',text:'mo-info'}[ok||''];
if(target){var el=document.getElementById(target);if(el)el.scrollIntoView({block:'start'});}
if(ok&&history.replaceState){var u=new URL(location.href);u.searchParams.delete('ok');history.replaceState(null,'',u.pathname+u.search);}
})();</script>
""";
}
