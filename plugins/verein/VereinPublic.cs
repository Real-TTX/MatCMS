using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Verein;

/// <summary>
/// The member area under /mitglied. The sign-in form is on /mitglied itself (it posts to the site's own sign-in, which returns here); every other page
/// of the area sends a visitor who is not signed in back to /mitglied — never to the site's general sign-in page. Only accounts the club has a record
/// for (and has not switched off) get in. Everything a person typed is encoded on output.
/// </summary>
public static class VereinPublic
{
    public const string Base = "/mitglied";

    public static void Configure(IPluginBuilder b)
    {
        // One POST limit for all routes: the host counts per plugin and address over every route together.
        var form = new PublicRouteOptions { Antiforgery = true, DefaultLanguageOnly = true, PostsPerMinute = 60 };
        var read = new PublicRouteOptions { Methods = PluginMethod.Get, DefaultLanguageOnly = true };
        b.Route(Base, HomeAsync, form);
        b.Route(Base + "/dokumente", DocsAsync, read);
        b.Route(Base + "/dokumente/{id}", DocAsync, form);
        b.Route(Base + "/dokumente/{id}/datei", DocFileAsync, read);
        b.Route(Base + "/dokumente/{id}/pdf", DocPdfAsync, read);
        b.Route(Base + "/termine", DatesAsync, form);         // form: its list answers "Dabei" / "Nicht dabei" with the token of this page
        b.Route(Base + "/termine/{id}", EventAsync, form);
        b.Route(Base + "/sitzungen/{id}", MeetingAsync, form);
        b.Route(Base + "/sitzungen/{id}/pdf", MeetingPdfAsync, read);
        b.Route(Base + "/dateien", FilesAsync, form);
        b.Route(Base + "/dateien/{id}", FileAsync, read);
        b.Route(Base + "/hochladen", UploadPageAsync, form);     // form: its page carries the token the uploads are sent with
        b.Route(Base + "/abmelden", SignOutAsync, read);
        b.Route(Base + "/mitglieder", DirectoryAsync, read);
        b.Route(Base + "/foto/{id}", PhotoAsync, read);
        b.Route(Base + "/profil", ProfileAsync, form);
        b.Route(Base + "/vorstand", BoardAsync, read);
    }

    // ---- access -------------------------------------------------------------------------------------------------

    private sealed record Who(Person Person, bool Board);

    /// <summary>The signed-in club member, or the answer to give instead (back to /mitglied, or "no access").</summary>
    private static async Task<(Who? Who, PluginResult? Deny)> GateAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (ctx.Member is null) return (null, PluginResult.Redirect(Base));
        var p = await new VereinStore(ctx.Data).PersonOfMemberAsync(ctx.Member.Id, ct);
        if (p is null || !p.Active) return (null, NoAccess(ctx));
        var board = VereinSettings.BoardList(ctx);
        var isBoard = p.Roles.Any(r => board.Contains(r, StringComparer.OrdinalIgnoreCase));
        // the club requires a second factor: whoever has none is sent to set it up first, and comes back here afterwards
        if (VereinSettings.TwoFactorRequired(ctx, isBoard) && !HasTwoFactor(ctx.Member))
            return (null, PluginResult.Redirect(MemberPaths.Security + "?pflicht=1&returnUrl=" + Uri.EscapeDataString(Base)));
        return (new Who(p, isBoard), null);
    }

    /// <summary>The member's second factor — false on a host older than that API (the property does not exist there; the call is in its own frame).</summary>
    internal static bool HasTwoFactor(PluginMember? m)
    {
        if (m is null) return false;
        try { return TwoFactorOf(m); } catch (MissingMethodException) { return false; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool TwoFactorOf(PluginMember m) => m.TwoFactorEnabled;

    private static PluginResult NoAccess(IPluginContext ctx) => Bare(ctx,
        "<div class=\"vm-card vm-login\"><h1>Kein Zugang</h1><p>Mit diesem Konto gibt es keinen Zugang zum Mitgliederbereich von " + V.H(VereinSettings.Club(ctx)) +
        ". Wende dich bitte an den Vorstand.</p>" + LogoutForm(ctx) + "</div>", "Mitgliederbereich");

    // ---- page frame ---------------------------------------------------------------------------------------------

    private static PluginResult Bare(IPluginContext ctx, string body, string title) =>
        PluginResult.Page("<style>" + VereinCss.Css + "</style><section class=\"section vm\"><div class=\"container\">" + body + "</div></section>", title);

    private static string LogoutForm(IPluginContext ctx) =>
        "<form method=\"post\" action=\"/anmelden?handler=Logout\" class=\"vm-logout\">" + ctx.Antiforgery +
        "<input type=\"hidden\" name=\"returnUrl\" value=\"" + Base + "\"><button class=\"vm-btn ghost small\" type=\"submit\">Abmelden</button></form>";

    /// <summary>
    /// The frame of every member page. Its navigation is the SITE's menu ("Mitglieder" with the pages under it, see <see cref="VereinMenu"/>) — an own
    /// side menu next to it showed every link twice. What is left: where you are, the page's title, who is signed in.
    /// </summary>
    private static Task<PluginResult> ShellAsync(IPluginContext ctx, Who who, string active, string title, string body, CancellationToken ct)
    {
        var crumb = active == "home" ? "Mitgliederbereich" : "<a href=\"" + Base + "\">Mitgliederbereich</a>";
        var html = "<header class=\"vm-head\"><div><p class=\"vm-kicker\">" + crumb + "<span class=\"vm-club\"> · " + V.H(VereinSettings.Club(ctx)) + "</span></p><h1>" + V.H(title) + "</h1></div>" +
                   "<a class=\"vm-me\" href=\"" + Base + "/profil\" title=\"Mein Profil\">" + VereinPhoto.Avatar(who.Person, Base + "/foto/" + who.Person.Id, Initials(who.Person), 36) +
                   "<span>" + V.H(who.Person.First.Length > 0 ? who.Person.First : who.Person.Name) + "</span></a></header>" +
                   "<div class=\"vm-main\">" + body + "</div>";
        return Task.FromResult(Bare(ctx, html, title));
    }

    /// <summary>Sign out from the menu ("Mitglieder → Abmelden"): a plain link, then back to the start page.</summary>
    private static async Task<PluginResult> SignOutAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (ctx.Member is not null) await ctx.Members.SignOutAsync(ct);
        return PluginResult.Redirect("/", "Du bist abgemeldet.");
    }

    /// <summary>The photo upload of one event (from the hint, the event page or the menu) — or of none, with a choice.</summary>
    private static async Task<PluginResult> UploadPageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var store = new VereinStore(ctx.Data);
        var events = (await store.EventsAsync(ct)).Where(e => !e.Draft).ToList();
        var e = long.TryParse(ctx.Request.Q("termin"), out var id) ? events.FirstOrDefault(x => x.Id == id) : null;
        var choices = events.Where(x => string.CompareOrdinal(x.Day, V.Local(DateTime.UtcNow).AddDays(-120).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) >= 0
            && string.CompareOrdinal(x.Day, V.Today) <= 0).OrderByDescending(x => x.Starts).ToList();
        var visible = e is null ? new List<Upload>() : (await store.UploadsAsync(VereinStore.Photo, ct)).Where(u => u.EventId == e.Id && CanSee(u, who!.Person, who.Board)).ToList();
        var done = e is null ? Base + "/dateien" : Base + "/termine/" + e.Id;
        return await ShellAsync(ctx, who!, "files", e is null ? "Bilder hochladen" : "Bilder hochladen", VereinUpload.Page(ctx, e, choices, visible, who!.Board, done), ct);
    }

    /// <summary>The "you were there – upload?" cards for the member.</summary>
    private static async Task<string> UploadHintAsync(IPluginContext ctx, VereinStore store, Person me, CancellationToken ct)
    {
        var replies = (await store.AllRepliesAsync(ct)).Where(r => r.MemberId == me.MemberId).ToDictionary(r => r.EventId, r => r.Answer);
        var declined = replies.Where(r => r.Value == VereinStore.No).Select(r => r.Key).ToHashSet();
        var recent = VereinUpload.Recent(await store.EventsAsync(ct), declined);
        if (recent.Count == 0) return "";
        return VereinUpload.Hint(me, recent, await store.UploadsAsync(ct: ct), replies);
    }

    private static string Notice(IPluginContext ctx) => ctx.Request.Q("ok") switch
    {
        "" => "",
        "antwort" => Ok("Danke! Deine Antwort ist gespeichert."),
        "upload" => Ok("Danke! Die Datei ist hochgeladen."),
        "upload-pruefung" => Ok("Danke! Hochgeladen – die anderen Mitglieder sehen es, sobald der Vorstand es freigegeben hat."),
        "freigegeben" => Ok("Freigegeben – jetzt sehen es alle Mitglieder."),
        "geloescht" => Ok("Die Datei ist gelöscht."),
        "profil" => Ok("Dein Profil ist gespeichert."),
        "passwort" => Ok("Dein Passwort ist geändert."),
        "kalender" => Ok("Dein Kalender-Link ist bereit."),
        "foto" => Ok("Dein Profilbild ist gespeichert."),
        "zusage" => Ok("Danke für deine Rückmeldung!"),
        _ => ""
    };
    private static string Ok(string text) => "<div class=\"vm-ok\" role=\"status\">" + V.H(text) + "</div>";
    private static string Err(string text) => "<div class=\"vm-err\" role=\"alert\">" + V.H(text) + "</div>";

    private static long Id(IPluginContext ctx) => long.TryParse(ctx.Request.R("id"), out var id) ? id : 0;

    // ---- start / sign-in ----------------------------------------------------------------------------------------

    private static async Task<PluginResult> HomeAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (ctx.Member is null)
        {
            var intro = ctx.Settings.Get(VereinSettings.LoginIntro);
            var failed = ctx.Request.Q("fehler") == "1";
            return Bare(ctx, "<div class=\"vm-card vm-login\"><p class=\"vm-kicker\">" + V.H(VereinSettings.Club(ctx)) + "</p><h1>Mitgliederbereich</h1>" +
                (string.IsNullOrWhiteSpace(intro) ? "" : V.Paragraphs(intro)) + (failed ? Err("Anmeldung fehlgeschlagen.") : "") +
                "<form method=\"post\" action=\"/anmelden\" class=\"vm-form\">" + ctx.Antiforgery + "<input type=\"hidden\" name=\"returnUrl\" value=\"" + Base + "\">" +
                "<label class=\"vm-field\"><span>E-Mail-Adresse</span><input name=\"username\" type=\"email\" autocomplete=\"username\" required></label>" +
                "<label class=\"vm-field\"><span>Passwort</span><input name=\"password\" type=\"password\" autocomplete=\"current-password\" required></label>" +
                "<label class=\"vm-check\"><input type=\"checkbox\" name=\"remember\" value=\"true\"> <span>Angemeldet bleiben</span></label>" +
                "<button class=\"vm-btn\" type=\"submit\">Anmelden</button></form>" +
                "<p class=\"vm-muted\"><a href=\"/anmelden/vergessen\">Passwort vergessen oder noch keins festgelegt?</a></p></div>", "Mitgliederbereich");
        }
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var store = new VereinStore(ctx.Data);
        var p = who!.Person;

        var open = await OpenDocsAsync(ctx, store, p, ct);
        // only a REQUIRED reply is a duty: an event that merely offers zu-/absagen is answered on its page and in the list of dates, never listed as missing
        var replies = (await OpenRepliesAsync(ctx, store, p, ct)).Where(e => e.AskRequired).ToList();
        var meetings = (await MeetingsForAsync(ctx, store, who, ct)).Where(m => m.Status == VereinStore.Planned && string.CompareOrdinal(m.Starts.Length >= 10 ? m.Starts[..10] : m.Starts, V.Today) >= 0)
            .OrderBy(m => m.Starts).Take(3).ToList();
        var events = await store.UpcomingAsync(V.Today, publicOnly: false, 4, ct);
        var rsvps = (await store.RsvpsOfMemberAsync(p.MemberId, ct)).ToDictionary(r => r.MeetingId, r => r.Answer);
        var uploads = (await store.UploadsAsync(ct: ct)).Where(u => u.ForEveryone).Take(6).ToList();

        var sb = new StringBuilder(Notice(ctx));
        sb.Append(await UploadHintAsync(ctx, store, p, ct));
        // the area's places as tiles — the site's menu has them too, but on a phone this is where one starts
        string Tile(string href, string icon, string label, string? note = null, bool hot = false) =>
            "<a class=\"vm-tile" + (hot ? " hot" : "") + "\" href=\"" + href + "\"><i class=\"ti ti-" + icon + "\" aria-hidden=\"true\"></i><strong>" + V.H(label) + "</strong>" +
            (note is null ? "" : "<small>" + V.H(note) + "</small>") + "</a>";
        sb.Append("<nav class=\"vm-tiles\" aria-label=\"Mitgliederbereich\">")
          .Append(Tile(Base + "/dokumente", "file-check", "Dokumente", open.Count > 0 ? open.Count + " offen" : "alles erledigt", open.Count > 0))
          .Append(Tile(Base + "/termine", "calendar", "Termine", events.Count > 0 ? "nächster: " + V.Date(events[0].Starts) : null))
          .Append(Tile(Base + "/hochladen", "photo-up", "Bilder hochladen"))
          .Append(Tile(Base + "/dateien", "photo", "Bilder & Material"))
          .Append(Tile(Base + "/mitglieder", "users", "Mitglieder"))
          .Append(Tile(Base + "/profil", "user", "Mein Profil"));
        if (who.Board) sb.Append(Tile(Base + "/vorstand", "clipboard-list", "Vorstand"));
        sb.Append("</nav>");
        sb.Append("<div class=\"vm-grid2\">");
        sb.Append("<section class=\"vm-card vm-todo").Append(open.Count == 0 && replies.Count == 0 ? " is-empty" : "").Append("\"><h2><i class=\"ti ti-file-check\"></i> Zu erledigen</h2>");
        if (open.Count == 0 && replies.Count == 0) sb.Append("<p class=\"vm-muted\">Alles erledigt – danke!</p>");
        else
        {
            sb.Append("<ul class=\"vm-list\">");
            foreach (var e in replies) sb.Append("<li><a href=\"").Append(Base).Append("/termine/").Append(e.Id).Append("\"><strong>").Append(V.H(e.Title)).Append("</strong>")
                .Append("<span>").Append(e.AskRequired ? "Rückmeldung erforderlich" : "Bist du dabei?").Append(" · ").Append(V.H(V.When(e.Starts)))
                .Append(e.AskDue.Length > 0 ? " · bitte bis " + V.H(V.Date(e.AskDue)) : "").Append("</span></a></li>");
            foreach (var d in open) sb.Append("<li><a href=\"").Append(Base).Append("/dokumente/").Append(d.Id).Append("\"><strong>").Append(V.H(d.Title)).Append("</strong>")
                .Append("<span>Bitte ").Append(VereinMail.Task(d.Kind)).Append(V.H(VereinMail.Due(d.Due))).Append("</span></a></li>");
            sb.Append("</ul>");
        }
        sb.Append("</section>");
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-users-group\"></i> Nächste Sitzungen</h2>");
        if (meetings.Count == 0) sb.Append("<p class=\"vm-muted\">Gerade ist keine Sitzung geplant.</p>");
        else
        {
            sb.Append("<ul class=\"vm-list\">");
            foreach (var m in meetings)
                sb.Append("<li><a href=\"").Append(Base).Append("/sitzungen/").Append(m.Id).Append("\"><strong>").Append(V.H(m.Title)).Append("</strong><span>")
                  .Append(V.H(V.When(m.Starts))).Append(m.Place.Length > 0 ? " · " + V.H(m.Place) : "").Append(" · ")
                  .Append(rsvps.TryGetValue(m.Id, out var a) ? AnswerLabel(a) : "noch keine Rückmeldung").Append("</span></a></li>");
            sb.Append("</ul>");
        }
        sb.Append("</section></div>");

        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-calendar-event\"></i> Kommende Termine</h2>").Append(await AgendaAsync(ctx, store, p, events, "home", ct))
          .Append("<p class=\"vm-agenda-more\"><a href=\"").Append(Base).Append("/termine\">Alle Termine →</a></p></section>");

        if (uploads.Count > 0)
        {
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-photo\"></i> Neu hochgeladen</h2><div class=\"vm-thumbs\">");
            foreach (var u in uploads) sb.Append(Thumb(u));
            sb.Append("</div><p><a href=\"").Append(Base).Append("/dateien\">Alle Bilder und Materialien →</a></p></section>");
        }
        return await ShellAsync(ctx, who, "home", "Hallo " + (p.First.Length > 0 ? p.First : p.Name) + "!", sb.ToString(), ct);
    }

    // ---- documents ----------------------------------------------------------------------------------------------

    private static async Task<List<Doc>> OpenDocsAsync(IPluginContext ctx, VereinStore store, Person p, CancellationToken ct)
    {
        var answered = (await store.AnswersOfMemberAsync(p.MemberId, ct)).Select(a => a.DocId).ToHashSet();
        var board = VereinSettings.BoardList(ctx);
        return (await store.DocsAsync(archived: false, ct)).Where(d => VereinStore.InAudience(p, d.Audience, board) && !answered.Contains(d.Id)).ToList();
    }

    private static async Task<PluginResult> DocsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var store = new VereinStore(ctx.Data);
        var board = VereinSettings.BoardList(ctx);
        var answers = (await store.AnswersOfMemberAsync(who!.Person.MemberId, ct)).ToDictionary(a => a.DocId);
        var docs = (await store.DocsAsync(false, ct)).Where(d => VereinStore.InAudience(who.Person, d.Audience, board)).ToList();

        var sb = new StringBuilder(Notice(ctx));
        sb.Append("<p class=\"vm-lead\">Hier liegen die Dokumente des Vereins für dich – zum Bestätigen, Unterschreiben oder Lesen. Deine Antwort wird mit Datum und Uhrzeit gespeichert.</p>");
        if (docs.Count == 0) sb.Append("<div class=\"vm-empty\">Gerade liegt kein Dokument für dich bereit.</div>");
        else
        {
            sb.Append("<div class=\"vm-rows\">");
            foreach (var d in docs)
            {
                answers.TryGetValue(d.Id, out var a);
                sb.Append("<a class=\"vm-row\" href=\"").Append(Base).Append("/dokumente/").Append(d.Id).Append("\"><span class=\"vm-row-main\"><strong>").Append(V.H(d.Title))
                  .Append("</strong><small>").Append(KindLabel(d.Kind)).Append(d.Due.Length > 0 ? " · bis " + V.H(V.Date(d.Due)) : "").Append("</small></span>")
                  .Append(a is null ? "<span class=\"vm-badge warn\">offen</span>" : a.Status == VereinStore.Accepted
                      ? "<span class=\"vm-badge ok\">" + (d.Kind == VereinStore.Read ? "gelesen" : d.Kind == VereinStore.Sign ? "unterschrieben" : "bestätigt") + " am " + V.H(V.Stamp(a.At)) + "</span>"
                      : "<span class=\"vm-badge err\">abgelehnt am " + V.H(V.Stamp(a.At)) + "</span>")
                  .Append("</a>");
            }
            sb.Append("</div>");
        }
        return await ShellAsync(ctx, who, "docs", "Dokumente", sb.ToString(), ct);
    }

    private static async Task<PluginResult> DocAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var store = new VereinStore(ctx.Data);
        var d = await store.DocAsync(Id(ctx), ct);
        if (d is null || d.Archived || !VereinStore.InAudience(who!.Person, d.Audience, VereinSettings.BoardList(ctx))) return PluginResult.NotFound();
        var mine = (await store.AnswersOfMemberAsync(who.Person.MemberId, ct)).FirstOrDefault(a => a.DocId == d.Id);

        string? error = null;
        if (ctx.Request.IsPost && mine is null)
        {
            var decline = ctx.Request.Action == "decline" && d.Kind != VereinStore.Read;
            var sig = V.Clip(ctx.Request.F("signature"), 120);
            if (!decline && !V.Checked(ctx.Request.F("agree"))) error = d.Kind == VereinStore.Read ? "Bitte bestätige, dass du das Dokument gelesen hast." : "Bitte setze den Haken zur Bestätigung.";
            else if (!decline && d.Kind == VereinStore.Sign && !SameName(sig, who.Person.Name))
                error = "Bitte tippe zum Unterschreiben deinen vollständigen Namen genau so ein: " + who.Person.Name;
            else
            {
                await store.AnswerAsync(d.Id, who.Person.MemberId, decline ? VereinStore.Declined : VereinStore.Accepted, d.Kind == VereinStore.Sign && !decline ? sig : "", ctx.Request.ClientIp, ct);
                ctx.Log($"Dokument #{d.Id} „{d.Title}“: {(decline ? "abgelehnt" : "angenommen")} von {who.Person.Name}");
                return PluginResult.Redirect(Base + "/dokumente?ok=antwort");
            }
        }

        var sb = new StringBuilder();
        sb.Append("<p><a href=\"").Append(Base).Append("/dokumente\">← Alle Dokumente</a></p><article class=\"vm-card vm-doc\">");
        sb.Append("<p class=\"vm-kicker\">").Append(KindLabel(d.Kind)).Append(d.Due.Length > 0 ? " · bitte bis " + V.H(V.Date(d.Due)) : "").Append("</p>");
        sb.Append("<div class=\"vm-text\">").Append(V.Paragraphs(d.Body)).Append("</div>");
        if (d.File.Length > 0)
            sb.Append("<p><a class=\"vm-btn ghost\" href=\"").Append(Base).Append("/dokumente/").Append(d.Id).Append("/datei\"><i class=\"ti ti-download\"></i> ")
              .Append(V.H(d.FileName.Length > 0 ? d.FileName : "Datei öffnen")).Append("</a></p>");
        sb.Append("<p><a class=\"vm-btn ghost small\" href=\"").Append(Base).Append("/dokumente/").Append(d.Id).Append("/pdf\" target=\"_blank\" rel=\"noopener\"><i class=\"ti ti-printer\"></i> Als PDF / drucken</a></p>");
        sb.Append("</article>");

        if (mine is not null)
            sb.Append(mine.Status == VereinStore.Accepted
                ? Ok((d.Kind == VereinStore.Sign ? "Unterschrieben als „" + mine.Signature + "“" : d.Kind == VereinStore.Read ? "Gelesen" : "Bestätigt") + " am " + V.Stamp(mine.At) + ".")
                : "<div class=\"vm-err\">Abgelehnt am " + V.H(V.Stamp(mine.At)) + ". Wenn du das ändern möchtest, wende dich bitte an den Vorstand.</div>");
        else
        {
            sb.Append("<form method=\"post\" class=\"vm-card vm-form vm-answer\">").Append(ctx.Antiforgery);
            if (error is not null) sb.Append(Err(error));
            var agree = d.Kind switch
            {
                VereinStore.Read => "Ich habe das Dokument gelesen.",
                VereinStore.Sign => "Ich habe das Dokument gelesen und stimme ihm zu.",
                _ => "Ich habe das Dokument gelesen und bin einverstanden."
            };
            sb.Append("<label class=\"vm-check\"><input type=\"checkbox\" name=\"agree\" value=\"1\"> <span>").Append(V.H(agree)).Append("</span></label>");
            if (d.Kind == VereinStore.Sign)
                sb.Append("<label class=\"vm-field\"><span>Unterschrift: dein vollständiger Name</span><input name=\"signature\" autocomplete=\"name\" placeholder=\"")
                  .Append(V.H(who.Person.Name)).Append("\" maxlength=\"120\"><small>Mit dem Eintippen deines Namens unterschreibst du elektronisch. Gespeichert werden Name, Datum, Uhrzeit und die Adresse deines Geräts.</small></label>");
            sb.Append("<div class=\"vm-actions\"><button class=\"vm-btn\" type=\"submit\" name=\"action\" value=\"accept\">")
              .Append(d.Kind switch { VereinStore.Sign => "Unterschreiben", VereinStore.Read => "Gelesen", _ => "Bestätigen" }).Append("</button>");
            if (d.Kind != VereinStore.Read)
                sb.Append("<button class=\"vm-btn ghost\" type=\"submit\" name=\"action\" value=\"decline\" data-confirm=\"Wirklich ablehnen?\">Nicht einverstanden</button>");
            sb.Append("</div></form>");
        }
        return await ShellAsync(ctx, who, "docs", d.Title, sb.ToString(), ct);
    }

    private static bool SameName(string typed, string name)
    {
        static string N(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return typed.Trim().Length >= 3 && N(typed) == N(name);
    }

    private static async Task<PluginResult> DocFileAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var d = await new VereinStore(ctx.Data).DocAsync(Id(ctx), ct);
        if (d is null || d.File.Length == 0 || (!who!.Board && (d.Archived || !VereinStore.InAudience(who.Person, d.Audience, VereinSettings.BoardList(ctx))))) return PluginResult.NotFound();
        return await ServeAsync(ctx, d.File, d.FileName, d.FileType, ct);
    }

    private static async Task<PluginResult> DocPdfAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var store = new VereinStore(ctx.Data);
        var d = await store.DocAsync(Id(ctx), ct);
        if (d is null || (!who!.Board && (d.Archived || !VereinStore.InAudience(who.Person, d.Audience, VereinSettings.BoardList(ctx))))) return PluginResult.NotFound();
        var mine = (await store.AnswersOfMemberAsync(who.Person.MemberId, ct)).FirstOrDefault(a => a.DocId == d.Id);
        return VereinPrint.Result(VereinPrint.Document(await VereinPrint.LetterheadAsync(ctx, ct), d, mine), d.Title);
    }

    /// <summary>Invitation for whoever is invited; the minutes only once they are published.</summary>
    private static async Task<PluginResult> MeetingPdfAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var m = await new VereinStore(ctx.Data).MeetingAsync(Id(ctx), ct);
        if (m is null || (!who!.Board && !VereinStore.InAudience(who.Person, m.Audience, VereinSettings.BoardList(ctx)))) return PluginResult.NotFound();
        var minutes = ctx.Request.Q("teil") == "protokoll";
        if (minutes && (m.MinutesStatus != VereinStore.Final || m.Minutes.Trim().Length == 0)) return PluginResult.NotFound();
        return VereinPrint.Result(VereinPrint.Meeting(await VereinPrint.LetterheadAsync(ctx, ct), m, minutes), (minutes ? "Protokoll " : "Einladung ") + m.Title);
    }

    internal static async Task<PluginResult> ServeAsync(IPluginContext ctx, string file, string name, string type, CancellationToken ct)
    {
        var stream = await ctx.Files.OpenAsync(file, ct);
        if (stream is null) return PluginResult.NotFound();
        using (stream) using (var ms = new MemoryStream())
        {
            await stream.CopyToAsync(ms, ct);
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var inline = V.IsImage(ext) || ext == ".pdf";
            // inline only for pictures and PDF; everything else is a download. Never sniffed, never cached by a shared cache.
            return PluginResult.File(ms.ToArray(), V.Types.TryGetValue(ext, out var t) ? t : "application/octet-stream", inline ? null : V.SafeName(name))
                .WithHeader("X-Content-Type-Options", "nosniff").WithHeader("Cache-Control", "private, max-age=3600")
                .WithHeader("Content-Security-Policy", "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; sandbox");
        }
    }

    private static string KindLabel(string kind) => kind switch { VereinStore.Sign => "Zum Unterschreiben", VereinStore.Read => "Zum Lesen", _ => "Zum Bestätigen" };

    // ---- dates: events and meetings -----------------------------------------------------------------------------

    private static async Task<List<Meeting>> MeetingsForAsync(IPluginContext ctx, VereinStore store, Who who, CancellationToken ct)
    {
        var board = VereinSettings.BoardList(ctx);
        return (await store.MeetingsAsync(ct)).Where(m => who.Board || VereinStore.InAudience(who.Person, m.Audience, board)).ToList();
    }

    private static string AnswerLabel(string a) => a switch { VereinStore.Yes => "du kommst", VereinStore.No => "du kommst nicht", _ => "du weißt es noch nicht" };

    /// <summary>Events that still wait for this member's reply — for themself or for one of their children.</summary>
    private static async Task<List<Event>> OpenRepliesAsync(IPluginContext ctx, VereinStore store, Person me, CancellationToken ct)
    {
        var events = (await store.EventsAsync(ct)).Where(e => e.ReplyOpen(V.Today)).ToList();
        if (events.Count == 0) return events;
        var people = await store.PeopleAsync(ct);
        var board = VereinSettings.BoardList(ctx);
        var replied = (await store.AllRepliesAsync(ct)).Select(r => (r.EventId, r.MemberId)).ToHashSet();
        return events.Where(e => VereinStore.AnswersFor(people, me, e, board).Any(p => !replied.Contains((e.Id, p.MemberId)))).ToList();
    }

    internal static string EventList(IEnumerable<Event> events, bool internalBadge, bool memberLinks = false, ISet<long>? needsReply = null, bool publicLinks = false)
    {
        var list = events.ToList();
        if (list.Count == 0) return "<p class=\"vm-muted\">Gerade stehen keine Termine an.</p>";
        var sb = new StringBuilder("<ul class=\"vm-events\">");
        foreach (var e in list)
        {
            var (day, month, wd) = V.Badge(e.Starts);
            sb.Append("<li class=\"").Append(e.Cancelled ? "is-cancelled" : "").Append("\"><span class=\"vm-date\"><b>").Append(V.H(day)).Append("</b><small>").Append(V.H(month)).Append("</small><em>")
              .Append(V.H(wd)).Append("</em></span><span class=\"vm-ev\"><strong>").Append(memberLinks ? "<a href=\"" + Base + "/termine/" + e.Id + "\">" + V.H(e.Title) + "</a>" : publicLinks && e.Public && !e.Draft ? "<a href=\"" + VereinEvents.PagePath(e.Id) + "\">" + V.H(e.Title) + "</a>" : V.H(e.Title)).Append("</strong>");
            if (memberLinks && e.HasPlan) sb.Append(" <span class=\"vm-badge ok\">Ablauf &amp; Infos</span>");
            if (needsReply?.Contains(e.Id) == true) sb.Append(e.AskRequired ? " <span class=\"vm-badge warn\">Rückmeldung erforderlich</span>" : " <span class=\"vm-badge\">Bist du dabei?</span>");
            if (e.Cancelled) sb.Append(" <span class=\"vm-badge err\">abgesagt</span>");
            if (internalBadge && !e.Public) sb.Append(" <span class=\"vm-badge\">intern</span>");
            sb.Append("<small>").Append(V.H(V.When(e.Starts)));
            if (e.Ends.Length > 0 && e.Ends != e.Starts) sb.Append(" – ").Append(V.H(e.Ends[..Math.Min(10, e.Ends.Length)] == e.Day ? V.When(e.Ends).Split(", ").Last() : V.When(e.Ends)));
            if (e.Place.Length > 0) sb.Append(" · ").Append(V.H(e.Place));
            sb.Append("</small>");
            if (e.Description.Length > 0) sb.Append("<span class=\"vm-ev-text\">").Append(V.H(e.Description)).Append("</span>");
            if (e.Link.StartsWith("https://", StringComparison.Ordinal) || (e.Link.StartsWith('/') && !e.Link.StartsWith("//", StringComparison.Ordinal)))
                sb.Append("<a href=\"").Append(V.H(e.Link)).Append("\">Mehr erfahren →</a>");
            sb.Append("</span></li>");
        }
        return sb.Append("</ul>").ToString();
    }

    /// <summary>
    /// The dates as the member area shows them: a schedule grouped by month, ONE line per date — day, title, time and place, the organiser on a line
    /// of its own, clipped — and at the right what this member said. Who answers only for themself says "Dabei" / "Nicht dabei" right here (two
    /// buttons of one small form, the chosen one filled, a click changes it); who answers for children too sees how many are answered and opens the
    /// date. The list before — the date written out again under every title, two badges each on a line of its own, the whole description — was a wall
    /// once every date asked for a reply. <paramref name="back"/> ("home", "termine") is where a quick answer returns to.
    /// </summary>
    private static async Task<string> AgendaAsync(IPluginContext ctx, VereinStore store, Person me, IReadOnlyList<Event> events, string back, CancellationToken ct)
    {
        if (events.Count == 0) return "<p class=\"vm-muted\">Gerade stehen keine Termine an.</p>";
        var asking = events.Any(e => e.Ask && !e.Cancelled);
        var people = asking ? await store.PeopleAsync(ct) : Array.Empty<Person>();
        var board = VereinSettings.BoardList(ctx);
        var answers = asking ? (await store.AllRepliesAsync(ct)).GroupBy(r => (r.EventId, r.MemberId)).ToDictionary(g => g.Key, g => g.First().Answer) : new Dictionary<(long, long), string>();
        var today = V.Today;
        var sb = new StringBuilder("<div class=\"vm-agenda\">");
        string? month = null;
        foreach (var e in events)
        {
            var title = MonthTitle(e.Starts);
            if (title != month) { sb.Append("<h3 class=\"vm-agenda-month\">").Append(V.H(title)).Append("</h3>"); month = title; }
            var (day, _, wd) = V.Badge(e.Starts);
            sb.Append("<div class=\"vm-agenda-row").Append(e.Cancelled ? " is-cancelled" : "").Append("\"><a class=\"vm-agenda-main\" href=\"").Append(Base).Append("/termine/").Append(e.Id)
              .Append("\"><span class=\"vm-date sm\"><b>").Append(V.H(day)).Append("</b><em>").Append(V.H(wd)).Append("</em></span><span class=\"vm-agenda-text\"><strong>").Append(V.H(e.Title));
            if (e.HasPlan) sb.Append(" <i class=\"ti ti-list-details\" title=\"Ablauf &amp; Infos\" aria-label=\"Ablauf &amp; Infos\"></i>");
            sb.Append("</strong>");
            var meta = new List<string>();
            if (TimeOf(e) is { Length: > 0 } time) meta.Add(time);
            if (e.Place.Length > 0) meta.Add(e.Place);
            if (meta.Count > 0) sb.Append("<small>").Append(V.H(string.Join(" · ", meta))).Append("</small>");
            if (e.Description.Length > 0) sb.Append("<small class=\"vm-agenda-desc\">").Append(V.H(e.Description.Split('\n')[0].Trim())).Append("</small>");
            sb.Append("</span></a><div class=\"vm-agenda-side\">");
            if (!e.Public) sb.Append("<span class=\"vm-badge\">intern</span>");
            if (e.Cancelled) sb.Append("<span class=\"vm-badge err\">abgesagt</span>");
            else if (e.Ask)
            {
                var mine = VereinStore.AnswersFor(people, me, e, board);
                var open = e.ReplyOpen(today);
                if (mine.Count == 1 && open)
                {
                    var self = mine[0];
                    answers.TryGetValue((e.Id, self.MemberId), out var a);
                    if (e.AskRequired && a is null) sb.Append("<span class=\"vm-badge warn\">Pflicht</span>");
                    sb.Append("<form method=\"post\" action=\"").Append(Base).Append("/termine/").Append(e.Id).Append("\" class=\"vm-quick\" aria-label=\"Bist du dabei?\">").Append(ctx.Antiforgery)
                      .Append("<input type=\"hidden\" name=\"action\" value=\"reply\"><input type=\"hidden\" name=\"back\" value=\"").Append(back).Append("\">")
                      .Append("<button class=\"vm-pick yes").Append(a == VereinStore.Yes ? " on" : "").Append("\" name=\"a.").Append(self.Id).Append("\" value=\"yes\" aria-pressed=\"").Append(a == VereinStore.Yes ? "true" : "false").Append("\"><i class=\"ti ti-check\" aria-hidden=\"true\"></i> Dabei</button>")
                      .Append("<button class=\"vm-pick no").Append(a == VereinStore.No ? " on" : "").Append("\" name=\"a.").Append(self.Id).Append("\" value=\"no\" aria-pressed=\"").Append(a == VereinStore.No ? "true" : "false").Append("\">Nicht dabei</button></form>");
                    if (a == VereinStore.Maybe) sb.Append("<span class=\"vm-badge warn\">vielleicht</span>");
                }
                else if (mine.Count > 0)
                {
                    var done = mine.Count(x => answers.ContainsKey((e.Id, x.MemberId)));
                    sb.Append(done == mine.Count ? "<span class=\"vm-badge ok\">beantwortet</span>"
                        : open ? "<a class=\"vm-badge" + (e.AskRequired ? " warn" : "") + "\" href=\"" + Base + "/termine/" + e.Id + "\">" + done + " von " + mine.Count + " beantwortet →</a>" : "");
                }
            }
            sb.Append("</div></div>");
        }
        return sb.Append("</div>").Append(QuickScript).ToString();
    }

    /// <summary>"Dabei" / "Nicht dabei" without a reload — on a phone the page would start at the top again after every answer. (getAttribute: the form has a field named "action", so f.action is that field.) Without the script (or
    /// when the request fails) the form is sent as it is and the page comes back with the answer.</summary>
    private const string QuickScript = """
<script>(function(){if(window.__vmQuick)return;window.__vmQuick=1;
document.addEventListener('submit',function(e){var f=e.target;if(!f.classList||!f.classList.contains('vm-quick')||!window.fetch||!window.FormData||f.dataset.plain)return;
e.preventDefault();var b=e.submitter;var d=new FormData(f);if(b&&b.name)d.append(b.name,b.value);d.append('ajax','1');f.classList.add('is-busy');
fetch(f.getAttribute('action'),{method:'POST',body:d,credentials:'same-origin',headers:{Accept:'application/json'}}).then(function(r){if(!r.ok)throw 0;return r.json();}).then(function(j){if(!j||!j.ok)throw 0;
f.querySelectorAll('.vm-pick').forEach(function(x){var on=x===b;x.classList.toggle('on',on);x.setAttribute('aria-pressed',on?'true':'false');});
var side=f.parentNode;side.querySelectorAll('.vm-badge.warn').forEach(function(x){x.remove();});f.classList.remove('is-busy');})
.catch(function(){f.classList.remove('is-busy');f.dataset.plain='1';if(b&&b.name){var h=document.createElement('input');h.type='hidden';h.name=b.name;h.value=b.value;f.appendChild(h);}f.submit();});});})();</script>
""";

    private static string MonthTitle(string starts) =>
        starts.Length >= 7 && int.TryParse(starts[5..7], NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= 1 and <= 12
            ? VereinInstagram.MonthNames[m - 1] + " " + starts[..4] : "";

    /// <summary>"14:30 Uhr", "14:30–18:00 Uhr", "bis Mo, 10. Februar 2027" — the day itself is in the date tile.</summary>
    private static string TimeOf(Event e)
    {
        static string Hm(string s) => s.Length >= 16 && s[11..16] != "00:00" ? s[11..16] : "";
        var start = Hm(e.Starts);
        var endsOtherDay = e.Ends.Length >= 10 && e.Ends[..10] != e.Day;
        if (endsOtherDay) return (start.Length > 0 ? start + " Uhr " : "") + "bis " + V.When(e.Ends);
        var end = e.Ends.Length > 0 ? Hm(e.Ends) : "";
        return start.Length == 0 ? "" : end.Length > 0 && end != start ? start + "–" + end + " Uhr" : start + " Uhr";
    }

    private static async Task<PluginResult> DatesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var store = new VereinStore(ctx.Data);
        var events = await store.UpcomingAsync(V.Today, publicOnly: false, 100, ct);
        var meetings = await MeetingsForAsync(ctx, store, who!, ct);
        var rsvps = (await store.RsvpsOfMemberAsync(who!.Person.MemberId, ct)).ToDictionary(r => r.MeetingId, r => r.Answer);
        var upcoming = meetings.Where(m => m.Status != VereinStore.Held && string.CompareOrdinal(m.Starts.Length >= 10 ? m.Starts[..10] : m.Starts, V.Today) >= 0).OrderBy(m => m.Starts).ToList();
        var past = meetings.Where(m => !upcoming.Contains(m)).OrderByDescending(m => m.Starts).Take(20).ToList();

        string MeetingRow(Meeting m, bool showRsvp) =>
            "<a class=\"vm-row\" href=\"" + Base + "/sitzungen/" + m.Id + "\"><span class=\"vm-row-main\"><strong>" + V.H(m.Title) + "</strong><small>" + V.H(V.When(m.Starts)) +
            (m.Place.Length > 0 ? " · " + V.H(m.Place) : "") + "</small></span>" +
            (m.Status == VereinStore.Cancelled ? "<span class=\"vm-badge err\">abgesagt</span>"
                : showRsvp ? (rsvps.TryGetValue(m.Id, out var a) ? "<span class=\"vm-badge " + (a == VereinStore.Yes ? "ok" : a == VereinStore.No ? "err" : "warn") + "\">" + AnswerLabel(a) + "</span>" : "<span class=\"vm-badge warn\">Rückmeldung fehlt</span>")
                : m.MinutesStatus == VereinStore.Final ? "<span class=\"vm-badge ok\">Protokoll</span>" : "") + "</a>";

        var sb = new StringBuilder(Notice(ctx));
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-calendar-event\"></i> Termine</h2>").Append(await AgendaAsync(ctx, store, who.Person, events, "termine", ct)).Append("</section>");
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-users-group\"></i> Sitzungen</h2>");
        if (upcoming.Count == 0) sb.Append("<p class=\"vm-muted\">Gerade ist keine Sitzung geplant.</p>");
        else sb.Append("<div class=\"vm-rows\">").Append(string.Concat(upcoming.Select(m => MeetingRow(m, true)))).Append("</div>");
        if (past.Count > 0) sb.Append("<h3>Vergangene Sitzungen</h3><div class=\"vm-rows\">").Append(string.Concat(past.Select(m => MeetingRow(m, false)))).Append("</div>");
        sb.Append("</section>");
        return await ShellAsync(ctx, who, "dates", "Termine & Sitzungen", sb.ToString(), ct);
    }

    /// <summary>One event for members: the basics and the planning — running order, meeting point, parking, notes. A draft is not shown.</summary>
    private static async Task<PluginResult> EventAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var store = new VereinStore(ctx.Data);
        var e = await store.EventAsync(Id(ctx), ct);
        if (e is null || e.Draft) return PluginResult.NotFound();
        var people = e.Ask ? await store.PeopleAsync(ct) : Array.Empty<Person>();
        var board = VereinSettings.BoardList(ctx);
        var mine = e.Ask ? VereinStore.AnswersFor(people, who!.Person, e, board) : new List<Person>();

        if (ctx.Request.IsPost)
        {
            if (ctx.Request.Action != "reply" || !e.ReplyOpen(V.Today) || mine.Count == 0) return PluginResult.Redirect(Base + "/termine/" + e.Id);
            var before = ctx.Request.Form.Keys.Any(k => k.StartsWith("n.", StringComparison.Ordinal)) ? null : (await store.RepliesAsync(e.Id, ct)).ToDictionary(r => r.MemberId, r => r.Note);
            foreach (var p in mine)
                if (ctx.Request.F("a." + p.Id) is VereinStore.Yes or VereinStore.No or VereinStore.Maybe)
                {
                    // the list's two buttons send no note: the one written on the date's page stays
                    var note = before is null ? V.Clip(ctx.Request.F("n." + p.Id), 200).Replace("\n", " ") : before.GetValueOrDefault(p.MemberId, "");
                    await store.SetReplyAsync(e.Id, p.MemberId, ctx.Request.F("a." + p.Id), note, who!.Person.MemberId, ct);
                }
            if (ctx.Request.F("ajax") == "1") return PluginResult.Json(new System.Text.Json.Nodes.JsonObject { ["ok"] = true });
            return PluginResult.Redirect(ctx.Request.F("back") switch { "termine" => Base + "/termine?ok=zusage", "home" => Base + "?ok=zusage", _ => Base + "/termine/" + e.Id + "?ok=zusage" });
        }

        var sb = new StringBuilder(Notice(ctx) + "<p><a href=\"" + Base + "/termine\">← Termine & Sitzungen</a></p>");
        sb.Append("<section class=\"vm-card\">");
        if (e.Cancelled) sb.Append("<div class=\"vm-err\">Dieser Termin ist abgesagt.</div>");
        sb.Append("<dl class=\"vm-facts\"><dt>Wann</dt><dd>").Append(V.H(V.When(e.Starts)));
        if (e.Ends.Length > 0 && e.Ends != e.Starts) sb.Append(" – ").Append(V.H(e.Ends[..Math.Min(10, e.Ends.Length)] == e.Day ? V.When(e.Ends).Split(", ").Last() : V.When(e.Ends)));
        sb.Append("</dd>");
        if (e.Place.Length > 0) sb.Append("<dt>Wo</dt><dd>").Append(V.H(e.Place)).Append("</dd>");
        if (e.Kind.Length > 0) sb.Append("<dt>Art</dt><dd>").Append(V.H(e.Kind)).Append("</dd>");
        if (e.Meeting.Length > 0) sb.Append("<dt>Treffpunkt</dt><dd>").Append(V.H(e.Meeting)).Append("</dd>");
        sb.Append("</dl>");
        if (e.Description.Length > 0) sb.Append("<div class=\"vm-text\">").Append(V.Paragraphs(e.Description)).Append("</div>");
        if (e.Link.StartsWith("https://", StringComparison.Ordinal) || (e.Link.StartsWith('/') && !e.Link.StartsWith("//", StringComparison.Ordinal)))
            sb.Append("<p><a href=\"").Append(V.H(e.Link)).Append("\">Mehr erfahren →</a></p>");
        sb.Append("</section>");
        if (e.Ask && !e.Cancelled) sb.Append(await ReplyCardAsync(ctx, store, e, mine, people, who!.Board, ct));
        if (e.Steps.Count > 0)
        {
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-clock\"></i> Ablauf</h2><table class=\"vm-table vm-plan\"><tbody>");
            foreach (var (time, what) in e.Steps) sb.Append("<tr><th>").Append(V.H(time)).Append("</th><td>").Append(V.H(what)).Append("</td></tr>");
            sb.Append("</tbody></table></section>");
        }
        if (e.Parking.Length > 0) sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-parking\"></i> Parken & Anfahrt</h2><div class=\"vm-text\">").Append(V.Paragraphs(e.Parking)).Append("</div></section>");
        if (e.Notes.Length > 0) sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-info-circle\"></i> Hinweise</h2><div class=\"vm-text\">").Append(V.Paragraphs(e.Notes)).Append("</div></section>");
        if (!e.HasPlan) sb.Append("<p class=\"vm-muted\">Ablauf und weitere Infos folgen – der Vorstand trägt sie nach.</p>");
        var photos = (await store.UploadsAsync(VereinStore.Photo, ct)).Where(u => u.EventId == e.Id && CanSee(u, who!.Person, who.Board)).ToList();
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-photo\"></i> Bilder (").Append(photos.Count).Append(")</h2>");
        if (photos.Count > 0) sb.Append("<div class=\"vm-thumbs\">").Append(string.Concat(photos.Take(24).Select(Thumb))).Append("</div>");
        sb.Append("<p class=\"vm-actions\"><a class=\"vm-btn\" href=\"").Append(Base).Append("/hochladen?termin=").Append(e.Id).Append("\"><i class=\"ti ti-photo-up\"></i> Bilder hochladen</a>");
        if (photos.Count > 24) sb.Append("<a class=\"vm-btn ghost\" href=\"").Append(Base).Append("/dateien?termin=").Append(e.Id).Append("\">Alle ").Append(photos.Count).Append(" Bilder</a>");
        sb.Append("</p></section>");
        return await ShellAsync(ctx, who!, "dates", e.Title, sb.ToString(), ct);
    }

    internal static string ReplyLabel(string a) => a switch { VereinStore.Yes => "dabei", VereinStore.No => "nicht dabei", _ => "weiß noch nicht" };

    /// <summary>„Bist du dabei?“ for the member and each of their children; the board sees who has answered.</summary>
    private static async Task<string> ReplyCardAsync(IPluginContext ctx, VereinStore store, Event e, List<Person> mine, IReadOnlyList<Person> people, bool board, CancellationToken ct)
    {
        var replies = (await store.RepliesAsync(e.Id, ct)).ToDictionary(r => r.MemberId);
        var open = e.ReplyOpen(V.Today);
        var sb = new StringBuilder("<section class=\"vm-card vm-reply\"><h2><i class=\"ti ti-hand-stop\"></i> Teilnahme</h2>");
        if (e.AskRequired) sb.Append("<p><span class=\"vm-badge warn\">Rückmeldung erforderlich</span></p>");
        if (e.AskDue.Length > 0) sb.Append("<p class=\"vm-muted\">Bitte bis ").Append(V.H(V.Date(e.AskDue))).Append(" zu- oder absagen.</p>");
        if (mine.Count == 0) sb.Append("<p class=\"vm-muted\">Für diesen Termin ist von dir keine Rückmeldung nötig.</p>");
        else if (!open)
            foreach (var p in mine) sb.Append("<p>").Append(V.H(p.Name)).Append(": <strong>").Append(replies.TryGetValue(p.MemberId, out var r) ? ReplyLabel(r.Answer) : "keine Rückmeldung").Append("</strong></p>");
        else
        {
            sb.Append("<form method=\"post\" class=\"vm-form\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"reply\">");
            foreach (var p in mine)
            {
                replies.TryGetValue(p.MemberId, out var r);
                var who = p.Child ? p.Name : "Du";
                sb.Append("<div class=\"vm-reply-row\" role=\"radiogroup\" aria-label=\"").Append(V.H(who)).Append("\"><strong class=\"vm-reply-name\">").Append(V.H(who)).Append("</strong><div class=\"vm-choice\">");
                foreach (var (val, label) in new[] { (VereinStore.Yes, "Dabei"), (VereinStore.Maybe, "Vielleicht"), (VereinStore.No, "Nicht dabei") })
                    sb.Append("<label><input type=\"radio\" name=\"a.").Append(p.Id).Append("\" value=\"").Append(val).Append('"').Append(r?.Answer == val ? " checked" : "").Append("><span>").Append(label).Append("</span></label>");
                sb.Append("</div><input class=\"vm-note\" name=\"n.").Append(p.Id).Append("\" maxlength=\"200\" placeholder=\"Bemerkung (freiwillig)\" aria-label=\"Bemerkung für ").Append(V.H(who)).Append("\" value=\"").Append(V.H(r?.Note ?? "")).Append("\"></div>");
            }
            sb.Append("<button class=\"vm-btn\" type=\"submit\">Rückmeldung speichern</button></form>");
        }
        if (board)
        {
            var asked = VereinStore.Asked(people, e, VereinSettings.BoardList(ctx)).ToList();
            int Count(string a) => asked.Count(p => replies.TryGetValue(p.MemberId, out var r) && r.Answer == a);
            sb.Append("<h3>Rückmeldungen (für den Vorstand)</h3><p class=\"vm-muted\">").Append(Count(VereinStore.Yes)).Append(" dabei · ").Append(Count(VereinStore.Maybe)).Append(" unsicher · ")
              .Append(Count(VereinStore.No)).Append(" nicht dabei · ").Append(asked.Count(p => !replies.ContainsKey(p.MemberId))).Append(" offen</p>");
            // folded by answer, names in a compact list: as a table every one of 40 people was a row of its own, on a phone a long scroll
            foreach (var (answer, title, open0) in new[] { (VereinStore.Yes, "Dabei", true), (VereinStore.Maybe, "Vielleicht", false), (VereinStore.No, "Nicht dabei", false), ("", "Noch offen", false) })
            {
                var group = asked.Where(p => answer.Length == 0 ? !replies.ContainsKey(p.MemberId) : replies.TryGetValue(p.MemberId, out var x) && x.Answer == answer).OrderBy(p => p.Last).ThenBy(p => p.First).ToList();
                if (group.Count == 0) continue;
                sb.Append("<details class=\"vm-group\"").Append(open0 ? " open" : "").Append("><summary>").Append(title).Append(" <span class=\"vm-badge\">").Append(group.Count).Append("</span></summary><ul class=\"vm-names\">");
                foreach (var p in group)
                {
                    replies.TryGetValue(p.MemberId, out var r);
                    sb.Append("<li>").Append(V.H(p.Name));
                    if (r is { Note.Length: > 0 }) sb.Append(" <small>– ").Append(V.H(r.Note)).Append("</small>");
                    sb.Append("</li>");
                }
                sb.Append("</ul></details>");
            }
        }
        return sb.Append("</section>").ToString();
    }

    private static async Task<PluginResult> MeetingAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var store = new VereinStore(ctx.Data);
        var m = await store.MeetingAsync(Id(ctx), ct);
        if (m is null || (!who!.Board && !VereinStore.InAudience(who.Person, m.Audience, VereinSettings.BoardList(ctx)))) return PluginResult.NotFound();

        if (ctx.Request.IsPost && ctx.Request.Action is VereinStore.Yes or VereinStore.No or VereinStore.Maybe && m.Status == VereinStore.Planned)
        {
            await store.SetRsvpAsync(m.Id, who.Person.MemberId, ctx.Request.Action, ct);
            return PluginResult.Redirect(Base + "/sitzungen/" + m.Id + "?ok=zusage");
        }
        var mine = (await store.RsvpsAsync(m.Id, ct)).FirstOrDefault(r => r.MemberId == who.Person.MemberId);

        var sb = new StringBuilder(Notice(ctx));
        sb.Append("<p><a href=\"").Append(Base).Append("/termine\">← Termine & Sitzungen</a></p>");
        sb.Append("<section class=\"vm-card\"><dl class=\"vm-facts\"><dt>Wann</dt><dd>").Append(V.H(V.When(m.Starts))).Append("</dd>");
        if (m.Place.Length > 0) sb.Append("<dt>Wo</dt><dd>").Append(V.H(m.Place)).Append("</dd>");
        if (m.Kind.Length > 0) sb.Append("<dt>Art</dt><dd>").Append(V.H(m.Kind)).Append("</dd>");
        sb.Append("</dl>");
        if (m.Status == VereinStore.Cancelled) sb.Append("<div class=\"vm-err\">Diese Sitzung ist abgesagt.</div>");
        if (m.Agenda.Length > 0) sb.Append("<h2>Tagesordnung</h2><div class=\"vm-text\">").Append(V.Paragraphs(m.Agenda)).Append("</div>");
        if (m.Status == VereinStore.Planned)
        {
            sb.Append("<form method=\"post\" class=\"vm-rsvp\">").Append(ctx.Antiforgery).Append("<span>Kommst du?</span><span class=\"vm-seg\">");
            foreach (var (val, label) in new[] { (VereinStore.Yes, "Ich komme"), (VereinStore.Maybe, "Vielleicht"), (VereinStore.No, "Ich kann nicht") })
                sb.Append("<button class=\"vm-pick").Append(mine?.Answer == val ? " on" : "").Append(val == VereinStore.Yes ? " yes" : val == VereinStore.No ? " no" : "").Append("\" type=\"submit\" name=\"action\" value=\"").Append(val)
                  .Append("\" aria-pressed=\"").Append(mine?.Answer == val ? "true" : "false").Append("\">").Append(label).Append("</button>");
            sb.Append("</span></form>");
        }
        sb.Append("<p><a class=\"vm-btn ghost small\" href=\"").Append(Base).Append("/sitzungen/").Append(m.Id).Append("/pdf\" target=\"_blank\" rel=\"noopener\"><i class=\"ti ti-printer\"></i> Einladung als PDF</a></p>");
        sb.Append("</section>");
        if (m.MinutesStatus == VereinStore.Final && m.Minutes.Length > 0)
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-notes\"></i> Protokoll</h2><div class=\"vm-text\">").Append(V.Paragraphs(m.Minutes)).Append("</div><p><a class=\"vm-btn ghost small\" href=\"").Append(Base).Append("/sitzungen/").Append(m.Id).Append("/pdf?teil=protokoll\" target=\"_blank\" rel=\"noopener\"><i class=\"ti ti-printer\"></i> Protokoll als PDF</a></p></section>");
        if (who.Board)
        {
            var rsvps = await store.RsvpsAsync(m.Id, ct);
            var people = (await store.PeopleAsync(ct)).ToDictionary(p => p.MemberId);
            var invited = VereinStore.Audience(people.Values, m.Audience, VereinSettings.BoardList(ctx)).ToList();
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-clipboard-list\"></i> Rückmeldungen (für den Vorstand)</h2><table class=\"vm-table\"><thead><tr><th>Name</th><th>Antwort</th></tr></thead><tbody>");
            foreach (var p in invited)
            {
                var r = rsvps.FirstOrDefault(x => x.MemberId == p.MemberId);
                sb.Append("<tr><td>").Append(V.H(p.Name)).Append("</td><td>").Append(r is null ? "<span class=\"vm-muted\">–</span>" : r.Answer == VereinStore.Yes ? "kommt" : r.Answer == VereinStore.No ? "kommt nicht" : "weiß noch nicht").Append("</td></tr>");
            }
            sb.Append("</tbody></table></section>");
        }
        return await ShellAsync(ctx, who, "dates", m.Title, sb.ToString(), ct);
    }

    // ---- photos and material ------------------------------------------------------------------------------------

    internal static string ThumbHtml(Upload u) => Thumb(u);

    private static string Thumb(Upload u) =>
        V.IsImage(Path.GetExtension(u.File).ToLowerInvariant())
            ? "<a class=\"vm-thumb\" href=\"" + Base + "/dateien/" + u.Id + "\" target=\"_blank\" rel=\"noopener\"><img src=\"" + Base + "/dateien/" + u.Id + "\" alt=\"" + V.H(u.Title) + "\" loading=\"lazy\"><span>" + V.H(u.Title) + "</span></a>"
            : "<a class=\"vm-thumb file\" href=\"" + Base + "/dateien/" + u.Id + "\"><i class=\"ti ti-file\"></i><span>" + V.H(u.Title.Length > 0 ? u.Title : u.FileName) + "</span></a>";

    /// <summary>Who sees an upload: everybody once it is shared and released; the uploader and the board always.</summary>
    internal static bool CanSee(Upload u, Person p, bool board) => u.ForEveryone || u.MemberId == p.MemberId || board;

    /// <summary>The event a photo belongs to by its date (the day it was taken lies within the event's days), or 0.</summary>
    internal static long EventForDay(IEnumerable<Event> events, string day) =>
        day.Length < 10 ? 0 : events.FirstOrDefault(e => !e.Draft && string.CompareOrdinal(e.Day, day[..10]) <= 0
            && string.CompareOrdinal((e.Ends.Length >= 10 ? e.Ends[..10] : e.Day), day[..10]) >= 0)?.Id ?? 0;

    private static async Task<PluginResult> FilesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var store = new VereinStore(ctx.Data);
        var events = (await store.EventsAsync(ct)).Where(e => !e.Draft).ToList();
        string? error = null;

        if (ctx.Request.IsPost && ctx.Request.Action is "delete" or "approve" && long.TryParse(ctx.Request.F("id"), out var oneId) && await store.UploadAsync(oneId, ct) is Upload one)
        {
            if (ctx.Request.Action == "approve" && who!.Board)
            {
                await store.Uploads.UpdateAsync(one.Id, new Dictionary<string, object?> { ["approved"] = true }, ct);
                return PluginResult.Redirect(Base + "/dateien?ok=freigegeben" + (one.Kind == VereinStore.Material ? "&art=material" : ""));
            }
            if (ctx.Request.Action == "delete" && (one.MemberId == who!.Person.MemberId || who.Board))
            {
                await ctx.Files.DeleteAsync(one.File, ct);
                await store.Uploads.DeleteAsync(one.Id, ct);
                return PluginResult.Redirect(Base + "/dateien?ok=geloescht");
            }
        }
        if (ctx.Request.IsPost && ctx.Request.Action == "upload")
        {
            // the page's script sends ONE file per request (with a progress bar) and wants a short answer; without script the whole form comes at once
            var ajax = ctx.Request.F("ajax") == "1";
            var files = ctx.Request.Files.Where(f => f.Field == "file" && f.Length > 0).Take(40).ToList();
            var kind = ctx.Request.F("kind") == VereinStore.Material ? VereinStore.Material : VereinStore.Photo;
            var title = V.Clip(ctx.Request.F("title"), 120);
            var chosenEvent = ctx.Request.F("event");
            var max = VereinSettings.MaxBytes(ctx);
            var needsApproval = ctx.Settings.GetBool(VereinSettings.UploadApproval) && !who!.Board;
            if (files.Count == 0) error = "Bitte wähle eine Datei aus.";
            else
            {
                foreach (var f in files)
                {
                    var ext = V.AllowedExt(f.FileName);
                    if (ext.Length == 0) { error = "„" + f.FileName + "“: Diese Art von Datei geht leider nicht (erlaubt: Bilder, PDF, Office, Text, ZIP, MP3, MP4)."; break; }
                    if (kind == VereinStore.Photo && !V.IsImage(ext)) { error = "„" + f.FileName + "“ ist kein Bild – lade es bitte als Material hoch."; break; }
                    if (f.Length > max) { error = "„" + f.FileName + "“ ist zu groß (höchstens " + V.Size(max) + ")."; break; }
                    byte[] bytes;
                    using (var s = f.OpenRead()) using (var ms = new MemoryStream()) { await s.CopyToAsync(ms, ct); bytes = ms.ToArray(); }
                    if (!V.LooksLike(ext, bytes)) { error = "„" + f.FileName + "“ ist beschädigt oder keine echte " + ext + "-Datei."; break; }
                    // when it was taken: the photo's own date, else the upload's
                    var taken = ext is ".jpg" or ".jpeg" ? VereinExif.TakenAt(bytes) : "";
                    if (taken.Length == 0) taken = V.Local(DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
                    var eventId = chosenEvent == "auto" || chosenEvent.Length == 0 ? EventForDay(events, taken)
                        : long.TryParse(chosenEvent, out var ev) && events.Any(e => e.Id == ev) ? ev : 0;
                    var name = "u/" + Guid.NewGuid().ToString("N") + ext;
                    using (var ms = new MemoryStream(bytes)) await ctx.Files.SaveAsync(name, ms, ct);
                    await store.Uploads.InsertAsync(new Dictionary<string, object?>
                    {
                        ["member_id"] = who!.Person.MemberId, ["kind"] = kind, ["title"] = title.Length > 0 ? title : Path.GetFileNameWithoutExtension(f.FileName),
                        ["file"] = name, ["file_name"] = V.SafeName(f.FileName), ["file_type"] = V.Types[ext], ["size"] = bytes.LongLength,
                        ["shared"] = ctx.Request.F("private") != "1", ["created"] = DateTime.UtcNow, ["approved"] = !needsApproval, ["event_id"] = eventId, ["taken"] = taken
                    }, ct);
                }
                if (ajax) return PluginResult.Json(new System.Text.Json.Nodes.JsonObject { ["ok"] = error is null, ["error"] = error, ["pending"] = needsApproval });
                if (error is null) return PluginResult.Redirect(Base + "/dateien?ok=" + (needsApproval ? "upload-pruefung" : "upload"));
            }
            if (ajax) return PluginResult.Json(new System.Text.Json.Nodes.JsonObject { ["ok"] = false, ["error"] = error });
        }

        var all = await store.UploadsAsync(ct: ct);
        var visible = all.Where(u => CanSee(u, who!.Person, who.Board)).ToList();
        var people = (await store.PeopleAsync(ct)).ToDictionary(p => p.MemberId, p => p.Name);
        var eventTitle = events.ToDictionary(e => e.Id, e => e.Title + " (" + V.Date(e.Starts) + ")");
        var filter = ctx.Request.Q("art") == VereinStore.Material ? VereinStore.Material : VereinStore.Photo;
        long.TryParse(ctx.Request.Q("termin"), out var onlyEvent);
        var pending = who!.Board ? all.Where(u => !u.Approved).ToList() : new List<Upload>();

        var sb = new StringBuilder(Notice(ctx));
        if (pending.Count > 0)
        {
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-hourglass\"></i> Warten auf Freigabe (").Append(pending.Count).Append(")</h2><div class=\"vm-gallery\">");
            foreach (var u in pending)
                sb.Append("<figure>").Append(Thumb(u)).Append("<figcaption>").Append(V.H(people.TryGetValue(u.MemberId, out var n) ? n : "")).Append(" · ").Append(V.H(V.Date(u.Taken)))
                  .Append(ApproveButton(ctx, u.Id)).Append(DeleteButton(ctx, u.Id)).Append("</figcaption></figure>");
            sb.Append("</div></section>");
        }
        var recent = events.Where(e => string.CompareOrdinal(e.Day, V.Local(DateTime.UtcNow).AddDays(-120).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) >= 0
            && string.CompareOrdinal(e.Day, V.Local(DateTime.UtcNow).AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) <= 0).OrderByDescending(e => e.Starts).ToList();
        sb.Append(await UploadHintAsync(ctx, store, who.Person, ct));
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-upload\"></i> Hochladen</h2>");
        if (error is not null) sb.Append(Err(error));
        sb.Append(VereinUpload.Panel(ctx, onlyEvent > 0 ? events.FirstOrDefault(e => e.Id == onlyEvent) : null, recent, who.Board, materialToo: true));
        sb.Append("<p class=\"vm-up-done\" hidden><a class=\"vm-btn ghost\" href=\"").Append(Base).Append("/dateien").Append(onlyEvent > 0 ? "?termin=" + onlyEvent : "").Append("\"><i class=\"ti ti-refresh\"></i> Seite neu laden</a></p></section>");

        sb.Append("<div class=\"vm-tabs\"><a href=\"").Append(Base).Append("/dateien\"").Append(filter == VereinStore.Photo ? " class=\"on\"" : "").Append(">Bilder</a><a href=\"")
          .Append(Base).Append("/dateien?art=material\"").Append(filter == VereinStore.Material ? " class=\"on\"" : "").Append(">Material</a></div>");
        var shown = visible.Where(u => u.Kind == filter && (onlyEvent == 0 || u.EventId == onlyEvent)).OrderByDescending(u => u.Taken.Length > 0 ? u.Taken : V.Stamp(u.Created)).ToList();
        var withEvents = visible.Where(u => u.Kind == filter && u.EventId > 0 && eventTitle.ContainsKey(u.EventId)).Select(u => u.EventId).Distinct().ToList();
        if (withEvents.Count > 0)
        {
            sb.Append("<div class=\"vm-chips-row\"><a href=\"").Append(Base).Append("/dateien").Append(filter == VereinStore.Material ? "?art=material" : "").Append("\"").Append(onlyEvent == 0 ? " class=\"on\"" : "").Append(">Alle</a>");
            foreach (var id in withEvents)
                sb.Append("<a href=\"").Append(Base).Append("/dateien?termin=").Append(id).Append(filter == VereinStore.Material ? "&art=material" : "").Append("\"").Append(id == onlyEvent ? " class=\"on\"" : "").Append('>').Append(V.H(eventTitle[id])).Append("</a>");
            sb.Append("</div>");
        }
        if (shown.Count == 0) sb.Append("<div class=\"vm-empty\">Hier ist noch nichts – lade gern als Erste/r etwas hoch.</div>");
        else if (filter == VereinStore.Photo)
        {
            sb.Append("<div class=\"vm-gallery\">");
            foreach (var u in shown)
            {
                sb.Append("<figure>").Append(Thumb(u)).Append("<figcaption>").Append(V.H(people.TryGetValue(u.MemberId, out var n) ? n : "")).Append(" · ").Append(V.H(V.Date(u.Taken.Length > 0 ? u.Taken : V.Stamp(u.Created))));
                if (u.EventId > 0 && eventTitle.TryGetValue(u.EventId, out var et)) sb.Append(" · <a href=\"").Append(Base).Append("/termine/").Append(u.EventId).Append("\">").Append(V.H(et)).Append("</a>");
                if (!u.Shared) sb.Append(" · <span class=\"vm-badge\">privat</span>");
                if (!u.Approved) sb.Append(" · <span class=\"vm-badge warn\">wartet auf Freigabe</span>");
                if (u.MemberId == who.Person.MemberId || who.Board) sb.Append(DeleteButton(ctx, u.Id));
                sb.Append("</figcaption></figure>");
            }
            sb.Append("</div>");
        }
        else
        {
            sb.Append("<table class=\"vm-table\"><thead><tr><th>Titel</th><th>Von</th><th>Termin</th><th>Größe</th><th>Datum</th><th></th></tr></thead><tbody>");
            foreach (var u in shown)
                sb.Append("<tr><td><a href=\"").Append(Base).Append("/dateien/").Append(u.Id).Append("\"><i class=\"ti ti-file\"></i> ").Append(V.H(u.Title.Length > 0 ? u.Title : u.FileName)).Append("</a>")
                  .Append(u.Shared ? "" : " <span class=\"vm-badge\">privat</span>").Append(u.Approved ? "" : " <span class=\"vm-badge warn\">wartet auf Freigabe</span>")
                  .Append("</td><td>").Append(V.H(people.TryGetValue(u.MemberId, out var n) ? n : "")).Append("</td><td>").Append(V.H(eventTitle.TryGetValue(u.EventId, out var et) ? et : "–"))
                  .Append("</td><td>").Append(V.Size(u.Size)).Append("</td><td>").Append(V.H(V.Stamp(u.Created))).Append("</td><td>")
                  .Append(u.MemberId == who.Person.MemberId || who.Board ? DeleteButton(ctx, u.Id) : "").Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        return await ShellAsync(ctx, who, "files", "Bilder & Material", sb.ToString(), ct);
    }

    /// <summary>
    /// Many photos from a phone at once: every file goes in a request of its own (a whole batch would hit the server's size limit), with one bar for the
    /// batch. Without script the form posts as it is.
    /// </summary>
    private static string DeleteButton(IPluginContext ctx, long id) =>
        "<form method=\"post\" class=\"vm-inline\" data-confirm=\"Datei wirklich löschen?\">" + ctx.Antiforgery + "<input type=\"hidden\" name=\"action\" value=\"delete\"><input type=\"hidden\" name=\"id\" value=\"" + id +
        "\"><button class=\"vm-link-btn\" type=\"submit\" title=\"Löschen\" aria-label=\"Löschen\"><i class=\"ti ti-trash\"></i></button></form>";

    private static string ApproveButton(IPluginContext ctx, long id) =>
        "<form method=\"post\" class=\"vm-inline\">" + ctx.Antiforgery + "<input type=\"hidden\" name=\"action\" value=\"approve\"><input type=\"hidden\" name=\"id\" value=\"" + id +
        "\"><button class=\"vm-btn small\" type=\"submit\"><i class=\"ti ti-check\"></i> Freigeben</button></form>";

    private static async Task<PluginResult> FileAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var u = await new VereinStore(ctx.Data).UploadAsync(Id(ctx), ct);
        if (u is null || !CanSee(u, who!.Person, who.Board)) return PluginResult.NotFound();
        return await ServeAsync(ctx, u.File, u.FileName, u.FileType, ct);
    }

    // ---- people -------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> DirectoryAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var board = VereinSettings.BoardList(ctx);
        var everyone = (await new VereinStore(ctx.Data).PeopleAsync(ct)).Where(p => p.Active).ToList();
        var people = everyone.Where(p => !p.Child).ToList();
        var children = everyone.Where(p => p.Child).ToList();
        var all = who!.Board || ctx.Settings.GetBool(VereinSettings.DirectoryForAll);
        var vorstand = people.Where(p => p.Roles.Any(r => board.Contains(r, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(p => board.ToList().FindIndex(r => p.Roles.Contains(r, StringComparer.OrdinalIgnoreCase))).ToList();

        // One line per person: picture, name, roles, a call and a mail button (icons — a phone and an envelope say what they do); the numbers, the address
        // and the club's own fields fold out. As cards with everything open, 60 members were a scroll of several screens on a phone.
        string Card(Person p)
        {
            var mailable = p.Email.Length > 0 && !VereinMail.Parkable(p.Email);   // a parked address (xxx_…, a list taken over) is the board's record, not a way to write to somebody
            var firstPhone = p.Phone.Split(new[] { " · ", ";" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            var more = PhoneLinks(p.Phone) + (mailable ? "<a href=\"mailto:" + V.H(p.Email) + "\">" + V.H(p.Email) + "</a>" : "") + Details(p);
            return "<details class=\"vm-person\"><summary>" + VereinPhoto.Avatar(p, Base + "/foto/" + p.Id, Initials(p), 40) + "<span class=\"vm-person-main\"><strong>" + V.H(p.Name) + "</strong><small>" +
                V.H(string.Join(", ", p.Roles)) + "</small></span><span class=\"vm-person-acts\">" +
                (firstPhone.Length > 0 ? "<a class=\"vm-icon-btn\" href=\"tel:" + V.H(new string(firstPhone.Where(c => char.IsDigit(c) || c == '+').ToArray())) + "\" title=\"Anrufen: " + V.H(firstPhone) + "\" aria-label=\"" + V.H(p.Name) + " anrufen\"><i class=\"ti ti-phone\" aria-hidden=\"true\"></i></a>" : "") +
                (mailable ? "<a class=\"vm-icon-btn\" href=\"mailto:" + V.H(p.Email) + "\" title=\"E-Mail: " + V.H(p.Email) + "\" aria-label=\"E-Mail an " + V.H(p.Name) + "\"><i class=\"ti ti-mail\" aria-hidden=\"true\"></i></a>" : "") +
                "</span></summary>" + (more.Length > 0 ? "<div class=\"vm-person-more\">" + more + "</div>" : "") + "</details>";
        }
        // private details by their visibility: the board sees all, the others what the club made visible for everybody
        var fields = VereinSettings.Fields(ctx);
        var seeAddress = who.Board || ctx.Settings.GetBool(VereinSettings.AddressForAll);
        var seeBirthday = who.Board || ctx.Settings.GetBool(VereinSettings.BirthdayForAll);
        string Details(Person p)
        {
            var parts = new List<string>();
            if (seeAddress && p.Address.Length > 0) parts.Add(V.H(p.Address));
            if (seeBirthday && p.Born.Length > 0) parts.Add("geb. " + V.H(V.Date(p.Born)));
            foreach (var f in fields)
                if ((who.Board || f.ForAll) && p.Extra.TryGetValue(f.Label, out var v) && v.Length > 0) parts.Add(V.H(f.Label) + " " + V.H(v));
            return parts.Count == 0 ? "" : "<small>" + string.Join(" · ", parts) + "</small>";
        }

        var sb = new StringBuilder();
        if (all) sb.Append("<label class=\"vm-search\"><i class=\"ti ti-search\" aria-hidden=\"true\"></i><input type=\"search\" placeholder=\"Name, Rolle, Ort …\" data-vm-filter=\".vm-person\" aria-label=\"Mitglieder durchsuchen\"></label>")
              .Append("<script>(function(){var i=document.querySelector('[data-vm-filter]');if(!i)return;i.addEventListener('input',function(){var q=i.value.trim().toLowerCase();document.querySelectorAll(i.getAttribute('data-vm-filter')).forEach(function(c){c.hidden=q.length>0&&c.textContent.toLowerCase().indexOf(q)<0;});});})();</script>");
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-star\"></i> Vorstand</h2>");
        sb.Append(vorstand.Count == 0 ? "<p class=\"vm-muted\">Noch niemand eingetragen.</p>" : "<div class=\"vm-people\">" + string.Concat(vorstand.Select(Card)) + "</div>").Append("</section>");
        if (all)
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-users\"></i> Alle Mitglieder (").Append(people.Count).Append(")</h2><div class=\"vm-people\">")
              .Append(string.Concat(people.Select(Card))).Append("</div></section>");
        if (all && children.Count > 0)
        {
            var byId = everyone.ToDictionary(x => x.Id);
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-mood-kid\"></i> Kinder (").Append(children.Count).Append(")</h2><div class=\"vm-people\">");
            foreach (var c in children)
                sb.Append("<div class=\"vm-person\">").Append(VereinPhoto.Avatar(c, Base + "/foto/" + c.Id, Initials(c))).Append("<span><strong>").Append(V.H(c.Name)).Append("</strong><small>")
                  .Append(V.H(string.Join(", ", c.Roles))).Append(c.Born.Length > 0 ? " · " + V.H(V.Date(c.Born)) : "").Append("</small><small>gehört zu ")
                  .Append(V.H(string.Join(", ", c.Guardians.Select(g => byId.TryGetValue(g, out var x) ? x.Name : "").Where(n => n.Length > 0)))).Append("</small></span></div>");
            sb.Append("</div></section>");
        }
        return await ShellAsync(ctx, who, "people", "Mitglieder", sb.ToString(), ct);
    }

    /// <summary>A profile picture, for whoever may see that person in the member list: the board, the person, their guardians, everybody when the
    /// list is open to all, and the board members (who are listed for everybody).</summary>
    private static async Task<PluginResult> PhotoAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var p = await new VereinStore(ctx.Data).PersonAsync(Id(ctx), ct);
        if (p is null || p.Photo.Length == 0) return PluginResult.NotFound();
        var board = VereinSettings.BoardList(ctx);
        var may = who!.Board || p.Id == who.Person.Id || p.Guardians.Contains(who.Person.Id) || (p.Active && ctx.Settings.GetBool(VereinSettings.DirectoryForAll))
                  || (p.Active && p.Roles.Any(r => board.Contains(r, StringComparer.OrdinalIgnoreCase)));
        return may ? await VereinPhoto.ServeAsync(ctx, p, ct) : PluginResult.NotFound();
    }

    /// <summary>One call link per number ("mobile · landline" as one field).</summary>
    private static string PhoneLinks(string phone) => string.Concat(phone.Split(new[] { " · ", ";" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(n => "<a href=\"tel:" + V.H(new string(n.Where(c => char.IsDigit(c) || c == '+').ToArray())) + "\">" + V.H(n) + "</a>"));

    private static string Initials(Person p) => string.Concat(new[] { p.First, p.Last }.Where(x => x.Length > 0).Select(x => char.ToUpperInvariant(x[0]))) is { Length: > 0 } s ? s : "?";

    /// <summary>Subscribing in a phone or computer calendar: the member's own secret feed (made on request) and the club's public one.</summary>
    private static string CalendarCard(IPluginContext ctx, Person p)
    {
        var origin = VereinMail.Origin(ctx);
        string Webcal(string path) => (origin.StartsWith("https://", StringComparison.Ordinal) ? "webcal://" + origin[8..] : origin.Replace("http://", "webcal://")) + path;
        var sb = new StringBuilder("<section class=\"vm-card\"><h2><i class=\"ti ti-calendar-plus\"></i> Termine im eigenen Kalender</h2>");
        sb.Append("<p class=\"vm-muted\">Abonniere die Termine in deinem Handy- oder Computer-Kalender – neue und geänderte Termine kommen dann von selbst dazu.</p>");
        if (p.CalToken.Length == 40)
        {
            var path = VereinIcal.PersonalFeed(p.CalToken);
            sb.Append("<p><strong>Mein Kalender</strong> (mit internen Terminen und meinen Sitzungen):</p><p class=\"vm-actions\"><a class=\"vm-btn\" href=\"").Append(V.H(Webcal(path)))
              .Append("\"><i class=\"ti ti-calendar-plus\"></i> Abonnieren</a></p><p class=\"vm-muted\" style=\"word-break:break-all\">Adresse zum Kopieren (z. B. für Google Kalender): ")
              .Append(V.H(origin + path)).Append("</p><p class=\"vm-muted\">Diese Adresse ist geheim – wer sie hat, sieht deine Termine. ");
            sb.Append("<form method=\"post\" class=\"vm-inline\" data-confirm=\"Neue Adresse erzeugen? Die alte funktioniert dann nicht mehr.\">").Append(ctx.Antiforgery)
              .Append("<input type=\"hidden\" name=\"action\" value=\"calendar\"><button class=\"vm-link-btn\" type=\"submit\" style=\"font-size:14px;text-decoration:underline\">Neue Adresse erzeugen</button></form></p>");
        }
        else
            sb.Append("<form method=\"post\" class=\"vm-form\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"calendar\"><button class=\"vm-btn\" type=\"submit\"><i class=\"ti ti-calendar-plus\"></i> Meinen Kalender-Link erstellen</button></form>");
        sb.Append("<p class=\"vm-muted\">Nur die öffentlichen Termine des Vereins: <a href=\"").Append(V.H(Webcal(VereinIcal.PublicFeed))).Append("\">abonnieren</a>.</p></section>");
        return sb.ToString();
    }

    private static async Task<PluginResult> ProfileAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var store = new VereinStore(ctx.Data);
        var p = who!.Person;
        string? error = null;
        if (ctx.Request.IsPost && ctx.Request.Action == "profile")
        {
            await store.People.UpdateAsync(p.Id, new Dictionary<string, object?>
            {
                ["phone"] = V.Clip(ctx.Request.F("phone"), 40), ["street"] = V.Clip(ctx.Request.F("street"), 120), ["zip"] = V.Clip(ctx.Request.F("zip"), 10), ["city"] = V.Clip(ctx.Request.F("city"), 80)
            }, ct);
            return PluginResult.Redirect(Base + "/profil?ok=profil");
        }
        if (ctx.Request.IsPost && ctx.Request.Action is "photo" or "nophoto")
        {
            if (ctx.Request.Action == "nophoto") { await VereinPhoto.RemoveAsync(ctx, store, p, ct); return PluginResult.Redirect(Base + "/profil?ok=foto"); }
            error = await VereinPhoto.SaveAsync(ctx, store, p, ctx.Request.File("photo"), ct);
            if (error is null) return PluginResult.Redirect(Base + "/profil?ok=foto");
        }
        if (ctx.Request.IsPost && ctx.Request.Action == "calendar")
        {
            // a new secret link; the old one stops working at once
            await store.People.UpdateAsync(p.Id, new Dictionary<string, object?> { ["cal_token"] = VereinIcal.NewToken() }, ct);
            return PluginResult.Redirect(Base + "/profil?ok=kalender");
        }
        if (ctx.Request.IsPost && ctx.Request.Action == "password")
        {
            var next = ctx.Request.F("new");
            if (next.Length < 8) error = "Das neue Passwort muss mindestens 8 Zeichen haben.";
            else if (next != ctx.Request.F("repeat")) error = "Die beiden neuen Passwörter stimmen nicht überein.";
            else if (!await ctx.Members.ChangePasswordAsync(p.MemberId, ctx.Request.F("current"), next, ct)) error = "Das bisherige Passwort stimmt nicht.";
            else return PluginResult.Redirect(Base + "/profil?ok=passwort");
        }
        var answers = await store.AnswersOfMemberAsync(p.MemberId, ct);
        var docs = (await store.DocsAsync(false, ct)).Concat(await store.DocsAsync(true, ct)).ToDictionary(d => d.Id);

        var sb = new StringBuilder(Notice(ctx));
        if (error is not null) sb.Append(Err(error));
        sb.Append("<section class=\"vm-card vm-photo-card\"><h2>Profilbild</h2><div class=\"vm-photo-row\">").Append(VereinPhoto.Avatar(p, Base + "/foto/" + p.Id, Initials(p), 96).Replace("class=\"vm-avatar", "id=\"vm-photo-preview\" class=\"vm-avatar"))
          .Append("<form method=\"post\" enctype=\"multipart/form-data\" class=\"vm-form\">").Append(ctx.Antiforgery)
          .Append("<label class=\"vm-field\"><span>Neues Bild (wird vor dem Hochladen verkleinert)</span><input type=\"file\" name=\"photo\" accept=\"image/*\" data-vm-photo=\"#vm-photo-preview\" required></label>")
          .Append("<div class=\"vm-actions\"><button class=\"vm-btn\" type=\"submit\" name=\"action\" value=\"photo\">Hochladen</button>")
          .Append(p.Photo.Length > 0 ? "<button class=\"vm-btn ghost\" type=\"submit\" name=\"action\" value=\"nophoto\" formnovalidate data-confirm=\"Profilbild entfernen?\">Entfernen</button>" : "")
          .Append("</div></form></div><p class=\"vm-muted\">Das Bild sehen andere Mitglieder in der Mitgliederliste – auf der Website erscheint es nicht.</p></section>").Append(VereinPhoto.Script);
        sb.Append("<div class=\"vm-grid2\"><section class=\"vm-card\"><h2>Meine Angaben</h2><dl class=\"vm-facts\"><dt>Name</dt><dd>").Append(V.H(p.Name)).Append("</dd><dt>E-Mail</dt><dd>")
          .Append(V.H(p.Email)).Append("</dd><dt>Rollen</dt><dd>").Append(V.H(p.Roles.Count > 0 ? string.Join(", ", p.Roles) : "–")).Append("</dd>");
        if (p.Joined.Length > 0) sb.Append("<dt>Mitglied seit</dt><dd>").Append(V.H(V.Date(p.Joined))).Append("</dd>");
        if (p.Born.Length > 0) sb.Append("<dt>Geburtsdatum</dt><dd>").Append(V.H(V.Date(p.Born))).Append("</dd>");
        foreach (var f in VereinSettings.Fields(ctx))
            if (p.Extra.TryGetValue(f.Label, out var v) && v.Length > 0) sb.Append("<dt>").Append(V.H(f.Label)).Append("</dt><dd>").Append(V.H(v)).Append("</dd>");
        sb.Append("</dl><form method=\"post\" class=\"vm-form\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"profile\">")
          .Append("<label class=\"vm-field\"><span>Telefon</span><input name=\"phone\" type=\"tel\" value=\"").Append(V.H(p.Phone)).Append("\" maxlength=\"40\"></label>")
          .Append("<label class=\"vm-field\"><span>Straße und Hausnummer</span><input name=\"street\" value=\"").Append(V.H(p.Street)).Append("\" maxlength=\"120\" autocomplete=\"street-address\"></label>")
          .Append("<div class=\"vm-row2\"><label class=\"vm-field\"><span>PLZ</span><input name=\"zip\" value=\"").Append(V.H(p.Zip)).Append("\" maxlength=\"10\" autocomplete=\"postal-code\"></label>")
          .Append("<label class=\"vm-field\"><span>Ort</span><input name=\"city\" value=\"").Append(V.H(p.City)).Append("\" maxlength=\"80\" autocomplete=\"address-level2\"></label></div>")
          .Append("<button class=\"vm-btn\" type=\"submit\">Speichern</button></form><p class=\"vm-muted\">Name, E-Mail oder Geburtsdatum ändern? Bitte kurz an den Vorstand wenden.</p></section>");
        sb.Append("<section class=\"vm-card\"><h2>Passwort ändern</h2><form method=\"post\" class=\"vm-form\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"password\">")
          .Append("<label class=\"vm-field\"><span>Bisheriges Passwort</span><input name=\"current\" type=\"password\" autocomplete=\"current-password\" required></label>")
          .Append("<label class=\"vm-field\"><span>Neues Passwort</span><input name=\"new\" type=\"password\" autocomplete=\"new-password\" minlength=\"8\" required></label>")
          .Append("<label class=\"vm-field\"><span>Neues Passwort wiederholen</span><input name=\"repeat\" type=\"password\" autocomplete=\"new-password\" minlength=\"8\" required></label>")
          .Append("<button class=\"vm-btn\" type=\"submit\">Passwort ändern</button></form></section></div>");
        sb.Append(CalendarCard(ctx, p));
        var twoFactor = HasTwoFactor(ctx.Member);
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-shield-lock\"></i> Zwei-Faktor-Anmeldung</h2><p>")
          .Append(twoFactor ? "<span class=\"vm-badge ok\">eingeschaltet</span> Beim Anmelden fragen wir zusätzlich nach dem Code aus deiner Authenticator-App."
                            : "<span class=\"vm-badge warn\">aus</span> Schütze dein Konto zusätzlich mit einem Code von deinem Handy – selbst wer dein Passwort kennt, kommt dann nicht hinein.")
          .Append("</p><p><a class=\"vm-btn").Append(twoFactor ? " ghost" : "").Append("\" href=\"").Append(MemberPaths.Security).Append("?returnUrl=").Append(Uri.EscapeDataString(Base + "/profil")).Append("\">")
          .Append(twoFactor ? "Verwalten" : "Jetzt einrichten").Append("</a></p></section>");
        var kids = VereinStore.ChildrenOf(await store.PeopleAsync(ct), p).ToList();
        if (kids.Count > 0)
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-mood-kid\"></i> Meine Kinder</h2><ul class=\"vm-list\">")
              .Append(string.Concat(kids.Select(k => "<li><span style=\"padding:10px 12px;background:var(--vm-soft);border-radius:10px;display:block\"><strong>" + V.H(k.Name) + "</strong>" + (k.Born.Length > 0 ? " · geboren " + V.H(V.Date(k.Born)) : "") + (k.Roles.Count > 0 ? " · " + V.H(string.Join(", ", k.Roles)) : "") + "</span></li>")))
              .Append("</ul><p class=\"vm-muted\">Für deine Kinder bestätigst du Dokumente und bekommst die Nachrichten des Vereins. Änderungen bitte an den Vorstand.</p></section>");
        sb.Append("<section class=\"vm-card\"><h2>Meine Bestätigungen</h2>");
        if (answers.Count == 0) sb.Append("<p class=\"vm-muted\">Noch keine.</p>");
        else
        {
            sb.Append("<table class=\"vm-table\"><thead><tr><th>Dokument</th><th>Antwort</th><th>Wann</th></tr></thead><tbody>");
            foreach (var a in answers.OrderByDescending(a => a.At))
                sb.Append("<tr><td>").Append(V.H(docs.TryGetValue(a.DocId, out var d) ? d.Title : "#" + a.DocId)).Append("</td><td>")
                  .Append(a.Status == VereinStore.Accepted ? (a.Signature.Length > 0 ? "unterschrieben als „" + V.H(a.Signature) + "“" : "bestätigt") : "abgelehnt")
                  .Append("</td><td>").Append(V.H(V.Stamp(a.At))).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");
        return await ShellAsync(ctx, who, "profile", "Mein Profil", sb.ToString(), ct);
    }

    // ---- board overview -----------------------------------------------------------------------------------------

    private static async Task<PluginResult> BoardAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        if (!who!.Board) return PluginResult.NotFound();
        var store = new VereinStore(ctx.Data);
        var board = VereinSettings.BoardList(ctx);
        var people = (await store.PeopleAsync(ct)).Where(p => p.Active).ToList();
        var answers = await store.AllAnswersAsync(ct);
        var docs = await store.DocsAsync(false, ct);

        var sb = new StringBuilder("<p class=\"vm-lead\">Wer hat welches Dokument schon beantwortet? Anlegen, Erinnern und Auswerten geht im Admin der Website unter „Verein“.</p>");
        if (docs.Count == 0) sb.Append("<div class=\"vm-empty\">Noch keine Dokumente.</div>");
        foreach (var d in docs)
        {
            var target = VereinStore.Audience(people, d.Audience, board).ToList();
            var mine = answers.Where(a => a.DocId == d.Id).ToDictionary(a => a.MemberId);
            var done = target.Count(p => mine.ContainsKey(p.MemberId));
            sb.Append("<details class=\"vm-card vm-details\"><summary><strong>").Append(V.H(d.Title)).Append("</strong><span class=\"vm-progress\"><span style=\"width:")
              .Append(target.Count == 0 ? 0 : done * 100 / target.Count).Append("%\"></span></span><span>").Append(done).Append(" von ").Append(target.Count).Append("</span></summary>")
              .Append("<table class=\"vm-table\"><thead><tr><th>Name</th><th>Antwort</th><th>Wann</th></tr></thead><tbody>");
            foreach (var p in target.OrderBy(p => mine.ContainsKey(p.MemberId)).ThenBy(p => p.Last))
            {
                mine.TryGetValue(p.MemberId, out var a);
                sb.Append("<tr><td>").Append(V.H(p.Name)).Append("</td><td>").Append(a is null ? "<span class=\"vm-badge warn\">offen</span>"
                    : a.Status == VereinStore.Accepted ? "<span class=\"vm-badge ok\">" + (a.Signature.Length > 0 ? "unterschrieben" : "bestätigt") + "</span>" : "<span class=\"vm-badge err\">abgelehnt</span>")
                  .Append("</td><td>").Append(V.H(V.Stamp(a?.At))).Append("</td></tr>");
            }
            sb.Append("</tbody></table></details>");
        }
        return await ShellAsync(ctx, who, "board", "Vorstand", sb.ToString(), ct);
    }
}
