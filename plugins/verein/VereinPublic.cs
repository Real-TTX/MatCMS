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
        b.Route(Base + "/termine", DatesAsync, read);
        b.Route(Base + "/termine/{id}", EventAsync, read);
        b.Route(Base + "/sitzungen/{id}", MeetingAsync, form);
        b.Route(Base + "/dateien", FilesAsync, form);
        b.Route(Base + "/dateien/{id}", FileAsync, read);
        b.Route(Base + "/mitglieder", DirectoryAsync, read);
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
        return (new Who(p, p.Roles.Any(r => board.Contains(r, StringComparer.OrdinalIgnoreCase))), null);
    }

    private static PluginResult NoAccess(IPluginContext ctx) => Bare(ctx,
        "<div class=\"vm-card vm-login\"><h1>Kein Zugang</h1><p>Mit diesem Konto gibt es keinen Zugang zum Mitgliederbereich von " + V.H(VereinSettings.Club(ctx)) +
        ". Wende dich bitte an den Vorstand.</p>" + LogoutForm(ctx) + "</div>", "Mitgliederbereich");

    // ---- page frame ---------------------------------------------------------------------------------------------

    private static PluginResult Bare(IPluginContext ctx, string body, string title) =>
        PluginResult.Page("<style>" + VereinCss.Css + "</style><section class=\"section vm\"><div class=\"container\">" + body + "</div></section>", title);

    private static string LogoutForm(IPluginContext ctx) =>
        "<form method=\"post\" action=\"/anmelden?handler=Logout\" class=\"vm-logout\">" + ctx.Antiforgery +
        "<input type=\"hidden\" name=\"returnUrl\" value=\"" + Base + "\"><button class=\"vm-btn ghost small\" type=\"submit\">Abmelden</button></form>";

    private static async Task<PluginResult> ShellAsync(IPluginContext ctx, Who who, string active, string title, string body, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var open = (await OpenDocsAsync(ctx, store, who.Person, ct)).Count;
        string Item(string key, string href, string label, string icon, string? badge = null) =>
            "<a href=\"" + href + "\"" + (key == active ? " class=\"on\" aria-current=\"page\"" : "") + "><i class=\"ti ti-" + icon + "\" aria-hidden=\"true\"></i>" + V.H(label) +
            (badge is null ? "" : "<span class=\"vm-count\">" + badge + "</span>") + "</a>";
        var nav = new StringBuilder()
            .Append(Item("home", Base, "Übersicht", "home"))
            .Append(Item("docs", Base + "/dokumente", "Dokumente", "file-check", open > 0 ? open.ToString() : null))
            .Append(Item("dates", Base + "/termine", "Termine & Sitzungen", "calendar"))
            .Append(Item("files", Base + "/dateien", "Bilder & Material", "photo"))
            .Append(Item("people", Base + "/mitglieder", "Mitglieder", "users"));
        if (who.Board) nav.Append(Item("board", Base + "/vorstand", "Vorstand", "clipboard-list"));
        nav.Append(Item("profile", Base + "/profil", "Mein Profil", "user"));

        var html = "<header class=\"vm-head\"><div><p class=\"vm-kicker\">Mitgliederbereich · " + V.H(VereinSettings.Club(ctx)) + "</p><h1>" + V.H(title) + "</h1></div>" +
                   "<div class=\"vm-me\"><span>" + V.H(who.Person.Name) + (who.Person.Roles.Count > 0 ? " · " + V.H(string.Join(", ", who.Person.Roles)) : "") + "</span>" + LogoutForm(ctx) + "</div></header>" +
                   "<div class=\"vm-layout\"><nav class=\"vm-nav\" aria-label=\"Mitgliederbereich\">" + nav + "</nav><div class=\"vm-main\">" + body + "</div></div>";
        return Bare(ctx, html, title);
    }

    private static string Notice(IPluginContext ctx) => ctx.Request.Q("ok") switch
    {
        "" => "",
        "antwort" => Ok("Danke! Deine Antwort ist gespeichert."),
        "upload" => Ok("Danke! Die Datei ist hochgeladen."),
        "geloescht" => Ok("Die Datei ist gelöscht."),
        "profil" => Ok("Dein Profil ist gespeichert."),
        "passwort" => Ok("Dein Passwort ist geändert."),
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
        var meetings = (await MeetingsForAsync(ctx, store, who, ct)).Where(m => m.Status == VereinStore.Planned && string.CompareOrdinal(m.Starts.Length >= 10 ? m.Starts[..10] : m.Starts, V.Today) >= 0)
            .OrderBy(m => m.Starts).Take(3).ToList();
        var events = await store.UpcomingAsync(V.Today, publicOnly: false, 4, ct);
        var rsvps = (await store.RsvpsOfMemberAsync(p.MemberId, ct)).ToDictionary(r => r.MeetingId, r => r.Answer);
        var uploads = (await store.UploadsAsync(ct: ct)).Where(u => u.Shared).Take(6).ToList();

        var sb = new StringBuilder(Notice(ctx));
        sb.Append("<div class=\"vm-grid2\">");
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-file-check\"></i> Zu erledigen</h2>");
        if (open.Count == 0) sb.Append("<p class=\"vm-muted\">Alles erledigt – danke!</p>");
        else
        {
            sb.Append("<ul class=\"vm-list\">");
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

        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-calendar-event\"></i> Kommende Termine</h2>").Append(EventList(events, internalBadge: true, memberLinks: true)).Append("</section>");

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

    internal static string EventList(IEnumerable<Event> events, bool internalBadge, bool memberLinks = false)
    {
        var list = events.ToList();
        if (list.Count == 0) return "<p class=\"vm-muted\">Gerade stehen keine Termine an.</p>";
        var sb = new StringBuilder("<ul class=\"vm-events\">");
        foreach (var e in list)
        {
            var (day, month, wd) = V.Badge(e.Starts);
            sb.Append("<li class=\"").Append(e.Cancelled ? "is-cancelled" : "").Append("\"><span class=\"vm-date\"><b>").Append(V.H(day)).Append("</b><small>").Append(V.H(month)).Append("</small><em>")
              .Append(V.H(wd)).Append("</em></span><span class=\"vm-ev\"><strong>").Append(memberLinks ? "<a href=\"" + Base + "/termine/" + e.Id + "\">" + V.H(e.Title) + "</a>" : V.H(e.Title)).Append("</strong>");
            if (memberLinks && e.HasPlan) sb.Append(" <span class=\"vm-badge ok\">Ablauf &amp; Infos</span>");
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
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-calendar-event\"></i> Termine</h2>").Append(EventList(events, internalBadge: true, memberLinks: true)).Append("</section>");
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
        var e = await new VereinStore(ctx.Data).EventAsync(Id(ctx), ct);
        if (e is null || e.Draft) return PluginResult.NotFound();

        var sb = new StringBuilder("<p><a href=\"" + Base + "/termine\">← Termine & Sitzungen</a></p>");
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
        if (e.Steps.Count > 0)
        {
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-clock\"></i> Ablauf</h2><table class=\"vm-table vm-plan\"><tbody>");
            foreach (var (time, what) in e.Steps) sb.Append("<tr><th>").Append(V.H(time)).Append("</th><td>").Append(V.H(what)).Append("</td></tr>");
            sb.Append("</tbody></table></section>");
        }
        if (e.Parking.Length > 0) sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-parking\"></i> Parken & Anfahrt</h2><div class=\"vm-text\">").Append(V.Paragraphs(e.Parking)).Append("</div></section>");
        if (e.Notes.Length > 0) sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-info-circle\"></i> Hinweise</h2><div class=\"vm-text\">").Append(V.Paragraphs(e.Notes)).Append("</div></section>");
        if (!e.HasPlan) sb.Append("<p class=\"vm-muted\">Ablauf und weitere Infos folgen – der Vorstand trägt sie nach.</p>");
        return await ShellAsync(ctx, who!, "dates", e.Title, sb.ToString(), ct);
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
            sb.Append("<form method=\"post\" class=\"vm-rsvp\">").Append(ctx.Antiforgery).Append("<span>Kommst du?</span>");
            foreach (var (val, label) in new[] { (VereinStore.Yes, "Ich komme"), (VereinStore.Maybe, "Weiß noch nicht"), (VereinStore.No, "Ich kann nicht") })
                sb.Append("<button class=\"vm-btn").Append(mine?.Answer == val ? "" : " ghost").Append("\" type=\"submit\" name=\"action\" value=\"").Append(val).Append("\">").Append(label).Append("</button>");
            sb.Append("</form>");
        }
        sb.Append("</section>");
        if (m.MinutesStatus == VereinStore.Final && m.Minutes.Length > 0)
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-notes\"></i> Protokoll</h2><div class=\"vm-text\">").Append(V.Paragraphs(m.Minutes)).Append("</div></section>");
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

    private static string Thumb(Upload u) =>
        V.IsImage(Path.GetExtension(u.File).ToLowerInvariant())
            ? "<a class=\"vm-thumb\" href=\"" + Base + "/dateien/" + u.Id + "\" target=\"_blank\" rel=\"noopener\"><img src=\"" + Base + "/dateien/" + u.Id + "\" alt=\"" + V.H(u.Title) + "\" loading=\"lazy\"><span>" + V.H(u.Title) + "</span></a>"
            : "<a class=\"vm-thumb file\" href=\"" + Base + "/dateien/" + u.Id + "\"><i class=\"ti ti-file\"></i><span>" + V.H(u.Title.Length > 0 ? u.Title : u.FileName) + "</span></a>";

    private static async Task<PluginResult> FilesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var store = new VereinStore(ctx.Data);
        string? error = null;

        if (ctx.Request.IsPost && ctx.Request.Action == "delete" && long.TryParse(ctx.Request.F("id"), out var delId) && await store.UploadAsync(delId, ct) is Upload del
            && (del.MemberId == who!.Person.MemberId || who.Board))
        {
            await ctx.Files.DeleteAsync(del.File, ct);
            await store.Uploads.DeleteAsync(del.Id, ct);
            return PluginResult.Redirect(Base + "/dateien?ok=geloescht");
        }
        if (ctx.Request.IsPost && ctx.Request.Action == "upload")
        {
            var files = ctx.Request.Files.Where(f => f.Field == "file" && f.Length > 0).Take(10).ToList();
            var kind = ctx.Request.F("kind") == VereinStore.Material ? VereinStore.Material : VereinStore.Photo;
            var title = V.Clip(ctx.Request.F("title"), 120);
            var max = VereinSettings.MaxBytes(ctx);
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
                    var name = "u/" + Guid.NewGuid().ToString("N") + ext;
                    using (var ms = new MemoryStream(bytes)) await ctx.Files.SaveAsync(name, ms, ct);
                    await store.Uploads.InsertAsync(new Dictionary<string, object?>
                    {
                        ["member_id"] = who!.Person.MemberId, ["kind"] = kind, ["title"] = title.Length > 0 ? title : Path.GetFileNameWithoutExtension(f.FileName),
                        ["file"] = name, ["file_name"] = V.SafeName(f.FileName), ["file_type"] = V.Types[ext], ["size"] = bytes.LongLength,
                        ["shared"] = ctx.Request.F("private") != "1", ["created"] = DateTime.UtcNow
                    }, ct);
                }
                if (error is null) return PluginResult.Redirect(Base + "/dateien?ok=upload");
            }
        }

        var all = await store.UploadsAsync(ct: ct);
        var visible = all.Where(u => u.Shared || u.MemberId == who!.Person.MemberId || who.Board).ToList();
        var people = (await store.PeopleAsync(ct)).ToDictionary(p => p.MemberId, p => p.Name);
        var filter = ctx.Request.Q("art") == VereinStore.Material ? VereinStore.Material : VereinStore.Photo;

        var sb = new StringBuilder(Notice(ctx));
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-upload\"></i> Hochladen</h2>");
        if (error is not null) sb.Append(Err(error));
        sb.Append("<form method=\"post\" enctype=\"multipart/form-data\" class=\"vm-form vm-upload\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"upload\">")
          .Append("<label class=\"vm-field\"><span>Datei(en)</span><input type=\"file\" name=\"file\" multiple required></label>")
          .Append("<div class=\"vm-row2\"><label class=\"vm-field\"><span>Was ist es?</span><select name=\"kind\"><option value=\"photo\"").Append(filter == VereinStore.Photo ? " selected" : "")
          .Append(">Bild / Foto</option><option value=\"material\"").Append(filter == VereinStore.Material ? " selected" : "").Append(">Material (PDF, Dokument …)</option></select></label>")
          .Append("<label class=\"vm-field\"><span>Titel (freiwillig)</span><input name=\"title\" maxlength=\"120\" placeholder=\"z. B. Umzug Remseck 2026\"></label></div>")
          .Append("<label class=\"vm-check\"><input type=\"checkbox\" name=\"private\" value=\"1\"> <span>Nur für mich und den Vorstand sichtbar</span></label>")
          .Append("<p class=\"vm-muted\">Bitte lade nur eigene Bilder hoch oder solche, die du teilen darf. Erkennbare Personen sollten mit der Veröffentlichung im Mitgliederbereich einverstanden sein. Höchstens ")
          .Append(V.Size(VereinSettings.MaxBytes(ctx))).Append(" pro Datei.</p><button class=\"vm-btn\" type=\"submit\">Hochladen</button></form></section>");

        sb.Append("<div class=\"vm-tabs\"><a href=\"").Append(Base).Append("/dateien\"").Append(filter == VereinStore.Photo ? " class=\"on\"" : "").Append(">Bilder</a><a href=\"")
          .Append(Base).Append("/dateien?art=material\"").Append(filter == VereinStore.Material ? " class=\"on\"" : "").Append(">Material</a></div>");
        var shown = visible.Where(u => u.Kind == filter).ToList();
        if (shown.Count == 0) sb.Append("<div class=\"vm-empty\">Hier ist noch nichts – lade gern als Erste/r etwas hoch.</div>");
        else if (filter == VereinStore.Photo)
        {
            sb.Append("<div class=\"vm-gallery\">");
            foreach (var u in shown)
            {
                sb.Append("<figure>").Append(Thumb(u)).Append("<figcaption>").Append(V.H(people.TryGetValue(u.MemberId, out var n) ? n : "")).Append(" · ").Append(V.H(V.Stamp(u.Created)));
                if (!u.Shared) sb.Append(" · <span class=\"vm-badge\">privat</span>");
                if (u.MemberId == who!.Person.MemberId || who.Board) sb.Append(DeleteButton(ctx, u.Id));
                sb.Append("</figcaption></figure>");
            }
            sb.Append("</div>");
        }
        else
        {
            sb.Append("<table class=\"vm-table\"><thead><tr><th>Titel</th><th>Von</th><th>Größe</th><th>Datum</th><th></th></tr></thead><tbody>");
            foreach (var u in shown)
                sb.Append("<tr><td><a href=\"").Append(Base).Append("/dateien/").Append(u.Id).Append("\"><i class=\"ti ti-file\"></i> ").Append(V.H(u.Title.Length > 0 ? u.Title : u.FileName)).Append("</a>")
                  .Append(u.Shared ? "" : " <span class=\"vm-badge\">privat</span>").Append("</td><td>").Append(V.H(people.TryGetValue(u.MemberId, out var n) ? n : "")).Append("</td><td>")
                  .Append(V.Size(u.Size)).Append("</td><td>").Append(V.H(V.Stamp(u.Created))).Append("</td><td>")
                  .Append(u.MemberId == who!.Person.MemberId || who.Board ? DeleteButton(ctx, u.Id) : "").Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        return await ShellAsync(ctx, who!, "files", "Bilder & Material", sb.ToString(), ct);
    }

    private static string DeleteButton(IPluginContext ctx, long id) =>
        "<form method=\"post\" class=\"vm-inline\" data-confirm=\"Datei wirklich löschen?\">" + ctx.Antiforgery + "<input type=\"hidden\" name=\"action\" value=\"delete\"><input type=\"hidden\" name=\"id\" value=\"" + id +
        "\"><button class=\"vm-link-btn\" type=\"submit\" title=\"Löschen\" aria-label=\"Löschen\"><i class=\"ti ti-trash\"></i></button></form>";

    private static async Task<PluginResult> FileAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var u = await new VereinStore(ctx.Data).UploadAsync(Id(ctx), ct);
        if (u is null || !(u.Shared || u.MemberId == who!.Person.MemberId || who.Board)) return PluginResult.NotFound();
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

        string Card(Person p) => "<div class=\"vm-person\"><span class=\"vm-avatar\">" + V.H(Initials(p)) + "</span><span><strong>" + V.H(p.Name) + "</strong><small>" +
            V.H(string.Join(", ", p.Roles)) + "</small>" + (p.Phone.Length > 0 ? "<a href=\"tel:" + V.H(p.Phone.Replace(" ", "")) + "\">" + V.H(p.Phone) + "</a>" : "") +
            (p.Email.Length > 0 ? "<a href=\"mailto:" + V.H(p.Email) + "\">" + V.H(p.Email) + "</a>" : "") + "</span></div>";

        var sb = new StringBuilder("<section class=\"vm-card\"><h2><i class=\"ti ti-star\"></i> Vorstand</h2>");
        sb.Append(vorstand.Count == 0 ? "<p class=\"vm-muted\">Noch niemand eingetragen.</p>" : "<div class=\"vm-people\">" + string.Concat(vorstand.Select(Card)) + "</div>").Append("</section>");
        if (all)
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-users\"></i> Alle Mitglieder (").Append(people.Count).Append(")</h2><div class=\"vm-people\">")
              .Append(string.Concat(people.Select(Card))).Append("</div></section>");
        if (all && children.Count > 0)
        {
            var byId = everyone.ToDictionary(x => x.Id);
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-mood-kid\"></i> Kinder (").Append(children.Count).Append(")</h2><div class=\"vm-people\">");
            foreach (var c in children)
                sb.Append("<div class=\"vm-person\"><span class=\"vm-avatar\">").Append(V.H(Initials(c))).Append("</span><span><strong>").Append(V.H(c.Name)).Append("</strong><small>")
                  .Append(V.H(string.Join(", ", c.Roles))).Append(c.Born.Length > 0 ? " · " + V.H(V.Date(c.Born)) : "").Append("</small><small>gehört zu ")
                  .Append(V.H(string.Join(", ", c.Guardians.Select(g => byId.TryGetValue(g, out var x) ? x.Name : "").Where(n => n.Length > 0)))).Append("</small></span></div>");
            sb.Append("</div></section>");
        }
        return await ShellAsync(ctx, who, "people", "Mitglieder", sb.ToString(), ct);
    }

    private static string Initials(Person p) => string.Concat(new[] { p.First, p.Last }.Where(x => x.Length > 0).Select(x => char.ToUpperInvariant(x[0]))) is { Length: > 0 } s ? s : "?";

    private static async Task<PluginResult> ProfileAsync(IPluginContext ctx, CancellationToken ct)
    {
        var (who, deny) = await GateAsync(ctx, ct);
        if (deny is not null) return deny;
        var store = new VereinStore(ctx.Data);
        var p = who!.Person;
        string? error = null;
        if (ctx.Request.IsPost && ctx.Request.Action == "profile")
        {
            await store.People.UpdateAsync(p.Id, new Dictionary<string, object?> { ["phone"] = V.Clip(ctx.Request.F("phone"), 40) }, ct);
            return PluginResult.Redirect(Base + "/profil?ok=profil");
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
        sb.Append("<div class=\"vm-grid2\"><section class=\"vm-card\"><h2>Meine Angaben</h2><dl class=\"vm-facts\"><dt>Name</dt><dd>").Append(V.H(p.Name)).Append("</dd><dt>E-Mail</dt><dd>")
          .Append(V.H(p.Email)).Append("</dd><dt>Rollen</dt><dd>").Append(V.H(p.Roles.Count > 0 ? string.Join(", ", p.Roles) : "–")).Append("</dd>");
        if (p.Joined.Length > 0) sb.Append("<dt>Mitglied seit</dt><dd>").Append(V.H(V.Date(p.Joined))).Append("</dd>");
        sb.Append("</dl><form method=\"post\" class=\"vm-form\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"profile\">")
          .Append("<label class=\"vm-field\"><span>Telefon</span><input name=\"phone\" type=\"tel\" value=\"").Append(V.H(p.Phone)).Append("\" maxlength=\"40\"></label>")
          .Append("<button class=\"vm-btn\" type=\"submit\">Speichern</button></form><p class=\"vm-muted\">Name oder E-Mail ändern? Bitte kurz an den Vorstand wenden.</p></section>");
        sb.Append("<section class=\"vm-card\"><h2>Passwort ändern</h2><form method=\"post\" class=\"vm-form\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"password\">")
          .Append("<label class=\"vm-field\"><span>Bisheriges Passwort</span><input name=\"current\" type=\"password\" autocomplete=\"current-password\" required></label>")
          .Append("<label class=\"vm-field\"><span>Neues Passwort</span><input name=\"new\" type=\"password\" autocomplete=\"new-password\" minlength=\"8\" required></label>")
          .Append("<label class=\"vm-field\"><span>Neues Passwort wiederholen</span><input name=\"repeat\" type=\"password\" autocomplete=\"new-password\" minlength=\"8\" required></label>")
          .Append("<button class=\"vm-btn\" type=\"submit\">Passwort ändern</button></form></section></div>");
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
