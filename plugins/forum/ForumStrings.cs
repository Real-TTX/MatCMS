namespace MatCMS.Plugin.Forum;

/// <summary>Every text of the forum as (key, German, English). A test checks that each key the code uses is here with both languages.</summary>
internal static class ForumStrings
{
    public static void Register(IPluginBuilder b)
    {
        b.Strings("de", All.ToDictionary(r => r.Key, r => r.De));
        b.Strings("en", All.ToDictionary(r => r.Key, r => r.En));
    }

    private static readonly string[] Placeholders =
    [
        "id", "number", "url", "initials", "author_html", "author_meta", "date_html", "body_html", "edited", "signature_html", "actions_html", "is_first", "title", "intro",
        "toolbar_html", "notice_html", "categories_html", "latest_html", "stats_html", "description", "crumbs_html", "threads_html", "pager_html", "meta", "badges_html",
        "posts_html", "reply_html", "mod_html"
    ];

    internal static (string Key, string De, string En)[] All => _all ??= Core.Concat(Placeholders.Select(p => ("forum.ph." + p, PlaceholderDe(p), PlaceholderEn(p)))).ToArray();
    private static (string Key, string De, string En)[]? _all;

    private static string PlaceholderDe(string p) => p switch
    {
        "body_html" => "Text des Beitrags (HTML)", "author_html" => "Name mit Link zum Profil", "posts_html" => "Die Beiträge der Seite", "reply_html" => "Antwortfeld",
        "mod_html" => "Moderationsleiste (nur für Moderatoren)", "categories_html" => "Kategorien mit ihren Foren", "threads_html" => "Liste der Themen",
        "toolbar_html" => "Suche und Anmeldung", "crumbs_html" => "Brotkrumen", "pager_html" => "Seitenzahlen", "actions_html" => "Knöpfe", "notice_html" => "Hinweis nach einer Aktion",
        "latest_html" => "Neueste Beiträge", "stats_html" => "Zahlen des Forums", "badges_html" => "Kennzeichen (angeheftet, geschlossen)", "signature_html" => "Signatur",
        "date_html" => "Datum", "edited" => "Hinweis „bearbeitet“", "is_first" => "„1“ beim ersten Beitrag eines Themas", "initials" => "Initialen des Verfassers",
        "author_meta" => "Zahl der Beiträge des Verfassers", "number" => "Nummer im Thema", "url" => "Adresse des Beitrags", "id" => "Kennung des Beitrags",
        "title" => "Titel", "intro" => "Einleitung", "description" => "Beschreibung", "meta" => "Verfasser, Datum, Antworten, Aufrufe", _ => p
    };

    private static string PlaceholderEn(string p) => p switch
    {
        "body_html" => "The post's text (HTML)", "author_html" => "Name linked to the profile", "posts_html" => "The posts of the page", "reply_html" => "Reply box",
        "mod_html" => "Moderation bar (moderators only)", "categories_html" => "Categories with their boards", "threads_html" => "List of threads",
        "toolbar_html" => "Search and sign-in", "crumbs_html" => "Breadcrumbs", "pager_html" => "Page numbers", "actions_html" => "Buttons", "notice_html" => "Notice after an action",
        "latest_html" => "Latest posts", "stats_html" => "Forum figures", "badges_html" => "Labels (pinned, closed)", "signature_html" => "Signature",
        "date_html" => "Date", "edited" => "\"edited\" note", "is_first" => "\"1\" on a thread's first post", "initials" => "Author's initials",
        "author_meta" => "Author's post count", "number" => "Number in the thread", "url" => "Address of the post", "id" => "Id of the post",
        "title" => "Title", "intro" => "Introduction", "description" => "Description", "meta" => "Author, date, replies, views", _ => p
    };

    private static readonly (string Key, string De, string En)[] Core =
    {
        // ---- navigation, lists ----
        ("forum.nav", "Forum", "Forum"),
        ("forum.crumbs", "Brotkrumen", "Breadcrumbs"), ("forum.pages", "Seiten", "Pages"), ("forum.prev", "Vorige Seite", "Previous page"), ("forum.next", "Nächste Seite", "Next page"),
        ("forum.threads", "Themen", "Threads"), ("forum.posts", "Beiträge", "Posts"), ("forum.replies", "Antworten", "Replies"), ("forum.views", "Aufrufe", "Views"),
        ("forum.by", "von", "by"), ("forum.new", "Neu", "New"), ("forum.sticky", "Angeheftet", "Pinned"), ("forum.closed", "Geschlossen", "Closed"), ("forum.locked", "Gesperrt", "Locked"),
        ("forum.noBoards", "Es gibt noch keine Foren.", "There are no boards yet."), ("forum.noThreads", "Hier gibt es noch keine Themen – fang eins an!", "No threads here yet – start one!"),
        ("forum.noPosts", "Noch keine Beiträge", "No posts yet"), ("forum.latestPosts", "Neueste Beiträge", "Latest posts"), ("forum.latest", "Neueste Beiträge", "Latest posts"),
        ("forum.markAllRead", "Alles als gelesen markieren", "Mark everything read"),
        ("forum.stat.threads", "Themen", "Threads"), ("forum.stat.posts", "Beiträge", "Posts"), ("forum.stat.members", "Mitglieder mit Beiträgen", "Members who posted"),
        ("forum.notFound", "Nicht gefunden", "Not found"), ("forum.backHome", "Zur Forenübersicht", "Back to the forum"),
        ("forum.boardLocked", "Dieses Forum ist gesperrt – neue Beiträge schreiben nur Moderatoren.", "This board is locked – only moderators can post."),
        ("forum.time.today", "heute, {0}", "today, {0}"), ("forum.time.yesterday", "gestern, {0}", "yesterday, {0}"),
        ("forum.threadMeta", "Gestartet von {0} · {1} · {2} Antworten · {3} Aufrufe", "Started by {0} · {1} · {2} replies · {3} views"),
        ("forum.authorPosts", "{0} Beiträge", "{0} posts"),
        ("forum.deletedMember", "Gelöschtes Mitglied", "Deleted member"),
        ("forum.moderator", "Moderator", "Moderator"),

        // ---- toolbar, account ----
        ("forum.search", "Suchen", "Search"), ("forum.search.placeholder", "Im Forum suchen …", "Search the forum …"), ("forum.search.kind", "Forum", "Forum"),
        ("forum.searchFor", "Suche: „{0}“", "Search: “{0}”"), ("forum.searchNone", "Nichts gefunden. Versuch es mit anderen Wörtern.", "Nothing found. Try other words."),
        ("forum.searchShort", "Bitte mindestens zwei Zeichen eingeben.", "Please type at least two characters."),
        ("forum.signin", "Anmelden", "Sign in"), ("forum.register", "Registrieren", "Register"), ("forum.subs", "Abos", "Subscriptions"),
        ("forum.signinToWrite", "Zum Schreiben anmelden", "Sign in to post"), ("forum.signinToReply", "Melde dich an, um zu antworten.", "Sign in to reply."),
        ("forum.members", "Mitglieder", "Members"), ("forum.memberSince", "Mitglied seit {0}", "Member since {0}"), ("forum.lastActive", "zuletzt aktiv", "last active"),
        ("forum.recentPostsOf", "Letzte Beiträge von {0}", "Recent posts by {0}"),
        ("forum.signature", "Signatur", "Signature"), ("forum.signatureHint", "Erscheint unter deinen Beiträgen. Höchstens {0} Zeichen, BBCode erlaubt.", "Shown below your posts. At most {0} characters, BBCode allowed."),
        ("forum.subsHelp", "Bei neuen Antworten in diesen Themen bekommst du eine E-Mail.", "You get an e-mail when somebody replies in these threads."),
        ("forum.subsNone", "Du hast noch keine Themen abonniert.", "You have not subscribed to any thread yet."),
        ("forum.subscribe", "Abonnieren", "Subscribe"), ("forum.unsubscribe", "Abo beenden", "Unsubscribe"), ("forum.subscribeBox", "Bei Antworten per E-Mail benachrichtigen", "E-mail me about replies"),

        // ---- threads, posts ----
        ("forum.newThread", "Neues Thema", "New thread"), ("forum.newThreadIn", "Neues Thema in „{0}“", "New thread in “{0}”"), ("forum.createThread", "Thema erstellen", "Create thread"),
        ("forum.reply", "Antworten", "Reply"), ("forum.sendReply", "Antwort senden", "Send reply"), ("forum.send", "Senden", "Send"), ("forum.save", "Speichern", "Save"), ("forum.cancel", "Abbrechen", "Cancel"),
        ("forum.quote", "Zitieren", "Quote"), ("forum.edit", "Bearbeiten", "Edit"), ("forum.editPost", "Beitrag bearbeiten", "Edit post"), ("forum.report", "Melden", "Report"),
        ("forum.delete", "Löschen", "Delete"), ("forum.deletePost", "Beitrag löschen", "Delete post"), ("forum.confirmYes", "Ja, löschen", "Yes, delete"),
        ("forum.confirmDeletePost", "Diesen Beitrag wirklich löschen?", "Really delete this post?"),
        ("forum.confirmDeleteThread", "Das ganze Thema mit allen Antworten löschen?", "Delete the whole thread with all replies?"),
        ("forum.edited", "Zuletzt bearbeitet: {0}", "Last edited: {0}"), ("forum.editedByMod", "Von der Moderation bearbeitet: {0}", "Edited by a moderator: {0}"),
        ("forum.threadClosed", "Dieses Thema ist geschlossen.", "This thread is closed."),
        ("forum.reportTitle", "Beitrag melden", "Report post"), ("forum.reportReason", "Warum soll die Moderation sich das ansehen?", "Why should the moderators look at it?"),
        ("forum.reportSend", "Meldung senden", "Send report"),
        ("forum.field.title", "Titel", "Title"), ("forum.field.body", "Text", "Text"),
        ("forum.bb.bold", "Fett", "Bold"), ("forum.bb.italic", "Kursiv", "Italic"), ("forum.bb.underline", "Unterstrichen", "Underline"), ("forum.bb.strike", "Durchgestrichen", "Strike through"),
        ("forum.bb.quote", "Zitat", "Quote"), ("forum.bb.code", "Code", "Code"), ("forum.bb.link", "Link", "Link"), ("forum.bb.image", "Bild (https-Adresse)", "Picture (https address)"), ("forum.bb.list", "Liste", "List"),
        ("forum.bb.hint", "BBCode: [b] [i] [u] [s] [quote] [code] [url] [img] [list][*] – Links werden von selbst erkannt.", "BBCode: [b] [i] [u] [s] [quote] [code] [url] [img] [list][*] – links are found by themselves."),
        ("forum.bb.hintNoImg", "BBCode: [b] [i] [u] [s] [quote] [code] [url] [list][*] – Links werden von selbst erkannt.", "BBCode: [b] [i] [u] [s] [quote] [code] [url] [list][*] – links are found by themselves."),

        // ---- moderation bar ----
        ("forum.mod.title", "Moderation", "Moderation"), ("forum.mod.sticky", "Anheften", "Pin"), ("forum.mod.unsticky", "Lösen", "Unpin"),
        ("forum.mod.close", "Schließen", "Close"), ("forum.mod.open", "Öffnen", "Reopen"), ("forum.mod.move", "Verschieben", "Move"), ("forum.mod.moveTo", "Verschieben nach", "Move to"),
        ("forum.mod.rename", "Umbenennen", "Rename"), ("forum.mod.delete", "Thema löschen", "Delete thread"),

        // ---- notices, errors ----
        ("forum.ok.reported", "Danke – die Moderation sieht sich den Beitrag an.", "Thank you – the moderators will have a look."),
        ("forum.ok.saved", "Gespeichert.", "Saved."), ("forum.ok.deleted", "Gelöscht.", "Deleted."), ("forum.ok.subscribed", "Abonniert – du bekommst Antworten per E-Mail.", "Subscribed – you will get replies by e-mail."),
        ("forum.ok.unsubscribed", "Abo beendet.", "Unsubscribed."), ("forum.ok.read", "Alles als gelesen markiert.", "Everything marked as read."),
        ("forum.ok.welcome", "Willkommen im Forum! Du bist angemeldet.", "Welcome to the forum! You are signed in."),
        ("forum.err.spam", "Das sieht nach einem Bot aus.", "That looks like a bot."), ("forum.err.banned", "Dein Konto ist im Forum gesperrt.", "Your account is banned from the forum."),
        ("forum.err.noWrite", "Du darfst hier nicht schreiben.", "You may not post here."), ("forum.err.noEdit", "Diesen Beitrag kannst du nicht (mehr) bearbeiten.", "You cannot edit this post (any more)."),
        ("forum.err.title", "Der Titel braucht 3 bis {0} Zeichen.", "The title needs 3 to {0} characters."), ("forum.err.empty", "Der Text ist leer.", "The text is empty."),
        ("forum.err.long", "Der Text ist zu lang (höchstens {0} Zeichen).", "The text is too long (at most {0} characters)."),
        ("forum.err.noImages", "Bilder sind in diesem Forum ausgeschaltet.", "Pictures are switched off in this forum."),
        ("forum.err.flood", "Bitte warte {0} Sekunden zwischen zwei Beiträgen.", "Please wait {0} seconds between two posts."),
        ("forum.err.reason", "Bitte schreib kurz, worum es geht.", "Please say briefly what it is about."),
        ("forum.err.name", "Der Name braucht 2 bis 40 Zeichen.", "The name needs 2 to 40 characters."), ("forum.err.passwordMatch", "Die Passwörter stimmen nicht überein.", "The passwords do not match."),
        ("forum.err.terms", "Bitte bestätige die Forenregeln.", "Please accept the forum rules."), ("forum.err.emailTaken", "Diese E-Mail-Adresse hat schon ein Konto – melde dich an.", "This e-mail address already has an account – sign in."),
        ("forum.err.email", "Das ist keine gültige E-Mail-Adresse.", "That is not a valid e-mail address."), ("forum.err.password", "Das Passwort ist zu kurz (mindestens 8 Zeichen).", "The password is too short (at least 8 characters)."),
        ("forum.err.register", "Das Konto konnte nicht angelegt werden.", "The account could not be created."),

        // ---- registration ----
        ("forum.registerTitle", "Beim Forum registrieren", "Join the forum"), ("forum.registerSend", "Konto anlegen", "Create account"), ("forum.haveAccount", "Schon ein Konto? Anmelden", "Already have an account? Sign in"),
        ("forum.field.name", "Name im Forum", "Name in the forum"), ("forum.field.nameHint", "So erscheinst du über deinen Beiträgen.", "This is how you appear above your posts."),
        ("forum.field.email", "E-Mail-Adresse", "E-mail address"), ("forum.field.password", "Passwort", "Password"), ("forum.field.password2", "Passwort wiederholen", "Repeat password"),
        ("forum.field.terms", "Ich halte mich an die Regeln des Forums und an die Datenschutzerklärung.", "I keep to the forum's rules and the privacy policy."),

        // ---- links, seed ----
        ("forum.links.group", "Forum", "Forum"), ("forum.links.board", "Forum: {0}", "Board: {0}"),
        ("forum.seed.category", "Allgemein", "General"), ("forum.seed.board", "Allgemeine Diskussion", "General discussion"),
        ("forum.seed.boardDesc", "Für alles, was sonst nirgends hinpasst.", "For everything that fits nowhere else."),

        // ---- template parts ----
        ("forum.part.index", "Forum – Übersicht", "Forum – overview"), ("forum.part.indexHelp", "Die Startseite des Forums mit allen Kategorien.", "The forum's start page with all categories."),
        ("forum.part.board", "Forum – ein Forum", "Forum – one board"), ("forum.part.boardHelp", "Die Themenliste eines Forums.", "The thread list of a board."),
        ("forum.part.thread", "Forum – ein Thema", "Forum – one thread"), ("forum.part.threadHelp", "Ein Thema mit seinen Beiträgen und dem Antwortfeld.", "A thread with its posts and the reply box."),
        ("forum.part.post", "Forum – ein Beitrag", "Forum – one post"), ("forum.part.postHelp", "Ein einzelner Beitrag (Verfasser links, Text rechts).", "One post (author left, text right)."),

        // ---- settings ----
        ("forum.set.groupGeneral", "Allgemein", "General"), ("forum.set.groupMembers", "Mitglieder", "Members"), ("forum.set.groupPosting", "Schreiben", "Posting"),
        ("forum.set.groupMail", "E-Mail", "E-mail"), ("forum.set.groupLook", "Darstellung", "Appearance"),
        ("forum.set.title", "Name des Forums", "Name of the forum"), ("forum.set.intro", "Einleitung", "Introduction"),
        ("forum.set.introHelp", "Ein, zwei Sätze über der Forenübersicht (auch die Beschreibung für Suchmaschinen).", "One or two sentences above the overview (also the description for search engines)."),
        ("forum.set.indexing", "Von Suchmaschinen finden lassen", "Let search engines find it"),
        ("forum.set.indexingHelp", "Aus: Das Forum steht nicht in der Sitemap und alle Seiten bitten Suchmaschinen, sie nicht aufzunehmen. Foren nur für Mitglieder werden nie aufgenommen.", "Off: the forum is not in the sitemap and every page asks search engines not to index it. Boards for members only are never indexed."),
        ("forum.set.registration", "Registrierung im Forum erlauben", "Allow registration in the forum"),
        ("forum.set.registrationHelp", "Besucher können unter /forum/registrieren selbst ein Konto der Website anlegen. Aus: Konten legen Sie unter Admin → Mitglieder an (oder über die allgemeine Selbstregistrierung).", "Visitors can create an account of the site themselves at /forum/registrieren. Off: accounts are created under Admin → Members (or via the general self-registration)."),
        ("forum.set.modRole", "Rolle der Moderatoren", "Moderators' role"),
        ("forum.set.modRoleHelp", "Mitglieder mit dieser Rolle (Admin → Mitglieder) moderieren auf der Website: anheften, schließen, verschieben, bearbeiten, löschen.", "Members holding this role (Admin → Members) moderate on the site: pin, close, move, edit, delete."),
        ("forum.set.signatures", "Signaturen erlauben", "Allow signatures"), ("forum.set.images", "Bilder in Beiträgen erlauben", "Allow pictures in posts"),
        ("forum.set.imagesHelp", "[img] mit einer https-Adresse oder einem Bild der Mediathek. Bilder von fremden Servern verraten dem Server die Besucher.", "[img] with an https address or a picture of the media library. Pictures from other servers tell those servers who visits."),
        ("forum.set.flood", "Mindestabstand zwischen zwei Beiträgen (Sekunden)", "Minimum time between two posts (seconds)"),
        ("forum.set.floodHelp", "Bremst Spam und Doppelposts. 0 = aus. Gilt nicht für Moderatoren.", "Slows spam and double posts down. 0 = off. Does not apply to moderators."),
        ("forum.set.edit", "Bearbeiten erlaubt für (Minuten)", "Editing allowed for (minutes)"), ("forum.set.editHelp", "So lange dürfen Mitglieder eigene Beiträge ändern. 0 = immer.", "How long members may change their own posts. 0 = always."),
        ("forum.set.threadsPerPage", "Themen pro Seite", "Threads per page"), ("forum.set.postsPerPage", "Beiträge pro Seite", "Posts per page"),
        ("forum.set.subscriptions", "Abos per E-Mail", "Subscriptions by e-mail"), ("forum.set.subscriptionsHelp", "Mitglieder können Themen abonnieren und bekommen neue Antworten per E-Mail (Vorlage unter Mailvorlagen).", "Members can subscribe to threads and get new replies by e-mail (template under mail templates)."),
        ("forum.set.notify", "Meldungen und neue Themen an", "Reports and new threads to"),
        ("forum.set.notifyHelp", "E-Mail-Adresse(n) der Moderation, durch Komma getrennt. Leer = keine Mail.", "E-mail address(es) of the moderators, separated by commas. Empty = no mail."),
        ("forum.set.timeZone", "Zeitzone", "Time zone"), ("forum.set.timeZoneHelp", "Für die Uhrzeiten im Forum, z. B. Europe/Berlin.", "For the times shown in the forum, e.g. Europe/Berlin."),
        ("forum.set.css", "Eigenes CSS", "Own CSS"), ("forum.set.cssHelp", "Wird nach dem Stylesheet des Forums geladen. Farben kommen sonst aus dem Template (--forum-accent, --forum-line …).", "Loaded after the forum's stylesheet. Colours otherwise come from the template (--forum-accent, --forum-line …)."),

        // ---- access ----
        ("forum.access.all", "Alle Besucher", "All visitors"), ("forum.access.members", "Angemeldete Mitglieder", "Signed-in members"),
        ("forum.access.role", "Mitglieder mit Rolle", "Members with role"), ("forum.access.mods", "Nur Moderatoren", "Moderators only"),

        // ---- back office ----
        ("forum.admin.overview", "Übersicht", "Overview"), ("forum.admin.boards", "Foren", "Boards"), ("forum.admin.threads", "Themen", "Threads"),
        ("forum.admin.reports", "Meldungen", "Reports"), ("forum.admin.members", "Mitglieder", "Members"), ("forum.admin.settings", "Einstellungen", "Settings"),
        ("forum.admin.dashboardSub", "Was im Forum los ist.", "What is going on in the forum."), ("forum.admin.viewForum", "Forum ansehen", "View forum"),
        ("forum.admin.startHint", "Legen Sie das erste Forum an – danach können Mitglieder Themen eröffnen.", "Create the first board – then members can start threads."),
        ("forum.admin.openReports", "Offene Meldungen", "Open reports"), ("forum.admin.newBoard", "Neues Forum", "New board"), ("forum.admin.noBoards", "Noch keine Foren.", "No boards yet."),
        ("forum.admin.searchBoards", "Foren durchsuchen", "Search boards"), ("forum.admin.searchThreads", "Themen durchsuchen", "Search threads"), ("forum.admin.searchMembers", "Mitglieder durchsuchen", "Search members"),
        ("forum.admin.allBoards", "Alle Foren", "All boards"), ("forum.admin.allThreads", "Alle Themen", "All threads"),
        ("forum.admin.up", "Nach oben", "Move up"), ("forum.admin.down", "Nach unten", "Move down"), ("forum.admin.open", "Öffnen", "Open"),
        ("forum.admin.col.board", "Forum", "Board"), ("forum.admin.col.category", "Kategorie", "Category"), ("forum.admin.col.read", "Lesen", "Read"), ("forum.admin.col.write", "Schreiben", "Write"),
        ("forum.admin.col.thread", "Thema", "Thread"), ("forum.admin.col.author", "Verfasser", "Author"), ("forum.admin.col.status", "Status", "Status"), ("forum.admin.col.post", "Beitrag", "Post"),
        ("forum.admin.col.when", "Wann", "When"), ("forum.admin.col.reason", "Grund", "Reason"), ("forum.admin.col.reporter", "Gemeldet von", "Reported by"), ("forum.admin.col.member", "Mitglied", "Member"),
        ("forum.admin.lastPost", "Letzter Beitrag", "Last post"), ("forum.admin.figures", "Zahlen", "Figures"), ("forum.admin.danger", "Löschen", "Delete"),
        ("forum.admin.field.title", "Name", "Name"), ("forum.admin.field.slug", "Adresse", "Address"),
        ("forum.admin.field.slugHelp", "Das Forum ist unter /forum/<Adresse> zu erreichen. Leer = aus dem Namen.", "The board lives at /forum/<address>. Empty = from the name."),
        ("forum.admin.field.slugPlaceholder", "aus dem Namen", "from the name"), ("forum.admin.field.description", "Beschreibung", "Description"),
        ("forum.admin.field.category", "Kategorie", "Category"), ("forum.admin.field.categoryHelp", "Foren mit derselben Kategorie stehen in der Übersicht zusammen unter dieser Überschrift.", "Boards with the same category are listed together under that heading."),
        ("forum.admin.field.categoryHelpList", "Vorhandene Kategorien: {0}", "Existing categories: {0}"),
        ("forum.admin.field.read", "Wer darf lesen?", "Who may read?"), ("forum.admin.field.write", "Wer darf schreiben?", "Who may post?"),
        ("forum.admin.field.role", "Rolle", "Role"), ("forum.admin.field.roleHelp", "Nur bei „Mitglieder mit Rolle“: der Name der Rolle aus Admin → Mitglieder.", "Only for “Members with role”: the role's name from Admin → Members."),
        ("forum.admin.field.sort", "Reihenfolge", "Order"), ("forum.admin.field.locked", "Gesperrt", "Locked"),
        ("forum.admin.field.lockedHelp", "Lesen geht weiter, schreiben nur noch Moderatoren.", "Reading goes on, only moderators can post."),
        ("forum.admin.err.title", "Der Name braucht 2 bis 80 Zeichen.", "The name needs 2 to 80 characters."), ("forum.admin.err.role", "Für „Mitglieder mit Rolle“ bitte eine Rolle angeben.", "Please name a role for “Members with role”."),
        ("forum.admin.deleteBoard", "Forum löschen", "Delete board"), ("forum.admin.deleteBoardHelp", "Löscht das Forum mit allen Themen und Beiträgen.", "Deletes the board with all threads and posts."),
        ("forum.admin.confirmDeleteBoard", "„{0}“ mit {1} Themen und {2} Beiträgen endgültig löschen?", "Delete “{0}” with {1} threads and {2} posts for good?"),
        ("forum.admin.boardDeleted", "„{0}“ gelöscht.", "“{0}” deleted."), ("forum.admin.threadDeleted", "Thema „{0}“ gelöscht.", "Thread “{0}” deleted."),
        ("forum.admin.confirmDeleteThread", "Thema „{0}“ mit {1} Beiträgen löschen?", "Delete thread “{0}” with {1} posts?"),
        ("forum.admin.postDeleted", "Beitrag gelöscht.", "Post deleted."), ("forum.admin.postGone", "Beitrag gibt es nicht mehr", "Post no longer exists"),
        ("forum.admin.reportsOpen", "Offen", "Open"), ("forum.admin.reportsDone", "Erledigt", "Done"), ("forum.admin.markDone", "Erledigt", "Done"), ("forum.admin.reopen", "Wieder öffnen", "Reopen"),
        ("forum.admin.reportDone", "Meldung erledigt.", "Report done."), ("forum.admin.noReports", "Keine offenen Meldungen.", "No open reports."), ("forum.admin.noReportsDone", "Noch nichts erledigt.", "Nothing done yet."),
        ("forum.admin.ban", "Sperren", "Ban"), ("forum.admin.unban", "Entsperren", "Unban"), ("forum.admin.banned", "Gesperrt", "Banned"), ("forum.admin.inactive", "Konto inaktiv", "Account inactive"),
        ("forum.admin.confirmBan", "{0} im Forum sperren? Lesen geht weiter, schreiben nicht mehr.", "Ban {0} from the forum? Reading goes on, posting does not."),
        ("forum.admin.clearSignature", "Signatur entfernen", "Remove signature"), ("forum.admin.noMembers", "Noch hat niemand geschrieben.", "Nobody has posted yet."),
        ("forum.admin.modHint", "Moderatoren sind Mitglieder mit der Rolle „{0}“ – vergeben unter Admin → Mitglieder. Sie moderieren direkt auf der Website.", "Moderators are members holding the role “{0}” – given under Admin → Members. They moderate right on the site."),
        ("forum.admin.tileReports", "{0} offene Meldungen", "{0} open reports"), ("forum.admin.tileThreads", "in {0} Themen", "in {0} threads"),
        ("forum.admin.attentionTitle", "Forum", "Forum"), ("forum.admin.attentionText", "{0} gemeldete Beiträge warten auf die Moderation.", "{0} reported posts are waiting for the moderators."),
        ("forum.admin.cardTitle", "Forum · Neueste Beiträge", "Forum · Latest posts"),
    };
}
