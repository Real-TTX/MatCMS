using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Cookie-Banner — asks visitors before anything that needs consent runs (statistics, marketing,
// external media) and only then loads it. A site without such things needs no banner at all, so the
// banner only appears once at least one optional category is switched on (or "always show" is set).
//
// Settings live in SiteSettings "plugin.cookie-consent.config" (edited on the plugin's admin page).
// The visitor's choice lives in the first-party cookie "mcc" (technically necessary: it only records
// the choice itself), format "<version>|<categories,comma>|<unix seconds>".
//
// What gets blocked until consent:
//   * code pasted per category on the admin page (injected after consent, scripts execute);
//   * <script type="text/plain" data-consent="statistik"> … </script> anywhere on the site;
//   * <iframe data-consent="medien" data-src="https://…"></iframe> (placeholder with "Inhalt laden").
// JS API for templates/plugins: MatConsent.has('statistik'), MatConsent.open(), event "matconsent".

const string SettingKey = "plugin.cookie-consent.config";
string[] Optional = { "statistik", "marketing", "medien" };

string Enc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

JsonObject Defaults() => new JsonObject
{
    ["version"] = 1, ["savedAt"] = "", ["layout"] = "box", ["theme"] = "light", ["reopen"] = true, ["showAlways"] = false,
    ["consentMode"] = false, ["months"] = 12, ["accent"] = "",
    ["title"] = "Datenschutz-Einstellungen",
    ["text"] = "Wir verwenden Cookies und ähnliche Technologien. Einige sind technisch notwendig, andere helfen uns, die Website zu verbessern oder Inhalte externer Anbieter anzuzeigen. Sie entscheiden, was Sie zulassen – Ihre Auswahl können Sie jederzeit ändern.",
    ["accept"] = "Alle akzeptieren", ["reject"] = "Nur notwendige", ["settings"] = "Einstellungen", ["save"] = "Auswahl speichern",
    ["reopenLabel"] = "Cookie-Einstellungen",
    ["privacyUrl"] = "/datenschutz", ["privacyLabel"] = "Datenschutz", ["imprintUrl"] = "/impressum", ["imprintLabel"] = "Impressum",
    ["necessaryLabel"] = "Notwendig",
    ["necessaryDesc"] = "Für den Betrieb der Website erforderlich, z. B. Anmeldung, Spracheinstellung und das Speichern Ihrer Cookie-Auswahl. Immer aktiv.",
    ["cats"] = new JsonObject
    {
        ["statistik"] = new JsonObject { ["on"] = false, ["label"] = "Statistik", ["desc"] = "Hilft uns zu verstehen, wie Besucher die Website nutzen (z. B. Google Analytics, Matomo).", ["code"] = "" },
        ["marketing"] = new JsonObject { ["on"] = false, ["label"] = "Marketing", ["desc"] = "Macht Werbung relevanter und misst ihren Erfolg (z. B. Meta Pixel, Google Ads).", ["code"] = "" },
        ["medien"] = new JsonObject { ["on"] = false, ["label"] = "Externe Medien", ["desc"] = "Inhalte von Videoplattformen, Karten und sozialen Netzwerken (z. B. YouTube, Google Maps, Instagram).", ["code"] = "" },
    }
};

// Stored settings over the defaults, key by key: an older save that lacks a newer key still works.
JsonObject Load(AppDbContext db)
{
    var cfg = Defaults();
    var row = db.SiteSettings.FirstOrDefault(x => x.Key == SettingKey);
    if (row == null || string.IsNullOrWhiteSpace(row.Value)) return cfg;
    try
    {
        if (JsonNode.Parse(row.Value) is not JsonObject saved) return cfg;
        foreach (var kv in saved.ToList())
        {
            if (kv.Key == "cats" && kv.Value is JsonObject sc)
            {
                foreach (var c in sc.ToList())
                    if (c.Value is JsonObject co && cfg["cats"]![c.Key] is JsonObject dc)
                        foreach (var f in co.ToList()) dc[f.Key] = f.Value?.DeepClone();
            }
            else cfg[kv.Key] = kv.Value?.DeepClone();
        }
    }
    catch { }
    return cfg;
}
void Save(AppDbContext db, JsonObject cfg)
{
    var row = db.SiteSettings.FirstOrDefault(x => x.Key == SettingKey);
    if (row == null) db.SiteSettings.Add(new SiteSetting { Key = SettingKey, Value = cfg.ToJsonString() });
    else row.Value = cfg.ToJsonString();
    db.SaveChanges();
}
string S(JsonNode n, string k) { try { return n?[k]?.GetValue<string>() ?? ""; } catch { return n?[k]?.ToString() ?? ""; } }
bool B(JsonNode n, string k) { try { return n?[k]?.GetValue<bool>() ?? false; } catch { return S(n, k) == "true"; } }
int I(JsonNode n, string k, int d) { try { return n?[k]?.GetValue<int>() ?? d; } catch { return int.TryParse(S(n, k), out var v) ? v : d; } }
JsonNode Cat(JsonObject cfg, string c) => cfg["cats"]?[c] ?? new JsonObject();
// Only relative paths or http(s) URLs leave the admin form as link targets.
string SafeUrl(string u)
{
    u = (u ?? "").Trim();
    if (u.Length == 0) return "";
    if (u.StartsWith("/") && !u.StartsWith("//")) return u;
    return Uri.TryCreate(u, UriKind.Absolute, out var x) && (x.Scheme == "http" || x.Scheme == "https") ? x.ToString() : "";
}
string SafeColor(string c) => System.Text.RegularExpressions.Regex.IsMatch((c ?? "").Trim(), "^#[0-9a-fA-F]{3,8}$") ? c.Trim() : "";
bool Active(JsonObject cfg) => B(cfg, "showAlways") || Optional.Any(c => B(Cat(cfg, c), "on"));

// ------------------------------------------------------------------ the public side
const string Css = """
.mcc,.mcc *{box-sizing:border-box}
.mcc{--mcc-accent:var(--accent,#2f6f5e);--mcc-bg:#fff;--mcc-fg:#1d2329;--mcc-muted:#5b6670;--mcc-line:rgba(0,0,0,.12);--mcc-soft:rgba(0,0,0,.04);font-family:var(--font-body,system-ui,-apple-system,"Segoe UI",Roboto,sans-serif);font-size:15px;line-height:1.55;color:var(--mcc-fg);position:fixed;z-index:2147483000;display:none}
.mcc[data-theme=dark]{--mcc-bg:#17141b;--mcc-fg:#f1edf3;--mcc-muted:#b3aab8;--mcc-line:rgba(255,255,255,.14);--mcc-soft:rgba(255,255,255,.05)}
.mcc.is-open{display:block}
.mcc__panel{background:var(--mcc-bg);border:1px solid var(--mcc-line);box-shadow:0 18px 60px rgba(0,0,0,.28);border-radius:16px;padding:22px 22px 18px;max-height:calc(100vh - 32px);overflow:auto}
.mcc--box{left:16px;bottom:16px;width:min(440px,calc(100vw - 32px))}
.mcc--bar{left:0;right:0;bottom:0}
.mcc--bar .mcc__panel{border-radius:16px 16px 0 0;border-bottom:0;max-width:1180px;margin:0 auto}
.mcc--modal{inset:0;background:rgba(10,10,14,.55);-webkit-backdrop-filter:blur(3px);backdrop-filter:blur(3px)}
.mcc--modal.is-open{display:flex;align-items:center;justify-content:center;padding:16px}
.mcc--modal .mcc__panel{width:min(560px,100%)}
.mcc__title{margin:0 0 8px;font-size:18px;font-weight:700;line-height:1.3;color:var(--mcc-fg);font-family:inherit;letter-spacing:0;text-transform:none}
.mcc__text{margin:0 0 14px;color:var(--mcc-muted)}
.mcc__links{display:flex;flex-wrap:wrap;gap:4px 16px;margin:0 0 16px;font-size:13.5px}
.mcc__links a{color:var(--mcc-fg);text-decoration:underline;text-underline-offset:3px}
.mcc__btns{display:flex;flex-wrap:wrap;gap:10px;align-items:center}
.mcc__btn{appearance:none;font:inherit;font-weight:600;font-size:14.5px;cursor:pointer;border-radius:10px;padding:11px 18px;border:1.5px solid var(--mcc-accent);background:var(--mcc-accent);color:var(--mcc-on,#fff);flex:1 1 150px;text-align:center;line-height:1.2;transition:filter .15s}
.mcc__btn:hover{filter:brightness(1.08)}
.mcc__btn--link{flex:0 0 auto;background:none;border-color:transparent;color:var(--mcc-fg);text-decoration:underline;text-underline-offset:3px;padding:11px 6px}
.mcc__btn:focus-visible,.mcc__sw input:focus-visible+span,.mcc-reopen:focus-visible{outline:3px solid var(--mcc-accent);outline-offset:2px}
.mcc__cats{display:none;margin:0 0 16px;border:1px solid var(--mcc-line);border-radius:12px;overflow:hidden}
.mcc.is-detail .mcc__cats{display:block}
.mcc.is-detail [data-mcc=settings]{display:none}
.mcc:not(.is-detail) [data-mcc=save]{display:none}
.mcc__cat{display:flex;gap:14px;align-items:flex-start;padding:12px 14px;border-top:1px solid var(--mcc-line)}
.mcc__cat:first-child{border-top:0}
.mcc__cat b{display:block;font-size:14.5px}
.mcc__cat small{display:block;color:var(--mcc-muted);font-size:13px;line-height:1.45;margin-top:2px}
.mcc__cat>div{flex:1}
.mcc__sw{position:relative;flex:none;width:42px;height:24px;margin-top:2px}
.mcc__sw input{position:absolute;opacity:0;width:100%;height:100%;margin:0;cursor:pointer}
.mcc__sw span{position:absolute;inset:0;border-radius:99px;background:var(--mcc-line);transition:background .2s;pointer-events:none}
.mcc__sw span::after{content:"";position:absolute;left:3px;top:3px;width:18px;height:18px;border-radius:50%;background:#fff;box-shadow:0 1px 3px rgba(0,0,0,.3);transition:transform .2s}
.mcc__sw input:checked+span{background:var(--mcc-accent)}
.mcc__sw input:checked+span::after{transform:translateX(18px)}
.mcc__sw input:disabled{cursor:not-allowed}
.mcc__sw input:disabled+span{opacity:.6}
.mcc-reopen{position:fixed;left:16px;bottom:16px;z-index:2147482999;width:44px;height:44px;border-radius:50%;border:1px solid rgba(0,0,0,.15);background:#fff;color:#3a3a3a;display:none;align-items:center;justify-content:center;cursor:pointer;box-shadow:0 6px 20px rgba(0,0,0,.2);padding:0}
.mcc-reopen.is-on{display:flex}
.mcc-reopen svg{width:24px;height:24px}
.mcc-ph{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:10px;text-align:center;padding:24px;background:repeating-linear-gradient(135deg,rgba(0,0,0,.035) 0 12px,rgba(0,0,0,.06) 12px 24px),#f3f3f3;color:#333;border:1px solid rgba(0,0,0,.1);border-radius:10px;font:14px/1.5 var(--font-body,system-ui,sans-serif);min-height:200px;width:100%}
.mcc-ph p{margin:0;max-width:440px}
.mcc-ph div{display:flex;flex-wrap:wrap;gap:8px;justify-content:center}
.mcc-ph button{font:inherit;font-weight:600;cursor:pointer;border-radius:8px;padding:9px 14px;border:1.5px solid var(--accent,#2f6f5e);background:var(--accent,#2f6f5e);color:var(--mcc-on,#fff)}
.mcc-ph button+button{background:#fff;color:#222;border-color:rgba(0,0,0,.25)}
@media (max-width:560px){.mcc--box{left:8px;right:8px;bottom:8px;width:auto}.mcc__panel{padding:18px 16px 14px}.mcc__btn{flex-basis:100%}.mcc-reopen{left:10px;bottom:10px}}
@media print{.mcc,.mcc-reopen{display:none!important}}
""";

const string Js = """
(function () {
  var C = window.__mcc; if (!C || window.MatConsent) return;
  var NAME = 'mcc', root = document.getElementById('mcc'), reopen = document.getElementById('mcc-reopen');
  function read() {
    var m = document.cookie.match(/(?:^|;\s*)mcc=([^;]*)/); if (!m) return null;
    var p = decodeURIComponent(m[1]).split('|');
    if (+p[0] !== C.version) return null;
    return p[1] ? p[1].split(',').filter(Boolean) : [];
  }
  function write(cats) {
    var v = C.version + '|' + cats.join(',') + '|' + Math.floor(Date.now() / 1000);
    document.cookie = NAME + '=' + encodeURIComponent(v) + ';path=/;max-age=' + (C.months * 2592000) + ';SameSite=Lax' + (location.protocol === 'https:' ? ';Secure' : '');
  }
  var granted = read(), loaded = {};
  function has(c) { return c === 'notwendig' || !!(granted && granted.indexOf(c) >= 0); }
  function gmode() {
    if (!C.consentMode || typeof window.gtag !== 'function') return;
    var s = function (b) { return b ? 'granted' : 'denied'; };
    gtag('consent', 'update', { analytics_storage: s(has('statistik')), ad_storage: s(has('marketing')), ad_user_data: s(has('marketing')), ad_personalization: s(has('marketing')) });
  }
  function inject(html) {
    var box = document.createElement('div'); box.hidden = true; document.body.appendChild(box);
    box.appendChild(document.createRange().createContextualFragment(html));
  }
  function activate() {
    C.cats.forEach(function (c) {
      if (!has(c.id) || loaded[c.id]) return; loaded[c.id] = 1;
      if (c.code) inject(c.code);
      document.querySelectorAll('script[type="text/plain"][data-consent="' + c.id + '"]').forEach(function (old) {
        var s = document.createElement('script');
        for (var i = 0; i < old.attributes.length; i++) { var a = old.attributes[i]; if (a.name !== 'type' && a.name !== 'data-consent') s.setAttribute(a.name, a.value); }
        if (!old.src) s.textContent = old.textContent; old.parentNode.replaceChild(s, old);
      });
      document.querySelectorAll('[data-consent="' + c.id + '"][data-src]').forEach(load);
    });
    gmode();
    document.dispatchEvent(new CustomEvent('matconsent', { detail: { categories: (granted || []).slice() } }));
  }
  function load(el) {
    if (el.__mccPh) { el.__mccPh.remove(); el.__mccPh = null; }
    el.style.display = ''; if (el.getAttribute('data-src')) { el.setAttribute('src', el.getAttribute('data-src')); el.removeAttribute('data-src'); }
  }
  function host(u) { try { return new URL(u, location.href).hostname.replace(/^www\./, ''); } catch (e) { return ''; } }
  function placeholders() {
    document.querySelectorAll('[data-consent][data-src]').forEach(function (el) {
      var id = el.getAttribute('data-consent'); if (has(id) || el.__mccPh) return;
      var cat = C.cats.filter(function (c) { return c.id === id; })[0] || { label: id };
      var ph = document.createElement('div'); ph.className = 'mcc-ph';
      var h = el.getAttribute('height'); if (h && /^\d+$/.test(h)) ph.style.minHeight = h + 'px';
      ph.innerHTML = '<p></p><div><button type="button" data-a="once"></button><button type="button" data-a="always"></button></div>';
      ph.querySelector('p').textContent = C.phText.replace('{host}', host(el.getAttribute('data-src')) || 'einem externen Anbieter');
      ph.querySelector('[data-a=once]').textContent = C.phOnce;
      ph.querySelector('[data-a=always]').textContent = C.phAlways.replace('{cat}', cat.label);
      ph.addEventListener('click', function (e) {
        var a = e.target.getAttribute && e.target.getAttribute('data-a'); if (!a) return;
        if (a === 'once') load(el); else { decide((granted || []).concat([id])); }
      });
      el.style.display = 'none'; el.parentNode.insertBefore(ph, el); el.__mccPh = ph;
    });
  }
  function decide(cats) {
    granted = cats.filter(function (c, i) { return cats.indexOf(c) === i && C.cats.some(function (x) { return x.id === c; }); });
    write(granted); close(); activate(); placeholders();
  }
  var lastFocus = null;
  function open(detail) {
    if (!root) return; lastFocus = document.activeElement;
    root.classList.toggle('is-detail', !!detail);
    root.querySelectorAll('input[data-cat]').forEach(function (i) { i.checked = has(i.getAttribute('data-cat')); });
    root.classList.add('is-open'); if (reopen) reopen.classList.remove('is-on');
    // Focus moves into the banner only when the visitor opened it; on page load it would just look odd.
    if (detail) { var f = root.querySelector('input[data-cat]') || root.querySelector('.mcc__btn'); if (f) f.focus({ preventScroll: true }); }
  }
  function close() {
    if (!root) return; root.classList.remove('is-open');
    if (reopen && C.reopen) reopen.classList.add('is-on');
    if (lastFocus && lastFocus.focus) try { lastFocus.focus({ preventScroll: true }); } catch (e) { }
  }
  if (root) root.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('[data-mcc]'); if (!b) return;
    var a = b.getAttribute('data-mcc');
    if (a === 'accept') decide(C.cats.map(function (c) { return c.id; }));
    else if (a === 'reject') decide([]);
    else if (a === 'settings') { root.classList.add('is-detail'); var t = root.querySelector('input[data-cat]'); if (t) t.focus(); }
    else if (a === 'save') decide([].slice.call(root.querySelectorAll('input[data-cat]:checked')).map(function (i) { return i.getAttribute('data-cat'); }));
  });
  if (reopen) reopen.addEventListener('click', function () { open(true); });
  document.addEventListener('click', function (e) {
    var t = e.target.closest && e.target.closest('[data-cookie-settings],a[href="#cookie-einstellungen"]');
    if (t) { e.preventDefault(); open(true); }
  });
  window.MatConsent = { has: has, open: function () { open(true); }, reset: function () { granted = null; document.cookie = NAME + '=;path=/;max-age=0'; open(false); }, categories: function () { return (granted || []).slice(); } };
  // White text on a light accent (gold, pastel) is unreadable: pick dark text when the accent is bright.
  function contrast() {
    var probe = root && root.querySelector('.mcc__btn'); if (!probe) return;
    var m = getComputedStyle(probe).backgroundColor.match(/\d+(\.\d+)?/g); if (!m) return;
    var l = function (v) { v /= 255; return v <= .03928 ? v / 12.92 : Math.pow((v + .055) / 1.055, 2.4); };
    var lum = .2126 * l(+m[0]) + .7152 * l(+m[1]) + .0722 * l(+m[2]);
    if (lum > .4) document.documentElement.style.setProperty('--mcc-on', '#1b1b1b');
  }
  function start() {
    contrast();
    placeholders();
    if (granted) { activate(); if (reopen && C.reopen) reopen.classList.add('is-on'); }
    else open(false);
    if (/[?&]cookie-vorschau\b/.test(location.search) || location.hash === '#cookie-einstellungen') open(true);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
""";

var db0 = Service<AppDbContext>();
var cfg0 = db0 != null ? Load(db0) : Defaults();
if (Active(cfg0))
{
    var cats = Optional.Where(c => B(Cat(cfg0, c), "on")).Select(c => new { id = c, label = S(Cat(cfg0, c), "label"), code = S(Cat(cfg0, c), "code") }).ToList();
    var client = new
    {
        version = I(cfg0, "version", 1), months = Math.Clamp(I(cfg0, "months", 12), 1, 24), reopen = B(cfg0, "reopen"), consentMode = B(cfg0, "consentMode"),
        cats,
        phText = "Dieser Inhalt wird von {host} geladen. Dabei werden Daten an den Anbieter übertragen.",
        phOnce = "Inhalt laden", phAlways = "{cat} immer erlauben"
    };
    var accent = SafeColor(S(cfg0, "accent"));
    AddHeadHtml("<style>" + Css + (accent.Length > 0 ? ".mcc,.mcc-ph{--mcc-accent:" + accent + ";--accent:" + accent + "}" : "") + "</style>");
    if (B(cfg0, "consentMode"))
        // Before any Google tag: everything denied until the visitor decides; a stored choice is applied at once.
        AddHeadHtml("<script>window.dataLayer=window.dataLayer||[];function gtag(){dataLayer.push(arguments);}gtag('consent','default',{ad_storage:'denied',ad_user_data:'denied',ad_personalization:'denied',analytics_storage:'denied',wait_for_update:500});" +
            "(function(){var m=document.cookie.match(/(?:^|;\\s*)mcc=([^;]*)/);if(!m)return;var p=decodeURIComponent(m[1]).split('|');if(+p[0]!==" + I(cfg0, "version", 1) + ")return;var c=(p[1]||'').split(','),g=function(x){return c.indexOf(x)>=0?'granted':'denied'};" +
            "gtag('consent','update',{analytics_storage:g('statistik'),ad_storage:g('marketing'),ad_user_data:g('marketing'),ad_personalization:g('marketing')});})();</script>");

    var layout = S(cfg0, "layout") is "bar" or "modal" ? S(cfg0, "layout") : "box";
    var sb = new StringBuilder();
    sb.Append("<div id='mcc' class='mcc mcc--" + layout + "' data-theme='" + (S(cfg0, "theme") == "dark" ? "dark" : "light") + "' role='dialog' aria-modal='" + (layout == "modal" ? "true" : "false") + "' aria-labelledby='mcc-title' aria-describedby='mcc-text'><div class='mcc__panel'>");
    sb.Append("<p class='mcc__title' id='mcc-title'>" + Enc(S(cfg0, "title")) + "</p><p class='mcc__text' id='mcc-text'>" + Enc(S(cfg0, "text")) + "</p>");
    sb.Append("<div class='mcc__cats'>");
    sb.Append("<label class='mcc__cat'><span class='mcc__sw'><input type='checkbox' checked disabled><span></span></span><div><b>" + Enc(S(cfg0, "necessaryLabel")) + "</b><small>" + Enc(S(cfg0, "necessaryDesc")) + "</small></div></label>");
    foreach (var c in cats)
        sb.Append("<label class='mcc__cat'><span class='mcc__sw'><input type='checkbox' data-cat='" + c.id + "'><span></span></span><div><b>" + Enc(c.label) + "</b><small>" + Enc(S(Cat(cfg0, c.id), "desc")) + "</small></div></label>");
    sb.Append("</div>");
    var links = new StringBuilder();
    var pu = SafeUrl(S(cfg0, "privacyUrl")); if (pu.Length > 0) links.Append("<a href='" + Enc(pu) + "'>" + Enc(S(cfg0, "privacyLabel")) + "</a>");
    var iu = SafeUrl(S(cfg0, "imprintUrl")); if (iu.Length > 0) links.Append("<a href='" + Enc(iu) + "'>" + Enc(S(cfg0, "imprintLabel")) + "</a>");
    if (links.Length > 0) sb.Append("<div class='mcc__links'>" + links + "</div>");
    // Accept and reject side by side and alike: refusing must be as easy as agreeing.
    sb.Append("<div class='mcc__btns'><button type='button' class='mcc__btn' data-mcc='reject'>" + Enc(S(cfg0, "reject")) + "</button>" +
        "<button type='button' class='mcc__btn' data-mcc='accept'>" + Enc(S(cfg0, "accept")) + "</button>" +
        (cats.Count > 0 ? "<button type='button' class='mcc__btn mcc__btn--link' data-mcc='settings'>" + Enc(S(cfg0, "settings")) + "</button>" +
            "<button type='button' class='mcc__btn mcc__btn--link' data-mcc='save'>" + Enc(S(cfg0, "save")) + "</button>" : "") + "</div>");
    sb.Append("</div></div>");
    sb.Append("<button type='button' id='mcc-reopen' class='mcc-reopen' aria-label='" + Enc(S(cfg0, "reopenLabel")) + "' title='" + Enc(S(cfg0, "reopenLabel")) + "'>" +
        "<svg viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round' aria-hidden='true'><path d='M8 13v.01'/><path d='M12 17v.01'/><path d='M12 12v.01'/><path d='M16 14v.01'/><path d='M11 8v.01'/><path d='M13.148 3.476l2.667 1.104a4 4 0 0 0 4.656 6.14l.053 .132a3 3 0 0 1 0 2.296q -.745 1.18 -1.024 1.852q -.283 .684 -.66 2.216a3 3 0 0 1 -1.624 1.623q -1.572 .394 -2.216 .661q -.712 .295 -1.852 1.024a3 3 0 0 1 -2.296 0q -1.115 -.729 -1.852 -1.024q -.735 -.305 -2.216 -.66a3 3 0 0 1 -1.623 -1.624q -.39 -1.542 -.661 -2.216q -.271 -.676 -1.024 -1.852a3 3 0 0 1 0 -2.296q .73 -1.11 1.024 -1.852q .29 -.73 .66 -2.216a3 3 0 0 1 1.624 -1.623q 1.501 -.364 2.216 -.661q .647 -.268 1.852 -1.024a3 3 0 0 1 2.296 0'/></svg></button>");
    // System.Text.Json escapes < > & by default, so pasted code cannot close this <script> early.
    sb.Append("<script>window.__mcc=" + JsonSerializer.Serialize(client) + ";</script><script>" + Js + "</script>");
    AddBodyHtml(sb.ToString());
}

// ------------------------------------------------------------------ admin
AddAdminMenu("Cookie-Banner", "/admin/plugin/cookie-consent", "ti-cookie");
AddAdminPage("cookie-consent", req =>
{
    var db = req.Service<AppDbContext>();
    if (db == null) return req.Ui.Alert("Keine Datenbank.", "error");
    var cfg = Load(db);

    if (req.IsPost)
    {
        if (req.Action == "save")
        {
            foreach (var k in new[] { "title", "text", "accept", "reject", "settings", "save", "reopenLabel", "privacyLabel", "imprintLabel", "necessaryLabel", "necessaryDesc" })
                cfg[k] = (req.F(k) ?? "").Trim();
            cfg["privacyUrl"] = SafeUrl(req.F("privacyUrl"));
            cfg["imprintUrl"] = SafeUrl(req.F("imprintUrl"));
            cfg["accent"] = SafeColor(req.F("accent"));
            cfg["layout"] = req.F("layout") is "bar" or "modal" ? req.F("layout") : "box";
            cfg["theme"] = req.F("theme") == "dark" ? "dark" : "light";
            cfg["months"] = int.TryParse(req.F("months"), out var mo) ? Math.Clamp(mo, 1, 24) : 12;
            cfg["reopen"] = req.F("reopen") == "on";
            cfg["showAlways"] = req.F("showAlways") == "on";
            cfg["consentMode"] = req.F("consentMode") == "on";
            foreach (var c in Optional)
            {
                var co = (JsonObject)cfg["cats"]![c]!;
                co["on"] = req.F("on_" + c) == "on";
                co["label"] = (req.F("label_" + c) ?? "").Trim();
                co["desc"] = (req.F("desc_" + c) ?? "").Trim();
                co["code"] = req.F("code_" + c) ?? "";
            }
            cfg["savedAt"] = DateTime.UtcNow.ToString("o");
            Save(db, cfg);
            req.Log("Cookie-Banner gespeichert.");
        }
        else if (req.Action == "reset")
        {
            var fresh = Defaults();
            fresh["version"] = I(cfg, "version", 1);
            fresh["savedAt"] = DateTime.UtcNow.ToString("o");
            Save(db, fresh);
            req.Log("Cookie-Banner auf Standard zurückgesetzt.");
        }
        else if (req.Action == "reask")
        {
            cfg["version"] = I(cfg, "version", 1) + 1;
            cfg["savedAt"] = DateTime.UtcNow.ToString("o");
            Save(db, cfg);
            req.Log("Cookie-Banner: alle Besucher werden erneut gefragt (Version " + I(cfg, "version", 1) + ").");
        }
        // The banner is built when plugins run, so a saved change only reaches the site after a re-run.
        req.Service<PluginRunner>()?.RunAllAsync().GetAwaiter().GetResult();
        return "";
    }

    string Txt(string name, string label, string help = "") =>
        "<div class='form-field'><label>" + Enc(label) + "</label><input name='" + name + "' value='" + Enc(S(cfg, name)) + "'>" + (help.Length > 0 ? "<div class='help'>" + Enc(help) + "</div>" : "") + "</div>";
    string Area(string name, string label, string value, int rows, string help = "", bool mono = false) =>
        "<div class='form-field'><label>" + Enc(label) + "</label><textarea name='" + name + "' rows='" + rows + "'" + (mono ? " spellcheck='false' style='font-family:ui-monospace,Consolas,monospace;font-size:13px'" : "") + ">" + Enc(value) + "</textarea>" + (help.Length > 0 ? "<div class='help'>" + Enc(help) + "</div>" : "") + "</div>";
    string Check(string name, bool on, string label, string help = "") =>
        "<div class='form-field'><label style='display:flex;gap:8px;align-items:center;font-weight:600'><input type='checkbox' name='" + name + "'" + (on ? " checked" : "") + "> " + Enc(label) + "</label>" + (help.Length > 0 ? "<div class='help'>" + Enc(help) + "</div>" : "") + "</div>";
    string Sel(string name, string label, (string v, string l)[] opts) =>
        "<div class='form-field'><label>" + Enc(label) + "</label><select name='" + name + "'>" + string.Join("", opts.Select(o => "<option value='" + o.v + "'" + (S(cfg, name) == o.v ? " selected" : "") + ">" + Enc(o.l) + "</option>")) + "</select></div>";

    var sb = new StringBuilder();
    sb.Append(req.Ui.PageHead("Fragt Besucher, bevor Statistik, Marketing oder externe Medien geladen werden – und lädt sie erst danach."));
    var on = Optional.Where(c => B(Cat(cfg, c), "on")).Select(c => S(Cat(cfg, c), "label")).ToList();
    var saved = DateTime.TryParse(S(cfg, "savedAt"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var sd) ? " Zuletzt gespeichert: " + sd.ToLocalTime().ToString("dd.MM.yyyy HH:mm") + "." : "";
    sb.Append(Active(cfg)
        ? req.Ui.Alert("Der Banner ist aktiv" + (on.Count > 0 ? " – Kategorien: " + string.Join(", ", on) : " (Hinweis ohne optionale Kategorien)") + ". Version " + I(cfg, "version", 1) + "." + saved, "success")
        : req.Ui.Alert("Der Banner ist ausgeblendet: Keine optionale Kategorie ist eingeschaltet. Eine Seite, die nur technisch notwendige Cookies setzt (so wie MatCMS selbst), braucht keinen Banner." + saved, "info"));

    var cats = new StringBuilder();
    foreach (var c in Optional)
    {
        var n = Cat(cfg, c);
        cats.Append("<div style='border:1px solid var(--line);border-radius:10px;padding:14px 16px;margin:0 0 12px'>");
        cats.Append(Check("on_" + c, B(n, "on"), "„" + S(n, "label") + "“ einschalten"));
        cats.Append("<div class='form-grid'>" +
            "<div class='form-field'><label>Bezeichnung</label><input name='label_" + c + "' value='" + Enc(S(n, "label")) + "'></div>" +
            "<div class='form-field'><label>Beschreibung</label><input name='desc_" + c + "' value='" + Enc(S(n, "desc")) + "'></div></div>");
        cats.Append(Area("code_" + c, "Code, der erst nach Zustimmung geladen wird (optional)", S(n, "code"), 4,
            c == "medien" ? "Für eingebettete Videos/Karten meist leer lassen und stattdessen im HTML-Block data-consent nutzen (siehe unten)." : "z. B. das Google-Analytics- oder Matomo-Snippet. <script>-Tags werden nach der Zustimmung ausgeführt.", true));
        cats.Append("</div>");
    }

    var form = new StringBuilder();
    form.Append("<h3 style='margin:0 0 10px'>Kategorien</h3>");
    form.Append("<div style='border:1px solid var(--line);border-radius:10px;padding:14px 16px;margin:0 0 12px'><div class='form-grid'>" + Txt("necessaryLabel", "Notwendig – Bezeichnung") + Txt("necessaryDesc", "Notwendig – Beschreibung") + "</div><div class='help'>Immer aktiv, kann nicht abgewählt werden.</div></div>");
    form.Append(cats);
    form.Append("<h3 style='margin:22px 0 10px'>Texte</h3>");
    form.Append(Txt("title", "Überschrift"));
    form.Append(Area("text", "Text", S(cfg, "text"), 3));
    form.Append("<div class='form-grid'>" + Txt("reject", "Button „ablehnen“") + Txt("accept", "Button „akzeptieren“") + Txt("settings", "Link „Einstellungen“") + Txt("save", "Button „Auswahl speichern“") + "</div>");
    form.Append("<div class='form-grid'>" + Txt("privacyLabel", "Link-Text Datenschutz") + Txt("privacyUrl", "Ziel Datenschutz", "z. B. /datenschutz") + Txt("imprintLabel", "Link-Text Impressum") + Txt("imprintUrl", "Ziel Impressum", "z. B. /impressum") + "</div>");
    form.Append("<h3 style='margin:22px 0 10px'>Darstellung</h3>");
    form.Append("<div class='form-grid'>" +
        Sel("layout", "Position", new[] { ("box", "Kasten unten links"), ("bar", "Leiste unten über die ganze Breite"), ("modal", "Fenster in der Mitte (Seite abgedunkelt)") }) +
        Sel("theme", "Farbschema", new[] { ("light", "Hell"), ("dark", "Dunkel") }) +
        "<div class='form-field'><label>Akzentfarbe (optional)</label><input name='accent' value='" + Enc(S(cfg, "accent")) + "' placeholder='#2f6f5e'><div class='help'>Leer = Akzentfarbe des Templates.</div></div>" +
        "<div class='form-field'><label>Auswahl merken (Monate)</label><input type='number' min='1' max='24' name='months' value='" + I(cfg, "months", 12) + "'><div class='help'>Danach wird erneut gefragt. Üblich: 12.</div></div>" +
        Txt("reopenLabel", "Beschriftung des Wieder-Öffnen-Buttons") + "</div>");
    form.Append(Check("reopen", B(cfg, "reopen"), "Kleinen Cookie-Button unten links zeigen, um die Auswahl später zu ändern", "Alternativ oder zusätzlich: ein Link mit href=\"#cookie-einstellungen\" (z. B. im Fußzeilen-Menü) öffnet die Einstellungen."));
    form.Append("<h3 style='margin:22px 0 10px'>Erweitert</h3>");
    form.Append(Check("consentMode", B(cfg, "consentMode"), "Google Consent Mode v2 senden", "Setzt vor allen Google-Tags „abgelehnt“ als Standard und meldet die Entscheidung an gtag weiter."));
    form.Append(Check("showAlways", B(cfg, "showAlways"), "Banner auch ohne optionale Kategorie zeigen", "Nur nötig, wenn ausdrücklich ein Hinweis gewünscht ist. Ohne optionale Kategorien gibt es nichts zu entscheiden."));
    form.Append("<div style='display:flex;gap:10px;flex-wrap:wrap;margin-top:8px'><button type='submit' class='btn'>Speichern</button>" +
        (Active(cfg) ? "<a class='btn btn-ghost' href='/?cookie-vorschau' target='_blank' rel='noopener'>Vorschau auf der Website</a>" : "") + "</div>");
    sb.Append(req.Ui.Card(req.Ui.Form(form.ToString(), new Dictionary<string, string> { ["action"] = "save" }), "Einstellungen"));

    sb.Append(req.Ui.Card(
        "<p>Bereits auf der Seite stehender Code wartet auf die Zustimmung, wenn er so markiert ist:</p>" +
        "<pre style='white-space:pre-wrap;font-size:13px;background:var(--surface-2);padding:12px;border-radius:8px'>" + Enc("<script type=\"text/plain\" data-consent=\"statistik\">\n  // wird erst nach Zustimmung zu „Statistik“ ausgeführt\n</script>\n\n<iframe data-consent=\"medien\" data-src=\"https://www.youtube-nocookie.com/embed/…\" width=\"560\" height=\"315\"></iframe>") + "</pre>" +
        "<p>Das iframe zeigt bis zur Zustimmung einen Platzhalter mit „Inhalt laden“. Für Templates und Plugins: <code>MatConsent.has('statistik')</code>, <code>MatConsent.open()</code> und das Ereignis <code>matconsent</code>.</p>",
        "So bindet man Code und Einbettungen ein"));

    sb.Append(req.Ui.Card("<p>Erhöht die Version des Banners: Alle Besucher werden beim nächsten Besuch erneut gefragt – nötig, wenn neue Dienste dazukommen.</p>" +
        req.Ui.ActionButton("Alle Besucher erneut fragen", new Dictionary<string, string> { ["action"] = "reask" }, "btn-ghost", "Alle Besucher beim nächsten Besuch erneut fragen?"),
        "Erneut fragen"));
    sb.Append(req.Ui.Card("<p>Setzt Texte, Darstellung und Kategorien (samt eingefügtem Code) auf den Auslieferungszustand zurück. Bereits erteilte Zustimmungen der Besucher bleiben gültig.</p>" +
        req.Ui.ActionButton("Auf Standard zurücksetzen", new Dictionary<string, string> { ["action"] = "reset" }, "btn-danger", "Alle Einstellungen des Cookie-Banners auf Standard zurücksetzen?"),
        "Zurücksetzen"));
    return sb.ToString();
});

Log(Active(cfg0) ? "Cookie-Banner aktiv." : "Cookie-Banner geladen (ausgeblendet – keine optionale Kategorie aktiv).");
