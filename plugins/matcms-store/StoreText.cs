using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.MatcmsStore;

/// <summary>Every text of the store as (key, German, English), and the small text helpers. A test checks that each key the code uses is here.</summary>
internal static class StoreText
{
    public static void Register(IPluginBuilder b)
    {
        b.Strings("de", All.ToDictionary(r => r.Key, r => r.De));
        b.Strings("en", All.ToDictionary(r => r.Key, r => r.En));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    public static string Cut(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();

    /// <summary>A slug of letters, digits and dashes (umlauts spelled out).</summary>
    /// <summary>The label key of a package kind: script, native, template, component.</summary>
    public static string KindKey(string kind) => kind switch
    {
        "native" => "store.kind.native", StorePackage.KindTemplate => "store.kind.template", StorePackage.KindComponent => "store.kind.component", _ => "store.kind.script"
    };

    public static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Trim().ToLowerInvariant()
                     .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss"))
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "herausgeber" : Cut(slug, 50).Trim('-');
    }

    /// <summary>A sort key for versions: the numeric parts compared as numbers ("1.10" after "1.9"), anything else after them as text.</summary>
    public static string VersionKey(string v)
    {
        var parts = v.Split('.', '-', '+');
        var sb = new StringBuilder();
        foreach (var p in parts.Take(6))
            sb.Append(long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n.ToString("D10", CultureInfo.InvariantCulture) : "~" + p).Append('.');
        return sb.ToString();
    }

    public static IReadOnlyList<string> Tags(string tags) =>
        tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>At most ten keywords of at most 30 characters, comma-separated.</summary>
    public static string CleanTags(string raw) =>
        string.Join(", ", raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => Cut(t, 30))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(10));

    /// <summary>An address a visitor may be sent to: absolute http(s), nothing else.</summary>
    public static string SafeUrl(string url) =>
        Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.AbsoluteUri : "";

    /// <summary>PNG, JPEG or WebP by their first bytes; anything else (SVG, HTML) is refused.</summary>
    public static string? Sniff(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        return null;
    }

    public static string Size(long bytes) => bytes >= 1024 * 1024
        ? (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB";

    // ---- texts --------------------------------------------------------------------------------------------------------

    internal static readonly (string Key, string De, string En)[] All =
    [
        ("store.nav", "MatCMS Store", "MatCMS Store"),
        ("store.menu", "Store", "Store"),
        ("store.menuHelp", "Der MatCMS Store: Plugins, Templates und Komponenten stöbern und suchen.", "The MatCMS Store: browse and search plugins, templates and components."),
        ("store.links.group", "MatCMS Store", "MatCMS Store"),
        ("store.links.item", "Store: {0}", "Store: {0}"),
        ("store.search.kind", "Store", "Store"),
        ("store.all", "Alle", "All"),
        ("store.kind.script", "Skript", "Script"),
        ("store.kind.native", "Native", "Native"),
        ("store.kind.template", "Template", "Template"),
        ("store.kind.component", "Komponente", "Component"),
        ("store.kind.templates", "Templates", "Templates"),
        ("store.kind.components", "Komponenten", "Components"),
        ("store.installTemplate", "So kommt es auf Ihre Website: im Admin unter Templates → „Weiter durchsuchen …“ → „Installieren“ – oder die Datei herunterladen und unter Templates importieren. Aktiv wird es erst, wenn Sie es aktivieren.",
            "To put it on your website: in the admin, Templates → “Browse further …” → “Install” – or download the file and import it under Templates. It only goes live when you activate it."),
        ("store.installComponent", "So kommt es auf Ihre Website: im Admin unter Komponenten → „Weiter durchsuchen …“ → „Installieren“ – oder die Datei herunterladen und unter Komponenten importieren.",
            "To put it on your website: in the admin, Components → “Browse further …” → “Install” – or download the file and import it under Components."),
        ("store.field.version", "Version (nur Templates und Komponenten)", "Version (templates and components only)"),
        ("store.field.versionHint", "Ein Plugin-Paket nennt seine Version selbst; ein Template oder eine Komponente (die JSON-Datei aus „Als JSON exportieren“) bekommt sie hier.",
            "A plugin package names its own version; a template or component (the JSON file from “Export as JSON”) gets it here."),
        ("store.err.pkgJson", "Die Datei ist weder ein Plugin-Paket noch das JSON eines Templates oder einer Komponente.", "The file is neither a plugin package nor the JSON of a template or component."),
        ("store.err.pkgNoVersion", "Bitte für ein Template oder eine Komponente eine Version angeben.", "Please give a template or component a version."),
        ("store.err.kindChanged", "Der Key „{0}“ gehört zu einer anderen Art von Eintrag (Plugin, Template oder Komponente).", "The key “{0}” belongs to another kind of entry (plugin, template or component)."),
        ("store.by", "von", "by"),
        ("store.verified", "geprüfter Herausgeber", "verified publisher"),
        ("store.version", "Version {0}", "Version {0}"),
        ("store.downloads", "{0} Downloads", "{0} downloads"),
        ("store.submit", "Einreichen", "Submit"),
        ("store.searchPlaceholder", "Im Store suchen …", "Search the store …"),
        ("store.removeTag", "Schlagwort entfernen", "Remove keyword"),
        ("store.empty", "Noch keine Einträge.", "No entries yet."),
        ("store.nothingFound", "Nichts gefunden.", "Nothing found."),
        ("store.notFound", "Nicht gefunden", "Not found"),
        ("store.backHome", "Zum Store", "To the store"),
        ("store.install", "So kommt es auf Ihre Website: im Admin unter Plugins → „Weiter durchsuchen …“ → „Installieren“ – oder das Paket herunterladen und unter Plugins importieren.",
            "To put it on your website: in the admin, Plugins → “Browse further …” → “Install” – or download the package and import it under Plugins."),
        ("store.installNative", "Ein natives Plugin läuft nur, wo „Native Plugins erlauben“ eingeschaltet ist.", "A native plugin only runs where “Allow native plugins” is switched on."),
        ("store.downloadPackage", "Paket {0} herunterladen", "Download package {0}"),
        ("store.homepage", "Website des Plugins", "Plugin website"),
        ("store.versions", "Versionen", "Versions"),
        ("store.withdrawn", "zurückgezogen", "withdrawn"),
        ("store.fact.version", "Version", "Version"),
        ("store.fact.updated", "Aktualisiert", "Updated"),
        ("store.fact.kind", "Art", "Kind"),
        ("store.fact.api", "Plugin-API {0}", "plugin API {0}"),
        ("store.fact.publisher", "Herausgeber", "Publisher"),
        ("store.fact.license", "Lizenz", "Licence"),
        ("store.fact.downloads", "Downloads", "Downloads"),
        ("store.fact.signed", "Signiert", "Signed"),
        ("store.fact.signedBy", "Store-Schlüssel {0}", "store key {0}"),
        ("store.fact.sha", "SHA-256", "SHA-256"),

        ("store.account", "Herausgeber-Konto", "Publisher account"),
        ("store.accountIntro", "Angemeldet als Herausgeber „{0}“.", "Signed in as publisher “{0}”."),
        ("store.register", "Als Herausgeber registrieren", "Register as a publisher"),
        ("store.registerIntro", "Mit einem Herausgeber-Konto laden Sie Plugins in den MatCMS Store hoch. Jede Version wird geprüft und signiert, bevor sie im Store erscheint.",
            "With a publisher account you upload plugins to the MatCMS Store. Every version is reviewed and signed before it appears in the store."),
        ("store.registerSend", "Konto anlegen", "Create account"),
        ("store.registrationClosed", "Neue Herausgeber werden zurzeit nur auf Einladung aufgenommen.", "New publishers are currently accepted by invitation only."),
        ("store.haveAccount", "Ich habe schon ein Konto", "I already have an account"),
        ("store.become", "Herausgeber werden", "Become a publisher"),
        ("store.becomeIntro", "Ihr Konto dieser Website wird zum Herausgeber-Konto. Danach können Sie Plugins hochladen.", "Your account on this site becomes a publisher account. Then you can upload plugins."),
        ("store.becomeSend", "Herausgeber werden", "Become a publisher"),
        ("store.termsShow", "Bedingungen für Herausgeber lesen", "Read the terms for publishers"),
        ("store.need2fa", "Herausgeber brauchen die Anmeldung in zwei Schritten (2FA). Bitte richten Sie sie ein – danach können Sie hochladen.",
            "Publishers need two-step sign-in (2FA). Please set it up – then you can upload."),
        ("store.set2fa", "2FA einrichten", "Set up 2FA"),
        ("store.upload", "Plugin hochladen", "Upload a plugin"),
        ("store.uploadIntro", "Ein Plugin-Paket (.zip, höchstens {0} MB) wie es „Exportieren“ in MatCMS oder pack.ps1 erzeugt – oder ein Template bzw. eine Komponente als JSON (aus „Als JSON exportieren“). Key und Version stehen im Paket; ein neuer Key legt einen neuen Eintrag an, ein bekannter eine neue Version.",
            "A plugin package (.zip, at most {0} MB) as MatCMS’s “Export” or pack.ps1 makes it – or a template or component as JSON (from “Export as JSON”). Key and version are in the package; a new key makes a new entry, a known one a new version."),
        ("store.uploadSend", "Zur Prüfung einreichen", "Submit for review"),
        ("store.newVersion", "Neue Version hochladen", "Upload a new version"),
        ("store.newEntryTexts", "Texte für den Store", "Texts for the store"),
        ("store.newEntryTextsHint", "Nur bei einem neuen Eintrag nötig; später unter „Meine Einträge“ änderbar.", "Only needed for a new entry; can be changed later under “My entries”."),
        ("store.myItems", "Meine Einträge", "My entries"),
        ("store.noItems", "Noch nichts hochgeladen.", "Nothing uploaded yet."),
        ("store.profile", "Herausgeber-Profil", "Publisher profile"),
        ("store.profileName", "Name „{0}“ – öffentliche Seite {1}", "Name “{0}” – public page {1}"),
        ("store.texts", "Texte", "Texts"),
        ("store.viewPublic", "Im Store ansehen", "View in the store"),
        ("store.save", "Speichern", "Save"),
        ("store.cancel", "Abbrechen", "Cancel"),
        ("store.delete", "Löschen", "Delete"),
        ("store.caption", "Bildunterschrift", "Caption"),
        ("store.cover", "Titelbild", "Cover"),
        ("store.makeCover", "Als Titelbild", "Make cover"),
        ("store.addPictures", "Bilder hinzufügen", "Add pictures"),
        ("store.col.item", "Eintrag", "Entry"),
        ("store.col.status", "Status", "Status"),
        ("store.col.versions", "Versionen", "Versions"),
        ("store.col.downloads", "Downloads", "Downloads"),
        ("store.col.submitted", "Eingereicht", "Submitted"),
        ("store.col.note", "Hinweis", "Note"),
        ("store.istatus.draft", "noch nicht im Store", "not in the store yet"),
        ("store.istatus.published", "im Store", "in the store"),
        ("store.istatus.withdrawn", "zurückgezogen", "withdrawn"),
        ("store.vstatus.review", "in Prüfung", "in review"),
        ("store.vstatus.published", "freigegeben", "approved"),
        ("store.vstatus.rejected", "abgelehnt", "rejected"),
        ("store.vstatus.withdrawn", "zurückgezogen", "withdrawn"),
        ("store.ok.welcome", "Willkommen – Ihr Herausgeber-Konto ist angelegt.", "Welcome – your publisher account is ready."),
        ("store.ok.saved", "Gespeichert.", "Saved."),
        ("store.ok.submitted", "Eingereicht – die Version wird jetzt geprüft. Sie bekommen eine E-Mail, sobald sie freigegeben oder abgelehnt ist.",
            "Submitted – the version is being reviewed now. You get an e-mail once it is approved or rejected."),
        ("store.ok.pictures", "Bilder hinzugefügt.", "Pictures added."),
        ("store.ok.nopictures", "Keine Bilder übernommen (PNG, JPEG oder WebP, höchstens 3 MB, höchstens 8).", "No pictures taken (PNG, JPEG or WebP, at most 3 MB, at most 8)."),
        ("store.ok.deleted", "Gelöscht.", "Deleted."),
        ("store.ok.nothing", "Nichts geändert.", "Nothing changed."),
        ("store.rev.title", "Bewertungen", "Reviews"),
        ("store.rev.average", "Durchschnitt {0} von 5 aus {1} Bewertungen", "Average {0} of 5 from {1} reviews"),
        ("store.rev.signIn", "Anmelden und bewerten", "Sign in to review"),
        ("store.rev.ownEntry", "Den eigenen Eintrag kann man nicht bewerten – aber unten jede Bewertung beantworten (im Konto).", "You cannot review your own entry – but you can answer every review (in your account)."),
        ("store.rev.yours", "Ihre Bewertung", "Your review"),
        ("store.rev.yoursEdit", "Ihre Bewertung ändern", "Change your review"),
        ("store.rev.starsN", "{0} von 5 Sternen", "{0} of 5 stars"),
        ("store.rev.name", "Angezeigter Name", "Name shown"),
        ("store.rev.nameHint", "Erscheint öffentlich über Ihrer Bewertung. Ein Vorname oder ein Kürzel genügt.", "Shown publicly above your review. A first name or initials are enough."),
        ("store.rev.text", "Ihre Erfahrung (freiwillig)", "Your experience (optional)"),
        ("store.rev.send", "Bewertung speichern", "Save review"),
        ("store.rev.delete", "Bewertung löschen", "Delete review"),
        ("store.rev.none", "Noch keine Bewertungen.", "No reviews yet."),
        ("store.rev.anonymous", "Anonym", "Anonymous"),
        ("store.rev.answerBy", "Antwort von {0}", "Answer from {0}"),
        ("store.rev.report", "Bewertung melden", "Report review"),
        ("store.rev.saved", "Danke – Ihre Bewertung ist gespeichert.", "Thank you – your review is saved."),
        ("store.rev.deleted", "Ihre Bewertung ist gelöscht.", "Your review is deleted."),
        ("store.rev.reported", "Danke – die Bewertung wird geprüft.", "Thank you – the review will be checked."),
        ("store.rev.invalid", "Bitte 1 bis 5 Sterne wählen.", "Please choose 1 to 5 stars."),
        ("store.rev.answer", "Ihre Antwort (öffentlich)", "Your answer (public)"),
        ("store.rev.answerSend", "Antwort speichern", "Save answer"),
        ("store.rev.visible", "sichtbar", "visible"),
        ("store.rev.hidden", "ausgeblendet", "hidden"),
        ("store.rev.reportedN", "{0}× gemeldet", "reported {0}×"),
        ("store.rev.hide", "Ausblenden", "Hide"),
        ("store.rev.show", "Wieder zeigen", "Show again"),
        ("store.rev.dismiss", "Meldung erledigt", "Report handled"),
        ("store.rev.confirmDelete", "Diese Bewertung endgültig löschen?", "Delete this review for good?"),
        ("store.rev.col.review", "Bewertung", "Review"),
        ("store.set.groupReviews", "Bewertungen", "Reviews"),
        ("store.set.reviews", "Bewertungen erlauben", "Allow reviews"),
        ("store.set.reviewsHelp", "Angemeldete Konten bewerten veröffentlichte Einträge mit Sternen und Text; Herausgeber antworten, jeder kann melden, Sie blenden aus oder löschen (Bewertungen im Menü). Aus: nichts wird gezeigt oder angenommen.",
            "Signed-in accounts review published entries with stars and text; publishers answer, anybody can report, you hide or delete (Reviews in the menu). Off: nothing is shown or taken."),

        ("store.field.publisherName", "Name des Herausgebers", "Publisher name"),
        ("store.field.publisherNameHint", "So erscheint er im Store, z. B. Ihre Firma oder Ihr Name.", "How it appears in the store, e.g. your company or your name."),
        ("store.field.email", "E-Mail", "E-mail"),
        ("store.field.password", "Passwort", "Password"),
        ("store.field.password2", "Passwort wiederholen", "Repeat password"),
        ("store.field.website", "Website (optional)", "Website (optional)"),
        ("store.field.about", "Über den Herausgeber", "About the publisher"),
        ("store.field.terms", "Ich habe die Bedingungen für Herausgeber gelesen und stimme ihnen zu.", "I have read and agree to the terms for publishers."),
        ("store.field.package", "Paket (.zip) oder Template/Komponente (.json)", "Package (.zip) or template/component (.json)"),
        ("store.field.packageHint", "Skript-Plugins aus MatCMS → Plugins → Exportieren, native mit pack-native.ps1.", "Script plugins from MatCMS → Plugins → Export, native ones with pack-native.ps1."),
        ("store.field.notes", "Was ist neu?", "What is new?"),
        ("store.field.notesHint", "Erscheint bei den Versionen im Store.", "Shown with the versions in the store."),
        ("store.field.name", "Name", "Name"),
        ("store.field.summary", "Kurzbeschreibung", "Short description"),
        ("store.field.description", "Beschreibung", "Description"),
        ("store.field.descriptionHint", "Klartext; eine Leerzeile beginnt einen neuen Absatz.", "Plain text; a blank line starts a new paragraph."),
        ("store.field.tags", "Schlagwörter", "Keywords"),
        ("store.field.tagsHint", "Mit Komma getrennt, höchstens zehn.", "Comma-separated, at most ten."),
        ("store.field.license", "Lizenz", "Licence"),
        ("store.field.homepage", "Website des Plugins (optional)", "Plugin website (optional)"),
        ("store.field.pictures", "Bilder", "Pictures"),
        ("store.field.picturesHint", "Bildschirmfotos: PNG, JPEG oder WebP, je höchstens 3 MB, höchstens {0}. Das erste ist das Titelbild.",
            "Screenshots: PNG, JPEG or WebP, at most 3 MB each, at most {0}. The first is the cover."),

        ("store.err.spam", "Das hat nicht geklappt.", "That did not work."),
        ("store.err.name", "Der Name braucht 2 bis 60 Zeichen.", "The name needs 2 to 60 characters."),
        ("store.err.website", "Die Website muss mit http:// oder https:// beginnen.", "The website must start with http:// or https://."),
        ("store.err.passwordMatch", "Die Passwörter stimmen nicht überein.", "The passwords do not match."),
        ("store.err.terms", "Bitte den Bedingungen zustimmen.", "Please agree to the terms."),
        ("store.err.emailTaken", "Für diese E-Mail gibt es schon ein Konto – bitte anmelden.", "There is already an account for this e-mail – please sign in."),
        ("store.err.email", "Das ist keine gültige E-Mail-Adresse.", "That is not a valid e-mail address."),
        ("store.err.password", "Das Passwort ist zu kurz.", "The password is too short."),
        ("store.err.register", "Das Konto konnte nicht angelegt werden.", "The account could not be created."),
        ("store.err.blocked", "Dieses Herausgeber-Konto ist gesperrt.", "This publisher account is blocked."),
        ("store.err.noPackage", "Bitte ein Paket auswählen.", "Please choose a package."),
        ("store.err.pkgTooBig", "Das Paket ist zu groß (höchstens {0} MB).", "The package is too big (at most {0} MB)."),
        ("store.err.pkgZip", "Das ist kein lesbares ZIP-Paket.", "That is not a readable ZIP package."),
        ("store.err.pkgEntries", "Das Paket enthält zu viele Dateien.", "The package holds too many files."),
        ("store.err.pkgPath", "Das Paket enthält einen unzulässigen Dateipfad.", "The package holds a path that is not allowed."),
        ("store.err.pkgSize", "Das Paket ist entpackt zu groß.", "The package is too big unpacked."),
        ("store.err.pkgManifest", "Im Paket fehlt eine gültige plugin.json.", "The package has no valid plugin.json."),
        ("store.err.pkgKey", "Der Key in plugin.json muss aus Kleinbuchstaben, Ziffern und Bindestrichen bestehen (2–41 Zeichen).",
            "The key in plugin.json must be lower-case letters, digits and dashes (2–41 characters)."),
        ("store.err.pkgVersion", "Die Version in plugin.json muss mit einer Ziffer beginnen (z. B. 1.0).", "The version in plugin.json must start with a digit (e.g. 1.0)."),
        ("store.err.pkgKind", "Unbekannte Art in plugin.json (Kind).", "Unknown kind in plugin.json."),
        ("store.err.pkgNative", "Ein natives Paket braucht seine Dateien unter src/.", "A native package needs its files under src/."),
        ("store.err.pkgScript", "Ein Skript-Paket braucht seinen Code in plugin.json.", "A script package needs its code in plugin.json."),
        ("store.err.keyTaken", "Der Key „{0}“ gehört schon einem anderen Herausgeber.", "The key “{0}” already belongs to another publisher."),
        ("store.err.keyReserved", "Der Key „{0}“ ist reserviert – nur geprüfte Herausgeber dürfen ihn verwenden.", "The key “{0}” is reserved – only verified publishers may use it."),
        ("store.err.itemWithdrawn", "Dieser Eintrag wurde aus dem Store genommen.", "This entry was taken out of the store."),
        ("store.err.versionExists", "Version {0} gibt es schon.", "Version {0} exists already."),
        ("store.err.versionLower", "Version {0} muss höher sein als {1}.", "Version {0} must be higher than {1}."),

        ("store.admin.review", "Prüfen", "Review"),
        ("store.admin.items", "Einträge", "Entries"),
        ("store.admin.publishers", "Herausgeber", "Publishers"),
        ("store.admin.settings", "Einstellungen", "Settings"),
        ("store.admin.viewStore", "Store ansehen", "View store"),
        ("store.admin.search", "Suchen …", "Search …"),
        ("store.admin.queueEmpty", "Nichts zu prüfen.", "Nothing to review."),
        ("store.admin.col.what", "Was", "What"),
        ("store.admin.col.items", "Einträge", "Entries"),
        ("store.admin.col.since", "Seit", "Since"),
        ("store.admin.newEntry", "neuer Eintrag", "new entry"),
        ("store.admin.update", "neue Version", "new version"),
        ("store.admin.package", "Paket", "Package"),
        ("store.admin.size", "Größe", "Size"),
        ("store.admin.download", "Paket herunterladen", "Download package"),
        ("store.admin.notes", "Was ist neu", "What is new"),
        ("store.admin.files", "Dateien ({0})", "Files ({0})"),
        ("store.admin.decide", "Freigeben oder ablehnen", "Approve or reject"),
        ("store.admin.decided", "Entschieden: {0}", "Decided: {0}"),
        ("store.admin.signHow", "Erst das Paket herunterladen und prüfen. Freigeben braucht eine Signatur von Ihrem Rechner – im MatCMS-Repo ausführen und die ausgegebene Zeile hier einfügen:",
            "Download and check the package first. Approving needs a signature from your own machine – run this in the MatCMS repository and paste the printed line here:"),
        ("store.admin.signature", "Signatur", "Signature"),
        ("store.admin.signatureHelp", "Die Zeile „schlüssel-id:signatur“ aus dem Signier-Werkzeug.", "The line “key-id:signature” from the signing tool."),
        ("store.admin.reason", "Begründung (bei Ablehnung)", "Reason (when rejecting)"),
        ("store.admin.reasonHelp", "Geht per E-Mail an den Herausgeber.", "Goes to the publisher by e-mail."),
        ("store.admin.approve", "Freigeben", "Approve"),
        ("store.admin.reject", "Ablehnen", "Reject"),
        ("store.admin.approved", "{0} {1} ist freigegeben.", "{0} {1} is approved."),
        ("store.admin.rejected", "{0} {1} ist abgelehnt.", "{0} {1} is rejected."),
        ("store.admin.err.signature", "Die Signatur passt nicht zu diesem Paket (oder der Schlüssel ist unbekannt).", "The signature does not match this package (or the key is unknown)."),
        ("store.admin.err.reason", "Bitte eine Begründung angeben.", "Please give a reason."),
        ("store.admin.noItems", "Noch keine Einträge.", "No entries yet."),
        ("store.admin.entry", "Eintrag", "Entry"),
        ("store.admin.danger", "Aus dem Store nehmen", "Take out of the store"),
        ("store.admin.withdraw", "Zurückziehen", "Withdraw"),
        ("store.admin.confirmWithdraw", "Version {0} zurückziehen? Sie wird nicht mehr angeboten; wer sie hat, sieht den Grund.", "Withdraw version {0}? It is no longer offered; whoever has it sees the reason."),
        ("store.admin.versionWithdrawn", "Version {0} zurückgezogen.", "Version {0} withdrawn."),
        ("store.admin.withdrawItem", "Eintrag zurückziehen", "Withdraw entry"),
        ("store.admin.confirmWithdrawItem", "„{0}“ ganz aus dem Store nehmen?", "Take “{0}” out of the store completely?"),
        ("store.admin.itemWithdrawn", "Eintrag zurückgezogen.", "Entry withdrawn."),
        ("store.admin.restoreItem", "Wieder anbieten", "Offer again"),
        ("store.admin.itemRestored", "Eintrag wird wieder angeboten.", "Entry offered again."),
        ("store.admin.noPublishers", "Noch keine Herausgeber.", "No publishers yet."),
        ("store.admin.assign", "Konto zuordnen", "Assign account"),
        ("store.admin.assignHelp", "Verbindet einen Herausgeber mit einem Mitgliedskonto dieser Website (z. B. nach einer Wiederherstellung, die keine Konten mitbringt).",
            "Ties a publisher to a member account of this site (e.g. after a restore that brought no accounts)."),
        ("store.admin.assignSend", "Zuordnen", "Assign"),
        ("store.admin.err.noAccount", "Es gibt kein Mitgliedskonto mit dieser E-Mail.", "There is no member account with this e-mail."),
        ("store.admin.err.accountTaken", "Dieses Konto gehört schon zum Herausgeber „{0}“.", "This account already belongs to publisher “{0}”."),
        ("store.admin.blocked", "gesperrt", "blocked"),
        ("store.admin.verify", "Als geprüft markieren", "Mark verified"),
        ("store.admin.unverify", "Prüfung aufheben", "Remove verification"),
        ("store.admin.block", "Sperren", "Block"),
        ("store.admin.unblock", "Entsperren", "Unblock"),
        ("store.admin.confirmBlock", "„{0}“ sperren? Seine Einträge verschwinden aus dem Store, hochladen geht nicht mehr.", "Block “{0}”? Their entries disappear from the store and they can no longer upload."),

        ("store.set.title", "Name des Stores", "Store name"),
        ("store.set.groupGeneral", "Allgemein", "General"),
        ("store.set.intro", "Einleitung über dem Katalog", "Introduction above the catalogue"),
        ("store.set.groupPublishers", "Herausgeber", "Publishers"),
        ("store.set.registration", "Registrieren erlauben", "Allow registration"),
        ("store.set.registrationHelp", "Aus = neue Herausgeber nur, wenn Sie ihnen ein Konto der Website anlegen.", "Off = new publishers only when you create a site account for them."),
        ("store.set.twoFactor", "2FA für Herausgeber verlangen", "Require 2FA for publishers"),
        ("store.set.twoFactorHelp", "Hochladen erst mit eingerichteter Anmeldung in zwei Schritten.", "Uploading only once two-step sign-in is set up."),
        ("store.set.terms", "Bedingungen für Herausgeber", "Terms for publishers"),
        ("store.set.termsHelp", "Wird beim Registrieren gezeigt und muss angenommen werden.", "Shown when registering; must be accepted."),
        ("store.set.reserved", "Reservierte Keys", "Reserved keys"),
        ("store.set.reservedHelp", "Ein Key je Zeile – nur geprüfte Herausgeber dürfen sie belegen.", "One key per line – only verified publishers may claim them."),
        ("store.set.groupReview", "Prüfen", "Review"),
        ("store.set.notify", "E-Mail bei neuen Einreichungen", "E-mail on new submissions"),
        ("store.set.notifyHelp", "Leer = keine E-Mail.", "Empty = no e-mail."),
        ("store.set.keys", "Öffentliche Signatur-Schlüssel", "Public signing keys"),
        ("store.set.keysHelp", "Eine Zeile „id=Schlüssel“ je Schlüssel (tools/StoreSign pubkey). Websites und Clouds prüfen gegen die Schlüssel in ihrem eigenen Image.",
            "One line “id=key” per key (tools/StoreSign pubkey). Sites and clouds check against the keys in their own image."),
        ("store.set.maxPackage", "Größtes Paket (MB)", "Largest package (MB)"),
    ];
}
