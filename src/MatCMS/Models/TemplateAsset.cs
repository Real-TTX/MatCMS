namespace MatCMS.Models;

/// <summary>
/// A file attached to a <see cref="Template"/> and served as a real static asset — so a template can
/// bring its own JavaScript, CSS, fonts or images (e.g. self-hosting a clock library instead of a CDN)
/// without touching the app's wwwroot.
/// <para>Referenced from the template's HTML/CSS/JS by the token <c>{{asset:filename}}</c>, which
/// resolves to <c>/template-assets/{templateId}/{filename}</c>. Bytes live in the row (templates carry
/// a handful of small files, and this keeps them inside the backup/restore of the database).</para>
/// </summary>
public class TemplateAsset
{
    public int Id { get; set; }

    public int TemplateId { get; set; }
    public Template? Template { get; set; }

    /// <summary>File name, unique per template (e.g. <c>flipclock.js</c>). This is what
    /// <c>{{asset:…}}</c> and the serving URL use, so it is the stable identity.</summary>
    public string Name { get; set; } = "";

    /// <summary>MIME type sent when the file is served — must be right for the browser to run a script
    /// or apply a stylesheet.</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    public byte[] Bytes { get; set; } = System.Array.Empty<byte>();

    public long SizeBytes => Bytes.LongLength;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
