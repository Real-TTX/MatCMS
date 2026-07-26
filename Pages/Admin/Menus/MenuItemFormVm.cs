namespace MatCMS.Pages.Admin.Menus;

/// <summary>Shared view model for the menu-item form (used by both Create and Edit).</summary>
public class MenuItemFormVm
{
    public string Menu { get; set; } = "";
    public string? Label { get; set; }
    public string? Url { get; set; }
    public string? Icon { get; set; }
    public bool OpenInNewTab { get; set; }
    public List<MatCMS.Models.Menu> Menus { get; set; } = new();
    public List<MatCMS.Models.Page> Pages { get; set; } = new();

    // Per-page differences.
    public string QuickPickHelpKey { get; set; } = "menus.quickPickHelpCreate";
    public string SubmitLabelKey { get; set; } = "action.save";
    public bool AutofocusLabel { get; set; }
}
