namespace MatCMS.Models;

/// <summary>A visual theme ("Template") that drives the public site's accent color and fonts.</summary>
public class Template
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Only one template is active at a time; the active one styles the public site.</summary>
    public bool IsActive { get; set; }

    /// <summary>Accent color as hex, e.g. "#de7e11".</summary>
    public string AccentColor { get; set; } = "#de7e11";

    /// <summary>Google-Fonts family name for headings, e.g. "Geologica".</summary>
    public string HeadingFont { get; set; } = "Geologica";

    /// <summary>Google-Fonts family name for body text, e.g. "Inter".</summary>
    public string BodyFont { get; set; } = "Inter";

    /// <summary>"solid" or "outline".</summary>
    public string ButtonStyle { get; set; } = "solid";
}
