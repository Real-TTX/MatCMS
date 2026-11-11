using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Moments;

/// <summary>
/// Uploading from the phone, right at the party: ONE big button opens the photo library or the camera, every chosen picture becomes a tile that fills
/// while it uploads, one request per photo. The browser makes the large view (2048 px) and the tile (640 px) itself before sending — the plugin has no
/// image library, and a gallery of 12-megapixel originals would not load on a phone. The original goes along (for the download) unless it is too big or
/// in a format the server cannot check (HEIC); then the view stands in for it. On the server: the type is read from the bytes, the date from the photo,
/// and the location is removed from the original before anybody else can download it.
/// </summary>
public static class MomentsUpload
{
    public const long MaxView = 8 * 1024 * 1024, MaxThumb = 2 * 1024 * 1024;

    public static string Panel(IPluginContext ctx, Viewer v, IReadOnlyList<Album> albums, long preselect = 0)
    {
        var approval = ctx.Settings.GetBool(MomentsSettings.UploadApproval) && !v.Admin;
        // the hosts choose the album (the photographer's, the photo booth's …); a guest's photos go to the guests' album by themselves
        var choose = v.Admin && albums.Count > 0;
        var pick = preselect > 0 && albums.Any(a => a.Id == preselect) ? preselect : albums.FirstOrDefault(a => !a.Guests)?.Id ?? albums.FirstOrDefault()?.Id ?? 0;
        var sb = new StringBuilder("<section class=\"mo-card mo-upload\"><form method=\"post\" action=\"").Append(MomentsPublic.Base).Append("/hochladen\" enctype=\"multipart/form-data\" data-mo-up data-max=\"")
            .Append(MomentsSettings.MaxBytes(ctx).ToString(CultureInfo.InvariantCulture)).Append("\" data-mo-words=\"").Append(M.H(ScriptWords(ctx))).Append("\">").Append(ctx.Antiforgery)
            .Append("<input type=\"hidden\" name=\"action\" value=\"upload\">");
        if (choose)
        {
            sb.Append("<label class=\"mo-album-pick\"><span>").Append(ctx.T("moments.upload.whichAlbum")).Append("</span><select name=\"album\" data-mo-album>");
            foreach (var a in albums) sb.Append("<option value=\"").Append(a.Id).Append('"').Append(a.Id == pick ? " selected" : "").Append('>').Append(M.H(a.Name)).Append(a.Guests ? " (" + ctx.T("moments.guests") + ")" : "").Append("</option>");
            sb.Append("<option value=\"0\"").Append(pick == 0 ? " selected" : "").Append('>').Append(ctx.T("moments.noAlbum")).Append("</option></select></label>");
        }
        sb
            .Append("<label class=\"mo-drop\"><input type=\"file\" name=\"file\" multiple accept=\"image/*\"><span class=\"mo-big\"><i class=\"ti ti-camera-plus\" aria-hidden=\"true\"></i> ").Append(ctx.T("moments.upload.choose")).Append("</span>")
            .Append("<small>").Append(ctx.T("moments.upload.chooseHint")).Append("</small></label>")
            .Append("<div class=\"mo-up-tiles\" aria-live=\"polite\"></div><p class=\"mo-up-status\" role=\"status\"></p>")
            .Append("<noscript><button class=\"mo-btn\" type=\"submit\">").Append(ctx.T("moments.upload")).Append("</button></noscript></form>")
            .Append("<p class=\"mo-muted small\">").Append(approval ? ctx.T("moments.upload.approvalNote") + " " : "")
            .Append(ctx.T("moments.upload.limits", M.Size(ctx, MomentsSettings.MaxBytes(ctx)))).Append("</p>")
            .Append("<p class=\"mo-up-done\" hidden><a class=\"mo-btn\" href=\"").Append(MomentsPublic.Base).Append("/meine\"><i class=\"ti ti-check\" aria-hidden=\"true\"></i> ").Append(ctx.T("moments.upload.done")).Append("</a></p></section>");
        return sb.Append(MomentsScript.Upload).ToString();
    }

    /// <summary>The words of the upload script (MomentsScript.Upload), in the page's language; "{0}" stands for a number.</summary>
    private static string ScriptWords(IPluginContext ctx)
    {
        var o = new JsonObject();
        foreach (var k in new[] { "left1", "leftN", "done1", "doneN", "pending", "thanks", "bad1", "badN", "format", "tooBig", "error", "failed", "offline" })
            o[k] = ctx.T("moments.upjs." + k);
        return o.ToJsonString();
    }

    public sealed record Result(string? Error, bool Pending, int Saved);

    /// <summary>Stores what was posted: one photo from the script (file + view + thumb), or several plain files without it.</summary>
    public static async Task<Result> SaveAsync(IPluginContext ctx, MomentsStore store, Viewer v, CancellationToken ct)
    {
        var r = ctx.Request;
        var pending = ctx.Settings.GetBool(MomentsSettings.UploadApproval) && !v.Admin;
        var max = MomentsSettings.MaxBytes(ctx);
        var originals = r.Files.Where(f => f.Field == "file" && f.Length > 0).Take(40).ToList();
        var view = r.File("view") is { Length: > 0 } vf ? vf : null;
        var thumb = r.File("thumb") is { Length: > 0 } tf ? tf : null;
        if (originals.Count == 0 && view is null) return new Result(ctx.T("moments.upload.pickOne"), pending, 0);
        // the album: what the hosts chose (if it exists), the guests' album for a guest
        var albums = await store.AlbumsAsync(ct);
        var albumId = v.Admin
            ? (long.TryParse(r.F("album"), NumberStyles.None, CultureInfo.InvariantCulture, out var chosen) && albums.Any(a => a.Id == chosen) ? chosen : 0)
            : albums.FirstOrDefault(a => a.Guests)?.Id ?? 0;

        // the script's case: at most one original, plus the browser's view and tile
        if (view is not null)
        {
            if (originals.Count > 1) return new Result(ctx.T("moments.upload.onePerRequest"), pending, 0);
            var err = await SaveOneAsync(ctx, store, v, originals.FirstOrDefault(), view, thumb, r.F("name"), pending, max, albumId, ct);
            return new Result(err, pending, err is null ? 1 : 0);
        }
        var saved = 0;
        foreach (var f in originals)
        {
            var err = await SaveOneAsync(ctx, store, v, f, null, null, f.FileName, pending, max, albumId, ct);
            if (err is not null) return new Result(err, pending, saved);
            saved++;
        }
        return new Result(null, pending, saved);
    }

    private static async Task<byte[]> ReadAsync(UploadedFile f, CancellationToken ct)
    {
        using var s = f.OpenRead();
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static async Task<string?> SaveOneAsync(IPluginContext ctx, MomentsStore store, Viewer v, UploadedFile? original, UploadedFile? view, UploadedFile? thumb,
        string name, bool pending, long max, long albumId, CancellationToken ct)
    {
        var label = M.Line(Path.GetFileName(original?.FileName ?? name ?? ""), 120);
        if (original is not null && original.Length > max) return ctx.T("moments.upload.tooBig", label, M.Size(ctx, max));
        if (view is not null && view.Length > MaxView) return ctx.T("moments.upload.viewTooBig", label);
        if (thumb is not null && thumb.Length > MaxThumb) thumb = null;

        var orig = original is null ? null : await ReadAsync(original, ct);
        var origExt = orig is null ? "" : MomentsImage.Sniff(orig);
        if (orig is not null && origExt.Length == 0)
        {
            // HEIC and friends: the server cannot check them — with a view from the browser the view stands in, without one it is refused
            if (view is null) return ctx.T("moments.upload.badType", label);
            orig = null;
        }
        var viewBytes = view is null ? null : await ReadAsync(view, ct);
        if (viewBytes is not null && MomentsImage.Sniff(viewBytes).Length == 0) return ctx.T("moments.upload.viewNotImage", label);
        var thumbBytes = thumb is null ? null : await ReadAsync(thumb, ct);
        if (thumbBytes is not null && MomentsImage.Sniff(thumbBytes).Length == 0) thumbBytes = null;
        if (orig is null && viewBytes is null) return ctx.T("moments.upload.unreadable", label);

        // when it was taken: the photo's own date; else what the phone says about the file; else now
        var taken = orig is not null && origExt == ".jpg" ? MomentsImage.TakenAt(orig) : "";
        if (taken.Length == 0 && long.TryParse(ctx.Request.F("modified"), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms > 946_684_800_000L && ms < 4_102_444_800_000L
            && DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime is var mod && mod < DateTime.UtcNow.AddDays(1))
            taken = M.Local(mod).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        if (taken.Length == 0) taken = M.NowLocal;

        if (orig is not null && origExt == ".jpg") MomentsImage.StripLocation(orig);

        var key = "p/" + Guid.NewGuid().ToString("N");
        string Save(byte[] bytes, string suffix) => key + suffix + MomentsImage.Sniff(bytes);
        var files = new List<(string Name, byte[] Bytes)>();
        var fileName = orig is not null ? Save(orig, "") : Save(viewBytes!, "");
        files.Add((fileName, orig ?? viewBytes!));
        var viewName = fileName;
        if (viewBytes is not null && orig is not null) { viewName = Save(viewBytes, "-v"); files.Add((viewName, viewBytes)); }
        var thumbName = viewName;
        if (thumbBytes is not null) { thumbName = Save(thumbBytes, "-t"); files.Add((thumbName, thumbBytes)); }
        foreach (var (n, bytes) in files)
            using (var s = new MemoryStream(bytes)) await ctx.Files.SaveAsync(n, s, ct);

        var (w, h) = MomentsImage.Dimensions(viewBytes ?? orig!);
        if (w <= 0 || h <= 0)
        {
            int.TryParse(ctx.Request.F("w"), out w);
            int.TryParse(ctx.Request.F("h"), out h);
        }
        await store.Photos.InsertAsync(new Dictionary<string, object?>
        {
            ["guest_id"] = v.Id, ["file"] = fileName, ["view"] = viewName, ["thumb"] = thumbName, ["file_name"] = label,
            ["size"] = (orig ?? viewBytes!).LongLength, ["width"] = Math.Clamp(w, 0, 20000), ["height"] = Math.Clamp(h, 0, 20000), ["taken"] = taken, ["caption"] = "",
            ["status"] = pending ? MomentsStore.Pending : MomentsStore.Approved, ["featured"] = false, ["created"] = DateTime.UtcNow,
            ["approved_at"] = pending ? null : DateTime.UtcNow, ["album_id"] = albumId
        }, ct);
        return null;
    }
}
