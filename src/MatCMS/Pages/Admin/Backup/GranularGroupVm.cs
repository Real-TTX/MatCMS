namespace MatCMS.Pages.Admin.Backup;

/// <summary>View model for one collapsible backup section that supports per-item selection
/// (Templates / Pages / Forms). Rendered by _GranularGroup.cshtml for both the export form and
/// the scheduler form.</summary>
public class GranularGroupVm
{
    public string SectionLabel { get; set; } = "";
    /// <summary>Form field name of the section include checkbox (e.g. "IncTemplates" or "Schedule.Templates").</summary>
    public string SectionName { get; set; } = "";
    public bool SectionChecked { get; set; } = true;
    /// <summary>Render a hidden "false" fallback so unchecking posts false (needed for the bound Schedule object).</summary>
    public bool RenderHiddenFalse { get; set; }
    /// <summary>Form field name of the per-item checkboxes (e.g. "TemplateNames", "PageKeys", "FormSlugs").</summary>
    public string ItemName { get; set; } = "";
    public List<Item> Items { get; set; } = new();

    /// <summary>A section with no per-item children (e.g. Menus, Settings) — rendered as a matching
    /// box but without an expand arrow or item list.</summary>
    public bool Flat { get; set; }

    public record Item(string Value, string Label, bool Checked);
}
