using System.Text;
using System.Text.Json.Nodes;

string Enc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
// Sanitize the (client-supplied) store to a short ascii slug so an anonymous POST can't
// spawn arbitrary settings rows or odd keys. Falls back to "default".
string KeyFor(string store)
{
    var s = new string((store ?? "").Trim().ToLowerInvariant()
        .Where(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-').ToArray());
    if (s.Length == 0) s = "default";
    if (s.Length > 40) s = s.Substring(0, 40);
    return "plugin.reviews." + s;
}
// Serialize load-modify-save on a store row across concurrent requests. The registered
// lambdas below all close over this single object for the life of this plugin run.
var reviewLock = new object();
const int MaxPerStore = 1000; // bound storage: an anonymous endpoint must not grow without limit

JsonArray LoadArr(PluginRequest req, string store)
{
    var db = req.Service<AppDbContext>();
    var s = db.SiteSettings.FirstOrDefault(x => x.Key == KeyFor(store));
    if (s == null || string.IsNullOrWhiteSpace(s.Value)) return new JsonArray();
    try { return JsonNode.Parse(s.Value) as JsonArray ?? new JsonArray(); } catch { return new JsonArray(); }
}
void SaveArr(PluginRequest req, string store, JsonArray arr)
{
    var db = req.Service<AppDbContext>();
    var k = KeyFor(store);
    var s = db.SiteSettings.FirstOrDefault(x => x.Key == k);
    if (s == null) db.SiteSettings.Add(new SiteSetting { Key = k, Value = arr.ToJsonString() });
    else s.Value = arr.ToJsonString();
    db.SaveChanges();
}
string Stars(int r)
{
    r = r < 1 ? 1 : (r > 5 ? 5 : r);
    var sb = new StringBuilder("<span class='matrev-stars' aria-label='" + r + " von 5'>");
    for (int i = 1; i <= 5; i++) sb.Append(i <= r ? "<span class='on'>★</span>" : "<span class='off'>☆</span>");
    sb.Append("</span>");
    return sb.ToString();
}
bool AutoPublish() => string.Equals(Config("autoPublish"), "true", StringComparison.OrdinalIgnoreCase);

AddHeadHtml("<style>" +
  ".matrev{max-width:var(--max,1140px);margin:0 auto;padding:0 24px;}" +
  ".matrev-list{columns:3;column-gap:24px;margin:8px 0 34px;}" +
  "@media(max-width:960px){.matrev-list{columns:2;}}@media(max-width:600px){.matrev-list{columns:1;}}" +
  ".matrev-card{break-inside:avoid;margin:0 0 22px;background:#fff;border:1px solid #ece3d5;border-radius:12px;padding:20px 20px 16px;box-shadow:0 2px 10px rgba(20,16,14,.05);}" +
  ".matrev-stars{font-size:16px;letter-spacing:1px;white-space:nowrap;}.matrev-stars .on{color:var(--wolf-gold,#c98a2b);}.matrev-stars .off{color:#dcd2c2;}" +
  ".matrev-card blockquote{margin:10px 0 0;font-size:15px;line-height:1.6;color:#3a342e;}" +
  ".matrev-card figcaption{margin-top:12px;font-weight:700;font-size:14px;color:var(--wolf-ink,#1a1512);}" +
  ".matrev-form{max-width:640px;margin:0 auto;background:var(--bg-alt,#f5f0e8);border-radius:14px;padding:26px;}" +
  ".matrev-form label{display:block;font-weight:600;margin:0 0 6px;}" +
  ".matrev-form input[type=text],.matrev-form textarea{width:100%;padding:11px 13px;border:1px solid #d9cdba;border-radius:8px;font:inherit;background:#fff;margin-bottom:16px;box-sizing:border-box;}" +
  ".matrev-form textarea{min-height:120px;resize:vertical;}" +
  ".matrev-rate{display:inline-flex;flex-direction:row-reverse;gap:4px;margin-bottom:18px;}" +
  ".matrev-rate input{position:absolute;opacity:0;width:1px;height:1px;}" +
  ".matrev-rate label{font-size:30px;color:#dcd2c2;cursor:pointer;line-height:1;}" +
  ".matrev-rate input:checked ~ label,.matrev-rate label:hover,.matrev-rate label:hover ~ label{color:var(--wolf-gold,#c98a2b);}" +
  ".matrev-hp{position:absolute;left:-9999px;width:1px;height:1px;overflow:hidden;}" +
  ".matrev-ok{max-width:640px;margin:0 auto 22px;padding:14px 18px;background:#e7f6ec;border:1px solid #b7e2c4;border-radius:10px;color:#1c6b39;font-weight:600;}" +
  ".matrev-empty{color:#8a7f72;font-style:italic;}" +
  "</style>");

int pending = 0;
try {
    var db0 = Service<AppDbContext>();
    foreach (var s in db0.SiteSettings.Where(x => x.Key.StartsWith("plugin.reviews.")).ToList())
        try { var a = JsonNode.Parse(s.Value) as JsonArray; if (a != null) foreach (var n in a) if (!(n?["approved"]?.GetValue<bool>() ?? false)) pending++; } catch {}
} catch {}
AddAdminMenu(pending > 0 ? ("Bewertungen (" + pending + ")") : "Bewertungen", "/admin/plugin/bewertungen", "⭐");

AddPublicPage("bewertungen", req =>
{
    if (!req.IsPost) return "";
    if (!string.IsNullOrWhiteSpace(req.F("website"))) return "";
    var store = req.F("store");
    var name = (req.F("name") ?? "").Trim();
    var text = (req.F("text") ?? "").Trim();
    int rating = int.TryParse(req.F("rating"), out var rr) ? rr : 0;
    if (name.Length == 0 || text.Length == 0 || rating < 1 || rating > 5) return "";
    if (name.Length > 80) name = name.Substring(0, 80);
    if (text.Length > 2000) text = text.Substring(0, 2000);
    lock (reviewLock)
    {
        var arr = LoadArr(req, store);
        if (arr.Count >= MaxPerStore) return ""; // storage cap reached — drop silently
        arr.Add(new JsonObject {
            ["id"] = DateTime.UtcNow.Ticks,
            ["name"] = name,
            ["text"] = text,
            ["rating"] = rating,
            ["date"] = DateTime.UtcNow.ToString("o"),
            ["approved"] = AutoPublish()
        });
        SaveArr(req, store, arr);
    }
    req.Log("Neue Bewertung (" + rating + " Sterne) von " + name + (AutoPublish() ? " [auto]" : " [wartet]"));
    return "";
});

AddBlock("bewertungen", "Bewertungen", "Rezensionen mit Sternebewertung: Anzeige + Abgabeformular.", req =>
{
    string store = "default", heading = "";
    try { var d = JsonNode.Parse(req.Data) as JsonObject; if (d != null) {
        store = d["store"]?.GetValue<string>() ?? store;
        heading = d["heading"]?.GetValue<string>() ?? heading;
    } } catch {}
    var arr = LoadArr(req, store);
    var approved = new List<JsonNode>();
    foreach (var n in arr) if (n?["approved"]?.GetValue<bool>() ?? false) approved.Add(n);
    approved.Sort((a, b) => string.CompareOrdinal(b?["date"]?.GetValue<string>() ?? "", a?["date"]?.GetValue<string>() ?? ""));

    var sb = new StringBuilder();
    sb.Append("<section class='section'><div class='matrev'>");
    if (!string.IsNullOrWhiteSpace(heading)) sb.Append("<h2>" + Enc(heading) + "</h2>");
    if (req.Q("bewertung") == "ok")
        sb.Append("<div class='matrev-ok'>Vielen Dank für Ihre Bewertung!" + (AutoPublish() ? "" : " Sie erscheint nach einer kurzen Prüfung.") + "</div>");
    sb.Append("<div class='matrev-list'>");
    if (approved.Count == 0) sb.Append("<p class='matrev-empty'>Noch keine Bewertungen – seien Sie die/der Erste!</p>");
    foreach (var n in approved)
        sb.Append("<figure class='matrev-card'>" + Stars(n?["rating"]?.GetValue<int>() ?? 5) +
            "<blockquote>" + Enc(n?["text"]?.GetValue<string>()) + "</blockquote>" +
            "<figcaption>— " + Enc(n?["name"]?.GetValue<string>()) + "</figcaption></figure>");
    sb.Append("</div>");

    var ret = Enc((string.IsNullOrEmpty(req.Path) ? "/" : req.Path) + "?bewertung=ok");
    sb.Append("<form class='matrev-form' method='post' action='/plugin/bewertungen'>");
    sb.Append("<input type='hidden' name='__return' value='" + ret + "'/>");
    sb.Append("<input type='hidden' name='store' value='" + Enc(store) + "'/>");
    sb.Append("<div class='matrev-hp'><label>Website<input type='text' name='website' tabindex='-1' autocomplete='off'/></label></div>");
    sb.Append("<h3 style='margin-top:0;'>Ihre Bewertung abgeben</h3>");
    sb.Append("<label>Name</label><input type='text' name='name' maxlength='80' required placeholder='Ihr Name'/>");
    sb.Append("<label>Ihre Sterne</label><div class='matrev-rate'>");
    for (int i = 5; i >= 1; i--) sb.Append("<input type='radio' id='matrev-r" + i + "' name='rating' value='" + i + "'" + (i == 5 ? " required" : "") + "/><label for='matrev-r" + i + "' title='" + i + " Sterne'>★</label>");
    sb.Append("</div>");
    sb.Append("<label>Nachricht</label><textarea name='text' maxlength='2000' required placeholder='Was hat Ihnen gefallen?'></textarea>");
    sb.Append("<button class='btn' type='submit'>Bewertung absenden</button>");
    sb.Append("</form></div></section>");
    return sb.ToString();
});

AddAdminPage("bewertungen", req =>
{
    var db = req.Service<AppDbContext>();
    if (req.IsPost)
    {
        var action = req.F("action");
        var store = req.F("store");
        long id = long.TryParse(req.F("id"), out var pid) ? pid : 0;
        lock (reviewLock)
        {
            var arr = LoadArr(req, store);
            JsonNode target = null;
            foreach (var n in arr) if ((n?["id"]?.GetValue<long>() ?? -1) == id) target = n;
            if (target != null)
            {
                if (action == "approve") target["approved"] = true;
                else if (action == "unpublish") target["approved"] = false;
                else if (action == "delete") arr.Remove(target);
                SaveArr(req, store, arr);
                req.Log("Bewertung " + action + " (" + id + ")");
            }
        }
        return "";
    }

    var rows = new List<(string Store, JsonObject R)>();
    foreach (var s in db.SiteSettings.Where(x => x.Key.StartsWith("plugin.reviews.")).ToList())
    {
        var store = s.Key.Substring("plugin.reviews.".Length);
        try { var a = JsonNode.Parse(s.Value) as JsonArray; if (a != null) foreach (var n in a) if (n is JsonObject o) rows.Add((store, o)); } catch {}
    }
    rows.Sort((a, b) => string.CompareOrdinal(b.R["date"]?.GetValue<string>() ?? "", a.R["date"]?.GetValue<string>() ?? ""));
    var pend = rows.FindAll(x => !(x.R["approved"]?.GetValue<bool>() ?? false));
    var pub = rows.FindAll(x => (x.R["approved"]?.GetValue<bool>() ?? false));

    string Card(string store, JsonObject r, bool isPending)
    {
        var id = (r["id"]?.GetValue<long>() ?? 0).ToString();
        var b = new StringBuilder("<div class='block-item'><div class='b-info' style='flex:1;'>");
        b.Append("<div class='b-type'>" + Stars(r["rating"]?.GetValue<int>() ?? 5) + " <strong>" + Enc(r["name"]?.GetValue<string>()) + "</strong> <span class='muted' style='font-size:12px;'>(" + Enc(store) + ")</span></div>");
        b.Append("<div class='muted' style='font-size:14px;margin-top:4px;'>" + Enc(r["text"]?.GetValue<string>()) + "</div></div>");
        // Row actions via the admin-UI builder — each is a POST form that auto-carries the
        // antiforgery token (which is why Freigeben/Verbergen/Löschen now work).
        var f = new Dictionary<string, string> { ["store"] = store, ["id"] = id };
        b.Append(isPending
            ? req.Ui.ActionButton("✓ Freigeben", new Dictionary<string, string>(f) { ["action"] = "approve" }, "")
            : req.Ui.ActionButton("Verbergen", new Dictionary<string, string>(f) { ["action"] = "unpublish" }, "btn-ghost"));
        b.Append(req.Ui.ActionButton("✕", new Dictionary<string, string>(f) { ["action"] = "delete" }, "btn-danger", "Diese Bewertung wirklich löschen?"));
        b.Append("</div>");
        return b.ToString();
    }

    // Topbar already shows the plugin title ("Bewertungen"), so only a subtitle here.
    var sb = new StringBuilder();
    sb.Append(req.Ui.PageHead("Neue Bewertungen freigeben, verbergen oder löschen."));
    var pendInner = new StringBuilder("<div class='block-list'>");
    if (pend.Count == 0) pendInner.Append("<p class='muted'>Nichts zu prüfen.</p>");
    foreach (var x in pend) pendInner.Append(Card(x.Store, x.R, true));
    pendInner.Append("</div>");
    sb.Append(req.Ui.Card(pendInner.ToString(), "Wartet auf Freigabe (" + pend.Count + ")"));
    var pubInner = new StringBuilder("<div class='block-list'>");
    if (pub.Count == 0) pubInner.Append("<p class='muted'>Noch nichts veröffentlicht.</p>");
    foreach (var x in pub) pubInner.Append(Card(x.Store, x.R, false));
    pubInner.Append("</div>");
    sb.Append(req.Ui.Card(pubInner.ToString(), "Veröffentlicht (" + pub.Count + ")"));
    return sb.ToString();
});

Log("Bewertungen-Plugin geladen. Offen: " + pending);
