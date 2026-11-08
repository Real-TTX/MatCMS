using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Moments;

/// <summary>
/// What the plugin needs to know about a picture without an image library: its kind from the first bytes, when it was taken (EXIF), its size (JPEG
/// frame header, PNG IHDR) — and the original's location removed before anybody else can download it (EXIF GPS block and XMP with GPS, blanked in place).
/// Every reader is defensive: a cut file or a bogus offset gives "nothing found", never an exception.
/// </summary>
public static class MomentsImage
{
    /// <summary>".jpg", ".png", ".webp" from the first bytes, or "" for anything else (HEIC, GIF, a renamed script …).</summary>
    public static string Sniff(byte[] b)
    {
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G') return ".png";
        if (b.Length > 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') return ".webp";
        return "";
    }

    public static string ContentType(string ext) => ext switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };

    /// <summary>The APP segments of a JPEG before the image data: (marker, start of the payload, end of the segment).</summary>
    private static IEnumerable<(byte Marker, int Payload, int End)> Segments(byte[] b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) yield break;
        var p = 2;
        while (p + 4 <= b.Length && b[p] == 0xFF)
        {
            var marker = b[p + 1];
            if (marker == 0xFF) { p++; continue; }                     // fill byte
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { p += 2; continue; }   // markers without a length
            if (marker is 0xDA or 0xD9) yield break;                   // image data starts: nothing more of interest
            var len = (b[p + 2] << 8) | b[p + 3];
            if (len < 2 || p + 2 + len > b.Length) yield break;
            yield return (marker, p + 4, p + 2 + len);
            p += 2 + len;
        }
    }

    /// <summary>"yyyy-MM-ddTHH:mm:ss" (the camera's local time as it wrote it) or "".</summary>
    public static string TakenAt(byte[] b)
    {
        try
        {
            foreach (var (marker, payload, end) in Segments(b))
                if (marker == 0xE1 && payload + 6 < end && Encoding.ASCII.GetString(b, payload, 4) == "Exif")
                    return FromTiff(b, payload + 6, end);
        }
        catch { /* a damaged file has no date we could trust */ }
        return "";
    }

    private static string FromTiff(byte[] b, int t, int end)
    {
        var le = b[t] == 0x49;
        int U16(int o) => le ? b[o] | (b[o + 1] << 8) : (b[o] << 8) | b[o + 1];
        int U32(int o) => le ? b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24) : (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        string? Find(int ifd, int tag, out int sub)
        {
            sub = 0;
            if (ifd <= 0 || t + ifd + 2 > end) return null;
            var n = U16(t + ifd);
            for (var i = 0; i < n && i < 300; i++)
            {
                var e = t + ifd + 2 + i * 12;
                if (e + 12 > end) break;
                var id = U16(e);
                if (id == 0x8769) sub = U32(e + 8);
                if (id == tag && U16(e + 2) == 2)
                {
                    var count = U32(e + 4);
                    var off = count <= 4 ? e + 8 : t + U32(e + 8);
                    if (count is >= 19 and < 64 && off >= t && off + 19 <= end) return Encoding.ASCII.GetString(b, off, 19);
                }
            }
            return null;
        }
        var plain = Find(U32(t + 4), 0x0132, out var exifIfd);
        var original = exifIfd > 0 ? Find(exifIfd, 0x9003, out _) : null;
        return Parse(original) is { Length: > 0 } o ? o : Parse(plain);
    }

    private static string Parse(string? s) =>
        s is not null && DateTime.TryParseExact(s.Trim('\0', ' '), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year > 1990
            ? d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// Removes where the photo was taken, in place: the EXIF GPS directory is emptied (its entries and the values they point to are zeroed, its count set
    /// to 0 — a valid, empty directory, so every reader still opens the file and keeps the orientation) and an XMP packet that names a position is blanked.
    /// True when something was removed.
    /// </summary>
    public static bool StripLocation(byte[] b)
    {
        var removed = false;
        try
        {
            foreach (var (marker, payload, end) in Segments(b).ToList())
            {
                if (marker != 0xE1) continue;
                if (payload + 6 < end && Encoding.ASCII.GetString(b, payload, 4) == "Exif") removed |= StripGps(b, payload + 6, end);
                else if (end - payload > 30 && Encoding.ASCII.GetString(b, payload, Math.Min(28, end - payload)).StartsWith("http://ns.adobe.com/xap/1.0/", StringComparison.Ordinal))
                {
                    var text = Encoding.UTF8.GetString(b, payload, end - payload);
                    if (text.Contains("GPSLatitude", StringComparison.Ordinal) || text.Contains("GPSLongitude", StringComparison.Ordinal))
                    {
                        // keep the namespace header (so the segment stays an XMP segment), turn the packet itself into spaces
                        var start = Array.IndexOf(b, (byte)0, payload, end - payload);
                        for (var i = start < 0 ? payload + 29 : start + 1; i < end; i++) b[i] = (byte)' ';
                        removed = true;
                    }
                }
            }
        }
        catch { /* a file we cannot read is stored as it is — it has no location we could find either */ }
        return removed;
    }

    private static bool StripGps(byte[] b, int t, int end)
    {
        if (t + 8 > end) return false;
        var le = b[t] == 0x49;
        int U16(int o) => le ? b[o] | (b[o + 1] << 8) : (b[o] << 8) | b[o + 1];
        int U32(int o) => le ? b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24) : (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        var ifd0 = U32(t + 4);
        if (ifd0 <= 0 || t + ifd0 + 2 > end) return false;
        var gps = 0;
        var n0 = U16(t + ifd0);
        for (var i = 0; i < n0 && i < 300; i++)
        {
            var e = t + ifd0 + 2 + i * 12;
            if (e + 12 > end) break;
            if (U16(e) == 0x8825) { gps = U32(e + 8); break; }
        }
        if (gps <= 0 || t + gps + 2 > end) return false;
        var g = t + gps;
        var n = U16(g);
        if (n == 0) return false;
        for (var i = 0; i < n && i < 100; i++)
        {
            var e = g + 2 + i * 12;
            if (e + 12 > end) break;
            var size = (long)U32(e + 4) * (U16(e + 2) switch { 3 => 2, 4 or 9 => 4, 5 or 10 or 12 => 8, _ => 1 });
            if (size > 4)
            {
                var off = t + U32(e + 8);
                if (off >= t && off + size <= end) Array.Clear(b, off, (int)size);
            }
        }
        // the entries and the pointer to a next directory, then the count: an empty, valid directory
        Array.Clear(b, g + 2, Math.Min(n * 12 + 4, end - (g + 2)));
        b[g] = 0; b[g + 1] = 0;
        return true;
    }

    /// <summary>Width and height from a JPEG's frame header or a PNG's IHDR; (0, 0) when not found.</summary>
    public static (int W, int H) Dimensions(byte[] b)
    {
        try
        {
            if (Sniff(b) == ".png" && b.Length > 24)
                return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
            if (Sniff(b) != ".jpg") return (0, 0);
            var p = 2;
            while (p + 9 < b.Length)
            {
                if (b[p] != 0xFF) { p++; continue; }
                var marker = b[p + 1];
                if (marker == 0xFF) { p++; continue; }
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { p += 2; continue; }
                if (marker is 0xD9 or 0xDA) break;
                var len = (b[p + 2] << 8) | b[p + 3];
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                    return ((b[p + 7] << 8) | b[p + 8], (b[p + 5] << 8) | b[p + 6]);
                p += 2 + len;
            }
        }
        catch { /* not found */ }
        return (0, 0);
    }
}
