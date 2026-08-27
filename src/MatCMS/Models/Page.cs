namespace MatCMS.Models;

/// <summary>Who may open a page on the public site.</summary>
public enum PageAccess
{
    /// <summary>Anyone. The default, and what every existing page stays.</summary>
    Public = 0,

    /// <summary>Only a logged-in <see cref="SiteMember"/> — optionally narrowed to one role via
    /// <see cref="Page.RequiredRole"/>.</summary>
    Members = 1
}

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

    /// <summary>Optional template this page renders with, overriding the site's active template. Null =
    /// use the active template. Lets one site run more than one design/menu — e.g. a public area and a
    /// members-only area with their own navigation — without separate installations.</summary>
    public int? TemplateId { get; set; }

    /// <summary>Per-page overrides for the (per-page or active) template's parameters, as a JSON object
    /// (<c>{"bereich":"intern"}</c>). Lets ONE template render differently on this page via {{param:id}}
    /// and {{#if:id}} — e.g. a members area that reuses the public template but swaps its menu. Empty/null
    /// = the template's own values apply, so every existing page is unchanged.</summary>
    public string? TemplateParamsJson { get; set; }

    /// <summary>Optional page-specific CSS, injected in a &lt;style&gt; only on this page's response
    /// (so it is naturally scoped to the page — no selector prefixing needed). Admin-entered and
    /// output raw, exactly like the template's CustomCss and the html block.</summary>
    public string? CustomCss { get; set; }

    /// <summary>Whether this page is public or restricted to logged-in members. Default
    /// <see cref="PageAccess.Public"/>, so every existing page is unchanged.</summary>
    public PageAccess Access { get; set; } = PageAccess.Public;

    /// <summary>When <see cref="Access"/> is <see cref="PageAccess.Members"/>: the single role a
    /// member must hold (a <see cref="SiteRole.Name"/>). Empty = any logged-in member suffices.</summary>
    public string? RequiredRole { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ContentBlock> Blocks { get; set; } = new();

    public string DisplayNavLabel => string.IsNullOrWhiteSpace(NavLabel) ? Title : NavLabel!;
}
