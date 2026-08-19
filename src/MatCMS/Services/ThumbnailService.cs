using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace MatCMS.Services;

/// <summary>
/// Scaled-down copies of uploaded images, produced ON FIRST REQUEST and then cached on disk under
/// <c>appdata/thumbs/{width}/</c>.
///
/// <para><b>Why on demand and not at upload.</b> A gallery that is slow today is slow because of the
/// files that are ALREADY on disk — a customer site carries hundreds of them. Generating at upload
/// only ever helps the next picture somebody adds, so it would have needed a second mechanism (a
/// backfill job, a queue, a startup sweep) for exactly the case that matters. On demand is one code
/// path that covers all four cases at once: new uploads, the existing stock, everything a backup
/// restore drops into <c>uploads/</c>, and a thumbs folder someone deleted to reclaim space. It also
/// costs nothing at boot — there is no sweep to block startup on — and it never generates a size
/// nobody asks for.</para>
///
/// <para><b>The originals keep their URLs.</b> Pages, blocks and backups store <c>/uploads/…</c>
/// paths. Those files are not touched, not moved and not re-encoded; a thumbnail is a NEW url
/// (<c>/thumb/{w}/…</c>) beside it. The lightbox still opens the original.</para>
/// </summary>
public class ThumbnailService
{
    /// <summary>
    /// The only widths that may be generated. A closed list, because the width comes off a URL: with
    /// a free-form width one visitor can ask for ten thousand sizes and fill the volume.
    /// <para>320 = the filmstrip and the reference cards' screenshot row (they display at ~104&#160;px,
    /// so this still covers a 3x phone screen). 1200 = a gallery tile or a card's main image, which is
    /// at most ~700&#160;px wide in a two-column layout and covers it at 2x.</para>
    /// </summary>
    public static readonly int[] Widths = [320, 1200];

    /// <summary>
    /// Extensions we scale. GIF is deliberately absent — re-encoding one to a still frame would
    /// silently kill the animation — and so is SVG, which the upload endpoint rejects anyway and which
    /// is already small. Anything not listed simply keeps using its original URL.
    /// </summary>
    private static readonly string[] Scalable = [".png", ".jpg", ".jpeg", ".webp"];

    /// <summary>
    /// At most this many images are decoded at the same time. A gallery of 88 pictures opens 88
    /// requests in one go on the very first view; without a gate they would each take a decode buffer
    /// of several megabytes and the container would spend its whole CPU budget on the one page load
    /// that is supposed to get FASTER. Queued requests wait a few hundred ms instead.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(2, 2);

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ThumbnailService> _log;

    public ThumbnailService(IWebHostEnvironment env, ILogger<ThumbnailService> log)
    {
        _env = env;
        _log = log;
    }

    /// <summary>True when this upload URL can be served as a thumbnail at all.</summary>
    private static bool IsScalable(string fileName) =>
        Scalable.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>
    /// The thumbnail URL for an image the page is about to render, or <paramref name="src"/> itself
    /// when there is nothing to gain. Static and free of state so the Razor blocks can call it
    /// without an injection.
    /// <para>Only OUR OWN uploads are rewritten: an <c>https://…</c> source belongs to someone else's
    /// server and we have no file to scale, and any other site-relative path (a template asset, a
    /// plugin asset) is not in <c>uploads/</c>. In both cases the caller gets its input back
    /// unchanged, so a block that renders an external image renders exactly as before.</para>
    /// </summary>
    public static string Url(string? src, int width)
    {
        if (string.IsNullOrWhiteSpace(src)) return src ?? "";
        if (!src.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase)) return src;
        if (!Widths.Contains(width)) return src;

        var name = src[9..];                       // everything after "/uploads/"
        if (name.Length == 0 || name.Contains('/')) return src;   // uploads/ is flat; a slash is not ours
        if (!IsScalable(name)) return src;

        return $"/thumb/{width}/{name}";
    }

    /// <summary>
    /// Returns the cached thumbnail file for <paramref name="fileName"/> at <paramref name="width"/>,
    /// generating it if this is the first ask. <c>null</c> means "serve the original instead" — the
    /// caller must always have that fallback, because a broken thumbnail may never turn into a broken
    /// image on a customer's live page.
    /// </summary>
    public async Task<string?> GetOrCreateAsync(string fileName, int width, CancellationToken ct = default)
    {
        if (!Widths.Contains(width)) return null;

        // The name comes off the URL. Strip any path, then require it to be exactly what we stored.
        var safe = Path.GetFileName(fileName ?? "");
        if (string.IsNullOrWhiteSpace(safe) || safe != fileName || !IsScalable(safe)) return null;

        var source = Path.Combine(StoragePaths.Uploads(_env), safe);
        if (!File.Exists(source)) return null;

        var dir = Path.Combine(StoragePaths.Thumbs(_env), width.ToString());
        var target = Path.Combine(dir, safe + ".webp");
        if (File.Exists(target)) return target;

        // A file that could not be decoded once cannot be decoded next time either. Without this
        // marker every single view of the page would re-run the failing decode — a page with one
        // corrupt upload would become a permanent CPU drain.
        var failed = target + ".failed";
        if (File.Exists(failed)) return null;

        await Gate.WaitAsync(ct);
        try
        {
            // Someone else may have finished it while we queued on the gate.
            if (File.Exists(target)) return target;
            if (File.Exists(failed)) return null;

            Directory.CreateDirectory(dir);
            // Write beside the target and rename: a reader must never catch a half-written file, and
            // two requests for the same picture must not interleave into one stream.
            var temp = Path.Combine(dir, $".{Guid.NewGuid():N}.tmp");
            try
            {
                using (var image = await Image.LoadAsync(source, ct))
                {
                    // Never enlarge. A 200 px logo asked for at 1200 would gain nothing and lose
                    // sharpness; Max keeps the aspect ratio and simply leaves a smaller image alone.
                    if (image.Width > width)
                        image.Mutate(x => x.Resize(new ResizeOptions
                        {
                            Size = new Size(width, 0),
                            Mode = ResizeMode.Max
                        }));

                    // EXIF/ICC/XMP go: on a phone photo the metadata block alone can outweigh a
                    // thumbnail. Orientation is already applied by the decoder, so nothing rotates.
                    image.Metadata.ExifProfile = null;
                    image.Metadata.IptcProfile = null;
                    image.Metadata.XmpProfile = null;

                    // WebP at 80: visually indistinguishable at these sizes and roughly a third of the
                    // JPEG bytes. It also covers transparency, so a PNG logo keeps its alpha instead
                    // of gaining a white box the way a JPEG fallback would.
                    await image.SaveAsync(temp, new WebpEncoder { Quality = 80 }, ct);
                }
                File.Move(temp, target, overwrite: true);
                return target;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
                _log.LogWarning(ex, "Vorschaubild für {File} bei {Width}px fehlgeschlagen – Original wird ausgeliefert.", safe, width);
                try { await File.WriteAllBytesAsync(failed, [], ct); } catch { /* best effort */ }
                return null;
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
