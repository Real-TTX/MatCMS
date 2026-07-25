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

    // ---- Extended theme options (defaults match the built-in look, so unset = no change) ----

    /// <summary>Secondary/brand color (gradients, hover). Empty = fall back to the accent color.</summary>
    public string SecondaryColor { get; set; } = "";

    /// <summary>Heading text color.</summary>
    public string HeadingColor { get; set; } = "#010101";

    /// <summary>Body text color.</summary>
    public string TextColor { get; set; } = "#1a1a1a";

    /// <summary>Page background color.</summary>
    public string BackgroundColor { get; set; } = "#ffffff";

    /// <summary>Alternate section background color.</summary>
    public string AltBackground { get; set; } = "#f6f7f9";

    /// <summary>Max content width in px (digits only).</summary>
    public string ContainerWidth { get; set; } = "1180";

    /// <summary>Button corner radius in px (digits only).</summary>
    public string ButtonRadius { get; set; } = "0";

    /// <summary>Header background color. Empty = keep the default translucent white.</summary>
    public string HeaderBackground { get; set; } = "";

    /// <summary>Header link/text color. Empty = inherit the body text color.</summary>
    public string HeaderTextColor { get; set; } = "";

    /// <summary>Header vertical padding in px (controls header height).</summary>
    public string HeaderPadding { get; set; } = "16";

    /// <summary>Raw CSS injected into every public page (advanced).</summary>
    public string CustomCss { get; set; } = "";

    /// <summary>Raw JavaScript injected before &lt;/body&gt; on every public page (advanced).</summary>
    public string CustomJs { get; set; } = "";

    /// <summary>
    /// Advanced: a custom HTML body layout with placeholders ({{content}}, {{nav}}, {{logo}} …).
    /// Only applied when it contains {{content}}; otherwise the default layout is used. The document
    /// head (fonts, theme, favicon) always stays managed, so this can't break page styling.
    /// </summary>
    public string LayoutHtml { get; set; } = "";
}
