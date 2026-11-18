using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Moments;

/// <summary>
/// Videos (1.8). A phone video is hundreds of megabytes, and a plugin request is limited (Kestrel's 30 MB, the proxies in front of a site), so the
/// browser sends it in pieces of <see cref="ChunkBytes"/>: <c>vstart</c> (name, size — checked before a byte is sent) → <c>vpart</c> one after the
/// other → <c>vdone</c> with the poster the browser drew from the video. The pieces wait as private files (<c>u/{id}-{n}</c>), the state of an upload
/// is a row of the meta table (<c>up:{id}</c>), so the hourly job finds and removes what a closed browser left behind — the file store cannot list.
/// <para>On <c>vdone</c> the pieces are joined into ONE file while it is written, and on the way the ISO box <c>moov</c> (the film's header) is cleaned:
/// every ISO 6709 location ("+48.8577+002.2950/", where phones write it — <c>©xyz</c> and Apple's location key) is overwritten with zeros, as the location
/// is taken out of a photo; from the same box the recording time (<c>mvhd</c>). The type is read from the bytes (ISO BMFF: MP4, MOV, M4V; WebM),
/// never from the name. Played through <see cref="MomentsPublic"/>'s <c>/video/{id}</c> with range requests (seeking); a MOV is sent as
/// <c>video/mp4</c> — the same container, and Chrome plays a QuickTime file only under that name.</para>
/// <para>A video is a row of the photo table with <c>kind = video</c>: <c>file</c> the film, <c>view</c>/<c>thumb</c> the poster (empty when the
/// browser could not draw one — then a placeholder picture stands in), so albums, keywords, hearts, comments and approval work as for a photo.</para>
/// </summary>
public static class MomentsVideo
{
    public const string Kind = "video";
    public const int ChunkBytes = 6 * 1024 * 1024;
    private const string Prefix = "up:";
    private static readonly TimeSpan Stale = TimeSpan.FromHours(24);

    public static long MaxBytes(IPluginContext ctx) => (long)Math.Clamp(ctx.Settings.GetNumber(MomentsSettings.MaxVideoMb, 500), 10, 2000) * 1024 * 1024;
    public static bool Allowed(IPluginContext ctx) => ctx.Settings.Get(MomentsSettings.Videos) != "false";

    /// <summary>The extensions a video may have when it is chosen (the bytes decide afterwards).</summary>
    public static bool IsVideoName(string name) => Path.GetExtension(name ?? "").ToLowerInvariant() is ".mp4" or ".mov" or ".m4v" or ".webm" or ".qt";

    /// <summary>".mp4" for every ISO BMFF film (MP4, MOV, M4V), ".webm" for WebM, "" for anything else.</summary>
    public static string Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 4 && b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3) return ".webm";
        if (b.Length >= 8)
        {
            var type = Encoding.ASCII.GetString(b.Slice(4, 4));
            if (type is "ftyp" or "moov" or "mdat" or "wide" or "free" or "skip" or "pnot") return ".mp4";
        }
        return "";
    }

    public static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() == ".webm" ? "video/webm" : "video/mp4";

    /// <summary>"0:42" / "1:05:10".</summary>
    public static string Duration(int seconds) => seconds <= 0 ? "" : seconds >= 3600
        ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";

    /// <summary>The picture that stands in for a video without a poster (and is never the film itself).</summary>
    public static readonly byte[] Placeholder = Encoding.UTF8.GetBytes(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 640 640\"><rect width=\"640\" height=\"640\" fill=\"#2a221c\"/>" +
        "<circle cx=\"320\" cy=\"320\" r=\"96\" fill=\"none\" stroke=\"#f6efe6\" stroke-width=\"14\" opacity=\".85\"/><path d=\"M292 262v116l96-58z\" fill=\"#f6efe6\" opacity=\".85\"/></svg>");

    // ---- the three steps of an upload --------------------------------------------------------------------------------------

    private sealed record State(long Guest, bool Admin, string Name, long Size, long Received, int Parts, long Album, long Tag, long Modified, DateTime At);

    private static JsonObject Json(State s) => new()
    {
        ["g"] = s.Guest, ["a"] = s.Admin, ["name"] = s.Name, ["size"] = s.Size, ["got"] = s.Received, ["n"] = s.Parts, ["album"] = s.Album, ["tag"] = s.Tag,
        ["mod"] = s.Modified, ["at"] = s.At.Ticks
    };

    private static State? Read(string? value)
    {
        try
        {
            if (JsonNode.Parse(value ?? "") is not JsonObject o) return null;
            long L(string k) => o[k] is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;
            return new State(L("g"), o["a"] is JsonValue a && a.TryGetValue<bool>(out var ad) && ad, (string?)o["name"] ?? "", L("size"), L("got"), (int)L("n"), L("album"), L("tag"), L("mod"),
                new DateTime(L("at"), DateTimeKind.Utc));
        }
        catch { return null; }
    }

    private static async Task<(DataRow Row, State State)?> LoadAsync(MomentsStore store, Viewer v, string id, CancellationToken ct)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower)) return null;
        if (await store.Meta.FindAsync(DataQuery.Everything.Eq("name", Prefix + id), ct) is not DataRow row || Read(row.Text("value")) is not State s) return null;
        // only who started it may go on with it
        return s.Guest == v.Id && s.Admin == v.Admin ? (row, s) : null;
    }

    private static string Part(string id, int n) => "u/" + id + "-" + n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Answers the upload's id and the size of a piece — or why not.</summary>
    public static async Task<JsonObject> StartAsync(IPluginContext ctx, MomentsStore store, Viewer v, CancellationToken ct)
    {
        var r = ctx.Request;
        if (!Allowed(ctx)) return Error(ctx.T("moments.video.off"));
        var name = M.Line(Path.GetFileName(r.F("name") ?? ""), 120);
        if (!IsVideoName(name)) return Error(ctx.T("moments.video.format", name));
        if (!long.TryParse(r.F("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0) return Error(ctx.T("moments.upload.unreadable", name));
        if (size > MaxBytes(ctx)) return Error(ctx.T("moments.video.tooBig", name, M.Size(ctx, MaxBytes(ctx))));
        var albums = await store.AlbumsAsync(ct);
        var album = v.Admin
            ? (long.TryParse(r.F("album"), NumberStyles.None, CultureInfo.InvariantCulture, out var chosen) && albums.Any(a => a.Id == chosen) ? chosen : 0)
            : albums.FirstOrDefault(a => a.Guests)?.Id ?? 0;
        var tagName = MomentsSettings.UploadTagList(ctx).FirstOrDefault(t => string.Equals(t, (r.F("tag") ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        var tag = tagName is null ? 0 : (await store.EnsureTagAsync(tagName, MomentsStore.Topic, ct)).Id;
        long.TryParse(r.F("modified"), NumberStyles.None, CultureInfo.InvariantCulture, out var modified);
        var id = Guid.NewGuid().ToString("N");
        var state = new State(v.Id, v.Admin, name, size, 0, 0, album, tag, modified, DateTime.UtcNow);
        await store.Meta.InsertAsync(new Dictionary<string, object?> { ["name"] = Prefix + id, ["value"] = Json(state).ToJsonString() }, ct);
        return new JsonObject { ["ok"] = true, ["id"] = id, ["chunk"] = ChunkBytes };
    }

    /// <summary>One piece, in order. A piece sent again (the answer got lost) is taken as already there.</summary>
    public static async Task<JsonObject> PartAsync(IPluginContext ctx, MomentsStore store, Viewer v, CancellationToken ct)
    {
        var r = ctx.Request;
        if (await LoadAsync(store, v, r.F("id") ?? "", ct) is not var (row, s)) return Error(ctx.T("moments.video.lost"));
        if (!int.TryParse(r.F("n"), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return Error(ctx.T("moments.video.lost"));
        if (n < s.Parts) return new JsonObject { ["ok"] = true };
        if (n != s.Parts || r.File("part") is not { Length: > 0 } part || part.Length > ChunkBytes || s.Received + part.Length > s.Size) return Error(ctx.T("moments.video.lost"));
        using (var stream = part.OpenRead()) await ctx.Files.SaveAsync(Part(r.F("id")!, n), stream, ct);
        await store.Meta.UpdateAsync(row.Id, new Dictionary<string, object?> { ["value"] = Json(s with { Received = s.Received + part.Length, Parts = n + 1 }).ToJsonString() }, ct);
        return new JsonObject { ["ok"] = true };
    }

    /// <summary>Joins the pieces into the film (cleaned on the way), stores the poster and the row. Answers like a photo upload.</summary>
    public static async Task<(string? Error, bool Pending)> DoneAsync(IPluginContext ctx, MomentsStore store, Viewer v, CancellationToken ct)
    {
        var r = ctx.Request;
        var id = r.F("id") ?? "";
        if (await LoadAsync(store, v, id, ct) is not var (row, s)) return (ctx.T("moments.video.lost"), false);
        if (s.Received != s.Size) return (ctx.T("moments.video.lost"), false);
        var pending = ctx.Settings.GetBool(MomentsSettings.UploadApproval) && !v.Admin;
        try
        {
            // the type, from the first bytes
            var head = new byte[16];
            await using (var first = await ctx.Files.OpenAsync(Part(id, 0), ct))
            {
                var got = first is null ? 0 : await first.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
                if (Sniff(head.AsSpan(0, got)) is not { Length: > 0 }) return (ctx.T("moments.video.format", s.Name), false);
            }
            var ext = Sniff(head);
            var key = Guid.NewGuid().ToString("N");
            var file = "v/" + key + (ext == ".webm" ? ".webm" : Path.GetExtension(s.Name).ToLowerInvariant() is ".mov" or ".qt" ? ".mov" : ext);
            var info = new FilmInfo();
            await using (var joined = new ChunkStream(Join(ctx.Files, id, s.Parts, ext == ".mp4" ? info : null, ct)))
                await ctx.Files.SaveAsync(file, joined, ct);

            // the poster: what the browser drew (only a picture the server recognises), else none
            async Task<string> Poster(string field, string suffix, long max)
            {
                if (r.File(field) is not { Length: > 0 } f || f.Length > max) return "";
                using var ms = new MemoryStream();
                using (var fs = f.OpenRead()) await fs.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();
                var e = MomentsImage.Sniff(bytes);
                if (e.Length == 0) return "";
                var name = "p/" + key + suffix + e;
                ms.Position = 0;
                await ctx.Files.SaveAsync(name, ms, ct);
                return name;
            }
            var view = await Poster("view", "-v", MomentsUpload.MaxView);
            var thumb = await Poster("thumb", "-t", MomentsUpload.MaxThumb);

            int.TryParse(r.F("w"), NumberStyles.None, CultureInfo.InvariantCulture, out var w);
            int.TryParse(r.F("h"), NumberStyles.None, CultureInfo.InvariantCulture, out var h);
            var duration = double.TryParse(r.F("duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d is > 0 and < 86400 ? (int)Math.Round(d) : info.Seconds;
            var taken = info.Recorded is DateTime rec && rec.Year >= 2000 && rec < DateTime.UtcNow.AddDays(1) ? M.Local(rec).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : "";
            if (taken.Length == 0 && s.Modified > 946_684_800_000L && s.Modified < 4_102_444_800_000L && DateTimeOffset.FromUnixTimeMilliseconds(s.Modified).UtcDateTime is var mod && mod < DateTime.UtcNow.AddDays(1))
                taken = M.Local(mod).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            if (taken.Length == 0) taken = M.NowLocal;

            var photoId = await store.Photos.InsertAsync(new Dictionary<string, object?>
            {
                ["guest_id"] = v.Id, ["file"] = file, ["view"] = view, ["thumb"] = thumb, ["file_name"] = s.Name, ["size"] = s.Size,
                ["width"] = Math.Clamp(w, 0, 20000), ["height"] = Math.Clamp(h, 0, 20000), ["taken"] = taken, ["caption"] = "",
                ["status"] = pending ? MomentsStore.Pending : MomentsStore.Approved, ["featured"] = false, ["created"] = DateTime.UtcNow,
                ["approved_at"] = pending ? null : DateTime.UtcNow, ["album_id"] = s.Album, ["kind"] = Kind, ["duration"] = duration
            }, ct);
            if (s.Tag > 0) await store.AddTagAsync(photoId, s.Tag, v.Id, ct);
            return (null, pending);
        }
        finally
        {
            await DropAsync(ctx.Files, store, row.Id, id, s.Parts, ct);
        }
    }

    private static JsonObject Error(string text) => new() { ["ok"] = false, ["error"] = text };

    private static async Task DropAsync(IPluginFiles files, MomentsStore store, long rowId, string id, int parts, CancellationToken ct)
    {
        for (var n = 0; n < parts; n++) await files.DeleteAsync(Part(id, n), ct);
        await store.Meta.DeleteAsync(rowId, ct);
    }

    /// <summary>The job: uploads nobody finished within a day lose their pieces.</summary>
    public static async Task CleanupAsync(IPluginContext ctx)
    {
        var store = new MomentsStore(ctx.Data);
        foreach (var row in await store.Meta.QueryAsync(DataQuery.Everything, default))
        {
            var name = row.Text("name");
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            var s = Read(row.Text("value"));
            if (s is not null && DateTime.UtcNow - s.At < Stale) continue;
            await DropAsync(ctx.Files, store, row.Id, name[Prefix.Length..], s?.Parts ?? 0, default);
        }
    }

    // ---- joining and cleaning ----------------------------------------------------------------------------------------------

    /// <summary>What the film's header says (filled while it is joined).</summary>
    public sealed class FilmInfo
    {
        public DateTime? Recorded { get; set; }
        public int Seconds { get; set; }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Join(IPluginFiles files, string id, int parts, FilmInfo? iso, [EnumeratorCancellation] CancellationToken ct)
    {
        var all = new PartsReader(files, id, parts);
        try
        {
            if (iso is null) { await foreach (var c in all.RestAsync(ct)) yield return c; yield break; }
            await foreach (var c in Clean(all, iso, ct)) yield return c;
        }
        finally { await all.DisposeAsync(); }
    }

    /// <summary>Walks the top-level boxes; the <c>moov</c> box is read whole, cleaned and passed on, every other box is copied through.</summary>
    internal static async IAsyncEnumerable<ReadOnlyMemory<byte>> Clean(PartsReader src, FilmInfo info, [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            var header = await src.ReadAsync(8, ct);
            if (header.Length < 8) { if (header.Length > 0) yield return header; yield break; }
            long size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var head = header;
            if (size == 1)
            {
                var large = await src.ReadAsync(8, ct);
                head = [.. header, .. large];
                if (large.Length < 8) { yield return head; yield break; }
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(large);
            }
            if (size == 0 || size < head.Length)
            {
                // "to the end of the file" (or nonsense): the rest as it is
                yield return head;
                await foreach (var c in src.RestAsync(ct)) yield return c;
                yield break;
            }
            var body = size - head.Length;
            if (type == "moov" && body <= 64 * 1024 * 1024)
            {
                var box = await src.ReadAsync((int)body, ct);
                Scrub(box, info);
                yield return head;
                yield return box;
                if (box.Length < body) yield break;
                continue;
            }
            yield return head;
            await foreach (var c in src.CopyAsync(body, ct)) yield return c;
        }
    }

    private static readonly Regex Iso6709 = new(@"[+-]\d{2}(\.\d+)?[+-]\d{3}(\.\d+)?([+-]\d+(\.\d+)?)?/?", RegexOptions.CultureInvariant);

    /// <summary>Zeros every location in the header and reads the recording time and length from <c>mvhd</c>.</summary>
    internal static void Scrub(byte[] box, FilmInfo info)
    {
        var text = Encoding.Latin1.GetString(box);
        foreach (Match m in Iso6709.Matches(text))
        {
            // a real one has a decimal point (phones write "+48.8577+002.2950+035.000/"); digits alone could be anything in a binary header
            if (!m.Value.Contains('.')) continue;
            for (var i = m.Index; i < m.Index + m.Length; i++) if (box[i] is >= (byte)'0' and <= (byte)'9') box[i] = (byte)'0';
        }
        var at = text.IndexOf("mvhd", StringComparison.Ordinal);
        if (at < 0 || at + 32 > box.Length) return;
        var p = at + 4;
        var version = box[p];
        ulong created; uint scale; ulong length;
        if (version == 1 && p + 36 <= box.Length)
        {
            created = BinaryPrimitives.ReadUInt64BigEndian(box.AsSpan(p + 4));
            scale = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(p + 20));
            length = BinaryPrimitives.ReadUInt64BigEndian(box.AsSpan(p + 24));
        }
        else
        {
            created = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(p + 4));
            scale = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(p + 12));
            length = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(p + 16));
        }
        if (created > 0 && created < 10_000_000_000UL) info.Recorded = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(created);
        if (scale > 0 && length / scale < 86400) info.Seconds = (int)Math.Round((double)length / scale);
    }

    /// <summary>Reads the pieces one after the other as one stream of bytes.</summary>
    internal sealed class PartsReader(IPluginFiles files, string id, int parts) : IAsyncDisposable
    {
        private int _next;
        private Stream? _current;

        private async Task<Stream?> CurrentAsync(CancellationToken ct)
        {
            while (_current is null && _next < parts)
            {
                _current = await files.OpenAsync(Part(id, _next++), ct);
            }
            return _current;
        }

        private async Task<int> ReadSomeAsync(Memory<byte> into, CancellationToken ct)
        {
            while (await CurrentAsync(ct) is Stream s)
            {
                var n = await s.ReadAsync(into, ct);
                if (n > 0) return n;
                await s.DisposeAsync();
                _current = null;
            }
            return 0;
        }

        /// <summary>Exactly <paramref name="count"/> bytes, or fewer at the end.</summary>
        public async Task<byte[]> ReadAsync(int count, CancellationToken ct)
        {
            var buf = new byte[count];
            var got = 0;
            while (got < count)
            {
                var n = await ReadSomeAsync(buf.AsMemory(got), ct);
                if (n == 0) break;
                got += n;
            }
            return got == count ? buf : buf[..got];
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> CopyAsync(long count, [EnumeratorCancellation] CancellationToken ct)
        {
            while (count > 0)
            {
                var buf = new byte[(int)Math.Min(count, 1024 * 1024)];
                var n = await ReadSomeAsync(buf, ct);
                if (n == 0) yield break;
                count -= n;
                yield return buf.AsMemory(0, n);
            }
        }

        public IAsyncEnumerable<ReadOnlyMemory<byte>> RestAsync(CancellationToken ct) => CopyAsync(long.MaxValue, ct);

        public async ValueTask DisposeAsync()
        {
            if (_current is not null) await _current.DisposeAsync();
            _current = null;
        }
    }

    /// <summary>A read-only stream over pieces handed out by an async sequence — what <see cref="IPluginFiles.SaveAsync"/> reads to its end.</summary>
    internal sealed class ChunkStream(IAsyncEnumerable<ReadOnlyMemory<byte>> source) : Stream
    {
        private readonly IAsyncEnumerator<ReadOnlyMemory<byte>> _it = source.GetAsyncEnumerator();
        private ReadOnlyMemory<byte> _left;
        private bool _done;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_left.IsEmpty && !_done)
            {
                if (await _it.MoveNextAsync()) _left = _it.Current;
                else _done = true;
            }
            if (_left.IsEmpty) return 0;
            var n = Math.Min(buffer.Length, _left.Length);
            _left[..n].CopyTo(buffer);
            _left = _left[n..];
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        private bool _disposed;
        public override async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _it.DisposeAsync();
            GC.SuppressFinalize(this);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed) { _disposed = true; _it.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            base.Dispose(disposing);
        }
    }
}
