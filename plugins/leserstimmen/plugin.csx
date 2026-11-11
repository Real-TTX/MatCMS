using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

// Leserstimmen — visitor reviews with a 1–5 rating (stars, hearts, flames, dots — or none at all), shown as
// cards, a quote carousel, a list or a ticker, with a submission form, an average, moderation in the admin
// and a mail notification on new reviews.
//
// Its own key on purpose: MatCMS seeds a plugin "bewertungen" and rewrites that one's code on every
// start, so an improved version under the same key would silently be reverted by the next restart.
// Storage is its own too (SiteSettings "plugin.leserstimmen.<collection>") so the two never mix.
//
// Configuration (Plugins → Leserstimmen → Konfiguration, all optional):
//   autoPublish = true      publish without moderation
//   notifyEmail = a@b.de    legacy: used only until the notification is set up on the admin page
// The notification itself (on/off, recipients, when) lives in SiteSettings "plugin.leserstimmen-notify" —
// deliberately NOT under Prefix, which is where the collections are listed from.

const string Prefix = "plugin.leserstimmen.";
const int MaxPerStore = 1000;   // an anonymous endpoint must not grow storage without bound
const int MinSeconds = 3;       // a form sent faster than this after rendering came from a script
const string NotifyKey = "plugin.leserstimmen-notify";
const string AdminUrl = "/admin/plugin/leserstimmen";

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
int Rating(JsonNode n) { long r; try { r = n?["rating"]?.GetValue<int>() ?? 5; } catch { r = L(n, "rating"); } return (int)Math.Clamp(r, 0, 5); }
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
// ---- texts: German and English ---------------------------------------------------------------
// Key, German, English. A key ending in ".sie"/".du" exists twice for the block's form of address (English has one form).
// The back office and the block follow the request's language (the admin's choice, the page's /en/ prefix); the mail about a new
// review goes out in the site's default language. Texts an editor typed into a block (heading, button …) are shown as typed.
(string Key, string De, string En)[] LsRows =
{
    ("ls.nav", "Leserstimmen", "Reviews"),
    ("ls.block.name", "Leserstimmen", "Reviews"),
    ("ls.block.desc", "Rezensionen und Kundenstimmen – als Karten, großes Zitat, Liste oder Laufband, mit Sternen, Herzen, Punkten oder ohne Wertung, Durchschnitt und Formular.",
        "Reviews and testimonials – as cards, a large quote, a list or a ticker, with stars, hearts, dots or no rating, an average and a form."),
    ("ls.f.heading", "Überschrift", "Heading"),
    ("ls.f.store", "Sammlung", "Collection"),
    ("ls.f.storeHelp", "Name der Liste, z. B. ein Buchtitel. Blöcke mit derselben Sammlung zeigen dieselben Stimmen.", "Name of the list, e.g. a book title. Blocks with the same collection show the same reviews."),
    ("ls.f.layout", "Darstellung", "Layout"),
    ("ls.o.karten", "Karten (klassisch)", "Cards (classic)"),
    ("ls.o.elegant", "Karten mit Zitatzeichen und Initialen", "Cards with quotation marks and initials"),
    ("ls.o.zitat", "Großes Zitat, eins nach dem anderen", "Large quote, one after the other"),
    ("ls.o.liste", "Schlichte Liste", "Simple list"),
    ("ls.o.laufband", "Laufband", "Ticker"),
    ("ls.f.look", "Farben", "Colours"),
    ("ls.o.template", "Wie das Template", "Like the template"),
    ("ls.o.hell", "Hell", "Light"),
    ("ls.o.dunkel", "Dunkel", "Dark"),
    ("ls.o.akzent", "Akzentfarbe", "Accent colour"),
    ("ls.f.symbol", "Wertung mit", "Rating with"),
    ("ls.o.stern", "Sternen", "Stars"),
    ("ls.o.herz", "Herzen", "Hearts"),
    ("ls.o.punkt", "Punkten", "Dots"),
    ("ls.o.flamme", "Flammen", "Flames"),
    ("ls.o.keine", "Ohne Wertung", "No rating"),
    ("ls.f.address", "Anrede", "Form of address"),
    ("ls.o.sie", "Sie", "Formal (German “Sie”)"),
    ("ls.o.du", "du", "Informal (German “du”)"),
    ("ls.f.addressHelp", "Wie das Formular die Besucher anspricht.", "How the form addresses visitors – only German has two forms, English pages read the same either way."),
    ("ls.f.columns", "Spalten", "Columns"),
    ("ls.f.showAverage", "Durchschnitt zeigen", "Show average"),
    ("ls.f.showForm", "Formular zeigen", "Show form"),
    ("ls.o.ja", "Ja", "Yes"),
    ("ls.o.nein", "Nein", "No"),
    ("ls.f.askLink", "Link-Feld (z. B. Instagram, Blog)", "Link field (e.g. Instagram, blog)"),
    ("ls.f.askEmail", "E-Mail-Feld (wird nie gezeigt)", "E-mail field (never shown)"),
    ("ls.o.optional", "Optional", "Optional"),
    ("ls.o.pflicht", "Pflicht", "Required"),
    ("ls.f.formTitle", "Formular-Überschrift", "Form heading"),
    ("ls.f.formTitleHelp", "Leer = „Ihre Bewertung abgeben“ (bei du: „Deine Bewertung“), in der Sprache der Seite.", "Empty = “Submit your review”, in the language of the page."),
    ("ls.f.intro", "Hinweis über dem Formular", "Note above the form"),
    ("ls.f.introHelp", "z. B. Einverständnis zur Veröffentlichung", "e.g. consent to publication"),
    ("ls.f.buttonText", "Button-Text", "Button text"),
    ("ls.f.buttonTextHelp", "Leer = „Bewertung absenden“, in der Sprache der Seite.", "Empty = “Submit review”, in the language of the page."),

    ("ls.avg", "{0} von 5 · {1}", "{0} out of 5 · {1}"),
    ("ls.votes.1", "{0} Stimme", "{0} review"),
    ("ls.votes.n", "{0} Stimmen", "{0} reviews"),
    ("ls.thanks.sie", "Vielen Dank für Ihre Bewertung!", "Thank you for your review!"),
    ("ls.thanks.du", "Danke für deine Bewertung!", "Thanks for your review!"),
    ("ls.later.sie", "Sie erscheint nach einer kurzen Prüfung.", "It will appear after a short check."),
    ("ls.later.du", "Sie erscheint, sobald ich sie freigegeben habe.", "It will appear as soon as I have approved it."),
    ("ls.empty.sie", "Noch keine Bewertungen – seien Sie die oder der Erste!", "No reviews yet – be the first!"),
    ("ls.empty.du", "Noch keine Stimmen – sei die oder der Erste!", "No reviews yet – be the first!"),
    ("ls.slider", "Stimmen, zum Blättern wischen", "Reviews, swipe to browse"),
    ("ls.prev", "Vorherige Stimme", "Previous review"),
    ("ls.next", "Nächste Stimme", "Next review"),
    ("ls.slide", "Stimme {0}", "Review {0}"),
    ("ls.symLabel", "{0} von 5 {1}", "{0} out of 5 {1}"),
    ("ls.n.stern.1", "Stern", "star"), ("ls.n.stern.n", "Sterne", "stars"),
    ("ls.n.herz.1", "Herz", "heart"), ("ls.n.herz.n", "Herzen", "hearts"),
    ("ls.n.punkt.1", "Punkt", "dot"), ("ls.n.punkt.n", "Punkte", "dots"),
    ("ls.n.flamme.1", "Flamme", "flame"), ("ls.n.flamme.n", "Flammen", "flames"),
    ("ls.formTitle.sie", "Ihre Bewertung abgeben", "Submit your review"),
    ("ls.formTitle.du", "Deine Bewertung", "Your review"),
    ("ls.name.sie", "Name", "Name"),
    ("ls.name.du", "Name oder Account", "Name or account"),
    ("ls.namePh.sie", "Ihr Name", "Your name"),
    ("ls.namePh.du", "z. B. @buecherliebe", "e.g. @booklover"),
    ("ls.email", "E-Mail", "E-mail"),
    ("ls.emailNote.req", "(wird nicht veröffentlicht)", "(not published)"),
    ("ls.emailNote.opt", "(wird nicht veröffentlicht, optional)", "(not published, optional)"),
    ("ls.legend.sie", "Ihre {0}", "Your {0}"),
    ("ls.legend.du", "Deine {0}", "Your {0}"),
    ("ls.link.sie", "Link zu Ihrem Beitrag", "Link to your post"),
    ("ls.link.du", "Link zu deinem Beitrag", "Link to your post"),
    ("ls.optional", "(optional)", "(optional)"),
    ("ls.linkPh", "Instagram, Blog, Shop …", "Instagram, blog, shop …"),
    ("ls.text.sie", "Ihre Bewertung", "Your review"),
    ("ls.text.du", "Deine Bewertung", "Your review"),
    ("ls.textPh.sie", "Was hat Ihnen gefallen?", "What did you like?"),
    ("ls.textPh.du", "Wie hat es dir gefallen?", "How did you like it?"),
    ("ls.button", "Bewertung absenden", "Submit review"),

    ("ls.mail.collection", "Leserstimmen · Sammlung „{0}“", "Reviews · collection “{0}”"),
    ("ls.mail.heading", "Neue Stimme von {0}", "New review from {0}"),
    ("ls.mail.email", "E-Mail: {0}", "E-mail: {0}"),
    ("ls.mail.link", "Link: {0}", "Link: {0}"),
    ("ls.mail.published", "Sie ist bereits veröffentlicht.", "It is already published."),
    ("ls.mail.waiting", "Sie wartet auf deine Freigabe.", "It is waiting for your approval."),
    ("ls.mail.open", "Leserstimmen öffnen", "Open reviews"),
    ("ls.mail.check", "Jetzt prüfen und freigeben", "Review and approve now"),
    ("ls.mail.subjPublished", "Neue Leserstimme von {0}", "New review from {0}"),
    ("ls.mail.subjPending", "Neue Leserstimme zur Freigabe: {0}", "New review awaiting approval: {0}"),
    ("ls.test.subject", "Test: Leserstimmen-Benachrichtigung", "Test: review notification"),
    ("ls.test.text", "So sieht die Benachrichtigung aus, wenn jemand eine Stimme abgibt. Diese hier ist nur ein Test.", "This is what the notification looks like when someone submits a review. This one is only a test."),
    ("ls.test.noRecipient", "Test-Mail nicht gesendet: kein Empfänger eingetragen.", "Test mail not sent: no recipient entered."),
    ("ls.test.noService", "kein Mail-Dienst", "no mail service"),
    ("ls.test.sent", "Test-Mail an {0} gesendet.", "Test mail sent to {0}."),
    ("ls.test.failed", "Test-Mail fehlgeschlagen: {0}", "Test mail failed: {0}"),

    ("ls.a.sub", "Neue Stimmen freigeben, verbergen oder löschen – oder eine Stimme von Hand eintragen.", "Approve, hide or delete new reviews – or enter one by hand."),
    ("ls.a.auto", "Neue Stimmen erscheinen sofort (autoPublish).", "New reviews appear at once (autoPublish)."),
    ("ls.a.pending", "Wartet auf Freigabe ({0})", "Awaiting approval ({0})"),
    ("ls.a.nothingPending", "Nichts zu prüfen.", "Nothing to review."),
    ("ls.a.published", "Veröffentlicht ({0})", "Published ({0})"),
    ("ls.a.nothingPublished", "Noch nichts veröffentlicht.", "Nothing published yet."),
    ("ls.a.ofFive", "{0} von 5", "{0} out of 5"),
    ("ls.a.approve", "Freigeben", "Approve"),
    ("ls.a.hide", "Verbergen", "Hide"),
    ("ls.a.delete", "Löschen", "Delete"),
    ("ls.a.deleteConfirm", "Diese Leserstimme wirklich löschen?", "Really delete this review?"),
    ("ls.a.thRating", "Wertung", "Rating"),
    ("ls.a.thName", "Name", "Name"),
    ("ls.a.thText", "Text", "Text"),
    ("ls.a.thStore", "Sammlung", "Collection"),
    ("ls.a.thDate", "Datum", "Date"),
    ("ls.a.storeHelp", "Muss zur Sammlung im Block passen.", "Must match the collection in the block."),
    ("ls.a.link", "Link (optional)", "Link (optional)"),
    ("ls.a.add", "Hinzufügen", "Add"),
    ("ls.a.addTitle", "Von Hand hinzufügen", "Add by hand"),
    ("ls.a.noMail", "Diese Seite kann noch keine Mails versenden. Unter Einstellungen → E-Mail den Versand einrichten (oder über die Cloud).",
        "This site cannot send mail yet. Set up sending under Settings → E-mail (or through the cloud)."),
    ("ls.a.notifyOn", "Bei neuen Stimmen per E-Mail benachrichtigen", "Notify by e-mail about new reviews"),
    ("ls.a.when", "Wann", "When"),
    ("ls.a.whenAll", "Bei jeder neuen Stimme", "For every new review"),
    ("ls.a.whenPending", "Nur wenn eine Freigabe nötig ist", "Only when approval is needed"),
    ("ls.a.users", "Empfänger aus den Benutzern", "Recipients from the users"),
    ("ls.a.more", "Weitere Adressen", "Further addresses"),
    ("ls.a.morePh", "eine pro Zeile", "one per line"),
    ("ls.a.moreHelp", "Eine Adresse pro Zeile.", "One address per line."),
    ("ls.a.save", "Speichern", "Save"),
    ("ls.a.test", "Test-Mail senden", "Send test mail"),
    ("ls.a.notifyTitle", "Benachrichtigung", "Notification"),
    ("ls.a.lastTest", "Letzter Test ({0}): {1}", "Last test ({0}): {1}"),
    ("ls.a.importOne", "Auf dieser Seite liegt noch eine Stimme aus dem früheren Plugin „Bewertungen“.", "There is still one review from the former plugin “Bewertungen” on this site."),
    ("ls.a.importMany", "Auf dieser Seite liegen noch {0} Stimmen aus dem früheren Plugin „Bewertungen“.", "There are still {0} reviews from the former plugin “Bewertungen” on this site."),
    ("ls.a.importHow", "Sie werden mit ihrer Sammlung und ihrem Freigabe-Status übernommen; danach auf der Seite den Block „Bewertungen“ durch „Leserstimmen“ ersetzen.",
        "They are taken over with their collection and approval status; afterwards replace the block “Bewertungen” with “Reviews” on the page."),
    ("ls.a.importBtn", "Übernehmen", "Take over"),
    ("ls.a.importTitle", "Aus „Bewertungen“ übernehmen", "Take over from “Bewertungen”"),
};
var LsDe = LsRows.ToDictionary(r => r.Key, r => r.De);
var LsEn = LsRows.ToDictionary(r => r.Key, r => r.En);
// The menu label carries the number of reviews waiting ("Leserstimmen (2)"). The host translates a label only when it is a key, so
// the counted labels are keys too (ls.nav.2), made here instead of written out.
const int MaxBadge = 999;
for (int i = 1; i <= MaxBadge; i++) { LsDe["ls.nav." + i] = "Leserstimmen (" + i + ")"; LsEn["ls.nav." + i] = "Reviews (" + i + ")"; }
LsDe["ls.nav.more"] = "Leserstimmen (" + MaxBadge + "+)"; LsEn["ls.nav.more"] = "Reviews (" + MaxBadge + "+)";
// Registered with the host: it translates the menu label and the block's name, fields and options (all keys of these tables).
Plugin.Strings("de", LsDe);
Plugin.Strings("en", LsEn);

// German or English for a culture: as itself, else the site's default language, else English (as the host's own lookup does).
string Lang(string culture)
{
    var c = (culture ?? "").Split('-')[0].ToLowerInvariant();
    if (c is "de" or "en") return c;
    return Localizer.DefaultCulture is "de" or "en" ? Localizer.DefaultCulture : "en";
}
// The request's language: the admin's choice in the back office, the page's language on the site.
string ReqLang() => Lang(System.Globalization.CultureInfo.CurrentUICulture.Name);
// The site's default language — for mail, which is read by the operator and not tied to a page.
string SiteLang() => Lang(Localizer.DefaultCulture);
string TIn(string lang, string key, params object[] args)
{
    var v = (lang == "en" ? LsEn : LsDe).TryGetValue(key, out var x) ? x : LsDe.TryGetValue(key, out var d) ? d : key;
    return args.Length > 0 ? string.Format(v, args) : v;
}
string T(string key, params object[] args) => TIn(ReqLang(), key, args);
// The form of address: key + .sie or key + .du.
string TA(bool du, string key, params object[] args) => T(key + (du ? ".du" : ".sie"), args);

// MatCMS runs with InvariantGlobalization: "de-DE" exists but carries invariant data, so it would
// print "4.8" and "October". German formatting is therefore done by hand; the invariant culture's own is English.
string[] Months = { "Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember" };
string Month(DateTime d) => ReqLang() == "en" ? d.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : Months[d.Month - 1] + " " + d.Year;
string Day(DateTime d) => d.ToString(ReqLang() == "en" ? "yyyy-MM-dd" : "dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);
bool AutoPublish() => string.Equals(Config("autoPublish"), "true", StringComparison.OrdinalIgnoreCase);

// ---- symbols: inline SVG in currentColor, so the theme colours them (an emoji ignores CSS) ----
string SymPath(string sym) => sym switch
{
    "flamme" => "M12 2c.6 3.2 2.4 5.2 4.1 7.1C17.7 10.9 19 12.7 19 15.2 19 19 15.9 22 12 22s-7-3-7-6.8c0-2.4 1.2-4.2 2.7-5.6.2 1.7 1 2.9 2.2 3.5-.4-2.9.4-5.8 3.1-9.1z",
    "punkt" => "M12 5.5a6.5 6.5 0 1 0 0 13a6.5 6.5 0 1 0 0-13z",
    "herz" => "M12 21s-7.5-4.6-9.6-9.3C1 8.3 3 4.5 6.6 4.5c2.1 0 3.6 1.2 4.4 2.6.8-1.4 2.3-2.6 4.4-2.6 3.6 0 5.6 3.8 4.2 7.2C19.5 16.4 12 21 12 21z",
    _ => "M12 2.6l2.9 6 6.6.9-4.8 4.6 1.2 6.5L12 17.5l-5.9 3.1 1.2-6.5L2.5 9.5l6.6-.9z"
};
string Svg(string sym) => "<svg viewBox='0 0 24 24' aria-hidden='true' focusable='false'><path d='" + SymPath(sym) + "'/></svg>";
string Noun(string sym, int n) => T("ls.n." + (sym is "flamme" or "herz" or "punkt" ? sym : "stern") + (n == 1 ? ".1" : ".n"));
string Symbols(int r, string sym)
{
    if (sym == "keine" || r < 1) return "";
    var sb = new StringBuilder("<span class='mls-sym' role='img' aria-label='" + Enc(T("ls.symLabel", r, Noun(sym, 5))) + "'>");
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

// ---- notification ------------------------------------------------------------------------------
// Settings: { on, when: "all"|"pending", users: [ids], emails: "one per line" }. Without a saved row the
// plugin behaves as before 1.3: mail to the notifyEmail config, else the site's contact recipient.
JsonObject NotifySettings(AppDbContext db)
{
    var row = db?.SiteSettings.FirstOrDefault(x => x.Key == NotifyKey);
    if (row != null && !string.IsNullOrWhiteSpace(row.Value)) { try { if (JsonNode.Parse(row.Value) is JsonObject o) return o; } catch { } }
    return null;
}
void SaveNotifySettings(AppDbContext db, JsonObject o)
{
    var row = db.SiteSettings.FirstOrDefault(x => x.Key == NotifyKey);
    if (row == null) db.SiteSettings.Add(new SiteSetting { Key = NotifyKey, Value = o.ToJsonString() });
    else row.Value = o.ToJsonString();
    db.SaveChanges();
}
bool NotifyOn(JsonObject ns) { try { return ns?["on"]?.GetValue<bool>() ?? false; } catch { return false; } }
List<string> SplitMails(string s) => (s ?? "").Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Where(x => x.Contains('@') && x.Length <= 200).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
List<int> UserIds(JsonObject o) { var l = new List<int>(); try { if (o?["users"] is JsonArray a) foreach (var n in a) l.Add(n.GetValue<int>()); } catch { } return l; }
List<string> Recipients(AppDbContext db, JsonObject ns)
{
    if (ns == null)
    {
        var legacy = Config("notifyEmail");
        if (string.IsNullOrWhiteSpace(legacy)) legacy = db?.SiteSettings.FirstOrDefault(x => x.Key == "ContactRecipient")?.Value ?? "";
        return SplitMails(legacy);
    }
    var ids = UserIds(ns);
    var list = db.Users.Where(u => ids.Contains(u.Id) && u.Email != null).Select(u => u.Email).ToList();
    list.AddRange(SplitMails(S(ns, "emails")));
    return list.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
// The site's own address for the link in the mail. The request carries it; behind a proxy MatCMS has
// already applied the forwarded headers.
string SiteBase(IServiceProvider sp)
{
    var http = sp.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()?.HttpContext;
    return http == null ? "" : http.Request.Scheme + "://" + http.Request.Host.Value;
}
string MailHtml(string lang, string siteBase, string name, int rating, string email, string text, string link, bool published, string store)
{
    string E(string x) => System.Net.WebUtility.HtmlEncode(x ?? "");
    // The wording is encoded first, then the (already encoded or built) values go into its {0}.
    string M(string key, string html) => string.Format(E(TIn(lang, key)), html);
    var stars = rating > 0 ? new string('★', rating) + new string('☆', 5 - rating) : "";
    return "<div style='font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:0 auto;color:#222'>" +
        "<p style='font-size:13px;color:#888;margin:0 0 6px'>" + M("ls.mail.collection", E(store)) + "</p>" +
        "<h2 style='margin:0 0 14px;font-size:20px'>" + M("ls.mail.heading", E(name)) + "</h2>" +
        (stars.Length > 0 ? "<p style='font-size:22px;letter-spacing:2px;color:#c98a2b;margin:0 0 12px'>" + stars + "</p>" : "") +
        "<blockquote style='margin:0 0 16px;padding:14px 18px;border-left:4px solid #c98a2b;background:#f7f4ef;font-size:15px;line-height:1.6'>" + E(text).Replace("\n", "<br>") + "</blockquote>" +
        (email.Length > 0 ? "<p style='margin:0 0 6px;font-size:14px'>" + M("ls.mail.email", E(email)) + "</p>" : "") +
        (link.Length > 0 ? "<p style='margin:0 0 6px;font-size:14px'>" + M("ls.mail.link", "<a href='" + E(link) + "'>" + E(link) + "</a>") + "</p>" : "") +
        "<p style='margin:18px 0'>" + E(TIn(lang, published ? "ls.mail.published" : "ls.mail.waiting")) + "</p>" +
        (siteBase.Length > 0 ? "<p><a href='" + E(siteBase + AdminUrl) + "' style='display:inline-block;background:#222;color:#fff;text-decoration:none;padding:11px 20px;border-radius:8px;font-weight:600'>" +
            E(TIn(lang, published ? "ls.mail.open" : "ls.mail.check")) + "</a></p>" : "") +
        "</div>";
}
// Mail goes out after the response on its own scope: the request's services are disposed by then,
// and a slow SMTP server must not hold up the visitor's redirect.
void SendMail(IServiceProvider sp, List<string> to, string subject, string html)
{
    if (to.Count == 0) return;
    var scopes = sp.GetRequiredService<IServiceScopeFactory>();
    _ = Task.Run(async () =>
    {
        try
        {
            using var scope = scopes.CreateScope();
            var mail = scope.ServiceProvider.GetService<EmailService>();
            if (mail != null) await mail.SendAsync(to, subject, html, null, true);
        }
        catch { }
    });
}
void Notify(IServiceProvider sp, string name, int rating, string email, string text, string link, bool published, string store)
{
    var db = sp.GetService<AppDbContext>();
    var ns = NotifySettings(db);
    if (ns != null && (!NotifyOn(ns) || (S(ns, "when") == "pending" && published))) return;
    var lang = SiteLang();
    SendMail(sp, Recipients(db, ns), TIn(lang, published ? "ls.mail.subjPublished" : "ls.mail.subjPending", name),
        MailHtml(lang, SiteBase(sp), name, rating, email, text, link, published, store));
}
// The admin menu shows how many reviews wait: "Leserstimmen (2)". The registry entry is replaced in place
// after every change, so the number is current without re-running every plugin. The label is a key (see LsRows).
string MenuLabel(AppDbContext db) { var n = db == null ? 0 : All(db).Count(x => !Approved(x.R)); return n <= 0 ? "ls.nav" : n <= MaxBadge ? "ls.nav." + n : "ls.nav.more"; }
void RefreshBadge(PluginRegistry reg, AppDbContext db)
{
    if (reg == null || db == null) return;
    for (int i = 0; i < reg.AdminMenu.Count; i++)
        if (reg.AdminMenu[i].Url == AdminUrl) { reg.AdminMenu[i] = new PluginRegistry.AdminMenuEntry(MenuLabel(db), AdminUrl, "ti-message-heart"); return; }
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

AddHeadHtml("<style>" + DesignCss + "</style>");
AddBodyHtml("<script>" + SliderJs + "</script>");
AddAdminMenu(MenuLabel(Service<AppDbContext>()), AdminUrl, "ti-message-heart");

// ---- designs: everything below is scoped to .mls--<design> / .mls--<look>, the classic cards stay as they were
const string DesignCss = """
.mls--hell :is(.mls-card,.mls-c,.mls-form,.mls-ok,.mls-slider__btn){--mls-card:#fff;--mls-text:#444;--mls-head:#151515;--mls-line:rgba(0,0,0,.1);--mls-off:rgba(0,0,0,.18);--mls-field:#fff}
.mls--dunkel :is(.mls-card,.mls-c,.mls-form,.mls-ok,.mls-slider__btn){--mls-card:#1d1b22;--mls-text:#d6d2dc;--mls-head:#fff;--mls-line:rgba(255,255,255,.12);--mls-off:rgba(255,255,255,.22);--mls-field:#141218}
.mls--akzent :is(.mls-card,.mls-c,.mls-form,.mls-ok,.mls-slider__btn){--mls-card:color-mix(in srgb,var(--mls-accent) 11%,var(--bg,#fff));--mls-line:color-mix(in srgb,var(--mls-accent) 30%,transparent);--mls-off:color-mix(in srgb,var(--mls-accent) 28%,transparent)}
.mls--hell .mls-c--row,.mls--dunkel .mls-c--row,.mls--akzent .mls-c--row{padding:24px 22px;margin:0 0 10px;border:1px solid var(--mls-line);border-radius:var(--mls-radius);background:var(--mls-card)}
.mls-c{position:relative;break-inside:avoid;margin:0 0 24px;background:var(--mls-card);border:1px solid var(--mls-line);border-radius:calc(var(--mls-radius) + 4px);padding:30px 26px 22px;color:var(--mls-text);transition:transform .25s ease,box-shadow .25s ease}
.mls-c:hover{transform:translateY(-3px);box-shadow:0 18px 40px rgba(0,0,0,.12)}
.mls-c__q{position:absolute;left:20px;top:2px;font:700 64px/1 Georgia,serif;color:var(--mls-accent);opacity:.35;pointer-events:none}
.mls-c blockquote{margin:14px 0 18px;padding:0;border:0;font-family:var(--mls-quote-font);font-size:16px;line-height:1.7;color:var(--mls-text);overflow-wrap:anywhere}
.mls-c figcaption{display:flex;align-items:center;gap:12px;margin:0;padding-top:14px;border-top:1px solid var(--mls-line)}
.mls-c__av{flex:none;width:40px;height:40px;border-radius:50%;display:grid;place-items:center;font-weight:700;font-size:16px;color:#fff;background:hsl(var(--h) 42% 46%)}
.mls-c__who{flex:1;min-width:0;display:flex;flex-direction:column;line-height:1.35}
.mls-c__who b{color:var(--mls-head);font-weight:600;font-size:15px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.mls-c__who small{font-size:12.5px;opacity:.7}
.mls-c__who a{color:var(--mls-accent);text-decoration:none}
.mls-c__r .mls-sym svg{width:15px;height:15px}
.mls-rows{max-width:860px;margin:0 auto 40px;border-top:1px solid var(--mls-line)}
.mls-c--row{display:grid;grid-template-columns:220px 1fr;gap:6px 30px;margin:0;padding:24px 4px;border:0;border-bottom:1px solid var(--mls-line);border-radius:0;background:none}
.mls-c--row:hover{transform:none;box-shadow:none}
.mls-c--row .mls-c__q{display:none}
.mls-c--row blockquote{grid-column:2;grid-row:1;margin:0}
.mls-c--row figcaption{grid-column:1;grid-row:1;align-self:start;flex-wrap:wrap;border:0;padding:0}
.mls-c--row .mls-c__r{flex-basis:100%}
@media(max-width:700px){.mls-c--row{grid-template-columns:1fr}.mls-c--row blockquote,.mls-c--row figcaption{grid-column:1;grid-row:auto}}
.mls-slider{max-width:820px;margin:0 auto 44px}
.mls-slider__track{display:flex;overflow-x:auto;scroll-snap-type:x mandatory;scroll-behavior:smooth;scrollbar-width:none;outline:none}
.mls-slider__track::-webkit-scrollbar{display:none}
.mls-c--slide{flex:0 0 100%;scroll-snap-align:center;margin:0;text-align:center;padding:44px 40px 30px;border-radius:calc(var(--mls-radius) + 8px)}
.mls-c--slide:hover{transform:none;box-shadow:none}
.mls-c--slide .mls-c__q{left:50%;transform:translateX(-50%);top:-4px;font-size:84px}
.mls-c--slide blockquote{font-size:clamp(17px,2.2vw,21px);line-height:1.6;margin:18px auto 22px;max-width:640px}
.mls-c--slide figcaption{justify-content:center;border:0;padding:0}
.mls-c--slide .mls-c__who{flex:0 1 auto;text-align:left}
.mls-slider__nav{display:flex;align-items:center;justify-content:center;gap:14px;margin-top:18px}
.mls-slider__btn{width:40px;height:40px;border-radius:50%;border:1px solid var(--mls-line);background:var(--mls-card);color:var(--mls-head);font-size:22px;line-height:1;cursor:pointer}
.mls-slider__btn:hover{border-color:var(--mls-accent);color:var(--mls-accent)}
.mls-slider__dots{display:flex;gap:8px}
.mls-slider__dots button{width:9px;height:9px;padding:0;border-radius:50%;border:0;background:var(--mls-off);cursor:pointer;transition:transform .2s,background .2s}
.mls-slider__dots button[aria-current]{background:var(--mls-accent);transform:scale(1.35)}
.mls-marquee{overflow:hidden;margin:0 -24px 44px;-webkit-mask-image:linear-gradient(90deg,transparent,#000 8%,#000 92%,transparent);mask-image:linear-gradient(90deg,transparent,#000 8%,#000 92%,transparent)}
.mls-marquee__track{display:flex;gap:22px;width:max-content;animation:mls-tick var(--mls-dur,40s) linear infinite}
.mls-marquee__dup{display:contents}
.mls-marquee:hover .mls-marquee__track{animation-play-state:paused}
.mls-c--tick{width:340px;flex:none;margin:0}
.mls-c--tick blockquote{display:-webkit-box;-webkit-line-clamp:6;-webkit-box-orient:vertical;overflow:hidden}
@keyframes mls-tick{to{transform:translateX(calc(-50% - 11px))}}
@media(prefers-reduced-motion:reduce){.mls-marquee{overflow-x:auto}.mls-marquee__track{animation:none}.mls-marquee__dup{display:none}.mls-c{transition:none}}
""";
const string SliderJs = """
(function(){if(window.__mlsSlider)return;window.__mlsSlider=1;
function init(root){if(root.__mls)return;root.__mls=1;var tr=root.querySelector('.mls-slider__track');if(!tr)return;
var dots=[].slice.call(root.querySelectorAll('.mls-slider__dots button')),n=tr.children.length,i=0,t=null,rm=window.matchMedia&&matchMedia('(prefers-reduced-motion: reduce)').matches;
function go(k){i=(k+n)%n;tr.scrollTo({left:tr.clientWidth*i});mark();}
function mark(){dots.forEach(function(d,j){if(j===i)d.setAttribute('aria-current','true');else d.removeAttribute('aria-current');});}
tr.addEventListener('scroll',function(){var k=Math.round(tr.scrollLeft/Math.max(1,tr.clientWidth));if(k!==i){i=k;mark();}},{passive:true});
root.querySelectorAll('[data-dir]').forEach(function(b){b.addEventListener('click',function(){go(i+ +b.getAttribute('data-dir'));});});
dots.forEach(function(d,j){d.addEventListener('click',function(){go(j);});});
function start(){if(rm||n<2)return;stop();t=setInterval(function(){go(i+1);},7000);}function stop(){if(t)clearInterval(t);t=null;}
root.addEventListener('mouseenter',stop);root.addEventListener('mouseleave',start);root.addEventListener('focusin',stop);start();}
function scan(){document.querySelectorAll('[data-mls-slider]').forEach(init);}
if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',scan);else scan();})();
""";

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
    // A block set to "ohne Wertung" sends nr=1 and no rating; it is stored as 0 and never counted in an average.
    if (req.F("nr") == "1") rating = 0;
    else if (rating < 1 || rating > 5) return "";
    if (name.Length == 0 || text.Length == 0) return "";
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
    req.Log("Neue Leserstimme (" + (rating > 0 ? rating + "/5" : "ohne Wertung") + ") von " + name + (auto ? " – veröffentlicht" : " – wartet auf Freigabe"));
    Notify(req.Services, name, rating, email, text, link, auto, store);
    RefreshBadge(req.Registry, db);
    return "";
});

// ---- the block ------------------------------------------------------------------------------
// Labels, help texts and option labels are KEYS of LsRows: the editor shows them in the admin's language. A placeholder is not
// translated by the host, so the defaults are named in the help texts instead. Values (karten, sie, ja …) never change.
const string Fields = """
[
 {"id":"heading","label":"ls.f.heading","type":"text"},
 {"id":"store","label":"ls.f.store","type":"text","default":"default","help":"ls.f.storeHelp"},
 {"id":"layout","label":"ls.f.layout","type":"select","default":"karten","options":[{"value":"karten","label":"ls.o.karten"},{"value":"elegant","label":"ls.o.elegant"},{"value":"zitat","label":"ls.o.zitat"},{"value":"liste","label":"ls.o.liste"},{"value":"laufband","label":"ls.o.laufband"}]},
 {"id":"look","label":"ls.f.look","type":"select","default":"template","options":[{"value":"template","label":"ls.o.template"},{"value":"hell","label":"ls.o.hell"},{"value":"dunkel","label":"ls.o.dunkel"},{"value":"akzent","label":"ls.o.akzent"}]},
 {"id":"symbol","label":"ls.f.symbol","type":"select","default":"stern","options":[{"value":"stern","label":"ls.o.stern"},{"value":"herz","label":"ls.o.herz"},{"value":"punkt","label":"ls.o.punkt"},{"value":"flamme","label":"ls.o.flamme"},{"value":"keine","label":"ls.o.keine"}]},
 {"id":"address","label":"ls.f.address","type":"select","default":"sie","help":"ls.f.addressHelp","options":[{"value":"sie","label":"ls.o.sie"},{"value":"du","label":"ls.o.du"}]},
 {"id":"columns","label":"ls.f.columns","type":"select","default":"3","options":[{"value":"1","label":"1"},{"value":"2","label":"2"},{"value":"3","label":"3"}]},
 {"id":"showAverage","label":"ls.f.showAverage","type":"select","default":"ja","options":[{"value":"ja","label":"ls.o.ja"},{"value":"nein","label":"ls.o.nein"}]},
 {"id":"showForm","label":"ls.f.showForm","type":"select","default":"ja","options":[{"value":"ja","label":"ls.o.ja"},{"value":"nein","label":"ls.o.nein"}]},
 {"id":"askLink","label":"ls.f.askLink","type":"select","default":"nein","options":[{"value":"nein","label":"ls.o.nein"},{"value":"ja","label":"ls.o.ja"}]},
 {"id":"askEmail","label":"ls.f.askEmail","type":"select","default":"nein","options":[{"value":"nein","label":"ls.o.nein"},{"value":"optional","label":"ls.o.optional"},{"value":"pflicht","label":"ls.o.pflicht"}]},
 {"id":"formTitle","label":"ls.f.formTitle","type":"text","help":"ls.f.formTitleHelp"},
 {"id":"intro","label":"ls.f.intro","type":"textarea","help":"ls.f.introHelp"},
 {"id":"buttonText","label":"ls.f.buttonText","type":"text","help":"ls.f.buttonTextHelp"}
]
""";

AddBlock("leserstimmen", "ls.block.name", "ls.block.desc", req =>
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
    var layout = D("layout", "karten");
    if (layout is not ("elegant" or "zitat" or "liste" or "laufband")) layout = "karten";
    var look = D("look", "template");
    if (look is not ("hell" or "dunkel" or "akzent")) look = "template";
    var noRating = sym == "keine";

    var db = req.Service<AppDbContext>();
    var shown = Load(db, store).Where(Approved).ToList();
    shown.Sort((a, b) => string.CompareOrdinal(S(b, "date"), S(a, "date")));

    var id = "mls-" + store;   // anchor + id prefix: two blocks on one page must not share radio ids
    var sb = new StringBuilder();
    var cls = "mls" + (layout != "karten" ? " mls--" + layout : "") + (look != "template" ? " mls--" + look : "");
    sb.Append("<section class='section'><div class='" + cls + "' id='" + id + "' style='--mls-cols:" + cols + "'>");
    var heading = D("heading", "");
    if (heading.Length > 0) sb.Append("<h2>" + Enc(heading) + "</h2>");

    var rated = shown.Where(n => Rating(n) > 0).ToList();
    if (!noRating && rated.Count > 0 && D("showAverage", "ja") == "ja")
    {
        var avg = rated.Average(n => (double)Rating(n));
        var avgText = avg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        if (ReqLang() == "de") avgText = avgText.Replace(".", ",");
        sb.Append("<p class='mls-avg'>" + Symbols((int)Math.Round(avg), sym) +
            string.Format(Enc(T("ls.avg")), "<strong>" + avgText + "</strong>", Enc(T(rated.Count == 1 ? "ls.votes.1" : "ls.votes.n", rated.Count))) + "</p>");
    }
    if (req.Q("leserstimme") == "danke")
        sb.Append("<div class='mls-ok' role='status'>" + Enc(TA(du, "ls.thanks")) +
            (AutoPublish() ? "" : " " + Enc(TA(du, "ls.later"))) + "</div>" +
            // The redirect cannot carry a #fragment (the endpoint refuses such a return URL), so the
            // page would open at the top with the message out of sight below the hero.
            "<script>document.currentScript.previousElementSibling.scrollIntoView({block:'center'});</script>");

    if (shown.Count == 0)
        sb.Append("<p class='mls-empty'>" + Enc(TA(du, "ls.empty")) + "</p>");
    else if (layout == "karten")
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
    else
    {
        // One card markup for the newer designs: quote mark, text, then who (initial, name, date/link) and rating.
        string Card(JsonNode n, string extra)
        {
            var link = SafeUrl(S(n, "link"));
            var date = DateTime.TryParse(S(n, "date"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? Month(dt) : "";
            var nm = S(n, "name").Trim();
            var bare = nm.TrimStart('@').Trim();
            var initial = bare.Length > 0 ? bare.Substring(0, 1).ToUpperInvariant() : "?";
            var hue = Math.Abs(nm.Aggregate(17, (h, ch) => unchecked(h * 31 + ch))) % 360;
            var meta = new List<string>();
            if (date.Length > 0) meta.Add(date);
            if (link.Length > 0) meta.Add("<a href='" + Enc(link) + "' target='_blank' rel='nofollow ugc noopener'>" + Enc(UrlHost(link)) + " ↗</a>");
            return "<figure class='mls-c" + extra + "'><span class='mls-c__q' aria-hidden='true'>“</span>" +
                (layout == "zitat" ? Symbols(Rating(n), sym) : "") +
                "<blockquote>" + Enc(S(n, "text")).Replace("\n", "<br>") + "</blockquote>" +
                "<figcaption><span class='mls-c__av' style='--h:" + hue + "' aria-hidden='true'>" + Enc(initial) + "</span>" +
                "<span class='mls-c__who'><b>" + Enc(nm) + "</b>" + (meta.Count > 0 ? "<small>" + string.Join(" · ", meta) + "</small>" : "") + "</span>" +
                (layout != "zitat" ? "<span class='mls-c__r'>" + Symbols(Rating(n), sym) + "</span>" : "") + "</figcaption></figure>";
        }
        if (layout == "elegant")
        {
            sb.Append("<div class='mls-list'>");
            foreach (var n in shown) sb.Append(Card(n, ""));
            sb.Append("</div>");
        }
        else if (layout == "liste")
        {
            sb.Append("<div class='mls-rows'>");
            foreach (var n in shown) sb.Append(Card(n, " mls-c--row"));
            sb.Append("</div>");
        }
        else if (layout == "zitat")
        {
            sb.Append("<div class='mls-slider' data-mls-slider><div class='mls-slider__track' tabindex='0' aria-label='" + Enc(T("ls.slider")) + "'>");
            foreach (var n in shown) sb.Append(Card(n, " mls-c--slide"));
            sb.Append("</div>");
            if (shown.Count > 1)
            {
                sb.Append("<div class='mls-slider__nav'><button type='button' class='mls-slider__btn' data-dir='-1' aria-label='" + Enc(T("ls.prev")) + "'>‹</button><div class='mls-slider__dots'>");
                for (int i = 0; i < shown.Count; i++) sb.Append("<button type='button' aria-label='" + Enc(T("ls.slide", i + 1)) + "'" + (i == 0 ? " aria-current='true'" : "") + "></button>");
                sb.Append("</div><button type='button' class='mls-slider__btn' data-dir='1' aria-label='" + Enc(T("ls.next")) + "'>›</button></div>");
            }
            sb.Append("</div>");
        }
        else
        {
            // Ticker: the cards twice in a row, the track moves by exactly one half — a seamless loop.
            var cards = string.Join("", shown.Select(n => Card(n, " mls-c--tick")));
            var secs = Math.Max(30, shown.Count * 9);
            sb.Append("<div class='mls-marquee'><div class='mls-marquee__track' style='--mls-dur:" + secs + "s'>" + cards +
                "<div class='mls-marquee__dup' aria-hidden='true'>" + cards + "</div></div></div>");
        }
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
        sb.Append("<h3>" + Enc(D("formTitle", TA(du, "ls.formTitle"))) + "</h3>");
        var intro = D("intro", "");
        if (intro.Length > 0) sb.Append("<p class='mls-intro'>" + Enc(intro).Replace("\n", "<br>") + "</p>");
        sb.Append("<label for='" + id + "-name'>" + Enc(TA(du, "ls.name")) + "</label>");
        sb.Append("<input type='text' id='" + id + "-name' name='name' maxlength='80' required autocomplete='name' placeholder='" + Enc(TA(du, "ls.namePh")) + "'/>");
        if (askEmail != "nein")
        {
            sb.Append("<label for='" + id + "-email'>" + Enc(T("ls.email")) + " <span class='mls-opt'>" + Enc(T(askEmail == "pflicht" ? "ls.emailNote.req" : "ls.emailNote.opt")) + "</span></label>");
            sb.Append("<input type='email' id='" + id + "-email' name='email' maxlength='200' autocomplete='email'" + (askEmail == "pflicht" ? " required" : "") + "/>");
        }
        if (noRating) sb.Append("<input type='hidden' name='nr' value='1'/>");
        else
        {
            sb.Append("<fieldset><legend>" + Enc(TA(du, "ls.legend", Noun(sym, 5))) + "</legend><div class='mls-rate'>");
            for (int i = 5; i >= 1; i--)
                sb.Append("<input type='radio' id='" + id + "-r" + i + "' name='rating' value='" + i + "'" + (i == 5 ? " required" : "") + "/>" +
                    "<label for='" + id + "-r" + i + "' title='" + i + " " + Enc(Noun(sym, i)) + "'>" + Svg(sym) + "</label>");
            sb.Append("</div></fieldset>");
        }
        if (D("askLink", "nein") == "ja")
        {
            sb.Append("<label for='" + id + "-link'>" + Enc(TA(du, "ls.link")) + " <span class='mls-opt'>" + Enc(T("ls.optional")) + "</span></label>");
            sb.Append("<input type='url' id='" + id + "-link' name='link' maxlength='300' inputmode='url' placeholder='" + Enc(T("ls.linkPh")) + "'/>");
        }
        sb.Append("<label for='" + id + "-text'>" + Enc(TA(du, "ls.text")) + "</label>");
        sb.Append("<textarea id='" + id + "-text' name='text' maxlength='3000' required placeholder='" + Enc(TA(du, "ls.textPh")) + "'></textarea>");
        sb.Append("<button class='btn' type='submit'>" + Enc(D("buttonText", T("ls.button"))) + "</button>");
        sb.Append("</form>");
    }
    sb.Append("</div></section>");
    return sb.ToString();
}, Fields);

// ---- admin: moderate, and add reviews by hand (e.g. from Instagram or a shop) ---------------
AddAdminPage("leserstimmen", req =>
{
    var db = req.Service<AppDbContext>();
    if (req.IsPost && req.Action == "notify-save")
    {
        var users = new JsonArray();
        foreach (var u in db.Users.Where(u => u.Email != null && u.Email != "").Select(u => u.Id).ToList())
            if (req.F("nu_" + u) == "on") users.Add(u);
        SaveNotifySettings(db, new JsonObject
        {
            ["on"] = req.F("n_on") == "on", ["when"] = req.F("n_when") == "pending" ? "pending" : "all",
            ["users"] = users, ["emails"] = string.Join("\n", SplitMails(req.F("n_emails")))
        });
        req.Log("Leserstimmen: Benachrichtigung gespeichert.");
        return "";
    }
    if (req.IsPost && req.Action == "notify-test")
    {
        var to = Recipients(db, NotifySettings(db));
        void Remember(string msg)
        {
            var row = db.SiteSettings.FirstOrDefault(x => x.Key == NotifyKey + "-test");
            var v = DateTime.UtcNow.ToString("o") + "|" + msg;
            if (row == null) db.SiteSettings.Add(new SiteSetting { Key = NotifyKey + "-test", Value = v }); else row.Value = v;
            db.SaveChanges();
            req.Log("Leserstimmen: " + msg);
        }
        // The real notification is written in the site's language; the test shows exactly that one.
        if (to.Count == 0) { Remember(T("ls.test.noRecipient")); return ""; }
        var mail = req.Service<EmailService>();
        var lang = SiteLang();
        var (ok, err) = mail == null ? (false, T("ls.test.noService")) : mail.SendAsync(to, TIn(lang, "ls.test.subject"),
            MailHtml(lang, SiteBase(req.Services), "Lena (Test)", 5, "", TIn(lang, "ls.test.text"), "", false, "default"), null, true).GetAwaiter().GetResult();
        Remember(ok ? T("ls.test.sent", string.Join(", ", to)) : T("ls.test.failed", err));
        return "";
    }
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
        RefreshBadge(req.Registry, db);
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
        var b = new StringBuilder("<tr><td style='white-space:nowrap;color:#c98a2b' title='" + Enc(T("ls.a.ofFive", Rating(r))) + "'>" + stars + "</td>");
        b.Append("<td><strong>" + Enc(S(r, "name")) + "</strong>" +
            (email.Length > 0 ? "<div class='muted' style='font-size:12px'>" + Enc(email) + "</div>" : "") +
            (link.Length > 0 ? "<div style='font-size:12px'><a href='" + Enc(link) + "' target='_blank' rel='noopener'>" + Enc(UrlHost(link)) + "</a></div>" : "") + "</td>");
        b.Append("<td style='max-width:520px'>" + Enc(S(r, "text")).Replace("\n", "<br>") + "</td>");
        b.Append("<td><span class='badge'>" + Enc(store) + "</span></td>");
        var date = DateTime.TryParse(S(r, "date"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? Day(dt.ToLocalTime()) : "";
        b.Append("<td style='white-space:nowrap'>" + date + "</td><td><div class='actions'>");
        b.Append(isPending
            ? req.Ui.ActionButton(T("ls.a.approve"), new Dictionary<string, string>(f) { ["action"] = "approve" }, "")
            : req.Ui.ActionButton(T("ls.a.hide"), new Dictionary<string, string>(f) { ["action"] = "unpublish" }, "btn-ghost"));
        b.Append(req.Ui.ActionButton(T("ls.a.delete"), new Dictionary<string, string>(f) { ["action"] = "delete" }, "btn-danger", T("ls.a.deleteConfirm")));
        return b.Append("</div></td></tr>").ToString();
    }
    string Table(List<(string Store, JsonObject R)> list, bool isPending, string empty)
    {
        var t = new StringBuilder("<table class='data'><thead><tr><th>" + Enc(T("ls.a.thRating")) + "</th><th>" + Enc(T("ls.a.thName")) + "</th><th>" + Enc(T("ls.a.thText")) +
            "</th><th>" + Enc(T("ls.a.thStore")) + "</th><th>" + Enc(T("ls.a.thDate")) + "</th><th></th></tr></thead><tbody>");
        if (list.Count == 0) t.Append("<tr><td colspan='99' class='muted' style='font-style:italic'>" + Enc(empty) + "</td></tr>");
        foreach (var x in list) t.Append(Row(x.Store, x.R, isPending));
        return t.Append("</tbody></table>").ToString();
    }

    var sb = new StringBuilder();
    sb.Append(req.Ui.PageHead(T("ls.a.sub") + (AutoPublish() ? " " + T("ls.a.auto") : "")));
    sb.Append(req.Ui.Card(Table(pend, true, T("ls.a.nothingPending")), T("ls.a.pending", pend.Count)));
    sb.Append(req.Ui.Card(Table(pub, false, T("ls.a.nothingPublished")), T("ls.a.published", pub.Count)));

    var opts = string.Join("", Enumerable.Range(1, 5).Reverse().Select(i => "<option value='" + i + "'>" + Enc(T("ls.a.ofFive", i)) + "</option>"));
    var storeList = "<datalist id='mls-stores'>" + string.Join("", stores.Select(s => "<option value='" + Enc(s) + "'>")) + "</datalist>";
    var add =
        "<div class='form-grid'>" +
        "<div class='form-field'><label>" + Enc(T("ls.a.thName")) + "</label><input name='name' maxlength='80' required></div>" +
        "<div class='form-field'><label>" + Enc(T("ls.a.thRating")) + "</label><select name='rating'>" + opts + "</select></div>" +
        "<div class='form-field'><label>" + Enc(T("ls.a.thStore")) + "</label><input name='store' list='mls-stores' value='" + Enc(stores.FirstOrDefault() ?? "default") + "' required>" + storeList +
        "<div class='help'>" + Enc(T("ls.a.storeHelp")) + "</div></div>" +
        "<div class='form-field'><label>" + Enc(T("ls.a.link")) + "</label><input name='link' maxlength='300' placeholder='https://…'></div>" +
        "</div>" +
        "<div class='form-field'><label>" + Enc(T("ls.a.thText")) + "</label><textarea name='text' rows='4' maxlength='3000' required></textarea></div>" +
        "<button type='submit' class='btn'>" + Enc(T("ls.a.add")) + "</button>";
    sb.Append(req.Ui.Card(req.Ui.Form(add, new Dictionary<string, string> { ["action"] = "add" }), T("ls.a.addTitle")));

    // ---- notification
    string LastTest(AppDbContext d)
    {
        var v = d.SiteSettings.FirstOrDefault(x => x.Key == NotifyKey + "-test")?.Value ?? "";
        var i = v.IndexOf('|');
        if (i < 0) return "";
        var when = DateTime.TryParse(v.Substring(0, i), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? Day(t.ToLocalTime()) + t.ToLocalTime().ToString(" HH:mm") : "";
        return "<p class='help' style='margin-top:8px'>" + Enc(T("ls.a.lastTest", when, v.Substring(i + 1))) + "</p>";
    }
    var ns = NotifySettings(db);
    var nOn = ns == null ? Recipients(db, null).Count > 0 : NotifyOn(ns);
    var nIds = ns == null ? new List<int>() : UserIds(ns);
    var nMails = ns == null ? string.Join("\n", Recipients(db, null)) : S(ns, "emails");
    var mailSvc = req.Service<EmailService>();
    var mailReady = mailSvc != null && mailSvc.IsConfiguredAsync().GetAwaiter().GetResult();
    var nb = new StringBuilder();
    if (!mailReady) nb.Append(req.Ui.Alert(T("ls.a.noMail"), "info"));
    nb.Append("<div class='form-field'><label style='display:flex;gap:8px;align-items:center;font-weight:600'><input type='checkbox' name='n_on'" + (nOn ? " checked" : "") + "> " + Enc(T("ls.a.notifyOn")) + "</label></div>");
    nb.Append("<div class='form-field'><label>" + Enc(T("ls.a.when")) + "</label><select name='n_when'><option value='all'" + (ns == null || S(ns, "when") != "pending" ? " selected" : "") + ">" + Enc(T("ls.a.whenAll")) + "</option><option value='pending'" + (ns != null && S(ns, "when") == "pending" ? " selected" : "") + ">" + Enc(T("ls.a.whenPending")) + "</option></select></div>");
    var admins = db.Users.Where(u => u.Email != null && u.Email != "").OrderBy(u => u.Username).ToList();
    if (admins.Count > 0)
    {
        nb.Append("<div class='form-field'><label>" + Enc(T("ls.a.users")) + "</label>");
        foreach (var u in admins)
            nb.Append("<label style='display:flex;gap:8px;align-items:center;font-weight:400;margin:4px 0'><input type='checkbox' name='nu_" + u.Id + "'" + (nIds.Contains(u.Id) ? " checked" : "") + "> " + Enc(string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName) + " <span class='muted'>(" + Enc(u.Email) + ")</span></label>");
        nb.Append("</div>");
    }
    nb.Append("<div class='form-field'><label>" + Enc(T("ls.a.more")) + "</label><textarea name='n_emails' rows='2' placeholder='" + Enc(T("ls.a.morePh")) + "'>" + Enc(nMails) + "</textarea><div class='help'>" + Enc(T("ls.a.moreHelp")) + "</div></div>");
    nb.Append("<button type='submit' class='btn'>" + Enc(T("ls.a.save")) + "</button>");
    var nCard = req.Ui.Form(nb.ToString(), new Dictionary<string, string> { ["action"] = "notify-save" }) +
        "<div style='margin-top:10px'>" + req.Ui.ActionButton(T("ls.a.test"), new Dictionary<string, string> { ["action"] = "notify-test" }, "btn-ghost") + "</div>" +
        LastTest(db);
    sb.Append(req.Ui.Card(nCard, T("ls.a.notifyTitle")));

    var open = Legacy(db).Count(x => !Load(db, x.Store).Any(n => S(n, "name") == S(x.R, "name") && S(n, "text") == S(x.R, "text")));
    if (open > 0)
        sb.Append(req.Ui.Card("<p>" + Enc((open == 1 ? T("ls.a.importOne") : T("ls.a.importMany", open)) + " " + T("ls.a.importHow")) + "</p>" +
            req.Ui.ActionButton(T("ls.a.importBtn"), new Dictionary<string, string> { ["action"] = "import" }, ""), T("ls.a.importTitle")));
    return sb.ToString();
});

Log("Leserstimmen geladen.");
