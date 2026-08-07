namespace MatCMS.Content;

/// <summary>View model handed to the shared <c>Blocks/_FormRender</c> partial.</summary>
public class FormRenderModel
{
    public int FormId { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Heading { get; set; }
    public string? Intro { get; set; }

    /// <summary>Custom submit-button label (empty = localized default).</summary>
    public string? SubmitLabel { get; set; }
    public List<FormElement> Elements { get; set; } = new();

    /// <summary>Preview mode: render a non-submitting form (used in the builder iframe).</summary>
    public bool Preview { get; set; }

    /// <summary>When true, the preview also emits the select-on-click builder bridge script.</summary>
    public bool Builder { get; set; }

    public string? Success { get; set; }
    public List<string> Errors { get; set; } = new();
    public Dictionary<string, string> Values { get; set; } = new();
}
