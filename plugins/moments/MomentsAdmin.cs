using System.Globalization;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Moments;

/// <summary>
/// The back office (Admin → Moments): release what guests uploaded, look after photos, comments and keywords, and keep the guest list with its access
/// codes. Everything is drawn with the toolkit; the photos themselves are loaded from the guest area's own routes, which let an administrator through.
/// </summary>
public static class MomentsAdmin
{
    public const string Root = "/admin/p/moments/";
    private const string B = MomentsPublic.Base;

    public static void Configure(IPluginBuilder b)
    {
        b.Admin.Section("Moments", "ti-photo-heart", AdminGroup.Content, 26)
            .Page("overview", null, OverviewAsync)
            .Page("review", null, ReviewAsync)
            .Page("photos", null, PhotosAsync)
            .Page("photos/{id}", null, PhotoAsync)
            .Page("albums", null, AlbumsAsync)
            .Page("albums/{id}", null, AlbumAsync)
            .Page("comments", null, CommentsAsync)
            .Page("guests", null, GuestsAsync)
            .Page("guests/{id}", null, GuestAsync)
            .Page("tags", null, TagsAsync)
            .Page("tags/{id}", null, TagAsync)
            .Link("Übersicht", "overview", "ti-layout-dashboard")
            .Link("Freigabe", "review", "ti-checks", badge: true)
            .Link("Fotos", "photos", "ti-photo", also: new[] { "photos/" })
            .Link("Alben", "albums", "ti-album", also: new[] { "albums/" })
            .Link("Kommentare", "comments", "ti-message-circle")
            .Link("Gäste", "guests", "ti-users", also: new[] { "guests/" })
            .Link("Personen & Stichworte", "tags", "ti-tags", also: new[] { "tags/" })
            .Link("Einstellungen", "_settings", "ti-settings")
            .Badge(async (c, ct) => await new MomentsStore(c.Data).PendingCountAsync(ct) is var n and > 0 ? n.ToString(CultureInfo.InvariantCulture) : null);
        b.Admin.Attention(async (c, ct) =>
        {
            var store = new MomentsStore(c.Data);
            var photos = await store.PendingCountAsync(ct);
            var comments = await store.Comments.CountAsync(DataQuery.Everything.Eq("status", MomentsStore.Pending), ct);
            var notes = new List<AttentionNote>();
            if (photos > 0) notes.Add(new AttentionNote("Moments", M.Plural(photos, "Foto wartet", "Fotos warten") + " auf Freigabe", Root + "review") { Tone = UiTone.Warning });
            if (comments > 0) notes.Add(new AttentionNote("Moments", M.Plural(comments, "Kommentar wartet", "Kommentare warten") + " auf Freigabe", Root + "comments?status=wartet") { Tone = UiTone.Warning });
            return notes;
        });
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static UiAction Act(string label, IReadOnlyDictionary<string, string> hidden, UiButtonKind kind = UiButtonKind.Ghost, string? confirm = null, string? icon = null) =>
        Ui.Action(label, hidden, kind, confirm, icon) with { WithLabel = true };
    private static Dictionary<string, string> Do(string action, long id) => new() { ["action"] = action, ["id"] = M.N(id) };
    private static long Id(IPluginContext ctx) => long.TryParse(ctx.Request.R("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
    private static string Thumb(long id) => B + "/bild/" + M.N(id) + "/t";
    private static string Short(string s, int n = 40) => s.Length > n ? s[..(n - 1)] + "…" : s;

    /// <summary>A detail column that stays out of the card a row becomes on a phone (UiColumn.Phone, CMS 1.13.371); an older host shows it.</summary>
    private static UiColumn NotOnPhone(UiColumn c)
    {
        try { return OffPhone(c); }
        catch (Exception ex) when (ex is MissingMethodException or TypeLoadException) { return c; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static UiColumn OffPhone(UiColumn c) => c with { Phone = false };

    private static UiBadge StatusBadge(Photo p) => p.Status switch
    {
        MomentsStore.Pending => Ui.Badge("wartet auf Freigabe", UiTone.Warning),
        MomentsStore.Hidden => Ui.Badge("ausgeblendet", UiTone.Muted),
        _ => p.Featured ? Ui.Badge("Highlight", UiTone.Info) : Ui.Badge("sichtbar", UiTone.Success)
    };

    /// <summary>The actions every photo list shares: release / hide, highlight, delete.</summary>
    private static async Task<PluginResult?> PhotoActionAsync(IPluginContext ctx, MomentsStore store, string back, CancellationToken ct)
    {
        if (!ctx.Request.IsPost) return null;
        if (ctx.Request.Action == "approveall")
        {
            var pending = await store.PhotosAsync(MomentsStore.Pending, ct);
            foreach (var x in pending) await store.SetStatusAsync(x, MomentsStore.Approved, ct);
            return PluginResult.Redirect(back, M.Plural(pending.Count, "Foto", "Fotos") + " freigegeben.");
        }
        if (!long.TryParse(ctx.Request.F("id"), out var id) || await store.PhotoAsync(id, ct) is not Photo p) return null;
        switch (ctx.Request.Action)
        {
            case "approve": await store.SetStatusAsync(p, MomentsStore.Approved, ct); return PluginResult.Redirect(back, "Freigegeben.");
            case "hide": await store.SetStatusAsync(p, MomentsStore.Hidden, ct); return PluginResult.Redirect(back, "Ausgeblendet – die Gäste sehen es nicht mehr.");
            case "feature":
                await store.Photos.UpdateAsync(p.Id, new Dictionary<string, object?> { ["featured"] = !p.Featured }, ct);
                return PluginResult.Redirect(back, p.Featured ? "Kein Highlight mehr." : "Als Highlight markiert – es steht oben in der Galerie.");
            case "delete": await store.DeletePhotoAsync(ctx.Files, p, ct); return PluginResult.Redirect(back, "Gelöscht.");
        }
        return null;
    }

    // ---- overview -----------------------------------------------------------------------------------------------

    private static async Task<PluginResult> OverviewAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        var photos = await store.PhotosAsync(ct: ct);
        var guests = await store.GuestsAsync(ct);
        var comments = await store.CommentsAsync(ct);
        var hearts = (await store.HeartsAsync(ct)).Sum(kv => kv.Value.Count);
        var pending = photos.Where(p => p.Pending).OrderBy(p => p.Created).ToList();
        var byGuest = guests.ToDictionary(g => g.Id);
        var signedIn = guests.Count(g => g.Visits > 0);
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(MomentsSettings.Event(ctx), "Moments",
                Ui.Button("Gästebereich öffnen", B, UiButtonKind.Ghost, "ti-external-link") with { NewTab = true },
                Ui.Button("Fotos hochladen", B + "/hochladen", UiButtonKind.Primary, "ti-camera-plus") with { NewTab = true }),
            (await store.AlbumsAsync(ct)).Count == 0 ? Ui.Alert("Tipp: Mit Alben bekommt die Galerie Struktur – z. B. „Fotograf“, „Gästebilder“, „Fotobox“. Die Galerie zeigt sie zuerst, in eurer Reihenfolge; Fotos der Gäste landen automatisch in eurem Gäste-Album. Admin → Moments → Alben.", UiTone.Info) : null,
            guests.Count == 0 ? Ui.Alert("Legt zuerst die Gäste an: jeder Haushalt bekommt einen persönlichen Zugangscode (mit Link und QR-Code für die Karte). Ohne Code kommt niemand in den Gästebereich.", UiTone.Info) : null,
            Ui.Stats(
                Ui.Stat("Fotos", M.N(photos.Count(p => p.Approved)), M.Plural(photos.Count(p => p.Featured), "Highlight", "Highlights"), href: Root + "photos"),
                Ui.Stat("Warten auf Freigabe", M.N(pending.Count), pending.Count > 0 ? "jetzt ansehen" : "alles erledigt", pending.Count > 0 ? UiTone.Warning : UiTone.Success, Root + "review"),
                Ui.Stat("Gäste angemeldet", signedIn + " von " + guests.Count, M.Plural(guests.Sum(g => g.PeopleList.Count), "Person", "Personen"), href: Root + "guests"),
                Ui.Stat("Kommentare", M.N(comments.Count(c => c.Visible)), hearts + " Herzen", href: Root + "comments")),
            pending.Count == 0 ? null : Ui.Card("Warten auf Freigabe",
                Ui.Grid(150, pending.Take(8).Select(p => (UiNode?)Ui.Stack(Ui.Img(Thumb(p.Id), p.FileName, 0), Ui.Muted(MomentsPublic.AuthorName(ctx, p.GuestId, byGuest)))).ToArray()),
                Ui.Inline(Ui.Button("Alle " + pending.Count + " ansehen und freigeben", Root + "review", UiButtonKind.Primary, "ti-checks"))),
            comments.Count == 0 ? null : Ui.Card("Neueste Kommentare",
                Ui.Table(new[] { Ui.Column("Von"), Ui.Column("Kommentar"), NotOnPhone(Ui.Column("Wann")) },
                    comments.Take(5).Select(c => Ui.Row(MomentsPublic.AuthorName(ctx, c.GuestId, byGuest), Ui.Link(Short(c.Text, 80), B + "/foto/" + c.PhotoId, newTab: true), M.Ago(c.Created)))),
                Ui.Inline(Ui.Button("Alle Kommentare", Root + "comments", UiButtonKind.Ghost)))
        ), "Moments");
    }

    // ---- review -------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> ReviewAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        if (await PhotoActionAsync(ctx, store, Root + "review", ct) is PluginResult done) return done;
        var pending = (await store.PhotosAsync(MomentsStore.Pending, ct)).OrderBy(p => p.Created).ToList();
        var guests = (await store.GuestsAsync(ct)).ToDictionary(g => g.Id);
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Was Gäste hochgeladen haben und die anderen noch nicht sehen", "Freigabe",
                pending.Count > 1 ? Act("Alle " + pending.Count + " freigeben", new Dictionary<string, string> { ["action"] = "approveall" }, UiButtonKind.Primary, "Alle wartenden Fotos freigeben?", "ti-checks") : null),
            pending.Count == 0
                ? Ui.Empty("Nichts wartet auf Freigabe.")
                : Ui.Grid(220, pending.Select(p => (UiNode?)Ui.Card(
                    Ui.Img(Thumb(p.Id), p.FileName, 0),
                    Ui.Text(MomentsPublic.AuthorName(ctx, p.GuestId, guests)),
                    Ui.Muted(M.Taken(p.Taken) + " · hochgeladen " + M.Ago(p.Created)),
                    Ui.Inline(
                        Act("Freigeben", Do("approve", p.Id), UiButtonKind.Primary, icon: "ti-check"),
                        Act("Löschen", Do("delete", p.Id), UiButtonKind.Danger, "Dieses Foto löschen? Der Gast sieht es dann auch nicht mehr.", "ti-trash"),
                        Ui.Button("Groß ansehen", B + "/foto/" + p.Id, UiButtonKind.Ghost, "ti-zoom-in") with { NewTab = true }))).ToArray())
        ), "Freigabe");
    }

    // ---- photos -------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> PhotosAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        var status = ctx.Request.Q("status");
        var back = Root + "photos" + (status.Length > 0 ? "?status=" + M.U(status) : "");
        if (await PhotoActionAsync(ctx, store, back, ct) is PluginResult done) return done;
        var all = await store.PhotosAsync(ct: ct);
        var guests = (await store.GuestsAsync(ct)).ToDictionary(g => g.Id);
        var hearts = await store.HeartsAsync(ct);
        var comments = (await store.CommentsAsync(ct)).GroupBy(c => c.PhotoId).ToDictionary(g => g.Key, g => g.Count());
        var tabs = new (string Key, string Label, Func<Photo, bool> Fits)[]
        {
            ("", "Alle", _ => true), ("sichtbar", "Sichtbar", p => p.Approved), ("highlights", "Highlights", p => p.Featured && p.Approved),
            ("wartet", "Warten", p => p.Pending), ("ausgeblendet", "Ausgeblendet", p => p.Status == MomentsStore.Hidden)
        };
        var tab = tabs.FirstOrDefault(t => t.Key == status) is { Label: not null } found ? found : tabs[0];
        var albums = (await store.AlbumsAsync(ct)).ToDictionary(a => a.Id);
        string AlbumOf(Photo p) => albums.TryGetValue(p.AlbumId, out var a) ? a.Name : "";
        var list = all.Where(tab.Fits).OrderByDescending(p => p.Created).ToList();
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Alle Fotos im Gästebereich – ausblenden, als Highlight markieren, Personen und Stichworte setzen", "Fotos",
                Ui.Button("Fotos hochladen", B + "/hochladen", UiButtonKind.Primary, "ti-camera-plus") with { NewTab = true }),
            Ui.Tabs(tabs.Select(t => Ui.Tab(t.Label + " (" + all.Count(t.Fits) + ")", Root + "photos" + (t.Key.Length > 0 ? "?status=" + t.Key : ""), t.Key == tab.Key)).ToArray()),
            Ui.Table(new[] { Ui.Column(""), Ui.Column("Foto"), Ui.Column("Von"), NotOnPhone(Ui.Column("Album")), NotOnPhone(Ui.Column("Aufgenommen")), NotOnPhone(Ui.Column("Reaktionen")), Ui.Column("Status"), Ui.Column("", actions: true) },
                list.Select(p => Ui.RowFor(p.Caption + " " + p.FileName + " " + MomentsPublic.AuthorName(ctx, p.GuestId, guests) + " " + AlbumOf(p),
                    Ui.Img(Thumb(p.Id), p.FileName, 56),
                    Ui.Link(Short(p.Caption.Length > 0 ? p.Caption : p.FileName.Length > 0 ? p.FileName : "Foto " + p.Id), Root + "photos/" + p.Id),
                    MomentsPublic.AuthorName(ctx, p.GuestId, guests),
                    AlbumOf(p) is { Length: > 0 } an ? an : "–",
                    M.Taken(p.Taken),
                    (hearts.TryGetValue(p.Id, out var h) ? h.Count : 0) + " ♥ · " + (comments.TryGetValue(p.Id, out var c) ? c : 0) + " Kommentare",
                    StatusBadge(p),
                    Ui.Inline(
                        p.Approved ? Act("Ausblenden", Do("hide", p.Id), icon: "ti-eye-off") : Act("Freigeben", Do("approve", p.Id), UiButtonKind.Primary, icon: "ti-check"),
                        p.Approved ? Act(p.Featured ? "Kein Highlight" : "Highlight", Do("feature", p.Id), icon: p.Featured ? "ti-star-off" : "ti-star") : null,
                        Act("Löschen", Do("delete", p.Id), UiButtonKind.Danger, "Foto mit allen Kommentaren und Markierungen löschen?", "ti-trash")))),
                searchPlaceholder: "Fotos durchsuchen …", pageSize: 50, emptyText: "Hier ist kein Foto.")
        ), "Fotos");
    }

    private static async Task<PluginResult> PhotoAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        if (await store.PhotoAsync(Id(ctx), ct) is not Photo p) return PluginResult.NotFound();
        var here = Root + "photos/" + p.Id;
        if (ctx.Request.IsPost && ctx.Request.Action == "delete")
        {
            await store.DeletePhotoAsync(ctx.Files, p, ct);
            return PluginResult.Redirect(Root + "photos", "Gelöscht.");
        }
        if (await PhotoActionAsync(ctx, store, here, ct) is PluginResult done) return done;
        if (ctx.Request.IsPost && ctx.Request.Action is "hidecomment" or "showcomment" or "deletecomment"
            && long.TryParse(ctx.Request.F("comment"), out var cid) && await store.CommentAsync(cid, ct) is Comment cm && cm.PhotoId == p.Id)
        {
            if (ctx.Request.Action == "deletecomment") await store.Comments.DeleteAsync(cm.Id, ct);
            else await store.Comments.UpdateAsync(cm.Id, new Dictionary<string, object?> { ["status"] = ctx.Request.Action == "hidecomment" ? MomentsStore.Hidden : MomentsStore.Visible }, ct);
            return PluginResult.Redirect(here, "Gespeichert.");
        }
        var guestList = await store.GuestsAsync(ct);
        if (ctx.Request.IsPost && ctx.Request.Action == "save")
        {
            var taken = M.TryTaken(ctx.Request.F("taken"), out var t) ? t.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : p.Taken;
            var status = ctx.Request.F("status") is MomentsStore.Approved or MomentsStore.Pending or MomentsStore.Hidden ? ctx.Request.F("status") : p.Status;
            await store.Photos.UpdateAsync(p.Id, new Dictionary<string, object?>
            {
                ["caption"] = M.Line(ctx.Request.F("caption"), 200), ["taken"] = taken, ["featured"] = ctx.Request.F("featured") is "true" or "on" or "1",
                ["album_id"] = long.TryParse(ctx.Request.F("album"), NumberStyles.None, CultureInfo.InvariantCulture, out var al) && (al == 0 || await store.AlbumAsync(al, ct) is not null) ? al : p.AlbumId
            }, ct);
            if (status != p.Status) await store.SetStatusAsync(p with { }, status, ct);
            await SaveMarksAsync(store, p.Id, ctx.Request.F("tags"), guestList, ct);
            return PluginResult.Redirect(here, "Gespeichert.");
        }

        var guests = guestList.ToDictionary(g => g.Id);
        var tags = await store.TagsAsync(ct);
        var tagById = tags.ToDictionary(t => t.Id);
        var marks = (await store.TagsOfPhotoAsync(p.Id, ct)).Where(m => tagById.ContainsKey(m.TagId)).Select(m => tagById[m.TagId].Name).ToList();
        var suggestions = guestList.SelectMany(g => g.PeopleList).Concat(tags.Select(t => t.Name)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !marks.Contains(n, StringComparer.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var comments = await store.CommentsOfPhotoAsync(p.Id, ct);
        var hearts = (await store.HeartsAsync(ct)).TryGetValue(p.Id, out var h) ? h : new List<long>();
        var takenValue = M.TryTaken(p.Taken, out var td) ? td.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) : "";
        var albumOptions = new List<UiOption> { Ui.Option("0", "kein Album") };
        albumOptions.AddRange((await store.AlbumsAsync(ct)).Select(a => Ui.Option(M.N(a.Id), a.Name)));
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(MomentsPublic.AuthorName(ctx, p.GuestId, guests) + " · " + M.Taken(p.Taken), p.Caption.Length > 0 ? p.Caption : "Foto " + p.Id,
                Ui.Button("Im Gästebereich ansehen", B + "/foto/" + p.Id, UiButtonKind.Ghost, "ti-external-link") with { NewTab = true }),
            Ui.Columns(
                new UiNode?[]
                {
                    Ui.Card(Ui.Img(B + "/bild/" + p.Id + "/v", p.Caption, 0)),
                    Ui.Card("Angaben", Ui.Form(
                        Ui.Hidden("action", "save"),
                        Ui.Input("caption", "Bildunterschrift", p.Caption) with { MaxLength = 200 },
                        Ui.Select("album", "Album", M.N(p.AlbumId), albumOptions.ToArray()),
                        Ui.Tags("tags", "Personen & Stichworte", string.Join(", ", marks), suggestions) with { Help = "Namen aus der Gästeliste werden als Person markiert, alles andere als Stichwort." },
                        Ui.Grid(220,
                            Ui.Input("taken", "Aufgenommen", takenValue, UiInputType.DateTime) with { Help = "Bestimmt die Reihenfolge in der chronologischen Galerie." },
                            Ui.Select("status", "Status", p.Status, Ui.Option(MomentsStore.Approved, "sichtbar für alle Gäste"), Ui.Option(MomentsStore.Pending, "wartet auf Freigabe"), Ui.Option(MomentsStore.Hidden, "ausgeblendet"))),
                        Ui.Checkbox("featured", "Highlight (steht oben in der Galerie)", p.Featured),
                        Ui.FormActions(Ui.Submit("Speichern"))))
                },
                new UiNode?[]
                {
                    Ui.Card("Datei", Ui.KeyValue(
                        ("Von", MomentsPublic.AuthorName(ctx, p.GuestId, guests)), ("Datei", p.FileName), ("Größe", M.Size(p.Size)),
                        ("Maße", p.Width > 0 ? p.Width + " × " + p.Height + " (Ansicht)" : "–"), ("Hochgeladen", M.Stamp(p.Created)), ("Freigegeben", p.ApprovedAt is null ? "–" : M.Stamp(p.ApprovedAt)),
                        ("Herzen", hearts.Count == 0 ? "–" : string.Join(", ", hearts.Select(x => MomentsPublic.AuthorName(ctx, x, guests)))))),
                    Ui.Card("Kommentare (" + comments.Count + ")", comments.Count == 0 ? Ui.Muted("Noch keine.") :
                        Ui.Table(new[] { Ui.Column("Von"), Ui.Column("Kommentar"), Ui.Column("", actions: true) },
                            comments.Select(c => Ui.Row(MomentsPublic.AuthorName(ctx, c.GuestId, guests) + (c.Visible ? "" : c.Status == MomentsStore.Pending ? " (wartet)" : " (ausgeblendet)"), c.Text,
                                Ui.Inline(
                                    c.Visible ? Act("Ausblenden", new Dictionary<string, string> { ["action"] = "hidecomment", ["comment"] = M.N(c.Id) }, icon: "ti-eye-off")
                                              : Act(c.Status == MomentsStore.Pending ? "Freigeben" : "Einblenden", new Dictionary<string, string> { ["action"] = "showcomment", ["comment"] = M.N(c.Id) }, UiButtonKind.Primary, icon: "ti-check"),
                                    Act("Löschen", new Dictionary<string, string> { ["action"] = "deletecomment", ["comment"] = M.N(c.Id) }, UiButtonKind.Danger, "Kommentar löschen?", "ti-trash")))))),
                    Ui.Card("Löschen", Ui.Muted("Das Foto verschwindet mit allen Kommentaren, Herzen und Markierungen."),
                        Act("Foto löschen", new Dictionary<string, string> { ["action"] = "delete" }, UiButtonKind.Danger, "Dieses Foto endgültig löschen?", "ti-trash"))
                })
        ), "Foto");
    }

    /// <summary>Sets a photo's marks to the posted list: new names become tags (a name from the guest list a person, anything else a keyword), marks no longer listed go.</summary>
    private static async Task SaveMarksAsync(MomentsStore store, long photoId, string posted, IReadOnlyList<Guest> guests, CancellationToken ct)
    {
        var names = posted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => M.Line(n, 40)).Where(n => n.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        var people = guests.SelectMany(g => g.PeopleList).ToList();
        var wanted = new HashSet<long>();
        foreach (var n in names)
        {
            var person = people.FirstOrDefault(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
            var tag = await store.EnsureTagAsync(person ?? n, person is null ? MomentsStore.Topic : MomentsStore.Person, ct);
            wanted.Add(tag.Id);
            await store.AddTagAsync(photoId, tag.Id, 0, ct);
        }
        foreach (var m in await store.TagsOfPhotoAsync(photoId, ct))
            if (!wanted.Contains(m.TagId)) await store.PhotoTags.DeleteAsync(m.Id, ct);
    }

    // ---- comments -----------------------------------------------------------------------------------------------

    private static async Task<PluginResult> CommentsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        var status = ctx.Request.Q("status");
        var back = Root + "comments" + (status.Length > 0 ? "?status=" + M.U(status) : "");
        if (ctx.Request.IsPost && long.TryParse(ctx.Request.F("id"), out var id) && await store.CommentAsync(id, ct) is Comment c)
        {
            switch (ctx.Request.Action)
            {
                case "show": await store.Comments.UpdateAsync(c.Id, new Dictionary<string, object?> { ["status"] = MomentsStore.Visible }, ct); return PluginResult.Redirect(back, "Sichtbar.");
                case "hide": await store.Comments.UpdateAsync(c.Id, new Dictionary<string, object?> { ["status"] = MomentsStore.Hidden }, ct); return PluginResult.Redirect(back, "Ausgeblendet.");
                case "delete": await store.Comments.DeleteAsync(c.Id, ct); return PluginResult.Redirect(back, "Gelöscht.");
            }
        }
        var all = await store.CommentsAsync(ct);
        var guests = (await store.GuestsAsync(ct)).ToDictionary(g => g.Id);
        var tabs = new (string Key, string Label, Func<Comment, bool> Fits)[]
        {
            ("", "Alle", _ => true), ("wartet", "Warten auf Freigabe", x => x.Status == MomentsStore.Pending), ("ausgeblendet", "Ausgeblendet", x => x.Status == MomentsStore.Hidden)
        };
        var tab = tabs.FirstOrDefault(t => t.Key == status) is { Label: not null } found ? found : tabs[0];
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(ctx.Settings.GetBool(MomentsSettings.CommentApproval) ? "Kommentare erscheinen erst nach eurer Freigabe." : "Kommentare erscheinen sofort – hier blendet ihr sie aus oder löscht sie.", "Kommentare"),
            Ui.Tabs(tabs.Select(t => Ui.Tab(t.Label + " (" + all.Count(t.Fits) + ")", Root + "comments" + (t.Key.Length > 0 ? "?status=" + t.Key : ""), t.Key == tab.Key)).ToArray()),
            Ui.Table(new[] { Ui.Column(""), Ui.Column("Von"), Ui.Column("Kommentar"), NotOnPhone(Ui.Column("Wann")), Ui.Column("Status"), Ui.Column("", actions: true) },
                all.Where(tab.Fits).Select(x => Ui.RowFor(x.Text + " " + MomentsPublic.AuthorName(ctx, x.GuestId, guests),
                    Ui.Img(Thumb(x.PhotoId), "", 48),
                    MomentsPublic.AuthorName(ctx, x.GuestId, guests),
                    Ui.Link(Short(x.Text, 90), Root + "photos/" + x.PhotoId),
                    M.Stamp(x.Created),
                    x.Status == MomentsStore.Pending ? Ui.Badge("wartet", UiTone.Warning) : x.Status == MomentsStore.Hidden ? Ui.Badge("ausgeblendet", UiTone.Muted) : Ui.Badge("sichtbar", UiTone.Success),
                    Ui.Inline(
                        x.Visible ? Act("Ausblenden", Do("hide", x.Id), icon: "ti-eye-off") : Act(x.Status == MomentsStore.Pending ? "Freigeben" : "Einblenden", Do("show", x.Id), UiButtonKind.Primary, icon: "ti-check"),
                        Act("Löschen", Do("delete", x.Id), UiButtonKind.Danger, "Kommentar löschen?", "ti-trash")))),
                searchPlaceholder: "Kommentare durchsuchen …", pageSize: 50, emptyText: "Keine Kommentare.")
        ), "Kommentare");
    }

    // ---- albums -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The albums: where photos come from ("Fotograf", "Gästebilder", "Fotobox") in the order the gallery shows them; one of them takes the guests'
    /// uploads by itself.
    /// </summary>
    private static async Task<PluginResult> AlbumsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        if (ctx.Request.IsPost)
        {
            var id = long.TryParse(ctx.Request.F("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var x) ? x : 0;
            switch (ctx.Request.Action)
            {
                case "add":
                {
                    var name = M.Line(ctx.Request.F("name"), 60);
                    if (name.Length == 0) return PluginResult.Redirect(Root + "albums", "Bitte einen Namen eingeben.", true);
                    if ((await store.AlbumsAsync(ct)).Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                        return PluginResult.Redirect(Root + "albums", "Ein Album mit diesem Namen gibt es schon.", true);
                    var created = await store.AddAlbumAsync(name, ct);
                    var taken = ctx.Request.F("guests") is "true" or "on" or "1" ? await store.SetGuestAlbumAsync(created, ct) : 0;
                    return PluginResult.Redirect(Root + "albums", "„" + name + "“ angelegt." + (taken > 0 ? " " + M.Plural(taken, "Gästefoto", "Gästefotos") + " übernommen." : ""));
                }
                case "up" or "down" when id > 0:
                    await store.MoveAlbumAsync(id, ctx.Request.Action == "up" ? -1 : 1, ct);
                    return PluginResult.Redirect(Root + "albums");
                case "guests" when id > 0:
                {
                    var taken = await store.SetGuestAlbumAsync(id, ct);
                    return PluginResult.Redirect(Root + "albums", "Fotos der Gäste landen jetzt hier." + (taken > 0 ? " " + M.Plural(taken, "Gästefoto ohne Album wurde", "Gästefotos ohne Album wurden") + " übernommen." : ""));
                }
                case "noguests":
                    await store.SetGuestAlbumAsync(0, ct);
                    return PluginResult.Redirect(Root + "albums", "Fotos der Gäste landen jetzt in keinem Album.");
            }
        }
        var albums = await store.AlbumsAsync(ct);
        var photos = await store.PhotosAsync(ct: ct);
        var count = photos.GroupBy(p => p.AlbumId).ToDictionary(g => g.Key, g => g.Count());
        var loose = count.TryGetValue(0, out var n0) ? n0 : 0;
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Die Galerie zeigt die Alben zuerst – in dieser Reihenfolge. Jedes Foto ist in einem Album.", "Alben",
                Ui.Button("Fotos hochladen", B + "/hochladen", UiButtonKind.Primary, "ti-camera-plus") with { NewTab = true }),
            Ui.Table(new[] { Ui.Column("Album"), Ui.Column("Fotos", right: true), Ui.Column("Gästefotos"), Ui.Column("", actions: true) },
                albums.Select((a, i) => Ui.Row(
                    Ui.Link(a.Name, Root + "albums/" + a.Id),
                    count.TryGetValue(a.Id, out var n) ? M.N(n) : "0",
                    a.Guests ? Ui.Badge("landen hier", UiTone.Success) : (object)"–",
                    Ui.Inline(
                        i > 0 ? Act("Nach oben", Do("up", a.Id), icon: "ti-arrow-up") : null,
                        i < albums.Count - 1 ? Act("Nach unten", Do("down", a.Id), icon: "ti-arrow-down") : null,
                        a.Guests ? null : Act("Gäste-Album", Do("guests", a.Id), icon: "ti-users", confirm: "Sollen die Fotos der Gäste ab jetzt in „" + a.Name + "“ landen? Gästefotos ohne Album kommen gleich mit."),
                        Ui.Button("Bearbeiten", Root + "albums/" + a.Id, UiButtonKind.Ghost, "ti-pencil")))),
                emptyText: "Noch keine Alben – ohne Alben zeigt die Galerie alle Fotos in einem Strom."),
            loose > 0 && albums.Count > 0 ? Ui.Muted(M.Plural(loose, "Foto ist", "Fotos sind") + " in keinem Album – sie stehen nur unter „Alle Fotos“. Zuordnen: beim Foto (Fotos → Foto) oder im Album „Fotos ohne Album übernehmen“.") : null,
            albums.Any(a => a.Guests) ? Ui.Inline(Act("Gästefotos in kein Album", new Dictionary<string, string> { ["action"] = "noguests" }, icon: "ti-users-minus")) : null,
            Ui.Card("Neues Album", Ui.Form(
                Ui.Hidden("action", "add"),
                Ui.Input("name", "Name", "") with { Required = true, MaxLength = 60, Placeholder = "z. B. Fotograf, Gästebilder, Fotobox" },
                Ui.Checkbox("guests", "Fotos der Gäste landen hier (beim Hochladen von selbst)", albums.Count > 0 && !albums.Any(a => a.Guests)),
                Ui.FormActions(Ui.Submit("Anlegen"))))
        ), "Alben");
    }

    private static async Task<PluginResult> AlbumAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        if (await store.AlbumAsync(Id(ctx), ct) is not Album a) return PluginResult.NotFound();
        var here = Root + "albums/" + a.Id;
        if (ctx.Request.IsPost)
        {
            switch (ctx.Request.Action)
            {
                case "save":
                {
                    var name = M.Line(ctx.Request.F("name"), 60);
                    if (name.Length == 0) return PluginResult.Redirect(here, "Bitte einen Namen eingeben.", true);
                    if ((await store.AlbumsAsync(ct)).Any(o => o.Id != a.Id && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
                        return PluginResult.Redirect(here, "Ein Album mit diesem Namen gibt es schon.", true);
                    await store.Albums.UpdateAsync(a.Id, new Dictionary<string, object?> { ["name"] = name }, ct);
                    return PluginResult.Redirect(here, "Gespeichert.");
                }
                case "adopt":
                {
                    // the photos that are in no album yet — all of them, or only the hosts' or only the guests'
                    var who = ctx.Request.F("who");
                    var adopt = (await store.PhotosAsync(ct: ct)).Where(p => p.AlbumId == 0 && (who == "gaeste" ? p.GuestId > 0 : who == "gastgeber" ? p.GuestId == 0 : true)).ToList();
                    foreach (var p in adopt) await store.Photos.UpdateAsync(p.Id, new Dictionary<string, object?> { ["album_id"] = a.Id }, ct);
                    return PluginResult.Redirect(here, M.Plural(adopt.Count, "Foto", "Fotos") + " übernommen.");
                }
                case "delete":
                    await store.DeleteAlbumAsync(a.Id, ct);
                    return PluginResult.Redirect(Root + "albums", "„" + a.Name + "“ ist gelöscht. Die Fotos sind noch da (in keinem Album).");
            }
        }
        var photos = await store.PhotosAsync(ct: ct);
        var mine = photos.Where(p => p.AlbumId == a.Id).OrderBy(p => p.Taken, StringComparer.Ordinal).ToList();
        var loose = photos.Where(p => p.AlbumId == 0).ToList();
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(M.Plural(mine.Count, "Foto", "Fotos") + (a.Guests ? " · hier landen die Fotos der Gäste" : ""), a.Name,
                Ui.Button("In der Galerie ansehen", B + "?album=" + a.Id, UiButtonKind.Ghost, "ti-external-link") with { NewTab = true },
                Ui.Button("Fotos hierher hochladen", B + "/hochladen?album=" + a.Id, UiButtonKind.Primary, "ti-camera-plus") with { NewTab = true }),
            Ui.Columns(
                new UiNode?[]
                {
                    Ui.Card("Album", Ui.Form(
                        Ui.Hidden("action", "save"),
                        Ui.Input("name", "Name", a.Name) with { Required = true, MaxLength = 60 },
                        Ui.FormActions(Ui.Submit("Speichern")))),
                    Ui.Card("Fotos (" + mine.Count + ")", mine.Count == 0 ? Ui.Muted("Noch keine.") :
                        Ui.Grid(90, mine.Take(24).Select(p => (UiNode?)Ui.Img(Thumb(p.Id), p.FileName, 90)).ToArray()))
                },
                new UiNode?[]
                {
                    loose.Count == 0 ? null : Ui.Card("Fotos ohne Album (" + loose.Count + ")",
                        Ui.Muted("Fotos, die in keinem Album sind, hierher holen."),
                        Ui.Inline(
                            Act("Alle " + loose.Count + " übernehmen", new Dictionary<string, string> { ["action"] = "adopt", ["who"] = "" }, icon: "ti-arrow-bar-to-down"),
                            loose.Any(p => p.GuestId > 0) && loose.Any(p => p.GuestId == 0)
                                ? Act("Nur die der Gäste", new Dictionary<string, string> { ["action"] = "adopt", ["who"] = "gaeste" }, icon: "ti-users") : null,
                            loose.Any(p => p.GuestId > 0) && loose.Any(p => p.GuestId == 0)
                                ? Act("Nur eure", new Dictionary<string, string> { ["action"] = "adopt", ["who"] = "gastgeber" }, icon: "ti-heart") : null)),
                    Ui.Card("Löschen", Ui.Muted("Das Album verschwindet, seine Fotos bleiben (in keinem Album, unter „Alle Fotos“)."),
                        Act("Album löschen", new Dictionary<string, string> { ["action"] = "delete" }, UiButtonKind.Danger, "„" + a.Name + "“ löschen? Die Fotos bleiben.", "ti-trash"))
                })
        ), a.Name);
    }

    // ---- guests -------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> GuestsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        var mode = MomentsSettings.Mode(ctx);
        if (ctx.Request.IsPost && ctx.Request.Action == "bulk")
        {
            // one household per line: "Name | Personen | Tisch | Benutzername | Passwort" (everything after the name may be left out)
            var lines = ctx.Request.F("lines").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(500).ToList();
            var all = await store.GuestsAsync(ct);
            var known = all.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var logins = all.Where(g => g.Login.Length > 0).Select(g => g.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var format = MomentsSettings.CodeFormat(ctx);
            var added = 0;
            var skipped = new List<string>();
            foreach (var line in lines)
            {
                var parts = line.Split('|').Select(x => x.Trim()).ToArray();
                var name = M.Line(parts[0], 120);
                if (name.Length == 0 || known.Contains(name)) continue;
                var login = parts.Length > 3 ? Passwords.CleanLogin(parts[3]) : "";
                var password = parts.Length > 4 ? parts[4] : "";
                if (login is null) { skipped.Add(name + " (Benutzername)"); continue; }
                if (login.Length > 0 && logins.Contains(login)) { skipped.Add(name + " (Benutzername vergeben)"); continue; }
                if (login.Length > 0 && password.Length is < Passwords.MinLength or > Passwords.MaxLength) { skipped.Add(name + " (Passwort zu kurz)"); continue; }
                var people = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : name;
                var id = await store.SaveGuestAsync(0, name, M.Clip(string.Join(", ", MomentsStore.SplitPeople(people)), 400), parts.Length > 2 ? M.Line(parts[2], 60) : "", "", true, format, ct);
                if (login.Length > 0)
                {
                    await store.Guests.UpdateAsync(id, new Dictionary<string, object?> { ["login"] = login, ["password"] = Passwords.Hash(password) }, ct);
                    logins.Add(login);
                }
                known.Add(name);
                added++;
            }
            var msg = added == 0 ? "Niemand Neues angelegt (schon vorhandene Namen werden übersprungen)." : M.Plural(added, "Haushalt", "Haushalte") + " angelegt – jeder mit eigenem Zugang.";
            if (skipped.Count > 0) msg += " Nicht angelegt: " + string.Join(", ", skipped.Take(10)) + (skipped.Count > 10 ? " …" : "") + ".";
            return PluginResult.Redirect(Root + "guests", msg, skipped.Count > 0 && added == 0);
        }
        var guests = await store.GuestsAsync(ct);
        var photos = await store.PhotosAsync(ct: ct);
        var count = photos.GroupBy(p => p.GuestId).ToDictionary(g => g.Key, g => g.Count());
        var signedIn = guests.Count(g => g.Visits > 0);
        var codes = mode != MomentsSettings.ModePassword;
        var showLogins = mode != MomentsSettings.ModeCode || guests.Any(g => g.Login.Length > 0);
        var columns = new List<UiColumn> { Ui.Column("Haushalt"), Ui.Column("Personen"), NotOnPhone(Ui.Column("Tisch")) };
        if (codes) columns.Add(Ui.Column("Code"));
        if (showLogins) columns.Add(Ui.Column("Benutzername"));
        columns.AddRange(new[] { NotOnPhone(Ui.Column("Zuletzt da")), NotOnPhone(Ui.Column("Fotos", right: true)), Ui.Column("Status") });
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(mode switch
                {
                    MomentsSettings.ModePassword => "Jeder Haushalt meldet sich mit Benutzername und Passwort an – beides vergebt ihr hier.",
                    MomentsSettings.ModeBoth => "Jeder Haushalt hat einen persönlichen Code (mit Link und QR-Code) und – wenn ihr wollt – Benutzername und Passwort.",
                    _ => "Jeder Haushalt bekommt einen persönlichen Zugangscode – mit Link und QR-Code, ohne E-Mail und Passwort."
                }, "Gäste",
                guests.Count > 0 ? Ui.Button("Zugangskarten", B + "/karten", UiButtonKind.Ghost, "ti-printer") with { NewTab = true } : null),
            Ui.Table(columns,
                guests.Select(g =>
                {
                    var cells = new List<object?> { Ui.Link(g.Name, Root + "guests/" + g.Id), string.Join(", ", g.PeopleList), g.Table };
                    if (codes) cells.Add(g.CodeShown);
                    if (showLogins) cells.Add(g.Login.Length == 0 ? "–" : g.Login + (g.HasPassword ? "" : " (ohne Passwort)"));
                    cells.Add(g.LastSeen is null ? "noch nie" : M.Stamp(g.LastSeen));
                    cells.Add(count.TryGetValue(g.Id, out var n) ? M.N(n) : "–");
                    cells.Add(!g.Active ? Ui.Badge("gesperrt", UiTone.Error) : g.Visits > 0 ? Ui.Badge("angemeldet", UiTone.Success) : Ui.Badge("noch nicht da", UiTone.Muted));
                    return Ui.RowFor(g.Name + " " + g.People + " " + g.Table + " " + g.Code + " " + g.Login, cells.ToArray());
                }),
                searchPlaceholder: "Gäste durchsuchen …", pageSize: 100, emptyText: "Noch keine Gäste.") with
            {
                Below = new UiNode[] { Ui.Button("Neuer Haushalt", Root + "guests/new", UiButtonKind.Primary, "ti-plus") }
            },
            Ui.Muted(signedIn + " von " + guests.Count + " Haushalten waren schon im Gästebereich."),
            Ui.Card("Mehrere auf einmal anlegen", Ui.Form(
                Ui.Hidden("action", "bulk"),
                Ui.TextArea("lines", "Ein Haushalt pro Zeile", "", 8) with
                {
                    Help = "Name | Personen | Tisch | Benutzername | Passwort – z. B. „Zoe & Fabian | Zoe, Fabian | Startlinie“ oder „Zoe & Fabian | | | zoe | Start12345“. " +
                           "Alles nach dem Namen darf fehlen; ein Name, den es schon gibt, wird übersprungen. Jeder bekommt außerdem einen Code im eingestellten Format."
                },
                Ui.FormActions(Ui.Submit("Anlegen"))))
        ), "Gäste");
    }

    private static async Task<PluginResult> GuestAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        var mode = MomentsSettings.Mode(ctx);
        var isNew = ctx.Request.R("id") == "new";
        var g = isNew ? null : await store.GuestAsync(Id(ctx), ct);
        if (!isNew && g is null) return PluginResult.NotFound();
        if (ctx.Request.IsPost)
        {
            var here = Root + "guests/" + (isNew ? "new" : M.N(g!.Id));
            switch (ctx.Request.Action)
            {
                case "save":
                {
                    var name = M.Line(ctx.Request.F("name"), 120);
                    if (name.Length == 0) return PluginResult.Redirect(here, "Bitte einen Namen eingeben.", true);
                    var all = await store.GuestsAsync(ct);
                    // the code: kept, typed by the hosts ("START12345"), or made in the chosen format for a new household
                    var typedCode = ctx.Request.F("code");
                    var code = Codes.Normalize(typedCode);
                    var codeChanged = typedCode.Trim().Length > 0 && code != (g?.Code ?? "");
                    if (codeChanged)
                    {
                        if (Codes.Problem(typedCode) is string problem) return PluginResult.Redirect(here, problem, true);
                        if (all.Any(x => x.Id != (g?.Id ?? 0) && x.Code == code)) return PluginResult.Redirect(here, "Diesen Code hat schon ein anderer Haushalt.", true);
                    }
                    // user name + password: the password only when one is typed (empty = keep); no user name = no password either
                    var login = Passwords.CleanLogin(ctx.Request.F("login"));
                    if (login is null) return PluginResult.Redirect(here, "Ein Benutzername hat 3 bis 40 Zeichen: Buchstaben, Ziffern, Leerzeichen, Punkt, Bindestrich, Unterstrich oder @.", true);
                    if (login.Length > 0 && all.Any(x => x.Id != (g?.Id ?? 0) && string.Equals(x.Login, login, StringComparison.OrdinalIgnoreCase)))
                        return PluginResult.Redirect(here, "Diesen Benutzernamen hat schon ein anderer Haushalt.", true);
                    var password = ctx.Request.F("password");
                    if (password.Length > 0 && password.Length is < Passwords.MinLength or > Passwords.MaxLength)
                        return PluginResult.Redirect(here, "Ein Passwort braucht mindestens " + Passwords.MinLength + " Zeichen.", true);
                    if (login.Length > 0 && password.Length == 0 && (g?.PasswordHash ?? "").Length == 0)
                        return PluginResult.Redirect(here, "Zum Benutzernamen gehört ein Passwort.", true);

                    var people = ctx.Request.F("people") is { Length: > 0 } pp ? pp : name;
                    var id = await store.SaveGuestAsync(g?.Id ?? 0, name, M.Clip(string.Join(", ", MomentsStore.SplitPeople(people)), 400), M.Line(ctx.Request.F("table"), 60),
                        M.Clip(ctx.Request.F("note"), 2000), isNew || ctx.Request.F("active") is "true" or "on" or "1", MomentsSettings.CodeFormat(ctx), ct);
                    var extra = new Dictionary<string, object?> { ["login"] = login };
                    if (login.Length == 0) extra["password"] = "";
                    else if (password.Length > 0) extra["password"] = Passwords.Hash(password);
                    // a changed code or a changed password ends every sign-in made so far — as "Neuer Code" does (a new account at the next sign-in)
                    var signOut = !isNew && g!.MemberId > 0 && (codeChanged || (password.Length > 0 && g.PasswordHash.Length > 0) || (login.Length == 0 && g.HasPassword && mode == MomentsSettings.ModePassword));
                    if (codeChanged) extra["code"] = code;
                    if (signOut) { extra["member_id"] = 0; extra["account"] = ""; }
                    await store.Guests.UpdateAsync(id, extra, ct);
                    if (signOut) await ctx.Members.DeleteAsync(g!.MemberId, ct);
                    return PluginResult.Redirect(Root + "guests/" + id, isNew ? "Angelegt – der Zugang steht rechts." : signOut ? "Gespeichert. Wer mit dem alten Zugang angemeldet war, ist abgemeldet." : "Gespeichert.");
                }
                case "newcode" when g is not null:
                    // a new code AND a new account: every phone signed in with the old one is out; the household signs in again with the new code
                    await store.Guests.UpdateAsync(g.Id, new Dictionary<string, object?> { ["code"] = await store.NewCodeAsync(MomentsSettings.CodeFormat(ctx), ct), ["member_id"] = 0, ["account"] = "" }, ct);
                    if (g.MemberId > 0) await ctx.Members.DeleteAsync(g.MemberId, ct);
                    return PluginResult.Redirect(Root + "guests/" + g.Id, "Neuer Code erzeugt. Der alte Link gilt nicht mehr, angemeldete Geräte sind abgemeldet.");
                case "delete" or "deleteall" when g is not null:
                    await store.EraseGuestAsync(ctx.Files, g.Id, ctx.Request.Action == "deleteall", ct);
                    await store.Guests.DeleteAsync(g.Id, ct);
                    if (g.MemberId > 0) await ctx.Members.DeleteAsync(g.MemberId, ct);
                    return PluginResult.Redirect(Root + "guests", g.Name + " ist entfernt.");
            }
        }
        var photos = g is null ? Array.Empty<Photo>() : await store.PhotosOfGuestAsync(g.Id, ct);
        var tables = (await store.GuestsAsync(ct)).Select(x => x.Table).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
        var link = ctx.Site.Origin.TrimEnd('/') + B + "/z/" + (g?.Code ?? "");
        var (codeLength, _) = MomentsSettings.CodeFormat(ctx);
        var form = Ui.Card(isNew ? "Neuer Haushalt" : "Haushalt", Ui.Form(
            Ui.Hidden("action", "save"),
            Ui.Input("name", "Name", g?.Name ?? "") with { Required = true, Placeholder = "z. B. Zoe & Fabian", Help = "So wird der Haushalt begrüßt und so stehen seine Fotos und Kommentare da." },
            Ui.Input("people", "Personen", g?.People ?? "") with { Placeholder = "z. B. Zoe, Fabian", Help = "Vornamen, mit Komma getrennt – sie werden beim Markieren auf Fotos vorgeschlagen. Leer = der Name." },
            Ui.Grid(220,
                Ui.Input("table", "Tisch", g?.Table ?? "") with { Help = tables.Count > 0 ? "Bisher: " + string.Join(", ", tables) : "Optional (z. B. „Startlinie“)." },
                isNew ? null : Ui.Checkbox("active", "Zugang aktiv", g!.Active)),
            Ui.Input("code", "Zugangscode", g?.CodeShown ?? "") with
            {
                Placeholder = isNew ? "leer = wird erzeugt (" + codeLength + " Zeichen)" : "",
                Help = "Ein eigener Code, z. B. „START12345“: mindestens " + Codes.MinLength + " Buchstaben (A–Z) oder Ziffern, Groß-/Kleinschreibung egal." +
                       (isNew ? "" : " Ändert ihr ihn, gilt der alte Link nicht mehr und wer damit angemeldet ist, wird abgemeldet.")
            },
            Ui.Grid(220,
                Ui.Input("login", "Benutzername", g?.Login ?? "") with { Placeholder = "z. B. zoe-fabian", Help = mode == MomentsSettings.ModeCode ? "Gilt erst, wenn in den Einstellungen die Anmeldung mit Benutzername und Passwort an ist." : "3 bis 40 Zeichen. Leer = keine Anmeldung mit Passwort." },
                Ui.Input("password", g?.HasPassword == true ? "Neues Passwort" : "Passwort", "", UiInputType.Password) with
                {
                    Placeholder = g?.HasPassword == true ? "leer = bleibt" : "",
                    Help = "Mindestens " + Passwords.MinLength + " Zeichen. Wird nur verschlüsselt gespeichert – gebt es den Gästen weiter, ablesen könnt ihr es hier später nicht mehr."
                }),
            Ui.TextArea("note", "Notiz (nur für euch)", g?.Note ?? "", 3),
            Ui.FormActions(Ui.Submit(isNew ? "Anlegen" : "Speichern"))));
        if (g is null) return PluginResult.Page(Ui.Stack(Ui.PageHead("Jeder Haushalt bekommt beim Anlegen seinen Zugang.", "Neuer Haushalt"), form), "Neuer Haushalt");
        var access = new List<(string, string)>();
        if (mode != MomentsSettings.ModePassword) { access.Add(("Code", g.CodeShown)); access.Add(("Persönlicher Link", link)); }
        if (mode != MomentsSettings.ModeCode || g.Login.Length > 0) { access.Add(("Benutzername", g.Login.Length > 0 ? g.Login : "–")); access.Add(("Passwort", g.HasPassword ? "gesetzt" : "–")); }
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(g.Visits > 0 ? "Zuletzt da: " + M.Stamp(g.LastSeen) + " · " + M.Plural(g.Visits, "Anmeldung", "Anmeldungen") : "Noch nicht im Gästebereich gewesen", g.Name),
            Ui.Columns(
                new UiNode?[] { form },
                new UiNode?[]
                {
                    Ui.Card("Zugang",
                        Ui.KeyValue(access.ToArray()),
                        Ui.Muted(mode == MomentsSettings.ModePassword
                            ? "Der Haushalt meldet sich mit Benutzername und Passwort an. Die Karte trägt den Benutzernamen und eine Zeile für das Passwort."
                            : "Der Link (oder der QR-Code auf der Karte – einfach mit der Handykamera scannen) meldet den Haushalt direkt an. Den Code kann man auch auf der Anmeldeseite eingeben."),
                        Ui.Inline(
                            Ui.Button("Karte: drucken, Bild, PDF", B + "/karten?ids=" + g.Id, UiButtonKind.Ghost, "ti-id-badge-2") with { NewTab = true },
                            Ui.Button("Per WhatsApp", MomentsCards.WhatsAppUrl(ctx, g), UiButtonKind.Ghost, "ti-brand-whatsapp") with { NewTab = true },
                            mode != MomentsSettings.ModePassword
                                ? Act("Neuer Code", new Dictionary<string, string> { ["action"] = "newcode" }, UiButtonKind.Ghost, "Neuen Code erzeugen? Der alte Link und der alte QR-Code gelten danach nicht mehr, und wer damit angemeldet ist, wird abgemeldet.", "ti-refresh")
                                : null)),
                    Ui.Card("Fotos (" + photos.Count + ")", photos.Count == 0 ? Ui.Muted("Noch keine hochgeladen.") :
                        Ui.Grid(90, photos.Take(12).Select(p => (UiNode?)Ui.Img(Thumb(p.Id), p.FileName, 90)).ToArray())),
                    Ui.Card("Entfernen",
                        Ui.Muted("Entfernt den Haushalt mit seinem Zugang, seinen Kommentaren und Herzen. Seine Fotos bleiben (als „ein Gast“) – oder gehen mit."),
                        Ui.Inline(
                            Act("Entfernen", new Dictionary<string, string> { ["action"] = "delete" }, UiButtonKind.Danger, g.Name + " entfernen? Die Fotos bleiben.", "ti-user-minus"),
                            photos.Count > 0 ? Act("Mit Fotos entfernen", new Dictionary<string, string> { ["action"] = "deleteall" }, UiButtonKind.Danger, g.Name + " mit allen " + photos.Count + " Fotos entfernen?", "ti-trash") : null))
                })
        ), g.Name);
    }

    // ---- tags ---------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> TagsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        if (ctx.Request.IsPost && ctx.Request.Action == "add")
        {
            var added = 0;
            foreach (var name in ctx.Request.F("names").Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => M.Line(n, 40)).Where(n => n.Length >= 2).Take(100))
            {
                if (await store.TagByNameAsync(name, ct) is not null) continue;
                await store.EnsureTagAsync(name, ctx.Request.F("kind") == MomentsStore.Person ? MomentsStore.Person : MomentsStore.Topic, ct);
                added++;
            }
            return PluginResult.Redirect(Root + "tags", added == 0 ? "Nichts Neues." : M.Plural(added, "Eintrag", "Einträge") + " angelegt.");
        }
        var tags = await store.TagsAsync(ct);
        var used = (await store.PhotoTagsAsync(ct)).GroupBy(m => m.TagId).ToDictionary(g => g.Key, g => g.Count());
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Womit Gäste die Fotos markieren: Personen (aus der Gästeliste) und Stichworte wie „Trauung“ oder „Tanzfläche“.", "Personen & Stichworte"),
            Ui.Table(new[] { Ui.Column("Name"), Ui.Column("Art"), Ui.Column("Fotos", right: true) },
                tags.OrderBy(t => t.Kind == MomentsStore.Person ? 1 : 0).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => Ui.RowFor(t.Name,
                    Ui.Link(t.Name, Root + "tags/" + t.Id), t.Kind == MomentsStore.Person ? Ui.Badge("Person", UiTone.Info) : Ui.Badge("Stichwort"), used.TryGetValue(t.Id, out var n) ? M.N(n) : "0")),
                searchPlaceholder: "Suchen …", pageSize: 100, emptyText: "Noch nichts – Gäste legen beim Markieren neue an, oder ihr gebt hier welche vor."),
            Ui.Card("Stichworte vorgeben", Ui.Form(
                Ui.Hidden("action", "add"),
                Ui.TextArea("names", "Neue Einträge", "", 3) with { Help = "Mit Komma oder Zeile getrennt – z. B. Trauung, Sektempfang, Brautpaar, Torte, Tanzfläche, Fotobox." },
                Ui.Select("kind", "Art", MomentsStore.Topic, Ui.Option(MomentsStore.Topic, "Stichwort"), Ui.Option(MomentsStore.Person, "Person")),
                Ui.FormActions(Ui.Submit("Anlegen"))))
        ), "Personen & Stichworte");
    }

    private static async Task<PluginResult> TagAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        if (await store.TagAsync(Id(ctx), ct) is not Tag t) return PluginResult.NotFound();
        var tags = await store.TagsAsync(ct);
        if (ctx.Request.IsPost)
        {
            switch (ctx.Request.Action)
            {
                case "save":
                {
                    var name = M.Line(ctx.Request.F("name"), 40);
                    if (name.Length < 2) return PluginResult.Redirect(Root + "tags/" + t.Id, "Bitte einen Namen eingeben.", true);
                    if (tags.FirstOrDefault(x => x.Id != t.Id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is Tag twin)
                    {
                        await store.MergeTagAsync(t.Id, twin.Id, ct);
                        return PluginResult.Redirect(Root + "tags/" + twin.Id, "„" + t.Name + "“ ist mit „" + twin.Name + "“ zusammengeführt.");
                    }
                    await store.Tags.UpdateAsync(t.Id, new Dictionary<string, object?> { ["name"] = name, ["kind"] = ctx.Request.F("kind") == MomentsStore.Person ? MomentsStore.Person : MomentsStore.Topic }, ct);
                    return PluginResult.Redirect(Root + "tags/" + t.Id, "Gespeichert.");
                }
                case "merge" when long.TryParse(ctx.Request.F("into"), out var into) && tags.Any(x => x.Id == into && x.Id != t.Id):
                    await store.MergeTagAsync(t.Id, into, ct);
                    return PluginResult.Redirect(Root + "tags/" + into, "Zusammengeführt.");
                case "delete":
                    await store.DeleteTagAsync(t.Id, ct);
                    return PluginResult.Redirect(Root + "tags", "„" + t.Name + "“ ist gelöscht – die Fotos sind nur nicht mehr damit markiert.");
            }
        }
        var marks = (await store.PhotoTagsAsync(ct)).Where(m => m.TagId == t.Id).ToList();
        var others = tags.Where(x => x.Id != t.Id).ToList();
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(M.Plural(marks.Count, "Foto", "Fotos") + " damit markiert", t.Name,
                Ui.Button("Im Gästebereich ansehen", B + "?tag=" + t.Id, UiButtonKind.Ghost, "ti-external-link") with { NewTab = true }),
            Ui.Columns(
                new UiNode?[]
                {
                    Ui.Card(Ui.Form(
                        Ui.Hidden("action", "save"),
                        Ui.Input("name", "Name", t.Name) with { Required = true, MaxLength = 40, Help = "Heißt ein anderer Eintrag schon so, werden beide zusammengeführt." },
                        Ui.Select("kind", "Art", t.Kind, Ui.Option(MomentsStore.Topic, "Stichwort"), Ui.Option(MomentsStore.Person, "Person")),
                        Ui.FormActions(Ui.Submit("Speichern")))),
                    marks.Count == 0 ? null : Ui.Card("Fotos", Ui.Grid(90, marks.Take(24).Select(m => (UiNode?)Ui.Img(Thumb(m.PhotoId), "", 90)).ToArray()))
                },
                new UiNode?[]
                {
                    others.Count == 0 ? null : Ui.Card("Zusammenführen", Ui.Form(
                        Ui.Hidden("action", "merge"),
                        Ui.Select("into", "In diesen Eintrag", "", others.Select(x => Ui.Option(M.N(x.Id), x.Name)).ToArray()) with { Help = "Für Doppelte wie „Tanz“ und „Tanzfläche“: alle Fotos bekommen den anderen, dieser verschwindet." },
                        Ui.FormActions(Ui.Submit("Zusammenführen", UiButtonKind.Ghost)))),
                    Ui.Card("Löschen", Ui.Muted("Die Fotos bleiben, sie sind nur nicht mehr damit markiert."),
                        Act("Löschen", new Dictionary<string, string> { ["action"] = "delete" }, UiButtonKind.Danger, "„" + t.Name + "“ löschen?", "ti-trash"))
                })
        ), t.Name);
    }
}
