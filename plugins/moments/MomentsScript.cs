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
var inp=f.querySelector('input[type=file]'),tiles=f.querySelector('.mo-up-tiles'),status=f.querySelector('.mo-up-status'),done=document.querySelector('.mo-up-done'),max=+f.getAttribute('data-max')||0,vmax=+f.getAttribute('data-max-video')||0;
if(!window.FormData||!window.XMLHttpRequest||!window.Promise)return;var W={};try{W=JSON.parse(f.getAttribute('data-mo-words')||'{}');}catch(e){}function w(k,n){return String(W[k]||k).replace('{0}',n);}
var queue=[],busy=false,ok=0,bad=0,pending=false,plain=/^image\/(jpeg|png|webp)$/;
function say(){var left=queue.length+(busy?1:0);status.textContent=left?w(left===1?'left1':'leftN',left):(ok?(w(ok===1?'done1':'doneN',ok)+(pending?' '+w('pending'):'. '+w('thanks'))):'')+(bad?(' '+w(bad===1?'bad1':'badN',bad)):'');if(!left&&ok&&done)done.hidden=false;}
function viaImg(file){return new Promise(function(res,rej){var u=URL.createObjectURL(file),i=new Image();i.onload=function(){res({src:i,w:i.naturalWidth,h:i.naturalHeight,close:function(){URL.revokeObjectURL(u);}});};i.onerror=function(){URL.revokeObjectURL(u);rej(new Error('img'));};i.src=u;});}
function decode(file){if(!window.createImageBitmap)return viaImg(file);
return createImageBitmap(file,{imageOrientation:'from-image'}).catch(function(){return createImageBitmap(file);}).then(function(b){return{src:b,w:b.width,h:b.height,close:function(){if(b.close)b.close();}};}).catch(function(){return viaImg(file);});}
function scale(d,edge,q){return new Promise(function(res){var s=Math.min(1,edge/Math.max(d.w,d.h)),c=document.createElement('canvas');c.width=Math.max(1,Math.round(d.w*s));c.height=Math.max(1,Math.round(d.h*s));
var g=c.getContext('2d');g.imageSmoothingQuality='high';g.drawImage(d.src,0,0,c.width,c.height);c.toBlob(function(b){res(b?{blob:b,w:c.width,h:c.height}:null);},'image/jpeg',q);});}
function prepare(file){return decode(file).then(function(d){return scale(d,2048,0.86).then(function(v){return scale(d,640,0.8).then(function(t){d.close();if(!v||!t)throw new Error('canvas');return{view:v,thumb:t};});});});}
function fail(it,msg){bad++;it.tile.classList.add('bad');var m=document.createElement('span');m.className='mo-up-err';m.textContent=msg;it.tile.appendChild(m);busy=false;next();}
function isVideo(file){return /^video\//.test(file.type||'')||/\.(mp4|mov|m4v|webm|qt)$/i.test(file.name||'');}
function poster(file){return new Promise(function(res){var u=URL.createObjectURL(file),v=document.createElement('video'),done=false;v.muted=true;v.playsInline=true;v.preload='auto';
function end(r){if(done)return;done=true;clearTimeout(t);URL.revokeObjectURL(u);res(r);}var t=setTimeout(function(){end({dur:v.duration||0});},12000);
v.onerror=function(){end({});};v.onloadedmetadata=function(){var d=v.duration||0;try{v.currentTime=Math.min(1,d/3||0);}catch(e){end({dur:d});}};
v.onseeked=function(){var w=v.videoWidth,h=v.videoHeight,d=v.duration||0;if(!w||!h){end({dur:d});return;}var o={src:v,w:w,h:h,close:function(){}};
scale(o,2048,0.86).then(function(view){return scale(o,640,0.8).then(function(th){end({view:view,thumb:th,w:w,h:h,dur:d});});},function(){end({dur:d});});};v.src=u;});}
function post(fd,prog){return new Promise(function(res){var x=new XMLHttpRequest();x.open('POST',f.getAttribute('action'));if(prog)x.upload.onprogress=prog;
x.onload=function(){var r={};try{r=JSON.parse(x.responseText);}catch(e){r={ok:false,error:x.status===413?w('tooBig'):w('error'),retry:x.status>=500};}res(r);};x.onerror=function(){res({ok:false,error:w('offline'),retry:true});};x.send(fd);});}
function base(a){var fd=new FormData(),tok=f.querySelector('input[name=__RequestVerificationToken]');if(tok)fd.append(tok.name,tok.value);fd.append('action',a);fd.append('ajax','1');return fd;}
function sendVideo(it){if(!vmax){fail(it,w('vformat'));return;}if(it.file.size>vmax){fail(it,w('vtooBig'));return;}
poster(it.file).then(function(p){if(p.thumb){var img=new Image();img.alt='';img.src=URL.createObjectURL(p.thumb.blob);it.tile.insertBefore(img,it.tile.firstChild);}
var fd=base('vstart');fd.append('name',it.file.name||'video.mp4');fd.append('size',String(it.file.size));fd.append('modified',String(it.file.lastModified||0));
var al=f.querySelector('[data-mo-album]');if(al)fd.append('album',al.value);var tg=f.querySelector('[data-mo-tag]');if(tg)fd.append('tag',tg.value);
post(fd).then(function(r){if(!r.ok){fail(it,r.error||w('failed'));return;}var id=r.id,size=r.chunk||6291456,n=0,parts=Math.ceil(it.file.size/size),tries=0;
function piece(){if(n>=parts){finish();return;}var p2=base('vpart');p2.append('id',id);p2.append('n',String(n));p2.append('part',it.file.slice(n*size,Math.min(it.file.size,(n+1)*size)),'part');
post(p2,function(e){if(e.lengthComputable)it.bar.style.width=Math.round((n*size+e.loaded)/it.file.size*100)+'%';}).then(function(r2){if(r2.ok){n++;tries=0;piece();}
else if(r2.retry&&tries<4){tries++;setTimeout(piece,1500*tries);}else fail(it,r2.error||w('failed'));});}
function finish(){var d=base('vdone');d.append('id',id);if(p.view){d.append('view',p.view.blob,'view.jpg');d.append('thumb',p.thumb.blob,'thumb.jpg');}
if(p.w){d.append('w',String(p.w));d.append('h',String(p.h));}if(p.dur)d.append('duration',String(p.dur));
post(d).then(function(r3){if(r3.ok){ok++;it.tile.classList.add('ok');if(r3.pending)pending=true;busy=false;next();}else fail(it,r3.error||w('failed'));});}
piece();});});}
function next(){if(busy||!queue.length){say();return;}busy=true;var it=queue.shift();say();
if(isVideo(it.file)){sendVideo(it);return;}
prepare(it.file).then(function(p){send(it,p);},function(){if(plain.test(it.file.type)&&(!max||it.file.size<=max))send(it,null);else fail(it,w('format'));});}
function send(it,p){if(p){var img=new Image();img.alt='';img.src=URL.createObjectURL(p.thumb.blob);it.tile.insertBefore(img,it.tile.firstChild);}
var fd=new FormData(),tok=f.querySelector('input[name=__RequestVerificationToken]');if(tok)fd.append(tok.name,tok.value);
fd.append('action','upload');fd.append('ajax','1');var al=f.querySelector('[data-mo-album]');if(al)fd.append('album',al.value);var tg=f.querySelector('[data-mo-tag]');if(tg)fd.append('tag',tg.value);fd.append('modified',String(it.file.lastModified||0));fd.append('name',it.file.name||'');
var orig=plain.test(it.file.type)&&(!max||it.file.size<=max);if(orig)fd.append('file',it.file,it.file.name||'foto.jpg');
if(p){fd.append('view',p.view.blob,'view.jpg');fd.append('thumb',p.thumb.blob,'thumb.jpg');fd.append('w',String(p.view.w));fd.append('h',String(p.view.h));}
var x=new XMLHttpRequest();x.open('POST',f.getAttribute('action'));
x.upload.onprogress=function(e){if(e.lengthComputable)it.bar.style.width=Math.round(e.loaded/e.total*100)+'%';};
x.onload=function(){var r={};try{r=JSON.parse(x.responseText);}catch(e){r={ok:false,error:x.status===413?w('tooBig'):w('error')};}
if(r.ok){ok++;it.tile.classList.add('ok');if(r.pending)pending=true;busy=false;next();}else fail(it,r.error||w('failed'));};
x.onerror=function(){fail(it,w('offline'));};x.send(fd);}
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
document.addEventListener('keydown',function(e){var t=e.target&&e.target.tagName;if(t==='INPUT'||t==='TEXTAREA'||t==='VIDEO'||e.altKey||e.ctrlKey||e.metaKey)return;if(e.key==='ArrowLeft')go('prev');else if(e.key==='ArrowRight')go('next');});
var x0=null,y0=0;st.addEventListener('touchstart',function(e){if(e.target&&e.target.tagName==='VIDEO'){x0=null;return;}if(e.touches.length===1){x0=e.touches[0].clientX;y0=e.touches[0].clientY;}},{passive:true});
st.addEventListener('touchend',function(e){if(x0===null)return;var dx=e.changedTouches[0].clientX-x0,dy=e.changedTouches[0].clientY-y0;x0=null;if(Math.abs(dx)>50&&Math.abs(dx)>Math.abs(dy)*1.5)go(dx>0?'prev':'next');},{passive:true});
var hb=document.querySelector('.mo-heart');if(hb&&window.fetch){hb.form.addEventListener('submit',function(e){e.preventDefault();var fd=new FormData(hb.form);fd.append('action','heart');fd.append('ajax','1');
fetch(location.pathname,{method:'POST',body:fd,credentials:'same-origin'}).then(function(r){return r.json();}).then(function(r){if(!r.ok)return;hb.classList.toggle('on',r.on);hb.setAttribute('aria-pressed',r.on?'true':'false');
hb.innerHTML='<i class="ti ti-heart'+(r.on?'-filled':'')+'" aria-hidden="true"></i> ';hb.appendChild(document.createTextNode(r.count>0?String(r.count):(hb.getAttribute('data-mo-like')||'')));}).catch(function(){hb.form.submit();});});}
var ok=new URLSearchParams(location.search).get('ok'),target={markiert:'mo-tags',entfernt:'mo-tags',kommentar:'mo-comments','kommentar-pruefung':'mo-comments','kommentar-weg':'mo-comments',text:'mo-info'}[ok||''];
if(target){var el=document.getElementById(target);if(el)el.scrollIntoView({block:'start'});}
if(ok&&history.replaceState){var u=new URL(location.href);u.searchParams.delete('ok');history.replaceState(null,'',u.pathname+u.search);}
})();</script>
""";

    /// <summary>
    /// The gallery's stream: when the visitor nears the end of what is shown, the next part is fetched (<c>?…&amp;teil=1</c> answers the tiles as HTML the
    /// server built and encoded) and appended — so a gallery of hundreds of photos opens with one part and loads the rest while scrolling. The page links
    /// stay for a browser without the script; a tile's picture itself loads only when it comes near the screen (<c>loading=lazy</c>).
    /// </summary>
    public const string Gallery = """
<script>(function(){var more=document.querySelector('[data-mo-more]'),grid=document.querySelector('[data-mo-grid]');
if(!more||!grid||!window.fetch||!('IntersectionObserver' in window)){if(more)more.hidden=true;return;}
var pager=document.querySelector('.mo-pager');if(pager)pager.hidden=true;var busy=false;
function near(){var b=more.getBoundingClientRect();return b.top<window.innerHeight+900;}
function load(){var url=more.getAttribute('data-mo-more');if(busy||!url)return;busy=true;more.classList.remove('mo-more-error');
 fetch(url,{credentials:'same-origin',headers:{'Accept':'application/json'}}).then(function(r){if(!r.ok)throw new Error(r.status);return r.json();}).then(function(j){
  var t=document.createElement('template');t.innerHTML=j.html||'';grid.appendChild(t.content);busy=false;
  if(j.next){more.setAttribute('data-mo-more',j.next);if(near())requestAnimationFrame(load);}else{io.disconnect();more.remove();}
 }).catch(function(){busy=false;more.classList.add('mo-more-error');more.textContent=more.getAttribute('data-mo-error')||'';});}
var io=new IntersectionObserver(function(es){for(var i=0;i<es.length;i++)if(es[i].isIntersecting){load();break;}},{rootMargin:'900px 0px'});io.observe(more);
})();</script>
""";
}
