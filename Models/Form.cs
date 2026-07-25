namespace MatCMS.Models;

/// <summary>
/// A user-defined form built with the visual form builder. The actual fields
/// (an ordered array of elements) live in <see cref="DefinitionJson"/>.
/// Rendered on the public site via the "form" block.
/// </summary>
public class Form
{
    public int Id { get; set; }

    /// <summary>Admin-facing name, e.g. "Kontakt".</summary>
    public string Name { get; set; } = "";

    /// <summary>Unique key used to reference the form from a "form" block.</summary>
    public string Slug { get; set; } = "";

    /// <summary>The form fields serialized as a JSON array of elements.</summary>
    public string DefinitionJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<FormSubmission> Submissions { get; set; } = new();
}
