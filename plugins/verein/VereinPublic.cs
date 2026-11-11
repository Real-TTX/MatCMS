using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Verein;

/// <summary>
/// The member area under /mitglied. The sign-in form is on /mitglied itself (it posts to the site's own sign-in, which returns here); every other page
/// of the area sends a visitor who is not signed in back to /mitglied — never to the site's general sign-in page. Only accounts the club has a record
/// for (and has not switched off) get in. Everything a person typed is encoded on output.
/// </summary>
public static partial class VereinPublic
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
        b.Route(Base + "/kalender", CalendarAsync, read);
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
        "<div class=\"vm-card vm-login\"><h1>" + V.H(ctx.T("verein.pub.noAccess")) + "</h1><p>" + V.H(ctx.T("verein.pub.noAccessText", VereinSettings.Club(ctx))) +
        "</p>" + LogoutForm(ctx) + "</div>", ctx.T("verein.pub.area"));

    // ---- page frame ---------------------------------------------------------------------------------------------

    private static PluginResult Bare(IPluginContext ctx, string body, string title) => PluginResult.Page(Frame(body), title);

    /// <summary>The member area's band around a page's content — what every page was before the block editor, and what <c>page_html</c> still is.</summary>
    private static string Frame(string body) => "<style>" + VereinCss.Css + "</style><section class=\"section vm\"><div class=\"container\">" + body + "</div></section>";

    private static string LogoutForm(IPluginContext ctx) =>
        "<form method=\"post\" action=\"/anmelden?handler=Logout\" class=\"vm-logout\">" + ctx.Antiforgery +
        "<input type=\"hidden\" name=\"returnUrl\" value=\"" + Base + "\"><button class=\"vm-btn ghost small\" type=\"submit\">" + V.H(ctx.T("verein.pub.signOut")) + "</button></form>";

    /// <summary>
    /// The frame of every member page. Its navigation is the SITE's menu ("Mitglieder" with the pages under it, see <see cref="VereinMenu"/>) — an own
    /// side menu next to it showed every link twice. What is left: where you are, the page's title, who is signed in.
    /// </summary>
    private static async Task<PluginResult> ShellAsync(IPluginContext ctx, Who who, string active, string title, string body, CancellationToken ct)
    {
        var p = who.Person;
        var values = AreaValues(ctx, title, active, p.Name, p.First.Length > 0 ? p.First : p.Name, Initials(p), who.Board,
            VereinPhoto.Avatar(p, Base + "/foto/" + p.Id, Initials(p), 36), body);
        // the page the editors laid out in the block editor (Seiten → "Verein – Mitgliederbereich"); none = the frame as it always was
        var blocks = await VereinPages.RenderAsync(ctx, VereinPages.Area, values, ct);
        return PluginResult.Page(blocks ?? values["page_html"], title);
    }

    /// <summary>
    /// The variables of the member area's page (role <c>area</c>): the words, and the pieces the plugin builds — the head (where you are, the title, who is
    /// signed in), the page's content, and the whole page exactly as it was before (<c>page_html</c>, the starting layout's one slot).
    /// </summary>
    internal static Dictionary<string, string> AreaValues(IPluginContext ctx, string title, string active, string name, string first, string initials, bool board, string avatar, string body)
    {
        var crumb = active == "home" ? V.H(ctx.T("verein.pub.area")) : "<a href=\"" + Base + "\">" + V.H(ctx.T("verein.pub.area")) + "</a>";
        var me = "<a class=\"vm-me\" href=\"" + Base + "/profil\" title=\"" + V.H(ctx.T("verein.pub.myProfile")) + "\">" + avatar + "<span>" + V.H(first) + "</span></a>";
        var head = "<header class=\"vm-head\"><div><p class=\"vm-kicker\">" + crumb + "<span class=\"vm-club\"> · " + V.H(VereinSettings.Club(ctx)) + "</span></p><h1>" + V.H(title) + "</h1></div>" + me + "</header>";
        var content = "<div class=\"vm-main\">" + body + "</div>";
        return new Dictionary<string, string>
        {
            ["title"] = title, ["page"] = active, ["club"] = VereinSettings.Club(ctx), ["name"] = name, ["first_name"] = first, ["initials"] = initials, ["board"] = board ? "1" : "",
            ["page_html"] = Frame(head + content), ["head_html"] = head, ["content_html"] = content, ["me_html"] = me
        };
    }

    /// <summary>Sign out from the menu ("Mitglieder → Abmelden"): a plain link, then back to the start page.</summary>
    private static async Task<PluginResult> SignOutAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (ctx.Member is not null) await ctx.Members.SignOutAsync(ct);
        return PluginResult.Redirect("/", ctx.T("verein.pub.signedOut"));
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
        return await ShellAsync(ctx, who!, "files", ctx.T("verein.pub.uploadPhotos"), VereinUpload.Page(ctx, e, choices, visible, who!.Board, done), ct);
    }

    /// <summary>The "you were there – upload?" cards for the member.</summary>
    private static async Task<string> UploadHintAsync(IPluginContext ctx, VereinStore store, Person me, CancellationToken ct)
    {
        var replies = (await store.AllRepliesAsync(ct)).Where(r => r.MemberId == me.MemberId).ToDictionary(r => r.EventId, r => r.Answer);
        var declined = replies.Where(r => r.Value == VereinStore.No).Select(r => r.Key).ToHashSet();
        var recent = VereinUpload.Recent(await store.EventsAsync(ct), declined);
        if (recent.Count == 0) return "";
        return VereinUpload.Hint(ctx, me, recent, await store.UploadsAsync(ct: ct), replies);
    }

    private static string Notice(IPluginContext ctx) => ctx.Request.Q("ok") switch
    {
        "" => "",
        "antwort" => Ok(ctx.T("verein.pub.ok.answer")),
        "upload" => Ok(ctx.T("verein.pub.ok.upload")),
        "upload-pruefung" => Ok(ctx.T("verein.pub.ok.uploadReview")),
        "freigegeben" => Ok(ctx.T("verein.pub.ok.approved")),
        "geloescht" => Ok(ctx.T("verein.pub.ok.deleted")),
        "profil" => Ok(ctx.T("verein.pub.ok.profile")),
        "passwort" => Ok(ctx.T("verein.pub.ok.password")),
        "kalender" => Ok(ctx.T("verein.pub.ok.calendar")),
        "foto" => Ok(ctx.T("verein.pub.ok.photo")),
        "zusage" => Ok(ctx.T("verein.pub.ok.reply")),
        _ => ""
    };
    private static string Ok(string text) => "<div class=\"vm-ok\" role=\"status\">" + V.H(text) + "</div>";
    private static string Err(string text) => "<div class=\"vm-err\" role=\"alert\">" + V.H(text) + "</div>";

    private static long Id(IPluginContext ctx) => long.TryParse(ctx.Request.R("id"), out var id) ? id : 0;

    // ---- start / sign-in ----------------------------------------------------------------------------------------

    /// <summary>
    /// The variables of the sign-in page (role <c>signin</c>): the words, the card with the form (<c>form_html</c> — the form stays whole: it posts to the
    /// site's own sign-in with its token and comes back here) and the whole page exactly as it was before (<c>page_html</c>).
    /// </summary>
    internal static Dictionary<string, string> SignInValues(IPluginContext ctx, bool failed)
    {
        var intro = VereinSettings.Shown(ctx, VereinSettings.LoginIntro, VereinSettings.DefaultLoginIntro, "verein.set.loginIntroDefault");
        var card = "<div class=\"vm-card vm-login\"><p class=\"vm-kicker\">" + V.H(VereinSettings.Club(ctx)) + "</p><h1>" + V.H(ctx.T("verein.pub.area")) + "</h1>" +
            (string.IsNullOrWhiteSpace(intro) ? "" : V.Paragraphs(intro)) + (failed ? Err(ctx.T("verein.pub.loginFailed")) : "") +
            "<form method=\"post\" action=\"/anmelden\" class=\"vm-form\">" + ctx.Antiforgery + "<input type=\"hidden\" name=\"returnUrl\" value=\"" + Base + "\">" +
            "<label class=\"vm-field\"><span>" + V.H(ctx.T("verein.pub.email")) + "</span><input name=\"username\" type=\"email\" autocomplete=\"username\" required></label>" +
            "<label class=\"vm-field\"><span>" + V.H(ctx.T("verein.pub.password")) + "</span><input name=\"password\" type=\"password\" autocomplete=\"current-password\" required></label>" +
            "<label class=\"vm-check\"><input type=\"checkbox\" name=\"remember\" value=\"true\"> <span>" + V.H(ctx.T("verein.pub.remember")) + "</span></label>" +
            "<button class=\"vm-btn\" type=\"submit\">" + V.H(ctx.T("verein.pub.signIn")) + "</button></form>" +
            "<p class=\"vm-muted\"><a href=\"/anmelden/vergessen\">" + V.H(ctx.T("verein.pub.forgot")) + "</a></p></div>";
        return new Dictionary<string, string>
        {
            ["title"] = ctx.T("verein.pub.area"), ["club"] = VereinSettings.Club(ctx), ["intro"] = intro ?? "", ["failed"] = failed ? "1" : "",
            ["page_html"] = Frame(card), ["form_html"] = card
        };
    }

    private static async Task<PluginResult> HomeAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (ctx.Member is null)
        {
            var values = SignInValues(ctx, ctx.Request.Q("fehler") == "1");
            // the sign-in page the editors laid out (Seiten → "Verein – Anmeldung"); none = the card as it always was
            var blocks = await VereinPages.RenderAsync(ctx, VereinPages.SignIn, values, ct);
            return PluginResult.Page(blocks ?? values["page_html"], ctx.T("verein.pub.area"));
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
        sb.Append("<nav class=\"vm-tiles\" aria-label=\"" + V.H(ctx.T("verein.pub.area")) + "\">")
          .Append(Tile(Base + "/dokumente", "file-check", ctx.T("verein.pub.docs"), open.Count > 0 ? ctx.T("verein.pub.nOpen", open.Count) : ctx.T("verein.pub.allDone"), open.Count > 0))
          .Append(Tile(Base + "/termine", "calendar", ctx.T("verein.pub.dates"), events.Count > 0 ? ctx.T("verein.pub.nextDate", V.Date(ctx, events[0].Starts)) : null))
          .Append(Tile(Base + "/hochladen", "photo-up", ctx.T("verein.pub.uploadPhotos")))
          .Append(Tile(Base + "/dateien", "photo", ctx.T("verein.pub.files")))
          .Append(Tile(Base + "/mitglieder", "users", ctx.T("verein.pub.members")))
          .Append(Tile(Base + "/profil", "user", ctx.T("verein.pub.myProfile")));
        if (who.Board) sb.Append(Tile(Base + "/vorstand", "clipboard-list", ctx.T("verein.pub.board")));
        sb.Append("</nav>");
        sb.Append("<div class=\"vm-grid2\">");
        sb.Append("<section class=\"vm-card vm-todo").Append(open.Count == 0 && replies.Count == 0 ? " is-empty" : "").Append("\"><h2><i class=\"ti ti-file-check\"></i> ").Append(V.H(ctx.T("verein.pub.todo"))).Append("</h2>");
        if (open.Count == 0 && replies.Count == 0) sb.Append("<p class=\"vm-muted\">").Append(V.H(ctx.T("verein.pub.allDoneThanks"))).Append("</p>");
        else
        {
            sb.Append("<ul class=\"vm-list\">");
            foreach (var e in replies) sb.Append("<li><a href=\"").Append(Base).Append("/termine/").Append(e.Id).Append("\"><strong>").Append(V.H(e.Title)).Append("</strong>")
                .Append("<span>").Append(V.H(e.AskRequired ? ctx.T("verein.pub.replyRequired") : ctx.T("verein.pub.areYouIn"))).Append(" · ").Append(V.H(V.When(ctx, e.Starts)))
                .Append(e.AskDue.Length > 0 ? " · " + V.H(ctx.T("verein.pub.pleaseBy", V.Date(ctx, e.AskDue))) : "").Append("</span></a></li>");
            foreach (var d in open) sb.Append("<li><a href=\"").Append(Base).Append("/dokumente/").Append(d.Id).Append("\"><strong>").Append(V.H(d.Title)).Append("</strong>")
                .Append("<span>").Append(V.H(TaskLabel(ctx, d))).Append("</span></a></li>");
            sb.Append("</ul>");
        }
        sb.Append("</section>");
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-users-group\"></i> ").Append(V.H(ctx.T("verein.pub.nextMeetings"))).Append("</h2>");
        if (meetings.Count == 0) sb.Append("<p class=\"vm-muted\">").Append(V.H(ctx.T("verein.pub.noMeeting"))).Append("</p>");
        else
        {
            sb.Append("<ul class=\"vm-list\">");
            foreach (var m in meetings)
                sb.Append("<li><a href=\"").Append(Base).Append("/sitzungen/").Append(m.Id).Append("\"><strong>").Append(V.H(m.Title)).Append("</strong><span>")
                  .Append(V.H(V.When(ctx, m.Starts))).Append(m.Place.Length > 0 ? " · " + V.H(m.Place) : "").Append(" · ")
                  .Append(V.H(rsvps.TryGetValue(m.Id, out var a) ? AnswerLabel(ctx, a) : ctx.T("verein.pub.noReplyYet"))).Append("</span></a></li>");
            sb.Append("</ul>");
        }
        sb.Append("</section></div>");

        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-calendar-event\"></i> ").Append(V.H(ctx.T("verein.pub.upcoming"))).Append("</h2>").Append(await AgendaAsync(ctx, store, p, events, "home", ct))
          .Append("<p class=\"vm-agenda-more\"><a href=\"").Append(Base).Append("/termine\">").Append(V.H(ctx.T("verein.pub.allDates"))).Append(" →</a></p></section>");

        if (uploads.Count > 0)
        {
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-photo\"></i> ").Append(V.H(ctx.T("verein.pub.newUploads"))).Append("</h2><div class=\"vm-thumbs\">");
            foreach (var u in uploads) sb.Append(Thumb(u));
            sb.Append("</div><p><a href=\"").Append(Base).Append("/dateien\">").Append(V.H(ctx.T("verein.pub.allFiles"))).Append(" →</a></p></section>");
        }
        return await ShellAsync(ctx, who, "home", ctx.T("verein.pub.hello", p.First.Length > 0 ? p.First : p.Name), sb.ToString(), ct);
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
        sb.Append("<p class=\"vm-lead\">").Append(V.H(ctx.T("verein.pub.docsLead"))).Append("</p>");
        if (docs.Count == 0) sb.Append("<div class=\"vm-empty\">").Append(V.H(ctx.T("verein.pub.noDocs"))).Append("</div>");
        else
        {
            sb.Append("<div class=\"vm-rows\">");
            foreach (var d in docs)
            {
                answers.TryGetValue(d.Id, out var a);
                sb.Append("<a class=\"vm-row\" href=\"").Append(Base).Append("/dokumente/").Append(d.Id).Append("\"><span class=\"vm-row-main\"><strong>").Append(V.H(d.Title))
                  .Append("</strong><small>").Append(V.H(KindLabel(ctx, d.Kind))).Append(d.Due.Length > 0 ? " · " + V.H(ctx.T("verein.pub.until", V.Date(ctx, d.Due))) : "").Append("</small></span>")
                  .Append(a is null ? "<span class=\"vm-badge warn\">" + V.H(ctx.T("verein.pub.open")) + "</span>" : a.Status == VereinStore.Accepted
                      ? "<span class=\"vm-badge ok\">" + V.H(ctx.T(d.Kind == VereinStore.Read ? "verein.pub.readOn" : d.Kind == VereinStore.Sign ? "verein.pub.signedOn" : "verein.pub.confirmedOn", V.Stamp(ctx, a.At))) + "</span>"
                      : "<span class=\"vm-badge err\">" + V.H(ctx.T("verein.pub.declinedOn", V.Stamp(ctx, a.At))) + "</span>")
                  .Append("</a>");
            }
            sb.Append("</div>");
        }
        return await ShellAsync(ctx, who, "docs", ctx.T("verein.pub.docs"), sb.ToString(), ct);
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
            if (!decline && !V.Checked(ctx.Request.F("agree"))) error = d.Kind == VereinStore.Read ? ctx.T("verein.pub.err.confirmRead") : ctx.T("verein.pub.err.tick");
            else if (!decline && d.Kind == VereinStore.Sign && !SameName(sig, who.Person.Name))
                error = ctx.T("verein.pub.err.signName", who.Person.Name);
            else
            {
                await store.AnswerAsync(d.Id, who.Person.MemberId, decline ? VereinStore.Declined : VereinStore.Accepted, d.Kind == VereinStore.Sign && !decline ? sig : "", ctx.Request.ClientIp, ct);
                ctx.Log($"Dokument #{d.Id} „{d.Title}“: {(decline ? "abgelehnt" : "angenommen")} von {who.Person.Name}");
                return PluginResult.Redirect(Base + "/dokumente?ok=antwort");
            }
        }

        var sb = new StringBuilder();
        sb.Append("<p><a href=\"").Append(Base).Append("/dokumente\">← ").Append(V.H(ctx.T("verein.pub.allDocs"))).Append("</a></p><article class=\"vm-card vm-doc\">");
        sb.Append("<p class=\"vm-kicker\">").Append(V.H(KindLabel(ctx, d.Kind))).Append(d.Due.Length > 0 ? " · " + V.H(ctx.T("verein.pub.pleaseBy", V.Date(ctx, d.Due))) : "").Append("</p>");
        sb.Append("<div class=\"vm-text\">").Append(V.Paragraphs(d.Body)).Append("</div>");
        if (d.File.Length > 0)
            sb.Append("<p><a class=\"vm-btn ghost\" href=\"").Append(Base).Append("/dokumente/").Append(d.Id).Append("/datei\"><i class=\"ti ti-download\"></i> ")
              .Append(V.H(d.FileName.Length > 0 ? d.FileName : ctx.T("verein.pub.openFile"))).Append("</a></p>");
        sb.Append("<p><a class=\"vm-btn ghost small\" href=\"").Append(Base).Append("/dokumente/").Append(d.Id).Append("/pdf\" target=\"_blank\" rel=\"noopener\"><i class=\"ti ti-printer\"></i> ").Append(V.H(ctx.T("verein.pub.pdf"))).Append("</a></p>");
        sb.Append("</article>");

        if (mine is not null)
            sb.Append(mine.Status == VereinStore.Accepted
                ? Ok(d.Kind == VereinStore.Sign ? ctx.T("verein.pub.signedAsOn", mine.Signature, V.Stamp(ctx, mine.At)) : ctx.T(d.Kind == VereinStore.Read ? "verein.pub.readOnFull" : "verein.pub.confirmedOnFull", V.Stamp(ctx, mine.At)))
                : "<div class=\"vm-err\">" + V.H(ctx.T("verein.pub.declinedOnFull", V.Stamp(ctx, mine.At))) + "</div>");
        else
        {
            sb.Append("<form method=\"post\" class=\"vm-card vm-form vm-answer\">").Append(ctx.Antiforgery);
            if (error is not null) sb.Append(Err(error));
            var agree = d.Kind switch
            {
                VereinStore.Read => ctx.T("verein.pub.agree.read"),
                VereinStore.Sign => ctx.T("verein.pub.agree.sign"),
                _ => ctx.T("verein.pub.agree.confirm")
            };
            sb.Append("<label class=\"vm-check\"><input type=\"checkbox\" name=\"agree\" value=\"1\"> <span>").Append(V.H(agree)).Append("</span></label>");
            if (d.Kind == VereinStore.Sign)
                sb.Append("<label class=\"vm-field\"><span>").Append(V.H(ctx.T("verein.pub.signatureLabel"))).Append("</span><input name=\"signature\" autocomplete=\"name\" placeholder=\"")
                  .Append(V.H(who.Person.Name)).Append("\" maxlength=\"120\"><small>").Append(V.H(ctx.T("verein.pub.signatureHelp"))).Append("</small></label>");
            sb.Append("<div class=\"vm-actions\"><button class=\"vm-btn\" type=\"submit\" name=\"action\" value=\"accept\">")
              .Append(V.H(ctx.T(d.Kind switch { VereinStore.Sign => "verein.pub.btn.sign", VereinStore.Read => "verein.pub.btn.read", _ => "verein.pub.btn.confirm" }))).Append("</button>");
            if (d.Kind != VereinStore.Read)
                sb.Append("<button class=\"vm-btn ghost\" type=\"submit\" name=\"action\" value=\"decline\" data-confirm=\"").Append(V.H(ctx.T("verein.pub.declineConfirm"))).Append("\">").Append(V.H(ctx.T("verein.pub.decline"))).Append("</button>");
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
        return VereinPrint.Result(VereinPrint.Meeting(await VereinPrint.LetterheadAsync(ctx, ct), m, minutes), ctx.T(minutes ? "verein.pub.minutesOf" : "verein.pub.invitationOf", m.Title));
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

    private static string KindLabel(IPluginContext ctx, string kind) =>
        ctx.T(kind switch { VereinStore.Sign => "verein.pub.kind.sign", VereinStore.Read => "verein.pub.kind.read", _ => "verein.pub.kind.confirm" });

    /// <summary>"Bitte unterschreiben bis zum 14.02.2027" — the line of an open document on the start page.</summary>
    private static string TaskLabel(IPluginContext ctx, Doc d)
    {
        var kind = d.Kind switch { VereinStore.Sign => "sign", VereinStore.Read => "read", _ => "confirm" };
        return d.Due.Length > 0 ? ctx.T("verein.pub.task." + kind + "By", V.Date(ctx, d.Due)) : ctx.T("verein.pub.task." + kind);
    }

    // ---- dates: events and meetings -----------------------------------------------------------------------------

    private static async Task<List<Meeting>> MeetingsForAsync(IPluginContext ctx, VereinStore store, Who who, CancellationToken ct)
    {
        var board = VereinSettings.BoardList(ctx);
        return (await store.MeetingsAsync(ct)).Where(m => who.Board || VereinStore.InAudience(who.Person, m.Audience, board)).ToList();
    }

    private static string AnswerLabel(IPluginContext ctx, string a) =>
        ctx.T(a switch { VereinStore.Yes => "verein.pub.rsvp.yes", VereinStore.No => "verein.pub.rsvp.no", _ => "verein.pub.rsvp.maybe" });

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

    internal static string EventList(IPluginContext ctx, IEnumerable<Event> events, bool internalBadge, bool memberLinks = false, ISet<long>? needsReply = null, bool publicLinks = false)
    {
        var list = events.ToList();
        if (list.Count == 0) return "<p class=\"vm-muted\">" + V.H(ctx.T("verein.pub.noDates")) + "</p>";
        var sb = new StringBuilder("<ul class=\"vm-events\">");
        foreach (var e in list)
        {
            var (day, month, wd) = V.Badge(ctx, e.Starts);
            sb.Append("<li class=\"").Append(e.Cancelled ? "is-cancelled" : "").Append("\"><span class=\"vm-date\"><b>").Append(V.H(day)).Append("</b><small>").Append(V.H(month)).Append("</small><em>")
              .Append(V.H(wd)).Append("</em></span><span class=\"vm-ev\"><strong>").Append(memberLinks ? "<a href=\"" + Base + "/termine/" + e.Id + "\">" + V.H(e.Title) + "</a>" : publicLinks && e.Public && !e.Draft ? "<a href=\"" + VereinEvents.PagePath(e.Id) + "\">" + V.H(e.Title) + "</a>" : V.H(e.Title)).Append("</strong>");
            if (memberLinks && e.HasPlan) sb.Append(" <span class=\"vm-badge ok\">").Append(V.H(ctx.T("verein.pub.plan"))).Append("</span>");
            if (needsReply?.Contains(e.Id) == true) sb.Append(e.AskRequired ? " <span class=\"vm-badge warn\">" + V.H(ctx.T("verein.pub.replyRequired")) + "</span>" : " <span class=\"vm-badge\">" + V.H(ctx.T("verein.pub.areYouIn")) + "</span>");
            if (e.Cancelled) sb.Append(" <span class=\"vm-badge err\">").Append(V.H(ctx.T("verein.pub.cancelled"))).Append("</span>");
            if (internalBadge && !e.Public) sb.Append(" <span class=\"vm-badge\">").Append(V.H(ctx.T("verein.pub.internal"))).Append("</span>");
            sb.Append("<small>").Append(V.H(V.When(ctx, e.Starts)));
            if (e.Ends.Length > 0 && e.Ends != e.Starts) sb.Append(" – ").Append(V.H(e.Ends[..Math.Min(10, e.Ends.Length)] == e.Day ? V.When(ctx, e.Ends).Split(", ").Last() : V.When(ctx, e.Ends)));
            if (e.Place.Length > 0) sb.Append(" · ").Append(V.H(e.Place));
            sb.Append("</small>");
            if (e.Description.Length > 0) sb.Append("<span class=\"vm-ev-text\">").Append(V.H(e.Description)).Append("</span>");
            if (e.Link.StartsWith("https://", StringComparison.Ordinal) || (e.Link.StartsWith('/') && !e.Link.StartsWith("//", StringComparison.Ordinal)))
                sb.Append("<a href=\"").Append(V.H(e.Link)).Append("\">").Append(V.H(ctx.T("verein.pub.more"))).Append(" →</a>");
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
        if (events.Count == 0) return "<p class=\"vm-muted\">" + V.H(ctx.T("verein.pub.noDates")) + "</p>";
        var asking = events.Any(e => e.Ask && !e.Cancelled);
        var people = asking ? await store.PeopleAsync(ct) : Array.Empty<Person>();
        var board = VereinSettings.BoardList(ctx);
        var answers = asking ? (await store.AllRepliesAsync(ct)).GroupBy(r => (r.EventId, r.MemberId)).ToDictionary(g => g.Key, g => g.First().Answer) : new Dictionary<(long, long), string>();
        var today = V.Today;
        var sb = new StringBuilder("<div class=\"vm-agenda\">");
        string? month = null;
        foreach (var e in events)
        {
            var title = MonthTitle(ctx, e.Starts);
            if (title != month) { sb.Append("<h3 class=\"vm-agenda-month\">").Append(V.H(title)).Append("</h3>"); month = title; }
            var (day, _, wd) = V.Badge(ctx, e.Starts);
            sb.Append("<div class=\"vm-agenda-row").Append(e.Cancelled ? " is-cancelled" : "").Append("\"><a class=\"vm-agenda-main\" href=\"").Append(Base).Append("/termine/").Append(e.Id)
              .Append("\"><span class=\"vm-date sm\"><b>").Append(V.H(day)).Append("</b><em>").Append(V.H(wd)).Append("</em></span><span class=\"vm-agenda-text\"><strong>").Append(V.H(e.Title));
            if (e.HasPlan) sb.Append(" <i class=\"ti ti-list-details\" title=\"").Append(V.H(ctx.T("verein.pub.plan"))).Append("\" aria-label=\"").Append(V.H(ctx.T("verein.pub.plan"))).Append("\"></i>");
            sb.Append("</strong>");
            var meta = new List<string>();
            if (TimeOf(ctx, e) is { Length: > 0 } time) meta.Add(time);
            if (e.Place.Length > 0) meta.Add(e.Place);
            if (meta.Count > 0) sb.Append("<small>").Append(V.H(string.Join(" · ", meta))).Append("</small>");
            if (e.Description.Length > 0) sb.Append("<small class=\"vm-agenda-desc\">").Append(V.H(e.Description.Split('\n')[0].Trim())).Append("</small>");
            sb.Append("</span></a><div class=\"vm-agenda-side\">");
            if (!e.Public) sb.Append("<span class=\"vm-badge\">").Append(V.H(ctx.T("verein.pub.internal"))).Append("</span>");
            if (e.Cancelled) sb.Append("<span class=\"vm-badge err\">").Append(V.H(ctx.T("verein.pub.cancelled"))).Append("</span>");
            else if (e.Ask)
            {
                var mine = VereinStore.AnswersFor(people, me, e, board);
                var open = e.ReplyOpen(today);
                if (mine.Count == 1 && open)
                {
                    var self = mine[0];
                    answers.TryGetValue((e.Id, self.MemberId), out var a);
                    if (e.AskRequired && a is null) sb.Append("<span class=\"vm-badge warn\">").Append(V.H(ctx.T("verein.pub.required"))).Append("</span>");
                    sb.Append("<form method=\"post\" action=\"").Append(Base).Append("/termine/").Append(e.Id).Append("\" class=\"vm-quick\" aria-label=\"").Append(V.H(ctx.T("verein.pub.areYouIn"))).Append("\">").Append(ctx.Antiforgery)
                      .Append("<input type=\"hidden\" name=\"action\" value=\"reply\"><input type=\"hidden\" name=\"back\" value=\"").Append(back).Append("\">")
                      .Append("<button class=\"vm-pick yes").Append(a == VereinStore.Yes ? " on" : "").Append("\" name=\"a.").Append(self.Id).Append("\" value=\"yes\" aria-pressed=\"").Append(a == VereinStore.Yes ? "true" : "false").Append("\"><i class=\"ti ti-check\" aria-hidden=\"true\"></i> ").Append(V.H(ctx.T("verein.pub.in"))).Append("</button>")
                      .Append("<button class=\"vm-pick no").Append(a == VereinStore.No ? " on" : "").Append("\" name=\"a.").Append(self.Id).Append("\" value=\"no\" aria-pressed=\"").Append(a == VereinStore.No ? "true" : "false").Append("\">").Append(V.H(ctx.T("verein.pub.out"))).Append("</button></form>");
                    if (a == VereinStore.Maybe) sb.Append("<span class=\"vm-badge warn\">").Append(V.H(ctx.T("verein.pub.maybe"))).Append("</span>");
                }
                else if (mine.Count > 0)
                {
                    var done = mine.Count(x => answers.ContainsKey((e.Id, x.MemberId)));
                    sb.Append(done == mine.Count ? "<span class=\"vm-badge ok\">" + V.H(ctx.T("verein.pub.answered")) + "</span>"
                        : open ? "<a class=\"vm-badge" + (e.AskRequired ? " warn" : "") + "\" href=\"" + Base + "/termine/" + e.Id + "\">" + V.H(ctx.T("verein.pub.nOfAnswered", done, mine.Count)) + " →</a>" : "");
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

    private static string MonthTitle(IPluginContext ctx, string starts) =>
        starts.Length >= 7 && int.TryParse(starts[5..7], NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= 1 and <= 12
            ? V.MonthNames(ctx)[m - 1] + " " + starts[..4] : "";

    /// <summary>"14:30 Uhr", "14:30–18:00 Uhr", "bis Mo, 10. Februar 2027" (English: "14:30", "until Mon, 10 February 2027") — the day itself is in the date tile.</summary>
    private static string TimeOf(IPluginContext ctx, Event e)
    {
        static string Hm(string s) => s.Length >= 16 && s[11..16] != "00:00" ? s[11..16] : "";
        string Clock(string t) => ctx.T("verein.pub.clock", t);
        var start = Hm(e.Starts);
        var endsOtherDay = e.Ends.Length >= 10 && e.Ends[..10] != e.Day;
        if (endsOtherDay) return (start.Length > 0 ? Clock(start) + " " : "") + ctx.T("verein.pub.untilDay", V.When(ctx, e.Ends));
        var end = e.Ends.Length > 0 ? Hm(e.Ends) : "";
        return start.Length == 0 ? "" : end.Length > 0 && end != start ? Clock(start + "–" + end) : Clock(start);
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
            "<a class=\"vm-row\" href=\"" + Base + "/sitzungen/" + m.Id + "\"><span class=\"vm-row-main\"><strong>" + V.H(m.Title) + "</strong><small>" + V.H(V.When(ctx, m.Starts)) +
            (m.Place.Length > 0 ? " · " + V.H(m.Place) : "") + "</small></span>" +
            (m.Status == VereinStore.Cancelled ? "<span class=\"vm-badge err\">" + V.H(ctx.T("verein.pub.cancelled")) + "</span>"
                : showRsvp ? (rsvps.TryGetValue(m.Id, out var a) ? "<span class=\"vm-badge " + (a == VereinStore.Yes ? "ok" : a == VereinStore.No ? "err" : "warn") + "\">" + V.H(AnswerLabel(ctx, a)) + "</span>" : "<span class=\"vm-badge warn\">" + V.H(ctx.T("verein.pub.replyMissing")) + "</span>")
                : m.MinutesStatus == VereinStore.Final ? "<span class=\"vm-badge ok\">" + V.H(ctx.T("verein.pub.minutes")) + "</span>" : "") + "</a>";

        var sb = new StringBuilder(Notice(ctx));
        sb.Append("<p class=\"vm-actions\"><a class=\"vm-btn ghost small\" href=\"").Append(Base).Append("/kalender\"><i class=\"ti ti-calendar-month\" aria-hidden=\"true\"></i> ").Append(V.H(ctx.T("verein.cal.open"))).Append("</a></p>");
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-calendar-event\"></i> ").Append(V.H(ctx.T("verein.pub.dates"))).Append("</h2>").Append(await AgendaAsync(ctx, store, who.Person, events, "termine", ct)).Append("</section>");
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-users-group\"></i> ").Append(V.H(ctx.T("verein.pub.meetings"))).Append("</h2>");
        if (upcoming.Count == 0) sb.Append("<p class=\"vm-muted\">").Append(V.H(ctx.T("verein.pub.noMeeting"))).Append("</p>");
        else sb.Append("<div class=\"vm-rows\">").Append(string.Concat(upcoming.Select(m => MeetingRow(m, true)))).Append("</div>");
        if (past.Count > 0) sb.Append("<h3>").Append(V.H(ctx.T("verein.pub.pastMeetings"))).Append("</h3><div class=\"vm-rows\">").Append(string.Concat(past.Select(m => MeetingRow(m, false)))).Append("</div>");
        sb.Append("</section>");
        return await ShellAsync(ctx, who, "dates", ctx.T("verein.pub.datesMeetings"), sb.ToString(), ct);
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

        var sb = new StringBuilder(Notice(ctx) + "<p><a href=\"" + Base + "/termine\">← " + V.H(ctx.T("verein.pub.datesMeetings")) + "</a></p>");
        sb.Append("<section class=\"vm-card\">");
        if (e.Cancelled) sb.Append("<div class=\"vm-err\">").Append(V.H(ctx.T("verein.pub.eventCancelled"))).Append("</div>");
        sb.Append("<dl class=\"vm-facts\"><dt>").Append(V.H(ctx.T("verein.pub.when"))).Append("</dt><dd>").Append(V.H(V.When(ctx, e.Starts)));
        if (e.Ends.Length > 0 && e.Ends != e.Starts) sb.Append(" – ").Append(V.H(e.Ends[..Math.Min(10, e.Ends.Length)] == e.Day ? V.When(ctx, e.Ends).Split(", ").Last() : V.When(ctx, e.Ends)));
        sb.Append("</dd>");
        if (e.Place.Length > 0) sb.Append("<dt>").Append(V.H(ctx.T("verein.pub.where"))).Append("</dt><dd>").Append(V.H(e.Place)).Append("</dd>");
        if (e.Kind.Length > 0) sb.Append("<dt>").Append(V.H(ctx.T("verein.pub.kind"))).Append("</dt><dd>").Append(V.H(e.Kind)).Append("</dd>");
        if (e.Meeting.Length > 0) sb.Append("<dt>").Append(V.H(ctx.T("verein.pub.meetingPoint"))).Append("</dt><dd>").Append(V.H(e.Meeting)).Append("</dd>");
        sb.Append("</dl>");
        if (e.Description.Length > 0) sb.Append("<div class=\"vm-text\">").Append(V.Paragraphs(e.Description)).Append("</div>");
        if (e.Link.StartsWith("https://", StringComparison.Ordinal) || (e.Link.StartsWith('/') && !e.Link.StartsWith("//", StringComparison.Ordinal)))
            sb.Append("<p><a href=\"").Append(V.H(e.Link)).Append("\">").Append(V.H(ctx.T("verein.pub.more"))).Append(" →</a></p>");
        sb.Append("</section>");
        if (e.Ask && !e.Cancelled) sb.Append(await ReplyCardAsync(ctx, store, e, mine, people, who!.Board, ct));
        if (e.Steps.Count > 0)
        {
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-clock\"></i> ").Append(V.H(ctx.T("verein.pub.schedule"))).Append("</h2><table class=\"vm-table vm-plan\"><tbody>");
            foreach (var (time, what) in e.Steps) sb.Append("<tr><th>").Append(V.H(time)).Append("</th><td>").Append(V.H(what)).Append("</td></tr>");
            sb.Append("</tbody></table></section>");
        }
        if (e.Parking.Length > 0) sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-parking\"></i> ").Append(V.H(ctx.T("verein.pub.parking"))).Append("</h2><div class=\"vm-text\">").Append(V.Paragraphs(e.Parking)).Append("</div></section>");
        if (e.Notes.Length > 0) sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-info-circle\"></i> ").Append(V.H(ctx.T("verein.pub.notes"))).Append("</h2><div class=\"vm-text\">").Append(V.Paragraphs(e.Notes)).Append("</div></section>");
        if (!e.HasPlan) sb.Append("<p class=\"vm-muted\">").Append(V.H(ctx.T("verein.pub.planFollows"))).Append("</p>");
        var photos = (await store.UploadsAsync(VereinStore.Photo, ct)).Where(u => u.EventId == e.Id && CanSee(u, who!.Person, who.Board)).ToList();
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-photo\"></i> ").Append(V.H(ctx.T("verein.pub.photosN", photos.Count))).Append("</h2>");
        if (photos.Count > 0) sb.Append("<div class=\"vm-thumbs\">").Append(string.Concat(photos.Take(24).Select(Thumb))).Append("</div>");
        sb.Append("<p class=\"vm-actions\"><a class=\"vm-btn\" href=\"").Append(Base).Append("/hochladen?termin=").Append(e.Id).Append("\"><i class=\"ti ti-photo-up\"></i> ").Append(V.H(ctx.T("verein.pub.uploadPhotos"))).Append("</a>");
        if (photos.Count > 24) sb.Append("<a class=\"vm-btn ghost\" href=\"").Append(Base).Append("/dateien?termin=").Append(e.Id).Append("\">").Append(V.H(ctx.T("verein.pub.allNPhotos", photos.Count))).Append("</a>");
        sb.Append("</p></section>");
        return await ShellAsync(ctx, who!, "dates", e.Title, sb.ToString(), ct);
    }

    internal static string ReplyLabel(IPluginContext ctx, string a) =>
        ctx.T(a switch { VereinStore.Yes => "verein.pub.reply.yes", VereinStore.No => "verein.pub.reply.no", _ => "verein.pub.reply.maybe" });

    /// <summary>„Bist du dabei?“ for the member and each of their children; the board sees who has answered.</summary>
    private static async Task<string> ReplyCardAsync(IPluginContext ctx, VereinStore store, Event e, List<Person> mine, IReadOnlyList<Person> people, bool board, CancellationToken ct)
    {
        var replies = (await store.RepliesAsync(e.Id, ct)).ToDictionary(r => r.MemberId);
        var open = e.ReplyOpen(V.Today);
        var sb = new StringBuilder("<section class=\"vm-card vm-reply\"><h2><i class=\"ti ti-hand-stop\"></i> " + V.H(ctx.T("verein.pub.participation")) + "</h2>");
        if (e.AskRequired) sb.Append("<p><span class=\"vm-badge warn\">").Append(V.H(ctx.T("verein.pub.replyRequired"))).Append("</span></p>");
        if (e.AskDue.Length > 0) sb.Append("<p class=\"vm-muted\">").Append(V.H(ctx.T("verein.pub.replyBy", V.Date(ctx, e.AskDue)))).Append("</p>");
        if (mine.Count == 0) sb.Append("<p class=\"vm-muted\">").Append(V.H(ctx.T("verein.pub.noReplyNeeded"))).Append("</p>");
        else if (!open)
            foreach (var p in mine) sb.Append("<p>").Append(V.H(p.Name)).Append(": <strong>").Append(V.H(replies.TryGetValue(p.MemberId, out var r) ? ReplyLabel(ctx, r.Answer) : ctx.T("verein.pub.noReply"))).Append("</strong></p>");
        else
        {
            sb.Append("<form method=\"post\" class=\"vm-form\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"reply\">");
            foreach (var p in mine)
            {
                replies.TryGetValue(p.MemberId, out var r);
                var who = p.Child ? p.Name : ctx.T("verein.pub.you");
                sb.Append("<div class=\"vm-reply-row\" role=\"radiogroup\" aria-label=\"").Append(V.H(who)).Append("\"><strong class=\"vm-reply-name\">").Append(V.H(who)).Append("</strong><div class=\"vm-choice\">");
                foreach (var (val, label) in new[] { (VereinStore.Yes, ctx.T("verein.pub.in")), (VereinStore.Maybe, ctx.T("verein.pub.maybeCap")), (VereinStore.No, ctx.T("verein.pub.out")) })
                    sb.Append("<label><input type=\"radio\" name=\"a.").Append(p.Id).Append("\" value=\"").Append(val).Append('"').Append(r?.Answer == val ? " checked" : "").Append("><span>").Append(V.H(label)).Append("</span></label>");
                sb.Append("</div><input class=\"vm-note\" name=\"n.").Append(p.Id).Append("\" maxlength=\"200\" placeholder=\"").Append(V.H(ctx.T("verein.pub.notePlaceholder"))).Append("\" aria-label=\"").Append(V.H(ctx.T("verein.pub.noteFor", who))).Append("\" value=\"").Append(V.H(r?.Note ?? "")).Append("\"></div>");
            }
            sb.Append("<button class=\"vm-btn\" type=\"submit\">").Append(V.H(ctx.T("verein.pub.saveReply"))).Append("</button></form>");
        }
        if (board)
        {
            var asked = VereinStore.Asked(people, e, VereinSettings.BoardList(ctx)).ToList();
            int Count(string a) => asked.Count(p => replies.TryGetValue(p.MemberId, out var r) && r.Answer == a);
            sb.Append("<h3>").Append(V.H(ctx.T("verein.pub.repliesBoard"))).Append("</h3><p class=\"vm-muted\">")
              .Append(V.H(ctx.T("verein.pub.replyCounts", Count(VereinStore.Yes), Count(VereinStore.Maybe), Count(VereinStore.No), asked.Count(p => !replies.ContainsKey(p.MemberId))))).Append("</p>");
            // folded by answer, names in a compact list: as a table every one of 40 people was a row of its own, on a phone a long scroll
            foreach (var (answer, title, open0) in new[] { (VereinStore.Yes, ctx.T("verein.pub.in"), true), (VereinStore.Maybe, ctx.T("verein.pub.maybeCap"), false), (VereinStore.No, ctx.T("verein.pub.out"), false), ("", ctx.T("verein.pub.stillOpen"), false) })
            {
                var group = asked.Where(p => answer.Length == 0 ? !replies.ContainsKey(p.MemberId) : replies.TryGetValue(p.MemberId, out var x) && x.Answer == answer).OrderBy(p => p.Last).ThenBy(p => p.First).ToList();
                if (group.Count == 0) continue;
                sb.Append("<details class=\"vm-group\"").Append(open0 ? " open" : "").Append("><summary>").Append(V.H(title)).Append(" <span class=\"vm-badge\">").Append(group.Count).Append("</span></summary><ul class=\"vm-names\">");
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
        sb.Append("<p><a href=\"").Append(Base).Append("/termine\">← ").Append(V.H(ctx.T("verein.pub.datesMeetings"))).Append("</a></p>");
        sb.Append("<section class=\"vm-card\"><dl class=\"vm-facts\"><dt>").Append(V.H(ctx.T("verein.pub.when"))).Append("</dt><dd>").Append(V.H(V.When(ctx, m.Starts))).Append("</dd>");
        if (m.Place.Length > 0) sb.Append("<dt>").Append(V.H(ctx.T("verein.pub.where"))).Append("</dt><dd>").Append(V.H(m.Place)).Append("</dd>");
        if (m.Kind.Length > 0) sb.Append("<dt>").Append(V.H(ctx.T("verein.pub.kind"))).Append("</dt><dd>").Append(V.H(VereinAdmin.MeetingKindLabel(ctx, m.Kind))).Append("</dd>");
        sb.Append("</dl>");
        if (m.Status == VereinStore.Cancelled) sb.Append("<div class=\"vm-err\">").Append(V.H(ctx.T("verein.pub.meetingCancelled"))).Append("</div>");
        if (m.Agenda.Length > 0) sb.Append("<h2>").Append(V.H(ctx.T("verein.pub.agenda"))).Append("</h2><div class=\"vm-text\">").Append(V.Paragraphs(m.Agenda)).Append("</div>");
        if (m.Status == VereinStore.Planned)
        {
            sb.Append("<form method=\"post\" class=\"vm-rsvp\">").Append(ctx.Antiforgery).Append("<span>").Append(V.H(ctx.T("verein.pub.comingQ"))).Append("</span><span class=\"vm-seg\">");
            foreach (var (val, label) in new[] { (VereinStore.Yes, ctx.T("verein.pub.imComing")), (VereinStore.Maybe, ctx.T("verein.pub.maybeCap")), (VereinStore.No, ctx.T("verein.pub.cantCome")) })
                sb.Append("<button class=\"vm-pick").Append(mine?.Answer == val ? " on" : "").Append(val == VereinStore.Yes ? " yes" : val == VereinStore.No ? " no" : "").Append("\" type=\"submit\" name=\"action\" value=\"").Append(val)
                  .Append("\" aria-pressed=\"").Append(mine?.Answer == val ? "true" : "false").Append("\">").Append(V.H(label)).Append("</button>");
            sb.Append("</span></form>");
        }
        sb.Append("<p><a class=\"vm-btn ghost small\" href=\"").Append(Base).Append("/sitzungen/").Append(m.Id).Append("/pdf\" target=\"_blank\" rel=\"noopener\"><i class=\"ti ti-printer\"></i> ").Append(V.H(ctx.T("verein.pub.invitationPdf"))).Append("</a></p>");
        sb.Append("</section>");
        if (m.MinutesStatus == VereinStore.Final && m.Minutes.Length > 0)
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-notes\"></i> ").Append(V.H(ctx.T("verein.pub.minutes"))).Append("</h2><div class=\"vm-text\">").Append(V.Paragraphs(m.Minutes)).Append("</div><p><a class=\"vm-btn ghost small\" href=\"").Append(Base).Append("/sitzungen/").Append(m.Id).Append("/pdf?teil=protokoll\" target=\"_blank\" rel=\"noopener\"><i class=\"ti ti-printer\"></i> ").Append(V.H(ctx.T("verein.pub.minutesPdf"))).Append("</a></p></section>");
        if (who.Board)
        {
            var rsvps = await store.RsvpsAsync(m.Id, ct);
            var people = (await store.PeopleAsync(ct)).ToDictionary(p => p.MemberId);
            var invited = VereinStore.Audience(people.Values, m.Audience, VereinSettings.BoardList(ctx)).ToList();
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-clipboard-list\"></i> ").Append(V.H(ctx.T("verein.pub.repliesBoard"))).Append("</h2><table class=\"vm-table\"><thead><tr><th>")
              .Append(V.H(ctx.T("verein.pub.col.name"))).Append("</th><th>").Append(V.H(ctx.T("verein.pub.col.answer"))).Append("</th></tr></thead><tbody>");
            foreach (var p in invited)
            {
                var r = rsvps.FirstOrDefault(x => x.MemberId == p.MemberId);
                sb.Append("<tr><td>").Append(V.H(p.Name)).Append("</td><td>").Append(r is null ? "<span class=\"vm-muted\">–</span>" : V.H(ctx.T(r.Answer == VereinStore.Yes ? "verein.pub.comes" : r.Answer == VereinStore.No ? "verein.pub.notComing" : "verein.pub.reply.maybe"))).Append("</td></tr>");
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
            if (files.Count == 0) error = ctx.T("verein.pub.err.noFile");
            else
            {
                foreach (var f in files)
                {
                    var ext = V.AllowedExt(f.FileName);
                    if (ext.Length == 0) { error = ctx.T("verein.pub.err.type", f.FileName); break; }
                    if (kind == VereinStore.Photo && !V.IsImage(ext)) { error = ctx.T("verein.pub.err.notImage", f.FileName); break; }
                    if (f.Length > max) { error = ctx.T("verein.pub.err.tooBig", f.FileName, V.Size(ctx, max)); break; }
                    byte[] bytes;
                    using (var s = f.OpenRead()) using (var ms = new MemoryStream()) { await s.CopyToAsync(ms, ct); bytes = ms.ToArray(); }
                    if (!V.LooksLike(ext, bytes)) { error = ctx.T("verein.pub.err.broken", f.FileName, ext); break; }
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
        var eventTitle = events.ToDictionary(e => e.Id, e => e.Title + " (" + V.Date(ctx, e.Starts) + ")");
        var filter = ctx.Request.Q("art") == VereinStore.Material ? VereinStore.Material : VereinStore.Photo;
        long.TryParse(ctx.Request.Q("termin"), out var onlyEvent);
        var pending = who!.Board ? all.Where(u => !u.Approved).ToList() : new List<Upload>();

        var sb = new StringBuilder(Notice(ctx));
        if (pending.Count > 0)
        {
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-hourglass\"></i> ").Append(V.H(ctx.T("verein.pub.pendingN", pending.Count))).Append("</h2><div class=\"vm-gallery\">");
            foreach (var u in pending)
                sb.Append("<figure>").Append(Thumb(u)).Append("<figcaption>").Append(V.H(people.TryGetValue(u.MemberId, out var n) ? n : "")).Append(" · ").Append(V.H(V.Date(ctx, u.Taken)))
                  .Append(ApproveButton(ctx, u.Id)).Append(DeleteButton(ctx, u.Id)).Append("</figcaption></figure>");
            sb.Append("</div></section>");
        }
        var recent = events.Where(e => string.CompareOrdinal(e.Day, V.Local(DateTime.UtcNow).AddDays(-120).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) >= 0
            && string.CompareOrdinal(e.Day, V.Local(DateTime.UtcNow).AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) <= 0).OrderByDescending(e => e.Starts).ToList();
        sb.Append(await UploadHintAsync(ctx, store, who.Person, ct));
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-upload\"></i> ").Append(V.H(ctx.T("verein.pub.upload"))).Append("</h2>");
        if (error is not null) sb.Append(Err(error));
        sb.Append(VereinUpload.Panel(ctx, onlyEvent > 0 ? events.FirstOrDefault(e => e.Id == onlyEvent) : null, recent, who.Board, materialToo: true));
        sb.Append("<p class=\"vm-up-done\" hidden><a class=\"vm-btn ghost\" href=\"").Append(Base).Append("/dateien").Append(onlyEvent > 0 ? "?termin=" + onlyEvent : "").Append("\"><i class=\"ti ti-refresh\"></i> ").Append(V.H(ctx.T("verein.pub.reload"))).Append("</a></p></section>");

        sb.Append("<div class=\"vm-tabs\"><a href=\"").Append(Base).Append("/dateien\"").Append(filter == VereinStore.Photo ? " class=\"on\"" : "").Append('>').Append(V.H(ctx.T("verein.pub.photos"))).Append("</a><a href=\"")
          .Append(Base).Append("/dateien?art=material\"").Append(filter == VereinStore.Material ? " class=\"on\"" : "").Append('>').Append(V.H(ctx.T("verein.pub.material"))).Append("</a></div>");
        var shown = visible.Where(u => u.Kind == filter && (onlyEvent == 0 || u.EventId == onlyEvent)).OrderByDescending(u => u.Taken.Length > 0 ? u.Taken : V.Stamp(u.Created)).ToList();
        var withEvents = visible.Where(u => u.Kind == filter && u.EventId > 0 && eventTitle.ContainsKey(u.EventId)).Select(u => u.EventId).Distinct().ToList();
        if (withEvents.Count > 0)
        {
            sb.Append("<div class=\"vm-chips-row\"><a href=\"").Append(Base).Append("/dateien").Append(filter == VereinStore.Material ? "?art=material" : "").Append("\"").Append(onlyEvent == 0 ? " class=\"on\"" : "").Append('>').Append(V.H(ctx.T("verein.pub.all"))).Append("</a>");
            foreach (var id in withEvents)
                sb.Append("<a href=\"").Append(Base).Append("/dateien?termin=").Append(id).Append(filter == VereinStore.Material ? "&art=material" : "").Append("\"").Append(id == onlyEvent ? " class=\"on\"" : "").Append('>').Append(V.H(eventTitle[id])).Append("</a>");
            sb.Append("</div>");
        }
        if (shown.Count == 0) sb.Append("<div class=\"vm-empty\">").Append(V.H(ctx.T("verein.pub.nothingYet"))).Append("</div>");
        else if (filter == VereinStore.Photo)
        {
            sb.Append("<div class=\"vm-gallery\">");
            foreach (var u in shown)
            {
                sb.Append("<figure>").Append(Thumb(u)).Append("<figcaption>").Append(V.H(people.TryGetValue(u.MemberId, out var n) ? n : "")).Append(" · ").Append(V.H(u.Taken.Length > 0 ? V.Date(ctx, u.Taken) : V.Day(ctx, u.Created)));
                if (u.EventId > 0 && eventTitle.TryGetValue(u.EventId, out var et)) sb.Append(" · <a href=\"").Append(Base).Append("/termine/").Append(u.EventId).Append("\">").Append(V.H(et)).Append("</a>");
                if (!u.Shared) sb.Append(" · <span class=\"vm-badge\">").Append(V.H(ctx.T("verein.pub.private"))).Append("</span>");
                if (!u.Approved) sb.Append(" · <span class=\"vm-badge warn\">").Append(V.H(ctx.T("verein.pub.awaitingApproval"))).Append("</span>");
                if (u.MemberId == who.Person.MemberId || who.Board) sb.Append(DeleteButton(ctx, u.Id));
                sb.Append("</figcaption></figure>");
            }
            sb.Append("</div>");
        }
        else
        {
            sb.Append("<table class=\"vm-table\"><thead><tr>").Append(string.Concat(new[] { "title", "by", "event", "size", "date" }.Select(c => "<th>" + V.H(ctx.T("verein.pub.col." + c)) + "</th>"))).Append("<th></th></tr></thead><tbody>");
            foreach (var u in shown)
                sb.Append("<tr><td><a href=\"").Append(Base).Append("/dateien/").Append(u.Id).Append("\"><i class=\"ti ti-file\"></i> ").Append(V.H(u.Title.Length > 0 ? u.Title : u.FileName)).Append("</a>")
                  .Append(u.Shared ? "" : " <span class=\"vm-badge\">" + V.H(ctx.T("verein.pub.private")) + "</span>").Append(u.Approved ? "" : " <span class=\"vm-badge warn\">" + V.H(ctx.T("verein.pub.awaitingApproval")) + "</span>")
                  .Append("</td><td>").Append(V.H(people.TryGetValue(u.MemberId, out var n) ? n : "")).Append("</td><td>").Append(V.H(eventTitle.TryGetValue(u.EventId, out var et) ? et : "–"))
                  .Append("</td><td>").Append(V.H(V.Size(ctx, u.Size))).Append("</td><td>").Append(V.H(V.Stamp(ctx, u.Created))).Append("</td><td>")
                  .Append(u.MemberId == who.Person.MemberId || who.Board ? DeleteButton(ctx, u.Id) : "").Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        return await ShellAsync(ctx, who, "files", ctx.T("verein.pub.files"), sb.ToString(), ct);
    }

    /// <summary>
    /// Many photos from a phone at once: every file goes in a request of its own (a whole batch would hit the server's size limit), with one bar for the
    /// batch. Without script the form posts as it is.
    /// </summary>
    private static string DeleteButton(IPluginContext ctx, long id) =>
        "<form method=\"post\" class=\"vm-inline\" data-confirm=\"" + V.H(ctx.T("verein.pub.deleteConfirm")) + "\">" + ctx.Antiforgery + "<input type=\"hidden\" name=\"action\" value=\"delete\"><input type=\"hidden\" name=\"id\" value=\"" + id +
        "\"><button class=\"vm-link-btn\" type=\"submit\" title=\"" + V.H(ctx.T("verein.pub.delete")) + "\" aria-label=\"" + V.H(ctx.T("verein.pub.delete")) + "\"><i class=\"ti ti-trash\"></i></button></form>";

    private static string ApproveButton(IPluginContext ctx, long id) =>
        "<form method=\"post\" class=\"vm-inline\">" + ctx.Antiforgery + "<input type=\"hidden\" name=\"action\" value=\"approve\"><input type=\"hidden\" name=\"id\" value=\"" + id +
        "\"><button class=\"vm-btn small\" type=\"submit\"><i class=\"ti ti-check\"></i> " + V.H(ctx.T("verein.pub.approve")) + "</button></form>";

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
            var more = PhoneLinks(p.Phone) + (mailable ? "<a href=\"mailto:" + V.H(p.Email) + "\">" + V.Mail(p.Email) + "</a>" : "") + Details(p);
            return "<details class=\"vm-person\"><summary>" + VereinPhoto.Avatar(p, Base + "/foto/" + p.Id, Initials(p), 40) + "<span class=\"vm-person-main\"><strong>" + V.H(p.Name) + "</strong><small>" +
                V.H(string.Join(", ", p.Roles)) + (p.Passive ? " · " + V.H(ctx.T("verein.pub.passive")) : "") + "</small></span><span class=\"vm-person-acts\">" +
                (firstPhone.Length > 0 ? "<a class=\"vm-icon-btn\" href=\"tel:" + V.H(new string(firstPhone.Where(c => char.IsDigit(c) || c == '+').ToArray())) + "\" title=\"" + V.H(ctx.T("verein.pub.callTitle", firstPhone)) + "\" aria-label=\"" + V.H(ctx.T("verein.pub.callLabel", p.Name)) + "\"><i class=\"ti ti-phone\" aria-hidden=\"true\"></i></a>" : "") +
                (mailable ? "<a class=\"vm-icon-btn\" href=\"mailto:" + V.H(p.Email) + "\" title=\"" + V.H(ctx.T("verein.pub.mailTitle", p.Email)) + "\" aria-label=\"" + V.H(ctx.T("verein.pub.mailLabel", p.Name)) + "\"><i class=\"ti ti-mail\" aria-hidden=\"true\"></i></a>" : "") +
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
            if (seeBirthday && p.Born.Length > 0) parts.Add(V.H(ctx.T("verein.pub.bornShort", V.Date(ctx, p.Born))));
            foreach (var f in fields)
                if ((who.Board || f.ForAll) && p.Extra.TryGetValue(f.Label, out var v) && v.Length > 0) parts.Add(V.H(f.Label) + " " + V.H(v));
            return parts.Count == 0 ? "" : "<small>" + string.Join(" · ", parts) + "</small>";
        }

        var sb = new StringBuilder();
        if (all) sb.Append("<label class=\"vm-search\"><i class=\"ti ti-search\" aria-hidden=\"true\"></i><input type=\"search\" placeholder=\"").Append(V.H(ctx.T("verein.pub.searchPlaceholder"))).Append("\" data-vm-filter=\".vm-person\" aria-label=\"").Append(V.H(ctx.T("verein.pub.searchLabel"))).Append("\"></label>")
              .Append("<script>(function(){var i=document.querySelector('[data-vm-filter]');if(!i)return;i.addEventListener('input',function(){var q=i.value.trim().toLowerCase();document.querySelectorAll(i.getAttribute('data-vm-filter')).forEach(function(c){c.hidden=q.length>0&&c.textContent.toLowerCase().indexOf(q)<0;});});})();</script>");
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-star\"></i> ").Append(V.H(ctx.T("verein.pub.board"))).Append("</h2>");
        sb.Append(vorstand.Count == 0 ? "<p class=\"vm-muted\">" + V.H(ctx.T("verein.pub.nobodyYet")) + "</p>" : "<div class=\"vm-people\">" + string.Concat(vorstand.Select(Card)) + "</div>").Append("</section>");
        if (all)
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-users\"></i> ").Append(V.H(ctx.T("verein.pub.allMembersN", people.Count))).Append("</h2><div class=\"vm-people\">")
              .Append(string.Concat(people.Select(Card))).Append("</div></section>");
        if (all && children.Count > 0)
        {
            var byId = everyone.ToDictionary(x => x.Id);
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-mood-kid\"></i> ").Append(V.H(ctx.T("verein.pub.childrenN", children.Count))).Append("</h2><div class=\"vm-people\">");
            foreach (var c in children)
                sb.Append("<div class=\"vm-person\">").Append(VereinPhoto.Avatar(c, Base + "/foto/" + c.Id, Initials(c))).Append("<span><strong>").Append(V.H(c.Name)).Append("</strong><small>")
                  .Append(V.H(string.Join(", ", c.Roles))).Append(c.Born.Length > 0 ? " · " + V.H(V.Date(ctx, c.Born)) : "").Append("</small><small>")
                  .Append(V.H(ctx.T("verein.pub.belongsTo", string.Join(", ", c.Guardians.Select(g => byId.TryGetValue(g, out var x) ? x.Name : "").Where(n => n.Length > 0))))).Append("</small></span></div>");
            sb.Append("</div></section>");
        }
        return await ShellAsync(ctx, who, "people", ctx.T("verein.pub.members"), sb.ToString(), ct);
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
        var sb = new StringBuilder("<section class=\"vm-card\"><h2><i class=\"ti ti-calendar-plus\"></i> " + V.H(ctx.T("verein.pub.cal.title")) + "</h2>");
        sb.Append("<p class=\"vm-muted\">").Append(V.H(ctx.T("verein.pub.cal.lead"))).Append("</p>");
        if (p.CalToken.Length == 40)
        {
            var path = VereinIcal.PersonalFeed(p.CalToken);
            sb.Append("<p><strong>").Append(V.H(ctx.T("verein.pub.cal.mine"))).Append("</strong> ").Append(V.H(ctx.T("verein.pub.cal.mineNote"))).Append("</p><p class=\"vm-actions\"><a class=\"vm-btn\" href=\"").Append(V.H(Webcal(path)))
              .Append("\"><i class=\"ti ti-calendar-plus\"></i> ").Append(V.H(ctx.T("verein.pub.cal.subscribe"))).Append("</a></p><p class=\"vm-muted\" style=\"word-break:break-all\">").Append(V.H(ctx.T("verein.pub.cal.copy"))).Append(" ")
              .Append(V.H(origin + path)).Append("</p><p class=\"vm-muted\">").Append(V.H(ctx.T("verein.pub.cal.secret"))).Append(" ");
            sb.Append("<form method=\"post\" class=\"vm-inline\" data-confirm=\"").Append(V.H(ctx.T("verein.pub.cal.newConfirm"))).Append("\">").Append(ctx.Antiforgery)
              .Append("<input type=\"hidden\" name=\"action\" value=\"calendar\"><button class=\"vm-link-btn\" type=\"submit\" style=\"font-size:14px;text-decoration:underline\">").Append(V.H(ctx.T("verein.pub.cal.new"))).Append("</button></form></p>");
        }
        else
            sb.Append("<form method=\"post\" class=\"vm-form\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"calendar\"><button class=\"vm-btn\" type=\"submit\"><i class=\"ti ti-calendar-plus\"></i> ").Append(V.H(ctx.T("verein.pub.cal.create"))).Append("</button></form>");
        sb.Append("<p class=\"vm-muted\">").Append(V.H(ctx.T("verein.pub.cal.publicOnly"))).Append(" <a href=\"").Append(V.H(Webcal(VereinIcal.PublicFeed))).Append("\">").Append(V.H(ctx.T("verein.pub.cal.subscribeLower"))).Append("</a>.</p></section>");
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
            if (next.Length < 8) error = ctx.T("verein.pub.err.pwShort");
            else if (next != ctx.Request.F("repeat")) error = ctx.T("verein.pub.err.pwMismatch");
            else if (!await ctx.Members.ChangePasswordAsync(p.MemberId, ctx.Request.F("current"), next, ct)) error = ctx.T("verein.pub.err.pwWrong");
            else return PluginResult.Redirect(Base + "/profil?ok=passwort");
        }
        var answers = await store.AnswersOfMemberAsync(p.MemberId, ct);
        var docs = (await store.DocsAsync(false, ct)).Concat(await store.DocsAsync(true, ct)).ToDictionary(d => d.Id);

        var sb = new StringBuilder(Notice(ctx));
        if (error is not null) sb.Append(Err(error));
        sb.Append("<section class=\"vm-card vm-photo-card\"><h2>").Append(V.H(ctx.T("verein.pub.profilePhoto"))).Append("</h2><div class=\"vm-photo-row\">").Append(VereinPhoto.Avatar(p, Base + "/foto/" + p.Id, Initials(p), 96).Replace("class=\"vm-avatar", "id=\"vm-photo-preview\" class=\"vm-avatar"))
          .Append("<form method=\"post\" enctype=\"multipart/form-data\" class=\"vm-form\">").Append(ctx.Antiforgery)
          .Append("<label class=\"vm-field\"><span>").Append(V.H(ctx.T("verein.pub.newPhoto"))).Append("</span><input type=\"file\" name=\"photo\" accept=\"image/*\" data-vm-photo=\"#vm-photo-preview\" required></label>")
          .Append("<div class=\"vm-actions\"><button class=\"vm-btn\" type=\"submit\" name=\"action\" value=\"photo\">").Append(V.H(ctx.T("verein.pub.upload"))).Append("</button>")
          .Append(p.Photo.Length > 0 ? "<button class=\"vm-btn ghost\" type=\"submit\" name=\"action\" value=\"nophoto\" formnovalidate data-confirm=\"" + V.H(ctx.T("verein.pub.removePhotoConfirm")) + "\">" + V.H(ctx.T("verein.pub.remove")) + "</button>" : "")
          .Append("</div></form></div><p class=\"vm-muted\">").Append(V.H(ctx.T("verein.pub.photoNote"))).Append("</p></section>").Append(VereinPhoto.Script);
        sb.Append("<div class=\"vm-grid2\"><section class=\"vm-card\"><h2>").Append(V.H(ctx.T("verein.pub.myDetails"))).Append("</h2><dl class=\"vm-facts\"><dt>").Append(V.H(ctx.T("verein.pub.col.name"))).Append("</dt><dd>").Append(V.H(p.Name)).Append("</dd><dt>").Append(V.H(ctx.T("verein.pub.mail"))).Append("</dt><dd>")
          .Append(V.Mail(p.Email)).Append("</dd><dt>").Append(V.H(ctx.T("verein.pub.membership"))).Append("</dt><dd>").Append(V.H(ctx.T(p.Passive ? "verein.pub.passiveLong" : "verein.pub.active"))).Append("</dd><dt>").Append(V.H(ctx.T("verein.pub.roles"))).Append("</dt><dd>").Append(V.H(p.Roles.Count > 0 ? string.Join(", ", p.Roles) : "–")).Append("</dd>");
        if (p.Joined.Length > 0) sb.Append("<dt>").Append(V.H(ctx.T("verein.pub.memberSince"))).Append("</dt><dd>").Append(V.H(V.Date(ctx, p.Joined))).Append("</dd>");
        if (p.Born.Length > 0) sb.Append("<dt>").Append(V.H(ctx.T("verein.pub.birthday"))).Append("</dt><dd>").Append(V.H(V.Date(ctx, p.Born))).Append("</dd>");
        foreach (var f in VereinSettings.Fields(ctx))
            if (p.Extra.TryGetValue(f.Label, out var v) && v.Length > 0) sb.Append("<dt>").Append(V.H(f.Label)).Append("</dt><dd>").Append(V.H(v)).Append("</dd>");
        sb.Append("</dl><form method=\"post\" class=\"vm-form\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"profile\">")
          .Append("<label class=\"vm-field\"><span>").Append(V.H(ctx.T("verein.pub.phone"))).Append("</span><input name=\"phone\" type=\"tel\" value=\"").Append(V.H(p.Phone)).Append("\" maxlength=\"40\"></label>")
          .Append("<label class=\"vm-field\"><span>").Append(V.H(ctx.T("verein.pub.street"))).Append("</span><input name=\"street\" value=\"").Append(V.H(p.Street)).Append("\" maxlength=\"120\" autocomplete=\"street-address\"></label>")
          .Append("<div class=\"vm-row2\"><label class=\"vm-field\"><span>").Append(V.H(ctx.T("verein.pub.zip"))).Append("</span><input name=\"zip\" value=\"").Append(V.H(p.Zip)).Append("\" maxlength=\"10\" autocomplete=\"postal-code\"></label>")
          .Append("<label class=\"vm-field\"><span>").Append(V.H(ctx.T("verein.pub.city"))).Append("</span><input name=\"city\" value=\"").Append(V.H(p.City)).Append("\" maxlength=\"80\" autocomplete=\"address-level2\"></label></div>")
          .Append("<button class=\"vm-btn\" type=\"submit\">").Append(V.H(ctx.T("verein.pub.save"))).Append("</button></form><p class=\"vm-muted\">").Append(V.H(ctx.T("verein.pub.changeViaBoard"))).Append("</p></section>");
        sb.Append("<section class=\"vm-card\"><h2>").Append(V.H(ctx.T("verein.pub.changePassword"))).Append("</h2><form method=\"post\" class=\"vm-form\">").Append(ctx.Antiforgery).Append("<input type=\"hidden\" name=\"action\" value=\"password\">")
          .Append("<label class=\"vm-field\"><span>").Append(V.H(ctx.T("verein.pub.pwCurrent"))).Append("</span><input name=\"current\" type=\"password\" autocomplete=\"current-password\" required></label>")
          .Append("<label class=\"vm-field\"><span>").Append(V.H(ctx.T("verein.pub.pwNew"))).Append("</span><input name=\"new\" type=\"password\" autocomplete=\"new-password\" minlength=\"8\" required></label>")
          .Append("<label class=\"vm-field\"><span>").Append(V.H(ctx.T("verein.pub.pwRepeat"))).Append("</span><input name=\"repeat\" type=\"password\" autocomplete=\"new-password\" minlength=\"8\" required></label>")
          .Append("<button class=\"vm-btn\" type=\"submit\">").Append(V.H(ctx.T("verein.pub.changePassword"))).Append("</button></form></section></div>");
        sb.Append(CalendarCard(ctx, p));
        var twoFactor = HasTwoFactor(ctx.Member);
        sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-shield-lock\"></i> ").Append(V.H(ctx.T("verein.pub.tfa.title"))).Append("</h2><p>")
          .Append(twoFactor ? "<span class=\"vm-badge ok\">" + V.H(ctx.T("verein.pub.tfa.on")) + "</span> " + V.H(ctx.T("verein.pub.tfa.onText")) + ""
                            : "<span class=\"vm-badge warn\">" + V.H(ctx.T("verein.pub.tfa.off")) + "</span> " + V.H(ctx.T("verein.pub.tfa.offText")) + "")
          .Append("</p><p><a class=\"vm-btn").Append(twoFactor ? " ghost" : "").Append("\" href=\"").Append(MemberPaths.Security).Append("?returnUrl=").Append(Uri.EscapeDataString(Base + "/profil")).Append("\">")
          .Append(V.H(ctx.T(twoFactor ? "verein.pub.tfa.manage" : "verein.pub.tfa.setup"))).Append("</a></p></section>");
        var kids = VereinStore.ChildrenOf(await store.PeopleAsync(ct), p).ToList();
        if (kids.Count > 0)
            sb.Append("<section class=\"vm-card\"><h2><i class=\"ti ti-mood-kid\"></i> ").Append(V.H(ctx.T("verein.pub.myChildren"))).Append("</h2><ul class=\"vm-list\">")
              .Append(string.Concat(kids.Select(k => "<li><span style=\"padding:10px 12px;background:var(--vm-soft);border-radius:10px;display:block\"><strong>" + V.H(k.Name) + "</strong>" + (k.Born.Length > 0 ? " · " + V.H(ctx.T("verein.pub.bornOn", V.Date(ctx, k.Born))) : "") + (k.Roles.Count > 0 ? " · " + V.H(string.Join(", ", k.Roles)) : "") + "</span></li>")))
              .Append("</ul><p class=\"vm-muted\">").Append(V.H(ctx.T("verein.pub.childrenNote"))).Append("</p></section>");
        sb.Append("<section class=\"vm-card\"><h2>").Append(V.H(ctx.T("verein.pub.myConfirmations"))).Append("</h2>");
        if (answers.Count == 0) sb.Append("<p class=\"vm-muted\">").Append(V.H(ctx.T("verein.pub.noneYet"))).Append("</p>");
        else
        {
            sb.Append("<table class=\"vm-table\"><thead><tr><th>").Append(V.H(ctx.T("verein.pub.col.doc"))).Append("</th><th>").Append(V.H(ctx.T("verein.pub.col.answer"))).Append("</th><th>").Append(V.H(ctx.T("verein.pub.when"))).Append("</th></tr></thead><tbody>");
            foreach (var a in answers.OrderByDescending(a => a.At))
                sb.Append("<tr><td>").Append(V.H(docs.TryGetValue(a.DocId, out var d) ? d.Title : "#" + a.DocId)).Append("</td><td>")
                  .Append(V.H(a.Status == VereinStore.Accepted ? (a.Signature.Length > 0 ? ctx.T("verein.pub.signedAs", a.Signature) : ctx.T("verein.pub.confirmed")) : ctx.T("verein.pub.declined")))
                  .Append("</td><td>").Append(V.H(V.Stamp(ctx, a.At))).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");
        return await ShellAsync(ctx, who, "profile", ctx.T("verein.pub.myProfile"), sb.ToString(), ct);
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

        var sb = new StringBuilder("<p class=\"vm-lead\">" + V.H(ctx.T("verein.pub.boardLead")) + "</p>");
        if (docs.Count == 0) sb.Append("<div class=\"vm-empty\">").Append(V.H(ctx.T("verein.pub.noDocsYet"))).Append("</div>");
        foreach (var d in docs)
        {
            var target = VereinStore.Audience(people, d.Audience, board).ToList();
            var mine = answers.Where(a => a.DocId == d.Id).ToDictionary(a => a.MemberId);
            var done = target.Count(p => mine.ContainsKey(p.MemberId));
            sb.Append("<details class=\"vm-card vm-details\"><summary><strong>").Append(V.H(d.Title)).Append("</strong><span class=\"vm-progress\"><span style=\"width:")
              .Append(target.Count == 0 ? 0 : done * 100 / target.Count).Append("%\"></span></span><span>").Append(V.H(ctx.T("verein.pub.nOfM", done, target.Count))).Append("</span></summary>")
              .Append("<table class=\"vm-table\"><thead><tr><th>").Append(V.H(ctx.T("verein.pub.col.name"))).Append("</th><th>").Append(V.H(ctx.T("verein.pub.col.answer"))).Append("</th><th>").Append(V.H(ctx.T("verein.pub.when"))).Append("</th></tr></thead><tbody>");
            foreach (var p in target.OrderBy(p => mine.ContainsKey(p.MemberId)).ThenBy(p => p.Last))
            {
                mine.TryGetValue(p.MemberId, out var a);
                sb.Append("<tr><td>").Append(V.H(p.Name)).Append("</td><td>").Append(a is null ? "<span class=\"vm-badge warn\">" + V.H(ctx.T("verein.pub.open")) + "</span>"
                    : a.Status == VereinStore.Accepted ? "<span class=\"vm-badge ok\">" + V.H(ctx.T(a.Signature.Length > 0 ? "verein.pub.signed" : "verein.pub.confirmed")) + "</span>" : "<span class=\"vm-badge err\">" + V.H(ctx.T("verein.pub.declined")) + "</span>")
                  .Append("</td><td>").Append(V.H(V.Stamp(ctx, a?.At))).Append("</td></tr>");
            }
            sb.Append("</tbody></table></details>");
        }
        return await ShellAsync(ctx, who, "board", ctx.T("verein.pub.board"), sb.ToString(), ct);
    }
}
