namespace MatCMS.Plugin.Moments;

/// <summary>
/// A household of guests: one access code, one site account (made on the first sign-in, never with a real address — the plugin sends no mail), one name
/// to greet ("Zoe &amp; Fabian"). <see cref="People"/> are the first names of who belongs to it, offered when somebody marks who is on a photo.
/// <see cref="Login"/> and <see cref="PasswordHash"/> are the second way in (Benutzername + Passwort, given by the hosts); empty = none.
/// </summary>
public sealed record Guest(long Id, string Name, string People, string Code, long MemberId, string Table, string Note, bool Active, DateTime? Created,
    DateTime? LastSeen, long Visits, string Account, string Login = "", string PasswordHash = "")
{
    public IReadOnlyList<string> PeopleList => MomentsStore.SplitPeople(People);
    /// <summary>The code as printed, in groups ("K7XM-4QPA").</summary>
    public string CodeShown => Codes.Shown(Code);
    public bool HasPassword => Login.Length > 0 && PasswordHash.Length > 0;
}

/// <summary>
/// A photo: the original (GPS removed), a large view and a small tile — the two smaller ones made by the uploader's browser, since the plugin has no image
/// library. <see cref="GuestId"/> 0 = the hosts (uploaded in the back office or by an administrator on the site).
/// </summary>
public sealed record Photo(long Id, long GuestId, string File, string View, string Thumb, string FileName, long Size, int Width, int Height, string Taken,
    string Caption, string Status, bool Featured, DateTime? Created, DateTime? ApprovedAt, long AlbumId = 0)
{
    public bool Approved => Status == MomentsStore.Approved;
    public bool Pending => Status == MomentsStore.Pending;
}

/// <summary>
/// An album: where a photo comes from ("Fotograf", "Gästebilder", "Fotobox") — every photo is in at most ONE, unlike keywords. The gallery opens with
/// the albums in the hosts' order (<see cref="Sort"/>); <see cref="Guests"/> marks the one a guest's upload lands in by itself.
/// </summary>
public sealed record Album(long Id, string Name, int Sort, bool Guests);

/// <summary>A keyword or a person a photo is marked with. <see cref="Kind"/>: <see cref="MomentsStore.Person"/> or <see cref="MomentsStore.Topic"/>.</summary>
public sealed record Tag(long Id, string Name, string Kind);

/// <summary>A mark on a photo, with the household that set it (0 = the hosts).</summary>
public sealed record PhotoTag(long Id, long PhotoId, long TagId, long By);

/// <summary>A comment under a photo.</summary>
public sealed record Comment(long Id, long PhotoId, long GuestId, string Text, string Status, DateTime? Created)
{
    public bool Visible => Status == MomentsStore.Visible;
}

/// <summary>
/// The plugin's data. Tables are declared (the host creates and extends them, never drops anything); every value goes in as a parameter. Photos are
/// private plugin files, served only through the plugin's own routes after the visitor was checked.
/// </summary>
public sealed class MomentsStore(IPluginData data)
{
    public const string TGuests = "guests", TPhotos = "photos", TTags = "tags", TPhotoTags = "photo_tags", TComments = "comments", THearts = "hearts", TMeta = "meta",
        TAlbums = "albums";

    // stored values — never renamed
    public const string Pending = "pending", Approved = "approved", Hidden = "hidden", Visible = "visible";
    public const string Person = "person", Topic = "topic";

    public static void Declare(IDataBuilder b)
    {
        b.Table(TGuests, t => t.Text("name", required: true).Text("people").Text("code", required: true).Int("member_id", def: "0").Text("table_name").Text("note")
            .Bool("active", def: "1").Timestamp("created").Timestamp("last_seen").Int("visits", def: "0").Text("account")
            // 1.1: Benutzername + Passwort (the hash only). The host adds the columns on load; rows from 1.0 read them as "".
            .Text("login").Text("password").UniqueIndex("code").Index("member_id"));
        b.Table(TPhotos, t => t.Int("guest_id", def: "0").Text("file", required: true).Text("view").Text("thumb").Text("file_name").Int("size", def: "0")
            .Int("width", def: "0").Int("height", def: "0").Text("taken").Text("caption").Text("status", required: true).Bool("featured", def: "0")
            .Timestamp("created").Timestamp("approved_at")
            // 1.1: the album (0 = none)
            .Int("album_id", def: "0").Index("status").Index("guest_id").Index("taken").Index("album_id"));
        b.Table(TTags, t => t.Text("name", required: true).Text("kind", required: true).Timestamp("created").Index("name"));
        b.Table(TPhotoTags, t => t.Int("photo_id", required: true).Int("tag_id", required: true).Int("by", def: "0").Timestamp("created")
            .UniqueIndex("photo_id", "tag_id").Index("tag_id"));
        b.Table(TComments, t => t.Int("photo_id", required: true).Int("guest_id", def: "0").Text("text", required: true).Text("status", required: true)
            .Timestamp("created").Index("photo_id").Index("status"));
        b.Table(THearts, t => t.Int("photo_id", required: true).Int("guest_id", required: true).Timestamp("created").UniqueIndex("photo_id", "guest_id"));
        b.Table(TMeta, t => t.Text("name", required: true).Text("value").UniqueIndex("name"));
        b.Table(TAlbums, t => t.Text("name", required: true).Int("sort", def: "0").Bool("guests", def: "0").Timestamp("created"));
    }

    public IDataTable Guests => data.Table(TGuests);
    public IDataTable Photos => data.Table(TPhotos);
    public IDataTable Tags => data.Table(TTags);
    public IDataTable PhotoTags => data.Table(TPhotoTags);
    public IDataTable Comments => data.Table(TComments);
    public IDataTable Hearts => data.Table(THearts);
    public IDataTable Meta => data.Table(TMeta);
    public IDataTable Albums => data.Table(TAlbums);

    // ---- mapping ------------------------------------------------------------------------------------------------

    /// <summary>"Zoe, Fabian" / "Zoe + Fabian" / "Zoe und Fabian" → the names.</summary>
    public static IReadOnlyList<string> SplitPeople(string s) =>
        (s ?? "").Replace(" und ", ",").Replace("+", ",").Replace("&", ",").Replace(";", ",").Replace("\n", ",")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length is > 0 and <= 60 && x != "???").Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();

    private static Guest G(DataRow r) => new(r.Id, r.Text("name"), r.Text("people"), r.Text("code"), r.Int("member_id"), r.Text("table_name"), r.Text("note"),
        r.Bool("active"), r.Timestamp("created"), r.Timestamp("last_seen"), r.Int("visits"), r.Text("account"), r.Text("login"), r.Text("password"));
    private static Photo P(DataRow r) => new(r.Id, r.Int("guest_id"), r.Text("file"), r.Text("view"), r.Text("thumb"), r.Text("file_name"), r.Int("size"),
        (int)r.Int("width"), (int)r.Int("height"), r.Text("taken"), r.Text("caption"), r.Text("status"), r.Bool("featured"), r.Timestamp("created"), r.Timestamp("approved_at"), r.Int("album_id"));
    private static Tag T(DataRow r) => new(r.Id, r.Text("name"), r.Text("kind"));
    private static PhotoTag PT(DataRow r) => new(r.Id, r.Int("photo_id"), r.Int("tag_id"), r.Int("by"));
    private static Comment C(DataRow r) => new(r.Id, r.Int("photo_id"), r.Int("guest_id"), r.Text("text"), r.Text("status"), r.Timestamp("created"));

    // ---- guests -------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Guest>> GuestsAsync(CancellationToken ct = default) =>
        (await Guests.QueryAsync(DataQuery.Everything.OrderBy("name"), ct)).Select(G).ToList();
    public async Task<Guest?> GuestAsync(long id, CancellationToken ct = default) => id > 0 && await Guests.GetAsync(id, ct) is DataRow r ? G(r) : null;
    public async Task<Guest?> GuestByCodeAsync(string code, CancellationToken ct = default) =>
        code.Length == 0 ? null : await Guests.FindAsync(DataQuery.Everything.Eq("code", code), ct) is DataRow r ? G(r) : null;
    public async Task<Guest?> GuestOfMemberAsync(long memberId, CancellationToken ct = default) =>
        memberId <= 0 ? null : await Guests.FindAsync(DataQuery.Everything.Eq("member_id", memberId), ct) is DataRow r ? G(r) : null;

    /// <summary>The household with this user name, compared without case (a party has a few dozen households — read in memory).</summary>
    public async Task<Guest?> GuestByLoginAsync(string login, CancellationToken ct = default) =>
        login.Length == 0 ? null : (await GuestsAsync(ct)).FirstOrDefault(g => g.Login.Length > 0 && string.Equals(g.Login, login, StringComparison.OrdinalIgnoreCase));

    /// <summary>A fresh code nobody has, in the shape the hosts chose (checked against the table, so a collision is retried, not stored).</summary>
    public async Task<string> NewCodeAsync((int Length, string Chars) format, CancellationToken ct = default)
    {
        for (var i = 0; i < 50; i++)
        {
            var c = Codes.New(format.Length, format.Chars);
            if (await Guests.FindAsync(DataQuery.Everything.Eq("code", c), ct) is null) return c;
        }
        throw new InvalidOperationException("Kein freier Zugangscode gefunden – stellt in den Einstellungen längere Codes ein.");
    }

    public async Task<long> SaveGuestAsync(long id, string name, string people, string table, string note, bool active, (int Length, string Chars) format, CancellationToken ct = default)
    {
        var v = new Dictionary<string, object?> { ["name"] = name, ["people"] = people, ["table_name"] = table, ["note"] = note, ["active"] = active };
        if (id > 0) { await Guests.UpdateAsync(id, v, ct); return id; }
        v["code"] = await NewCodeAsync(format, ct);
        v["created"] = DateTime.UtcNow;
        v["login"] = "";
        v["password"] = "";
        return await Guests.InsertAsync(v, ct);
    }

    // ---- photos -------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Photo>> PhotosAsync(string? status = null, CancellationToken ct = default)
    {
        var q = DataQuery.Everything.OrderBy("taken").OrderBy("id");
        if (status is not null) q = q.Eq("status", status);
        return (await Photos.QueryAsync(q, ct)).Select(P).ToList();
    }
    public async Task<Photo?> PhotoAsync(long id, CancellationToken ct = default) => id > 0 && await Photos.GetAsync(id, ct) is DataRow r ? P(r) : null;
    public async Task<IReadOnlyList<Photo>> PhotosOfGuestAsync(long guestId, CancellationToken ct = default) =>
        (await Photos.QueryAsync(DataQuery.Everything.Eq("guest_id", guestId).OrderBy("created", descending: true), ct)).Select(P).ToList();
    public Task<long> PendingCountAsync(CancellationToken ct = default) => Photos.CountAsync(DataQuery.Everything.Eq("status", Pending), ct);

    public Task SetStatusAsync(Photo p, string status, CancellationToken ct = default) =>
        Photos.UpdateAsync(p.Id, new Dictionary<string, object?> { ["status"] = status, ["approved_at"] = status == Approved ? p.ApprovedAt ?? DateTime.UtcNow : p.ApprovedAt }, ct);

    /// <summary>Removes the photo with everything hanging on it: its files, marks, comments and hearts.</summary>
    public async Task DeletePhotoAsync(IPluginFiles files, Photo p, CancellationToken ct = default)
    {
        foreach (var f in new[] { p.File, p.View, p.Thumb }.Where(f => f.Length > 0).Distinct()) await files.DeleteAsync(f, ct);
        await PhotoTags.DeleteWhereAsync(DataQuery.Everything.Eq("photo_id", p.Id), ct);
        await Comments.DeleteWhereAsync(DataQuery.Everything.Eq("photo_id", p.Id), ct);
        await Hearts.DeleteWhereAsync(DataQuery.Everything.Eq("photo_id", p.Id), ct);
        await Photos.DeleteAsync(p.Id, ct);
    }

    // ---- albums -------------------------------------------------------------------------------------------------

    private static Album A(DataRow r) => new(r.Id, r.Text("name"), (int)r.Int("sort"), r.Bool("guests"));

    /// <summary>The albums in the hosts' order.</summary>
    public async Task<IReadOnlyList<Album>> AlbumsAsync(CancellationToken ct = default) =>
        (await Albums.QueryAsync(DataQuery.Everything.OrderBy("sort").OrderBy("id"), ct)).Select(A).ToList();
    public async Task<Album?> AlbumAsync(long id, CancellationToken ct = default) => id > 0 && await Albums.GetAsync(id, ct) is DataRow r ? A(r) : null;

    /// <summary>Where a guest's upload goes by itself (0 = no album).</summary>
    public async Task<long> GuestAlbumAsync(CancellationToken ct = default) => (await AlbumsAsync(ct)).FirstOrDefault(a => a.Guests)?.Id ?? 0;

    /// <summary>A new album at the end of the order.</summary>
    public async Task<long> AddAlbumAsync(string name, CancellationToken ct = default)
    {
        var all = await AlbumsAsync(ct);
        return await Albums.InsertAsync(new Dictionary<string, object?> { ["name"] = name, ["sort"] = all.Count == 0 ? 1 : all.Max(a => a.Sort) + 1, ["guests"] = false, ["created"] = DateTime.UtcNow }, ct);
    }

    /// <summary>Moves an album one place up or down (and numbers all of them anew, so equal numbers from an older order cannot stick).</summary>
    public async Task MoveAlbumAsync(long id, int step, CancellationToken ct = default)
    {
        var all = (await AlbumsAsync(ct)).ToList();
        var i = all.FindIndex(a => a.Id == id);
        var j = i + Math.Sign(step);
        if (i < 0 || j < 0 || j >= all.Count) return;
        (all[i], all[j]) = (all[j], all[i]);
        for (var k = 0; k < all.Count; k++) await Albums.UpdateAsync(all[k].Id, new Dictionary<string, object?> { ["sort"] = k + 1 }, ct);
    }

    /// <summary>Makes this album the one guests' uploads land in (only one can be) and takes in the guests' photos that are in no album yet. Answers how many.</summary>
    public async Task<int> SetGuestAlbumAsync(long id, CancellationToken ct = default)
    {
        foreach (var a in await AlbumsAsync(ct)) if (a.Guests != (a.Id == id)) await Albums.UpdateAsync(a.Id, new Dictionary<string, object?> { ["guests"] = a.Id == id }, ct);
        if (id <= 0) return 0;
        var loose = (await PhotosAsync(ct: ct)).Where(p => p.GuestId > 0 && p.AlbumId == 0).ToList();
        foreach (var p in loose) await Photos.UpdateAsync(p.Id, new Dictionary<string, object?> { ["album_id"] = id }, ct);
        return loose.Count;
    }

    /// <summary>Removes the album; its photos stay (in no album).</summary>
    public async Task DeleteAlbumAsync(long id, CancellationToken ct = default)
    {
        await Photos.UpdateWhereAsync(DataQuery.Everything.Eq("album_id", id), new Dictionary<string, object?> { ["album_id"] = 0 }, ct);
        await Albums.DeleteAsync(id, ct);
    }

    // ---- tags ---------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Tag>> TagsAsync(CancellationToken ct = default) =>
        (await Tags.QueryAsync(DataQuery.Everything.OrderBy("name"), ct)).Select(T).ToList();
    public async Task<Tag?> TagAsync(long id, CancellationToken ct = default) => id > 0 && await Tags.GetAsync(id, ct) is DataRow r ? T(r) : null;
    public async Task<IReadOnlyList<PhotoTag>> PhotoTagsAsync(CancellationToken ct = default) =>
        (await PhotoTags.QueryAsync(DataQuery.Everything, ct)).Select(PT).ToList();
    public async Task<IReadOnlyList<PhotoTag>> TagsOfPhotoAsync(long photoId, CancellationToken ct = default) =>
        (await PhotoTags.QueryAsync(DataQuery.Everything.Eq("photo_id", photoId), ct)).Select(PT).ToList();

    /// <summary>The tag with this name (compared without case), or null.</summary>
    public async Task<Tag?> TagByNameAsync(string name, CancellationToken ct = default) =>
        (await TagsAsync(ct)).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public async Task<Tag> EnsureTagAsync(string name, string kind, CancellationToken ct = default)
    {
        if (await TagByNameAsync(name, ct) is Tag known) return known;
        var id = await Tags.InsertAsync(new Dictionary<string, object?> { ["name"] = name, ["kind"] = kind, ["created"] = DateTime.UtcNow }, ct);
        return new Tag(id, name, kind);
    }

    /// <summary>Marks the photo (once); false when it already was.</summary>
    public async Task<bool> AddTagAsync(long photoId, long tagId, long by, CancellationToken ct = default)
    {
        if (await PhotoTags.FindAsync(DataQuery.Everything.Eq("photo_id", photoId).Eq("tag_id", tagId), ct) is not null) return false;
        await PhotoTags.InsertAsync(new Dictionary<string, object?> { ["photo_id"] = photoId, ["tag_id"] = tagId, ["by"] = by, ["created"] = DateTime.UtcNow }, ct);
        return true;
    }

    public async Task DeleteTagAsync(long tagId, CancellationToken ct = default)
    {
        await PhotoTags.DeleteWhereAsync(DataQuery.Everything.Eq("tag_id", tagId), ct);
        await Tags.DeleteAsync(tagId, ct);
    }

    /// <summary>Moves every mark of <paramref name="from"/> to <paramref name="into"/> (a photo marked with both keeps one) and removes the first.</summary>
    public async Task MergeTagAsync(long from, long into, CancellationToken ct = default)
    {
        if (from == into) return;
        var already = (await PhotoTags.QueryAsync(DataQuery.Everything.Eq("tag_id", into), ct)).Select(r => r.Int("photo_id")).ToHashSet();
        foreach (var r in await PhotoTags.QueryAsync(DataQuery.Everything.Eq("tag_id", from), ct))
        {
            if (already.Contains(r.Int("photo_id"))) await PhotoTags.DeleteAsync(r.Id, ct);
            else await PhotoTags.UpdateAsync(r.Id, new Dictionary<string, object?> { ["tag_id"] = into }, ct);
        }
        await Tags.DeleteAsync(from, ct);
    }

    // ---- comments and hearts ------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Comment>> CommentsOfPhotoAsync(long photoId, CancellationToken ct = default) =>
        (await Comments.QueryAsync(DataQuery.Everything.Eq("photo_id", photoId).OrderBy("created").OrderBy("id"), ct)).Select(C).ToList();
    public async Task<IReadOnlyList<Comment>> CommentsAsync(CancellationToken ct = default) =>
        (await Comments.QueryAsync(DataQuery.Everything.OrderBy("created", descending: true).OrderBy("id", descending: true), ct)).Select(C).ToList();
    public async Task<Comment?> CommentAsync(long id, CancellationToken ct = default) => id > 0 && await Comments.GetAsync(id, ct) is DataRow r ? C(r) : null;

    /// <summary>photo id → the households that gave it a heart.</summary>
    public async Task<Dictionary<long, List<long>>> HeartsAsync(CancellationToken ct = default) =>
        (await Hearts.QueryAsync(DataQuery.Everything, ct)).GroupBy(r => r.Int("photo_id")).ToDictionary(g => g.Key, g => g.Select(r => r.Int("guest_id")).ToList());

    /// <summary>Gives the heart or takes it back; true when the photo has it now.</summary>
    public async Task<bool> ToggleHeartAsync(long photoId, long guestId, CancellationToken ct = default)
    {
        if (await Hearts.FindAsync(DataQuery.Everything.Eq("photo_id", photoId).Eq("guest_id", guestId), ct) is DataRow r)
        {
            await Hearts.DeleteAsync(r.Id, ct);
            return false;
        }
        await Hearts.InsertAsync(new Dictionary<string, object?> { ["photo_id"] = photoId, ["guest_id"] = guestId, ["created"] = DateTime.UtcNow }, ct);
        return true;
    }

    /// <summary>Everything a household wrote or marked (its photos stay unless <paramref name="photosToo"/>): for deleting a guest.</summary>
    public async Task EraseGuestAsync(IPluginFiles files, long guestId, bool photosToo, CancellationToken ct = default)
    {
        await Comments.DeleteWhereAsync(DataQuery.Everything.Eq("guest_id", guestId), ct);
        await Hearts.DeleteWhereAsync(DataQuery.Everything.Eq("guest_id", guestId), ct);
        await PhotoTags.UpdateWhereAsync(DataQuery.Everything.Eq("by", guestId), new Dictionary<string, object?> { ["by"] = 0 }, ct);
        if (photosToo)
            foreach (var p in await PhotosOfGuestAsync(guestId, ct)) await DeletePhotoAsync(files, p, ct);
        else
            await Photos.UpdateWhereAsync(DataQuery.Everything.Eq("guest_id", guestId), new Dictionary<string, object?> { ["guest_id"] = -1 }, ct);
    }
}
