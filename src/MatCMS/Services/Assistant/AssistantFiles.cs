using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MatCMS.Data;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace MatCMS.Services.Assistant;

/// <summary>One file attached to a message, after it was taken in.</summary>
/// <param name="Name">The original file name.</param>
/// <param name="Kind">"image" | "pdf" | "document" | "text".</param>
/// <param name="MediaUrl">Where it is in the media library (pictures, PDFs, Word files), or null.</param>
/// <param name="ModelFile">For a picture: the scaled-down copy the model looks at, relative to <c>ai-files</c>.</param>
/// <param name="Text">For a document: the text read out of it (bounded), or null.</param>
/// <param name="Error">Why it could not be used, or null.</param>
public sealed record AttachedFile(string Name, string Kind, string? MediaUrl, string? ModelFile, string? Text, string? Error);

/// <summary>
/// Takes in what an editor attaches to a message. Pictures go into the media library — so the assistant can PUT them on a
/// page right away — and a scaled-down copy is kept for the model to look at (a model that cannot see pictures still gets
/// the name and the address). PDFs and Word files go into the library too (a page can link them) and their TEXT goes to
/// the model: reading it here works with every provider, where sending the file only works with some. Plain text files
/// (txt, md, csv, json, html) are read and not stored.
/// </summary>
public sealed class AssistantFiles
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;

    public const int MaxFiles = 6;
    public const int MaxTextChars = 60_000;
    private const long MaxImageBytes = 15L * 1024 * 1024, MaxDocBytes = 25L * 1024 * 1024, MaxTextBytes = 3L * 1024 * 1024;
    private static readonly string[] ImageExt = [".jpg", ".jpeg", ".png", ".webp", ".gif"];
    private static readonly string[] TextExt = [".txt", ".md", ".markdown", ".csv", ".json", ".html", ".htm", ".xml"];

    public AssistantFiles(AppDbContext db, IWebHostEnvironment env)
    {
        _db = db;
        _env = env;
    }

    public async Task<List<AttachedFile>> AcceptAsync(int conversationId, IEnumerable<IFormFile> files, CancellationToken ct)
    {
        var result = new List<AttachedFile>();
        foreach (var f in files.Where(f => f.Length > 0))
        {
            var name = Path.GetFileName(f.FileName);
            if (result.Count >= MaxFiles) { result.Add(new(name, "text", null, null, null, $"Höchstens {MaxFiles} Dateien pro Nachricht.")); continue; }
            var ext = Path.GetExtension(name).ToLowerInvariant();
            try
            {
                if (ImageExt.Contains(ext)) result.Add(await ImageAsync(conversationId, f, name, ext, ct));
                else if (ext == ".svg") result.Add(await SvgAsync(f, name, ct));
                else if (ext == ".pdf") result.Add(await DocumentAsync(f, name, ext, "pdf", ReadPdf, ct));
                else if (ext == ".docx") result.Add(await DocumentAsync(f, name, ext, "document", ReadDocx, ct));
                else if (TextExt.Contains(ext)) result.Add(await TextAsync(f, name, ct));
                else result.Add(new(name, "text", null, null, null, "Dateityp wird nicht unterstützt (Bilder inkl. SVG, PDF, DOCX, TXT, MD, CSV, JSON, HTML)."));
            }
            catch (Exception ex)
            {
                result.Add(new(name, "text", null, null, null, "Konnte nicht gelesen werden: " + ex.Message));
            }
        }
        return result;
    }

    private async Task<AttachedFile> ImageAsync(int conversationId, IFormFile f, string name, string ext, CancellationToken ct)
    {
        if (f.Length > MaxImageBytes) return new(name, "image", null, null, null, "Bild zu groß (höchstens 15 MB).");
        await using var buffer = new MemoryStream();
        await f.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        using var image = await Image.LoadAsync(buffer, ct);   // throws on a file that only claims to be a picture

        var url = await SaveToLibraryAsync(buffer.ToArray(), name, ext, f.ContentType, ct);

        // The model's copy: at most 1600 px, JPEG, no metadata — what vision models read well and what keeps the call small.
        var dir = Path.Combine(StoragePaths.AiFiles(_env), conversationId.ToString());
        Directory.CreateDirectory(dir);
        var file = Guid.NewGuid().ToString("N") + ".jpg";
        image.Mutate(c => c.AutoOrient());
        if (image.Width > 1600 || image.Height > 1600)
            image.Mutate(c => c.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(1600, 1600) }));
        image.Metadata.ExifProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.XmpProfile = null;
        await image.SaveAsJpegAsync(Path.Combine(dir, file), new JpegEncoder { Quality = 82 }, ct);
        return new(name, "image", url, $"{conversationId}/{file}", null, null);
    }

    /// <summary>An SVG (a logo, an icon): cleaned (<see cref="SafeSvg"/>) into the library, and its code goes to the model as text —
    /// no model looks at an SVG as a picture, but every model reads its markup.</summary>
    private async Task<AttachedFile> SvgAsync(IFormFile f, string name, CancellationToken ct)
    {
        if (f.Length > SafeSvg.MaxChars) return new(name, "image", null, null, null, "SVG zu groß (höchstens 1 MB).");
        using var reader = new StreamReader(f.OpenReadStream());
        var safe = SafeSvg.Sanitize(await reader.ReadToEndAsync(ct));
        if (safe is null) return new(name, "image", null, null, null, "Kein gültiges SVG.");
        var url = await SaveSvgAsync(safe, name, ct);
        return new(name, "image", url, null, safe.Length > 20000 ? safe[..20000] + " …[gekürzt]" : safe, null);
    }

    /// <summary>Stores an ALREADY CLEANED SVG in the library (used for attachments and for the assistant's own drawings).</summary>
    public Task<string> SaveSvgAsync(string cleanSvg, string name, CancellationToken ct) =>
        SaveToLibraryAsync(System.Text.Encoding.UTF8.GetBytes(cleanSvg), name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? name : name + ".svg", ".svg", "image/svg+xml", ct);

    private async Task<AttachedFile> DocumentAsync(IFormFile f, string name, string ext, string kind, Func<byte[], string> read, CancellationToken ct)
    {
        if (f.Length > MaxDocBytes) return new(name, kind, null, null, null, "Datei zu groß (höchstens 25 MB).");
        await using var buffer = new MemoryStream();
        await f.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var text = Bound(read(bytes));
        var url = await SaveToLibraryAsync(bytes, name, ext, f.ContentType, ct);
        return new(name, kind, url, null, text, text.Trim().Length == 0 ? "Kein Text gefunden (gescanntes Dokument?)." : null);
    }

    private static async Task<AttachedFile> TextAsync(IFormFile f, string name, CancellationToken ct)
    {
        if (f.Length > MaxTextBytes) return new(name, "text", null, null, null, "Datei zu groß (höchstens 3 MB).");
        using var reader = new StreamReader(f.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return new(name, "text", null, null, Bound(await reader.ReadToEndAsync(ct)), null);
    }

    /// <summary>Stores the file in the uploads folder and the media library, exactly like an upload in the editor (tagged so
    /// it can be found again).</summary>
    private async Task<string> SaveToLibraryAsync(byte[] bytes, string name, string ext, string? contentType, CancellationToken ct)
    {
        var uploads = StoragePaths.Uploads(_env);
        Directory.CreateDirectory(uploads);
        var file = $"{Guid.NewGuid():N}{ext}";
        await File.WriteAllBytesAsync(Path.Combine(uploads, file), bytes, ct);
        var url = $"/uploads/{file}";
        var next = (_db.Media.Max(m => (int?)m.SortOrder) ?? 0) + 1;
        _db.Media.Add(new Models.Media
        {
            Url = url, FileName = name, ContentType = contentType ?? "", SizeBytes = bytes.Length, SortOrder = next, Tags = "KI-Anhang",
        });
        await _db.SaveChangesAsync(ct);
        return url;
    }

    private static string ReadPdf(byte[] bytes)
    {
        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(bytes);
        foreach (var page in doc.GetPages())
        {
            string text;
            try { text = ContentOrderTextExtractor.GetText(page); }
            catch { text = page.Text; }
            sb.Append(text.Trim()).Append("\n\n");
            if (sb.Length > MaxTextChars) break;
        }
        return sb.ToString();
    }

    /// <summary>The text of a .docx: its paragraphs in order (w:p), tabs and line breaks kept. No formatting.</summary>
    private static string ReadDocx(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Kein Word-Dokument.");
        using var s = entry.Open();
        var xml = XDocument.Load(s);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var sb = new StringBuilder();
        foreach (var p in xml.Descendants(w + "p"))
        {
            foreach (var n in p.Descendants())
            {
                if (n.Name == w + "t") sb.Append(n.Value);
                else if (n.Name == w + "tab") sb.Append('\t');
                else if (n.Name == w + "br") sb.Append('\n');
            }
            sb.Append('\n');
            if (sb.Length > MaxTextChars) break;
        }
        return sb.ToString();
    }

    private static string Bound(string text)
    {
        var t = Regex.Replace(text.Replace("\r\n", "\n"), @"\n{3,}", "\n\n").Trim();
        return t.Length > MaxTextChars ? t[..MaxTextChars] + "\n…[gekürzt]" : t;
    }

    /// <summary>The model's copy of a picture as an attachment, or null when it is gone.</summary>
    public Shared.AiAttachment? ModelAttachment(string? modelFile, string name)
    {
        if (string.IsNullOrWhiteSpace(modelFile)) return null;
        var root = Path.GetFullPath(StoragePaths.AiFiles(_env));
        var path = Path.GetFullPath(Path.Combine(root, modelFile));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) return null;
        return new Shared.AiAttachment { Kind = "image", MediaType = "image/jpeg", Name = name, Data = Convert.ToBase64String(File.ReadAllBytes(path)) };
    }

    /// <summary>Removes the model copies of a conversation (the library files stay — they may be on a page by now).</summary>
    public void DeleteConversationFiles(int conversationId)
    {
        var dir = Path.Combine(StoragePaths.AiFiles(_env), conversationId.ToString());
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
