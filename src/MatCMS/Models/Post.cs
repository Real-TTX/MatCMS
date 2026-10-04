namespace MatCMS.Models;

/// <summary>A blog post ("Beitrag"): title image, heading, rich content, tags and optional attachments.</summary>
public class Post
{
    public int Id { get; set; }

    /// <summary>Heading of the post.</summary>
    public string Title { get; set; } = "";

    /// <summary>URL-safe slug; the post is served at <c>/beitrag/{slug}</c>.</summary>
    public string Slug { get; set; } = "";

    /// <summary>Title image URL (shown on cards + at the top of the post). Optional.</summary>
    public string? TitleImage { get; set; }

    /// <summary>Short teaser shown on cards / listings.</summary>
    public string Excerpt { get; set; } = "";

    /// <summary>Rich HTML body.</summary>
    public string ContentHtml { get; set; } = "";

    /// <summary>Comma-separated tags (same convention as media tags).</summary>
    public string Tags { get; set; } = "";

    /// <summary>Optional attachments (images/files) as a JSON array of {url,name}.</summary>
    public string AttachmentsJson { get; set; } = "[]";

    /// <summary>Images shown as a gallery in the post (1:N, e.g. an Instagram carousel), as a JSON array
    /// of {url,alt}. Separate from <see cref="AttachmentsJson"/>, which is a download list.</summary>
    public string GalleryJson { get; set; } = "[]";

    /// <summary>How the gallery is shown: "carousel" (swipe, like Instagram) or "grid".</summary>
    public string GalleryLayout { get; set; } = "carousel";

    /// <summary>Where the post was taken from (e.g. the Instagram post), shown as "Quelle: …". Optional.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>Label for <see cref="SourceUrl"/>, e.g. "Instagram · @name". Empty = the host name.</summary>
    public string? SourceName { get; set; }

    /// <summary>Content locale (matches the page locale scheme).</summary>
    public string Locale { get; set; } = "de";

    public bool IsPublished { get; set; } = false;

    /// <summary>Publication date (used for ordering + display).</summary>
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
