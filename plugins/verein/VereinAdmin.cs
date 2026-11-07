using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Verein;

/// <summary>
/// The back office (Admin → Verein): members and their accounts, documents with the proof of who answered, meetings with invitations and minutes,
/// events (public ones feed the block on the website), uploads and templates. Mails go out from here; each action says how many were sent.
/// </summary>
public static class VereinAdmin
{
    public const string Root = "/admin/p/verein/";

    public static void Configure(IPluginBuilder b)
    {
        b.Admin.Section("Verein", "ti-users-group", AdminGroup.Content, 25)
            .Page("dashboard", null, DashboardAsync)
            .Page("members", null, MembersAsync)
            .Page("members/{id}", null, MemberAsync)
            .Page("docs", null, DocsAsync)
            .Page("docs/{id}", null, DocAsync)
            .Page("docs/{id}/file", null, DocFileAsync)
            .Page("docs/{id}/csv", null, DocCsvAsync)
            .Page("meetings", null, MeetingsAsync)
            .Page("meetings/{id}", null, MeetingAsync)
            .Page("events", null, EventsAsync)
            .Page("events/{id}", null, EventAsync)
            .Page("instagram", null, VereinInstagram.PageAsync)
            .Page("uploads", null, UploadsAsync)
            .Page("uploads/{id}", null, UploadFileAsync)
            .Page("templates", null, TemplatesAsync)
            .Page("templates/{id}", null, TemplateAsync)
            .Link("Übersicht", "dashboard", "ti-layout-dashboard")
            .Link("Mitglieder", "members", "ti-users", also: new[] { "members/" })
            .Link("Dokumente", "docs", "ti-file-check", also: new[] { "docs/" })
            .Link("Sitzungen", "meetings", "ti-users-group", also: new[] { "meetings/" })
            .Link("Termine", "events", "ti-calendar-event", also: new[] { "events/" })
            .Link("Instagram-Bild", "instagram", "ti-brand-instagram")
            .Link("Bilder & Material", "uploads", "ti-photo")
            .Link("Vorlagen", "templates", "ti-template", also: new[] { "templates/" })
            .Link("Einstellungen", "_settings", "ti-settings");
    }

    private static long Id(IPluginContext ctx) => long.TryParse(ctx.Request.R("id"), out var id) ? id : 0;
    private static PluginResult Page(UiNode tree, string title) => PluginResult.Page(tree, title);

    private static UiOption[] AudienceOptions(IPluginContext ctx) =>
        new[] { Ui.Option(VereinStore.AudienceAll, "Alle Mitglieder"), Ui.Option(VereinStore.AudienceBoard, "Nur der Vorstand") }
            .Concat(VereinSettings.RoleList(ctx).Select(r => Ui.Option("role:" + r, "Rolle: " + r))).ToArray();

    private static string AudienceLabel(string a) => a == VereinStore.AudienceAll ? "Alle" : a == VereinStore.AudienceBoard ? "Vorstand" : a.StartsWith("role:") ? a[5..] : a;

    private static string Sent(int sent, int failed) => failed == 0 ? $"{sent} E-Mail(s) verschickt." : $"{sent} E-Mail(s) verschickt, {failed} nicht (siehe Plugin-Protokoll).";

    // ---- dashboard ----------------------------------------------------------------------------------------------

    private static async Task<PluginResult> DashboardAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var board = VereinSettings.BoardList(ctx);
        var people = (await store.PeopleAsync(ct)).Where(p => p.Active).ToList();
        var docs = await store.DocsAsync(false, ct);
        var answers = await store.AllAnswersAsync(ct);
        long open = 0;
        foreach (var d in docs)
        {
            var done = answers.Where(a => a.DocId == d.Id).Select(a => a.MemberId).ToHashSet();
            open += VereinStore.Audience(people, d.Audience, board).Count(p => !done.Contains(p.MemberId));
        }
        var events = await store.UpcomingAsync(V.Today, false, 5, ct);
        var next = (await store.MeetingsAsync(ct)).Where(m => m.Status == VereinStore.Planned && string.CompareOrdinal(m.Starts.Length >= 10 ? m.Starts[..10] : m.Starts, V.Today) >= 0)
            .OrderBy(m => m.Starts).FirstOrDefault();

        return Page(Ui.Stack(
            Ui.PageHead("Mitglieder, Dokumente, Sitzungen und Termine", VereinSettings.Club(ctx),
                Ui.Button("Mitgliederbereich öffnen", VereinPublic.Base, UiButtonKind.Ghost, "ti-external-link", small: true)),
            Ui.Stats(
                Ui.Stat("Aktive Mitglieder", people.Count.ToString(CultureInfo.InvariantCulture), href: Root + "members"),
                Ui.Stat("Offene Bestätigungen", open.ToString(CultureInfo.InvariantCulture), tone: open > 0 ? UiTone.Warning : UiTone.Success, href: Root + "docs"),
                Ui.Stat("Kommende Termine", events.Count.ToString(CultureInfo.InvariantCulture), href: Root + "events"),
                Ui.Stat("Nächste Sitzung", next is null ? "–" : V.Date(next.Starts), next?.Title, href: next is null ? Root + "meetings" : Root + "meetings/" + next.Id)),
            Ui.Card("Schnell anlegen", Ui.Inline(
                Ui.Button("Mitglied", Root + "members/new", icon: "ti-user-plus"),
                Ui.Button("Dokument", Root + "docs/new", UiButtonKind.Ghost, "ti-file-plus"),
                Ui.Button("Sitzung", Root + "meetings/new", UiButtonKind.Ghost, "ti-calendar-plus"),
                Ui.Button("Termin", Root + "events/new", UiButtonKind.Ghost, "ti-calendar-event"))),
            Ui.Card("Kommende Termine", events.Count == 0 ? Ui.Muted("Keine.") : Ui.Table(new[] { Ui.Column("Wann"), Ui.Column("Termin"), Ui.Column("Sichtbar") },
                events.Select(e => Ui.Row(V.When(e.Starts), Ui.Link(e.Title, Root + "events/" + e.Id), e.Public ? Ui.Badge("öffentlich", UiTone.Success) : Ui.Badge("intern"))))),
            Ui.Muted("Auf der Website: Der Mitgliederbereich liegt unter " + VereinPublic.Base + ". Öffentliche Termine lassen sich mit dem Block „Vereinstermine“ auf jede Seite setzen.")
        ), "Verein");
    }

    // ---- members ------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> MembersAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var people = await store.PeopleAsync(ct);
        var board = VereinSettings.BoardList(ctx);
        var docs = await store.DocsAsync(false, ct);
        var answers = await store.AllAnswersAsync(ct);
        int OpenOf(Person p) => docs.Count(d => VereinStore.InAudience(p, d.Audience, board) && !answers.Any(a => a.DocId == d.Id && a.MemberId == p.MemberId));

        var table = Ui.Table(new[] { Ui.Column("Name"), Ui.Column("Rollen"), Ui.Column("E-Mail"), Ui.Column("Telefon"), Ui.Column("Offen", right: true), Ui.Column("Status") },
            people.Select(p => Ui.RowFor(p.Name + " " + p.Email + " " + string.Join(" ", p.Roles),
                Ui.Link(p.Name.Length > 0 ? p.Name : p.Email, Root + "members/" + p.Id), string.Join(", ", p.Roles), p.Email, p.Phone,
                p.Active ? OpenOf(p) : 0, p.Active ? Ui.Badge("aktiv", UiTone.Success) : Ui.Badge("inaktiv"))),
            searchPlaceholder: "Mitglieder durchsuchen …", pageSize: 50, emptyText: "Noch keine Mitglieder. Lege das erste an – es bekommt eine E-Mail mit dem Zugang.");
        return Page(Ui.Stack(Ui.PageHead(people.Count + " Mitglieder", "Mitglieder", Ui.Button("Neues Mitglied", Root + "members/new", icon: "ti-user-plus")), table), "Mitglieder");
    }

    private static async Task<PluginResult> MemberAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var isNew = ctx.Request.R("id") == "new";
        var p = isNew ? null : await store.PersonAsync(Id(ctx), ct);
        if (!isNew && p is null) return PluginResult.NotFound();
        var roles = VereinSettings.RoleList(ctx);

        if (ctx.Request.IsPost)
        {
            if (ctx.Request.Action == "invite" && p is not null)
            {
                var (s, f) = await InviteAsync(ctx, p, ct);
                return PluginResult.Redirect(Root + "members/" + p.Id, f == 0 ? "Einladung verschickt." : "Einladung konnte nicht verschickt werden (siehe Plugin-Protokoll).", f > 0);
            }
            if (ctx.Request.Action == "remove" && p is not null)
            {
                await store.People.DeleteAsync(p.Id, ct);
                if (V.Checked(ctx.Request.F("deleteAccount"))) await ctx.Members.DeleteAsync(p.MemberId, ct);
                ctx.Log($"Mitglied {p.Name} entfernt von {ctx.User.Name}");
                return PluginResult.Redirect(Root + "members", p.Name + " ist entfernt.");
            }

            var first = V.Clip(ctx.Request.F("first"), 80);
            var last = V.Clip(ctx.Request.F("last"), 80);
            var email = V.Clip(ctx.Request.F("email"), 200).ToLowerInvariant();
            var chosen = roles.Where((r, i) => V.Checked(ctx.Request.F("role_" + i))).ToList();
            var phone = V.Clip(ctx.Request.F("phone"), 40);
            var joined = V.CleanDate(ctx.Request.F("joined"));
            var note = V.Clip(ctx.Request.F("note"), 2000);
            var active = isNew || V.Checked(ctx.Request.F("active"));
            if (first.Length == 0 && last.Length == 0) return PluginResult.Redirect(Root + "members/" + (isNew ? "new" : p!.Id), "Bitte einen Namen eingeben.", true);

            if (isNew)
            {
                if (!email.Contains('@')) return PluginResult.Redirect(Root + "members/new", "Bitte eine gültige E-Mail-Adresse eingeben – sie ist der Benutzername.", true);
                // The account: an existing one of the site is linked, otherwise one is created with a random password the person replaces via the invitation.
                var account = await ctx.Members.FindByEmailAsync(email, ct);
                if (account is null)
                {
                    var created = await ctx.Members.CreateAsync(email, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)), (first + " " + last).Trim(), ct);
                    if (created.Status != MemberCreateStatus.Created || created.Account is null)
                        return PluginResult.Redirect(Root + "members/new", created.Status == MemberCreateStatus.InvalidEmail ? "Die E-Mail-Adresse ist ungültig." : "Das Konto konnte nicht angelegt werden.", true);
                    account = created.Account;
                }
                if (await store.PersonOfMemberAsync(account.Id, ct) is Person existing)
                    return PluginResult.Redirect(Root + "members/" + existing.Id, "Zu dieser E-Mail-Adresse gibt es schon ein Mitglied.", true);
                var id = await store.SavePersonAsync(0, account.Id, first, last, email, phone, chosen, joined, true, note, ct);
                var person = (await store.PersonAsync(id, ct))!;
                var notice = (first + " " + last).Trim() + " ist angelegt.";
                if (V.Checked(ctx.Request.F("sendInvite")))
                {
                    var (_, f) = await InviteAsync(ctx, person, ct);
                    notice += f == 0 ? " Die Einladung ist verschickt." : " Die Einladung konnte nicht verschickt werden.";
                }
                ctx.Log($"Mitglied {person.Name} angelegt von {ctx.User.Name}");
                return PluginResult.Redirect(Root + "members/" + id, notice);
            }
            await store.SavePersonAsync(p!.Id, p.MemberId, first, last, p.Email, phone, chosen, joined, active, note, ct);
            return PluginResult.Redirect(Root + "members/" + p.Id, "Gespeichert.");
        }

        var form = Ui.Form(
            Ui.Grid(240, Ui.Input("first", "Vorname", p?.First ?? ""), Ui.Input("last", "Nachname", p?.Last ?? "")),
            isNew ? Ui.Input("email", "E-Mail-Adresse (Benutzername)", "", UiInputType.Email) : Ui.KeyValue(("E-Mail-Adresse", p!.Email)),
            Ui.Grid(240, Ui.Input("phone", "Telefon", p?.Phone ?? "", UiInputType.Tel), Ui.Input("joined", "Mitglied seit", p?.Joined ?? "", UiInputType.Date)),
            Ui.Heading("Rollen", 3),
            Ui.Grid(220, roles.Select((r, i) => (UiNode?)Ui.Checkbox("role_" + i, r, p?.Has(r) ?? (isNew && r == roles[^1]))).ToArray()),
            Ui.TextArea("note", "Notiz (nur im Admin)", p?.Note ?? "", 3),
            isNew ? Ui.Checkbox("sendInvite", "Einladung mit Zugang per E-Mail schicken", true) : Ui.Checkbox("active", "Aktiv (Zugang zum Mitgliederbereich)", p!.Active),
            Ui.FormActions(Ui.Submit(isNew ? "Anlegen" : "Speichern"), Ui.Button("Zurück", Root + "members", UiButtonKind.Ghost)));

        if (isNew) return Page(Ui.Stack(Ui.PageHead("Ein Konto für den Mitgliederbereich wird angelegt", "Neues Mitglied"), Ui.Card(form)), "Neues Mitglied");

        var answers = await store.AnswersOfMemberAsync(p!.MemberId, ct);
        var docs = (await store.DocsAsync(false, ct)).Concat(await store.DocsAsync(true, ct)).ToDictionary(d => d.Id);
        var side = new List<UiNode?>
        {
            Ui.Card("Zugang",
                Ui.Muted("Die Einladung erklärt, wie man ein eigenes Passwort festlegt, und verlinkt den Mitgliederbereich."),
                Ui.Inline(Ui.Action("Einladung erneut senden", "invite", kind: UiButtonKind.Ghost, icon: "ti-mail"))),
            Ui.Card("Bestätigungen", answers.Count == 0 ? Ui.Muted("Noch keine.") : Ui.Table(new[] { Ui.Column("Dokument"), Ui.Column("Antwort"), Ui.Column("Wann") },
                answers.OrderByDescending(a => a.At).Select(a => Ui.Row(docs.TryGetValue(a.DocId, out var d) ? Ui.Link(d.Title, Root + "docs/" + d.Id) : "#" + a.DocId,
                    a.Status == VereinStore.Accepted ? Ui.Badge(a.Signature.Length > 0 ? "unterschrieben" : "bestätigt", UiTone.Success) : Ui.Badge("abgelehnt", UiTone.Error), V.Stamp(a.At))))),
            Ui.Card("Entfernen", Ui.Form(Ui.Muted("Nimmt die Person aus dem Verein. Ihre bisherigen Antworten bleiben als Nachweis erhalten."),
                Ui.Checkbox("deleteAccount", "Auch das Konto der Website löschen (Uploads und Rückmeldungen werden dann gelöscht)"),
                Ui.FormActions(Ui.Submit("Aus dem Verein entfernen", UiButtonKind.Danger, "action", "remove", confirm: "Wirklich entfernen?"))))
        };
        return Page(Ui.Stack(Ui.PageHead(string.Join(", ", p.Roles), p.Name, Ui.Button("Zurück", Root + "members", UiButtonKind.Ghost, small: true)),
            Ui.Columns(new UiNode?[] { Ui.Card("Angaben", form) }, side)), p.Name);
    }

    private static Task<(int, int)> InviteAsync(IPluginContext ctx, Person p, CancellationToken ct) =>
        VereinMail.SendAsync(ctx, VereinMail.Invite, new[] { p }, _ => new Dictionary<string, string>
        {
            ["email"] = p.Email, ["password_url"] = VereinMail.Origin(ctx) + "/anmelden/vergessen"
        }, ct);

    // ---- documents ----------------------------------------------------------------------------------------------

    private static async Task<PluginResult> DocsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var archived = ctx.Request.Q("archiv") == "1";
        var docs = await store.DocsAsync(archived, ct);
        var people = (await store.PeopleAsync(ct)).Where(p => p.Active).ToList();
        var answers = await store.AllAnswersAsync(ct);
        var board = VereinSettings.BoardList(ctx);

        var rows = docs.Select(d =>
        {
            var target = VereinStore.Audience(people, d.Audience, board).Select(p => p.MemberId).ToHashSet();
            var done = answers.Count(a => a.DocId == d.Id && target.Contains(a.MemberId));
            return Ui.RowFor(d.Title, Ui.Link(d.Title, Root + "docs/" + d.Id), Kind(d.Kind), AudienceLabel(d.Audience), d.Due.Length > 0 ? V.Date(d.Due) : "–",
                Ui.Badge(done + " / " + target.Count, done >= target.Count && target.Count > 0 ? UiTone.Success : UiTone.Warning), V.Stamp(d.Created));
        });
        return Page(Ui.Stack(
            Ui.PageHead("Zum Bestätigen, Unterschreiben oder Lesen – mit Nachweis, wer wann geantwortet hat", "Dokumente", Ui.Button("Neues Dokument", Root + "docs/new", icon: "ti-file-plus")),
            Ui.Tabs(Ui.Tab("Aktuell", Root + "docs", !archived), Ui.Tab("Archiv", Root + "docs?archiv=1", archived)),
            Ui.Table(new[] { Ui.Column("Titel"), Ui.Column("Art"), Ui.Column("Für"), Ui.Column("Frist"), Ui.Column("Beantwortet"), Ui.Column("Angelegt") }, rows,
                searchPlaceholder: "Dokumente durchsuchen …", emptyText: archived ? "Das Archiv ist leer." : "Noch keine Dokumente.")), "Dokumente");
    }

    private static string Kind(string k) => k switch { VereinStore.Sign => "Unterschreiben", VereinStore.Read => "Lesen", _ => "Bestätigen" };

    private static async Task<PluginResult> DocAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        if (ctx.Request.R("id") == "new") return await NewDocAsync(ctx, store, ct);
        var d = await store.DocAsync(Id(ctx), ct);
        if (d is null) return PluginResult.NotFound();
        var board = VereinSettings.BoardList(ctx);
        var people = (await store.PeopleAsync(ct)).ToList();
        var target = VereinStore.Audience(people, d.Audience, board).ToList();
        var answers = (await store.AnswersOfDocAsync(d.Id, ct)).ToDictionary(a => a.MemberId);

        if (ctx.Request.IsPost)
        {
            switch (ctx.Request.Action)
            {
                case "remind":
                    var open = target.Where(p => !answers.ContainsKey(p.MemberId)).ToList();
                    var (s, f) = await VereinMail.SendAsync(ctx, VereinMail.Reminder, open, _ => DocValues(ctx, d), ct);
                    return PluginResult.Redirect(Root + "docs/" + d.Id, "Erinnerung: " + Sent(s, f), f > 0);
                case "archive":
                    await store.Docs.UpdateAsync(d.Id, new Dictionary<string, object?> { ["archived"] = !d.Archived }, ct);
                    return PluginResult.Redirect(Root + "docs/" + d.Id, d.Archived ? "Wieder aktiv." : "Archiviert – im Mitgliederbereich nicht mehr sichtbar.");
                case "delete":
                    if (answers.Count > 0) return PluginResult.Redirect(Root + "docs/" + d.Id, "Ein Dokument mit Antworten wird nicht gelöscht (Nachweis) – archiviere es.", true);
                    if (d.File.Length > 0) await ctx.Files.DeleteAsync(d.File, ct);
                    await store.Docs.DeleteAsync(d.Id, ct);
                    return PluginResult.Redirect(Root + "docs", "Gelöscht.");
            }
            return PluginResult.Redirect(Root + "docs/" + d.Id);
        }

        var done = target.Count(p => answers.ContainsKey(p.MemberId));
        var table = Ui.Table(new[] { Ui.Column("Name"), Ui.Column("Antwort"), Ui.Column("Unterschrift"), Ui.Column("Wann"), Ui.Column("Adresse") },
            target.OrderBy(p => answers.ContainsKey(p.MemberId)).ThenBy(p => p.Last).Select(p =>
            {
                answers.TryGetValue(p.MemberId, out var a);
                return Ui.RowFor(p.Name, Ui.Link(p.Name, Root + "members/" + p.Id),
                    a is null ? Ui.Badge("offen", UiTone.Warning) : a.Status == VereinStore.Accepted ? Ui.Badge(d.Kind == VereinStore.Read ? "gelesen" : d.Kind == VereinStore.Sign ? "unterschrieben" : "bestätigt", UiTone.Success) : Ui.Badge("abgelehnt", UiTone.Error),
                    a?.Signature ?? "", V.Stamp(a?.At), a?.Ip ?? "");
            }), searchPlaceholder: "Namen suchen …", emptyText: "Für dieses Dokument ist niemand vorgesehen.");
        // Answers of people who are no longer in the audience (role changed, removed) stay visible as proof.
        var others = answers.Values.Where(a => !target.Any(p => p.MemberId == a.MemberId)).ToList();

        var main = new List<UiNode?>
        {
            Ui.Card("Text", Ui.Html("<div class=\"vm-text\">" + V.Paragraphs(d.Body) + "</div>"),
                d.File.Length > 0 ? Ui.Button(d.FileName.Length > 0 ? d.FileName : "Datei", Root + "docs/" + d.Id + "/file", UiButtonKind.Ghost, "ti-download", small: true) : null),
            Ui.Card("Antworten (" + done + " von " + target.Count + ")", table),
            others.Count > 0 ? Ui.Card("Weitere Antworten (nicht mehr in der Zielgruppe)", Ui.Table(new[] { Ui.Column("Mitglied"), Ui.Column("Antwort"), Ui.Column("Wann") },
                others.Select(a => Ui.Row(people.FirstOrDefault(p => p.MemberId == a.MemberId)?.Name ?? "Gelöschtes Mitglied", a.Status == VereinStore.Accepted ? "ja" : "abgelehnt", V.Stamp(a.At))))) : null
        };
        var side = new List<UiNode?>
        {
            Ui.Card("Angaben", Ui.KeyValue(("Art", Kind(d.Kind)), ("Für", AudienceLabel(d.Audience)), ("Frist", d.Due.Length > 0 ? V.Date(d.Due) : "–"),
                ("Angelegt", V.Stamp(d.Created) + (d.CreatedBy.Length > 0 ? " von " + d.CreatedBy : "")), ("Status", d.Archived ? "Archiv" : "aktiv"))),
            Ui.Card("Aktionen", Ui.Stack(
                d.Archived ? null : Ui.Action("Offene erinnern (" + (target.Count - done) + ")", "remind", kind: UiButtonKind.Primary, icon: "ti-mail", confirm: "Erinnerung an alle schicken, die noch nicht geantwortet haben?"),
                Ui.Button("Liste als CSV", Root + "docs/" + d.Id + "/csv", UiButtonKind.Ghost, "ti-file-spreadsheet", small: true),
                Ui.Action(d.Archived ? "Wieder aktivieren" : "Archivieren", "archive", icon: "ti-archive"),
                answers.Count == 0 ? Ui.Action("Löschen", "delete", kind: UiButtonKind.Danger, confirm: "Dokument löschen?", icon: "ti-trash") : null))
        };
        return Page(Ui.Stack(Ui.PageHead(Kind(d.Kind) + " · " + AudienceLabel(d.Audience), d.Title, Ui.Button("Zurück", Root + "docs", UiButtonKind.Ghost, small: true)),
            Ui.Columns(main, side)), d.Title);
    }

    private static Dictionary<string, string> DocValues(IPluginContext ctx, Doc d) => new()
    {
        ["titel"] = d.Title, ["aufgabe"] = VereinMail.Task(d.Kind), ["frist"] = VereinMail.Due(d.Due), ["doc_url"] = VereinMail.Origin(ctx) + VereinPublic.Base + "/dokumente/" + d.Id
    };

    private static async Task<PluginResult> NewDocAsync(IPluginContext ctx, VereinStore store, CancellationToken ct)
    {
        if (ctx.Request.IsPost)
        {
            var title = V.Clip(ctx.Request.F("title"), 160);
            var body = V.Clip(ctx.Request.F("body"), 30000);
            var kind = ctx.Request.F("kind") is VereinStore.Sign or VereinStore.Read ? ctx.Request.F("kind") : VereinStore.Confirm;
            var audience = AudienceOptions(ctx).Any(o => o.Value == ctx.Request.F("audience")) ? ctx.Request.F("audience") : VereinStore.AudienceAll;
            if (title.Length < 3) return PluginResult.Redirect(Root + "docs/new", "Bitte einen Titel eingeben.", true);
            var upload = ctx.Request.File("file");
            string file = "", fileName = "", fileType = "";
            if (upload is not null && upload.Length > 0)
            {
                var ext = V.AllowedExt(upload.FileName);
                if (ext.Length == 0) return PluginResult.Redirect(Root + "docs/new", "Diese Art von Datei geht nicht (erlaubt: PDF, Bilder, Office, Text).", true);
                if (upload.Length > 25 * 1024 * 1024) return PluginResult.Redirect(Root + "docs/new", "Die Datei ist größer als 25 MB.", true);
                file = "d/" + Guid.NewGuid().ToString("N") + ext; fileName = V.SafeName(upload.FileName); fileType = V.Types[ext];
                using var s = upload.OpenRead();
                await ctx.Files.SaveAsync(file, s, ct);
            }
            var id = await store.InsertDocAsync(title, body, kind, audience, V.CleanDate(ctx.Request.F("due")), ctx.User.Name, ct);
            if (file.Length > 0) await store.Docs.UpdateAsync(id, new Dictionary<string, object?> { ["file"] = file, ["file_name"] = fileName, ["file_type"] = fileType }, ct);
            var d = (await store.DocAsync(id, ct))!;
            var notice = "Das Dokument ist im Mitgliederbereich.";
            if (V.Checked(ctx.Request.F("notify")))
            {
                var people = VereinStore.Audience(await store.PeopleAsync(ct), d.Audience, VereinSettings.BoardList(ctx));
                var (s, f) = await VereinMail.SendAsync(ctx, VereinMail.Document, people, _ => DocValues(ctx, d), ct);
                notice += " " + Sent(s, f);
            }
            ctx.Log($"Dokument #{id} „{title}“ angelegt von {ctx.User.Name}");
            return PluginResult.Redirect(Root + "docs/" + id, notice);
        }

        var templates = await store.TemplatesAsync(VereinStore.TplDocument, ct);
        Template? tpl = long.TryParse(ctx.Request.Q("vorlage"), out var tid) ? await store.TemplateAsync(tid, ct) : null;
        var form = Ui.Form(
            Ui.Input("title", "Titel", tpl is null ? "" : V.Fill(tpl.Title, ctx)),
            Ui.TextArea("body", "Text", tpl is null ? "" : V.Fill(tpl.Body, ctx), 14) with { Help = "Reiner Text; Leerzeilen trennen Absätze." },
            Ui.Grid(220,
                Ui.Select("kind", "Was sollen die Mitglieder tun?", VereinStore.Confirm, Ui.Option(VereinStore.Confirm, "Bestätigen (Haken)"), Ui.Option(VereinStore.Sign, "Unterschreiben (Name eintippen)"), Ui.Option(VereinStore.Read, "Nur lesen")),
                Ui.Select("audience", "Für wen?", VereinStore.AudienceAll, AudienceOptions(ctx)),
                Ui.Input("due", "Frist (freiwillig)", "", UiInputType.Date)),
            Ui.File("file", "Datei anhängen (freiwillig, z. B. PDF)"),
            Ui.Checkbox("notify", "Alle Betroffenen per E-Mail benachrichtigen", true),
            Ui.FormActions(Ui.Submit("Dokument veröffentlichen"), Ui.Button("Abbrechen", Root + "docs", UiButtonKind.Ghost))) with { Upload = true };
        var side = Ui.Card("Vorlage verwenden", templates.Count == 0 ? Ui.Muted("Keine Vorlagen.") : Ui.Stack(templates.Select(t => (UiNode?)Ui.Button(t.Name, Root + "docs/new?vorlage=" + t.Id, t.Id == tpl?.Id ? UiButtonKind.Primary : UiButtonKind.Ghost, small: true)).ToArray()),
            Ui.Muted("Vorlagen sind ein Ausgangspunkt – bitte vor dem Veröffentlichen prüfen und anpassen."), Ui.Link("Vorlagen bearbeiten", Root + "templates"));
        return Page(Ui.Stack(Ui.PageHead("Erscheint sofort im Mitgliederbereich", "Neues Dokument"), Ui.Columns(new UiNode?[] { Ui.Card(form) }, new UiNode?[] { side })), "Neues Dokument");
    }

    private static async Task<PluginResult> DocFileAsync(IPluginContext ctx, CancellationToken ct)
    {
        var d = await new VereinStore(ctx.Data).DocAsync(Id(ctx), ct);
        return d is null || d.File.Length == 0 ? PluginResult.NotFound() : await VereinPublic.ServeAsync(ctx, d.File, d.FileName, d.FileType, ct);
    }

    /// <summary>The proof as a spreadsheet (semicolon, UTF-8 with BOM for Excel; a leading = + - @ is quoted against formula injection).</summary>
    private static async Task<PluginResult> DocCsvAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var d = await store.DocAsync(Id(ctx), ct);
        if (d is null) return PluginResult.NotFound();
        var people = await store.PeopleAsync(ct);
        var answers = (await store.AnswersOfDocAsync(d.Id, ct)).ToDictionary(a => a.MemberId);
        static string C(string s) { s ??= ""; if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
        var sb = new StringBuilder("﻿Name;E-Mail;Antwort;Unterschrift;Zeitpunkt;Adresse\r\n");
        foreach (var p in VereinStore.Audience(people, d.Audience, VereinSettings.BoardList(ctx)))
        {
            answers.TryGetValue(p.MemberId, out var a);
            sb.Append(string.Join(";", C(p.Name), C(p.Email), C(a is null ? "offen" : a.Status == VereinStore.Accepted ? "ja" : "abgelehnt"), C(a?.Signature ?? ""), C(V.Stamp(a?.At)), C(a?.Ip ?? ""))).Append("\r\n");
        }
        return PluginResult.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", V.SafeName(d.Title) + ".csv");
    }

    // ---- meetings -----------------------------------------------------------------------------------------------

    private static readonly string[] MeetingKinds = { "Vorstandssitzung", "Mitgliederversammlung", "Jahreshauptversammlung", "Besprechung", "Sonstiges" };

    private static async Task<PluginResult> MeetingsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var list = await store.MeetingsAsync(ct);
        var rows = new List<UiTableRow>();
        foreach (var m in list)
        {
            var r = await store.RsvpsAsync(m.Id, ct);
            rows.Add(Ui.RowFor(m.Title + " " + m.Kind, V.When(m.Starts), Ui.Link(m.Title, Root + "meetings/" + m.Id), m.Kind, AudienceLabel(m.Audience),
                r.Count(x => x.Answer == VereinStore.Yes) + " zu / " + r.Count(x => x.Answer == VereinStore.No) + " ab",
                m.Status switch { VereinStore.Cancelled => Ui.Badge("abgesagt", UiTone.Error), VereinStore.Held => Ui.Badge("stattgefunden"), _ => Ui.Badge("geplant", UiTone.Info) },
                m.MinutesStatus == VereinStore.Final ? Ui.Badge("veröffentlicht", UiTone.Success) : m.Minutes.Length > 0 ? Ui.Badge("Entwurf", UiTone.Warning) : Ui.Text("–")));
        }
        return Page(Ui.Stack(Ui.PageHead("Einladen, Zu- und Absagen sehen, Protokoll schreiben", "Sitzungen", Ui.Button("Neue Sitzung", Root + "meetings/new", icon: "ti-calendar-plus")),
            Ui.Table(new[] { Ui.Column("Wann"), Ui.Column("Titel"), Ui.Column("Art"), Ui.Column("Für"), Ui.Column("Rückmeldungen"), Ui.Column("Status"), Ui.Column("Protokoll") }, rows,
                searchPlaceholder: "Sitzungen durchsuchen …", emptyText: "Noch keine Sitzungen.")), "Sitzungen");
    }

    private static Dictionary<string, string> MeetingValues(IPluginContext ctx, Meeting m) => new()
    {
        ["titel"] = m.Title, ["termin"] = V.When(m.Starts), ["ort"] = m.Place.Length > 0 ? m.Place : "–", ["tagesordnung"] = m.Agenda.Length > 0 ? m.Agenda : "–",
        ["meeting_url"] = VereinMail.Origin(ctx) + VereinPublic.Base + "/sitzungen/" + m.Id
    };

    private static async Task<PluginResult> MeetingAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var isNew = ctx.Request.R("id") == "new";
        var m = isNew ? null : await store.MeetingAsync(Id(ctx), ct);
        if (!isNew && m is null) return PluginResult.NotFound();
        var board = VereinSettings.BoardList(ctx);

        if (ctx.Request.IsPost)
        {
            if (m is not null && ctx.Request.Action == "invite")
            {
                var people = VereinStore.Audience(await store.PeopleAsync(ct), m.Audience, board);
                var (s, f) = await VereinMail.SendAsync(ctx, VereinMail.Meeting, people, _ => MeetingValues(ctx, m), ct);
                await store.Meetings.UpdateAsync(m.Id, new Dictionary<string, object?> { ["invited_at"] = DateTime.UtcNow }, ct);
                return PluginResult.Redirect(Root + "meetings/" + m.Id, "Einladung: " + Sent(s, f), f > 0);
            }
            if (m is not null && ctx.Request.Action == "minutes")
            {
                var final = ctx.Request.F("minutes_status") == VereinStore.Final;
                await store.Meetings.UpdateAsync(m.Id, new Dictionary<string, object?>
                {
                    ["minutes"] = V.Clip(ctx.Request.F("minutes"), 60000), ["minutes_status"] = final ? VereinStore.Final : VereinStore.Draft,
                    ["status"] = m.Status == VereinStore.Planned && final ? VereinStore.Held : m.Status
                }, ct);
                var notice = final ? "Protokoll veröffentlicht – im Mitgliederbereich sichtbar." : "Protokoll als Entwurf gespeichert (nur im Admin sichtbar).";
                if (final && m.MinutesStatus != VereinStore.Final && V.Checked(ctx.Request.F("notify")))
                {
                    var people = VereinStore.Audience(await store.PeopleAsync(ct), m.Audience, board);
                    var (s, f) = await VereinMail.SendAsync(ctx, VereinMail.Minutes, people, _ => MeetingValues(ctx, m), ct);
                    notice += " " + Sent(s, f);
                }
                return PluginResult.Redirect(Root + "meetings/" + m.Id, notice);
            }
            if (m is not null && ctx.Request.Action == "delete")
            {
                await store.Rsvps.DeleteWhereAsync(DataQuery.Everything.Eq("meeting_id", m.Id), ct);
                await store.Meetings.DeleteAsync(m.Id, ct);
                return PluginResult.Redirect(Root + "meetings", "Sitzung gelöscht.");
            }
            var title = V.Clip(ctx.Request.F("title"), 160);
            var starts = V.CleanDate(ctx.Request.F("starts"));
            if (title.Length < 3 || starts.Length == 0) return PluginResult.Redirect(Root + "meetings/" + (isNew ? "new" : m!.Id), "Bitte Titel und Termin angeben.", true);
            var v = new Dictionary<string, object?>
            {
                ["title"] = title, ["kind"] = MeetingKinds.Contains(ctx.Request.F("kind")) ? ctx.Request.F("kind") : "", ["starts"] = starts, ["place"] = V.Clip(ctx.Request.F("place"), 200),
                ["agenda"] = V.Clip(ctx.Request.F("agenda"), 20000),
                ["audience"] = AudienceOptions(ctx).Any(o => o.Value == ctx.Request.F("audience")) ? ctx.Request.F("audience") : VereinStore.AudienceBoard,
                ["status"] = ctx.Request.F("status") is VereinStore.Held or VereinStore.Cancelled ? ctx.Request.F("status") : VereinStore.Planned
            };
            if (isNew) { v["created"] = DateTime.UtcNow; v["minutes"] = ""; v["minutes_status"] = VereinStore.Draft; var id = await store.Meetings.InsertAsync(v, ct); return PluginResult.Redirect(Root + "meetings/" + id, "Sitzung angelegt. Jetzt kann die Einladung raus."); }
            await store.Meetings.UpdateAsync(m!.Id, v, ct);
            return PluginResult.Redirect(Root + "meetings/" + m.Id, "Gespeichert.");
        }

        var agendaTpls = await store.TemplatesAsync(VereinStore.TplAgenda, ct);
        Template? tpl = long.TryParse(ctx.Request.Q("vorlage"), out var tid) ? await store.TemplateAsync(tid, ct) : null;
        var form = Ui.Form(
            Ui.Input("title", "Titel", m?.Title ?? (tpl is null ? "" : V.Fill(tpl.Title, ctx))),
            Ui.Grid(220,
                Ui.Select("kind", "Art", m?.Kind ?? MeetingKinds[0], MeetingKinds.Select(k => Ui.Option(k, k)).ToArray()),
                Ui.Input("starts", "Beginn", m?.Starts ?? "", UiInputType.DateTime),
                Ui.Input("place", "Ort", m?.Place ?? "")),
            Ui.Grid(220,
                Ui.Select("audience", "Eingeladen", m?.Audience ?? VereinStore.AudienceBoard, AudienceOptions(ctx)),
                Ui.Select("status", "Status", m?.Status ?? VereinStore.Planned, Ui.Option(VereinStore.Planned, "geplant"), Ui.Option(VereinStore.Held, "hat stattgefunden"), Ui.Option(VereinStore.Cancelled, "abgesagt"))),
            Ui.TextArea("agenda", "Tagesordnung", m?.Agenda ?? (tpl is null ? "" : V.Fill(tpl.Body, ctx)), 10),
            Ui.FormActions(Ui.Submit(isNew ? "Anlegen" : "Speichern"), Ui.Button("Zurück", Root + "meetings", UiButtonKind.Ghost)));
        if (isNew)
            return Page(Ui.Stack(Ui.PageHead("Danach: Einladung verschicken, Rückmeldungen sehen, Protokoll schreiben", "Neue Sitzung"),
                Ui.Columns(new UiNode?[] { Ui.Card(form) }, new UiNode?[] { Ui.Card("Tagesordnung aus Vorlage", agendaTpls.Count == 0 ? Ui.Muted("Keine Vorlagen.") :
                    Ui.Stack(agendaTpls.Select(t => (UiNode?)Ui.Button(t.Name, Root + "meetings/new?vorlage=" + t.Id, t.Id == tpl?.Id ? UiButtonKind.Primary : UiButtonKind.Ghost, small: true)).ToArray())) })), "Neue Sitzung");

        var rsvps = await store.RsvpsAsync(m!.Id, ct);
        var invited = VereinStore.Audience(await store.PeopleAsync(ct), m.Audience, board).ToList();
        var minutes = m.Minutes;
        if (minutes.Length == 0)
        {
            var mt = (await store.TemplatesAsync(VereinStore.TplMinutes, ct)).FirstOrDefault();
            if (mt is not null) minutes = V.Fill(mt.Body, ctx, MeetingValues(ctx, m));
        }
        var main = new List<UiNode?>
        {
            Ui.Card("Sitzung", form),
            Ui.Card("Protokoll", Ui.Form(
                Ui.TextArea("minutes", "Protokoll", minutes, 18) with { Help = m.Minutes.Length == 0 ? "Aus der Protokoll-Vorlage vorausgefüllt – noch nicht gespeichert." : null },
                Ui.Select("minutes_status", "Sichtbarkeit", m.MinutesStatus == VereinStore.Final ? VereinStore.Final : VereinStore.Draft,
                    Ui.Option(VereinStore.Draft, "Entwurf (nur im Admin)"), Ui.Option(VereinStore.Final, "Veröffentlicht (für die Eingeladenen im Mitgliederbereich)")),
                m.MinutesStatus == VereinStore.Final ? null : Ui.Checkbox("notify", "Beim Veröffentlichen die Eingeladenen per E-Mail benachrichtigen", true),
                Ui.FormActions(Ui.Submit("Protokoll speichern", name: "action", value: "minutes"))))
        };
        var side = new List<UiNode?>
        {
            Ui.Card("Einladung", Ui.KeyValue(("Eingeladen", invited.Count + " (" + AudienceLabel(m.Audience) + ")"), ("Verschickt", m.InvitedAt is null ? "noch nicht" : V.Stamp(m.InvitedAt))),
                m.Status == VereinStore.Planned ? Ui.Action(m.InvitedAt is null ? "Einladung senden" : "Einladung erneut senden", "invite", kind: UiButtonKind.Primary, icon: "ti-mail", confirm: "Einladung an " + invited.Count + " Person(en) schicken?") : null),
            Ui.Card("Rückmeldungen", Ui.Table(new[] { Ui.Column("Name"), Ui.Column("Antwort") }, invited.OrderBy(p => p.Last).Select(p =>
            {
                var r = rsvps.FirstOrDefault(x => x.MemberId == p.MemberId);
                return Ui.Row(p.Name, r is null ? Ui.Text("–") : r.Answer == VereinStore.Yes ? Ui.Badge("kommt", UiTone.Success) : r.Answer == VereinStore.No ? Ui.Badge("kommt nicht", UiTone.Error) : Ui.Badge("vielleicht", UiTone.Warning));
            }), emptyText: "Niemand eingeladen.")),
            Ui.Card("Löschen", Ui.Action("Sitzung löschen", "delete", kind: UiButtonKind.Danger, confirm: "Sitzung mit Rückmeldungen und Protokoll löschen?", icon: "ti-trash"))
        };
        return Page(Ui.Stack(Ui.PageHead(V.When(m.Starts) + (m.Place.Length > 0 ? " · " + m.Place : ""), m.Title, Ui.Button("Zurück", Root + "meetings", UiButtonKind.Ghost, small: true)),
            Ui.Columns(main, side)), m.Title);
    }

    // ---- events -------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> EventsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var past = ctx.Request.Q("vergangen") == "1";
        var all = await store.EventsAsync(ct);
        var list = past ? all.Where(e => string.CompareOrdinal(e.Day, V.Today) < 0).Reverse().ToList() : all.Where(e => string.CompareOrdinal((e.Ends.Length >= 10 ? e.Ends[..10] : e.Day), V.Today) >= 0).ToList();
        return Page(Ui.Stack(
            Ui.PageHead("Öffentliche Termine erscheinen im Block „Vereinstermine“ auf der Website, interne nur im Mitgliederbereich", "Termine", Ui.Button("Als Instagram-Bild", Root + "instagram", UiButtonKind.Ghost, "ti-brand-instagram"), Ui.Button("Neuer Termin", Root + "events/new", icon: "ti-calendar-plus")),
            Ui.Tabs(Ui.Tab("Kommende", Root + "events", !past), Ui.Tab("Vergangene", Root + "events?vergangen=1", past)),
            Ui.Table(new[] { Ui.Column("Wann"), Ui.Column("Termin"), Ui.Column("Art"), Ui.Column("Ort"), Ui.Column("Sichtbar") },
                list.Select(e => Ui.RowFor(e.Title + " " + e.Kind + " " + e.Place, V.When(e.Starts), Ui.Link(e.Title, Root + "events/" + e.Id), e.Kind, e.Place,
                    e.Cancelled ? Ui.Badge("abgesagt", UiTone.Error) : e.Public ? Ui.Badge("öffentlich", UiTone.Success) : Ui.Badge("intern"))),
                searchPlaceholder: "Termine durchsuchen …", emptyText: past ? "Keine vergangenen Termine." : "Keine kommenden Termine.")), "Termine");
    }

    private static async Task<PluginResult> EventAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var isNew = ctx.Request.R("id") == "new";
        var e = isNew ? null : await store.EventAsync(Id(ctx), ct);
        if (!isNew && e is null) return PluginResult.NotFound();

        if (ctx.Request.IsPost)
        {
            if (e is not null && ctx.Request.Action == "delete") { await store.Events.DeleteAsync(e.Id, ct); return PluginResult.Redirect(Root + "events", "Termin gelöscht."); }
            var title = V.Clip(ctx.Request.F("title"), 160);
            var starts = V.CleanDate(ctx.Request.F("starts"));
            if (title.Length < 2 || starts.Length == 0) return PluginResult.Redirect(Root + "events/" + (isNew ? "new" : e!.Id), "Bitte Titel und Beginn angeben.", true);
            var link = V.Clip(ctx.Request.F("link"), 500);
            if (link.Length > 0 && !(link.StartsWith("https://", StringComparison.Ordinal) || (link.StartsWith('/') && !link.StartsWith("//", StringComparison.Ordinal)))) link = "";
            var v = new Dictionary<string, object?>
            {
                ["title"] = title, ["kind"] = V.Clip(ctx.Request.F("kind"), 60), ["starts"] = starts, ["ends"] = V.CleanDate(ctx.Request.F("ends")), ["place"] = V.Clip(ctx.Request.F("place"), 200),
                ["description"] = V.Clip(ctx.Request.F("description"), 2000), ["public"] = V.Checked(ctx.Request.F("public")), ["link"] = link, ["cancelled"] = V.Checked(ctx.Request.F("cancelled"))
            };
            if (isNew) { v["created"] = DateTime.UtcNow; var id = await store.Events.InsertAsync(v, ct); return PluginResult.Redirect(Root + "events/" + id, "Termin angelegt."); }
            await store.Events.UpdateAsync(e!.Id, v, ct);
            return PluginResult.Redirect(Root + "events/" + e.Id, "Gespeichert.");
        }

        var form = Ui.Form(
            Ui.Input("title", "Titel", e?.Title ?? "") with { Help = "z. B. „Umzug in Remseck“" },
            Ui.Grid(220, Ui.Input("kind", "Art", e?.Kind ?? "") with { Help = "z. B. Umzug, Ball, Ausflug, Narrentreffen" }, Ui.Input("place", "Ort", e?.Place ?? "")),
            Ui.Grid(220, Ui.Input("starts", "Beginn", e?.Starts ?? "", UiInputType.DateTime), Ui.Input("ends", "Ende (freiwillig)", e?.Ends ?? "", UiInputType.DateTime)),
            Ui.TextArea("description", "Beschreibung (kurz)", e?.Description ?? "", 3),
            Ui.Input("link", "Link (freiwillig)", e?.Link ?? "") with { Help = "Eine Seite der Website (/…) oder eine https-Adresse, z. B. die Seite des Veranstalters." },
            Ui.Checkbox("public", "Öffentlich – auf der Website im Block „Vereinstermine“ zeigen", e?.Public ?? true),
            Ui.Checkbox("cancelled", "Abgesagt (bleibt durchgestrichen stehen)", e?.Cancelled ?? false),
            Ui.FormActions(Ui.Submit(isNew ? "Anlegen" : "Speichern"), Ui.Button("Zurück", Root + "events", UiButtonKind.Ghost)));
        return Page(Ui.Stack(Ui.PageHead(isNew ? "Umzug, Ball, Ausflug …" : V.When(e!.Starts), isNew ? "Neuer Termin" : e!.Title),
            Ui.Card(form), isNew ? null : Ui.Card("Teilen", Ui.Button("Als Instagram-Bild", Root + "instagram?vorlage=plakat&event=" + e!.Id, UiButtonKind.Ghost, "ti-brand-instagram")), isNew ? null : Ui.Card("Löschen", Ui.Action("Termin löschen", "delete", kind: UiButtonKind.Danger, confirm: "Termin löschen?", icon: "ti-trash"))), isNew ? "Neuer Termin" : e!.Title);
    }

    // ---- uploads ------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> UploadsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        if (ctx.Request.IsPost && long.TryParse(ctx.Request.F("id"), out var id) && await store.UploadAsync(id, ct) is Upload u)
        {
            if (ctx.Request.Action == "delete") { await ctx.Files.DeleteAsync(u.File, ct); await store.Uploads.DeleteAsync(u.Id, ct); return PluginResult.Redirect(Root + "uploads", "Gelöscht."); }
            if (ctx.Request.Action == "share") { await store.Uploads.UpdateAsync(u.Id, new Dictionary<string, object?> { ["shared"] = !u.Shared }, ct); return PluginResult.Redirect(Root + "uploads"); }
        }
        var list = await store.UploadsAsync(ct: ct);
        var people = (await store.PeopleAsync(ct)).ToDictionary(p => p.MemberId, p => p.Name);
        return Page(Ui.Stack(Ui.PageHead("Was Mitglieder im Mitgliederbereich hochgeladen haben", "Bilder & Material"),
            Ui.Table(new[] { Ui.Column(""), Ui.Column("Titel"), Ui.Column("Art"), Ui.Column("Von"), Ui.Column("Größe"), Ui.Column("Datum"), Ui.Column("Sichtbar"), Ui.Column("", actions: true) },
                list.Select(x => Ui.RowFor(x.Title + " " + x.FileName,
                    V.IsImage(Path.GetExtension(x.File).ToLowerInvariant()) ? Ui.Img(Root + "uploads/" + x.Id, x.Title, 48) : Ui.Text(""),
                    Ui.Link(x.Title.Length > 0 ? x.Title : x.FileName, Root + "uploads/" + x.Id, newTab: true), x.Kind == VereinStore.Photo ? "Bild" : "Material",
                    people.TryGetValue(x.MemberId, out var n) ? n : "–", V.Size(x.Size), V.Stamp(x.Created),
                    x.Shared ? Ui.Badge("alle Mitglieder", UiTone.Success) : Ui.Badge("privat"),
                    Ui.Inline(Ui.Action(x.Shared ? "Privat machen" : "Für alle", new Dictionary<string, string> { ["action"] = "share", ["id"] = x.Id.ToString(CultureInfo.InvariantCulture) }),
                        Ui.Action("Löschen", new Dictionary<string, string> { ["action"] = "delete", ["id"] = x.Id.ToString(CultureInfo.InvariantCulture) }, UiButtonKind.Danger, "Datei löschen?")))),
                searchPlaceholder: "Dateien durchsuchen …", pageSize: 50, emptyText: "Noch nichts hochgeladen.")), "Bilder & Material");
    }

    private static async Task<PluginResult> UploadFileAsync(IPluginContext ctx, CancellationToken ct)
    {
        var u = await new VereinStore(ctx.Data).UploadAsync(Id(ctx), ct);
        return u is null ? PluginResult.NotFound() : await VereinPublic.ServeAsync(ctx, u.File, u.FileName, u.FileType, ct);
    }

    // ---- templates ----------------------------------------------------------------------------------------------

    private static string TplKind(string k) => k switch { VereinStore.TplAgenda => "Tagesordnung", VereinStore.TplMinutes => "Protokoll", _ => "Dokument" };

    private static async Task<PluginResult> TemplatesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var list = await new VereinStore(ctx.Data).TemplatesAsync(ct: ct);
        return Page(Ui.Stack(Ui.PageHead("Ausgangstexte für Dokumente, Tagesordnungen und Protokolle", "Vorlagen", Ui.Button("Neue Vorlage", Root + "templates/new", icon: "ti-plus")),
            Ui.Table(new[] { Ui.Column("Name"), Ui.Column("Für") }, list.Select(t => Ui.RowFor(t.Name, Ui.Link(t.Name, Root + "templates/" + t.Id), TplKind(t.Kind))), emptyText: "Keine Vorlagen."),
            Ui.Muted("Platzhalter: {{verein}}, {{datum}}, {{jahr}} – im Protokoll außerdem {{titel}}, {{termin}}, {{ort}}, {{tagesordnung}}.")), "Vorlagen");
    }

    private static async Task<PluginResult> TemplateAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var isNew = ctx.Request.R("id") == "new";
        var t = isNew ? null : await store.TemplateAsync(Id(ctx), ct);
        if (!isNew && t is null) return PluginResult.NotFound();
        if (ctx.Request.IsPost)
        {
            if (t is not null && ctx.Request.Action == "delete") { await store.Templates.DeleteAsync(t.Id, ct); return PluginResult.Redirect(Root + "templates", "Vorlage gelöscht."); }
            var name = V.Clip(ctx.Request.F("name"), 120);
            if (name.Length < 2) return PluginResult.Redirect(Root + "templates/" + (isNew ? "new" : t!.Id), "Bitte einen Namen eingeben.", true);
            var kind = ctx.Request.F("kind") is VereinStore.TplAgenda or VereinStore.TplMinutes ? ctx.Request.F("kind") : VereinStore.TplDocument;
            var v = new Dictionary<string, object?> { ["name"] = name, ["kind"] = kind, ["title"] = V.Clip(ctx.Request.F("title"), 160), ["body"] = V.Clip(ctx.Request.F("body"), 30000) };
            if (isNew) { v["created"] = DateTime.UtcNow; var id = await store.Templates.InsertAsync(v, ct); return PluginResult.Redirect(Root + "templates/" + id, "Vorlage angelegt."); }
            await store.Templates.UpdateAsync(t!.Id, v, ct);
            return PluginResult.Redirect(Root + "templates/" + t.Id, "Gespeichert.");
        }
        var form = Ui.Form(
            Ui.Grid(220, Ui.Input("name", "Name der Vorlage", t?.Name ?? ""),
                Ui.Select("kind", "Für", t?.Kind ?? VereinStore.TplDocument, Ui.Option(VereinStore.TplDocument, "Dokument"), Ui.Option(VereinStore.TplAgenda, "Tagesordnung / Sitzung"), Ui.Option(VereinStore.TplMinutes, "Protokoll"))),
            Ui.Input("title", "Titel (Dokument bzw. Sitzung)", t?.Title ?? ""),
            Ui.TextArea("body", "Text", t?.Body ?? "", 18),
            Ui.FormActions(Ui.Submit(isNew ? "Anlegen" : "Speichern"), Ui.Button("Zurück", Root + "templates", UiButtonKind.Ghost)));
        return Page(Ui.Stack(Ui.PageHead("Platzhalter: {{verein}}, {{datum}}, {{jahr}}; im Protokoll {{titel}}, {{termin}}, {{ort}}, {{tagesordnung}}", isNew ? "Neue Vorlage" : t!.Name),
            Ui.Card(form), isNew ? null : Ui.Card("Löschen", Ui.Action("Vorlage löschen", "delete", kind: UiButtonKind.Danger, confirm: "Vorlage löschen?", icon: "ti-trash"))), isNew ? "Neue Vorlage" : t!.Name);
    }
}
