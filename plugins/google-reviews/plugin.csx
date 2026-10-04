using System.Text;
using System.Net.Http;
using System.Text.Json.Nodes;

string Enc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
const string CfgKey = "plugin.googlereviews.cfg";
const string ManualKey = "plugin.googlereviews.manual";

// Config lives in SiteSettings (set on this plugin's admin page), falling back to the generic
// key/value plugin config (Config("key")).
string GetCfg(PluginRequest req, string key) {
    try {
        var db = req.Service<AppDbContext>();
        var row = db.SiteSettings.FirstOrDefault(x => x.Key == CfgKey);
        if (row != null && !string.IsNullOrWhiteSpace(row.Value)) {
            var o = JsonNode.Parse(row.Value) as JsonObject;
            var v = o?[key]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
    } catch {}
    return Config(key) ?? "";
}
JsonArray LoadManual(PluginRequest req) {
    try {
        var db = req.Service<AppDbContext>();
        var s = db.SiteSettings.FirstOrDefault(x => x.Key == ManualKey);
        if (s != null && !string.IsNullOrWhiteSpace(s.Value)) return JsonNode.Parse(s.Value) as JsonArray ?? new JsonArray();
    } catch {}
    return new JsonArray();
}
void SaveManual(PluginRequest req, JsonArray arr) {
    var db = req.Service<AppDbContext>();
    var s = db.SiteSettings.FirstOrDefault(x => x.Key == ManualKey);
    if (s == null) db.SiteSettings.Add(new SiteSetting { Key = ManualKey, Value = arr.ToJsonString() });
    else s.Value = arr.ToJsonString();
    db.SaveChanges();
}
string CacheKey(string pid) {
    var s = new string((pid ?? "").Where(c => (c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')).ToArray());
    if (s.Length > 40) s = s.Substring(0, 40);
    return "plugin.googlereviews.cache." + s;
}
string Stars(int r) {
    r = r < 0 ? 0 : (r > 5 ? 5 : r);
    var sb = new StringBuilder("<span class='matg-stars' aria-label='" + r + " von 5'>");
    for (int i = 1; i <= 5; i++) sb.Append(i <= r ? "<span class='on'>★</span>" : "<span class='off'>☆</span>");
    sb.Append("</span>");
    return sb.ToString();
}
string Card(string who, string when, int rating, string txt, string photo) {
    var sb = new StringBuilder("<div class='matg-card'><div class='matg-top'>");
    if (!string.IsNullOrWhiteSpace(photo)) sb.Append("<img class='matg-av' src='").Append(Enc(photo)).Append("' alt='' referrerpolicy='no-referrer'>");
    sb.Append("<div><div class='matg-who'>").Append(Enc(who)).Append("</div><div class='matg-when'>").Append(Enc(when)).Append("</div></div></div>");
    sb.Append(Stars(rating));
    if (!string.IsNullOrWhiteSpace(txt)) sb.Append("<p class='matg-text'>").Append(Enc(txt)).Append("</p>");
    sb.Append("</div>");
    return sb.ToString();
}
string HeadStyle(string font) {
    if (string.IsNullOrWhiteSpace(font)) return "";
    var ok = new string(font.Where(c => char.IsLetterOrDigit(c) || c==' ' || c==',' || c=='-' || c=='\'' || c=='.').ToArray());
    return ok.Length == 0 ? "" : " style=\"font-family:" + ok + "\"";
}
string Wrap(string heading, string headingFont, double overall, int total, string cards, bool hasCards) {
    var sb = new StringBuilder("<div class='matg'><div class='matg-head'>");
    if (!string.IsNullOrWhiteSpace(heading)) sb.Append("<h2").Append(HeadStyle(headingFont)).Append(">").Append(Enc(heading)).Append("</h2>");
    if (overall > 0) sb.Append("<div class='matg-overall'>").Append(Stars((int)Math.Round(overall)))
        .Append("<b>").Append(overall.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append("</b>")
        .Append(total > 0 ? "<span>(" + total + ")</span>" : "").Append("</div>");
    sb.Append("</div><div class='matg-list'>").Append(cards).Append("</div>");
    if (!hasCards) sb.Append("<div class='matg-empty'>Noch keine Bewertungen.</div>");
    sb.Append("<div class='matg-attr'>Bewertungen von Google</div></div>");
    return sb.ToString();
}

AddHeadHtml("<style>" +
  ".matg{max-width:var(--max,1140px);margin:0 auto;padding:0 24px;}" +
  ".matg-head{display:flex;align-items:center;gap:14px;flex-wrap:wrap;margin:0 0 20px;}" +
  ".matg-head h2{margin:0;}" +
  ".matg-overall{display:flex;align-items:center;gap:8px;font-size:15px;color:#4a4137;}" +
  ".matg-overall b{font-size:22px;color:#1a1512;}" +
  ".matg-list{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:20px;}" +
  ".matg-card{background:#fff;border:1px solid #e7ded0;border-radius:12px;padding:18px 18px 16px;box-shadow:0 2px 10px rgba(20,16,14,.05);}" +
  ".matg-top{display:flex;align-items:center;gap:12px;margin-bottom:8px;}" +
  ".matg-av{width:40px;height:40px;border-radius:50%;object-fit:cover;background:#eee;flex:none;}" +
  ".matg-who{font-weight:700;font-size:14px;color:#1a1512;}" +
  ".matg-when{font-size:12px;color:#8a7f72;}" +
  ".matg-stars{font-size:15px;letter-spacing:1px;white-space:nowrap;}.matg-stars .on{color:#e7b41c;}.matg-stars .off{color:#dcd2c2;}" +
  ".matg-text{margin:8px 0 0;font-size:14px;line-height:1.6;color:#3a342e;}" +
  ".matg-attr{margin:18px 0 0;font-size:12px;color:#8a7f72;}" +
  ".matg-embed{border:0;width:100%;height:480px;border-radius:12px;}" +
  ".matg-empty{color:#8a7f72;font-style:italic;padding:16px 0;}" +
  "</style>");

// Admin page: mode switch + Place-ID Finder link + per-mode fields + manual-review manager.
AddAdminMenu("Google Bewertungen", "/admin/plugin/google-reviews", "🗺️");
AddAdminPage("google-reviews", req =>
{
    var db = req.Service<AppDbContext>();
    var msg = "";
    if (req.IsPost)
    {
        var action = req.F("action");
        if (action == "cfg")
        {
            var cfg = new JsonObject {
                ["mode"] = (req.F("mode") ?? "manual").Trim(),
                ["placeId"] = (req.F("placeId") ?? "").Trim(),
                ["apiKey"] = (req.F("apiKey") ?? "").Trim(),
                ["embedKey"] = (req.F("embedKey") ?? "").Trim(),
                ["maxReviews"] = (req.F("maxReviews") ?? "").Trim(),
                ["minRating"] = (req.F("minRating") ?? "").Trim(),
                ["refreshHours"] = (req.F("refreshHours") ?? "").Trim()
            };
            var row = db.SiteSettings.FirstOrDefault(x => x.Key == CfgKey);
            if (row == null) db.SiteSettings.Add(new SiteSetting { Key = CfgKey, Value = cfg.ToJsonString() });
            else row.Value = cfg.ToJsonString();
            // Drop only the API response cache (keep config + manual reviews).
            foreach (var s in db.SiteSettings.Where(x => x.Key.StartsWith("plugin.googlereviews.cache.")).ToList()) db.SiteSettings.Remove(s);
            db.SaveChanges();
            msg = "Konfiguration gespeichert.";
        }
        else if (action == "addrev")
        {
            var arr = LoadManual(req);
            int rr = int.TryParse(req.F("rating"), out var x) ? x : 5;
            if (rr < 1) rr = 1; if (rr > 5) rr = 5;
            arr.Add(new JsonObject {
                ["id"] = DateTime.UtcNow.Ticks,
                ["name"] = (req.F("name") ?? "").Trim(),
                ["rating"] = rr,
                ["text"] = (req.F("text") ?? "").Trim(),
                ["date"] = (req.F("date") ?? "").Trim()
            });
            SaveManual(req, arr);
            msg = "Bewertung hinzugefügt.";
        }
        else if (action == "delrev")
        {
            var arr = LoadManual(req);
            var rid = req.F("rid");
            for (int i = arr.Count - 1; i >= 0; i--) {
                var o = arr[i] as JsonObject;
                if (o != null && (o["id"]?.ToString() ?? "") == rid) arr.RemoveAt(i);
            }
            SaveManual(req, arr);
            msg = "Bewertung gelöscht.";
        }
    }

    string V(string k) => Enc(GetCfg(req, k));
    var mode = GetCfg(req, "mode"); if (string.IsNullOrWhiteSpace(mode)) mode = "manual";
    string Sel(string m) => mode == m ? " selected" : "";
    var modeSel = "<select name='mode'>"
        + "<option value='manual'" + Sel("manual") + ">Manuell (ohne Key)</option>"
        + "<option value='embed'" + Sel("embed") + ">Google-Embed (kostenloser Key)</option>"
        + "<option value='api'" + Sel("api") + ">Places-API (Key + Billing)</option>"
        + "</select>";
    var finder = "<p style='margin:0 0 14px'><a class='btn btn-ghost' href='https://developers.google.com/maps/documentation/places/web-service/place-id' target='_blank' rel='noopener'>🔎 Place-ID Finder öffnen</a> <span class='muted'>Für Embed/Places-API: Ort suchen, die ChIJ…-ID kopieren.</span></p>";
    var cfgInner =
        "<div class='form-field'><label>Modus</label>" + modeSel + "<div class='help'>Manuell = ohne Key (Bewertungen unten pflegen). Embed = kostenloser Maps-Embed-Key. Places-API = Key mit Billing (schöne Karten, live).</div></div>"
        + finder
        + "<div class='form-field'><label>Place-ID <span class='muted'>(Embed + Places-API)</span></label><input name='placeId' value='" + V("placeId") + "' placeholder='ChIJ…' style='width:100%;max-width:520px'></div>"
        + "<div class='form-field'><label>Embed-Key <span class='muted'>(Modus Google-Embed, kostenlos)</span></label><input name='embedKey' value='" + V("embedKey") + "' placeholder='AIza…' style='width:100%;max-width:520px'><div class='help'>Maps Embed API – kostenlos, ohne Billing.</div></div>"
        + "<div class='form-field'><label>API-Key <span class='muted'>(Modus Places-API)</span></label><input name='apiKey' value='" + V("apiKey") + "' placeholder='AIza…' style='width:100%;max-width:520px'><div class='help'>Places API mit aktiviertem Billing.</div></div>"
        + "<div class='form-row'>"
        + "<div class='form-field'><label>Max. Bewertungen</label><input name='maxReviews' value='" + V("maxReviews") + "' placeholder='6' style='width:110px'></div>"
        + "<div class='form-field'><label>Min. Sterne</label><input name='minRating' value='" + V("minRating") + "' placeholder='0' style='width:110px'></div>"
        + "<div class='form-field'><label>Cache (Std.)</label><input name='refreshHours' value='" + V("refreshHours") + "' placeholder='12' style='width:110px'></div>"
        + "</div>"
        + "<div style='margin-top:12px'><button type='submit' class='btn'>Speichern</button></div>";

    var addInner =
        "<div class='form-row'>"
        + "<div class='form-field'><label>Name</label><input name='name' style='width:220px'></div>"
        + "<div class='form-field'><label>Sterne</label><select name='rating'><option>5</option><option>4</option><option>3</option><option>2</option><option>1</option></select></div>"
        + "<div class='form-field'><label>Datum (optional)</label><input name='date' placeholder='Juli 2026' style='width:160px'></div>"
        + "</div>"
        + "<div class='form-field'><label>Text</label><textarea name='text' rows='2' style='width:100%;max-width:640px'></textarea></div>"
        + "<button type='submit' class='btn btn-sm'>Bewertung hinzufügen</button>";

    var manualArr = LoadManual(req);
    var list = new StringBuilder();
    foreach (var n in manualArr) {
        var o = n as JsonObject; if (o == null) continue;
        int rr = 0; try { if (o["rating"] != null) rr = o["rating"].GetValue<int>(); } catch {}
        var rid = o["id"]?.ToString() ?? "";
        list.Append("<div style='border-top:1px solid #eee;padding:10px 0;display:flex;justify-content:space-between;gap:12px;align-items:flex-start'>");
        list.Append("<div>").Append(Stars(rr)).Append(" <strong>").Append(Enc(o["name"]?.GetValue<string>() ?? "")).Append("</strong> <span class='muted'>").Append(Enc(o["date"]?.GetValue<string>() ?? "")).Append("</span>");
        var t = o["text"]?.GetValue<string>() ?? ""; if (!string.IsNullOrWhiteSpace(t)) list.Append("<div style='font-size:14px;color:#3a342e;margin-top:4px'>").Append(Enc(t)).Append("</div>");
        list.Append("</div>");
        list.Append(req.Ui.ActionButton("Löschen", new Dictionary<string, string> { ["action"] = "delrev", ["rid"] = rid }, "btn-danger", "Bewertung wirklich löschen?"));
        list.Append("</div>");
    }
    if (manualArr.Count == 0) list.Append("<p class='muted'>Noch keine manuellen Bewertungen.</p>");

    var sb = new StringBuilder();
    sb.Append(req.Ui.PageHead("Modus wählen und Bewertungen bereitstellen – danach den Block „Google Bewertungen\" auf einer Seite einsetzen."));
    if (!string.IsNullOrWhiteSpace(msg)) sb.Append(req.Ui.Alert(msg, "success"));
    sb.Append(req.Ui.Card(req.Ui.Form(cfgInner, new Dictionary<string, string> { ["action"] = "cfg" }), "Konfiguration"));
    sb.Append(req.Ui.Card(req.Ui.Form(addInner, new Dictionary<string, string> { ["action"] = "addrev" }) + list.ToString(), "Bewertungen (manueller Modus)"));
    return sb.ToString();
});

var blockFields = new JsonArray {
    new JsonObject { ["id"] = "heading", ["label"] = "Überschrift", ["type"] = "text", ["placeholder"] = "z. B. Das sagen unsere Gäste", ["help"] = "Leer lassen = keine Überschrift anzeigen." },
    new JsonObject { ["id"] = "headingFont", ["label"] = "Überschrift-Schriftart", ["type"] = "select", ["help"] = "Schriftart nur für die Überschrift.",
        ["options"] = new JsonArray {
            new JsonObject { ["value"] = "", ["label"] = "Standard (Theme)" },
            new JsonObject { ["value"] = "'Roboto Slab', serif", ["label"] = "Roboto Slab (Serif)" },
            new JsonObject { ["value"] = "'Manrope', sans-serif", ["label"] = "Manrope (Sans-Serif)" },
            new JsonObject { ["value"] = "Georgia, serif", ["label"] = "Georgia (Serif)" },
            new JsonObject { ["value"] = "Arial, Helvetica, sans-serif", ["label"] = "Arial (Sans-Serif)" },
            new JsonObject { ["value"] = "'Courier New', monospace", ["label"] = "Monospace" }
        } }
}.ToJsonString();

AddBlock("googlereviews", "Google Bewertungen", "Zeigt Google-Rezensionen als Karten (manuell / Embed / Places-API).", req =>
{
    try
    {
        // No default heading: empty = the heading is simply not shown (per-block "heading" field).
        string heading = "";
        string headingFont = "";
        try {
            var d = JsonNode.Parse(req.Data) as JsonObject;
            if (d != null) {
                if (d["heading"] != null) { var h = d["heading"].GetValue<string>(); if (!string.IsNullOrWhiteSpace(h)) heading = h; }
                if (d["headingFont"] != null) { var hf = d["headingFont"].GetValue<string>(); if (!string.IsNullOrWhiteSpace(hf)) headingFont = hf; }
            }
        } catch {}

        var mode = GetCfg(req, "mode").Trim().ToLowerInvariant();
        var placeId = GetCfg(req, "placeId").Trim();
        int maxReviews = int.TryParse(GetCfg(req, "maxReviews"), out var mrv) && mrv > 0 ? mrv : 6;
        int minRating = int.TryParse(GetCfg(req, "minRating"), out var mnr) ? mnr : 0;
        int refreshHours = int.TryParse(GetCfg(req, "refreshHours"), out var rfh) && rfh > 0 ? rfh : 12;
        if (mode.Length == 0) mode = GetCfg(req, "apiKey").Trim().Length > 0 ? "api" : "manual";

        // ---- Manual mode: reviews entered in the admin page ----
        if (mode == "manual")
        {
            var arr = LoadManual(req);
            double sum = 0; int cnt = 0, shown = 0;
            var cards = new StringBuilder();
            foreach (var n in arr) {
                var o = n as JsonObject; if (o == null) continue;
                int rr = 0; try { if (o["rating"] != null) rr = o["rating"].GetValue<int>(); } catch {}
                sum += rr; cnt++;
                if (rr < minRating) continue;
                if (shown >= maxReviews) break;
                shown++;
                cards.Append(Card(o["name"]?.GetValue<string>() ?? "", o["date"]?.GetValue<string>() ?? "", rr, o["text"]?.GetValue<string>() ?? "", ""));
            }
            if (cnt == 0)
                return "<div class='matg'><div class='matg-empty'>Google-Bewertungen: Bitte unter Plugins → Google Bewertungen Bewertungen eintragen.</div></div>";
            return Wrap(heading, headingFont, cnt > 0 ? sum / cnt : 0, cnt, cards.ToString(), shown > 0);
        }

        // ---- Embed mode: Google Maps Embed API iframe (free key, no billing) ----
        if (mode == "embed")
        {
            var ek = GetCfg(req, "embedKey").Trim();
            if (placeId.Length == 0 || ek.Length == 0)
                return "<div class='matg'><div class='matg-empty'>Google-Bewertungen: Bitte unter Plugins → Google Bewertungen Place-ID und Embed-Key hinterlegen.</div></div>";
            var src = "https://www.google.com/maps/embed/v1/place?key=" + Uri.EscapeDataString(ek) + "&q=place_id:" + Uri.EscapeDataString(placeId) + "&language=de";
            var sb2 = new StringBuilder("<div class='matg'>");
            if (!string.IsNullOrWhiteSpace(heading)) sb2.Append("<div class='matg-head'><h2").Append(HeadStyle(headingFont)).Append(">").Append(Enc(heading)).Append("</h2></div>");
            sb2.Append("<iframe class='matg-embed' loading='lazy' referrerpolicy='no-referrer-when-downgrade' src='").Append(Enc(src)).Append("'></iframe>");
            sb2.Append("<div class='matg-attr'>Bewertungen von Google</div></div>");
            return sb2.ToString();
        }

        // ---- Places-API mode: fetch + cache + custom cards ----
        var apiKey = GetCfg(req, "apiKey").Trim();
        if (placeId.Length == 0 || apiKey.Length == 0)
            return "<div class='matg'><div class='matg-empty'>Google-Bewertungen: Bitte unter Plugins → Google Bewertungen Place-ID und API-Key hinterlegen.</div></div>";

        var db = req.Service<AppDbContext>();
        var ckey = CacheKey(placeId);
        var row = db.SiteSettings.FirstOrDefault(x => x.Key == ckey);
        JsonObject data = null;
        DateTime at = DateTime.MinValue;
        if (row != null && !string.IsNullOrWhiteSpace(row.Value)) {
            try {
                var c = JsonNode.Parse(row.Value) as JsonObject;
                if (c != null) {
                    DateTime.TryParse(c["at"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out at);
                    var b = c["body"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(b)) data = (JsonNode.Parse(b) as JsonObject)?["result"] as JsonObject;
                }
            } catch {}
        }
        bool stale = data == null || (DateTime.UtcNow - at).TotalHours >= refreshHours;
        if (stale) {
            try {
                var http = req.Service<IHttpClientFactory>().CreateClient();
                http.Timeout = TimeSpan.FromSeconds(6);
                var url = "https://maps.googleapis.com/maps/api/place/details/json?place_id=" + Uri.EscapeDataString(placeId)
                        + "&fields=rating,user_ratings_total,reviews&language=de&reviews_sort=newest&key=" + Uri.EscapeDataString(apiKey);
                var body = http.GetStringAsync(url).GetAwaiter().GetResult();
                var parsed = JsonNode.Parse(body) as JsonObject;
                var status = parsed?["status"]?.GetValue<string>() ?? "";
                if (status == "OK" && parsed?["result"] is JsonObject res) {
                    data = res;
                    var save = new JsonObject { ["at"] = DateTime.UtcNow.ToString("o"), ["body"] = body };
                    if (row == null) db.SiteSettings.Add(new SiteSetting { Key = ckey, Value = save.ToJsonString() });
                    else row.Value = save.ToJsonString();
                    db.SaveChanges();
                } else { req.Log("Google Reviews API: " + status); }
            } catch (Exception ex) { req.Log("Google Reviews Fehler: " + ex.Message); }
        }

        if (data == null)
            return "<div class='matg'><div class='matg-empty'>Google-Bewertungen konnten nicht geladen werden.</div></div>";

        double overall = 0; try { if (data["rating"] != null) overall = data["rating"].GetValue<double>(); } catch {}
        int total = 0; try { if (data["user_ratings_total"] != null) total = data["user_ratings_total"].GetValue<int>(); } catch {}
        var reviews = data["reviews"] as JsonArray ?? new JsonArray();
        var cardsApi = new StringBuilder();
        int shownApi = 0;
        foreach (var rvNode in reviews) {
            var o = rvNode as JsonObject; if (o == null) continue;
            int rr = 0; try { if (o["rating"] != null) rr = o["rating"].GetValue<int>(); } catch {}
            if (rr < minRating) continue;
            if (shownApi >= maxReviews) break;
            shownApi++;
            cardsApi.Append(Card(o["author_name"]?.GetValue<string>() ?? "", o["relative_time_description"]?.GetValue<string>() ?? "", rr, o["text"]?.GetValue<string>() ?? "", o["profile_photo_url"]?.GetValue<string>() ?? ""));
        }
        return Wrap(heading, headingFont, overall, total, cardsApi.ToString(), shownApi > 0);
    }
    catch (Exception ex) { return "<div class='matg'><div class='matg-empty'>Google-Bewertungen: " + System.Net.WebUtility.HtmlEncode(ex.Message) + "</div></div>"; }
}, blockFields);
