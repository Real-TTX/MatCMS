namespace MatCMS.Models;

/// <summary>
/// A user-defined block type ("component"): a set of fields plus an HTML template with
/// {{field}} placeholders. Surfaced in the block picker like a built-in block.
/// </summary>
public class Component
{
    public int Id { get; set; }

    /// <summary>Unique type slug, e.g. "hero-cta".</summary>
    public string Type { get; set; } = "";

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Optional Tabler icon name (e.g. "photo") shown in the component list + block picker.</summary>
    public string Icon { get; set; } = "";

    /// <summary>Field definitions as JSON: [{ "id", "label", "type" }].</summary>
    public string FieldsJson { get; set; } = "[]";

    /// <summary>HTML template with {{fieldId}} placeholders.</summary>
    public string TemplateHtml { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
