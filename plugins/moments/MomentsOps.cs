using System.Globalization;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Moments;

/// <summary>
/// The guest area's content as plugin operations — for the site's AI assistant, the cloud's MCP and the site's own MCP server: albums (the hosts'
/// content), households (personal data) and photos (the guests' uploads, personal data).
/// <para>Rules: every write answers with an <c>undo</c> naming an INTERNAL operation of this plugin; values are cleaned like the admin's forms clean them
/// and written through <see cref="MomentsStore"/>. An access code, a user name or a password NEVER leaves the plugin in an answer and is never set by an
/// operation — a household's way in is handed over by the hosts (access card in the admin). Nothing is deleted for good: an album can be deleted (its
/// photos stay), a household is switched off, a photo hidden. The plugin sends no mail. Answers stay German.</para>
/// </summary>
public static class MomentsOps
{
    public static void Configure(IPluginBuilder b)
    {
        b.Operation(AlbumList, AlbumListAsync);
        b.Operation(AlbumRead, AlbumReadAsync);
        b.Operation(AlbumCreate, AlbumCreateAsync);
        b.Operation(AlbumUpdate, AlbumUpdateAsync);
        b.Operation(AlbumDelete, AlbumDeleteAsync);
        b.Operation(AlbumRemove, AlbumRemoveAsync);
        b.Operation(AlbumRestore, AlbumRestoreAsync);
        b.Operation(AlbumRecreate, AlbumRecreateAsync);

        b.Operation(GuestList, GuestListAsync);
        b.Operation(GuestRead, GuestReadAsync);
        b.Operation(GuestCreate, GuestCreateAsync);
        b.Operation(GuestUpdate, GuestUpdateAsync);
        b.Operation(GuestRemove, GuestRemoveAsync);
        b.Operation(GuestRestore, GuestRestoreAsync);

        b.Operation(PhotoList, PhotoListAsync);
        b.Operation(PhotoRead, PhotoReadAsync);
        b.Operation(PhotoUpdate, PhotoUpdateAsync);
        b.Operation(PhotoRestore, PhotoRestoreAsync);
    }

    private const string AdminRoot = MomentsAdmin.Root;
    private const string B = MomentsPublic.Base;

    // ---- the specs ------------------------------------------------------------------------------------------------

    public static readonly PluginOperationSpec AlbumList = new("album.list",
        "Alben der Galerie in ihrer Reihenfolge (Fotograf, Gästebilder, Fotobox …): id, Name, Platz, ob die Fotos der Gäste von selbst hinein kommen (guests), Zahl der Fotos.",
        "{}", Writes: false);
    public static readonly PluginOperationSpec AlbumRead = new("album.read", "Ein Album lesen: Name, Platz, Gäste-Album ja/nein, Fotos nach Status (sichtbar, wartend, ausgeblendet), Videos, Adresse in der Galerie.",
        """{"id": 2}""", Writes: false);
    public static readonly PluginOperationSpec AlbumCreate = new("album.create",
        "Neues Album am Ende der Reihenfolge anlegen. guests: true = die Fotos, die Gäste hochladen, landen von selbst darin (nur ein Album kann das; die Gästefotos ohne Album kommen gleich mit).",
        """{"name": "Fotobox", "guests"?: false}""");
    public static readonly PluginOperationSpec AlbumUpdate = new("album.update",
        "Ein Album ändern: name umbenennen; guests: true = ab jetzt landen die Gästefotos hier (und die ohne Album kommen mit), false = in keinem Album mehr von selbst.",
        """{"id": 2, "name"?: "Gästebilder", "guests"?: true}""");
    public static readonly PluginOperationSpec AlbumDelete = new("album.delete",
        "Ein Album löschen. Seine Fotos bleiben (ohne Album, unter „Alle Fotos“).", """{"id": 2}""");
    public static readonly PluginOperationSpec AlbumRemove = new("album.remove", "Entfernt ein eben angelegtes Album (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec AlbumRestore = new("album.restore", "Stellt ein geändertes Album wieder her (Rückgängig).", Internal: true);
    public static readonly PluginOperationSpec AlbumRecreate = new("album.recreate", "Legt ein gelöschtes Album mit seinen Fotos wieder an (Rückgängig).", Internal: true);

    public static readonly PluginOperationSpec GuestList = new("guest.list",
        "Haushalte der Gäste (personenbezogen): id, Name zur Begrüßung, Personen, Tisch, aktiv, Besuche, zuletzt da, Fotos, ob ein Passwort gesetzt ist. Zugangscodes, Benutzernamen und Passwörter stehen nie in der Antwort. active: nur aktive (true) bzw. gesperrte (false); search: Name, Personen, Tisch.",
        """{"active"?: true, "search"?: "text", "table"?: "Tisch 3", "limit"?: 200}""", Writes: false, PersonalData: true);
    public static readonly PluginOperationSpec GuestRead = new("guest.read",
        "Einen Haushalt lesen (personenbezogen): Name, Personen, Tisch, Notiz, aktiv, Besuche, Fotos und Kommentare. Ohne Code und Passwort – die Zugangskarte steht im Admin (admin_url, cards_url).",
        """{"id": 5}""", Writes: false, PersonalData: true);
    public static readonly PluginOperationSpec GuestCreate = new("guest.create",
        "Neuen Haushalt anlegen (personenbezogen). name: wie er begrüßt wird („Zoe & Fabian“, ohne Komma), people: die Vornamen, durch Komma getrennt (für „Wer ist drauf?“; ohne Angabe = name), table: Tisch. Der Zugangscode wird erzeugt, steht aber NICHT in der Antwort; Benutzername/Passwort und die Zugangskarte vergeben die Gastgeber im Admin. Keine E-Mail.",
        """{"name": "Zoe & Fabian", "people"?: "Zoe, Fabian", "table"?: "Tisch 3", "note"?: "…"}""", PersonalData: true);
    public static readonly PluginOperationSpec GuestUpdate = new("guest.update",
        "Einen Haushalt ändern (personenbezogen); nicht genannte Felder bleiben. active: false = gesperrt (kommt nicht mehr hinein – das „Ausschalten“), true = wieder offen. Code, Benutzername und Passwort ändern nur die Gastgeber im Admin.",
        """{"id": 5, "name"?, "people"?, "table"?, "note"?, "active"?: false}""", PersonalData: true);
    public static readonly PluginOperationSpec GuestRemove = new("guest.remove", "Entfernt einen eben angelegten, noch nie benutzten Haushalt (Rückgängig).", PersonalData: true, Internal: true);
    public static readonly PluginOperationSpec GuestRestore = new("guest.restore", "Stellt einen Haushalt aus einer Rückgängig-Aufzeichnung wieder her.", PersonalData: true, Internal: true);

    public static readonly PluginOperationSpec PhotoList = new("photo.list",
        "Fotos und Videos der Galerie (personenbezogen – Bilder der Gäste): id, von wem, Album, Status (approved = sichtbar, pending = wartet auf Freigabe, hidden = ausgeblendet), Highlight, Bildunterschrift, Aufnahmezeit, Video. Neueste zuerst.",
        """{"status"?: "pending", "album_id"?: 2, "guest_id"?: 5, "featured"?: true, "limit"?: 100}""", Writes: false, PersonalData: true);
    public static readonly PluginOperationSpec PhotoRead = new("photo.read",
        "Ein Foto lesen (personenbezogen): wie photo.list, dazu Stichworte und Personen, Herzen, Kommentare (Zahl) und die Adresse in der Galerie.",
        """{"id": 31}""", Writes: false, PersonalData: true);
    public static readonly PluginOperationSpec PhotoUpdate = new("photo.update",
        "Ein Foto freigeben, ausblenden oder einordnen (personenbezogen). status: approved (für alle Gäste sichtbar), hidden (ausgeblendet – das „Ausschalten“), pending. featured: Highlight. album_id: Album (0 = keins). caption: Bildunterschrift. Löschen nur im Admin.",
        """{"id": 31, "status"?: "approved", "featured"?: true, "album_id"?: 2, "caption"?: "Der erste Tanz"}""", PersonalData: true);
    public static readonly PluginOperationSpec PhotoRestore = new("photo.restore", "Stellt Status, Highlight, Album und Bildunterschrift eines Fotos wieder her (Rückgängig).", PersonalData: true, Internal: true);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    internal static JsonObject Answer(string status, string message, JsonNode? data = null, JsonNode? undo = null)
    {
        var o = new JsonObject { ["status"] = status, ["message"] = message };
        if (data is not null) o["data"] = data;
        if (undo is not null) o["undo"] = undo;
        return o;
    }

    private static JsonObject Undo(PluginOperationSpec op, JsonObject args) => new() { ["name"] = op.Name, ["args"] = args };

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : o.ContainsKey(key) && o[key] is null ? "" : null;

    private static long Long(JsonNode? o, string key)
    {
        if (o is not JsonObject obj || obj[key] is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < 1e15) return (long)d;
        return v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }

    private static bool? Bool(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        return v.TryGetValue<string>(out var s) ? s.Trim().ToLowerInvariant() switch { "true" or "1" or "ja" or "yes" => true, "false" or "0" or "nein" or "no" => false, _ => null } : null;
    }

    private static List<long> Ids(JsonObject o, string key) => o[key] is JsonArray a
        ? a.Select(x => x is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : 0) : 0).Where(x => x > 0).Distinct().ToList()
        : new List<long>();

    private static int Limit(JsonNode? args, int def, int max)
    {
        var n = Long(args, "limit");
        return (int)Math.Clamp(n <= 0 ? def : n, 1, max);
    }

    /// <summary>Sets which album takes the guests' uploads WITHOUT taking in any loose photo (an undo puts the old state back as it was).</summary>
    private static async Task SetGuestFlagAsync(MomentsStore store, long albumId, CancellationToken ct)
    {
        foreach (var a in await store.AlbumsAsync(ct))
            if (a.Guests != (a.Id == albumId)) await store.Albums.UpdateAsync(a.Id, new Dictionary<string, object?> { ["guests"] = a.Id == albumId }, ct);
    }

    /// <summary>Makes the album the guests' album the way the admin does (the guests' loose photos come along) and answers which photos came.</summary>
    private static async Task<List<long>> MakeGuestAlbumAsync(MomentsStore store, long albumId, CancellationToken ct)
    {
        var loose = (await store.PhotosAsync(ct: ct)).Where(p => p.GuestId > 0 && p.AlbumId == 0).Select(p => p.Id).ToList();
        await store.SetGuestAlbumAsync(albumId, ct);
        return loose;
    }

    // ---- albums -------------------------------------------------------------------------------------------------------

    private static async Task<JsonNode?> AlbumListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        var count = (await store.PhotosAsync(ct: ct)).GroupBy(p => p.AlbumId).ToDictionary(g => g.Key, g => g.Count());
        var arr = new JsonArray();
        var place = 0;
        foreach (var a in await store.AlbumsAsync(ct))
            arr.Add(new JsonObject { ["id"] = a.Id, ["name"] = a.Name, ["position"] = ++place, ["guests"] = a.Guests, ["photos"] = count.TryGetValue(a.Id, out var n) ? n : 0 });
        return Answer("applied", arr.Count + " Album/Alben" + (count.TryGetValue(0, out var loose) && loose > 0 ? ", " + loose + " Foto(s) ohne Album." : "."), arr);
    }

    private static async Task<JsonNode?> AlbumReadAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        if (await store.AlbumAsync(Long(args, "id"), ct) is not Album a) return Answer("failed", "Album nicht gefunden.");
        var photos = (await store.PhotosAsync(ct: ct)).Where(p => p.AlbumId == a.Id).ToList();
        var albums = await store.AlbumsAsync(ct);
        return Answer("applied", "Album „" + a.Name + "“.", new JsonObject
        {
            ["id"] = a.Id, ["name"] = a.Name, ["position"] = albums.ToList().FindIndex(x => x.Id == a.Id) + 1, ["guests"] = a.Guests,
            ["approved"] = photos.Count(p => p.Approved), ["pending"] = photos.Count(p => p.Pending), ["hidden"] = photos.Count(p => p.Status == MomentsStore.Hidden),
            ["videos"] = photos.Count(p => p.IsVideo), ["gallery_url"] = B + "?album=" + M.N(a.Id), ["admin_url"] = AdminRoot + "albums/" + M.N(a.Id)
        });
    }

    private static async Task<JsonNode?> AlbumCreateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var name = M.Line(Str(o, "name"), 60);
        if (name.Length == 0) return Answer("failed", "Bitte einen Namen angeben.");
        var store = new MomentsStore(ctx.Data);
        if ((await store.AlbumsAsync(ct)).FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is Album same)
            return Answer("skipped-exists", "Das Album „" + name + "“ gibt es schon.", new JsonObject { ["id"] = same.Id });
        var before = await store.GuestAlbumAsync(ct);
        var id = await store.AddAlbumAsync(name, ct);
        var adopted = Bool(o, "guests") == true ? await MakeGuestAlbumAsync(store, id, ct) : new List<long>();
        return Answer("applied", "Album „" + name + "“ angelegt" + (Bool(o, "guests") == true ? " – die Gästefotos landen darin" + (adopted.Count > 0 ? ", " + adopted.Count + " kamen gleich mit." : ".") : "."),
            new JsonObject { ["id"] = id, ["admin_url"] = AdminRoot + "albums/" + M.N(id) },
            Undo(AlbumRemove, new JsonObject { ["id"] = id, ["guest_album"] = before }));
    }

    private static async Task<JsonNode?> AlbumUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var id = Long(args, "id");
        if (args is not JsonObject o || id <= 0) return Answer("failed", "Bitte die id des Albums angeben (album.list).");
        var store = new MomentsStore(ctx.Data);
        if (await store.AlbumAsync(id, ct) is not Album a) return Answer("failed", "Album nicht gefunden.");
        var name = Str(o, "name") is string typed ? M.Line(typed, 60) : null;
        if (name is { Length: 0 }) return Answer("failed", "Ein Album braucht einen Namen.");
        if (name is not null && (await store.AlbumsAsync(ct)).Any(x => x.Id != a.Id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            return Answer("failed", "Ein anderes Album heißt schon „" + name + "“.");
        var guests = Bool(o, "guests");
        if (name is null && guests is null) return Answer("skipped", "Nichts zu ändern.");
        var before = await store.GuestAlbumAsync(ct);
        if (name is not null && name != a.Name) await store.Albums.UpdateAsync(a.Id, new Dictionary<string, object?> { ["name"] = name }, ct);
        var adopted = new List<long>();
        if (guests == true && !a.Guests) adopted = await MakeGuestAlbumAsync(store, a.Id, ct);
        if (guests == false && a.Guests) await store.SetGuestAlbumAsync(0, ct);
        return Answer("applied", "Album „" + (name ?? a.Name) + "“ geändert" + (adopted.Count > 0 ? ", " + adopted.Count + " Gästefoto(s) kamen hinzu." : "."), new JsonObject { ["id"] = a.Id },
            Undo(AlbumRestore, new JsonObject { ["id"] = a.Id, ["name"] = a.Name, ["guest_album"] = before, ["adopted"] = new JsonArray(adopted.Select(x => (JsonNode?)x).ToArray()) }));
    }

    private static async Task<JsonNode?> AlbumDeleteAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        if (await store.AlbumAsync(Long(args, "id"), ct) is not Album a) return Answer("failed", "Album nicht gefunden.");
        var photos = (await store.PhotosAsync(ct: ct)).Where(p => p.AlbumId == a.Id).Select(p => p.Id).ToList();
        await store.DeleteAlbumAsync(a.Id, ct);
        ctx.Log($"Album „{a.Name}“ gelöscht über eine Operation ({photos.Count} Fotos bleiben ohne Album)");
        return Answer("applied", "Album „" + a.Name + "“ gelöscht; " + photos.Count + " Foto(s) bleiben ohne Album.", null,
            Undo(AlbumRecreate, new JsonObject { ["name"] = a.Name, ["sort"] = a.Sort, ["guests"] = a.Guests, ["photos"] = new JsonArray(photos.Select(x => (JsonNode?)x).ToArray()) }));
    }

    private static async Task<JsonNode?> AlbumRemoveAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        if (await store.AlbumAsync(Long(args, "id"), ct) is not Album a) return Answer("skipped", "Das Album gibt es nicht mehr.");
        // its photos go back to no album — the guests' loose photos it took in included
        await store.DeleteAlbumAsync(a.Id, ct);
        var before = Long(args, "guest_album");
        if (a.Guests && before > 0 && await store.AlbumAsync(before, ct) is not null) await SetGuestFlagAsync(store, before, ct);
        return Answer("applied", "Album „" + a.Name + "“ entfernt.");
    }

    private static async Task<JsonNode?> AlbumRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var store = new MomentsStore(ctx.Data);
        if (await store.AlbumAsync(Long(o, "id"), ct) is not Album a) return Answer("skipped", "Das Album gibt es nicht mehr.");
        var name = M.Line(Str(o, "name"), 60);
        if (name.Length > 0 && name != a.Name && !(await store.AlbumsAsync(ct)).Any(x => x.Id != a.Id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            await store.Albums.UpdateAsync(a.Id, new Dictionary<string, object?> { ["name"] = name }, ct);
        if (o.ContainsKey("guest_album"))
        {
            var before = Long(o, "guest_album");
            await SetGuestFlagAsync(store, before > 0 && await store.AlbumAsync(before, ct) is not null ? before : 0, ct);
        }
        foreach (var pid in Ids(o, "adopted"))
            if (await store.PhotoAsync(pid, ct) is Photo p && p.AlbumId == a.Id) await store.Photos.UpdateAsync(p.Id, new Dictionary<string, object?> { ["album_id"] = 0L }, ct);
        return Answer("applied", "Album wiederhergestellt.");
    }

    private static async Task<JsonNode?> AlbumRecreateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var name = M.Line(Str(o, "name"), 60);
        if (name.Length == 0) return Answer("failed", "Name fehlt.");
        var store = new MomentsStore(ctx.Data);
        if ((await store.AlbumsAsync(ct)).Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) return Answer("skipped", "Ein Album „" + name + "“ gibt es inzwischen wieder.");
        var guests = Bool(o, "guests") == true;
        var id = await store.Albums.InsertAsync(new Dictionary<string, object?> { ["name"] = name, ["sort"] = Long(o, "sort"), ["guests"] = false, ["created"] = DateTime.UtcNow }, ct);
        if (guests) await SetGuestFlagAsync(store, id, ct);
        var back = 0;
        foreach (var pid in Ids(o, "photos"))
            if (await store.PhotoAsync(pid, ct) is Photo p && p.AlbumId == 0) { await store.Photos.UpdateAsync(p.Id, new Dictionary<string, object?> { ["album_id"] = id }, ct); back++; }
        return Answer("applied", "Album „" + name + "“ wieder angelegt, " + back + " Foto(s) wieder darin.", new JsonObject { ["id"] = id });
    }

    // ---- households -----------------------------------------------------------------------------------------------------

    /// <summary>What an answer may say about a household: never its code, its user name or anything of its password.</summary>
    private static JsonObject GuestData(Guest g, int photos) => new()
    {
        ["id"] = g.Id, ["name"] = g.Name, ["people"] = new JsonArray(g.PeopleList.Select(x => (JsonNode?)x).ToArray()), ["table"] = g.Table, ["active"] = g.Active,
        ["visits"] = g.Visits, ["last_seen"] = g.LastSeen?.ToString("o"), ["photos"] = photos, ["has_login"] = g.Login.Length > 0, ["has_password"] = g.HasPassword
    };

    private static JsonObject GuestSnapshot(Guest g) => new() { ["id"] = g.Id, ["name"] = g.Name, ["people"] = g.People, ["table"] = g.Table, ["note"] = g.Note, ["active"] = g.Active };

    /// <summary>The fields an operation may write, cleaned as the admin's household form cleans them; null = keep.</summary>
    private static (string? Name, string? People, string? Table, string? Note, bool? Active) GuestValues(JsonObject o)
    {
        var name = Str(o, "name") is string n ? M.Name(n, 120) : null;
        var people = Str(o, "people") is string p ? M.Clip(string.Join(", ", MomentsStore.SplitPeople(p)), 400) : null;
        var table = Str(o, "table") is string t ? M.Line(t, 60) : null;
        var note = Str(o, "note") is string x ? M.Clip(x, 2000) : null;
        return (name, people, table, note, Bool(o, "active"));
    }

    private static async Task<JsonNode?> GuestListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        var active = Bool(o, "active");
        var search = (Str(o, "search") ?? "").Trim().ToLowerInvariant();
        var table = (Str(o, "table") ?? "").Trim();
        var store = new MomentsStore(ctx.Data);
        var count = (await store.PhotosAsync(ct: ct)).GroupBy(p => p.GuestId).ToDictionary(g => g.Key, g => g.Count());
        var list = (await store.GuestsAsync(ct))
            .Where(g => active is null || g.Active == active)
            .Where(g => table.Length == 0 || string.Equals(g.Table, table, StringComparison.OrdinalIgnoreCase))
            .Where(g => search.Length == 0 || (g.Name + " " + g.People + " " + g.Table).ToLowerInvariant().Contains(search)).ToList();
        var arr = new JsonArray();
        foreach (var g in list.Take(Limit(args, 200, 1000))) arr.Add(GuestData(g, count.TryGetValue(g.Id, out var n) ? n : 0));
        return Answer("applied", list.Count + " Haushalt(e), " + list.Count(g => g.Visits > 0) + " davon schon da gewesen.", arr);
    }

    private static async Task<JsonNode?> GuestReadAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        if (await store.GuestAsync(Long(args, "id"), ct) is not Guest g) return Answer("failed", "Haushalt nicht gefunden.");
        var data = GuestData(g, (await store.PhotosOfGuestAsync(g.Id, ct)).Count);
        data["note"] = g.Note;
        data["created"] = g.Created?.ToString("o");
        data["comments"] = (await store.CommentsAsync(ct)).Count(c => c.GuestId == g.Id);
        data["signed_in"] = g.MemberId > 0;
        data["admin_url"] = AdminRoot + "guests/" + M.N(g.Id);
        data["cards_url"] = B + "/karten?ids=" + M.N(g.Id);
        return Answer("applied", "Haushalt „" + g.Name + "“ (Code und Passwort stehen nur im Admin).", data);
    }

    private static async Task<JsonNode?> GuestCreateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var (name, people, table, note, _) = GuestValues(o);
        if (string.IsNullOrEmpty(name)) return Answer("failed", "Bitte einen Namen angeben (so wird der Haushalt begrüßt).");
        var store = new MomentsStore(ctx.Data);
        if ((await store.GuestsAsync(ct)).FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is Guest same)
            return Answer("skipped-exists", "Den Haushalt „" + name + "“ gibt es schon.", new JsonObject { ["id"] = same.Id });
        var id = await store.SaveGuestAsync(0, name, string.IsNullOrEmpty(people) ? M.Clip(string.Join(", ", MomentsStore.SplitPeople(name)), 400) : people,
            table ?? "", note ?? "", true, MomentsSettings.CodeFormat(ctx), ct);
        ctx.Log($"Haushalt „{name}“ angelegt über eine Operation");
        return Answer("applied", "Haushalt „" + name + "“ angelegt. Der Zugangscode ist erzeugt – er steht auf der Zugangskarte im Admin, nicht hier.",
            new JsonObject { ["id"] = id, ["admin_url"] = AdminRoot + "guests/" + M.N(id), ["cards_url"] = B + "/karten?ids=" + M.N(id) },
            Undo(GuestRemove, new JsonObject { ["id"] = id }));
    }

    private static async Task<JsonNode?> GuestUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var id = Long(args, "id");
        if (args is not JsonObject o || id <= 0) return Answer("failed", "Bitte die id des Haushalts angeben (guest.list).");
        var store = new MomentsStore(ctx.Data);
        if (await store.GuestAsync(id, ct) is not Guest g) return Answer("failed", "Haushalt nicht gefunden.");
        foreach (var secret in new[] { "code", "login", "password" })
            if (o.ContainsKey(secret)) return Answer("failed", "Code, Benutzername und Passwort ändern nur die Gastgeber im Admin (" + AdminRoot + "guests/" + M.N(g.Id) + ").");
        var (name, people, table, note, active) = GuestValues(o);
        if (name is { Length: 0 }) return Answer("failed", "Ein Haushalt braucht einen Namen.");
        if (name is null && people is null && table is null && note is null && active is null) return Answer("skipped", "Nichts zu ändern.");
        await store.SaveGuestAsync(g.Id, name ?? g.Name, people is { Length: > 0 } ? people : people is null ? g.People : M.Clip(string.Join(", ", MomentsStore.SplitPeople(name ?? g.Name)), 400),
            table ?? g.Table, note ?? g.Note, active ?? g.Active, MomentsSettings.CodeFormat(ctx), ct);
        return Answer("applied", "Haushalt „" + (name ?? g.Name) + "“ geändert" + (active == false && g.Active ? " – gesperrt." : active == true && !g.Active ? " – wieder offen." : "."),
            new JsonObject { ["id"] = g.Id }, Undo(GuestRestore, GuestSnapshot(g)));
    }

    private static async Task<JsonNode?> GuestRemoveAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        if (await store.GuestAsync(Long(args, "id"), ct) is not Guest g) return Answer("skipped", "Den Haushalt gibt es nicht mehr.");
        // a household that has been in (an account, photos, comments) is not taken back silently — that is the hosts' decision in the admin
        if (g.Visits > 0 || g.MemberId > 0 || (await store.PhotosOfGuestAsync(g.Id, ct)).Count > 0 || (await store.CommentsAsync(ct)).Any(c => c.GuestId == g.Id))
            return Answer("skipped", "„" + g.Name + "“ war schon im Gästebereich – entfernen nur im Admin.");
        await store.EraseGuestAsync(ctx.Files, g.Id, photosToo: false, ct);
        await store.Guests.DeleteAsync(g.Id, ct);
        return Answer("applied", "Haushalt „" + g.Name + "“ entfernt.");
    }

    private static async Task<JsonNode?> GuestRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var store = new MomentsStore(ctx.Data);
        if (await store.GuestAsync(Long(o, "id"), ct) is not Guest g) return Answer("skipped", "Den Haushalt gibt es nicht mehr.");
        var (name, people, table, note, active) = GuestValues(o);
        await store.SaveGuestAsync(g.Id, string.IsNullOrEmpty(name) ? g.Name : name, people ?? g.People, table ?? g.Table, note ?? g.Note, active ?? g.Active, MomentsSettings.CodeFormat(ctx), ct);
        return Answer("applied", "Haushalt wiederhergestellt.");
    }

    // ---- photos ---------------------------------------------------------------------------------------------------------

    private static readonly string[] Statuses = { MomentsStore.Approved, MomentsStore.Pending, MomentsStore.Hidden };

    private static JsonObject PhotoData(IPluginContext ctx, Photo p, IReadOnlyDictionary<long, Guest> guests) => new()
    {
        ["id"] = p.Id, ["from"] = MomentsPublic.AuthorName(ctx, p.GuestId, guests), ["guest_id"] = p.GuestId > 0 ? p.GuestId : 0, ["album_id"] = p.AlbumId, ["status"] = p.Status,
        ["featured"] = p.Featured, ["caption"] = p.Caption, ["taken"] = p.Taken, ["video"] = p.IsVideo, ["file_name"] = p.FileName, ["created"] = p.Created?.ToString("o")
    };

    private static async Task<JsonNode?> PhotoListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        var status = Str(o, "status") is { Length: > 0 } s ? s.Trim() : null;
        if (status is not null && !Statuses.Contains(status)) return Answer("failed", "status ist approved, pending oder hidden.");
        var album = o.ContainsKey("album_id") ? Long(o, "album_id") : -1;
        var guest = Long(o, "guest_id");
        var featured = Bool(o, "featured");
        var store = new MomentsStore(ctx.Data);
        var guests = (await store.GuestsAsync(ct)).ToDictionary(g => g.Id);
        var list = (await store.PhotosAsync(status, ct))
            .Where(p => album < 0 || p.AlbumId == album).Where(p => guest <= 0 || p.GuestId == guest).Where(p => featured is null || p.Featured == featured)
            .OrderByDescending(p => p.Created).ThenByDescending(p => p.Id).ToList();
        var arr = new JsonArray();
        foreach (var p in list.Take(Limit(args, 100, 1000))) arr.Add(PhotoData(ctx, p, guests));
        return Answer("applied", list.Count + " Foto(s)" + (arr.Count < list.Count ? ", die neuesten " + arr.Count + "." : "."), arr);
    }

    private static async Task<JsonNode?> PhotoReadAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var store = new MomentsStore(ctx.Data);
        if (await store.PhotoAsync(Long(args, "id"), ct) is not Photo p) return Answer("failed", "Foto nicht gefunden.");
        var guests = (await store.GuestsAsync(ct)).ToDictionary(g => g.Id);
        var tags = (await store.TagsAsync(ct)).ToDictionary(t => t.Id);
        var data = PhotoData(ctx, p, guests);
        data["tags"] = new JsonArray((await store.TagsOfPhotoAsync(p.Id, ct)).Where(m => tags.ContainsKey(m.TagId))
            .Select(m => (JsonNode?)new JsonObject { ["name"] = tags[m.TagId].Name, ["kind"] = tags[m.TagId].Kind }).ToArray());
        data["hearts"] = (await store.HeartsAsync(ct)).TryGetValue(p.Id, out var h) ? h.Count : 0;
        data["comments"] = (await store.CommentsOfPhotoAsync(p.Id, ct)).Count;
        data["gallery_url"] = B + "/foto/" + M.N(p.Id);
        data["admin_url"] = AdminRoot + "photos/" + M.N(p.Id);
        return Answer("applied", "Foto " + p.Id + ".", data);
    }

    private static async Task<string?> PhotoValuesAsync(MomentsStore store, JsonObject o, Dictionary<string, object?> v, CancellationToken ct)
    {
        if (Str(o, "status") is string status)
        {
            if (!Statuses.Contains(status.Trim())) return "status ist approved, pending oder hidden.";
            v["status"] = status.Trim();
        }
        if (Bool(o, "featured") is bool featured) v["featured"] = featured;
        if (o.ContainsKey("album_id"))
        {
            var album = Long(o, "album_id");
            if (album < 0 || (album > 0 && await store.AlbumAsync(album, ct) is null)) return "Album " + album + " gibt es nicht (album.list; 0 = keins).";
            v["album_id"] = album;
        }
        if (Str(o, "caption") is string caption) v["caption"] = M.Line(caption, 200);
        return null;
    }

    private static async Task ApplyPhotoAsync(MomentsStore store, Photo p, Dictionary<string, object?> v, CancellationToken ct)
    {
        var status = v.Remove("status", out var st) ? (string)st! : null;
        if (v.Count > 0) await store.Photos.UpdateAsync(p.Id, v, ct);
        // the status through the store: it stamps the first release, as the admin's buttons do
        if (status is not null && status != p.Status) await store.SetStatusAsync(p, status, ct);
    }

    private static async Task<JsonNode?> PhotoUpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var id = Long(args, "id");
        if (args is not JsonObject o || id <= 0) return Answer("failed", "Bitte die id des Fotos angeben (photo.list).");
        var store = new MomentsStore(ctx.Data);
        if (await store.PhotoAsync(id, ct) is not Photo p) return Answer("failed", "Foto nicht gefunden.");
        var v = new Dictionary<string, object?>();
        if (await PhotoValuesAsync(store, o, v, ct) is string error) return Answer("failed", error);
        if (v.Count == 0) return Answer("skipped", "Nichts zu ändern.");
        await ApplyPhotoAsync(store, p, v, ct);
        return Answer("applied", "Foto " + p.Id + " geändert.", new JsonObject { ["id"] = p.Id },
            Undo(PhotoRestore, new JsonObject { ["id"] = p.Id, ["status"] = p.Status, ["featured"] = p.Featured, ["album_id"] = p.AlbumId, ["caption"] = p.Caption }));
    }

    private static async Task<JsonNode?> PhotoRestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        if (args is not JsonObject o) return Answer("failed", "Argumente fehlen.");
        var store = new MomentsStore(ctx.Data);
        if (await store.PhotoAsync(Long(o, "id"), ct) is not Photo p) return Answer("skipped", "Das Foto gibt es nicht mehr.");
        var v = new Dictionary<string, object?>();
        // an album that is gone meanwhile leaves the photo in none
        if (await PhotoValuesAsync(store, o, v, ct) is not null) { v.Clear(); o.Remove("album_id"); await PhotoValuesAsync(store, o, v, ct); v["album_id"] = 0L; }
        await ApplyPhotoAsync(store, p, v, ct);
        return Answer("applied", "Foto wiederhergestellt.");
    }
}
