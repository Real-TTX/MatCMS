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
            // link labels are keys: the host's sidebar looks them up in the admin's language
            .Link("moments.nav.overview", "overview", "ti-layout-dashboard")
            .Link("moments.nav.review", "review", "ti-checks", badge: true)
            .Link("moments.photos", "photos", "ti-photo", also: new[] { "photos/" })
            .Link("moments.albums", "albums", "ti-album", also: new[] { "albums/" })
            .Link("moments.comments", "comments", "ti-message-circle")
            .Link("moments.guests", "guests", "ti-users", also: new[] { "guests/" })
            .Link("moments.nav.tags", "tags", "ti-tags", also: new[] { "tags/" })
            .Link("moments.nav.settings", "_settings", "ti-settings")
            .Badge(async (c, ct) => await new MomentsStore(c.Data).PendingCountAsync(ct) is var n and > 0 ? n.ToString(CultureInfo.InvariantCulture) : null);
        b.Admin.Attention(async (c, ct) =>
        {
            var store = new MomentsStore(c.Data);
            var photos = await store.PendingCountAsync(ct);
            var comments = await store.Comments.CountAsync(DataQuery.Everything.Eq("status", MomentsStore.Pending), ct);
            var notes = new List<AttentionNote>();
            if (photos > 0) notes.Add(new AttentionNote("Moments", M.Count(c, photos, "moments.attention.photos."), Root + "review") { Tone = UiTone.Warning });
            if (comments > 0) notes.Add(new AttentionNote("Moments", M.Count(c, comments, "moments.attention.comments."), Root + "comments?status=wartet") { Tone = UiTone.Warning });
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

    private static UiBadge StatusBadge(IPluginContext ctx, Photo p) => p.Status switch
    {
        MomentsStore.Pending => Ui.Badge(ctx.T("moments.status.waiting"), UiTone.Warning),
        MomentsStore.Hidden => Ui.Badge(ctx.T("moments.status.hidden"), UiTone.Muted),
        _ => p.Featured ? Ui.Badge(ctx.T("moments.highlight"), UiTone.Info) : Ui.Badge(ctx.T("moments.status.visible"), UiTone.Success)
    };

    /// <summary>The actions every photo list shares: release / hide, highlight, delete.</summary>
    private static async Task<PluginResult?> PhotoActionAsync(IPluginContext ctx, MomentsStore store, string back, CancellationToken ct)
    {
        if (!ctx.Request.IsPost) return null;
        if (ctx.Request.Action == "approveall")
        {
            var pending = await store.PhotosAsync(MomentsStore.Pending, ct);
            foreach (var x in pending) await store.SetStatusAsync(x, MomentsStore.Approved, ct);
            return PluginResult.Redirect(back, M.Count(ctx, pending.Count, "moments.flash.approvedCount."));
        }
        if (!long.TryParse(ctx.Request.F("id"), out var id) || await store.PhotoAsync(id, ct) is not Photo p) return null;
        switch (ctx.Request.Action)
        {
            case "approve": await store.SetStatusAsync(p, MomentsStore.Approved, ct); return PluginResult.Redirect(back, ctx.T("moments.flash.approved"));
            case "hide": await store.SetStatusAsync(p, MomentsStore.Hidden, ct); return PluginResult.Redirect(back, ctx.T("moments.flash.hidden"));
            case "feature":
                await store.Photos.UpdateAsync(p.Id, new Dictionary<string, object?> { ["featured"] = !p.Featured }, ct);
                return PluginResult.Redirect(back, ctx.T(p.Featured ? "moments.flash.unfeatured" : "moments.flash.featured"));
            case "delete": await store.DeletePhotoAsync(ctx.Files, p, ct); return PluginResult.Redirect(back, ctx.T("moments.flash.deleted"));
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
                Ui.Button(ctx.T("moments.admin.openArea"), B, UiButtonKind.Ghost, "ti-external-link") with { NewTab = true },
                Ui.Button(ctx.T("moments.uploadPhotos"), B + "/hochladen", UiButtonKind.Primary, "ti-camera-plus") with { NewTab = true }),
            (await store.AlbumsAsync(ct)).Count == 0 ? Ui.Alert(ctx.T("moments.admin.albumTip"), UiTone.Info) : null,
            guests.Count == 0 ? Ui.Alert(ctx.T("moments.admin.guestsFirst"), UiTone.Info) : null,
            Ui.Stats(
                Ui.Stat(ctx.T("moments.photos"), M.N(photos.Count(p => p.Approved)), M.Count(ctx, photos.Count(p => p.Featured), "moments.count.highlight."), href: Root + "photos"),
                Ui.Stat(ctx.T("moments.admin.waiting"), M.N(pending.Count), ctx.T(pending.Count > 0 ? "moments.admin.lookNow" : "moments.admin.allDone"), pending.Count > 0 ? UiTone.Warning : UiTone.Success, Root + "review"),
                Ui.Stat(ctx.T("moments.admin.signedIn"), ctx.T("moments.admin.xOfY", M.N(signedIn), M.N(guests.Count)), M.Count(ctx, guests.Sum(g => g.PeopleList.Count), "moments.count.person."), href: Root + "guests"),
                Ui.Stat(ctx.T("moments.comments"), M.N(comments.Count(c => c.Visible)), ctx.T("moments.admin.hearts", M.N(hearts)), href: Root + "comments")),
            pending.Count == 0 ? null : Ui.Card(ctx.T("moments.admin.waiting"),
                Ui.Grid(150, pending.Take(8).Select(p => (UiNode?)Ui.Stack(Ui.Img(Thumb(p.Id), p.FileName, 0), Ui.Muted(MomentsPublic.AuthorName(ctx, p.GuestId, byGuest)))).ToArray()),
                Ui.Inline(Ui.Button(ctx.T("moments.admin.reviewAll", M.N(pending.Count)), Root + "review", UiButtonKind.Primary, "ti-checks"))),
            comments.Count == 0 ? null : Ui.Card(ctx.T("moments.admin.latestComments"),
                Ui.Table(new[] { Ui.Column(ctx.T("moments.col.from")), Ui.Column(ctx.T("moments.comment")), NotOnPhone(Ui.Column(ctx.T("moments.col.when"))) },
                    comments.Take(5).Select(c => Ui.Row(MomentsPublic.AuthorName(ctx, c.GuestId, byGuest), Ui.Link(Short(c.Text, 80), B + "/foto/" + c.PhotoId, newTab: true), M.Ago(ctx, c.Created)))),
                Ui.Inline(Ui.Button(ctx.T("moments.admin.allComments"), Root + "comments", UiButtonKind.Ghost)))
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
            Ui.PageHead(ctx.T("moments.review.intro"), ctx.T("moments.nav.review"),
                pending.Count > 1 ? Act(ctx.T("moments.review.approveAll", M.N(pending.Count)), new Dictionary<string, string> { ["action"] = "approveall" }, UiButtonKind.Primary, ctx.T("moments.review.approveAllConfirm"), "ti-checks") : null),
            pending.Count == 0
                ? Ui.Empty(ctx.T("moments.review.nothing"))
                : Ui.Grid(220, pending.Select(p => (UiNode?)Ui.Card(
                    Ui.Img(Thumb(p.Id), p.FileName, 0),
                    Ui.Text(MomentsPublic.AuthorName(ctx, p.GuestId, guests)),
                    Ui.Muted(M.Taken(ctx, p.Taken) + " · " + ctx.T("moments.review.uploaded", M.Ago(ctx, p.Created))),
                    Ui.Inline(
                        Act(ctx.T("moments.approve"), Do("approve", p.Id), UiButtonKind.Primary, icon: "ti-check"),
                        Act(ctx.T("moments.delete"), Do("delete", p.Id), UiButtonKind.Danger, ctx.T("moments.review.deleteConfirm"), "ti-trash"),
                        Ui.Button(ctx.T("moments.review.viewLarge"), B + "/foto/" + p.Id, UiButtonKind.Ghost, "ti-zoom-in") with { NewTab = true }))).ToArray())
        ), ctx.T("moments.nav.review"));
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
            ("", ctx.T("moments.tab.all"), _ => true), ("sichtbar", ctx.T("moments.tab.visible"), p => p.Approved), ("highlights", ctx.T("moments.highlights"), p => p.Featured && p.Approved),
            ("wartet", ctx.T("moments.tab.waiting"), p => p.Pending), ("ausgeblendet", ctx.T("moments.tab.hidden"), p => p.Status == MomentsStore.Hidden)
        };
        var tab = tabs.FirstOrDefault(t => t.Key == status) is { Label: not null } found ? found : tabs[0];
        var albums = (await store.AlbumsAsync(ct)).ToDictionary(a => a.Id);
        string AlbumOf(Photo p) => albums.TryGetValue(p.AlbumId, out var a) ? a.Name : "";
        var list = all.Where(tab.Fits).OrderByDescending(p => p.Created).ToList();
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(ctx.T("moments.photos.intro"), ctx.T("moments.photos"),
                Ui.Button(ctx.T("moments.uploadPhotos"), B + "/hochladen", UiButtonKind.Primary, "ti-camera-plus") with { NewTab = true }),
            Ui.Tabs(tabs.Select(t => Ui.Tab(t.Label + " (" + all.Count(t.Fits) + ")", Root + "photos" + (t.Key.Length > 0 ? "?status=" + t.Key : ""), t.Key == tab.Key)).ToArray()),
            Ui.Table(new[] { Ui.Column(""), Ui.Column(ctx.T("moments.photo")), Ui.Column(ctx.T("moments.col.from")), NotOnPhone(Ui.Column(ctx.T("moments.album"))), NotOnPhone(Ui.Column(ctx.T("moments.col.taken"))), NotOnPhone(Ui.Column(ctx.T("moments.col.reactions"))), Ui.Column(ctx.T("moments.col.status")), Ui.Column("", actions: true) },
                list.Select(p => Ui.RowFor(p.Caption + " " + p.FileName + " " + MomentsPublic.AuthorName(ctx, p.GuestId, guests) + " " + AlbumOf(p),
                    Ui.Img(Thumb(p.Id), p.FileName, 56),
                    Ui.Link(Short(p.Caption.Length > 0 ? p.Caption : p.FileName.Length > 0 ? p.FileName : ctx.T("moments.photoNumber", M.N(p.Id))), Root + "photos/" + p.Id),
                    MomentsPublic.AuthorName(ctx, p.GuestId, guests),
                    AlbumOf(p) is { Length: > 0 } an ? an : "–",
                    M.Taken(ctx, p.Taken),
                    (hearts.TryGetValue(p.Id, out var h) ? h.Count : 0) + " ♥ · " + ctx.T("moments.photos.comments", M.N(comments.TryGetValue(p.Id, out var c) ? c : 0)),
                    StatusBadge(ctx, p),
                    Ui.Inline(
                        p.Approved ? Act(ctx.T("moments.hide"), Do("hide", p.Id), icon: "ti-eye-off") : Act(ctx.T("moments.approve"), Do("approve", p.Id), UiButtonKind.Primary, icon: "ti-check"),
                        p.Approved ? Act(ctx.T(p.Featured ? "moments.photos.noHighlight" : "moments.highlight"), Do("feature", p.Id), icon: p.Featured ? "ti-star-off" : "ti-star") : null,
                        Act(ctx.T("moments.delete"), Do("delete", p.Id), UiButtonKind.Danger, ctx.T("moments.photos.deleteConfirm"), "ti-trash")))),
                searchPlaceholder: ctx.T("moments.photos.search"), pageSize: 50, emptyText: ctx.T("moments.photos.empty"))
        ), ctx.T("moments.photos"));
    }

    private static async Task<PluginResult> PhotoAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        if (await store.PhotoAsync(Id(ctx), ct) is not Photo p) return PluginResult.NotFound();
        var here = Root + "photos/" + p.Id;
        if (ctx.Request.IsPost && ctx.Request.Action == "delete")
        {
            await store.DeletePhotoAsync(ctx.Files, p, ct);
            return PluginResult.Redirect(Root + "photos", ctx.T("moments.flash.deleted"));
        }
        if (await PhotoActionAsync(ctx, store, here, ct) is PluginResult done) return done;
        if (ctx.Request.IsPost && ctx.Request.Action is "hidecomment" or "showcomment" or "deletecomment"
            && long.TryParse(ctx.Request.F("comment"), out var cid) && await store.CommentAsync(cid, ct) is Comment cm && cm.PhotoId == p.Id)
        {
            if (ctx.Request.Action == "deletecomment") await store.Comments.DeleteAsync(cm.Id, ct);
            else await store.Comments.UpdateAsync(cm.Id, new Dictionary<string, object?> { ["status"] = ctx.Request.Action == "hidecomment" ? MomentsStore.Hidden : MomentsStore.Visible }, ct);
            return PluginResult.Redirect(here, ctx.T("moments.saved"));
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
            return PluginResult.Redirect(here, ctx.T("moments.saved"));
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
        var albumOptions = new List<UiOption> { Ui.Option("0", ctx.T("moments.noAlbum")) };
        albumOptions.AddRange((await store.AlbumsAsync(ct)).Select(a => Ui.Option(M.N(a.Id), a.Name)));
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(MomentsPublic.AuthorName(ctx, p.GuestId, guests) + " · " + M.Taken(ctx, p.Taken), p.Caption.Length > 0 ? p.Caption : ctx.T("moments.photoNumber", M.N(p.Id)),
                Ui.Button(ctx.T("moments.admin.viewInArea"), B + "/foto/" + p.Id, UiButtonKind.Ghost, "ti-external-link") with { NewTab = true }),
            Ui.Columns(
                new UiNode?[]
                {
                    Ui.Card(Ui.Img(B + "/bild/" + p.Id + "/v", p.Caption, 0)),
                    Ui.Card(ctx.T("moments.photo.details"), Ui.Form(
                        Ui.Hidden("action", "save"),
                        Ui.Input("caption", ctx.T("moments.caption"), p.Caption) with { MaxLength = 200 },
                        Ui.Select("album", ctx.T("moments.album"), M.N(p.AlbumId), albumOptions.ToArray()),
                        Ui.Tags("tags", ctx.T("moments.nav.tags"), string.Join(", ", marks), suggestions) with { Help = ctx.T("moments.photo.tagsHelp") },
                        Ui.Grid(220,
                            Ui.Input("taken", ctx.T("moments.col.taken"), takenValue, UiInputType.DateTime) with { Help = ctx.T("moments.photo.takenHelp") },
                            Ui.Select("status", ctx.T("moments.col.status"), p.Status, Ui.Option(MomentsStore.Approved, ctx.T("moments.photo.statusVisible")), Ui.Option(MomentsStore.Pending, ctx.T("moments.status.waiting")), Ui.Option(MomentsStore.Hidden, ctx.T("moments.status.hidden")))),
                        Ui.Checkbox("featured", ctx.T("moments.photo.featuredBox"), p.Featured),
                        Ui.FormActions(Ui.Submit(ctx.T("moments.save")))))
                },
                new UiNode?[]
                {
                    Ui.Card(ctx.T("moments.photo.file"), Ui.KeyValue(
                        (ctx.T("moments.col.from"), MomentsPublic.AuthorName(ctx, p.GuestId, guests)), (ctx.T("moments.photo.file"), p.FileName), (ctx.T("moments.photo.size"), M.Size(ctx, p.Size)),
                        (ctx.T("moments.photo.dimensions"), p.Width > 0 ? ctx.T("moments.photo.dimensionsValue", M.N(p.Width), M.N(p.Height)) : "–"), (ctx.T("moments.photo.uploadedAt"), M.Stamp(p.Created)), (ctx.T("moments.photo.approvedAt"), p.ApprovedAt is null ? "–" : M.Stamp(p.ApprovedAt)),
                        (ctx.T("moments.photo.hearts"), hearts.Count == 0 ? "–" : string.Join(", ", hearts.Select(x => MomentsPublic.AuthorName(ctx, x, guests)))))),
                    Ui.Card(ctx.T("moments.comments") + " (" + comments.Count + ")", comments.Count == 0 ? Ui.Muted(ctx.T("moments.noneYet")) :
                        Ui.Table(new[] { Ui.Column(ctx.T("moments.col.from")), Ui.Column(ctx.T("moments.comment")), Ui.Column("", actions: true) },
                            comments.Select(c => Ui.Row(MomentsPublic.AuthorName(ctx, c.GuestId, guests) + (c.Visible ? "" : " (" + ctx.T(c.Status == MomentsStore.Pending ? "moments.status.waitingShort" : "moments.status.hidden") + ")"), c.Text,
                                Ui.Inline(
                                    c.Visible ? Act(ctx.T("moments.hide"), new Dictionary<string, string> { ["action"] = "hidecomment", ["comment"] = M.N(c.Id) }, icon: "ti-eye-off")
                                              : Act(ctx.T(c.Status == MomentsStore.Pending ? "moments.approve" : "moments.show"), new Dictionary<string, string> { ["action"] = "showcomment", ["comment"] = M.N(c.Id) }, UiButtonKind.Primary, icon: "ti-check"),
                                    Act(ctx.T("moments.delete"), new Dictionary<string, string> { ["action"] = "deletecomment", ["comment"] = M.N(c.Id) }, UiButtonKind.Danger, ctx.T("moments.comments.deleteConfirm"), "ti-trash")))))),
                    Ui.Card(ctx.T("moments.delete"), Ui.Muted(ctx.T("moments.photo.deleteNote")),
                        Act(ctx.T("moments.photo.deleteButton"), new Dictionary<string, string> { ["action"] = "delete" }, UiButtonKind.Danger, ctx.T("moments.photo.deleteConfirm"), "ti-trash"))
                })
        ), ctx.T("moments.photo"));
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
                case "show": await store.Comments.UpdateAsync(c.Id, new Dictionary<string, object?> { ["status"] = MomentsStore.Visible }, ct); return PluginResult.Redirect(back, ctx.T("moments.flash.visible"));
                case "hide": await store.Comments.UpdateAsync(c.Id, new Dictionary<string, object?> { ["status"] = MomentsStore.Hidden }, ct); return PluginResult.Redirect(back, ctx.T("moments.flash.hiddenShort"));
                case "delete": await store.Comments.DeleteAsync(c.Id, ct); return PluginResult.Redirect(back, ctx.T("moments.flash.deleted"));
            }
        }
        var all = await store.CommentsAsync(ct);
        var guests = (await store.GuestsAsync(ct)).ToDictionary(g => g.Id);
        var tabs = new (string Key, string Label, Func<Comment, bool> Fits)[]
        {
            ("", ctx.T("moments.tab.all"), _ => true), ("wartet", ctx.T("moments.admin.waiting"), x => x.Status == MomentsStore.Pending), ("ausgeblendet", ctx.T("moments.tab.hidden"), x => x.Status == MomentsStore.Hidden)
        };
        var tab = tabs.FirstOrDefault(t => t.Key == status) is { Label: not null } found ? found : tabs[0];
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(ctx.T(ctx.Settings.GetBool(MomentsSettings.CommentApproval) ? "moments.comments.introApproval" : "moments.comments.introDirect"), ctx.T("moments.comments")),
            Ui.Tabs(tabs.Select(t => Ui.Tab(t.Label + " (" + all.Count(t.Fits) + ")", Root + "comments" + (t.Key.Length > 0 ? "?status=" + t.Key : ""), t.Key == tab.Key)).ToArray()),
            Ui.Table(new[] { Ui.Column(""), Ui.Column(ctx.T("moments.col.from")), Ui.Column(ctx.T("moments.comment")), NotOnPhone(Ui.Column(ctx.T("moments.col.when"))), Ui.Column(ctx.T("moments.col.status")), Ui.Column("", actions: true) },
                all.Where(tab.Fits).Select(x => Ui.RowFor(x.Text + " " + MomentsPublic.AuthorName(ctx, x.GuestId, guests),
                    Ui.Img(Thumb(x.PhotoId), "", 48),
                    MomentsPublic.AuthorName(ctx, x.GuestId, guests),
                    Ui.Link(Short(x.Text, 90), Root + "photos/" + x.PhotoId),
                    M.Stamp(x.Created),
                    x.Status == MomentsStore.Pending ? Ui.Badge(ctx.T("moments.status.waitingShort"), UiTone.Warning) : x.Status == MomentsStore.Hidden ? Ui.Badge(ctx.T("moments.status.hidden"), UiTone.Muted) : Ui.Badge(ctx.T("moments.status.visible"), UiTone.Success),
                    Ui.Inline(
                        x.Visible ? Act(ctx.T("moments.hide"), Do("hide", x.Id), icon: "ti-eye-off") : Act(ctx.T(x.Status == MomentsStore.Pending ? "moments.approve" : "moments.show"), Do("show", x.Id), UiButtonKind.Primary, icon: "ti-check"),
                        Act(ctx.T("moments.delete"), Do("delete", x.Id), UiButtonKind.Danger, ctx.T("moments.comments.deleteConfirm"), "ti-trash")))),
                searchPlaceholder: ctx.T("moments.comments.search"), pageSize: 50, emptyText: ctx.T("moments.comments.empty"))
        ), ctx.T("moments.comments"));
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
                    if (name.Length == 0) return PluginResult.Redirect(Root + "albums", ctx.T("moments.err.name"), true);
                    if ((await store.AlbumsAsync(ct)).Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                        return PluginResult.Redirect(Root + "albums", ctx.T("moments.albums.exists"), true);
                    var created = await store.AddAlbumAsync(name, ct);
                    var taken = ctx.Request.F("guests") is "true" or "on" or "1" ? await store.SetGuestAlbumAsync(created, ct) : 0;
                    return PluginResult.Redirect(Root + "albums", ctx.T("moments.albums.created", name) + (taken > 0 ? " " + M.Count(ctx, taken, "moments.albums.adoptedGuest.") : ""));
                }
                case "up" or "down" when id > 0:
                    await store.MoveAlbumAsync(id, ctx.Request.Action == "up" ? -1 : 1, ct);
                    return PluginResult.Redirect(Root + "albums");
                case "guests" when id > 0:
                {
                    var taken = await store.SetGuestAlbumAsync(id, ct);
                    return PluginResult.Redirect(Root + "albums", ctx.T("moments.albums.guestsHere") + (taken > 0 ? " " + M.Count(ctx, taken, "moments.albums.adoptedLoose.") : ""));
                }
                case "noguests":
                    await store.SetGuestAlbumAsync(0, ct);
                    return PluginResult.Redirect(Root + "albums", ctx.T("moments.albums.guestsNowhere"));
            }
        }
        var albums = await store.AlbumsAsync(ct);
        var photos = await store.PhotosAsync(ct: ct);
        var count = photos.GroupBy(p => p.AlbumId).ToDictionary(g => g.Key, g => g.Count());
        var loose = count.TryGetValue(0, out var n0) ? n0 : 0;
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(ctx.T("moments.albums.intro"), ctx.T("moments.albums"),
                Ui.Button(ctx.T("moments.uploadPhotos"), B + "/hochladen", UiButtonKind.Primary, "ti-camera-plus") with { NewTab = true }),
            Ui.Table(new[] { Ui.Column(ctx.T("moments.album")), Ui.Column(ctx.T("moments.photos"), right: true), Ui.Column(ctx.T("moments.albums.guestPhotos")), Ui.Column("", actions: true) },
                albums.Select((a, i) => Ui.Row(
                    Ui.Link(a.Name, Root + "albums/" + a.Id),
                    count.TryGetValue(a.Id, out var n) ? M.N(n) : "0",
                    a.Guests ? Ui.Badge(ctx.T("moments.albums.landHere"), UiTone.Success) : (object)"–",
                    Ui.Inline(
                        i > 0 ? Act(ctx.T("moments.albums.up"), Do("up", a.Id), icon: "ti-arrow-up") : null,
                        i < albums.Count - 1 ? Act(ctx.T("moments.albums.down"), Do("down", a.Id), icon: "ti-arrow-down") : null,
                        a.Guests ? null : Act(ctx.T("moments.albums.guestAlbum"), Do("guests", a.Id), icon: "ti-users", confirm: ctx.T("moments.albums.guestAlbumConfirm", a.Name)),
                        Ui.Button(ctx.T("moments.edit"), Root + "albums/" + a.Id, UiButtonKind.Ghost, "ti-pencil")))),
                emptyText: ctx.T("moments.albums.empty")),
            loose > 0 && albums.Count > 0 ? Ui.Muted(M.Count(ctx, loose, "moments.albums.loose.")) : null,
            albums.Any(a => a.Guests) ? Ui.Inline(Act(ctx.T("moments.albums.noGuestAlbum"), new Dictionary<string, string> { ["action"] = "noguests" }, icon: "ti-users-minus")) : null,
            Ui.Card(ctx.T("moments.albums.new"), Ui.Form(
                Ui.Hidden("action", "add"),
                Ui.Input("name", ctx.T("moments.name"), "") with { Required = true, MaxLength = 60, Placeholder = ctx.T("moments.albums.namePlaceholder") },
                Ui.Checkbox("guests", ctx.T("moments.albums.guestsBox"), albums.Count > 0 && !albums.Any(a => a.Guests)),
                Ui.FormActions(Ui.Submit(ctx.T("moments.create")))))
        ), ctx.T("moments.albums"));
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
                    if (name.Length == 0) return PluginResult.Redirect(here, ctx.T("moments.err.name"), true);
                    if ((await store.AlbumsAsync(ct)).Any(o => o.Id != a.Id && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
                        return PluginResult.Redirect(here, ctx.T("moments.albums.exists"), true);
                    await store.Albums.UpdateAsync(a.Id, new Dictionary<string, object?> { ["name"] = name }, ct);
                    return PluginResult.Redirect(here, ctx.T("moments.saved"));
                }
                case "adopt":
                {
                    // the photos that are in no album yet — all of them, or only the hosts' or only the guests'
                    var who = ctx.Request.F("who");
                    var adopt = (await store.PhotosAsync(ct: ct)).Where(p => p.AlbumId == 0 && (who == "gaeste" ? p.GuestId > 0 : who == "gastgeber" ? p.GuestId == 0 : true)).ToList();
                    foreach (var p in adopt) await store.Photos.UpdateAsync(p.Id, new Dictionary<string, object?> { ["album_id"] = a.Id }, ct);
                    return PluginResult.Redirect(here, M.Count(ctx, adopt.Count, "moments.album.adopted."));
                }
                case "delete":
                    await store.DeleteAlbumAsync(a.Id, ct);
                    return PluginResult.Redirect(Root + "albums", ctx.T("moments.album.deleted", a.Name));
            }
        }
        var photos = await store.PhotosAsync(ct: ct);
        var mine = photos.Where(p => p.AlbumId == a.Id).OrderBy(p => p.Taken, StringComparer.Ordinal).ToList();
        var loose = photos.Where(p => p.AlbumId == 0).ToList();
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(M.Count(ctx, mine.Count, "moments.count.photo.") + (a.Guests ? " · " + ctx.T("moments.album.guestsLand") : ""), a.Name,
                Ui.Button(ctx.T("moments.album.viewInGallery"), B + "?album=" + a.Id, UiButtonKind.Ghost, "ti-external-link") with { NewTab = true },
                Ui.Button(ctx.T("moments.album.uploadHere"), B + "/hochladen?album=" + a.Id, UiButtonKind.Primary, "ti-camera-plus") with { NewTab = true }),
            Ui.Columns(
                new UiNode?[]
                {
                    Ui.Card(ctx.T("moments.album"), Ui.Form(
                        Ui.Hidden("action", "save"),
                        Ui.Input("name", ctx.T("moments.name"), a.Name) with { Required = true, MaxLength = 60 },
                        Ui.FormActions(Ui.Submit(ctx.T("moments.save"))))),
                    Ui.Card(ctx.T("moments.photos") + " (" + mine.Count + ")", mine.Count == 0 ? Ui.Muted(ctx.T("moments.noneYet")) :
                        Ui.Grid(90, mine.Take(24).Select(p => (UiNode?)Ui.Img(Thumb(p.Id), p.FileName, 90)).ToArray()))
                },
                new UiNode?[]
                {
                    loose.Count == 0 ? null : Ui.Card(ctx.T("moments.album.loose") + " (" + loose.Count + ")",
                        Ui.Muted(ctx.T("moments.album.looseNote")),
                        Ui.Inline(
                            Act(ctx.T("moments.album.adoptAll", M.N(loose.Count)), new Dictionary<string, string> { ["action"] = "adopt", ["who"] = "" }, icon: "ti-arrow-bar-to-down"),
                            loose.Any(p => p.GuestId > 0) && loose.Any(p => p.GuestId == 0)
                                ? Act(ctx.T("moments.album.adoptGuests"), new Dictionary<string, string> { ["action"] = "adopt", ["who"] = "gaeste" }, icon: "ti-users") : null,
                            loose.Any(p => p.GuestId > 0) && loose.Any(p => p.GuestId == 0)
                                ? Act(ctx.T("moments.album.adoptHosts"), new Dictionary<string, string> { ["action"] = "adopt", ["who"] = "gastgeber" }, icon: "ti-heart") : null)),
                    Ui.Card(ctx.T("moments.delete"), Ui.Muted(ctx.T("moments.album.deleteNote")),
                        Act(ctx.T("moments.album.deleteButton"), new Dictionary<string, string> { ["action"] = "delete" }, UiButtonKind.Danger, ctx.T("moments.album.deleteConfirm", a.Name), "ti-trash"))
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
                if (login is null) { skipped.Add(name + " (" + ctx.T("moments.login.user") + ")"); continue; }
                if (login.Length > 0 && logins.Contains(login)) { skipped.Add(name + " (" + ctx.T("moments.bulk.userTaken") + ")"); continue; }
                if (login.Length > 0 && password.Length is < Passwords.MinLength or > Passwords.MaxLength) { skipped.Add(name + " (" + ctx.T("moments.bulk.passwordShort") + ")"); continue; }
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
            var msg = added == 0 ? ctx.T("moments.bulk.none") : M.Count(ctx, added, "moments.bulk.added.");
            if (skipped.Count > 0) msg += " " + ctx.T("moments.bulk.skipped", string.Join(", ", skipped.Take(10)) + (skipped.Count > 10 ? " …" : ""));
            return PluginResult.Redirect(Root + "guests", msg, skipped.Count > 0 && added == 0);
        }
        var guests = await store.GuestsAsync(ct);
        var photos = await store.PhotosAsync(ct: ct);
        var count = photos.GroupBy(p => p.GuestId).ToDictionary(g => g.Key, g => g.Count());
        var signedIn = guests.Count(g => g.Visits > 0);
        var codes = mode != MomentsSettings.ModePassword;
        var showLogins = mode != MomentsSettings.ModeCode || guests.Any(g => g.Login.Length > 0);
        var columns = new List<UiColumn> { Ui.Column(ctx.T("moments.guest.household")), Ui.Column(ctx.T("moments.guest.people")), NotOnPhone(Ui.Column(ctx.T("moments.guest.table"))) };
        if (codes) columns.Add(Ui.Column(ctx.T("moments.cards.code")));
        if (showLogins) columns.Add(Ui.Column(ctx.T("moments.login.user")));
        columns.AddRange(new[] { NotOnPhone(Ui.Column(ctx.T("moments.guest.lastSeen"))), NotOnPhone(Ui.Column(ctx.T("moments.photos"), right: true)), Ui.Column(ctx.T("moments.col.status")) });
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(ctx.T(mode switch
                {
                    MomentsSettings.ModePassword => "moments.guests.introPassword",
                    MomentsSettings.ModeBoth => "moments.guests.introBoth",
                    _ => "moments.guests.introCode"
                }), ctx.T("moments.guests"),
                guests.Count > 0 ? Ui.Button(ctx.T("moments.cards.title"), B + "/karten", UiButtonKind.Ghost, "ti-printer") with { NewTab = true } : null),
            Ui.Table(columns,
                guests.Select(g =>
                {
                    var cells = new List<object?> { Ui.Link(g.Name, Root + "guests/" + g.Id), string.Join(", ", g.PeopleList), g.Table };
                    if (codes) cells.Add(g.CodeShown);
                    if (showLogins) cells.Add(g.Login.Length == 0 ? "–" : g.Login + (g.HasPassword ? "" : " (" + ctx.T("moments.guests.noPassword") + ")"));
                    cells.Add(g.LastSeen is null ? ctx.T("moments.guests.never") : M.Stamp(g.LastSeen));
                    cells.Add(count.TryGetValue(g.Id, out var n) ? M.N(n) : "–");
                    cells.Add(!g.Active ? Ui.Badge(ctx.T("moments.guests.locked"), UiTone.Error) : g.Visits > 0 ? Ui.Badge(ctx.T("moments.guests.signedIn"), UiTone.Success) : Ui.Badge(ctx.T("moments.guests.notYet"), UiTone.Muted));
                    return Ui.RowFor(g.Name + " " + g.People + " " + g.Table + " " + g.Code + " " + g.Login, cells.ToArray());
                }),
                searchPlaceholder: ctx.T("moments.guests.search"), pageSize: 100, emptyText: ctx.T("moments.guests.empty")) with
            {
                Below = new UiNode[] { Ui.Button(ctx.T("moments.guest.new"), Root + "guests/new", UiButtonKind.Primary, "ti-plus") }
            },
            Ui.Muted(ctx.T("moments.guests.visited", M.N(signedIn), M.N(guests.Count))),
            Ui.Card(ctx.T("moments.bulk.title"), Ui.Form(
                Ui.Hidden("action", "bulk"),
                Ui.TextArea("lines", ctx.T("moments.bulk.lines"), "", 8) with { Help = ctx.T("moments.bulk.help") },
                Ui.FormActions(Ui.Submit(ctx.T("moments.create")))))
        ), ctx.T("moments.guests"));
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
                    if (name.Length == 0) return PluginResult.Redirect(here, ctx.T("moments.err.name"), true);
                    var all = await store.GuestsAsync(ct);
                    // the code: kept, typed by the hosts ("START12345"), or made in the chosen format for a new household
                    var typedCode = ctx.Request.F("code");
                    var code = Codes.Normalize(typedCode);
                    var codeChanged = typedCode.Trim().Length > 0 && code != (g?.Code ?? "");
                    if (codeChanged)
                    {
                        if (Codes.Problem(ctx, typedCode) is string problem) return PluginResult.Redirect(here, problem, true);
                        if (all.Any(x => x.Id != (g?.Id ?? 0) && x.Code == code)) return PluginResult.Redirect(here, ctx.T("moments.guest.codeTaken"), true);
                    }
                    // user name + password: the password only when one is typed (empty = keep); no user name = no password either
                    var login = Passwords.CleanLogin(ctx.Request.F("login"));
                    if (login is null) return PluginResult.Redirect(here, ctx.T("moments.guest.loginRules"), true);
                    if (login.Length > 0 && all.Any(x => x.Id != (g?.Id ?? 0) && string.Equals(x.Login, login, StringComparison.OrdinalIgnoreCase)))
                        return PluginResult.Redirect(here, ctx.T("moments.guest.loginTaken"), true);
                    var password = ctx.Request.F("password");
                    if (password.Length > 0 && password.Length is < Passwords.MinLength or > Passwords.MaxLength)
                        return PluginResult.Redirect(here, ctx.T("moments.guest.passwordShort", M.N(Passwords.MinLength)), true);
                    if (login.Length > 0 && password.Length == 0 && (g?.PasswordHash ?? "").Length == 0)
                        return PluginResult.Redirect(here, ctx.T("moments.guest.passwordNeeded"), true);

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
                    return PluginResult.Redirect(Root + "guests/" + id, ctx.T(isNew ? "moments.guest.created" : signOut ? "moments.guest.savedSignedOut" : "moments.saved"));
                }
                case "newcode" when g is not null:
                    // a new code AND a new account: every phone signed in with the old one is out; the household signs in again with the new code
                    await store.Guests.UpdateAsync(g.Id, new Dictionary<string, object?> { ["code"] = await store.NewCodeAsync(MomentsSettings.CodeFormat(ctx), ct), ["member_id"] = 0, ["account"] = "" }, ct);
                    if (g.MemberId > 0) await ctx.Members.DeleteAsync(g.MemberId, ct);
                    return PluginResult.Redirect(Root + "guests/" + g.Id, ctx.T("moments.guest.newCodeDone"));
                case "delete" or "deleteall" when g is not null:
                    await store.EraseGuestAsync(ctx.Files, g.Id, ctx.Request.Action == "deleteall", ct);
                    await store.Guests.DeleteAsync(g.Id, ct);
                    if (g.MemberId > 0) await ctx.Members.DeleteAsync(g.MemberId, ct);
                    return PluginResult.Redirect(Root + "guests", ctx.T("moments.guest.removed", g.Name));
            }
        }
        var photos = g is null ? Array.Empty<Photo>() : await store.PhotosOfGuestAsync(g.Id, ct);
        var tables = (await store.GuestsAsync(ct)).Select(x => x.Table).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
        var link = ctx.Site.Origin.TrimEnd('/') + B + "/z/" + (g?.Code ?? "");
        var (codeLength, _) = MomentsSettings.CodeFormat(ctx);
        var form = Ui.Card(ctx.T(isNew ? "moments.guest.new" : "moments.guest.household"), Ui.Form(
            Ui.Hidden("action", "save"),
            Ui.Input("name", ctx.T("moments.name"), g?.Name ?? "") with { Required = true, Placeholder = ctx.T("moments.guest.namePlaceholder"), Help = ctx.T("moments.guest.nameHelp") },
            Ui.Input("people", ctx.T("moments.guest.people"), g?.People ?? "") with { Placeholder = ctx.T("moments.guest.peoplePlaceholder"), Help = ctx.T("moments.guest.peopleHelp") },
            Ui.Grid(220,
                Ui.Input("table", ctx.T("moments.guest.table"), g?.Table ?? "") with { Help = tables.Count > 0 ? ctx.T("moments.guest.tablesSoFar", string.Join(", ", tables)) : ctx.T("moments.guest.tableHelp") },
                isNew ? null : Ui.Checkbox("active", ctx.T("moments.guest.active"), g!.Active)),
            Ui.Input("code", ctx.T("moments.guest.code"), g?.CodeShown ?? "") with
            {
                Placeholder = isNew ? ctx.T("moments.guest.codePlaceholder", M.N(codeLength)) : "",
                Help = ctx.T("moments.guest.codeHelp", M.N(Codes.MinLength)) + (isNew ? "" : " " + ctx.T("moments.guest.codeHelpChange"))
            },
            Ui.Grid(220,
                Ui.Input("login", ctx.T("moments.login.user"), g?.Login ?? "") with { Placeholder = ctx.T("moments.guest.loginPlaceholder"), Help = ctx.T(mode == MomentsSettings.ModeCode ? "moments.guest.loginHelpOff" : "moments.guest.loginHelp") },
                Ui.Input("password", ctx.T(g?.HasPassword == true ? "moments.guest.newPassword" : "moments.login.password"), "", UiInputType.Password) with
                {
                    Placeholder = g?.HasPassword == true ? ctx.T("moments.guest.passwordKeep") : "",
                    Help = ctx.T("moments.guest.passwordHelp", M.N(Passwords.MinLength))
                }),
            Ui.TextArea("note", ctx.T("moments.guest.note"), g?.Note ?? "", 3),
            Ui.FormActions(Ui.Submit(ctx.T(isNew ? "moments.create" : "moments.save")))));
        if (g is null) return PluginResult.Page(Ui.Stack(Ui.PageHead(ctx.T("moments.guest.newIntro"), ctx.T("moments.guest.new")), form), ctx.T("moments.guest.new"));
        var access = new List<(string, string)>();
        if (mode != MomentsSettings.ModePassword) { access.Add((ctx.T("moments.cards.code"), g.CodeShown)); access.Add((ctx.T("moments.guest.link"), link)); }
        if (mode != MomentsSettings.ModeCode || g.Login.Length > 0) { access.Add((ctx.T("moments.login.user"), g.Login.Length > 0 ? g.Login : "–")); access.Add((ctx.T("moments.login.password"), g.HasPassword ? ctx.T("moments.guest.passwordSet") : "–")); }
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(g.Visits > 0 ? ctx.T("moments.guest.lastSeenAt", M.Stamp(g.LastSeen)) + " · " + M.Count(ctx, g.Visits, "moments.count.visit.") : ctx.T("moments.guest.neverVisited"), g.Name),
            Ui.Columns(
                new UiNode?[] { form },
                new UiNode?[]
                {
                    Ui.Card(ctx.T("moments.guest.access"),
                        Ui.KeyValue(access.ToArray()),
                        Ui.Muted(ctx.T(mode == MomentsSettings.ModePassword ? "moments.guest.accessPassword" : "moments.guest.accessCode")),
                        Ui.Inline(
                            Ui.Button(ctx.T("moments.guest.card"), B + "/karten?ids=" + g.Id, UiButtonKind.Ghost, "ti-id-badge-2") with { NewTab = true },
                            Ui.Button(ctx.T("moments.guest.whatsapp"), MomentsCards.WhatsAppUrl(ctx, g), UiButtonKind.Ghost, "ti-brand-whatsapp") with { NewTab = true },
                            mode != MomentsSettings.ModePassword
                                ? Act(ctx.T("moments.guest.newCode"), new Dictionary<string, string> { ["action"] = "newcode" }, UiButtonKind.Ghost, ctx.T("moments.guest.newCodeConfirm"), "ti-refresh")
                                : null)),
                    Ui.Card(ctx.T("moments.photos") + " (" + photos.Count + ")", photos.Count == 0 ? Ui.Muted(ctx.T("moments.guest.noPhotos")) :
                        Ui.Grid(90, photos.Take(12).Select(p => (UiNode?)Ui.Img(Thumb(p.Id), p.FileName, 90)).ToArray())),
                    Ui.Card(ctx.T("moments.remove"),
                        Ui.Muted(ctx.T("moments.guest.removeNote")),
                        Ui.Inline(
                            Act(ctx.T("moments.remove"), new Dictionary<string, string> { ["action"] = "delete" }, UiButtonKind.Danger, ctx.T("moments.guest.removeConfirm", g.Name), "ti-user-minus"),
                            photos.Count > 0 ? Act(ctx.T("moments.guest.removeAll"), new Dictionary<string, string> { ["action"] = "deleteall" }, UiButtonKind.Danger, ctx.T("moments.guest.removeAllConfirm", g.Name, M.N(photos.Count)), "ti-trash") : null))
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
            return PluginResult.Redirect(Root + "tags", added == 0 ? ctx.T("moments.tags.nothingNew") : M.Count(ctx, added, "moments.tags.added."));
        }
        var tags = await store.TagsAsync(ct);
        var used = (await store.PhotoTagsAsync(ct)).GroupBy(m => m.TagId).ToDictionary(g => g.Key, g => g.Count());
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(ctx.T("moments.tags.intro"), ctx.T("moments.nav.tags")),
            Ui.Table(new[] { Ui.Column(ctx.T("moments.name")), Ui.Column(ctx.T("moments.tags.kind")), Ui.Column(ctx.T("moments.photos"), right: true) },
                tags.OrderBy(t => t.Kind == MomentsStore.Person ? 1 : 0).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => Ui.RowFor(t.Name,
                    Ui.Link(t.Name, Root + "tags/" + t.Id), t.Kind == MomentsStore.Person ? Ui.Badge(ctx.T("moments.tags.person"), UiTone.Info) : Ui.Badge(ctx.T("moments.tags.keyword")), used.TryGetValue(t.Id, out var n) ? M.N(n) : "0")),
                searchPlaceholder: ctx.T("moments.search"), pageSize: 100, emptyText: ctx.T("moments.tags.empty")),
            Ui.Card(ctx.T("moments.tags.preset"), Ui.Form(
                Ui.Hidden("action", "add"),
                Ui.TextArea("names", ctx.T("moments.tags.newEntries"), "", 3) with { Help = ctx.T("moments.tags.newEntriesHelp") },
                Ui.Select("kind", ctx.T("moments.tags.kind"), MomentsStore.Topic, Ui.Option(MomentsStore.Topic, ctx.T("moments.tags.keyword")), Ui.Option(MomentsStore.Person, ctx.T("moments.tags.person"))),
                Ui.FormActions(Ui.Submit(ctx.T("moments.create")))))
        ), ctx.T("moments.nav.tags"));
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
                    if (name.Length < 2) return PluginResult.Redirect(Root + "tags/" + t.Id, ctx.T("moments.err.name"), true);
                    if (tags.FirstOrDefault(x => x.Id != t.Id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is Tag twin)
                    {
                        await store.MergeTagAsync(t.Id, twin.Id, ct);
                        return PluginResult.Redirect(Root + "tags/" + twin.Id, ctx.T("moments.tags.mergedInto", t.Name, twin.Name));
                    }
                    await store.Tags.UpdateAsync(t.Id, new Dictionary<string, object?> { ["name"] = name, ["kind"] = ctx.Request.F("kind") == MomentsStore.Person ? MomentsStore.Person : MomentsStore.Topic }, ct);
                    return PluginResult.Redirect(Root + "tags/" + t.Id, ctx.T("moments.saved"));
                }
                case "merge" when long.TryParse(ctx.Request.F("into"), out var into) && tags.Any(x => x.Id == into && x.Id != t.Id):
                    await store.MergeTagAsync(t.Id, into, ct);
                    return PluginResult.Redirect(Root + "tags/" + into, ctx.T("moments.tags.merged"));
                case "delete":
                    await store.DeleteTagAsync(t.Id, ct);
                    return PluginResult.Redirect(Root + "tags", ctx.T("moments.tags.deleted", t.Name));
            }
        }
        var marks = (await store.PhotoTagsAsync(ct)).Where(m => m.TagId == t.Id).ToList();
        var others = tags.Where(x => x.Id != t.Id).ToList();
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(M.Count(ctx, marks.Count, "moments.tags.markedCount."), t.Name,
                Ui.Button(ctx.T("moments.admin.viewInArea"), B + "?tag=" + t.Id, UiButtonKind.Ghost, "ti-external-link") with { NewTab = true }),
            Ui.Columns(
                new UiNode?[]
                {
                    Ui.Card(Ui.Form(
                        Ui.Hidden("action", "save"),
                        Ui.Input("name", ctx.T("moments.name"), t.Name) with { Required = true, MaxLength = 40, Help = ctx.T("moments.tags.nameHelp") },
                        Ui.Select("kind", ctx.T("moments.tags.kind"), t.Kind, Ui.Option(MomentsStore.Topic, ctx.T("moments.tags.keyword")), Ui.Option(MomentsStore.Person, ctx.T("moments.tags.person"))),
                        Ui.FormActions(Ui.Submit(ctx.T("moments.save"))))),
                    marks.Count == 0 ? null : Ui.Card(ctx.T("moments.photos"), Ui.Grid(90, marks.Take(24).Select(m => (UiNode?)Ui.Img(Thumb(m.PhotoId), "", 90)).ToArray()))
                },
                new UiNode?[]
                {
                    others.Count == 0 ? null : Ui.Card(ctx.T("moments.tags.merge"), Ui.Form(
                        Ui.Hidden("action", "merge"),
                        Ui.Select("into", ctx.T("moments.tags.mergeInto"), "", others.Select(x => Ui.Option(M.N(x.Id), x.Name)).ToArray()) with { Help = ctx.T("moments.tags.mergeHelp") },
                        Ui.FormActions(Ui.Submit(ctx.T("moments.tags.merge"), UiButtonKind.Ghost)))),
                    Ui.Card(ctx.T("moments.delete"), Ui.Muted(ctx.T("moments.tags.deleteNote")),
                        Act(ctx.T("moments.delete"), new Dictionary<string, string> { ["action"] = "delete" }, UiButtonKind.Danger, ctx.T("moments.tags.deleteConfirm", t.Name), "ti-trash"))
                })
        ), t.Name);
    }
}
