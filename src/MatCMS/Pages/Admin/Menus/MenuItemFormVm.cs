namespace MatCMS.Pages.Admin.Menus;

/// <summary>Shared view model for the menu-item form (used by both Create and Edit).</summary>
public class MenuItemFormVm
{
    public string Menu { get; set; } = "";
    /// <summary>Content language the new item belongs to (Create only; carried through as a hidden field
    /// so a validation re-render keeps it). Edit leaves the existing item's locale untouched.</summary>
    public string? Locale { get; set; }
    public string? Label { get; set; }
    public string? Url { get; set; }
    public string? Icon { get; set; }
    public bool OpenInNewTab { get; set; }
    public int? ParentId { get; set; }
    public List<MatCMS.Models.Menu> Menus { get; set; } = new();
    public List<MatCMS.Models.Page> Pages { get; set; } = new();

    /// <summary>Candidate parent items (same menu + locale, minus self/descendants) for the
    /// "übergeordneter Eintrag" picker. Empty = only top-level possible.</summary>
    public List<MatCMS.Models.MenuItem> ParentOptions { get; set; } = new();

    // Per-page differences.
    public string QuickPickHelpKey { get; set; } = "menus.quickPickHelpCreate";
    public string SubmitLabelKey { get; set; } = "action.save";
    public bool AutofocusLabel { get; set; }

    /// <summary>Where "Zurück" leads. The link sits in the partial's action row NEXT to the submit
    /// button — it used to hang in the page head, split from the button that saves the same form.
    /// Only the two pages know their own way back (a menu item edited from a menu returns to that
    /// menu, not to the overview), so the URL is built there and handed in.</summary>
    public string BackUrl { get; set; } = "";
}
