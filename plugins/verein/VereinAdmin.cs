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
            .Page("members/csv", null, MembersCsvAsync)
            .Page("members/{id}", null, MemberAsync)
            .Page("members/{id}/photo", null, PhotoAsync)
            .Page("children/{id}", null, ChildAsync)
            .Page("docs", null, DocsAsync)
            .Page("docs/{id}", null, DocAsync)
            .Page("docs/{id}/file", null, DocFileAsync)
            .Page("docs/{id}/csv", null, DocCsvAsync)
            .Page("docs/{id}/pdf", null, DocPdfAsync)
            .Page("meetings", null, MeetingsAsync)
            .Page("meetings/{id}", null, MeetingAsync)
            .Page("meetings/{id}/pdf", null, MeetingPdfAsync)
            .Page("events", null, EventsAsync)
            .Page("events/{id}", null, EventAsync)
            .Page("events/{id}/pdf", null, EventPdfAsync)
            .Page("events/{id}/csv", null, EventCsvAsync)
            .Page("instagram", null, VereinInstagram.PageAsync)
            .Page("uploads", null, UploadsAsync)
            .Page("uploads/{id}", null, UploadFileAsync)
            .Page("templates", null, TemplatesAsync)
            .Page("templates/{id}", null, TemplateAsync)
            .Link("Übersicht", "dashboard", "ti-layout-dashboard")
            .Link("Mitglieder", "members", "ti-users", also: new[] { "members/", "children/" })
            .Link("Dokumente", "docs", "ti-file-check", also: new[] { "docs/" })
            .Link("Sitzungen", "meetings", "ti-users-group", also: new[] { "meetings/" })
            .Link("Termine", "events", "ti-calendar-event", also: new[] { "events/" })
            .Link("Instagram-Bild", "instagram", "ti-brand-instagram")
            .Link("Bilder & Material", "uploads", "ti-photo", badge: true)
            .Link("Vorlagen", "templates", "ti-template", also: new[] { "templates/" })
            .Link("Einstellungen", "_settings", "ti-settings")
            .Badge(async (c, ct) => (await new VereinStore(c.Data).UploadsAsync(ct: ct)).Count(u => !u.Approved) is var n and > 0 ? n.ToString(CultureInfo.InvariantCulture) : null);
        b.Admin.Attention(async (c, ct) =>
        {
            var n = (await new VereinStore(c.Data).UploadsAsync(ct: ct)).Count(u => !u.Approved);
            return n == 0 ? Array.Empty<AttentionNote>() : new[] { new AttentionNote("Verein", n == 1 ? "1 Upload wartet auf Freigabe" : n + " Uploads warten auf Freigabe", Root + "uploads?status=offen") { Tone = UiTone.Warning } };
        });
    }

    /// <summary>The club's own fields as posted (x_0, x_1 … in the order of the settings).</summary>
    private static Dictionary<string, string> PostedFields(IPluginContext ctx) =>
        VereinSettings.Fields(ctx).Select((f, i) => (f.Label, Value: V.Clip(ctx.Request.F("x_" + i), 200))).Where(x => x.Value.Length > 0).ToDictionary(x => x.Label, x => x.Value);

    private static UiNode? FieldInputs(IPluginContext ctx, Person? p)
    {
        var fields = VereinSettings.Fields(ctx);
        return fields.Count == 0 ? null : Ui.Grid(220, fields.Select((f, i) => (UiNode?)(Ui.Input("x_" + i, f.Label, p is not null && p.Extra.TryGetValue(f.Label, out var v) ? v : "") with { Help = f.ForAll ? "sehen alle Mitglieder" : "sieht nur der Vorstand" })).ToArray());
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
        int OpenOf(Person p) => p.Child ? 0 : docs.Count(d => VereinStore.InAudience(p, d.Audience, board) && !answers.Any(a => a.DocId == d.Id && a.MemberId == p.MemberId));
        var byId = people.ToDictionary(p => p.Id);
        string Guardians(Person c) => string.Join(", ", c.Guardians.Select(g => byId.TryGetValue(g, out var x) ? x.Name : "").Where(n => n.Length > 0));

        var table = Ui.Table(new[] { Ui.Column("Name"), Ui.Column("Rollen"), Ui.Column("E-Mail / gehört zu"), Ui.Column("Telefon"), Ui.Column("Offen", right: true), Ui.Column("Status") },
            people.Select(p => Ui.RowFor(p.Name + " " + p.Email + " " + string.Join(" ", p.Roles) + (p.Child ? " kind " + Guardians(p) : ""),
                Ui.Inline(Ui.Link(p.Name.Length > 0 ? p.Name : p.Email, Root + (p.Child ? "children/" : "members/") + p.Id), p.Child ? Ui.Badge("Kind", UiTone.Info) : null),
                string.Join(", ", p.Roles), p.Child ? Guardians(p) : p.Email, p.Phone,
                p.Active ? OpenOf(p) : 0, p.Active ? Ui.Badge("aktiv", UiTone.Success) : Ui.Badge("inaktiv"))),
            searchPlaceholder: "Mitglieder durchsuchen …", pageSize: 50, emptyText: "Noch keine Mitglieder. Lege das erste an – es bekommt eine E-Mail mit dem Zugang.");
        var kids = people.Count(p => p.Child);
        return Page(Ui.Stack(Ui.PageHead(people.Count + " Mitglieder" + (kids > 0 ? ", davon " + kids + " Kinder" : ""), "Mitglieder",
            Ui.Button("Liste als CSV", Root + "members/csv", UiButtonKind.Ghost, "ti-file-spreadsheet"), Ui.Button("Kind eintragen", Root + "children/new", UiButtonKind.Ghost, "ti-mood-kid"), Ui.Button("Neues Mitglied", Root + "members/new", icon: "ti-user-plus")), table), "Mitglieder");
    }

    /// <summary>A child: name, birthday, roles, guardians (one or more grown-up members) — no mail, no account; documents and mails go to the guardians.</summary>
    private static async Task<PluginResult> ChildAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var isNew = ctx.Request.R("id") == "new";
        var c = isNew ? null : await store.PersonAsync(Id(ctx), ct);
        if (!isNew && (c is null || !c.Child)) return PluginResult.NotFound();
        var adults = (await store.PeopleAsync(ct)).Where(p => !p.Child).ToList();
        var roles = VereinSettings.RoleList(ctx);

        if (ctx.Request.IsPost)
        {
            if (c is not null && ctx.Request.Action is "photo" or "nophoto") return await PhotoPostAsync(ctx, store, c, Root + "children/" + c.Id, ct);
            if (c is not null && ctx.Request.Action == "remove")
            {
                if (c.Photo.Length > 0) await ctx.Files.DeleteAsync(c.Photo, ct);
                await store.People.DeleteAsync(c.Id, ct);
                return PluginResult.Redirect(Root + "members", c.Name + " ist entfernt.");
            }
            var first = V.Clip(ctx.Request.F("first"), 80);
            var last = V.Clip(ctx.Request.F("last"), 80);
            var guardians = adults.Where(a => V.Checked(ctx.Request.F("g_" + a.Id))).Select(a => a.Id).ToList();
            if (first.Length == 0) return PluginResult.Redirect(Root + "children/" + (isNew ? "new" : c!.Id), "Bitte einen Vornamen eingeben.", true);
            if (guardians.Count == 0) return PluginResult.Redirect(Root + "children/" + (isNew ? "new" : c!.Id), "Bitte mindestens ein Elternteil / eine erziehungsberechtigte Person auswählen.", true);
            var id = await store.SaveChildAsync(c?.Id ?? 0, first, last, V.CleanDate(ctx.Request.F("born")), roles.Where((r, i) => V.Checked(ctx.Request.F("role_" + i))),
                V.CleanDate(ctx.Request.F("joined")), isNew || V.Checked(ctx.Request.F("active")), V.Clip(ctx.Request.F("note"), 2000), guardians, ct);
            await store.SaveDetailsAsync(id, V.CleanDate(ctx.Request.F("born")), "", "", "", PostedFields(ctx), ct);
            return PluginResult.Redirect(Root + "children/" + id, isNew ? first + " ist eingetragen." : "Gespeichert.");
        }

        var preset = long.TryParse(ctx.Request.Q("eltern"), out var pg) ? pg : 0;
        var form = Ui.Form(
            Ui.Grid(240, Ui.Input("first", "Vorname", c?.First ?? ""), Ui.Input("last", "Nachname", c?.Last ?? (adults.FirstOrDefault(a => a.Id == preset)?.Last ?? ""))),
            Ui.Grid(240, Ui.Input("born", "Geburtsdatum (freiwillig)", c?.Born ?? "", UiInputType.Date), Ui.Input("joined", "Mitglied seit", c?.Joined ?? "", UiInputType.Date)),
            FieldInputs(ctx, c),
            Ui.Heading("Gehört zu", 3),
            adults.Count == 0 ? Ui.Alert("Erst ein erwachsenes Mitglied anlegen – ein Kind gehört immer zu jemandem.", UiTone.Warning)
                : Ui.Grid(240, adults.Select(a => (UiNode?)Ui.Checkbox("g_" + a.Id, a.Name, c?.Guardians.Contains(a.Id) ?? a.Id == preset)).ToArray()),
            Ui.Heading("Rollen", 3),
            Ui.Grid(220, roles.Select((r, i) => (UiNode?)Ui.Checkbox("role_" + i, r, c?.Has(r) ?? r == roles[^1])).ToArray()),
            Ui.TextArea("note", "Notiz (nur im Admin)", c?.Note ?? "", 3),
            isNew ? null : Ui.Checkbox("active", "Aktiv", c!.Active),
            Ui.FormActions(Ui.Submit(isNew ? "Eintragen" : "Speichern"), Ui.Button("Zurück", Root + "members", UiButtonKind.Ghost)));
        return Page(Ui.Stack(
            Ui.PageHead("Ein Kind hat keine eigene E-Mail und keinen Zugang – Dokumente und Nachrichten gehen an die Eltern", isNew ? "Kind eintragen" : c!.Name),
            Ui.Card(form),
            isNew ? null : PhotoCard(c!),
            isNew ? null : Ui.Card("Entfernen", Ui.Action("Aus dem Verein entfernen", "remove", kind: UiButtonKind.Danger, confirm: "Wirklich entfernen?", icon: "ti-trash"))), isNew ? "Kind eintragen" : c!.Name);
    }

    private static UiNode PhotoCard(Person p) => Ui.Card("Profilbild",
        p.Photo.Length > 0 ? Ui.Img(Root + "members/" + p.Id + "/photo?v=" + Path.GetFileNameWithoutExtension(p.Photo)[..8], p.Name, 120) : Ui.Muted("Noch kein Bild."),
        Ui.Form(Ui.File("photo", "Bild hochladen (JPG, PNG oder WebP)", "image/jpeg,image/png,image/webp"),
            Ui.FormActions(Ui.Submit("Hochladen", UiButtonKind.Ghost, "action", "photo"))) with { Upload = true },
        p.Photo.Length > 0 ? Ui.Action("Bild entfernen", "nophoto", kind: UiButtonKind.Ghost, icon: "ti-trash", confirm: "Profilbild entfernen?") : null);

    private static async Task<PluginResult> PhotoPostAsync(IPluginContext ctx, VereinStore store, Person p, string back, CancellationToken ct)
    {
        if (ctx.Request.Action == "nophoto") { await VereinPhoto.RemoveAsync(ctx, store, p, ct); return PluginResult.Redirect(back, "Profilbild entfernt."); }
        var error = await VereinPhoto.SaveAsync(ctx, store, p, ctx.Request.File("photo"), ct);
        return PluginResult.Redirect(back, error ?? "Profilbild gespeichert.", error is not null);
    }

    private static async Task<PluginResult> PhotoAsync(IPluginContext ctx, CancellationToken ct)
    {
        var p = await new VereinStore(ctx.Data).PersonAsync(Id(ctx), ct);
        return p is null || p.Photo.Length == 0 ? PluginResult.NotFound() : await VereinPhoto.ServeAsync(ctx, p, ct);
    }

    private static async Task<PluginResult> MemberAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var isNew = ctx.Request.R("id") == "new";
        var p = isNew ? null : await store.PersonAsync(Id(ctx), ct);
        if (!isNew && p is null) return PluginResult.NotFound();
        if (p is not null && p.Child) return PluginResult.Redirect(Root + "children/" + p.Id);
        var roles = VereinSettings.RoleList(ctx);

        if (ctx.Request.IsPost)
        {
            if (ctx.Request.Action == "invite" && p is not null)
            {
                // a fresh welcome link while the person has never set a password; a sign-in reminder once they have
                var again = await ctx.Members.InviteAsync(p.Email, p.Name, VereinPublic.Base, ct);
                var (s, f) = await SendInviteAsync(ctx, p, again.Link, ct);
                return PluginResult.Redirect(Root + "members/" + p.Id, f > 0 ? "Einladung konnte nicht verschickt werden (siehe Plugin-Protokoll)."
                    : again.Link is null ? "Erinnerung verschickt – das Mitglied hat schon ein Passwort und meldet sich damit an." : "Einladung mit neuem Link verschickt.", f > 0);
            }
            if (ctx.Request.Action == "setpw" && p is not null)
            {
                var ok = await ctx.Members.SetPasswordAsync(p.MemberId, ctx.Request.F("newpw"), ct);
                if (ok) ctx.Log($"Passwort für {p.Name} gesetzt von {ctx.User.Name}");
                return PluginResult.Redirect(Root + "members/" + p.Id, ok ? "Passwort gesetzt – bitte persönlich weitergeben. " + p.Name + " meldet sich damit unter " + VereinPublic.Base + " an." : "Das Passwort muss mindestens 8 Zeichen haben.", !ok);
            }
            if (ctx.Request.Action is "photo" or "nophoto" && p is not null) return await PhotoPostAsync(ctx, store, p, Root + "members/" + p.Id, ct);
            if (ctx.Request.Action == "remove" && p is not null)
            {
                if (p.Photo.Length > 0) await ctx.Files.DeleteAsync(p.Photo, ct);
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
                // The account comes as an INVITATION: no password yet and no confirmation mail of the site – our one mail carries the link that sets the
                // password, confirms the address and signs in (straight into /mitglied). A confirmed account that already exists is linked as it is.
                var invite = await ctx.Members.InviteAsync(email, (first + " " + last).Trim(), VereinPublic.Base, ct);
                if (invite.Status == MemberInviteStatus.InvalidEmail || invite.Account is null)
                    return PluginResult.Redirect(Root + "members/new", invite.Status == MemberInviteStatus.InvalidEmail ? "Die E-Mail-Adresse ist ungültig." : "Das Konto konnte nicht angelegt werden.", true);
                var account = invite.Account;
                if (await store.PersonOfMemberAsync(account.Id, ct) is Person existing)
                    return PluginResult.Redirect(Root + "members/" + existing.Id, "Zu dieser E-Mail-Adresse gibt es schon ein Mitglied.", true);
                var id = await store.SavePersonAsync(0, account.Id, first, last, email, phone, chosen, joined, true, note, ct);
                await store.SaveDetailsAsync(id, V.CleanDate(ctx.Request.F("born")), V.Clip(ctx.Request.F("street"), 120), V.Clip(ctx.Request.F("zip"), 10), V.Clip(ctx.Request.F("city"), 80), PostedFields(ctx), ct);
                var person = (await store.PersonAsync(id, ct))!;
                var notice = (first + " " + last).Trim() + " ist angelegt.";
                // a starting password from the board: only for an account the club just made (a confirmed account keeps its owner's password)
                var initial = ctx.Request.F("initpw");
                if (initial.Length > 0)
                {
                    if (invite.Status == MemberInviteStatus.EmailTaken) notice += " Das Konto hatte schon ein eigenes Passwort – es bleibt.";
                    else notice += await ctx.Members.SetPasswordAsync(account.Id, initial, ct) ? " Das Startpasswort ist gesetzt – bitte persönlich weitergeben, nicht per E-Mail." : " Das Startpasswort war zu kurz (mindestens 8 Zeichen) und wurde nicht gesetzt.";
                }
                if (V.Checked(ctx.Request.F("sendInvite")))
                {
                    var (_, f) = await SendInviteAsync(ctx, person, invite.Link, ct);
                    notice += f == 0 ? " Die Einladung ist verschickt." : " Die Einladung konnte nicht verschickt werden.";
                }
                ctx.Log($"Mitglied {person.Name} angelegt von {ctx.User.Name}");
                return PluginResult.Redirect(Root + "members/" + id, notice);
            }
            await store.SavePersonAsync(p!.Id, p.MemberId, first, last, p.Email, phone, chosen, joined, active, note, ct);
            await store.SaveDetailsAsync(p.Id, V.CleanDate(ctx.Request.F("born")), V.Clip(ctx.Request.F("street"), 120), V.Clip(ctx.Request.F("zip"), 10), V.Clip(ctx.Request.F("city"), 80), PostedFields(ctx), ct);
            return PluginResult.Redirect(Root + "members/" + p.Id, "Gespeichert.");
        }

        var form = Ui.Form(
            Ui.Grid(240, Ui.Input("first", "Vorname", p?.First ?? ""), Ui.Input("last", "Nachname", p?.Last ?? "")),
            isNew ? Ui.Input("email", "E-Mail-Adresse (Benutzername)", "", UiInputType.Email) : Ui.KeyValue(("E-Mail-Adresse", p!.Email)),
            Ui.Grid(240, Ui.Input("phone", "Telefon", p?.Phone ?? "", UiInputType.Tel), Ui.Input("born", "Geburtsdatum", p?.Born ?? "", UiInputType.Date), Ui.Input("joined", "Mitglied seit", p?.Joined ?? "", UiInputType.Date)),
            Ui.Grid(200, Ui.Input("street", "Straße und Hausnummer", p?.Street ?? ""), Ui.Input("zip", "PLZ", p?.Zip ?? ""), Ui.Input("city", "Ort", p?.City ?? "")),
            FieldInputs(ctx, p),
            Ui.Heading("Rollen", 3),
            Ui.Grid(220, roles.Select((r, i) => (UiNode?)Ui.Checkbox("role_" + i, r, p?.Has(r) ?? (isNew && r == roles[^1]))).ToArray()),
            Ui.TextArea("note", "Notiz (nur im Admin)", p?.Note ?? "", 3),
            isNew ? Ui.Input("initpw", "Startpasswort (freiwillig, mindestens 8 Zeichen)", "", UiInputType.Text) with { Help = "Damit kann sich das Mitglied sofort unter " + VereinPublic.Base + " anmelden. Persönlich weitergeben – es wird nie per E-Mail verschickt." } : null,
            isNew ? Ui.Checkbox("sendInvite", "Einladung mit Zugang per E-Mail schicken", true) : Ui.Checkbox("active", "Aktiv (Zugang zum Mitgliederbereich)", p!.Active),
            Ui.FormActions(Ui.Submit(isNew ? "Anlegen" : "Speichern"), Ui.Button("Zurück", Root + "members", UiButtonKind.Ghost)));

        if (isNew) return Page(Ui.Stack(Ui.PageHead("Ein Konto für den Mitgliederbereich wird angelegt", "Neues Mitglied"), Ui.Card(form)), "Neues Mitglied");

        var answers = await store.AnswersOfMemberAsync(p!.MemberId, ct);
        var docs = (await store.DocsAsync(false, ct)).Concat(await store.DocsAsync(true, ct)).ToDictionary(d => d.Id);
        var kids = VereinStore.ChildrenOf(await store.PeopleAsync(ct), p).ToList();
        var side = new List<UiNode?>
        {
            PhotoCard(p),
            Ui.Card("Kinder", kids.Count == 0 ? Ui.Muted("Keine eingetragen.") : Ui.Stack(kids.Select(k => (UiNode?)Ui.Link(k.Name + (k.Born.Length > 0 ? " (" + V.Date(k.Born) + ")" : ""), Root + "children/" + k.Id)).ToArray()),
                Ui.Button("Kind eintragen", Root + "children/new?eltern=" + p.Id, UiButtonKind.Ghost, "ti-mood-kid", small: true)),
            Ui.Card("Zugang",
                Ui.Muted("Die Einladung enthält einen Link: Passwort festlegen, danach ist das Mitglied direkt im Mitgliederbereich angemeldet. Hat es schon ein Passwort, kommt eine Erinnerung mit dem Link zur Anmeldung."),
                Ui.Inline(Ui.Action("Einladung erneut senden", "invite", kind: UiButtonKind.Ghost, icon: "ti-mail"))),
            Ui.Card("Passwort setzen", Ui.Form(
                Ui.Input("newpw", "Neues Passwort (mindestens 8 Zeichen)", "", UiInputType.Text) with { Help = "Z. B. ein Startpasswort. Persönlich weitergeben – das Mitglied kann es danach im Profil ändern." },
                Ui.FormActions(Ui.Submit("Passwort setzen", UiButtonKind.Ghost, "action", "setpw", confirm: "Passwort für dieses Mitglied neu setzen?")))),
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

    /// <summary>The one invitation mail: with a welcome link it says "set your password", without one (the account had a password already) "sign in".</summary>
    private static Task<(int, int)> SendInviteAsync(IPluginContext ctx, Person p, string? welcomeLink, CancellationToken ct) =>
        VereinMail.SendAsync(ctx, VereinMail.Invite, new[] { p }, _ => new Dictionary<string, string>
        {
            ["email"] = p.Email,
            ["link"] = welcomeLink ?? VereinMail.Origin(ctx) + VereinPublic.Base,
            ["schritt"] = welcomeLink is null
                ? "Du hast schon ein Konto auf unserer Website – melde dich hier einfach mit deinem bisherigen Passwort an:"
                : "Klick auf den Link und lege dein Passwort fest – danach bist du direkt angemeldet (der Link gilt 14 Tage):"
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
                Ui.Button("PDF / Drucken", Root + "docs/" + d.Id + "/pdf", UiButtonKind.Ghost, "ti-printer", small: true),
                Ui.Button("Nachweis als PDF", Root + "docs/" + d.Id + "/pdf?nachweis=1", UiButtonKind.Ghost, "ti-file-certificate", small: true),
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

    /// <summary>The document on the club's letterhead; <c>?nachweis=1</c> adds everybody's answers (the proof to file away).</summary>
    private static async Task<PluginResult> DocPdfAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var d = await store.DocAsync(Id(ctx), ct);
        if (d is null) return PluginResult.NotFound();
        List<(string, Answer?)>? proof = null;
        if (ctx.Request.Q("nachweis") == "1")
        {
            var people = await store.PeopleAsync(ct);
            var answers = (await store.AnswersOfDocAsync(d.Id, ct)).ToDictionary(a => a.MemberId);
            proof = VereinStore.Audience(people, d.Audience, VereinSettings.BoardList(ctx)).OrderBy(p => p.Last).ThenBy(p => p.First)
                .Select(p => (p.Name, answers.TryGetValue(p.MemberId, out var a) ? a : null)).ToList();
        }
        var pdf = VereinPrint.Document(await VereinPrint.LetterheadAsync(ctx, ct), d, proof: proof);
        return VereinPrint.Result(pdf, d.Title + (proof is null ? "" : " Nachweis"));
    }

    /// <summary>A meeting's invitation (agenda) or, with <c>?teil=protokoll</c>, its minutes — a draft too, marked as one.</summary>
    private static async Task<PluginResult> MeetingPdfAsync(IPluginContext ctx, CancellationToken ct)
    {
        var m = await new VereinStore(ctx.Data).MeetingAsync(Id(ctx), ct);
        if (m is null) return PluginResult.NotFound();
        var minutes = ctx.Request.Q("teil") == "protokoll";
        return VereinPrint.Result(VereinPrint.Meeting(await VereinPrint.LetterheadAsync(ctx, ct), m, minutes), (minutes ? "Protokoll " : "Einladung ") + m.Title);
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
                m.Status == VereinStore.Planned ? Ui.Action(m.InvitedAt is null ? "Einladung senden" : "Einladung erneut senden", "invite", kind: UiButtonKind.Primary, icon: "ti-mail", confirm: "Einladung an " + invited.Count + " Person(en) schicken?") : null,
                Ui.Inline(Ui.Button("Einladung als PDF", Root + "meetings/" + m.Id + "/pdf", UiButtonKind.Ghost, "ti-printer", small: true),
                    Ui.Button("Protokoll als PDF", Root + "meetings/" + m.Id + "/pdf?teil=protokoll", UiButtonKind.Ghost, "ti-printer", small: true))),
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
                    e.Draft ? Ui.Badge("Entwurf", UiTone.Warning) : e.Cancelled ? Ui.Badge("abgesagt", UiTone.Error) : e.Public ? Ui.Badge("öffentlich", UiTone.Success) : Ui.Badge("intern"))),
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
            if (e is not null && ctx.Request.Action == "delete")
            {
                await store.Replies.DeleteWhereAsync(DataQuery.Everything.Eq("event_id", e.Id), ct);
                await store.Events.DeleteAsync(e.Id, ct);
                return PluginResult.Redirect(Root + "events", "Termin gelöscht.");
            }
            if (e is not null && ctx.Request.Action == "ask")
            {
                if (!e.ReplyOpen(V.Today)) return PluginResult.Redirect(Root + "events/" + e.Id, "Für diesen Termin ist keine Teilnahme-Abfrage offen (abgefragt, veröffentlicht, nicht vorbei?).", true);
                var (s, f) = await SendAskAsync(ctx, store, e, ct);
                await store.Events.UpdateAsync(e.Id, new Dictionary<string, object?> { ["asked_at"] = DateTime.UtcNow }, ct);
                return PluginResult.Redirect(Root + "events/" + e.Id, "Teilnahme-Abfrage: " + Sent(s, f), f > 0);
            }
            if (e is not null && ctx.Request.Action == "setreply" && long.TryParse(ctx.Request.F("member"), out var who)
                && ctx.Request.F("answer") is VereinStore.Yes or VereinStore.No or VereinStore.Maybe or "")
            {
                if (ctx.Request.F("answer") == "") await store.Replies.DeleteWhereAsync(DataQuery.Everything.Eq("event_id", e.Id).Eq("member_id", who), ct);
                else await store.SetReplyAsync(e.Id, who, ctx.Request.F("answer"), "eingetragen vom Vorstand", 0, ct);
                return PluginResult.Redirect(Root + "events/" + e.Id, "Rückmeldung eingetragen.");
            }
            var title = V.Clip(ctx.Request.F("title"), 160);
            var starts = V.CleanDate(ctx.Request.F("starts"));
            if (title.Length < 2 || starts.Length == 0) return PluginResult.Redirect(Root + "events/" + (isNew ? "new" : e!.Id), "Bitte Titel und Beginn angeben.", true);
            var link = V.Clip(ctx.Request.F("link"), 500);
            if (link.Length > 0 && !(link.StartsWith("https://", StringComparison.Ordinal) || (link.StartsWith('/') && !link.StartsWith("//", StringComparison.Ordinal)))) link = "";
            var v = new Dictionary<string, object?>
            {
                ["title"] = title, ["kind"] = V.Clip(ctx.Request.F("kind"), 60), ["starts"] = starts, ["ends"] = V.CleanDate(ctx.Request.F("ends")), ["place"] = V.Clip(ctx.Request.F("place"), 200),
                ["description"] = V.Clip(ctx.Request.F("description"), 2000), ["public"] = V.Checked(ctx.Request.F("public")), ["link"] = link, ["cancelled"] = V.Checked(ctx.Request.F("cancelled")),
                // planning: a draft is seen only here; the running order and the practical notes are for members
                ["draft"] = ctx.Request.F("status") == "draft", ["meeting"] = V.Clip(ctx.Request.F("meeting"), 500), ["parking"] = V.Clip(ctx.Request.F("parking"), 1000),
                ["notes"] = V.Clip(ctx.Request.F("notes"), 3000), ["schedule"] = ReadPlan(ctx),
                // asking who takes part (a parade): replies in the member area, parents answer for their children
                ["ask"] = V.Checked(ctx.Request.F("ask")), ["ask_required"] = V.Checked(ctx.Request.F("ask_required")), ["ask_due"] = V.CleanDate(ctx.Request.F("ask_due")),
                ["ask_audience"] = AudienceOptions(ctx).Any(o => o.Value == ctx.Request.F("ask_audience")) ? ctx.Request.F("ask_audience") : VereinStore.AudienceAll
            };
            var notice = ctx.Request.F("status") == "draft" ? " Noch ein Entwurf – nur hier im Admin sichtbar." : "";
            if (isNew) { v["created"] = DateTime.UtcNow; var id = await store.Events.InsertAsync(v, ct); return PluginResult.Redirect(Root + "events/" + id, "Termin angelegt." + notice); }
            await store.Events.UpdateAsync(e!.Id, v, ct);
            return PluginResult.Redirect(Root + "events/" + e.Id, "Gespeichert." + notice);
        }

        var main = new UiNode?[]
        {
            Ui.Card("Termin",
                Ui.Input("title", "Titel", e?.Title ?? "") with { Help = "z. B. „Umzug in Remseck“" },
                Ui.Grid(220, Ui.Input("kind", "Art", e?.Kind ?? "") with { Help = "z. B. Umzug, Ball, Ausflug, Narrentreffen" }, Ui.Input("place", "Ort", e?.Place ?? "")),
                Ui.Grid(220, Ui.Input("starts", "Beginn", e?.Starts ?? "", UiInputType.DateTime), Ui.Input("ends", "Ende (freiwillig)", e?.Ends ?? "", UiInputType.DateTime)),
                Ui.TextArea("description", "Beschreibung (kurz, öffentlich)", e?.Description ?? "", 3),
                Ui.Input("link", "Link (freiwillig)", e?.Link ?? "") with { Help = "Eine Seite der Website (/…) oder eine https-Adresse, z. B. die Seite des Veranstalters." }),
            Ui.Card("Planung (für Mitglieder)",
                Ui.Muted("Nach und nach ergänzen – die Mitglieder sehen das im Mitgliederbereich beim Termin, auf der Website steht es nicht."),
                Ui.RowEditor("plan", "Ablauf", new[] { Ui.RowColumn("time", "Uhrzeit", placeholder: "13:00", width: "110px"), Ui.RowColumn("what", "Was", placeholder: "Aufstellung, Zunftmeisterempfang, Umzug …") },
                    (e?.Steps ?? Array.Empty<(string, string)>()).Select((s, i) => Ui.RowValue(i.ToString(CultureInfo.InvariantCulture), s.Time, s.What)), "+ Programmpunkt"),
                Ui.Input("meeting", "Treffpunkt / Aufstellung", e?.Meeting ?? "") with { Help = "z. B. „Aufstellung Bahnhofstraße, Startnummer 34“" },
                Ui.TextArea("parking", "Parkmöglichkeiten / Anfahrt", e?.Parking ?? "", 3),
                Ui.TextArea("notes", "Weitere Hinweise", e?.Notes ?? "", 3) with { Help = "z. B. Häs und Maske mitbringen, Busabfahrt, Verpflegung, Ansprechpartner." })
        };
        var side = new UiNode?[]
        {
            Ui.Card("Status",
                Ui.Select("status", "Stand", isNew || e!.Draft ? "draft" : "published", Ui.Option("draft", "Entwurf (nur im Admin)"), Ui.Option("published", "Veröffentlicht")),
                Ui.Checkbox("public", "Öffentlich – auf der Website im Block „Vereinstermine“", e?.Public ?? true),
                Ui.Checkbox("cancelled", "Abgesagt (bleibt durchgestrichen stehen)", e?.Cancelled ?? false),
                Ui.FormActions(Ui.Submit(isNew ? "Anlegen" : "Speichern"), Ui.Button("Zurück", Root + "events", UiButtonKind.Ghost))),
            Ui.Card("Teilnahme abfragen",
                Ui.Checkbox("ask", "Mitglieder sollen zu- oder absagen", e?.Ask ?? false),
                Ui.Checkbox("ask_required", "Rückmeldung ist Pflicht (z. B. Umzug)", e?.AskRequired ?? false),
                Ui.Select("ask_audience", "Wer wird gefragt?", e is { AskAudience.Length: > 0 } ? e.AskAudience : VereinStore.AudienceAll, AudienceOptions(ctx)),
                Ui.Input("ask_due", "Rückmeldung bis (freiwillig)", e?.AskDue ?? "", UiInputType.Date),
                Ui.Muted("Bei „Alle“ werden auch die Kinder gefragt – für sie antworten ihre Eltern. Die Abfrage erscheint im Mitgliederbereich beim Termin und unter „Zu erledigen“."))
        };
        var form = Ui.Form(Ui.Columns(main, side));
        var replyCard = e is { Ask: true } ? await RepliesCardAsync(ctx, store, e, ct) : null;
        return Page(Ui.Stack(Ui.PageHead(isNew ? "Umzug, Ball, Ausflug … – erst als Entwurf planen, dann veröffentlichen" : V.When(e!.Starts) + (e.Draft ? " · Entwurf" : ""), isNew ? "Neuer Termin" : e!.Title),
            form,
            replyCard,
            isNew || e!.Draft ? null : Ui.Card("Teilen", Ui.Button("Als Instagram-Bild", Root + "instagram?vorlage=plakat&event=" + e!.Id, UiButtonKind.Ghost, "ti-brand-instagram")),
            isNew ? null : Ui.Card("Löschen", Ui.Action("Termin löschen", "delete", kind: UiButtonKind.Danger, confirm: "Termin löschen?", icon: "ti-trash"))), isNew ? "Neuer Termin" : e!.Title);
    }

    /// <summary>Who replied how, with the board's own entries (a reply by phone) and the request by mail.</summary>
    private static async Task<UiNode> RepliesCardAsync(IPluginContext ctx, VereinStore store, Event e, CancellationToken ct)
    {
        var people = await store.PeopleAsync(ct);
        var byId = people.ToDictionary(p => p.MemberId);
        var asked = VereinStore.Asked(people, e, VereinSettings.BoardList(ctx)).ToList();
        var replies = (await store.RepliesAsync(e.Id, ct)).ToDictionary(r => r.MemberId);
        int Count(string a) => asked.Count(p => replies.TryGetValue(p.MemberId, out var r) && r.Answer == a);
        var open = asked.Count(p => !replies.ContainsKey(p.MemberId));
        var choices = new[] { Ui.Option("", "– offen –"), Ui.Option(VereinStore.Yes, "dabei"), Ui.Option(VereinStore.Maybe, "weiß noch nicht"), Ui.Option(VereinStore.No, "nicht dabei") };
        var table = Ui.Table(new[] { Ui.Column("Name"), Ui.Column("Antwort"), Ui.Column("Bemerkung"), Ui.Column("Wann"), Ui.Column("Eintragen", actions: true) },
            asked.OrderBy(p => replies.ContainsKey(p.MemberId)).ThenBy(p => p.Last).ThenBy(p => p.First).Select(p =>
            {
                replies.TryGetValue(p.MemberId, out var r);
                var by = r is null || r.By == 0 || r.By == p.MemberId ? "" : byId.TryGetValue(r.By, out var g) ? " (von " + g.First + ")" : "";
                return Ui.RowFor(p.Name, p.Name + (p.Child ? " (Kind)" : ""),
                    r is null ? Ui.Badge("offen", UiTone.Warning) : r.Answer == VereinStore.Yes ? Ui.Badge("dabei", UiTone.Success) : r.Answer == VereinStore.No ? Ui.Badge("nicht dabei", UiTone.Error) : Ui.Badge("weiß noch nicht"),
                    r?.Note ?? "", V.Stamp(r?.At) + by,
                    Ui.Form(Ui.Hidden("action", "setreply"), Ui.Hidden("member", p.MemberId.ToString(CultureInfo.InvariantCulture)), Ui.Select("answer", "", r?.Answer ?? "", choices), Ui.Submit("OK", UiButtonKind.Ghost, small: true)));
            }), searchPlaceholder: "Namen suchen …", emptyText: "Niemand wird gefragt.");
        return Ui.Card("Teilnahme: " + Count(VereinStore.Yes) + " dabei · " + Count(VereinStore.Maybe) + " unsicher · " + Count(VereinStore.No) + " nicht dabei · " + open + " offen",
            Ui.Inline(
                e.ReplyOpen(V.Today) ? Ui.Action(e.AskedAt is null ? "Abfrage per E-Mail senden" : "Offene erinnern", "ask", kind: UiButtonKind.Primary, icon: "ti-mail",
                    confirm: "E-Mail an alle schicken, die noch nicht geantwortet haben (für Kinder an die Eltern)?") : null,
                Ui.Button("Teilnehmerliste als PDF", Root + "events/" + e.Id + "/pdf", UiButtonKind.Ghost, "ti-printer", small: true),
                Ui.Button("Als CSV", Root + "events/" + e.Id + "/csv", UiButtonKind.Ghost, "ti-file-spreadsheet", small: true)),
            e.AskedAt is null ? null : Ui.Muted("Zuletzt per E-Mail gefragt: " + V.Stamp(e.AskedAt)),
            e.Draft ? Ui.Muted("Noch ein Entwurf – Mitglieder können erst antworten, wenn der Termin veröffentlicht ist.") : null,
            table);
    }

    /// <summary>Mails the request to everybody asked who has not answered; for a child, to its guardians (once per guardian, naming the children).</summary>
    private static async Task<(int, int)> SendAskAsync(IPluginContext ctx, VereinStore store, Event e, CancellationToken ct)
    {
        var people = await store.PeopleAsync(ct);
        var byId = people.ToDictionary(p => p.Id);
        var replied = (await store.RepliesAsync(e.Id, ct)).Select(r => r.MemberId).ToHashSet();
        var openPeople = VereinStore.Asked(people, e, VereinSettings.BoardList(ctx)).Where(p => !replied.Contains(p.MemberId)).ToList();
        var kidsFor = new Dictionary<long, List<string>>();
        var recipients = new Dictionary<long, Person>();
        foreach (var p in openPeople)
        {
            if (!p.Child) { recipients[p.Id] = p; continue; }
            foreach (var g in p.Guardians)
                if (byId.TryGetValue(g, out var parent) && parent.Active && !parent.Child)
                {
                    recipients[parent.Id] = parent;
                    (kidsFor.TryGetValue(parent.Id, out var l) ? l : kidsFor[parent.Id] = new List<string>()).Add(p.First.Length > 0 ? p.First : p.Name);
                }
        }
        return await VereinMail.SendAsync(ctx, VereinMail.EventAsk, recipients.Values, p => new Dictionary<string, string>
        {
            ["titel"] = e.Title, ["termin"] = V.When(e.Starts), ["ort"] = e.Place.Length > 0 ? ", " + e.Place : "", ["frist"] = e.AskDue.Length > 0 ? " bis zum " + V.Date(e.AskDue) : "",
            ["fuer"] = kidsFor.TryGetValue(p.Id, out var k) ? " (auch für " + string.Join(", ", k) + ")" : "",
            ["pflicht"] = e.AskRequired ? " Die Rückmeldung ist verpflichtend." : "",
            ["event_url"] = VereinMail.Origin(ctx) + VereinPublic.Base + "/termine/" + e.Id
        }, ct);
    }

    private static async Task<List<(Person P, EventReply? R)>> ReplyListAsync(IPluginContext ctx, VereinStore store, Event e, CancellationToken ct)
    {
        var replies = (await store.RepliesAsync(e.Id, ct)).ToDictionary(r => r.MemberId);
        return VereinStore.Asked(await store.PeopleAsync(ct), e, VereinSettings.BoardList(ctx))
            .OrderBy(p => replies.TryGetValue(p.MemberId, out var r) ? r.Answer switch { VereinStore.Yes => 0, VereinStore.Maybe => 1, _ => 3 } : 2).ThenBy(p => p.Last).ThenBy(p => p.First)
            .Select(p => (p, replies.TryGetValue(p.MemberId, out var r) ? r : null)).ToList();
    }

    /// <summary>The list of who takes part, on the club's letterhead — to take along on the day.</summary>
    private static async Task<PluginResult> EventPdfAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var e = await store.EventAsync(Id(ctx), ct);
        if (e is null) return PluginResult.NotFound();
        var list = await ReplyListAsync(ctx, store, e, ct);
        return VereinPrint.Result(VereinPrint.Participants(await VereinPrint.LetterheadAsync(ctx, ct), e, list), "Teilnehmer " + e.Title);
    }

    private static async Task<PluginResult> EventCsvAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var e = await store.EventAsync(Id(ctx), ct);
        if (e is null) return PluginResult.NotFound();
        static string C(string s) { s ??= ""; if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
        var sb = new StringBuilder("\uFEFF" + string.Join(";", new[] { "Nachname", "Vorname", "Kind", "Antwort", "Bemerkung", "Wann" }.Select(C)) + "\r\n");
        foreach (var (p, r) in await ReplyListAsync(ctx, store, e, ct))
            sb.Append(string.Join(";", new[] { p.Last, p.First, p.Child ? "ja" : "", r is null ? "offen" : VereinPublic.ReplyLabel(r.Answer), r?.Note ?? "", V.Stamp(r?.At) }.Select(C))).Append("\r\n");
        return PluginResult.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "teilnahme-" + VereinPrint.FileName(e.Title) + ".csv");
    }

    /// <summary>The running order as posted by the row editor (plan.count, plan.{i}.time, plan.{i}.what), stored as "time|what" lines.</summary>
    private static string ReadPlan(IPluginContext ctx)
    {
        if (!int.TryParse(ctx.Request.F("plan.count"), out var n)) return "";
        var lines = new List<string>();
        for (var i = 0; i < Math.Min(n, 40); i++)
        {
            var time = V.Clip(ctx.Request.F($"plan.{i}.time"), 20).Replace("|", " ").Replace("\n", " ");
            var what = V.Clip(ctx.Request.F($"plan.{i}.what"), 200).Replace("|", " ").Replace("\n", " ");
            if (time.Length + what.Length > 0) lines.Add(time + "|" + what);
        }
        return string.Join("\n", lines);
    }

    // ---- uploads ------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> UploadsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        if (ctx.Request.IsPost && ctx.Request.Action == "approveall")
        {
            var n = await store.Uploads.UpdateWhereAsync(DataQuery.Everything.Eq("approved", false), new Dictionary<string, object?> { ["approved"] = true }, ct);
            return PluginResult.Redirect(Root + "uploads", n + " freigegeben.");
        }
        if (ctx.Request.IsPost && long.TryParse(ctx.Request.F("id"), out var id) && await store.UploadAsync(id, ct) is Upload u)
        {
            if (ctx.Request.Action == "delete") { await ctx.Files.DeleteAsync(u.File, ct); await store.Uploads.DeleteAsync(u.Id, ct); return PluginResult.Redirect(Root + "uploads", "Gelöscht."); }
            if (ctx.Request.Action == "share") { await store.Uploads.UpdateAsync(u.Id, new Dictionary<string, object?> { ["shared"] = !u.Shared }, ct); return PluginResult.Redirect(Root + "uploads"); }
            if (ctx.Request.Action == "approve") { await store.Uploads.UpdateAsync(u.Id, new Dictionary<string, object?> { ["approved"] = true }, ct); return PluginResult.Redirect(Root + "uploads", "Freigegeben."); }
            if (ctx.Request.Action == "event" && long.TryParse(ctx.Request.F("event"), out var ev))
            { await store.Uploads.UpdateAsync(u.Id, new Dictionary<string, object?> { ["event_id"] = ev }, ct); return PluginResult.Redirect(Root + "uploads", "Termin geändert."); }
        }
        var pendingOnly = ctx.Request.Q("status") == "offen";
        var list = (await store.UploadsAsync(ct: ct)).Where(x => !pendingOnly || !x.Approved).ToList();
        var pendingCount = (await store.UploadsAsync(ct: ct)).Count(x => !x.Approved);
        var people = (await store.PeopleAsync(ct)).ToDictionary(p => p.MemberId, p => p.Name);
        var events = (await store.EventsAsync(ct)).Where(e => !e.Draft).OrderByDescending(e => e.Starts).Take(60).ToList();
        var eventOptions = new[] { Ui.Option("0", "– kein Termin –") }.Concat(events.Select(e => Ui.Option(e.Id.ToString(CultureInfo.InvariantCulture), V.Date(e.Starts) + " " + e.Title))).ToArray();
        string Id(Upload x) => x.Id.ToString(CultureInfo.InvariantCulture);
        return Page(Ui.Stack(
            Ui.PageHead("Was Mitglieder im Mitgliederbereich hochgeladen haben", "Bilder & Material",
                pendingCount > 0 ? Ui.Action("Alle " + pendingCount + " freigeben", new Dictionary<string, string> { ["action"] = "approveall" }, UiButtonKind.Primary, "Alle wartenden Uploads freigeben?", "ti-check") : null),
            Ui.Tabs(Ui.Tab("Alle", Root + "uploads", !pendingOnly), Ui.Tab("Warten auf Freigabe (" + pendingCount + ")", Root + "uploads?status=offen", pendingOnly)),
            Ui.Table(new[] { Ui.Column(""), Ui.Column("Titel"), Ui.Column("Von"), Ui.Column("Aufgenommen"), Ui.Column("Termin"), Ui.Column("Status"), Ui.Column("", actions: true) },
                list.Select(x => Ui.RowFor(x.Title + " " + x.FileName,
                    V.IsImage(Path.GetExtension(x.File).ToLowerInvariant()) ? Ui.Img(Root + "uploads/" + x.Id, x.Title, 48) : Ui.Text(""),
                    Ui.Link(x.Title.Length > 0 ? x.Title : x.FileName, Root + "uploads/" + x.Id, newTab: true),
                    people.TryGetValue(x.MemberId, out var n) ? n : "–", x.Taken.Length > 0 ? V.Date(x.Taken) : V.Stamp(x.Created),
                    Ui.Form(Ui.Hidden("action", "event"), Ui.Hidden("id", Id(x)), Ui.Select("event", "", x.EventId.ToString(CultureInfo.InvariantCulture), eventOptions), Ui.Submit("OK", UiButtonKind.Ghost, small: true)),
                    !x.Approved ? Ui.Badge("wartet auf Freigabe", UiTone.Warning) : x.Shared ? Ui.Badge("alle Mitglieder", UiTone.Success) : Ui.Badge("privat"),
                    Ui.Inline(
                        x.Approved ? null : Ui.Action("Freigeben", new Dictionary<string, string> { ["action"] = "approve", ["id"] = Id(x) }, UiButtonKind.Primary, icon: "ti-check"),
                        Ui.Action(x.Shared ? "Privat machen" : "Für alle", new Dictionary<string, string> { ["action"] = "share", ["id"] = Id(x) }),
                        Ui.Action("Löschen", new Dictionary<string, string> { ["action"] = "delete", ["id"] = Id(x) }, UiButtonKind.Danger, "Datei löschen?")))),
                searchPlaceholder: "Dateien durchsuchen …", pageSize: 50, emptyText: pendingOnly ? "Nichts wartet auf Freigabe." : "Noch nichts hochgeladen.")), "Bilder & Material");
    }

    /// <summary>The member list as a spreadsheet: every person (children too, with their parents), roles, contact, birthday, address and the own fields.</summary>
    private static async Task<PluginResult> MembersCsvAsync(IPluginContext ctx, CancellationToken ct)
    {
        var people = await new VereinStore(ctx.Data).PeopleAsync(ct);
        var byId = people.ToDictionary(p => p.Id);
        var fields = VereinSettings.Fields(ctx);
        static string C(string s) { s ??= ""; if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
        var sb = new StringBuilder("﻿" + string.Join(";", new[] { "Nachname", "Vorname", "Art", "Rollen", "E-Mail", "Telefon", "Geburtsdatum", "Straße", "PLZ", "Ort", "Mitglied seit", "Aktiv", "Gehört zu" }
            .Concat(fields.Select(f => f.Label)).Select(C)) + "\r\n");
        foreach (var p in people)
            sb.Append(string.Join(";", new[]
            {
                p.Last, p.First, p.Child ? "Kind" : "Mitglied", string.Join(", ", p.Roles), p.Email, p.Phone, V.Date(p.Born), p.Street, p.Zip, p.City, V.Date(p.Joined), p.Active ? "ja" : "nein",
                string.Join(", ", p.Guardians.Select(g => byId.TryGetValue(g, out var x) ? x.Name : ""))
            }.Concat(fields.Select(f => p.Extra.TryGetValue(f.Label, out var v) ? v : "")).Select(C))).Append("\r\n");
        return PluginResult.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "mitgliederliste-" + V.Today + ".csv");
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
