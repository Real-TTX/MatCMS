namespace MatCMS.Content;

public class BlockDefinition
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Inner SVG markup (paths/rects) for a 0 0 24 24 stroke icon.</summary>
    public string Svg { get; set; } = "";

    /// <summary>Razor partial used to render this block (under Pages/Shared/Blocks).</summary>
    public string Partial { get; set; } = "";

    public List<BlockField> Fields { get; set; } = new();

    /// <summary>Block types that may be nested inside this block. Non-empty ⇒ this is a container.</summary>
    public List<string> AllowedChildren { get; set; } = new();

    /// <summary>If true, this block only exists inside a container and is hidden from the top-level picker.</summary>
    public bool ChildOnly { get; set; }

    public bool IsContainer => AllowedChildren.Count > 0;

    /// <summary>For user-defined components: the HTML template with {{field}} placeholders (null for built-ins).</summary>
    public string? ComponentTemplate { get; set; }

    public bool IsComponent => ComponentTemplate is not null;
}
