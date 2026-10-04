using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

// Leserstimmen — visitor reviews with a 1–5 rating (stars, flames or hearts), shown as cards, with a
// submission form, an average, and moderation in the admin.
//
// Its own key on purpose: MatCMS seeds a plugin "bewertungen" and rewrites that one's code on every
// start, so an improved version under the same key would silently be reverted by the next restart.
// Storage is its own too (SiteSettings "plugin.leserstimmen.<collection>") so the two never mix.
//
// Configuration (Plugins → Leserstimmen → Konfiguration, all optional):
//   autoPublish = true      publish without moderation
//   notifyEmail = a@b.de    mail on every new review (falls back to the site's contact recipient)

const string Prefix = "plugin.leserstimmen.";
const int MaxPerStore = 1000;   // an anonymous endpoint must not grow storage without bound
const int MinSeconds = 3;       // a form sent faster than this after rendering came from a script

// Plugin code runs again on every admin save, and handlers of the old run can still be in flight
// while the new run registers its own. A lock object created HERE would therefore exist twice and
// lock nothing. A string literal is interned process-wide — the same object for every run.
object Gate() => "matcms.plugin.leserstimmen.gate";

string Enc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

// The collection name arrives from an anonymous POST: reduced to a short ascii slug so nobody can
// spawn arbitrary settings rows or odd keys.
string Slug(string store)
{
    var s = new string((store ?? "").Trim().ToLowerInvariant()
        .Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-').ToArray());
    if (s.Length == 0) s = "default";
    return s.Length > 40 ? s.Substring(0, 40) : s;
}

// Stored JSON is read defensively: one malformed value must cost one field, not the whole page.
string S(JsonNode n, string k) { try { return n?[k]?.GetValue<string>() ?? ""; } catch { return n?[k]?.ToString() ?? ""; } }
long L(JsonNode n, string k) { try { return n?[k]?.GetValue<long>() ?? 0; } catch { return long.TryParse(S(n, k), out var v) ? v : 0; } }
int Rating(JsonNode n) { long r; try { r = n?["rating"]?.GetValue<int>() ?? 5; } catch { r = L(n, "rating"); } return (int)Math.Clamp(r, 1, 5); }
bool Approved(JsonNode n) { try { return n?["approved"]?.GetValue<bool>() ?? false; } catch { return S(n, "approved") == "true"; } }

JsonArray Load(AppDbContext db, string store)
{
    var row = db.SiteSettings.FirstOrDefault(x => x.Key == Prefix + Slug(store));
    if (row == null || string.IsNullOrWhiteSpace(row.Value)) return new JsonArray();
    try { return JsonNode.Parse(row.Value) as JsonArray ?? new JsonArray(); } catch { return new JsonArray(); }
}
void Save(AppDbContext db, string store, JsonArray arr)
{
    var k = Prefix + Slug(store);
    var row = db.SiteSettings.FirstOrDefault(x => x.Key == k);
    if (row == null) db.SiteSettings.Add(new SiteSetting { Key = k, Value = arr.ToJsonString() });
    else row.Value = arr.ToJsonString();
    db.SaveChanges();
}
List<(string Store, JsonObject R)> All(AppDbContext db)
{
    var rows = new List<(string, JsonObject)>();
    foreach (var s in db.SiteSettings.Where(x => x.Key.StartsWith(Prefix)).ToList())
    {
        try { if (JsonNode.Parse(s.Value) is JsonArray a) foreach (var n in a) if (n is JsonObject o) rows.Add((s.Key.Substring(Prefix.Length), o)); }
        catch { }
    }
    return rows;
}
// Reviews of the former built-in "Bewertungen" plugin (SiteSettings "plugin.reviews.<collection>").
List<(string Store, JsonObject R)> Legacy(AppDbContext db)
{
    var rows = new List<(string, JsonObject)>();
    foreach (var s in db.SiteSettings.Where(x => x.Key.StartsWith("plugin.reviews.")).ToList())
    {
        try { if (JsonNode.Parse(s.Value) is JsonArray a) foreach (var n in a) if (n is JsonObject o) rows.Add((Slug(s.Key.Substring("plugin.reviews.".Length)), o)); }
        catch { }
    }
    return rows;
}
// Ticks alone collide when two reviews land in the same clock tick (coarse clocks on some hosts);
// one past the largest existing id never does — every write happens under Gate().
long NextId(JsonArray arr)
{
    long max = 0;
    foreach (var n in arr) max = Math.Max(max, L(n, "id"));
    return Math.Max(max + 1, DateTime.UtcNow.Ticks);
}
// Only plain http(s) links leave the form — anything else (javascript:, data:) is dropped.
string SafeUrl(string u)
{
    u = (u ?? "").Trim();
    if (u.Length == 0 || u.Length > 300) return "";
    if (!u.Contains("://")) u = "https://" + u;
    return Uri.TryCreate(u, UriKind.Absolute, out var x) && (x.Scheme == "http" || x.Scheme == "https") ? x.ToString() : "";
}
string UrlHost(string u)
{
    try { var h = new Uri(u).Host; return h.StartsWith("www.") ? h.Substring(4) : h; }
    catch { return ""; }
}
// MatCMS runs with InvariantGlobalization: "de-DE" exists but carries invariant data, so it would
// print "4.8" and "October". German formatting is therefore done by hand.
string[] Months = { "Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember" };
string Month(DateTime d) => Months[d.Month - 1] + " " + d.Year;
bool AutoPublish() => string.Equals(Config("autoPublish"), "true", StringComparison.OrdinalIgnoreCase);

// ---- symbols: inline SVG in currentColor, so the theme colours them (an emoji ignores CSS) ----
string SymPath(string sym) => sym switch
{
    "flamme" => "M12 2c.6 3.2 2.4 5.2 4.1 7.1C17.7 10.9 19 12.7 19 15.2 19 19 15.9 22 12 22s-7-3-7-6.8c0-2.4 1.2-4.2 2.7-5.6.2 1.7 1 2.9 2.2 3.5-.4-2.9.4-5.8 3.1-9.1z",
    "herz" => "M12 21s-7.5-4.6-9.6-9.3C1 8.3 3 4.5 6.6 4.5c2.1 0 3.6 1.2 4.4 2.6.8-1.4 2.3-2.6 4.4-2.6 3.6 0 5.6 3.8 4.2 7.2C19.5 16.4 12 21 12 21z",
    _ => "M12 2.6l2.9 6 6.6.9-4.8 4.6 1.2 6.5L12 17.5l-5.9 3.1 1.2-6.5L2.5 9.5l6.6-.9z"
};
string Svg(string sym) => "<svg viewBox='0 0 24 24' aria-hidden='true' focusable='false'><path d='" + SymPath(sym) + "'/></svg>";
string Noun(string sym, int n) => sym switch
{
    "flamme" => n == 1 ? "Flamme" : "Flammen",
    "herz" => n == 1 ? "Herz" : "Herzen",
    _ => n == 1 ? "Stern" : "Sterne"
};
string Symbols(int r, string sym)
{
    var sb = new StringBuilder("<span class='mls-sym' role='img' aria-label='" + r + " von 5 " + Noun(sym, 5) + "'>");
    for (int i = 1; i <= 5; i++) sb.Append("<span class='" + (i <= r ? "on" : "off") + "'>" + Svg(sym) + "</span>");
    return sb.Append("</span>").ToString();
}

// Anti-spam without a third party: a honeypot plus a signed render time. The token proves the form
// was rendered by this site at least MinSeconds ago and at most a week ago (a tab left open over the
// weekend still works): no session, nothing stored, and a bot posting straight to the endpoint has
// no valid token.
ITimeLimitedDataProtector Protector(IServiceProvider sp) =>
    sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("MatCMS.Plugin.Leserstimmen.v1").ToTimeLimitedDataProtector();
string IssueToken(IServiceProvider sp) =>
    Protector(sp).Protect(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), TimeSpan.FromDays(7));
bool TokenOk(IServiceProvider sp, string token)
{
    try { return long.TryParse(Protector(sp).Unprotect(token ?? ""), out var t) && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t >= MinSeconds; }
    catch { return false; }
}

// Mail goes out after the response on its own scope: the request's services are disposed by then,
// and a slow SMTP server must not hold up the visitor's redirect.
void Notify(IServiceProvider sp, string subject, string body)
{
    var to = Config("notifyEmail");
    if (string.IsNullOrWhiteSpace(to))
        to = sp.GetService<AppDbContext>()?.SiteSettings.FirstOrDefault(x => x.Key == "ContactRecipient")?.Value ?? "";
    if (string.IsNullOrWhiteSpace(to)) return;
    var scopes = sp.GetRequiredService<IServiceScopeFactory>();
    var list = to.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    _ = Task.Run(async () =>
    {
        try
        {
            using var scope = scopes.CreateScope();
            var mail = scope.ServiceProvider.GetService<EmailService>();
            if (mail != null) await mail.SendAsync(list, subject, body);
        }
        catch { }
    });
}

AddHeadHtml("<style>" +
  // Every colour is derived from the active template's variables, so the block fits a light and a
  // dark theme alike. A template overrides the --mls-* variables to restyle it.
  ".mls{--mls-accent:var(--accent,#c98a2b);--mls-text:var(--ink,#3a342e);--mls-head:var(--black,#1a1512);" +
  "--mls-card:color-mix(in srgb,var(--mls-text) 5%,var(--bg,#fff));--mls-line:color-mix(in srgb,var(--mls-text) 16%,transparent);" +
  "--mls-off:color-mix(in srgb,var(--mls-text) 24%,transparent);--mls-field:var(--bg,#fff);--mls-radius:12px;--mls-quote-font:inherit;" +
  "max-width:var(--max,1140px);margin:0 auto;padding:0 24px;color:var(--mls-text);}" +
  ".mls h2{text-align:center;margin:0 0 10px;}" +
  ".mls-avg{display:flex;align-items:center;justify-content:center;gap:12px;flex-wrap:wrap;margin:0 0 30px;color:var(--mls-text);font-size:15px;}" +
  ".mls-avg strong{font-size:22px;color:var(--mls-head);}" +
  ".mls-list{columns:var(--mls-cols,3);column-gap:24px;margin:0 0 40px;}" +
  "@media(max-width:960px){.mls-list{columns:min(var(--mls-cols,3),2);}}@media(max-width:600px){.mls-list{columns:1;}}" +
  ".mls-card{break-inside:avoid;margin:0 0 24px;background:var(--mls-card);border:1px solid var(--mls-line);border-radius:var(--mls-radius);padding:22px 22px 18px;}" +
  ".mls-sym{display:inline-flex;gap:3px;vertical-align:middle;}.mls-sym svg{width:18px;height:18px;fill:currentColor;display:block;}" +
  ".mls-sym .on{color:var(--mls-accent);}.mls-sym .off{color:var(--mls-off);}" +
  ".mls-card blockquote{margin:12px 0 0;padding:0;border:0;font-family:var(--mls-quote-font);font-size:15.5px;line-height:1.65;color:var(--mls-text);overflow-wrap:anywhere;}" +
  ".mls-card figcaption{margin-top:14px;display:flex;flex-wrap:wrap;align-items:baseline;gap:4px 10px;font-size:14px;}" +
  ".mls-card figcaption b{color:var(--mls-head);font-weight:600;}" +
  ".mls-card figcaption a{color:var(--mls-accent);font-size:13px;text-decoration:none;}.mls-card figcaption a:hover{text-decoration:underline;}" +
  ".mls-card figcaption small{opacity:.7;font-size:12.5px;}" +
  ".mls-form{max-width:660px;margin:0 auto;background:var(--mls-card);border:1px solid var(--mls-line);border-radius:var(--mls-radius);padding:28px;}" +
  ".mls-form h3{margin:0 0 6px;}.mls-form .mls-intro{margin:0 0 20px;font-size:14px;opacity:.85;}" +
  ".mls-form label,.mls-form legend{display:block;font-weight:600;margin:0 0 6px;color:var(--mls-head);font-size:14px;padding:0;}" +
  ".mls-form fieldset{border:0;margin:0 0 16px;padding:0;min-width:0;}" +
  ".mls-form input[type=text],.mls-form input[type=email],.mls-form input[type=url],.mls-form textarea{width:100%;padding:11px 13px;border:1px solid var(--mls-line);border-radius:calc(var(--mls-radius) / 2);font:inherit;color:var(--mls-head);background:var(--mls-field);margin:0 0 16px;box-sizing:border-box;}" +
  ".mls-form input:focus,.mls-form textarea:focus{outline:none;border-color:var(--mls-accent);}" +
  ".mls-form textarea{min-height:130px;resize:vertical;}" +
  ".mls-form .mls-opt{font-weight:400;opacity:.7;}" +
  // Rating: radios in reverse order so "this one and every one before it" is a plain sibling
  // selector; the radios stay focusable (keyboard: Tab, then arrow keys).
  ".mls-rate{display:inline-flex;flex-direction:row-reverse;justify-content:flex-end;gap:4px;}" +
  ".mls-rate input{position:absolute;opacity:0;width:1px;height:1px;margin:0;}" +
  ".mls-rate label{margin:0;cursor:pointer;color:var(--mls-off);line-height:0;padding:2px;}.mls-rate svg{width:32px;height:32px;fill:currentColor;transition:color .15s,transform .15s;}" +
  ".mls-rate input:checked ~ label,.mls-rate label:hover,.mls-rate label:hover ~ label{color:var(--mls-accent);}" +
  ".mls-rate label:hover svg{transform:scale(1.12);}" +
  ".mls-rate input:focus-visible + label{outline:2px solid var(--mls-accent);outline-offset:2px;}" +
  ".mls-hp{position:absolute;left:-9999px;width:1px;height:1px;overflow:hidden;}" +
  ".mls-ok{max-width:660px;margin:0 auto 26px;padding:14px 18px;border:1px solid var(--mls-accent);border-radius:var(--mls-radius);color:var(--mls-head);background:color-mix(in srgb,var(--mls-accent) 10%,transparent);}" +
  ".mls-empty{text-align:center;font-style:italic;opacity:.75;margin:0 0 30px;}" +
  "</style>");

AddAdminMenu("Leserstimmen", "/admin/plugin/leserstimmen", "⭐");

// ---- public endpoint: a visitor submits a review --------------------------------------------
AddPublicPage("leserstimmen", req =>
{
    if (!req.IsPost) return "";
    // Spam is dropped silently — the visitor still sees the thank-you, so a bot learns nothing.
    if (!string.IsNullOrWhiteSpace(req.F("website"))) return "";
    if (!TokenOk(req.Services, req.F("t"))) return "";

    var store = Slug(req.F("store"));
    var name = (req.F("name") ?? "").Trim();
    var text = (req.F("text") ?? "").Trim().Replace("\r\n", "\n");
    var email = (req.F("email") ?? "").Trim();
    var link = SafeUrl(req.F("link"));
    int rating = int.TryParse(req.F("rating"), out var rr) ? rr : 0;
    if (name.Length == 0 || text.Length == 0 || rating < 1 || rating > 5) return "";
    if (name.Length > 80) name = name.Substring(0, 80);
    if (text.Length > 3000) text = text.Substring(0, 3000);
    if (email.Length > 200 || !email.Contains('@')) email = "";

    var db = req.Service<AppDbContext>();
    var auto = AutoPublish();
    lock (Gate())
    {
        var arr = Load(db, store);
        if (arr.Count >= MaxPerStore) { req.Log("Leserstimmen: Sammlung '" + store + "' ist voll (" + MaxPerStore + ")."); return ""; }
        arr.Add(new JsonObject
        {
            ["id"] = NextId(arr),
            ["name"] = name,
            ["text"] = text,
            ["rating"] = rating,
            ["email"] = email,
            ["link"] = link,
            ["date"] = DateTime.UtcNow.ToString("o"),
            ["approved"] = auto
        });
        Save(db, store, arr);
    }
    req.Log("Neue Leserstimme (" + rating + "/5) von " + name + (auto ? " – veröffentlicht" : " – wartet auf Freigabe"));
    Notify(req.Services, "Neue Leserstimme von " + name,
        name + " hat " + rating + " von 5 gegeben" + (email.Length > 0 ? " (" + email + ")" : "") + ":\n\n" + text +
        (link.Length > 0 ? "\n\nLink: " + link : "") +
        (auto ? "\n\nSie ist bereits veröffentlicht." : "\n\nSie wartet in der Verwaltung unter „Leserstimmen“ auf deine Freigabe."));
    return "";
});

// ---- the block ------------------------------------------------------------------------------
const string Fields = """
[
 {"id":"heading","label":"Überschrift","type":"text","placeholder":"Leserstimmen"},
 {"id":"store","label":"Sammlung","type":"text","default":"default","help":"Name der Liste, z. B. ein Buchtitel. Blöcke mit derselben Sammlung zeigen dieselben Stimmen."},
 {"id":"symbol","label":"Symbol","type":"select","default":"stern","options":[{"value":"stern","label":"Sterne"},{"value":"flamme","label":"Flammen"},{"value":"herz","label":"Herzen"}]},
 {"id":"address","label":"Anrede","type":"select","default":"sie","options":[{"value":"sie","label":"Sie"},{"value":"du","label":"du"}]},
 {"id":"columns","label":"Spalten","type":"select","default":"3","options":[{"value":"1","label":"1"},{"value":"2","label":"2"},{"value":"3","label":"3"}]},
 {"id":"showAverage","label":"Durchschnitt zeigen","type":"select","default":"ja","options":[{"value":"ja","label":"Ja"},{"value":"nein","label":"Nein"}]},
 {"id":"showForm","label":"Formular zeigen","type":"select","default":"ja","options":[{"value":"ja","label":"Ja"},{"value":"nein","label":"Nein"}]},
 {"id":"askLink","label":"Link-Feld (z. B. Instagram, Blog)","type":"select","default":"nein","options":[{"value":"nein","label":"Nein"},{"value":"ja","label":"Ja"}]},
 {"id":"askEmail","label":"E-Mail-Feld (wird nie gezeigt)","type":"select","default":"nein","options":[{"value":"nein","label":"Nein"},{"value":"optional","label":"Optional"},{"value":"pflicht","label":"Pflicht"}]},
 {"id":"formTitle","label":"Formular-Überschrift","type":"text","placeholder":"Ihre Bewertung abgeben"},
 {"id":"intro","label":"Hinweis über dem Formular","type":"textarea","placeholder":"z. B. Einverständnis zur Veröffentlichung"},
 {"id":"buttonText","label":"Button-Text","type":"text","placeholder":"Bewertung absenden"}
]
""";

AddBlock("leserstimmen", "Leserstimmen", "Rezensionen mit Sterne-, Flammen- oder Herz-Bewertung: Karten, Durchschnitt und Formular.", req =>
{
    JsonObject d = null;
    try { d = JsonNode.Parse(req.Data) as JsonObject; } catch { }
    string D(string k, string def) { var v = S(d, k).Trim(); return v.Length == 0 ? def : v; }
    var store = Slug(D("store", "default"));
    var sym = D("symbol", "stern");
    var du = D("address", "sie") == "du";
    var cols = D("columns", "3");
    if (cols != "1" && cols != "2") cols = "3";
    var askEmail = D("askEmail", "nein");

    var db = req.Service<AppDbContext>();
    var shown = Load(db, store).Where(Approved).ToList();
    shown.Sort((a, b) => string.CompareOrdinal(S(b, "date"), S(a, "date")));

    var id = "mls-" + store;   // anchor + id prefix: two blocks on one page must not share radio ids
    var sb = new StringBuilder();
    sb.Append("<section class='section'><div class='mls' id='" + id + "' style='--mls-cols:" + cols + "'>");
    var heading = D("heading", "");
    if (heading.Length > 0) sb.Append("<h2>" + Enc(heading) + "</h2>");

    if (shown.Count > 0 && D("showAverage", "ja") == "ja")
    {
        var avg = shown.Average(n => (double)Rating(n));
        sb.Append("<p class='mls-avg'>" + Symbols((int)Math.Round(avg), sym) + "<strong>" +
            avg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace(".", ",") + "</strong> von 5 · " +
            shown.Count + (shown.Count == 1 ? " Stimme" : " Stimmen") + "</p>");
    }
    if (req.Q("leserstimme") == "danke")
        sb.Append("<div class='mls-ok' role='status'>" + (du ? "Danke für deine Bewertung!" : "Vielen Dank für Ihre Bewertung!") +
            (AutoPublish() ? "" : (du ? " Sie erscheint, sobald ich sie freigegeben habe." : " Sie erscheint nach einer kurzen Prüfung.")) + "</div>" +
            // The redirect cannot carry a #fragment (the endpoint refuses such a return URL), so the
            // page would open at the top with the message out of sight below the hero.
            "<script>document.currentScript.previousElementSibling.scrollIntoView({block:'center'});</script>");

    if (shown.Count == 0)
        sb.Append("<p class='mls-empty'>" + (du ? "Noch keine Stimmen – sei die oder der Erste!" : "Noch keine Bewertungen – seien Sie die oder der Erste!") + "</p>");
    else
    {
        sb.Append("<div class='mls-list'>");
        foreach (var n in shown)
        {
            var link = SafeUrl(S(n, "link"));
            var date = DateTime.TryParse(S(n, "date"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? Month(dt) : "";
            sb.Append("<figure class='mls-card'>" + Symbols(Rating(n), sym) +
                "<blockquote>" + Enc(S(n, "text")).Replace("\n", "<br>") + "</blockquote><figcaption><b>" + Enc(S(n, "name")) + "</b>" +
                (link.Length > 0 ? "<a href='" + Enc(link) + "' target='_blank' rel='nofollow ugc noopener'>" + Enc(UrlHost(link)) + " ↗</a>" : "") +
                (date.Length > 0 ? "<small>" + date + "</small>" : "") + "</figcaption></figure>");
        }
        sb.Append("</div>");
    }

    if (D("showForm", "ja") == "ja")
    {
        // Back to this page with the thank-you flag. No "#anchor": /plugin/{key} only redirects to a
        // return URL that Uri.IsWellFormedUriString accepts, and with a fragment it does not — the
        // visitor would land on the home page instead.
        var ret = (string.IsNullOrEmpty(req.Path) ? "/" : req.Path) + "?leserstimme=danke";
        sb.Append("<form class='mls-form' method='post' action='/plugin/leserstimmen'>");
        sb.Append("<input type='hidden' name='__return' value='" + Enc(ret) + "'/>");
        sb.Append("<input type='hidden' name='store' value='" + Enc(store) + "'/>");
        sb.Append("<input type='hidden' name='t' value='" + Enc(IssueToken(req.Services)) + "'/>");
        sb.Append("<div class='mls-hp' aria-hidden='true'><label>Website<input type='text' name='website' tabindex='-1' autocomplete='off'/></label></div>");
        sb.Append("<h3>" + Enc(D("formTitle", du ? "Deine Bewertung" : "Ihre Bewertung abgeben")) + "</h3>");
        var intro = D("intro", "");
        if (intro.Length > 0) sb.Append("<p class='mls-intro'>" + Enc(intro).Replace("\n", "<br>") + "</p>");
        sb.Append("<label for='" + id + "-name'>Name" + (du ? " oder Account" : "") + "</label>");
        sb.Append("<input type='text' id='" + id + "-name' name='name' maxlength='80' required autocomplete='name' placeholder='" + (du ? "z. B. @buecherliebe" : "Ihr Name") + "'/>");
        if (askEmail != "nein")
        {
            sb.Append("<label for='" + id + "-email'>E-Mail <span class='mls-opt'>(wird nicht veröffentlicht" + (askEmail == "pflicht" ? "" : ", optional") + ")</span></label>");
            sb.Append("<input type='email' id='" + id + "-email' name='email' maxlength='200' autocomplete='email'" + (askEmail == "pflicht" ? " required" : "") + "/>");
        }
        sb.Append("<fieldset><legend>" + (du ? "Deine " : "Ihre ") + Noun(sym, 5) + "</legend><div class='mls-rate'>");
        for (int i = 5; i >= 1; i--)
            sb.Append("<input type='radio' id='" + id + "-r" + i + "' name='rating' value='" + i + "'" + (i == 5 ? " required" : "") + "/>" +
                "<label for='" + id + "-r" + i + "' title='" + i + " " + Noun(sym, i) + "'>" + Svg(sym) + "</label>");
        sb.Append("</div></fieldset>");
        if (D("askLink", "nein") == "ja")
        {
            sb.Append("<label for='" + id + "-link'>Link zu " + (du ? "deinem" : "Ihrem") + " Beitrag <span class='mls-opt'>(optional)</span></label>");
            sb.Append("<input type='url' id='" + id + "-link' name='link' maxlength='300' inputmode='url' placeholder='Instagram, Blog, Shop …'/>");
        }
        sb.Append("<label for='" + id + "-text'>" + (du ? "Deine" : "Ihre") + " Bewertung</label>");
        sb.Append("<textarea id='" + id + "-text' name='text' maxlength='3000' required placeholder='" + (du ? "Wie hat es dir gefallen?" : "Was hat Ihnen gefallen?") + "'></textarea>");
        sb.Append("<button class='btn' type='submit'>" + Enc(D("buttonText", "Bewertung absenden")) + "</button>");
        sb.Append("</form>");
    }
    sb.Append("</div></section>");
    return sb.ToString();
}, Fields);

// ---- admin: moderate, and add reviews by hand (e.g. from Instagram or a shop) ---------------
AddAdminPage("leserstimmen", req =>
{
    var db = req.Service<AppDbContext>();
    if (req.IsPost)
    {
        var action = req.Action;
        var store = Slug(req.F("store"));
        lock (Gate())
        {
            var arr = Load(db, store);
            if (action == "add")
            {
                var name = (req.F("name") ?? "").Trim();
                var text = (req.F("text") ?? "").Trim().Replace("\r\n", "\n");
                int rating = int.TryParse(req.F("rating"), out var r) ? Math.Clamp(r, 1, 5) : 5;
                if (name.Length > 0 && text.Length > 0)
                {
                    arr.Add(new JsonObject
                    {
                        ["id"] = NextId(arr), ["name"] = name.Length > 80 ? name.Substring(0, 80) : name,
                        ["text"] = text.Length > 3000 ? text.Substring(0, 3000) : text, ["rating"] = rating,
                        ["email"] = "", ["link"] = SafeUrl(req.F("link")),
                        ["date"] = DateTime.UtcNow.ToString("o"), ["approved"] = true
                    });
                    Save(db, store, arr);
                    req.Log("Leserstimme von Hand hinzugefügt (" + name + ")");
                }
                return "";
            }
            if (action == "import")
            {
                // Take over the reviews of the former built-in "Bewertungen" plugin, collection by
                // collection. Its rows are only read, never changed, so the old block keeps working
                // until the page is switched to this one. Matching name + text is skipped, which
                // makes a second click harmless.
                int added = 0;
                foreach (var (legacyStore, o) in Legacy(db))
                {
                    var dst = Load(db, legacyStore);
                    if (dst.Any(n => S(n, "name") == S(o, "name") && S(n, "text") == S(o, "text"))) continue;
                    dst.Add(new JsonObject
                    {
                        ["id"] = NextId(dst), ["name"] = S(o, "name"), ["text"] = S(o, "text"), ["rating"] = Rating(o),
                        ["email"] = "", ["link"] = "", ["date"] = S(o, "date"), ["approved"] = Approved(o)
                    });
                    Save(db, legacyStore, dst);
                    added++;
                }
                req.Log("Aus „Bewertungen“ übernommen: " + added);
                return "";
            }
            long id = long.TryParse(req.F("id"), out var pid) ? pid : 0;
            var target = arr.FirstOrDefault(n => L(n, "id") == id);
            if (target != null)
            {
                if (action == "approve") target["approved"] = true;
                else if (action == "unpublish") target["approved"] = false;
                else if (action == "delete") arr.Remove(target);
                Save(db, store, arr);
                req.Log("Leserstimme " + action + " (" + id + ")");
            }
        }
        return "";
    }

    var rows = All(db);
    rows.Sort((a, b) => string.CompareOrdinal(S(b.R, "date"), S(a.R, "date")));
    var pend = rows.FindAll(x => !Approved(x.R));
    var pub = rows.FindAll(x => Approved(x.R));
    var stores = rows.Select(x => x.Store).Distinct().OrderBy(x => x).ToList();

    string Row(string store, JsonObject r, bool isPending)
    {
        var f = new Dictionary<string, string> { ["store"] = store, ["id"] = L(r, "id").ToString() };
        var link = SafeUrl(S(r, "link"));
        var email = S(r, "email");
        // Plain characters here: the block's stylesheet is only injected into the public site, and an
        // unstyled inline SVG in the admin would render at full width.
        var stars = new string('★', Rating(r)) + new string('☆', 5 - Rating(r));
        var b = new StringBuilder("<tr><td style='white-space:nowrap;color:#c98a2b' title='" + Rating(r) + " von 5'>" + stars + "</td>");
        b.Append("<td><strong>" + Enc(S(r, "name")) + "</strong>" +
            (email.Length > 0 ? "<div class='muted' style='font-size:12px'>" + Enc(email) + "</div>" : "") +
            (link.Length > 0 ? "<div style='font-size:12px'><a href='" + Enc(link) + "' target='_blank' rel='noopener'>" + Enc(UrlHost(link)) + "</a></div>" : "") + "</td>");
        b.Append("<td style='max-width:520px'>" + Enc(S(r, "text")).Replace("\n", "<br>") + "</td>");
        b.Append("<td><span class='badge'>" + Enc(store) + "</span></td>");
        var date = DateTime.TryParse(S(r, "date"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt.ToLocalTime().ToString("dd.MM.yyyy") : "";
        b.Append("<td style='white-space:nowrap'>" + date + "</td><td><div class='actions'>");
        b.Append(isPending
            ? req.Ui.ActionButton("Freigeben", new Dictionary<string, string>(f) { ["action"] = "approve" }, "")
            : req.Ui.ActionButton("Verbergen", new Dictionary<string, string>(f) { ["action"] = "unpublish" }, "btn-ghost"));
        b.Append(req.Ui.ActionButton("Löschen", new Dictionary<string, string>(f) { ["action"] = "delete" }, "btn-danger", "Diese Leserstimme wirklich löschen?"));
        return b.Append("</div></td></tr>").ToString();
    }
    string Table(List<(string Store, JsonObject R)> list, bool isPending, string empty)
    {
        var t = new StringBuilder("<table class='data'><thead><tr><th>Wertung</th><th>Name</th><th>Text</th><th>Sammlung</th><th>Datum</th><th></th></tr></thead><tbody>");
        if (list.Count == 0) t.Append("<tr><td colspan='99' class='muted' style='font-style:italic'>" + Enc(empty) + "</td></tr>");
        foreach (var x in list) t.Append(Row(x.Store, x.R, isPending));
        return t.Append("</tbody></table>").ToString();
    }

    var sb = new StringBuilder();
    sb.Append(req.Ui.PageHead("Neue Stimmen freigeben, verbergen oder löschen – oder eine Stimme von Hand eintragen." +
        (AutoPublish() ? " Neue Stimmen erscheinen sofort (autoPublish)." : "")));
    sb.Append(req.Ui.Card(Table(pend, true, "Nichts zu prüfen."), "Wartet auf Freigabe (" + pend.Count + ")"));
    sb.Append(req.Ui.Card(Table(pub, false, "Noch nichts veröffentlicht."), "Veröffentlicht (" + pub.Count + ")"));

    var opts = string.Join("", Enumerable.Range(1, 5).Reverse().Select(i => "<option value='" + i + "'>" + i + " von 5</option>"));
    var storeList = "<datalist id='mls-stores'>" + string.Join("", stores.Select(s => "<option value='" + Enc(s) + "'>")) + "</datalist>";
    var add =
        "<div class='form-grid'>" +
        "<div class='form-field'><label>Name</label><input name='name' maxlength='80' required></div>" +
        "<div class='form-field'><label>Wertung</label><select name='rating'>" + opts + "</select></div>" +
        "<div class='form-field'><label>Sammlung</label><input name='store' list='mls-stores' value='" + Enc(stores.FirstOrDefault() ?? "default") + "' required>" + storeList +
        "<div class='help'>Muss zur Sammlung im Block passen.</div></div>" +
        "<div class='form-field'><label>Link (optional)</label><input name='link' maxlength='300' placeholder='https://…'></div>" +
        "</div>" +
        "<div class='form-field'><label>Text</label><textarea name='text' rows='4' maxlength='3000' required></textarea></div>" +
        "<button type='submit' class='btn'>Hinzufügen</button>";
    sb.Append(req.Ui.Card(req.Ui.Form(add, new Dictionary<string, string> { ["action"] = "add" }), "Von Hand hinzufügen"));

    var open = Legacy(db).Count(x => !Load(db, x.Store).Any(n => S(n, "name") == S(x.R, "name") && S(n, "text") == S(x.R, "text")));
    if (open > 0)
        sb.Append(req.Ui.Card("<p>Auf dieser Seite " + (open == 1 ? "liegt noch eine Stimme" : "liegen noch " + open + " Stimmen") + " aus dem früheren Plugin „Bewertungen“. " +
            "Sie werden mit ihrer Sammlung und ihrem Freigabe-Status übernommen; danach auf der Seite den Block „Bewertungen“ durch „Leserstimmen“ ersetzen.</p>" +
            req.Ui.ActionButton("Übernehmen", new Dictionary<string, string> { ["action"] = "import" }, ""), "Aus „Bewertungen“ übernehmen"));
    return sb.ToString();
});

Log("Leserstimmen geladen.");
