using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Verein;

/// <summary>
/// When a photo was taken, read from its JPEG EXIF block (DateTimeOriginal, else DateTime) — a few bytes of TIFF structure, no image library.
/// Anything unexpected (no EXIF, a cut file, a bogus offset) gives "" and the upload date is used instead; it never throws.
/// </summary>
public static class VereinExif
{
    /// <summary>"yyyy-MM-ddTHH:mm" (local time of the camera, as it wrote it) or "".</summary>
    public static string TakenAt(byte[] b)
    {
        try
        {
            if (b.Length < 12 || b[0] != 0xFF || b[1] != 0xD8) return "";
            var p = 2;
            while (p + 4 < b.Length && b[p] == 0xFF)
            {
                var marker = b[p + 1];
                var len = (b[p + 2] << 8) | b[p + 3];
                if (marker == 0xE1 && p + 10 < b.Length && Encoding.ASCII.GetString(b, p + 4, 4) == "Exif")
                    return FromTiff(b, p + 10, Math.Min(b.Length, p + 2 + len));
                if (marker is 0xDA or 0xD9) break;   // image data starts: no EXIF before it
                p += 2 + len;
            }
        }
        catch { /* a damaged file has no date we could trust */ }
        return "";
    }

    private static string FromTiff(byte[] b, int t, int end)
    {
        var le = b[t] == 0x49;   // "II" little endian, "MM" big endian
        int U16(int o) => le ? b[o] | (b[o + 1] << 8) : (b[o] << 8) | b[o + 1];
        int U32(int o) => le ? b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24) : (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        string? Find(int ifd, int tag, out int sub)
        {
            sub = 0;
            if (ifd <= 0 || t + ifd + 2 > end) return null;
            var n = U16(t + ifd);
            for (var i = 0; i < n && i < 200; i++)
            {
                var e = t + ifd + 2 + i * 12;
                if (e + 12 > end) break;
                var id = U16(e);
                if (id == 0x8769) sub = U32(e + 8);
                if (id == tag && U16(e + 2) == 2)   // ASCII
                {
                    var count = U32(e + 4);
                    var off = count <= 4 ? e + 8 : t + U32(e + 8);
                    if (count is >= 19 and < 64 && off + 19 <= end) return Encoding.ASCII.GetString(b, off, 19);
                }
            }
            return null;
        }
        var ifd0 = U32(t + 4);
        var plain = Find(ifd0, 0x0132, out var exifIfd);                         // DateTime (when the file was last written)
        var original = exifIfd > 0 ? Find(exifIfd, 0x9003, out _) : null;      // DateTimeOriginal (when the shutter clicked)
        return Parse(original) is { Length: > 0 } o ? o : Parse(plain);
    }

    private static string Parse(string? s) =>
        s is not null && DateTime.TryParseExact(s.Trim('\0', ' '), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year > 1990
            ? d.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) : "";
}
