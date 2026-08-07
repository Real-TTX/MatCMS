namespace MatCMS.Models;

/// <summary>A single submission of a <see cref="Form"/>. The submitted values are
/// stored as JSON (an ordered array of {id,label,value}) so any form shape can be shown.</summary>
public class FormSubmission
{
    public int Id { get; set; }

    public int FormId { get; set; }
    public Form? Form { get; set; }

    /// <summary>Submitted values serialized as JSON: [{ "id", "label", "value" }].</summary>
    public string DataJson { get; set; } = "[]";

    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
