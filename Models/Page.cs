namespace MatCMS.Models;

public class Page
{
    public int Id { get; set; }

    /// <summary>Internal / admin title.</summary>
    public string Title { get; set; } = "";

    /// <summary>URL segment, e.g. "ueber-uns". "home" is served at "/".</summary>
    public string Slug { get; set; } = "";

    /// <summary>Label used in navigation (falls back to Title).</summary>
    public string? NavLabel { get; set; }

    public bool IsPublished { get; set; } = true;
    public bool ShowInNav { get; set; }
    public bool ShowInFooter { get; set; }
    public int NavOrder { get; set; }
    public int FooterOrder { get; set; }

    public string? MetaDescription { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ContentBlock> Blocks { get; set; } = new();

    public string DisplayNavLabel => string.IsNullOrWhiteSpace(NavLabel) ? Title : NavLabel!;
}
