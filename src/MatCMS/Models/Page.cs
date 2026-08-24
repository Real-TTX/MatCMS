namespace MatCMS.Models;

public class Page
{
    public int Id { get; set; }

    /// <summary>Internal / admin title.</summary>
    public string Title { get; set; } = "";

    /// <summary>URL segment, e.g. "ueber-uns". "home" is served at "/".</summary>
    public string Slug { get; set; } = "";

    /// <summary>
    /// Content locale of this page (e.g. "de", "en"). The default locale ("de") is served at the
    /// root URLs (/, /kontakt); other locales are served under a culture prefix (/en, /en/about).
    /// A given (Slug, Locale) pair is unique — the same slug may exist once per locale.
    /// </summary>
    public string Locale { get; set; } = "de";

    /// <summary>
    /// Groups pages that are translations of each other (one page per locale). Assigned as a GUID
    /// when a page is created; shared with its translations.
    /// </summary>
    public string? TranslationGroup { get; set; }

    /// <summary>Label used in navigation (falls back to Title).</summary>
    public string? NavLabel { get; set; }

    public bool IsPublished { get; set; } = true;
    public bool ShowInNav { get; set; }
    public bool ShowInFooter { get; set; }
    public int NavOrder { get; set; }
    public int FooterOrder { get; set; }

    public string? MetaDescription { get; set; }

    /// <summary>Optional page-specific CSS, injected in a &lt;style&gt; only on this page's response
    /// (so it is naturally scoped to the page — no selector prefixing needed). Admin-entered and
    /// output raw, exactly like the template's CustomCss and the html block.</summary>
    public string? CustomCss { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ContentBlock> Blocks { get; set; } = new();

    public string DisplayNavLabel => string.IsNullOrWhiteSpace(NavLabel) ? Title : NavLabel!;
}
