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
        b.Admin.Section("verein.admin.section", "ti-users-group", AdminGroup.Content, 25)
            .Page("dashboard", null, VereinDashboard.PageAsync)
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
            .Page("events/lookup", null, EventLookupAsync)
            .Page(VereinExport.Path, null, VereinExport.PageAsync)          // before events/{id}: "export" is no event number
            .Page(VereinExport.Path + "/pdf", null, VereinExport.PdfAsync)
            .Page(VereinExport.Path + "/csv", null, VereinExport.CsvAsync)
            .Page("events/{id}", null, EventAsync)
            .Page("events/{id}/pdf", null, EventPdfAsync)
            .Page("events/{id}/csv", null, EventCsvAsync)
            .Page("instagram", null, (ctx, ct) => Task.FromResult(VereinExport.FromInstagram(ctx)))
            .Page(VereinCalendar.AdminPath, null, VereinCalendar.AdminAsync)
            .Page("uploads", null, UploadsAsync)
            .Page("uploads/{id}", null, UploadFileAsync)
            .Page("templates", null, TemplatesAsync)
            .Page("templates/{id}", null, TemplateAsync)
            .Link("verein.admin.nav.overview", "dashboard", "ti-layout-dashboard")
            .Link("verein.admin.nav.members", "members", "ti-users", also: new[] { "members/", "children/" })
            .Link("verein.admin.nav.docs", "docs", "ti-file-check", also: new[] { "docs/" })
            .Link("verein.admin.nav.meetings", "meetings", "ti-users-group", also: new[] { "meetings/" })
            .Link("verein.admin.nav.events", "events", "ti-calendar-event", also: new[] { "events/" })
            .Link("verein.admin.nav.calendar", VereinCalendar.AdminPath, "ti-calendar-month")
            .Link("verein.admin.nav.uploads", "uploads", "ti-photo", badge: true)
            .Link("verein.admin.nav.templates", "templates", "ti-template", also: new[] { "templates/" })
            .Link("verein.admin.nav.settings", "_settings", "ti-settings")
            .Badge(async (c, ct) => (await new VereinStore(c.Data).UploadsAsync(ct: ct)).Count(u => !u.Approved) is var n and > 0 ? n.ToString(CultureInfo.InvariantCulture) : null);
        b.Admin.DashboardCard(VereinDashboard.CardAsync);
        b.Admin.Attention(async (c, ct) =>
        {
            var n = (await new VereinStore(c.Data).UploadsAsync(ct: ct)).Count(u => !u.Approved);
            return n == 0 ? Array.Empty<AttentionNote>() : new[] { new AttentionNote(c.T("verein.admin.section"), n == 1 ? c.T("verein.admin.attention.one") : c.T("verein.admin.attention.many", n), Root + "uploads?status=offen") { Tone = UiTone.Warning } };
        });
    }

    /// <summary>The club's own fields as posted (x_0, x_1 … in the order of the settings).</summary>
    private static Dictionary<string, string> PostedFields(IPluginContext ctx) =>
        VereinSettings.Fields(ctx).Select((f, i) => (f.Label, Value: V.Clip(ctx.Request.F("x_" + i), 200))).Where(x => x.Value.Length > 0).ToDictionary(x => x.Label, x => x.Value);

    private static UiNode? FieldInputs(IPluginContext ctx, Person? p)
    {
        var fields = VereinSettings.Fields(ctx);
        return fields.Count == 0 ? null : Ui.Grid(220, fields.Select((f, i) => (UiNode?)(Ui.Input("x_" + i, f.Label, p is not null && p.Extra.TryGetValue(f.Label, out var v) ? v : "") with { Help = f.ForAll ? ctx.T("verein.admin.field.forAll") : ctx.T("verein.admin.field.boardOnly") })).ToArray());
    }

    /// <summary>The roles field: chips (the club's roles offered with one click, a new one can be typed), posted comma-separated.</summary>
    private static UiNode RolesField(IPluginContext ctx, IEnumerable<string> current) =>
        Ui.Tags("roles", ctx.T("verein.admin.roles"), string.Join(", ", current), VereinSettings.RoleList(ctx).Where(r => !current.Contains(r, StringComparer.OrdinalIgnoreCase))) with { Help = ctx.T("verein.admin.rolesHelp") };

    private static List<string> PostedRoles(IPluginContext ctx) =>
        ctx.Request.F("roles").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => V.Clip(r.Replace("|", "/"), 60)).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();

    // An action button with its icon AND its words (the toolkit shows an icon alone unless told otherwise — a row of bare symbols says nothing).
    private static UiAction Act(string label, string action, string? id = null, UiButtonKind kind = UiButtonKind.Ghost, string? confirm = null, string? icon = null) =>
        Ui.Action(label, action, id, kind, confirm, icon) with { WithLabel = true };
    private static UiAction Act(string label, IReadOnlyDictionary<string, string> hidden, UiButtonKind kind = UiButtonKind.Ghost, string? confirm = null, string? icon = null) =>
        Ui.Action(label, hidden, kind, confirm, icon) with { WithLabel = true };

    private static long Id(IPluginContext ctx) => long.TryParse(ctx.Request.R("id"), out var id) ? id : 0;
    private static PluginResult Page(UiNode tree, string title) => PluginResult.Page(tree, title);

    private static UiOption[] AudienceOptions(IPluginContext ctx) =>
        new[] { Ui.Option(VereinStore.AudienceAll, ctx.T("verein.admin.aud.allMembers")), Ui.Option(VereinStore.AudienceActive, ctx.T("verein.admin.aud.activeOnly")), Ui.Option(VereinStore.AudienceBoard, ctx.T("verein.admin.aud.boardOnly")) }
            .Concat(VereinSettings.RoleList(ctx).Select(r => Ui.Option("role:" + r, ctx.T("verein.admin.roleX", r)))).ToArray();

    private static string AudienceLabel(IPluginContext ctx, string a) => a == VereinStore.AudienceAll ? ctx.T("verein.admin.aud.all") : a == VereinStore.AudienceActive ? ctx.T("verein.admin.aud.active") : a == VereinStore.AudienceBoard ? ctx.T("verein.admin.aud.board") : a.StartsWith("role:") ? a[5..] : a;

    private static string Sent(IPluginContext ctx, int sent, int failed) => failed == 0 ? ctx.T("verein.admin.sent", sent) : ctx.T("verein.admin.sentFailed", sent, failed);

    // ---- dashboard ----------------------------------------------------------------------------------------------

    // ---- members ------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> MembersAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var people = await store.PeopleAsync(ct);
        var board = VereinSettings.BoardList(ctx);
        var docs = await store.DocsAsync(false, ct);
        var answers = await store.AllAnswersAsync(ct);
        int OpenOf(Person p) => p.Child || !p.HasAccount ? 0 : docs.Count(d => VereinStore.InAudience(p, d.Audience, board) && !answers.Any(a => a.DocId == d.Id && a.MemberId == p.MemberId));
        var byId = people.ToDictionary(p => p.Id);
        string Guardians(Person c) => string.Join(", ", c.Guardians.Select(g => byId.TryGetValue(g, out var x) ? x.Name : "").Where(n => n.Length > 0));

        // Status as tabs (the list opens on the current members), role and the club's own fields (Figur …) as the toolbar's filter. The keys are
        // addresses (bookmarks, links) and stay: "aktiv" = everybody still in the club, "inaktiv" = the former members.
        var tabs = new (string Key, string Label, Func<Person, bool> Fits)[]
        {
            ("aktiv", ctx.T("verein.admin.tab.members"), p => p.Active),
            ("erwachsene", ctx.T("verein.admin.tab.adults"), p => p.Active && !p.Child),
            ("kinder", ctx.T("verein.admin.tab.children"), p => p.Active && p.Child),
            ("passiv", ctx.T("verein.admin.tab.passive"), p => p.Active && p.Passive),
            ("ohne-zugang", ctx.T("verein.admin.tab.noAccess"), p => p.Active && !p.Child && !p.HasAccount),
            ("inaktiv", ctx.T("verein.admin.tab.former"), p => !p.Active),
            ("alle", ctx.T("verein.admin.tab.all"), _ => true)
        };
        var chosen = tabs.FirstOrDefault(t => t.Key == ctx.Request.Q("status")) is { Key: not null } tt ? tt : tabs[0];
        var list = people.Where(chosen.Fits).ToList();

        var roles = VereinSettings.RoleList(ctx);
        var fields = VereinSettings.Fields(ctx);
        // a field becomes a filter when it has few distinct values (Figur: Hexe, Narr, Anwärter) – a number per person (Nr.) does not
        var filterFields = fields.Select((f, i) => (f, i, Values: people.Select(p => p.Extra.TryGetValue(f.Label, out var v) ? v.Trim() : "").Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList())).Where(x => x.Values.Count is > 0 and <= 12).ToList();
        // the first, empty option is "everybody" – without it the drop-down opens on its first role and hides all the others
        var options = new[] { ("", filterFields.Count > 0 ? ctx.T("verein.admin.filter.allRolesAnd", string.Join(", ", filterFields.Select(x => x.f.Label))) : ctx.T("verein.admin.filter.allRoles")) }
            .Concat(roles.Select((r, i) => ("r" + i, ctx.T("verein.admin.roleX", r))))
            .Concat(filterFields.SelectMany(x => x.Values.Select((v, j) => ($"f{x.i}-{j}", x.f.Label + ": " + v)))).ToArray();
        string Tags(Person p) => string.Join(" ", roles.Select((r, i) => p.Has(r) ? "r" + i : null)
            .Concat(filterFields.SelectMany(x => x.Values.Select((v, j) => p.Extra.TryGetValue(x.f.Label, out var pv) && string.Equals(pv.Trim(), v, StringComparison.OrdinalIgnoreCase) ? $"f{x.i}-{j}" : null)))
            .Where(t => t is not null));

        // At most seven columns: eleven ran out of the card at 1440 px. The address goes under the name, only the first own field is a column (the
        // others are searched and filtered, and stand on the person's page), the open confirmations join the status as a badge when there are any.
        var columns = new List<UiColumn> { Ui.Column(ctx.T("verein.admin.col.name")), Ui.Column(ctx.T("verein.admin.roles")) };
        columns.AddRange(fields.Take(1).Select(f => Ui.Column(f.Label)));
        columns.AddRange(new[] { Ui.Column(ctx.T("verein.admin.col.born")), NotOnPhone(Ui.Column(ctx.T("verein.admin.col.joined"))), Ui.Column(ctx.T("verein.admin.col.phoneOrGuardian")), Ui.Column(ctx.T("verein.admin.col.status")) });
        var table = Ui.Table(columns, list.Select(p =>
            {
                var mail = !p.Child && p.Email.Length > 0 && !VereinMail.Parkable(p.Email) ? p.Email : "";
                var cells = new List<object?>
                {
                    Ui.Stack(Ui.Inline(Ui.Link(p.Name.Length > 0 ? p.Name : p.Email, Root + (p.Child ? "children/" : "members/") + p.Id), p.Child ? Ui.Badge(ctx.T("verein.admin.badge.child"), UiTone.Info) : null, p.Passive ? Ui.Badge(ctx.T("verein.admin.badge.passive")) : null),
                        mail.Length > 0 ? Ui.Muted(mail) : null),
                    string.Join(", ", p.Roles)
                };
                cells.AddRange(fields.Take(1).Select(f => (object?)(p.Extra.TryGetValue(f.Label, out var v) ? v : "")));
                cells.Add(p.Born.Length > 0 ? V.Date(ctx, p.Born) + (Age(p.Born) is int a ? " (" + a + ")" : "") : "");
                cells.Add(p.Joined.Length > 0 ? V.Date(ctx, p.Joined) : "");
                cells.Add(p.Child ? Guardians(p) : p.Phone);
                var open = p.Active ? OpenOf(p) : 0;
                cells.Add(Ui.Inline(!p.Active ? Ui.Badge(InactiveLabel(ctx, p)) : p.Child ? Ui.Badge(ctx.T("verein.admin.badge.member"), UiTone.Success) : p.HasAccount ? Ui.Badge(ctx.T("verein.admin.badge.access"), UiTone.Success) : Ui.Badge(ctx.T("verein.admin.badge.noAccess"), UiTone.Warning),
                    open > 0 ? Ui.Badge(ctx.T("verein.admin.badge.open", open), UiTone.Warning) : null));
                return Ui.RowFor(p.Name + " " + p.Email + " " + p.Phone + " " + string.Join(" ", p.Roles) + " " + string.Join(" ", p.Extra.Values) + " " + p.City + (p.Child ? " kind " + ctx.T("verein.admin.badge.child") + " " + Guardians(p) : "") + (p.Passive ? " passiv " + ctx.T("verein.admin.badge.passive") : ""), cells.ToArray()) with { Tags = Tags(p) };
            }), searchPlaceholder: ctx.T("verein.admin.members.search"), pageSize: 50,
            emptyText: people.Count == 0 ? ctx.T("verein.admin.members.none") : ctx.T("verein.admin.members.noneHere")) with
        {
            Filter = options.Length > 1 ? Ui.ListFilter(ctx.T("verein.admin.role") + " / " + string.Join(" / ", filterFields.Select(x => x.f.Label)).TrimEnd(' ', '/'), options) : null,
            Below = new UiNode[] { Ui.Inline(Ui.Button(ctx.T("verein.admin.members.new"), Root + "members/new", icon: "ti-user-plus"), Ui.Button(ctx.T("verein.admin.members.addChild"), Root + "children/new", UiButtonKind.Ghost, "ti-mood-kid")) }
        };
        var activeCount = people.Count(p => p.Active);
        var kids = people.Count(p => p.Active && p.Child);
        var passive = people.Count(p => p.Active && p.Passive);
        var of = new[] { kids > 0 ? ctx.T("verein.admin.members.nKids", kids) : null, passive > 0 ? ctx.T("verein.admin.members.nPassive", passive) : null }.Where(x => x is not null).ToList();
        return Page(Ui.Stack(
            Ui.PageHead(ctx.T("verein.admin.members.count", activeCount) + (of.Count > 0 ? ctx.T("verein.admin.members.ofWhich", string.Join(", ", of)) : "") + (people.Count > activeCount ? " · " + ctx.T("verein.admin.members.nFormer", people.Count - activeCount) : ""), ctx.T("verein.admin.nav.members"),
                Ui.Button(ctx.T("verein.admin.members.csv"), Root + "members/csv", UiButtonKind.Ghost, "ti-file-spreadsheet")),
            Ui.Tabs(tabs.Select(t => Ui.Tab(t.Label + " (" + people.Count(t.Fits) + ")", Root + "members" + (t.Key == "aktiv" ? "" : "?status=" + t.Key), t.Key == chosen.Key)).ToArray()),
            table), ctx.T("verein.admin.nav.members"));
    }

    /// <summary>Age in whole years from a yyyy-MM-dd birthday, or null.</summary>
    internal static int? Age(string born)
    {
        if (born.Length < 10 || !DateTime.TryParseExact(born[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var b)) return null;
        var today = DateTime.ParseExact(V.Today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var age = today.Year - b.Year - (today.Month < b.Month || (today.Month == b.Month && today.Day < b.Day) ? 1 : 0);
        return age is >= 0 and < 130 ? age : null;
    }

    /// <summary>Why someone is a former member, as the board wrote it on the note's first line ("Ausgetreten", "Verstorben") — else "ehemalig".</summary>
    internal static string InactiveLabel(IPluginContext ctx, Person p) => p.Note.Split('\n')[0].Trim() is { Length: > 0 and <= 24 } first ? first : ctx.T("verein.admin.former");

    /// <summary>The membership type: active (takes part) or passive (supporting member). Not "aktiv" as in still a member — that is the "Ausgetreten" box.</summary>
    private static UiNode MembershipField(IPluginContext ctx, Person? p) =>
        Ui.Select("membership", ctx.T("verein.admin.membership"), p?.Passive == true ? "passive" : "active", Ui.Option("active", ctx.T("verein.admin.membership.active")), Ui.Option("passive", ctx.T("verein.admin.membership.passive")));

    private static bool PostedPassive(IPluginContext ctx) => ctx.Request.F("membership") == "passive";

    private static UiNode FormerBox(IPluginContext ctx, Person p) =>
        Ui.Checkbox("former", ctx.T("verein.admin.formerBox"), !p.Active) with { Help = ctx.T("verein.admin.formerBoxHelp") };

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
                return PluginResult.Redirect(Root + "members", ctx.T("verein.admin.removed", c.Name));
            }
            var first = V.Clip(ctx.Request.F("first"), 80);
            var last = V.Clip(ctx.Request.F("last"), 80);
            // guardians as names (chips); a name that is not a grown-up member of the club is ignored
            var wanted = ctx.Request.F("guardians").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var guardians = wanted.Select(n => adults.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase))?.Id ?? 0).Where(g => g > 0).Distinct().ToList();
            if (first.Length == 0) return PluginResult.Redirect(Root + "children/" + (isNew ? "new" : c!.Id), ctx.T("verein.admin.err.firstName"), true);
            if (guardians.Count == 0) return PluginResult.Redirect(Root + "children/" + (isNew ? "new" : c!.Id), ctx.T("verein.admin.err.guardian"), true);
            var id = await store.SaveChildAsync(c?.Id ?? 0, first, last, V.CleanDate(ctx.Request.F("born")), PostedRoles(ctx),
                V.CleanDate(ctx.Request.F("joined")), isNew || !V.Checked(ctx.Request.F("former")), V.Clip(ctx.Request.F("note"), 2000), guardians, ct);
            await store.SaveDetailsAsync(id, V.CleanDate(ctx.Request.F("born")), "", "", "", PostedFields(ctx), ct);
            await store.SetPassiveAsync(id, PostedPassive(ctx), ct);
            return PluginResult.Redirect(Root + "children/" + id, isNew ? ctx.T("verein.admin.childAdded", first) : ctx.T("verein.admin.saved"));
        }

        var preset = long.TryParse(ctx.Request.Q("eltern"), out var pg) ? pg : 0;
        var byId = adults.ToDictionary(a => a.Id);
        var currentGuardians = c is not null ? c.Guardians.Select(g => byId.TryGetValue(g, out var a) ? a.Name : "").Where(n => n.Length > 0)
            : adults.Where(a => a.Id == preset).Select(a => a.Name);
        var form = Ui.Form(
            Ui.Heading(ctx.T("verein.admin.badge.child"), 3),
            Ui.Grid(240, Ui.Input("first", ctx.T("verein.admin.f.first"), c?.First ?? "") with { Required = true }, Ui.Input("last", ctx.T("verein.admin.f.last"), c?.Last ?? (adults.FirstOrDefault(a => a.Id == preset)?.Last ?? ""))),
            Ui.Grid(240, Ui.Input("born", ctx.T("verein.admin.f.born"), c?.Born ?? "", UiInputType.Date), Ui.Input("joined", ctx.T("verein.admin.col.joined"), c?.Joined ?? "", UiInputType.Date)),
            Ui.Heading(ctx.T("verein.admin.child.belongsTo"), 3),
            adults.Count == 0 ? Ui.Alert(ctx.T("verein.admin.child.noAdults"), UiTone.Warning)
                : Ui.Tags("guardians", ctx.T("verein.admin.child.guardians"), string.Join(", ", currentGuardians), adults.Where(a => a.Active).Select(a => a.Name).OrderBy(n => n))
                    with { Help = ctx.T("verein.admin.child.guardiansHelp") },
            Ui.Heading(ctx.T("verein.admin.inClub"), 3),
            MembershipField(ctx, c),
            RolesField(ctx, c?.Roles ?? new[] { roles[^1] }),
            FieldInputs(ctx, c),
            Ui.TextArea("note", ctx.T("verein.admin.f.note"), c?.Note ?? "", 3),
            isNew ? null : FormerBox(ctx, c!),
            Ui.FormActions(Ui.Submit(isNew ? ctx.T("verein.admin.add") : ctx.T("verein.admin.save")), Ui.Button(ctx.T("verein.admin.back"), Root + "members?status=kinder", UiButtonKind.Ghost)));
        if (isNew)
            return Page(Ui.Card(form), ctx.T("verein.admin.members.addChild"));
        return Page(Ui.Stack(
            Ui.PageHead(ctx.T("verein.admin.badge.child") + " · " + (c!.Born.Length > 0 && Age(c.Born) is int age ? ctx.T("verein.admin.years", age) : ctx.T("verein.admin.child.noBirthday")), c.Name),
            Ui.Columns(new UiNode?[] { Ui.Card(form) }, new UiNode?[]
            {
                PhotoCard(ctx, c),
                Ui.Card(ctx.T("verein.admin.remove"), Act(ctx.T("verein.admin.removeFromClub"), "remove", kind: UiButtonKind.Danger, confirm: ctx.T("verein.admin.child.removeConfirm", c.Name), icon: "ti-trash"))
            })), c.Name);
    }

    /// <summary>An account's second factor — false on a host older than that API (its own frame, so a missing member only fails here).</summary>
    private static bool TwoFactorOf(MemberAccount a)
    {
        try { return TwoFactorFlag(a); } catch (MissingMethodException) { return false; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool TwoFactorFlag(MemberAccount a) => a.TwoFactorEnabled;

    private static UiNode PhotoCard(IPluginContext ctx, Person p) => Ui.Card(ctx.T("verein.admin.photo"),
        p.Photo.Length > 0 ? Ui.Img(Root + "members/" + p.Id + "/photo?v=" + Path.GetFileNameWithoutExtension(p.Photo)[..8], p.Name, 120) : Ui.Muted(ctx.T("verein.admin.photo.none")),
        Ui.Form(Ui.File("photo", ctx.T("verein.admin.photo.file"), "image/jpeg,image/png,image/webp"),
            Ui.FormActions(Ui.Submit(ctx.T("verein.admin.upload"), UiButtonKind.Ghost, "action", "photo"))) with { Upload = true },
        p.Photo.Length > 0 ? Act(ctx.T("verein.admin.photo.remove"), "nophoto", kind: UiButtonKind.Ghost, icon: "ti-trash", confirm: ctx.T("verein.admin.photo.removeConfirm")) : null);

    private static async Task<PluginResult> PhotoPostAsync(IPluginContext ctx, VereinStore store, Person p, string back, CancellationToken ct)
    {
        if (ctx.Request.Action == "nophoto") { await VereinPhoto.RemoveAsync(ctx, store, p, ct); return PluginResult.Redirect(back, ctx.T("verein.admin.photo.removed")); }
        var error = await VereinPhoto.SaveAsync(ctx, store, p, ctx.Request.File("photo"), ct);
        return PluginResult.Redirect(back, error ?? ctx.T("verein.admin.photo.saved"), error is not null);
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
            if (ctx.Request.Action == "access" && p is not null && !p.HasAccount) return await SetUpAccessAsync(ctx, store, p, ct);
            if (ctx.Request.Action == "invite" && p is not null && !p.HasAccount)
                return PluginResult.Redirect(Root + "members/" + p.Id, ctx.T("verein.admin.err.noAccessYet"), true);
            if (ctx.Request.Action == "invite" && p is not null)
            {
                // a fresh welcome link while the person has never set a password; a sign-in reminder once they have
                var again = await ctx.Members.InviteAsync(p.Email, p.Name, VereinPublic.Base, ct);
                var (s, f) = await SendInviteAsync(ctx, p, again.Link, ct);
                return PluginResult.Redirect(Root + "members/" + p.Id, f > 0 ? ctx.T("verein.admin.invite.failed")
                    : again.Link is null ? ctx.T("verein.admin.invite.reminded") : ctx.T("verein.admin.invite.resent"), f > 0);
            }
            if (ctx.Request.Action == "reset2fa" && p is not null && p.HasAccount)
            {
                await ctx.Members.ResetTwoFactorAsync(p.MemberId, ct);
                ctx.Log($"Zwei-Faktor-Anmeldung von {p.Name} ausgeschaltet von {ctx.User.Name}");
                return PluginResult.Redirect(Root + "members/" + p.Id, ctx.T("verein.admin.twofa.reset", p.Name));
            }
            if (ctx.Request.Action == "setpw" && p is not null && p.HasAccount)
            {
                var ok = await ctx.Members.SetPasswordAsync(p.MemberId, ctx.Request.F("newpw"), ct);
                if (ok) ctx.Log($"Passwort für {p.Name} gesetzt von {ctx.User.Name}");
                return PluginResult.Redirect(Root + "members/" + p.Id, ok ? ctx.T("verein.admin.pw.set", p.Name, VereinPublic.Base) : ctx.T("verein.admin.pw.tooShort"), !ok);
            }
            if (ctx.Request.Action is "photo" or "nophoto" && p is not null) return await PhotoPostAsync(ctx, store, p, Root + "members/" + p.Id, ct);
            if (ctx.Request.Action == "remove" && p is not null)
            {
                if (p.Photo.Length > 0) await ctx.Files.DeleteAsync(p.Photo, ct);
                await store.People.DeleteAsync(p.Id, ct);
                if (V.Checked(ctx.Request.F("deleteAccount")) && p.HasAccount) await ctx.Members.DeleteAsync(p.MemberId, ct);
                ctx.Log($"Mitglied {p.Name} entfernt von {ctx.User.Name}");
                return PluginResult.Redirect(Root + "members", ctx.T("verein.admin.removed", p.Name));
            }

            var first = V.Clip(ctx.Request.F("first"), 80);
            var last = V.Clip(ctx.Request.F("last"), 80);
            var email = V.Clip(ctx.Request.F("email"), 200).ToLowerInvariant();
            var chosen = PostedRoles(ctx);
            var phone = V.Clip(ctx.Request.F("phone"), 40);
            var joined = V.CleanDate(ctx.Request.F("joined"));
            var note = V.Clip(ctx.Request.F("note"), 2000);
            var active = isNew || !V.Checked(ctx.Request.F("former"));
            if (first.Length == 0 && last.Length == 0) return PluginResult.Redirect(Root + "members/" + (isNew ? "new" : p!.Id), ctx.T("verein.admin.err.name"), true);

            // a new member WITHOUT access (no account, no mail) — most of a club's people never sign in; access can be set up later
            if (isNew && !V.Checked(ctx.Request.F("access")))
            {
                var nid = await store.SavePersonAsync(0, VereinStore.Placeholder(), first, last, email, phone, chosen, joined, true, note, ct);
                await store.SetPassiveAsync(nid, PostedPassive(ctx), ct);
                await store.SaveDetailsAsync(nid, V.CleanDate(ctx.Request.F("born")), V.Clip(ctx.Request.F("street"), 120), V.Clip(ctx.Request.F("zip"), 10), V.Clip(ctx.Request.F("city"), 80), PostedFields(ctx), ct);
                ctx.Log($"Mitglied {(first + " " + last).Trim()} (ohne Zugang) angelegt von {ctx.User.Name}");
                return PluginResult.Redirect(Root + "members/" + nid, ctx.T("verein.admin.created.noAccess", (first + " " + last).Trim()));
            }
            if (isNew)
            {
                if (!email.Contains('@') || VereinMail.Parkable(email)) return PluginResult.Redirect(Root + "members/new", ctx.T("verein.admin.err.emailForAccess"), true);
                // The account comes as an INVITATION: no password yet and no confirmation mail of the site – our one mail carries the link that sets the
                // password, confirms the address and signs in (straight into /mitglied). A confirmed account that already exists is linked as it is.
                var invite = await ctx.Members.InviteAsync(email, (first + " " + last).Trim(), VereinPublic.Base, ct);
                if (invite.Status == MemberInviteStatus.InvalidEmail || invite.Account is null)
                    return PluginResult.Redirect(Root + "members/new", invite.Status == MemberInviteStatus.InvalidEmail ? ctx.T("verein.admin.err.emailInvalid") : ctx.T("verein.admin.err.accountFailed"), true);
                var account = invite.Account;
                if (await store.PersonOfMemberAsync(account.Id, ct) is Person existing)
                    return PluginResult.Redirect(Root + "members/" + existing.Id, ctx.T("verein.admin.err.emailExists"), true);
                var id = await store.SavePersonAsync(0, account.Id, first, last, email, phone, chosen, joined, true, note, ct);
                await store.SetPassiveAsync(id, PostedPassive(ctx), ct);
                await store.SaveDetailsAsync(id, V.CleanDate(ctx.Request.F("born")), V.Clip(ctx.Request.F("street"), 120), V.Clip(ctx.Request.F("zip"), 10), V.Clip(ctx.Request.F("city"), 80), PostedFields(ctx), ct);
                var person = (await store.PersonAsync(id, ct))!;
                var notice = ctx.T("verein.admin.created", (first + " " + last).Trim());
                // a starting password from the board: only for an account the club just made (a confirmed account keeps its owner's password)
                var initial = ctx.Request.F("initpw");
                if (initial.Length > 0)
                {
                    if (invite.Status == MemberInviteStatus.EmailTaken) notice += " " + ctx.T("verein.admin.pw.kept");
                    else notice += await ctx.Members.SetPasswordAsync(account.Id, initial, ct) ? " " + ctx.T("verein.admin.pw.initialSet") : " " + ctx.T("verein.admin.pw.initialTooShort");
                }
                if (V.Checked(ctx.Request.F("sendInvite")))
                {
                    var (_, f) = await SendInviteAsync(ctx, person, invite.Link, ct);
                    notice += " " + (f == 0 ? ctx.T("verein.admin.invite.sent") : ctx.T("verein.admin.invite.notSent"));
                }
                ctx.Log($"Mitglied {person.Name} angelegt von {ctx.User.Name}");
                return PluginResult.Redirect(Root + "members/" + id, notice);
            }
            // without an account the address is only the club's record (parked as xxx_… until access is set up) and may be edited here
            var keptEmail = p!.HasAccount ? p.Email : V.Clip(ctx.Request.F("email"), 160);
            await store.SavePersonAsync(p.Id, p.MemberId, first, last, keptEmail, phone, chosen, joined, active, note, ct);
            await store.SetPassiveAsync(p.Id, PostedPassive(ctx), ct);
            await store.SaveDetailsAsync(p.Id, V.CleanDate(ctx.Request.F("born")), V.Clip(ctx.Request.F("street"), 120), V.Clip(ctx.Request.F("zip"), 10), V.Clip(ctx.Request.F("city"), 80), PostedFields(ctx), ct);
            return PluginResult.Redirect(Root + "members/" + p.Id, ctx.T("verein.admin.saved"));
        }

        var form = Ui.Form(
            Ui.Heading(ctx.T("verein.admin.person"), 3),
            Ui.Grid(240, Ui.Input("first", ctx.T("verein.admin.f.first"), p?.First ?? ""), Ui.Input("last", ctx.T("verein.admin.f.last"), p?.Last ?? "")),
            Ui.Grid(240, Ui.Input("born", ctx.T("verein.admin.f.born"), p?.Born ?? "", UiInputType.Date), Ui.Input("joined", ctx.T("verein.admin.col.joined"), p?.Joined ?? "", UiInputType.Date)),
            Ui.Heading(ctx.T("verein.admin.contact"), 3),
            Ui.Grid(240,
                isNew ? Ui.Input("email", ctx.T("verein.admin.f.email"), "", UiInputType.Email) with { Help = ctx.T("verein.admin.f.emailHelpNew") }
                    : p!.HasAccount ? Ui.KeyValue((ctx.T("verein.admin.f.emailUser"), p.Email))
                    : Ui.Input("email", ctx.T("verein.admin.f.email"), p.Email) with { Help = ctx.T("verein.admin.f.emailHelpParked") },
                Ui.Input("phone", ctx.T("verein.admin.f.phone"), p?.Phone ?? "", UiInputType.Tel)),
            Ui.Grid(200, Ui.Input("street", ctx.T("verein.admin.f.street"), p?.Street ?? ""), Ui.Input("zip", ctx.T("verein.admin.f.zip"), p?.Zip ?? ""), Ui.Input("city", ctx.T("verein.admin.f.city"), p?.City ?? "")),
            Ui.Heading(ctx.T("verein.admin.inClub"), 3),
            MembershipField(ctx, p),
            RolesField(ctx, p?.Roles ?? new[] { roles[^1] }),
            FieldInputs(ctx, p),
            Ui.TextArea("note", ctx.T("verein.admin.f.note"), p?.Note ?? "", 3) with { Help = isNew ? null : ctx.T("verein.admin.f.noteHelp") },
            isNew ? Ui.Heading(ctx.T("verein.admin.access.heading"), 3) : null,
            isNew ? Ui.Checkbox("access", ctx.T("verein.admin.access.create", VereinPublic.Base), true) with { Help = ctx.T("verein.admin.access.createHelp") } : null,
            isNew ? Ui.Input("initpw", ctx.T("verein.admin.f.initpw"), "", UiInputType.Text) with { Help = ctx.T("verein.admin.f.initpwHelp") } : null,
            isNew ? Ui.Checkbox("sendInvite", ctx.T("verein.admin.f.sendInvite"), true) : FormerBox(ctx, p!),
            Ui.FormActions(Ui.Submit(isNew ? ctx.T("verein.admin.create") : ctx.T("verein.admin.save")), Ui.Button(ctx.T("verein.admin.back"), Root + "members", UiButtonKind.Ghost)));

        if (isNew) return Page(Ui.Card(form), ctx.T("verein.admin.members.new"));

        var answers = await store.AnswersOfMemberAsync(p!.MemberId, ct);
        var docs = (await store.DocsAsync(false, ct)).Concat(await store.DocsAsync(true, ct)).ToDictionary(d => d.Id);
        var kids = VereinStore.ChildrenOf(await store.PeopleAsync(ct), p).ToList();
        var siteAccount = p.HasAccount ? await ctx.Members.FindAsync(p.MemberId, ct) : null;
        var twoFactorOn = siteAccount is not null && TwoFactorOf(siteAccount);
        var side = new List<UiNode?>
        {
            PhotoCard(ctx, p),
            siteAccount is null ? null : Ui.Card(ctx.T("verein.admin.twofa"),
                twoFactorOn ? Ui.Badge(ctx.T("verein.admin.twofa.on"), UiTone.Success) : Ui.Badge(VereinSettings.TwoFactorRequired(ctx, p.Roles.Any(r => VereinSettings.BoardList(ctx).Contains(r, StringComparer.OrdinalIgnoreCase))) ? ctx.T("verein.admin.twofa.offRequired") : ctx.T("verein.admin.twofa.off"), UiTone.Warning),
                twoFactorOn ? Ui.Muted(ctx.T("verein.admin.twofa.lostPhone")) : Ui.Muted(ctx.T("verein.admin.twofa.onlyMember")),
                twoFactorOn ? Act(ctx.T("verein.admin.twofa.switchOff"), "reset2fa", kind: UiButtonKind.Danger, confirm: ctx.T("verein.admin.twofa.switchOffConfirm", p.Name), icon: "ti-shield-off") : null),
            Ui.Card(ctx.T("verein.admin.tab.children"), kids.Count == 0 ? Ui.Muted(ctx.T("verein.admin.noneEntered")) : Ui.Stack(kids.Select(k => (UiNode?)Ui.Link(k.Name + (k.Born.Length > 0 ? " (" + V.Date(ctx, k.Born) + ")" : ""), Root + "children/" + k.Id)).ToArray()),
                Ui.Button(ctx.T("verein.admin.members.addChild"), Root + "children/new?eltern=" + p.Id, UiButtonKind.Ghost, "ti-mood-kid", small: true)),
            p.HasAccount ? Ui.Card(ctx.T("verein.admin.badge.access"),
                Ui.Inline(Act(ctx.T("verein.admin.invite.again"), "invite", kind: UiButtonKind.Ghost, icon: "ti-mail")))
            : Ui.Card(ctx.T("verein.admin.access.setUp"), Ui.Form(
                Ui.Muted(VereinMail.Parkable(p.Email)
                    ? ctx.T("verein.admin.access.parked")
                    : ctx.T("verein.admin.access.with", p.Email)),
                Ui.Input("initpw", ctx.T("verein.admin.f.initpw"), "", UiInputType.Text) with { Help = ctx.T("verein.admin.f.min8") },
                Ui.Checkbox("sendInvite", ctx.T("verein.admin.f.sendInvite"), false),
                Ui.FormActions(VereinMail.Parkable(p.Email) ? null : Ui.Submit(ctx.T("verein.admin.access.setUp"), UiButtonKind.Primary, "action", "access", confirm: ctx.T("verein.admin.access.confirm", p.Email))))),
            p.HasAccount ? Ui.Card(ctx.T("verein.admin.pw.setTitle"), Ui.Form(
                Ui.Input("newpw", ctx.T("verein.admin.pw.new"), "", UiInputType.Text) with { Help = ctx.T("verein.admin.pw.newHelp") },
                Ui.FormActions(Ui.Submit(ctx.T("verein.admin.pw.setTitle"), UiButtonKind.Ghost, "action", "setpw", confirm: ctx.T("verein.admin.pw.confirm"))))) : null,
            Ui.Card(ctx.T("verein.admin.confirmations"), answers.Count == 0 ? Ui.Muted(ctx.T("verein.admin.noneYet")) : Ui.Table(new[] { Ui.Column(ctx.T("verein.admin.col.doc")), Ui.Column(ctx.T("verein.admin.col.answer")), Ui.Column(ctx.T("verein.admin.col.when")) },
                answers.OrderByDescending(a => a.At).Select(a => Ui.Row(docs.TryGetValue(a.DocId, out var d) ? Ui.Link(d.Title, Root + "docs/" + d.Id) : "#" + a.DocId,
                    a.Status == VereinStore.Accepted ? Ui.Badge(a.Signature.Length > 0 ? ctx.T("verein.admin.ans.signed") : ctx.T("verein.admin.ans.confirmed"), UiTone.Success) : Ui.Badge(ctx.T("verein.admin.ans.declined"), UiTone.Error), V.Stamp(ctx, a.At))))),
            Ui.Card(ctx.T("verein.admin.remove"), Ui.Form(
                Ui.Checkbox("deleteAccount", ctx.T("verein.admin.deleteAccount")),
                Ui.FormActions(Ui.Submit(ctx.T("verein.admin.removeFromClub"), UiButtonKind.Danger, "action", "remove", confirm: ctx.T("verein.admin.member.removeConfirm", p.Name)))))
        };
        return Page(Ui.Stack(Ui.PageHead(string.Join(", ", p.Roles), p.Name),
            Ui.Columns(new UiNode?[] { Ui.Card(form) }, side)), p.Name);
    }

    /// <summary>The one invitation mail: with a welcome link it says "set your password", without one (the account had a password already) "sign in".</summary>
    /// <summary>A member taken over without an account gets one: the club's address must be a real one (not parked as xxx_…); a mail only on request.</summary>
    private static async Task<PluginResult> SetUpAccessAsync(IPluginContext ctx, VereinStore store, Person p, CancellationToken ct)
    {
        var back = Root + "members/" + p.Id;
        if (VereinMail.Parkable(p.Email) || !p.Email.Contains('@')) return PluginResult.Redirect(back, ctx.T("verein.admin.err.realEmail"), true);
        var invite = await ctx.Members.InviteAsync(p.Email.Trim(), p.Name, VereinPublic.Base, ct);
        if (invite.Status == MemberInviteStatus.InvalidEmail || invite.Account is null) return PluginResult.Redirect(back, ctx.T("verein.admin.err.emailInvalid"), true);
        if (await store.PersonOfMemberAsync(invite.Account.Id, ct) is Person other && other.Id != p.Id)
            return PluginResult.Redirect(back, ctx.T("verein.admin.err.emailExistsName", other.Name), true);
        await store.People.UpdateAsync(p.Id, new Dictionary<string, object?> { ["member_id"] = invite.Account.Id, ["email"] = p.Email.Trim() }, ct);
        var person = (await store.PersonAsync(p.Id, ct))!;
        var notice = ctx.T("verein.admin.access.done");
        if (ctx.Request.F("initpw") is { Length: > 0 } pw && invite.Status != MemberInviteStatus.EmailTaken)
            notice += await ctx.Members.SetPasswordAsync(invite.Account.Id, pw, ct) ? " " + ctx.T("verein.admin.pw.initialSetShort") : " " + ctx.T("verein.admin.pw.initialTooShortShort");
        if (V.Checked(ctx.Request.F("sendInvite")))
        {
            var (_, f) = await SendInviteAsync(ctx, person, invite.Link, ct);
            notice += " " + (f == 0 ? ctx.T("verein.admin.invite.sent") : ctx.T("verein.admin.invite.notSent"));
        }
        ctx.Log($"Zugang für {person.Name} eingerichtet von {ctx.User.Name}");
        return PluginResult.Redirect(back, notice);
    }

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
            return Ui.RowFor(d.Title, Ui.Link(d.Title, Root + "docs/" + d.Id), Kind(ctx, d.Kind), AudienceLabel(ctx, d.Audience), d.Due.Length > 0 ? V.Date(ctx, d.Due) : "–",
                target.Count == 0 ? (UiNode)Ui.Muted("–") : Ui.Badge(done + " / " + target.Count, done >= target.Count ? UiTone.Success : UiTone.Warning), V.Stamp(ctx, d.Created));
        });
        return Page(Ui.Stack(
            Ui.Tabs(Ui.Tab(ctx.T("verein.admin.tab.current"), Root + "docs", !archived), Ui.Tab(ctx.T("verein.admin.tab.archive"), Root + "docs?archiv=1", archived)),
            Ui.Table(new[] { Ui.Column(ctx.T("verein.admin.col.title")), NotOnPhone(Ui.Column(ctx.T("verein.admin.col.kind"))), Ui.Column(ctx.T("verein.admin.col.for")), Ui.Column(ctx.T("verein.admin.col.due")), Ui.Column(ctx.T("verein.admin.col.answered")), NotOnPhone(Ui.Column(ctx.T("verein.admin.col.created"))) }, rows,
                searchPlaceholder: ctx.T("verein.admin.docs.search"), emptyText: archived ? ctx.T("verein.admin.archiveEmpty") : ctx.T("verein.admin.docs.none"))
                with { Below = new UiNode[] { Ui.Button(ctx.T("verein.admin.docs.new"), Root + "docs/new", icon: "ti-file-plus") } }), ctx.T("verein.admin.nav.docs"));
    }

    private static string Kind(IPluginContext ctx, string k) => k switch { VereinStore.Sign => ctx.T("verein.admin.kind.sign"), VereinStore.Read => ctx.T("verein.admin.kind.read"), _ => ctx.T("verein.admin.kind.confirm") };

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
                    return PluginResult.Redirect(Root + "docs/" + d.Id, ctx.T("verein.admin.reminderX", Sent(ctx, s, f)), f > 0);
                case "archive":
                    await store.Docs.UpdateAsync(d.Id, new Dictionary<string, object?> { ["archived"] = !d.Archived }, ct);
                    return PluginResult.Redirect(Root + "docs/" + d.Id, d.Archived ? ctx.T("verein.admin.activeAgain") : ctx.T("verein.admin.doc.archived"));
                case "delete":
                    if (answers.Count > 0) return PluginResult.Redirect(Root + "docs/" + d.Id, ctx.T("verein.admin.doc.noDelete"), true);
                    if (d.File.Length > 0) await ctx.Files.DeleteAsync(d.File, ct);
                    await store.Docs.DeleteAsync(d.Id, ct);
                    return PluginResult.Redirect(Root + "docs", ctx.T("verein.admin.deleted"));
            }
            return PluginResult.Redirect(Root + "docs/" + d.Id);
        }

        var done = target.Count(p => answers.ContainsKey(p.MemberId));
        var table = Ui.Table(new[] { Ui.Column(ctx.T("verein.admin.col.name")), Ui.Column(ctx.T("verein.admin.col.answer")), Ui.Column(ctx.T("verein.admin.col.signature")), Ui.Column(ctx.T("verein.admin.col.when")), Ui.Column(ctx.T("verein.admin.col.address")) },
            target.OrderBy(p => answers.ContainsKey(p.MemberId)).ThenBy(p => p.Last).Select(p =>
            {
                answers.TryGetValue(p.MemberId, out var a);
                return Ui.RowFor(p.Name, Ui.Link(p.Name, Root + "members/" + p.Id),
                    a is null ? Ui.Badge(ctx.T("verein.admin.ans.open"), UiTone.Warning) : a.Status == VereinStore.Accepted ? Ui.Badge(d.Kind == VereinStore.Read ? ctx.T("verein.admin.ans.read") : d.Kind == VereinStore.Sign ? ctx.T("verein.admin.ans.signed") : ctx.T("verein.admin.ans.confirmed"), UiTone.Success) : Ui.Badge(ctx.T("verein.admin.ans.declined"), UiTone.Error),
                    a?.Signature ?? "", V.Stamp(ctx, a?.At), a?.Ip ?? "");
            }), searchPlaceholder: ctx.T("verein.admin.searchNames"), emptyText: ctx.T("verein.admin.doc.nobody"));
        // Answers of people who are no longer in the audience (role changed, removed) stay visible as proof.
        var others = answers.Values.Where(a => !target.Any(p => p.MemberId == a.MemberId)).ToList();

        var main = new List<UiNode?>
        {
            Ui.Card(ctx.T("verein.admin.text"), Ui.Html("<div class=\"vm-text\">" + V.Paragraphs(d.Body) + "</div>"),
                d.File.Length > 0 ? Ui.Button(d.FileName.Length > 0 ? d.FileName : ctx.T("verein.admin.file"), Root + "docs/" + d.Id + "/file", UiButtonKind.Ghost, "ti-download", small: true) : null),
            Ui.Card(ctx.T("verein.admin.doc.answersOf", done, target.Count), table),
            others.Count > 0 ? Ui.Card(ctx.T("verein.admin.doc.otherAnswers"), Ui.Table(new[] { Ui.Column(ctx.T("verein.admin.badge.member")), Ui.Column(ctx.T("verein.admin.col.answer")), Ui.Column(ctx.T("verein.admin.col.when")) },
                others.Select(a => Ui.Row(people.FirstOrDefault(p => p.MemberId == a.MemberId)?.Name ?? ctx.T("verein.admin.deletedMember"), a.Status == VereinStore.Accepted ? ctx.T("verein.admin.yes") : ctx.T("verein.admin.ans.declined"), V.Stamp(ctx, a.At))))) : null
        };
        var side = new List<UiNode?>
        {
            Ui.Card(ctx.T("verein.admin.details"), Ui.KeyValue((ctx.T("verein.admin.col.kind"), Kind(ctx, d.Kind)), (ctx.T("verein.admin.col.for"), AudienceLabel(ctx, d.Audience)), (ctx.T("verein.admin.col.due"), d.Due.Length > 0 ? V.Date(ctx, d.Due) : "–"),
                (ctx.T("verein.admin.col.created"), V.Stamp(ctx, d.Created) + (d.CreatedBy.Length > 0 ? " " + ctx.T("verein.admin.byX", d.CreatedBy) : "")), (ctx.T("verein.admin.col.status"), d.Archived ? ctx.T("verein.admin.tab.archive") : ctx.T("verein.admin.statusActive")))),
            Ui.Card(ctx.T("verein.admin.actions"), Ui.Inline(
                d.Archived || target.Count == done ? null : Act(ctx.T("verein.admin.doc.remindOpen", target.Count - done), "remind", kind: UiButtonKind.Primary, icon: "ti-mail", confirm: ctx.T("verein.admin.doc.remindConfirm")),
                Ui.Button(ctx.T("verein.admin.pdfPrint"), Root + "docs/" + d.Id + "/pdf", UiButtonKind.Ghost, "ti-printer", small: true),
                Ui.Button(ctx.T("verein.admin.doc.proofPdf"), Root + "docs/" + d.Id + "/pdf?nachweis=1", UiButtonKind.Ghost, "ti-file-certificate", small: true),
                Ui.Button(ctx.T("verein.admin.members.csv"), Root + "docs/" + d.Id + "/csv", UiButtonKind.Ghost, "ti-file-spreadsheet", small: true),
                Act(d.Archived ? ctx.T("verein.admin.reactivate") : ctx.T("verein.admin.archive"), "archive", icon: "ti-archive"),
                answers.Count == 0 ? Act(ctx.T("verein.admin.delete"), "delete", kind: UiButtonKind.Danger, confirm: ctx.T("verein.admin.doc.deleteConfirm"), icon: "ti-trash") : null))
        };
        return Page(Ui.Columns(main, side), d.Title);   // kind and audience stand in "Angaben"
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
            if (title.Length < 3) return PluginResult.Redirect(Root + "docs/new", ctx.T("verein.admin.err.title"), true);
            var upload = ctx.Request.File("file");
            string file = "", fileName = "", fileType = "";
            if (upload is not null && upload.Length > 0)
            {
                var ext = V.AllowedExt(upload.FileName);
                if (ext.Length == 0) return PluginResult.Redirect(Root + "docs/new", ctx.T("verein.admin.err.fileType"), true);
                if (upload.Length > 25 * 1024 * 1024) return PluginResult.Redirect(Root + "docs/new", ctx.T("verein.admin.err.file25"), true);
                file = "d/" + Guid.NewGuid().ToString("N") + ext; fileName = V.SafeName(upload.FileName); fileType = V.Types[ext];
                using var s = upload.OpenRead();
                await ctx.Files.SaveAsync(file, s, ct);
            }
            var id = await store.InsertDocAsync(title, body, kind, audience, V.CleanDate(ctx.Request.F("due")), ctx.User.Name, ct);
            if (file.Length > 0) await store.Docs.UpdateAsync(id, new Dictionary<string, object?> { ["file"] = file, ["file_name"] = fileName, ["file_type"] = fileType }, ct);
            var d = (await store.DocAsync(id, ct))!;
            var notice = ctx.T("verein.admin.doc.published");
            if (V.Checked(ctx.Request.F("notify")))
            {
                var people = VereinStore.Audience(await store.PeopleAsync(ct), d.Audience, VereinSettings.BoardList(ctx));
                var (s, f) = await VereinMail.SendAsync(ctx, VereinMail.Document, people, _ => DocValues(ctx, d), ct);
                notice += " " + Sent(ctx, s, f);
            }
            ctx.Log($"Dokument #{id} „{title}“ angelegt von {ctx.User.Name}");
            return PluginResult.Redirect(Root + "docs/" + id, notice);
        }

        var templates = await store.TemplatesAsync(VereinStore.TplDocument, ct);
        Template? tpl = long.TryParse(ctx.Request.Q("vorlage"), out var tid) ? await store.TemplateAsync(tid, ct) : null;
        var form = Ui.Form(
            Ui.Input("title", ctx.T("verein.admin.col.title"), tpl is null ? "" : V.Fill(tpl.Title, ctx)),
            Ui.TextArea("body", ctx.T("verein.admin.text"), tpl is null ? "" : V.Fill(tpl.Body, ctx), 14) with { Help = ctx.T("verein.admin.paragraphHelp") },
            Ui.Grid(220,
                Ui.Select("kind", ctx.T("verein.admin.doc.whatToDo"), VereinStore.Confirm, Ui.Option(VereinStore.Confirm, ctx.T("verein.admin.doc.kindConfirm")), Ui.Option(VereinStore.Sign, ctx.T("verein.admin.doc.kindSign")), Ui.Option(VereinStore.Read, ctx.T("verein.admin.doc.kindRead"))),
                Ui.Select("audience", ctx.T("verein.admin.forWhom"), VereinStore.AudienceAll, AudienceOptions(ctx)),
                Ui.Input("due", ctx.T("verein.admin.f.dueOptional"), "", UiInputType.Date)),
            Ui.File("file", ctx.T("verein.admin.f.attach")),
            Ui.Checkbox("notify", ctx.T("verein.admin.f.notifyAll"), true),
            Ui.FormActions(Ui.Submit(ctx.T("verein.admin.doc.publish")), Ui.Button(ctx.T("verein.admin.cancel"), Root + "docs", UiButtonKind.Ghost))) with { Upload = true };
        var side = Ui.Card(ctx.T("verein.admin.tpl.use"), templates.Count == 0 ? Ui.Muted(ctx.T("verein.admin.tpl.none")) : Ui.Stack(templates.Select(t => (UiNode?)Ui.Button(t.Name, Root + "docs/new?vorlage=" + t.Id, t.Id == tpl?.Id ? UiButtonKind.Primary : UiButtonKind.Ghost, small: true)).ToArray()),
            Ui.Link(ctx.T("verein.admin.tpl.edit"), Root + "templates"));
        return Page(Ui.Columns(new UiNode?[] { Ui.Card(form) }, new UiNode?[] { side }), ctx.T("verein.admin.docs.new"));
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
        return VereinPrint.Result(pdf, d.Title + (proof is null ? "" : " " + ctx.T("verein.admin.file.proof")));
    }

    /// <summary>A meeting's invitation (agenda) or, with <c>?teil=protokoll</c>, its minutes — a draft too, marked as one.</summary>
    private static async Task<PluginResult> MeetingPdfAsync(IPluginContext ctx, CancellationToken ct)
    {
        var m = await new VereinStore(ctx.Data).MeetingAsync(Id(ctx), ct);
        if (m is null) return PluginResult.NotFound();
        var minutes = ctx.Request.Q("teil") == "protokoll";
        return VereinPrint.Result(VereinPrint.Meeting(await VereinPrint.LetterheadAsync(ctx, ct), m, minutes), (minutes ? ctx.T("verein.admin.file.minutes") : ctx.T("verein.admin.file.invitation")) + " " + m.Title);
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
        var sb = new StringBuilder("﻿" + ctx.T("verein.admin.csv.docHeader") + "\r\n");
        foreach (var p in VereinStore.Audience(people, d.Audience, VereinSettings.BoardList(ctx)))
        {
            answers.TryGetValue(p.MemberId, out var a);
            sb.Append(string.Join(";", C(p.Name), C(p.Email), C(a is null ? ctx.T("verein.admin.ans.open") : a.Status == VereinStore.Accepted ? ctx.T("verein.admin.yes") : ctx.T("verein.admin.ans.declined")), C(a?.Signature ?? ""), C(V.Stamp(ctx, a?.At)), C(a?.Ip ?? ""))).Append("\r\n");
        }
        return PluginResult.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", V.SafeName(d.Title) + ".csv");
    }

    // ---- meetings -----------------------------------------------------------------------------------------------

    private static readonly string[] MeetingKinds = { "Vorstandssitzung", "Mitgliederversammlung", "Jahreshauptversammlung", "Besprechung", "Sonstiges" };

    /// <summary>A meeting's kind is STORED as the German word; only its label follows the admin's language (an unknown kind shows as stored).</summary>
    private static string MeetingKindLabel(IPluginContext ctx, string kind) => Array.IndexOf(MeetingKinds, kind) switch
    {
        0 => ctx.T("verein.admin.mkind.board"), 1 => ctx.T("verein.admin.mkind.members"), 2 => ctx.T("verein.admin.mkind.annual"),
        3 => ctx.T("verein.admin.mkind.talk"), 4 => ctx.T("verein.admin.mkind.other"), _ => kind
    };

    private static async Task<PluginResult> MeetingsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var list = await store.MeetingsAsync(ct);
        var rows = new List<UiTableRow>();
        foreach (var m in list)
        {
            var r = await store.RsvpsAsync(m.Id, ct);
            rows.Add(Ui.RowFor(m.Title + " " + m.Kind + " " + MeetingKindLabel(ctx, m.Kind), Ui.Link(m.Title, Root + "meetings/" + m.Id), V.When(ctx, m.Starts), MeetingKindLabel(ctx, m.Kind), AudienceLabel(ctx, m.Audience),
                r.Count == 0 ? (UiNode)Ui.Muted("–") : Ui.Badge(ctx.T("verein.admin.rsvp.counts", r.Count(x => x.Answer == VereinStore.Yes), r.Count(x => x.Answer == VereinStore.No)), UiTone.Info),
                m.Status switch { VereinStore.Cancelled => Ui.Badge(ctx.T("verein.admin.mstatus.cancelled"), UiTone.Error), VereinStore.Held => Ui.Badge(ctx.T("verein.admin.mstatus.held")), _ => Ui.Badge(ctx.T("verein.admin.mstatus.planned"), UiTone.Info) },
                m.MinutesStatus == VereinStore.Final ? Ui.Badge(ctx.T("verein.admin.minutes.published"), UiTone.Success) : m.Minutes.Length > 0 ? Ui.Badge(ctx.T("verein.admin.minutes.draft"), UiTone.Warning) : Ui.Text("–")));
        }
        return Page(Ui.Stack(
            Ui.Table(new[] { Ui.Column(ctx.T("verein.admin.col.meeting")), Ui.Column(ctx.T("verein.admin.col.when")), NotOnPhone(Ui.Column(ctx.T("verein.admin.col.kind"))), NotOnPhone(Ui.Column(ctx.T("verein.admin.col.for"))), Ui.Column(ctx.T("verein.admin.col.replies")), Ui.Column(ctx.T("verein.admin.col.status")), Ui.Column(ctx.T("verein.admin.col.minutes")) }, rows,
                searchPlaceholder: ctx.T("verein.admin.meetings.search"), emptyText: ctx.T("verein.admin.meetings.none"))
                with { Below = new UiNode[] { Ui.Button(ctx.T("verein.admin.meetings.new"), Root + "meetings/new", icon: "ti-calendar-plus") } }), ctx.T("verein.admin.nav.meetings"));
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
                return PluginResult.Redirect(Root + "meetings/" + m.Id, ctx.T("verein.admin.invitationX", Sent(ctx, s, f)), f > 0);
            }
            if (m is not null && ctx.Request.Action == "minutes")
            {
                var final = ctx.Request.F("minutes_status") == VereinStore.Final;
                await store.Meetings.UpdateAsync(m.Id, new Dictionary<string, object?>
                {
                    ["minutes"] = V.Clip(ctx.Request.F("minutes"), 60000), ["minutes_status"] = final ? VereinStore.Final : VereinStore.Draft,
                    ["status"] = m.Status == VereinStore.Planned && final ? VereinStore.Held : m.Status
                }, ct);
                var notice = final ? ctx.T("verein.admin.minutes.publishedNotice") : ctx.T("verein.admin.minutes.draftNotice");
                if (final && m.MinutesStatus != VereinStore.Final && V.Checked(ctx.Request.F("notify")))
                {
                    var people = VereinStore.Audience(await store.PeopleAsync(ct), m.Audience, board);
                    var (s, f) = await VereinMail.SendAsync(ctx, VereinMail.Minutes, people, _ => MeetingValues(ctx, m), ct);
                    notice += " " + Sent(ctx, s, f);
                }
                return PluginResult.Redirect(Root + "meetings/" + m.Id, notice);
            }
            if (m is not null && ctx.Request.Action == "delete")
            {
                await store.Rsvps.DeleteWhereAsync(DataQuery.Everything.Eq("meeting_id", m.Id), ct);
                await store.Meetings.DeleteAsync(m.Id, ct);
                return PluginResult.Redirect(Root + "meetings", ctx.T("verein.admin.meeting.deleted"));
            }
            var title = V.Clip(ctx.Request.F("title"), 160);
            var starts = V.CleanDate(ctx.Request.F("starts"));
            if (title.Length < 3 || starts.Length == 0) return PluginResult.Redirect(Root + "meetings/" + (isNew ? "new" : m!.Id), ctx.T("verein.admin.err.titleAndDate"), true);
            var v = new Dictionary<string, object?>
            {
                ["title"] = title, ["kind"] = MeetingKinds.Contains(ctx.Request.F("kind")) ? ctx.Request.F("kind") : "", ["starts"] = starts, ["place"] = V.Clip(ctx.Request.F("place"), 200),
                ["agenda"] = V.Clip(ctx.Request.F("agenda"), 20000),
                ["audience"] = AudienceOptions(ctx).Any(o => o.Value == ctx.Request.F("audience")) ? ctx.Request.F("audience") : VereinStore.AudienceBoard,
                ["status"] = ctx.Request.F("status") is VereinStore.Held or VereinStore.Cancelled ? ctx.Request.F("status") : VereinStore.Planned
            };
            if (isNew) { v["created"] = DateTime.UtcNow; v["minutes"] = ""; v["minutes_status"] = VereinStore.Draft; var id = await store.Meetings.InsertAsync(v, ct); return PluginResult.Redirect(Root + "meetings/" + id, ctx.T("verein.admin.meeting.created")); }
            await store.Meetings.UpdateAsync(m!.Id, v, ct);
            return PluginResult.Redirect(Root + "meetings/" + m.Id, ctx.T("verein.admin.saved"));
        }

        var agendaTpls = await store.TemplatesAsync(VereinStore.TplAgenda, ct);
        Template? tpl = long.TryParse(ctx.Request.Q("vorlage"), out var tid) ? await store.TemplateAsync(tid, ct) : null;
        var form = Ui.Form(
            Ui.Input("title", ctx.T("verein.admin.col.title"), m?.Title ?? (tpl is null ? "" : V.Fill(tpl.Title, ctx))),
            Ui.Grid(220,
                Ui.Select("kind", ctx.T("verein.admin.col.kind"), m?.Kind ?? MeetingKinds[0], MeetingKinds.Select(k => Ui.Option(k, MeetingKindLabel(ctx, k))).ToArray()),
                Ui.Input("starts", ctx.T("verein.admin.f.starts"), m?.Starts ?? "", UiInputType.DateTime),
                Ui.Input("place", ctx.T("verein.admin.f.place"), m?.Place ?? "")),
            Ui.Grid(220,
                Ui.Select("audience", ctx.T("verein.admin.f.invited"), m?.Audience ?? VereinStore.AudienceBoard, AudienceOptions(ctx)),
                Ui.Select("status", ctx.T("verein.admin.col.status"), m?.Status ?? VereinStore.Planned, Ui.Option(VereinStore.Planned, ctx.T("verein.admin.mstatus.planned")), Ui.Option(VereinStore.Held, ctx.T("verein.admin.mstatus.held")), Ui.Option(VereinStore.Cancelled, ctx.T("verein.admin.mstatus.cancelled")))),
            Ui.TextArea("agenda", ctx.T("verein.admin.f.agenda"), m?.Agenda ?? (tpl is null ? "" : V.Fill(tpl.Body, ctx)), 10),
            Ui.FormActions(Ui.Submit(isNew ? ctx.T("verein.admin.create") : ctx.T("verein.admin.save")), Ui.Button(ctx.T("verein.admin.back"), Root + "meetings", UiButtonKind.Ghost)));
        if (isNew)
            return Page(Ui.Stack(
                Ui.Columns(new UiNode?[] { Ui.Card(form) }, new UiNode?[] { Ui.Card(ctx.T("verein.admin.agendaFromTpl"), agendaTpls.Count == 0 ? Ui.Muted(ctx.T("verein.admin.tpl.none")) :
                    Ui.Stack(agendaTpls.Select(t => (UiNode?)Ui.Button(t.Name, Root + "meetings/new?vorlage=" + t.Id, t.Id == tpl?.Id ? UiButtonKind.Primary : UiButtonKind.Ghost, small: true)).ToArray())) })), ctx.T("verein.admin.meetings.new"));

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
            Ui.Card(ctx.T("verein.admin.col.meeting"), form),
            Ui.Card(ctx.T("verein.admin.col.minutes"), Ui.Form(
                Ui.TextArea("minutes", ctx.T("verein.admin.text"), minutes, 18) with { Help = m.Minutes.Length == 0 ? ctx.T("verein.admin.minutes.prefilled") : null },
                Ui.Select("minutes_status", ctx.T("verein.admin.visibility"), m.MinutesStatus == VereinStore.Final ? VereinStore.Final : VereinStore.Draft,
                    Ui.Option(VereinStore.Draft, ctx.T("verein.admin.minutes.optDraft")), Ui.Option(VereinStore.Final, ctx.T("verein.admin.minutes.optFinal"))),
                m.MinutesStatus == VereinStore.Final ? null : Ui.Checkbox("notify", ctx.T("verein.admin.minutes.notify"), true),
                Ui.FormActions(Ui.Submit(ctx.T("verein.admin.minutes.save"), name: "action", value: "minutes"))))
        };
        var side = new List<UiNode?>
        {
            Ui.Card(ctx.T("verein.admin.file.invitation"), Ui.KeyValue((ctx.T("verein.admin.f.invited"), invited.Count + " (" + AudienceLabel(ctx, m.Audience) + ")"), (ctx.T("verein.admin.sentLabel"), m.InvitedAt is null ? ctx.T("verein.admin.notYet") : V.Stamp(ctx, m.InvitedAt))),
                m.Status == VereinStore.Planned ? Act(m.InvitedAt is null ? ctx.T("verein.admin.meeting.invite") : ctx.T("verein.admin.invite.again"), "invite", kind: UiButtonKind.Primary, icon: "ti-mail", confirm: ctx.T("verein.admin.meeting.inviteConfirm", invited.Count)) : null,
                Ui.Inline(Ui.Button(ctx.T("verein.admin.meeting.invitePdf"), Root + "meetings/" + m.Id + "/pdf", UiButtonKind.Ghost, "ti-printer", small: true),
                    Ui.Button(ctx.T("verein.admin.meeting.minutesPdf"), Root + "meetings/" + m.Id + "/pdf?teil=protokoll", UiButtonKind.Ghost, "ti-printer", small: true))),
            Ui.Card(ctx.T("verein.admin.col.replies"), Ui.Table(new[] { Ui.Column(ctx.T("verein.admin.col.name")), Ui.Column(ctx.T("verein.admin.col.answer")) }, invited.OrderBy(p => p.Last).Select(p =>
            {
                var r = rsvps.FirstOrDefault(x => x.MemberId == p.MemberId);
                return Ui.Row(p.Name, r is null ? Ui.Text("–") : r.Answer == VereinStore.Yes ? Ui.Badge(ctx.T("verein.admin.rsvp.yes"), UiTone.Success) : r.Answer == VereinStore.No ? Ui.Badge(ctx.T("verein.admin.rsvp.no"), UiTone.Error) : Ui.Badge(ctx.T("verein.admin.rsvp.maybe"), UiTone.Warning));
            }), emptyText: ctx.T("verein.admin.meeting.nobodyInvited"))),
            Ui.Card(ctx.T("verein.admin.delete"), Act(ctx.T("verein.admin.meeting.delete"), "delete", kind: UiButtonKind.Danger, confirm: ctx.T("verein.admin.meeting.deleteConfirm"), icon: "ti-trash"))
        };
        return Page(Ui.Stack(
            Ui.Columns(main, side)), m.Title);
    }

    // ---- events -------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> EventsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var past = ctx.Request.Q("vergangen") == "1";
        var all = await store.EventsAsync(ct);
        var list = past ? all.Where(e => string.CompareOrdinal(e.Day, V.Today) < 0).Reverse().ToList() : all.Where(e => string.CompareOrdinal((e.Ends.Length >= 10 ? e.Ends[..10] : e.Day), V.Today) >= 0).ToList();
        var replies = await store.AllRepliesAsync(ct);
        var people = await store.PeopleAsync(ct);
        var board = VereinSettings.BoardList(ctx);
        UiNode Participation(Event e)
        {
            if (!e.Ask) return Ui.Muted("–");
            var mine = replies.Where(r => r.EventId == e.Id).ToList();
            var open = Math.Max(0, VereinStore.Asked(people, e, board).Count() - mine.Count);
            return Ui.Badge(ctx.T("verein.admin.ev.partCounts", mine.Count(r => r.Answer == VereinStore.Yes), open), open > 0 ? UiTone.Warning : UiTone.Success);
        }
        var kinds = all.Select(e => e.Kind).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k).ToList();
        return Page(Ui.Stack(
            Ui.Tabs(Ui.Tab(ctx.T("verein.admin.ev.upcoming"), Root + "events", !past), Ui.Tab(ctx.T("verein.admin.ev.past"), Root + "events?vergangen=1", past)),
            Ui.Table(new[] { Ui.Column(ctx.T("verein.admin.col.event")), Ui.Column(ctx.T("verein.admin.col.when")), NotOnPhone(Ui.Column(ctx.T("verein.admin.col.kind"))), Ui.Column(ctx.T("verein.admin.f.place")), Ui.Column(ctx.T("verein.admin.col.participation")), NotOnPhone(Ui.Column(ctx.T("verein.admin.col.visible"))) },
                list.Select(e => Ui.RowFor(e.Title + " " + e.Kind + " " + e.Place, Ui.Link(e.Title, Root + "events/" + e.Id), V.When(ctx, e.Starts), e.Kind, e.Place, Participation(e),
                    e.Draft ? Ui.Badge(ctx.T("verein.admin.minutes.draft"), UiTone.Warning) : e.Cancelled ? Ui.Badge(ctx.T("verein.admin.ev.cancelled"), UiTone.Error) : e.Public ? Ui.Badge(ctx.T("verein.admin.ev.public"), UiTone.Success) : Ui.Badge(ctx.T("verein.admin.ev.internal"), UiTone.Info))
                    with { Tags = "k" + Math.Max(0, kinds.FindIndex(k => string.Equals(k, e.Kind, StringComparison.OrdinalIgnoreCase))) }),
                searchPlaceholder: ctx.T("verein.admin.ev.search"), emptyText: past ? ctx.T("verein.admin.ev.nonePast") : ctx.T("verein.admin.ev.noneUpcoming")) with
            {
                Filter = kinds.Count > 1 ? Ui.ListFilter(ctx.T("verein.admin.col.kind"), new[] { ("", ctx.T("verein.admin.ev.allKinds")) }.Concat(kinds.Select((k, i) => ("k" + i, k))).ToArray()) : null,
                Below = new UiNode[] { Ui.Button(ctx.T("verein.admin.ev.new"), Root + "events/new", icon: "ti-calendar-plus"), Ui.Button(ctx.T("verein.admin.ev.export"), VereinExport.Url("instagram"), UiButtonKind.Ghost, "ti-file-export") }
            }), ctx.T("verein.admin.nav.events"));
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
                return PluginResult.Redirect(Root + "events", ctx.T("verein.admin.ev.deleted"));
            }
            if (e is not null && ctx.Request.Action == "ask")
            {
                if (!e.ReplyOpen(V.Today)) return PluginResult.Redirect(Root + "events/" + e.Id, ctx.T("verein.admin.ev.askNotOpen"), true);
                var (s, f) = await SendAskAsync(ctx, store, e, ct);
                await store.Events.UpdateAsync(e.Id, new Dictionary<string, object?> { ["asked_at"] = DateTime.UtcNow }, ct);
                return PluginResult.Redirect(Root + "events/" + e.Id, ctx.T("verein.admin.ev.askX", Sent(ctx, s, f)), f > 0);
            }
            if (e is not null && ctx.Request.Action == "setreply" && long.TryParse(ctx.Request.F("member"), out var who)
                && ctx.Request.F("answer") is VereinStore.Yes or VereinStore.No or VereinStore.Maybe or "")
            {
                if (ctx.Request.F("answer") == "") await store.Replies.DeleteWhereAsync(DataQuery.Everything.Eq("event_id", e.Id).Eq("member_id", who), ct);
                else await store.SetReplyAsync(e.Id, who, ctx.Request.F("answer"), "eingetragen vom Vorstand", 0, ct);
                return PluginResult.Redirect(Root + "events/" + e.Id, ctx.T("verein.admin.ev.replySet"));
            }
            var title = V.Clip(ctx.Request.F("title"), 160);
            var starts = V.CleanDate(ctx.Request.F("starts"));
            if (title.Length < 2 || starts.Length == 0) return PluginResult.Redirect(Root + "events/" + (isNew ? "new" : e!.Id), ctx.T("verein.admin.err.titleAndStart"), true);
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
            var notice = ctx.Request.F("status") == "draft" ? " " + ctx.T("verein.admin.ev.stillDraft") : "";
            if (isNew) { v["created"] = DateTime.UtcNow; var id = await store.Events.InsertAsync(v, ct); return PluginResult.Redirect(Root + "events/" + id, ctx.T("verein.admin.ev.created") + notice); }
            await store.Events.UpdateAsync(e!.Id, v, ct);
            return PluginResult.Redirect(Root + "events/" + e.Id, ctx.T("verein.admin.saved") + notice);
        }

        var main = new UiNode?[]
        {
            Ui.Card(ctx.T("verein.admin.col.event"),
                Ui.Input("title", ctx.T("verein.admin.col.title"), e?.Title ?? "") with { Placeholder = ctx.T("verein.admin.ev.titlePh") },
                Ui.Grid(220, Ui.Input("kind", ctx.T("verein.admin.col.kind"), e?.Kind ?? "") with { Placeholder = ctx.T("verein.admin.ev.kindPh") }, Ui.Input("place", ctx.T("verein.admin.f.place"), e?.Place ?? "")),
                Ui.Grid(220, Ui.Input("starts", ctx.T("verein.admin.f.starts"), e?.Starts ?? "", UiInputType.DateTime), Ui.Input("ends", ctx.T("verein.admin.ev.ends"), e?.Ends ?? "", UiInputType.DateTime)),
                Ui.TextArea("description", ctx.T("verein.admin.ev.description"), e?.Description ?? "", 3),
                Ui.Input("link", ctx.T("verein.admin.ev.link"), e?.Link ?? "") with { Help = ctx.T("verein.admin.ev.linkHelp") }),
            Ui.Card(ctx.T("verein.admin.ev.planning"),
                Ui.RowEditor("plan", ctx.T("verein.admin.ev.schedule"), new[] { Ui.RowColumn("time", ctx.T("verein.admin.ev.time"), placeholder: "13:00", width: "110px"), Ui.RowColumn("what", ctx.T("verein.admin.ev.what"), placeholder: ctx.T("verein.admin.ev.whatPh")) },
                    (e?.Steps ?? Array.Empty<(string, string)>()).Select((s, i) => Ui.RowValue(i.ToString(CultureInfo.InvariantCulture), s.Time, s.What)), ctx.T("verein.admin.ev.step")),
                Ui.Input("meeting", ctx.T("verein.admin.ev.meetingPoint"), e?.Meeting ?? "") with { Placeholder = ctx.T("verein.admin.ev.meetingPointPh") },
                Ui.TextArea("parking", ctx.T("verein.admin.ev.parking"), e?.Parking ?? "", 3),
                Ui.TextArea("notes", ctx.T("verein.admin.ev.notes"), e?.Notes ?? "", 3) with { Placeholder = ctx.T("verein.admin.ev.notesPh") }),
            // the form is long: its save button belongs under it, not only in the side column
            Ui.FormActions(Ui.Submit(isNew ? ctx.T("verein.admin.create") : ctx.T("verein.admin.save")), Ui.Button(ctx.T("verein.admin.back"), Root + "events", UiButtonKind.Ghost))
        };
        var side = new UiNode?[]
        {
            Ui.Card(ctx.T("verein.admin.col.status"),
                Ui.Select("status", ctx.T("verein.admin.ev.state"), isNew || e!.Draft ? "draft" : "published", Ui.Option("draft", ctx.T("verein.admin.minutes.optDraft")), Ui.Option("published", ctx.T("verein.admin.ev.published"))),
                Ui.Checkbox("public", ctx.T("verein.admin.ev.publicBox"), e?.Public ?? true),
                Ui.Checkbox("cancelled", ctx.T("verein.admin.ev.cancelledBox"), e?.Cancelled ?? false)),
            Ui.Card(ctx.T("verein.admin.ev.ask"),
                Ui.Checkbox("ask", ctx.T("verein.admin.ev.askBox"), e?.Ask ?? ctx.Settings.GetBool(VereinSettings.AskDefault)),
                Ui.Checkbox("ask_required", ctx.T("verein.admin.ev.askRequired"), e?.AskRequired ?? false),
                Ui.Select("ask_audience", ctx.T("verein.admin.ev.askWho"), e is { AskAudience.Length: > 0 } ? e.AskAudience : VereinStore.AudienceAll, AudienceOptions(ctx)) with { Help = ctx.T("verein.admin.ev.askWhoHelp") },
                Ui.Input("ask_due", ctx.T("verein.admin.ev.askDue"), e?.AskDue ?? "", UiInputType.Date))
        };
        var form = Ui.Form(Ui.Columns(main, side));
        var replyCard = e is { Ask: true } ? await RepliesCardAsync(ctx, store, e, ct) : null;
        return Page(Ui.Stack(isNew ? null : Ui.PageHead(V.When(ctx, e!.Starts) + (e.Draft ? " · " + ctx.T("verein.admin.minutes.draft") : ""), e.Title),
            form,
            replyCard,
            isNew || e!.Draft ? null : Ui.Card(ctx.T("verein.admin.ev.share"), Ui.Button(ctx.T("verein.admin.ev.asInstagram"), VereinExport.Url("instagram") + "&vorlage=plakat&event=" + e!.Id, UiButtonKind.Ghost, "ti-brand-instagram")),
            isNew ? null : Ui.Card(ctx.T("verein.admin.delete"), Act(ctx.T("verein.admin.ev.delete"), "delete", kind: UiButtonKind.Danger, confirm: ctx.T("verein.admin.ev.deleteConfirm"), icon: "ti-trash"))), isNew ? ctx.T("verein.admin.ev.new") : e!.Title);
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
        var choices = new[] { Ui.Option("", ctx.T("verein.admin.rep.openOpt")), Ui.Option(VereinStore.Yes, ctx.T("verein.admin.rep.yes")), Ui.Option(VereinStore.Maybe, ctx.T("verein.admin.rep.maybe")), Ui.Option(VereinStore.No, ctx.T("verein.admin.rep.no")) };
        var table = Ui.Table(new[] { Ui.Column(ctx.T("verein.admin.col.name")), Ui.Column(ctx.T("verein.admin.col.answer")), Ui.Column(ctx.T("verein.admin.col.remark")), NotOnPhone(Ui.Column(ctx.T("verein.admin.col.when"))), Ui.Column(ctx.T("verein.admin.col.enter"), actions: true) },
            asked.OrderBy(p => replies.ContainsKey(p.MemberId)).ThenBy(p => p.Last).ThenBy(p => p.First).Select(p =>
            {
                replies.TryGetValue(p.MemberId, out var r);
                var by = r is null || r.By == 0 || r.By == p.MemberId ? "" : byId.TryGetValue(r.By, out var g) ? " (" + ctx.T("verein.admin.byX", g.First) + ")" : "";
                return Ui.RowFor(p.Name, p.Name + (p.Child ? " (" + ctx.T("verein.admin.badge.child") + ")" : ""),
                    r is null ? Ui.Badge(ctx.T("verein.admin.ans.open"), UiTone.Warning) : r.Answer == VereinStore.Yes ? Ui.Badge(ctx.T("verein.admin.rep.yes"), UiTone.Success) : r.Answer == VereinStore.No ? Ui.Badge(ctx.T("verein.admin.rep.no"), UiTone.Error) : Ui.Badge(ctx.T("verein.admin.rep.maybe")),
                    r?.Note ?? "", V.Stamp(ctx, r?.At) + by,
                    Ui.Form(Ui.Hidden("action", "setreply"), Ui.Hidden("member", p.MemberId.ToString(CultureInfo.InvariantCulture)), Ui.Select("answer", "", r?.Answer ?? "", choices), Ui.Submit("OK", UiButtonKind.Ghost, small: true)));
            }), searchPlaceholder: ctx.T("verein.admin.searchNames"), emptyText: ctx.T("verein.admin.rep.nobodyAsked"));
        return Ui.Card(ctx.T("verein.admin.rep.title", Count(VereinStore.Yes), Count(VereinStore.Maybe), Count(VereinStore.No), open),
            Ui.Inline(
                e.ReplyOpen(V.Today) ? Act(e.AskedAt is null ? ctx.T("verein.admin.rep.sendAsk") : ctx.T("verein.admin.rep.remindOpen"), "ask", kind: UiButtonKind.Primary, icon: "ti-mail",
                    confirm: ctx.T("verein.admin.rep.askConfirm")) : null,
                Ui.Button(ctx.T("verein.admin.rep.listPdf"), Root + "events/" + e.Id + "/pdf", UiButtonKind.Ghost, "ti-printer", small: true),
                Ui.Button(ctx.T("verein.admin.asCsv"), Root + "events/" + e.Id + "/csv", UiButtonKind.Ghost, "ti-file-spreadsheet", small: true)),
            e.AskedAt is null ? null : Ui.Muted(ctx.T("verein.admin.rep.lastAsked", V.Stamp(ctx, e.AskedAt))),
            e.Draft ? Ui.Muted(ctx.T("verein.admin.rep.draftNote")) : null,
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
        return VereinPrint.Result(VereinPrint.Participants(await VereinPrint.LetterheadAsync(ctx, ct), e, list), ctx.T("verein.admin.file.participants") + " " + e.Title);
    }

    private static async Task<PluginResult> EventCsvAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var e = await store.EventAsync(Id(ctx), ct);
        if (e is null) return PluginResult.NotFound();
        static string C(string s) { s ??= ""; if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
        var sb = new StringBuilder("\uFEFF" + string.Join(";", new[] { ctx.T("verein.admin.f.last"), ctx.T("verein.admin.f.first"), ctx.T("verein.admin.badge.child"), ctx.T("verein.admin.col.answer"), ctx.T("verein.admin.col.remark"), ctx.T("verein.admin.col.when") }.Select(C)) + "\r\n");
        foreach (var (p, r) in await ReplyListAsync(ctx, store, e, ct))
            sb.Append(string.Join(";", new[] { p.Last, p.First, p.Child ? ctx.T("verein.admin.yes") : "", r is null ? ctx.T("verein.admin.ans.open") : VereinPublic.ReplyLabel(ctx, r.Answer), r?.Note ?? "", V.Stamp(ctx, r?.At) }.Select(C))).Append("\r\n");
        // an export only (nothing reads it back), so its head, values and file name follow the admin's language
        return PluginResult.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", ctx.T("verein.admin.file.participationCsv") + "-" + VereinPrint.FileName(e.Title) + ".csv");
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
            return PluginResult.Redirect(Root + "uploads", ctx.T("verein.admin.up.nApproved", n));
        }
        if (ctx.Request.IsPost && long.TryParse(ctx.Request.F("id"), out var id) && await store.UploadAsync(id, ct) is Upload u)
        {
            if (ctx.Request.Action == "delete") { await ctx.Files.DeleteAsync(u.File, ct); await store.Uploads.DeleteAsync(u.Id, ct); return PluginResult.Redirect(Root + "uploads", ctx.T("verein.admin.deleted")); }
            if (ctx.Request.Action == "share") { await store.Uploads.UpdateAsync(u.Id, new Dictionary<string, object?> { ["shared"] = !u.Shared }, ct); return PluginResult.Redirect(Root + "uploads"); }
            if (ctx.Request.Action == "approve") { await store.Uploads.UpdateAsync(u.Id, new Dictionary<string, object?> { ["approved"] = true }, ct); return PluginResult.Redirect(Root + "uploads", ctx.T("verein.admin.up.approved")); }
            if (ctx.Request.Action == "event")
            {
                // "" or "0" = no event; anything else must be an event that exists (the field offers only those, but a post is a post)
                var raw = ctx.Request.F("event");
                long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var ev);
                if (ev != 0 && await store.EventAsync(ev, ct) is null) return PluginResult.Redirect(Root + "uploads", ctx.T("verein.admin.up.noSuchEvent"), isError: true);
                await store.Uploads.UpdateAsync(u.Id, new Dictionary<string, object?> { ["event_id"] = ev }, ct);
                return PluginResult.Redirect(Root + "uploads" + (PendingView(ctx) ? "?status=offen" : ""), ev == 0 ? ctx.T("verein.admin.up.unlinked") : ctx.T("verein.admin.up.linked"));
            }
        }
        var pendingOnly = ctx.Request.Q("status") == "offen";
        var list = (await store.UploadsAsync(ct: ct)).Where(x => !pendingOnly || !x.Approved).ToList();
        var pendingCount = (await store.UploadsAsync(ct: ct)).Count(x => !x.Approved);
        var people = (await store.PeopleAsync(ct)).ToDictionary(p => p.MemberId, p => p.Name);
        var events = (await store.EventsAsync(ct)).ToDictionary(e => e.Id);
        string Id(Upload x) => x.Id.ToString(CultureInfo.InvariantCulture);
        UiNode EventCell(Upload x)
        {
            var e = x.EventId > 0 && events.TryGetValue(x.EventId, out var found) ? found : null;
            return Ui.Form(Ui.Hidden("action", "event"), Ui.Hidden("id", Id(x)), pendingOnly ? Ui.Hidden("status", "offen") : null,
                EventField(ctx, e, events.Values));
        }
        return Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("verein.admin.nav.uploads"),
                pendingCount > 0 ? Act(ctx.T("verein.admin.up.approveAll", pendingCount), new Dictionary<string, string> { ["action"] = "approveall" }, UiButtonKind.Primary, ctx.T("verein.admin.up.approveAllConfirm"), "ti-check") : null),
            Ui.Tabs(Ui.Tab(ctx.T("verein.admin.tab.all"), Root + "uploads", !pendingOnly), Ui.Tab(ctx.T("verein.admin.up.pendingTab", pendingCount), Root + "uploads?status=offen", pendingOnly)),
            Ui.Table(new[] { Ui.Column(ctx.T("verein.admin.col.title")), Ui.Column(ctx.T("verein.admin.col.from")), NotOnPhone(Ui.Column(ctx.T("verein.admin.col.taken"))), Ui.Column(ctx.T("verein.admin.col.event")), Ui.Column(ctx.T("verein.admin.col.status")), Ui.Column("", actions: true) },
                list.Select(x => Ui.RowFor(x.Title + " " + x.FileName,
                    // the picture stands in the title cell: an own column without a heading pushed "Titel" away from its values
                    Ui.Inline(V.IsImage(Path.GetExtension(x.File).ToLowerInvariant()) ? Ui.Img(Root + "uploads/" + x.Id, x.Title, 48) : null,
                        Ui.Link(Short(x.Title.Length > 0 ? x.Title : x.FileName), Root + "uploads/" + x.Id, newTab: true)),
                    people.TryGetValue(x.MemberId, out var n) ? n : "–", x.Taken.Length > 0 ? V.Date(ctx, x.Taken) : V.Stamp(ctx, x.Created),
                    EventCell(x),
                    !x.Approved ? Ui.Badge(ctx.T("verein.admin.up.waiting"), UiTone.Warning) : x.Shared ? Ui.Badge(ctx.T("verein.admin.up.allMembers"), UiTone.Success) : Ui.Badge(ctx.T("verein.admin.up.private")),
                    Ui.Inline(
                        x.Approved ? null : Act(ctx.T("verein.admin.up.approve"), new Dictionary<string, string> { ["action"] = "approve", ["id"] = Id(x) }, UiButtonKind.Primary, icon: "ti-check"),
                        Ui.Action(x.Shared ? ctx.T("verein.admin.up.makePrivate") : ctx.T("verein.admin.up.makeShared"), new Dictionary<string, string> { ["action"] = "share", ["id"] = Id(x) }, icon: x.Shared ? "ti-lock" : "ti-world"),
                        Act(ctx.T("verein.admin.delete"), new Dictionary<string, string> { ["action"] = "delete", ["id"] = Id(x) }, UiButtonKind.Danger, ctx.T("verein.admin.up.deleteConfirm"), "ti-trash")))),
                searchPlaceholder: ctx.T("verein.admin.up.search"), pageSize: 50, emptyText: pendingOnly ? ctx.T("verein.admin.up.nonePending") : ctx.T("verein.admin.up.none"))), ctx.T("verein.admin.nav.uploads"));
    }

    /// <summary>A column that is a detail stays out of the card a row becomes on a phone (UiColumn.Phone, CMS 1.13.371). On an older host the property
    /// does not exist — the column is simply shown, the page must not fail.</summary>
    private static UiColumn NotOnPhone(UiColumn c)
    {
        try { return OffPhone(c); }
        catch (Exception ex) when (ex is MissingMethodException or TypeLoadException) { return c; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static UiColumn OffPhone(UiColumn c) => c with { Phone = false };

    private static string Short(string s) => s.Length > 36 ? s[..35] + "…" : s;

    private static bool PendingView(IPluginContext ctx) => ctx.Request.F("status") == "offen";

    /// <summary>
    /// The event of an upload: a lookup field (search, results fetched from <c>events/lookup</c> while one types), chosen = saved at once. A host
    /// older than the field (no <c>Ui.Lookup</c>) gets the drop-down with OK it had before — the plugin keeps loading there.
    /// </summary>
    private static UiNode EventField(IPluginContext ctx, Event? current, IEnumerable<Event> all)
    {
        try { return LookupField(ctx, current); }
        catch (Exception ex) when (ex is TypeLoadException or MissingMethodException or MissingFieldException)
        {
            var options = new[] { Ui.Option("0", ctx.T("verein.admin.up.noEvent")) }.Concat(all.Where(e => !e.Draft || e.Id == current?.Id).OrderByDescending(e => e.Starts).Take(80)
                .Select(e => Ui.Option(e.Id.ToString(CultureInfo.InvariantCulture), V.Date(ctx, e.Starts) + " " + e.Title))).ToArray();
            return Ui.Inline(Ui.Select("event", "", (current?.Id ?? 0).ToString(CultureInfo.InvariantCulture), options), Ui.Submit("OK", UiButtonKind.Ghost, small: true));
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static UiNode LookupField(IPluginContext ctx, Event? current) =>
        Ui.Lookup("event", ctx.T("verein.admin.col.event"), Root + "events/lookup", current is null ? "" : current.Id.ToString(CultureInfo.InvariantCulture),
            current is null ? "" : EventText(ctx, current)) with { AutoSubmit = true, Placeholder = ctx.T("verein.admin.up.noEvent") };

    private static string EventText(IPluginContext ctx, Event e) => e.Title + " · " + V.Date(ctx, e.Starts);

    /// <summary>
    /// The search behind the event field: <c>?q=</c> matches name, kind, place, date ("14.02.2027") and month ("Februar 2027"), every word must
    /// match; <c>?scope=upcoming</c> only what is still to come. Without a search the recent past comes first (that is what photos belong to), then
    /// what is ahead. 30 per page: <c>page</c> (from 1, MatUi's select) — the first version of the field sent <c>skip</c>, which is read too.
    /// </summary>
    private static async Task<PluginResult> EventLookupAsync(IPluginContext ctx, CancellationToken ct)
    {
        var today = V.Today;
        var words = (ctx.Request.Q("q") ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int.TryParse(ctx.Request.Q("skip"), NumberStyles.None, CultureInfo.InvariantCulture, out var skip);
        if (int.TryParse(ctx.Request.Q("page"), NumberStyles.None, CultureInfo.InvariantCulture, out var pageNo) && pageNo > 1) skip = (pageNo - 1) * 30;
        var upcomingOnly = ctx.Request.Q("scope") == "upcoming";
        string Hay(Event e)
        {
            // the date and month in German AND in the admin's language: a search typed either way finds the event
            var month = e.Day.Length >= 7 && int.TryParse(e.Day[5..7], NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= 1 and <= 12
                ? VereinInstagram.MonthNames[m - 1] + " " + e.Day[..4] + " " + V.MonthNames(ctx)[m - 1] + " " + e.Day[..4] : "";
            return (e.Title + " " + e.Kind + " " + e.Place + " " + V.Date(e.Starts) + " " + V.Date(ctx, e.Starts) + " " + month).ToLowerInvariant();
        }
        var all = (await new VereinStore(ctx.Data).EventsAsync(ct)).Where(e => words.All(w => Hay(e).Contains(w, StringComparison.Ordinal))).ToList();
        bool Ahead(Event e) => string.CompareOrdinal(e.Ends.Length >= 10 ? e.Ends[..10] : e.Day, today) >= 0;
        var past = upcomingOnly ? new List<Event>() : all.Where(e => !Ahead(e)).OrderByDescending(e => e.Starts).ToList();
        var ahead = all.Where(Ahead).OrderBy(e => e.Starts).ToList();
        var ordered = past.Select(e => (e, ctx.T("verein.admin.lookup.past"))).Concat(ahead.Select(e => (e, ctx.T("verein.admin.lookup.upcoming")))).ToList();
        const int size = 30;
        var page = ordered.Skip(Math.Max(0, skip)).Take(size).Select(x =>
        {
            var hint = V.Date(ctx, x.e.Starts) + (x.e.Kind.Length > 0 ? " · " + x.e.Kind : "") + (x.e.Cancelled ? " · " + ctx.T("verein.admin.ev.cancelled") : x.e.Draft ? " · " + ctx.T("verein.admin.minutes.draft") : "");
            return Ui.LookupItem(x.e.Id.ToString(CultureInfo.InvariantCulture), x.e.Title, hint, x.Item2);
        });
        return PluginResult.Lookup(page.ToList(), more: ordered.Count > skip + size);
    }

    /// <summary>The member list as a spreadsheet: every person (children too, with their parents), roles, contact, birthday, address and the own fields.</summary>
    private static async Task<PluginResult> MembersCsvAsync(IPluginContext ctx, CancellationToken ct)
    {
        var people = await new VereinStore(ctx.Data).PeopleAsync(ct);
        var byId = people.ToDictionary(p => p.Id);
        var fields = VereinSettings.Fields(ctx);
        static string C(string s) { s ??= ""; if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
        // an export only (no import reads it back), so its head and values follow the admin's language; dates stay dd.MM.yyyy for spreadsheets
        var sb = new StringBuilder("﻿" + string.Join(";", new[] { ctx.T("verein.admin.f.last"), ctx.T("verein.admin.f.first"), ctx.T("verein.admin.col.kind"), ctx.T("verein.admin.roles"), ctx.T("verein.admin.csv.email"), ctx.T("verein.admin.f.phone"), ctx.T("verein.admin.f.born"), ctx.T("verein.admin.csv.street"), ctx.T("verein.admin.f.zip"), ctx.T("verein.admin.f.city"), ctx.T("verein.admin.col.joined"), ctx.T("verein.admin.membership"), ctx.T("verein.admin.col.status"), ctx.T("verein.admin.child.belongsTo") }
            .Concat(fields.Select(f => f.Label)).Select(C)) + "\r\n");
        foreach (var p in people)
            sb.Append(string.Join(";", new[]
            {
                p.Last, p.First, p.Child ? ctx.T("verein.admin.badge.child") : ctx.T("verein.admin.badge.member"), string.Join(", ", p.Roles), p.Email, p.Phone, V.Date(p.Born), p.Street, p.Zip, p.City, V.Date(p.Joined), p.Passive ? ctx.T("verein.admin.badge.passive") : ctx.T("verein.admin.statusActive"), p.Active ? ctx.T("verein.admin.badge.member") : InactiveLabel(ctx, p),
                string.Join(", ", p.Guardians.Select(g => byId.TryGetValue(g, out var x) ? x.Name : ""))
            }.Concat(fields.Select(f => p.Extra.TryGetValue(f.Label, out var v) ? v : "")).Select(C))).Append("\r\n");
        return PluginResult.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", ctx.T("verein.admin.file.memberList") + "-" + V.Today + ".csv");
    }

    private static async Task<PluginResult> UploadFileAsync(IPluginContext ctx, CancellationToken ct)
    {
        var u = await new VereinStore(ctx.Data).UploadAsync(Id(ctx), ct);
        return u is null ? PluginResult.NotFound() : await VereinPublic.ServeAsync(ctx, u.File, u.FileName, u.FileType, ct);
    }

    // ---- templates ----------------------------------------------------------------------------------------------

    private static string TplKind(IPluginContext ctx, string k) => k switch { VereinStore.TplAgenda => ctx.T("verein.admin.f.agenda"), VereinStore.TplMinutes => ctx.T("verein.admin.col.minutes"), _ => ctx.T("verein.admin.col.doc") };

    private static async Task<PluginResult> TemplatesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var list = await new VereinStore(ctx.Data).TemplatesAsync(ct: ct);
        // the list in the admin's list shape: "Neue Vorlage" under the table; the placeholders are told where they are typed (the template's text field)
        return Page(Ui.Table(new[] { Ui.Column(ctx.T("verein.admin.col.name")), Ui.Column(ctx.T("verein.admin.col.for")) }, list.Select(t => Ui.RowFor(t.Name, Ui.Link(t.Name, Root + "templates/" + t.Id), TplKind(ctx, t.Kind))), emptyText: ctx.T("verein.admin.tpl.none"))
            with { Below = new UiNode[] { Ui.Button(ctx.T("verein.admin.tpl.new"), Root + "templates/new", icon: "ti-plus") } }, ctx.T("verein.admin.nav.templates"));
    }

    private static async Task<PluginResult> TemplateAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new VereinStore(ctx.Data);
        var isNew = ctx.Request.R("id") == "new";
        var t = isNew ? null : await store.TemplateAsync(Id(ctx), ct);
        if (!isNew && t is null) return PluginResult.NotFound();
        if (ctx.Request.IsPost)
        {
            if (t is not null && ctx.Request.Action == "delete") { await store.Templates.DeleteAsync(t.Id, ct); return PluginResult.Redirect(Root + "templates", ctx.T("verein.admin.tpl.deleted")); }
            var name = V.Clip(ctx.Request.F("name"), 120);
            if (name.Length < 2) return PluginResult.Redirect(Root + "templates/" + (isNew ? "new" : t!.Id), ctx.T("verein.admin.err.name"), true);
            var kind = ctx.Request.F("kind") is VereinStore.TplAgenda or VereinStore.TplMinutes ? ctx.Request.F("kind") : VereinStore.TplDocument;
            var v = new Dictionary<string, object?> { ["name"] = name, ["kind"] = kind, ["title"] = V.Clip(ctx.Request.F("title"), 160), ["body"] = V.Clip(ctx.Request.F("body"), 30000) };
            if (isNew) { v["created"] = DateTime.UtcNow; var id = await store.Templates.InsertAsync(v, ct); return PluginResult.Redirect(Root + "templates/" + id, ctx.T("verein.admin.tpl.created")); }
            await store.Templates.UpdateAsync(t!.Id, v, ct);
            return PluginResult.Redirect(Root + "templates/" + t.Id, ctx.T("verein.admin.saved"));
        }
        var form = Ui.Form(
            Ui.Grid(220, Ui.Input("name", ctx.T("verein.admin.tpl.name"), t?.Name ?? ""),
                Ui.Select("kind", ctx.T("verein.admin.col.for"), t?.Kind ?? VereinStore.TplDocument, Ui.Option(VereinStore.TplDocument, ctx.T("verein.admin.col.doc")), Ui.Option(VereinStore.TplAgenda, ctx.T("verein.admin.tpl.agendaMeeting")), Ui.Option(VereinStore.TplMinutes, ctx.T("verein.admin.col.minutes")))),
            Ui.Input("title", ctx.T("verein.admin.tpl.title"), t?.Title ?? ""),
            Ui.TextArea("body", ctx.T("verein.admin.text"), t?.Body ?? "", 18) with { Help = ctx.T("verein.admin.tpl.placeholders") },
            Ui.FormActions(Ui.Submit(isNew ? ctx.T("verein.admin.create") : ctx.T("verein.admin.save")), Ui.Button(ctx.T("verein.admin.back"), Root + "templates", UiButtonKind.Ghost)));
        return Page(Ui.Stack(
            Ui.Card(form), isNew ? null : Ui.Card(ctx.T("verein.admin.delete"), Act(ctx.T("verein.admin.tpl.delete"), "delete", kind: UiButtonKind.Danger, confirm: ctx.T("verein.admin.tpl.deleteConfirm"), icon: "ti-trash"))), isNew ? ctx.T("verein.admin.tpl.new") : t!.Name);
    }
}
